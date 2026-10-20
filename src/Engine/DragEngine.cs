using ThreeFingerDragLite.Touchpad;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// FastGestures-style multi-finger gesture state machine.
///
/// A chord (finger count + whether the modifier key is held) maps to an action through the config:
///   3 fingers            → ThreeFingerAction          (default Drag: simulated mouse-button drag)
///   modifier + 3 fingers → ModifiedThreeFingerAction  (default MoveWindow)
///   modifier + 2 fingers → ModifiedTwoFingerAction    (default ResizeWindow)
/// Actions: Drag (button press + cursor motion), MoveWindow / ResizeWindow (direct window APIs, no
/// button simulation — AltSnap-style), None.
///
/// States and transitions:
///   Idle    → Armed      : the finger count matches a chord with an action (4+ fingers belong to the
///                          system gesture namespace — never armed, never trigger). Resize chords also
///                          start swallowing wheel messages here so the content does not scroll.
///   Armed   → Dragging   : Drag/MoveWindow — trusted fingers accumulate StartThreshold movement
///                          (progressive trigger: the first fingers may already be moving when the
///                          remaining ones land)
///   Armed   → Resizing   : ResizeWindow — matched fingers accumulate StartThreshold movement and a
///                          valid window is under the cursor (otherwise back to Idle)
///   Armed   → Idle       : finger count changes, modifier released (modifier chords), or fingers lifted
///   Dragging             : injects cursor deltas (max trusted-finger delta, with speed/acceleration);
///                          with exactly one finger left the system cursor speed takes over if enabled
///   Dragging  → Holding  : all fingers lifted (input silence) and HoldEnabled
///   Dragging  → Idle     : all fingers lifted and !HoldEnabled, or 4+ fingers land (clean release)
///   Holding   → Dragging : 2-3 fingers placed back within HoldTimeMs (re-grip continues the drag)
///   Holding   → Idle     : hold timeout, a single finger moves slightly (FastGestures exit gesture),
///                          or 4+ fingers land (clean release so system gestures take over)
///   Resizing             : the finger AVERAGE delta drives the grabbed window edges 1:1
///   Resizing  → Idle     : modifier released, fingers lifted / count changed, or input silence
/// </summary>
public class DragEngine {
    private enum State {
        Idle,
        Armed,
        Dragging,
        Holding,
        Resizing
    }

    private const int TrustDelayMs = 40; // a contact must persist this long before its movement counts
    private const int RegrabWindowMs = 400; // after a silence release of a window move, a re-landed chord resumes this quickly
    private const int RegrabSlackUnits = 3; // movement still required to resume (instead of the full start threshold)
    private const int ResizeSilenceMs = 180; // the pad stops reporting when fingers hold still; a pause must not end a resize

    private readonly AppConfig _config;
    private readonly IGestureHost _host;
    private State _state = State.Idle;
    private bool _windowMove; // the current drag moves the window under the cursor instead of pressing a button
    private bool _aspectMasked; // the lock key's release was already masked during this resize
    private bool _needModifier; //the gesture was armed through the modifier chord and ends when the key is released
    private ModifierKey _modifier;

    private int _armedFingers;
    private GestureAction _armedAction;

    private TouchpadContact[] _prevContacts = Array.Empty<TouchpadContact>();
    private long _prevTime;

    private readonly Dictionary<int, long> _quarantine = new();
    private readonly HashSet<int> _trusted = new();
    private readonly List<int> _staleIds = new(); // scratch buffer for UpdateTrust

    private float _armedMovement;
    private long _regrabUntil;

    private long _holdStartTime;
    private TouchpadContact? _holdSinglePrev;

    public DragEngine(AppConfig config, IGestureHost host) {
        _config = config;
        _host = host;
    }

    /// <summary>True while a gesture is in progress or the host still animates (coalesced window update pending): the app should tick fast.</summary>
    public bool NeedsFastTick => _state != State.Idle || _host.Busy;

    /// <summary>Current state name (diagnostics and tests).</summary>
    public string StateName => _state.ToString();

    private bool ModifierHeld => _host.IsKeyDown(_modifier);

    /// <summary>
    /// A modifier gesture must track the physical key for its whole life: releasing it
    /// immediately drops the window (ending the drag), so the remaining fingers fall back
    /// to normal system behavior (e.g. two-finger scroll) instead of stacking on top of it.
    /// </summary>
    private bool EndWindowDragIfModifierReleased() {
        if (!_windowMove || !_needModifier || ModifierHeld) return false;
        Logger.Log("engine: modifier released → end window drag");
        ReleaseDragPress();
        ToIdle();
        return true;
    }

    // A modifier window move that ended only because the pad went quiet (finger adjustment) can be
    // picked up again almost instantly: the re-armed chord needs just a few units of movement.
    private void ArmRegrab(long now) {
        if (_windowMove && _needModifier) _regrabUntil = now + RegrabWindowMs;
    }

    private void ToIdle() {
        _state = State.Idle;
        ResetQuarantine();
    }

    private void StartDragPress() {
        bool windowAction = _armedAction == GestureAction.MoveWindow;
        Logger.Log($"engine: drag press (action {_armedAction}, fingers {_armedFingers})");
        _windowMove = false;
        if (windowAction && _host.WindowMoveStart()) {
            _windowMove = true;
            if (_needModifier && _modifier is ModifierKey.Win or ModifierKey.Alt) _host.MaskKeyRelease();
            return; // the window follows the cursor directly
        }
        // Plain Drag, or no movable window under the cursor (three fingers fall back to a normal button drag).
        _needModifier = false;
        _host.MouseButtonDown(_config.DragButton);
    }

    private void ReleaseDragPress() {
        if (_windowMove) _host.WindowMoveEnd();
        else _host.MouseButtonUp(_config.DragButton);
    }

    private void EndResize() {
        _host.WheelGuardOff();
        _host.WindowResizeEnd();
    }

    public void ForceRelease() {
        if (_state is State.Dragging or State.Holding) {
            ReleaseDragPress();
            Logger.Log("engine: force release");
        } else if (_state == State.Resizing || (_state == State.Armed && _armedAction == GestureAction.ResizeWindow)) {
            EndResize();
            Logger.Log("engine: force release resize");
        }
        _host.WindowMoveEnd();
        _regrabUntil = 0;
        _state = State.Idle;
        ResetQuarantine();
        _armedMovement = 0;
    }

    private GestureAction ResolveAction(int fingers, bool modifierHeld) {
        if (fingers == 3) return modifierHeld ? _config.ModifiedThreeFingerAction : _config.ThreeFingerAction;
        if (fingers == 2 && modifierHeld) return _config.ModifiedTwoFingerAction;
        return GestureAction.None;
    }

    public void OnContacts(TouchpadContact[] contacts) {
        long now = _host.NowMs();
        long gap = now - _prevTime;
        bool liftedGap = gap > _config.LiftedSilenceMs;

        switch (_state) {
            case State.Idle:
                // Keep the trust bookkeeping warm: a finger that was already moving stays
                // trusted, so the progressive trigger (land the other two fingers without
                // lifting) can fire as soon as the third finger lands, without waiting
                // out the quarantine delay again.
                UpdateTrust(contacts, now);
                if (contacts.Length is 2 or 3) TryArm(contacts.Length);
                break;

            case State.Armed:
                OnArmed(contacts, now, gap, liftedGap);
                break;

            case State.Dragging:
                if (EndWindowDragIfModifierReleased()) break;
                if (liftedGap) {
                    if (_config.HoldEnabled) {
                        Logger.Log("engine: fingers lifted → holding");
                        if (_windowMove) _host.WindowMoveRelease(hold: true);
                        _state = State.Holding;
                        _holdStartTime = now;
                        _holdSinglePrev = null;
                    } else {
                        Logger.Log("engine: fingers lifted → release");
                        ReleaseDragPress();
                        ArmRegrab(now);
                        ToIdle();
                    }
                    break;
                }
                if (contacts.Length >= 4) {
                    // 4+ fingers is the system gesture namespace (e.g. desktop swipe): release cleanly.
                    Logger.Log("engine: 4+ fingers → release");
                    ReleaseDragPress();
                    ToIdle();
                    break;
                }
                if (contacts.Length >= 2) {
                    (bool found, float dx, float dy, float _) = GetMaxTrustedDelta(contacts, now);
                    if (found) InjectDelta(dx, dy, gap);
                } else if (contacts.Length == 1 && !_config.TakeoverWhenSingleFinger) {
                    (bool found1, float dx1, float dy1, float _) = GetMaxTrustedDelta(contacts, now);
                    if (found1) InjectDelta(dx1, dy1, gap);
                }
                // contacts.Length == 1 && TakeoverWhenSingleFinger: the system cursor speed
                // takes over movement while the button stays pressed (FastGestures behavior).

                if (_windowMove) _host.WindowMoveUpdate(); // also follows system-takeover moves
                break;

            case State.Holding:
                if (EndWindowDragIfModifierReleased()) break;
                if (contacts.Length >= 4) {
                    Logger.Log("engine: 4+ fingers during hold → release");
                    ReleaseDragPress();
                    ToIdle();
                    break;
                }
                if (contacts.Length >= 2 && _config.HoldResumeEnabled) {
                    Logger.Log("engine: fingers back → resume drag");
                    ResetQuarantine(); // avoid a cursor jump from the old contact references
                    if (_windowMove) _host.WindowMoveGrab();
                    _state = State.Dragging;
                } else if (contacts.Length == 1) {
                    TouchpadContact contact = contacts[0];
                    if (_holdSinglePrev.HasValue && _holdSinglePrev.Value.ContactId == contact.ContactId) {
                        float move = _holdSinglePrev.Value.GetDist2D(contact);
                        if (move > _config.HoldExitMoveThreshold) {
                            Logger.Log("engine: single finger move during hold → release");
                            ReleaseDragPress();
                            ToIdle();
                        }
                    }
                    if (_state == State.Holding) _holdSinglePrev = contact;
                }
                break;

            case State.Resizing:
                if (_needModifier && !ModifierHeld) {
                    Logger.Log("engine: modifier released → end window resize");
                    EndResize();
                    ToIdle();
                    break;
                }
                if (gap > Math.Max(_config.LiftedSilenceMs, ResizeSilenceMs) || contacts.Length != _armedFingers) {
                    // Finger count changed (a different chord can take over from Idle next frame) or fingers lifted.
                    Logger.Log("engine: resize end");
                    EndResize();
                    ToIdle();
                    break;
                }
                {
                    (bool found, float dx, float dy, float _) = GetAverageDelta(contacts, now, requireTrusted: true);
                    // After a pause the first frame only rebases: a lift + re-land within the silence
                    // window can reuse contact ids and would otherwise show up as a position jump.
                    if (found && !liftedGap) ResizeBy(dx, dy);
                }
                break;
        }

        _prevContacts = contacts;
        _prevTime = now;
    }

    private void TryArm(int fingers) {
        _modifier = _config.EffectiveModifier;
        bool modifierHeld = _host.IsKeyDown(_modifier);
        GestureAction action = ResolveAction(fingers, modifierHeld);
        if (action == GestureAction.None) return;

        _armedFingers = fingers;
        _armedAction = action;
        _needModifier = modifierHeld;
        _armedMovement = 0;
        if (action == GestureAction.MoveWindow && modifierHeld && fingers == 3 && _host.NowMs() < _regrabUntil)
            _armedMovement = Math.Max(0, _config.StartThreshold - RegrabSlackUnits);
        _regrabUntil = 0;
        _state = State.Armed;
        Logger.Verbose($"idle → armed ({fingers} fingers, {action}{(modifierHeld ? " + " + _modifier : "")})");
        if (action == GestureAction.ResizeWindow)
            _host.WheelGuardOn(); // from touchdown on, the window content must not scroll
    }

    private void OnArmed(TouchpadContact[] contacts, long now, long gap, bool liftedGap) {
        bool resize = _armedAction == GestureAction.ResizeWindow;
        if (liftedGap || contacts.Length != _armedFingers || (_needModifier && !ModifierHeld)) {
            Logger.Verbose($"armed → idle (fingers={contacts.Length}, lifted={liftedGap})");
            if (resize) _host.WheelGuardOff();
            ToIdle();
            return;
        }

        if (resize) {
            // A modifier + fingers chord is deliberate, so arming skips the 40ms trust quarantine
            // (startup latency); a landing-coordinate correction is absorbed by clamping the
            // per-frame delta instead.
            (bool found, float dx, float dy, float dist) = GetAverageDelta(contacts, now, requireTrusted: false, clamp: 25f);
            if (found) _armedMovement += dist;
            if (_armedMovement < _config.StartThreshold) return;

            Logger.Log($"engine: start resize (movement {_armedMovement:F1} >= {_config.StartThreshold})");
            if (_host.WindowResizeStart()) {
                _state = State.Resizing;
                _aspectMasked = false;
                if (_needModifier && _modifier is ModifierKey.Win or ModifierKey.Alt) _host.MaskKeyRelease();
                if (found) ResizeBy(dx, dy);
            } else {
                Logger.Log("engine: resize target invalid → idle");
                _host.WheelGuardOff();
                ToIdle();
            }
            return;
        }

        {
            (bool found, float dx, float dy, float dist) = GetMaxTrustedDelta(contacts, now);
            if (found) _armedMovement += dist;
            if (_armedMovement < _config.StartThreshold) return;

            Logger.Log($"engine: start drag (movement {_armedMovement:F1} >= {_config.StartThreshold})");
            UpgradeLateModifier();
            StartDragPress();
            if (_armedAction == GestureAction.MoveWindow && !_windowMove && _armedFingers != 3) {
                // A two-finger window move has no button-drag fallback.
                ToIdle();
                return;
            }
            _state = State.Dragging;
            if (found) {
                InjectDelta(dx, dy, gap);
                Logger.Log($"engine: first injected move ({dx:F1}, {dy:F1})");
            }
        }
    }

    /// <summary>Pressing the modifier after the fingers landed still gets the modified three-finger action
    /// (Alt may be pressed just before or just after touching down).</summary>
    private void UpgradeLateModifier() {
        if (_needModifier || _armedFingers != 3 || !ModifierHeld) return;
        GestureAction modified = _config.ModifiedThreeFingerAction;
        if (modified is GestureAction.MoveWindow or GestureAction.Drag) {
            _armedAction = modified;
            _needModifier = true;
        }
    }

    private void ResizeBy(float dx, float dy) {
        bool lockAspect = _config.ResizeAspectLock &&
                          _config.AspectLockKey != _modifier &&
                          _host.IsKeyDown(_config.AspectLockKey);
        if (lockAspect && !_aspectMasked) {
            // Alt+Shift / Ctrl+Shift switch the input language when released with no other key in
            // between; one inert key event while both are held cancels that.
            _aspectMasked = true;
            _host.MaskKeyRelease();
        }
        _host.WindowResizeDelta(dx * _config.AltResizeSpeed, dy * _config.AltResizeSpeed, lockAspect);
    }

    /// <summary>Called periodically to detect finger lift (input silence), hold timeouts and host animation.</summary>
    public void Tick() {
        long now = _host.NowMs();
        _host.Tick();
        if (_state is State.Dragging or State.Holding && EndWindowDragIfModifierReleased()) return;
        switch (_state) {
            case State.Idle:
                break;

            case State.Dragging:
                if (_windowMove) _host.WindowMoveUpdate(); // covers system-takeover frames without WM_INPUT
                if (now - _prevTime > _config.LiftedSilenceMs) {
                    if (_config.HoldEnabled) {
                        Logger.Log("engine: input silent → holding");
                        if (_windowMove) _host.WindowMoveRelease(hold: true);
                        _state = State.Holding;
                        _holdStartTime = now;
                        _holdSinglePrev = null;
                    } else {
                        Logger.Log("engine: input silent → release");
                        ReleaseDragPress();
                        ArmRegrab(now);
                        ToIdle();
                    }
                }
                break;

            case State.Holding when now - _holdStartTime > _config.HoldTimeMs:
                Logger.Log("engine: hold timeout → release");
                ReleaseDragPress();
                ToIdle();
                break;

            case State.Resizing:
                // Wider than the drag's silence window: the pad stops reporting when the fingers
                // hold still, and a mid-gesture finger adjustment must not drop the resize.
                bool modifierGone = _needModifier && !ModifierHeld;
                if (modifierGone || now - _prevTime > Math.Max(_config.LiftedSilenceMs, ResizeSilenceMs)) {
                    Logger.Log(modifierGone
                        ? "engine: modifier released → end window resize"
                        : "engine: input silent → end window resize");
                    EndResize();
                    ToIdle();
                }
                break;
        }
    }

    // ---------- Movement calculation (quarantine + max delta, from ThreeFingerDragOnWindows) ----------

    /// <summary>
    /// Updates the quarantine/trust sets and returns the largest per-finger movement between the
    /// previous and current contact lists among trusted contacts (same contact id).
    /// Newly appearing contacts are untrusted for TrustDelayMs so their landing never causes a jump.
    /// </summary>
    private (bool found, float dx, float dy, float dist) GetMaxTrustedDelta(TouchpadContact[] contacts, long now) {
        UpdateTrust(contacts, now);

        float bestDist = 0;
        (bool found, float dx, float dy) best = (false, 0, 0);

        foreach (var contact in contacts) {
            if (!_trusted.Contains(contact.ContactId)) continue;

            foreach (var prev in _prevContacts) {
                if (prev.ContactId != contact.ContactId) continue;

                float dist = contact.GetDist2D(prev);
                if (dist > bestDist) {
                    bestDist = dist;
                    best = (true, contact.X - prev.X, contact.Y - prev.Y);
                }
                break;
            }
        }

        return (best.found, best.dx, best.dy, bestDist);
    }

    /// <summary>Average per-finger movement over the contacts matched with the previous frame. With
    /// <paramref name="requireTrusted"/> only quarantine-cleared contacts count; with a positive
    /// <paramref name="clamp"/> each finger's frame delta is capped so a landing-coordinate correction
    /// (first report vs second) cannot fake movement. <c>dist</c> is the length of the average.</summary>
    private (bool found, float dx, float dy, float dist) GetAverageDelta(TouchpadContact[] contacts, long now, bool requireTrusted, float clamp = 0f) {
        UpdateTrust(contacts, now);

        float sumX = 0, sumY = 0;
        int count = 0;
        foreach (var contact in contacts) {
            if (requireTrusted && !_trusted.Contains(contact.ContactId)) continue;
            foreach (var prev in _prevContacts) {
                if (prev.ContactId != contact.ContactId) continue;
                float dx = contact.X - prev.X, dy = contact.Y - prev.Y;
                if (clamp > 0) {
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > clamp) {
                        dx *= clamp / dist;
                        dy *= clamp / dist;
                    }
                }
                sumX += dx;
                sumY += dy;
                count++;
                break;
            }
        }

        if (count == 0) return (false, 0, 0, 0);
        float ax = sumX / count, ay = sumY / count;
        return (true, ax, ay, MathF.Sqrt(ax * ax + ay * ay));
    }

    private void UpdateTrust(TouchpadContact[] contacts, long now) {
        // Runs on every input frame: no LINQ/closures/allocations here.
        _staleIds.Clear();
        foreach (int id in _trusted)
            if (!ContainsId(contacts, id)) _staleIds.Add(id);
        foreach (int id in _staleIds) _trusted.Remove(id);

        _staleIds.Clear();
        foreach (var (id, firstSeen) in _quarantine) {
            if (!ContainsId(contacts, id)) {
                _staleIds.Add(id);
            } else if (now - firstSeen > TrustDelayMs) {
                _staleIds.Add(id);
                _trusted.Add(id);
            }
        }
        foreach (int id in _staleIds) _quarantine.Remove(id);

        foreach (var contact in contacts) {
            if (!_trusted.Contains(contact.ContactId) && !_quarantine.ContainsKey(contact.ContactId))
                _quarantine[contact.ContactId] = now;
        }
    }

    private static bool ContainsId(TouchpadContact[] contacts, int id) {
        foreach (var c in contacts)
            if (c.ContactId == id) return true;
        return false;
    }

    private void ResetQuarantine() {
        _quarantine.Clear();
        _trusted.Clear();
        _prevContacts = Array.Empty<TouchpadContact>();
    }

    // ---------- Speed & acceleration (from ThreeFingerDragOnWindows DistanceManager) ----------

    private void InjectDelta(float dx, float dy, long elapsedMs) {
        (float x, float y) = ComputeScaled(dx, dy, elapsedMs);
        if (_windowMove) _host.WindowMoveDelta(x, y);
        else _host.MoveCursor(x, y);
    }

    /// <summary>Speed factor + acceleration curve (from ThreeFingerDragOnWindows DistanceManager).</summary>
    private (float x, float y) ComputeScaled(float dx, float dy, long elapsedMs) {
        float speedFactor = _config.Speed / 120f;
        float x = dx * speedFactor, y = dy * speedFactor;

        if (_config.Acceleration != 0) {
            float length = MathF.Sqrt(x * x + y * y);
            float mouseVelocity = elapsedMs > 0 ? MathF.Min(length / elapsedMs, 4f) : 1f;
            if (float.IsNaN(mouseVelocity) || float.IsInfinity(mouseVelocity)) mouseVelocity = 1;

            float a = _config.Acceleration / 10f;
            float pointerVelocity = (float)(0.7 + 0.8 * Sigmoid(2.6 * a * (mouseVelocity - 1 + (3 - Math.Log2(0.8 / 0.3 - 1)) / (2.6 * a)) - 3));
            x *= pointerVelocity;
            y *= pointerVelocity;
        }

        return (x, y);
    }

    private static double Sigmoid(double value) {
        double k = Math.Exp(value);
        return k / (1.0d + k);
    }
}
