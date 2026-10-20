using ThreeFingerDragLite.Native;
using ThreeFingerDragLite.Touchpad;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// FastGestures-style three-finger drag state machine.
///
/// States and transitions (behavior spec from FastGestures docs/usage):
///   Idle    → Armed      : exactly 3 fingers land on the touchpad (4+ fingers belong to the
///                          system gesture namespace — never armed, never trigger)
///   Armed   → Dragging   : trusted fingers accumulate StartThreshold movement with exactly 3 fingers down
///                          (progressive trigger: the first fingers may already be moving when
///                           the remaining ones land — no need for a simultaneous 3-finger touchdown)
///   Armed   → Idle       : finger count is no longer exactly 3, or all fingers lifted
///   Dragging             : injects cursor deltas (max trusted-finger delta, with speed/acceleration);
///                          with exactly one finger left the system cursor speed takes over if enabled
///   Dragging  → Holding  : all fingers lifted (input silence) and HoldEnabled
///   Dragging  → Idle     : all fingers lifted and !HoldEnabled, or 4+ fingers land (clean release)
///   Holding   → Dragging : 2-3 fingers placed back within HoldTimeMs (re-grip continues the drag)
///   Holding   → Idle     : hold timeout, a single finger moves slightly (FastGestures exit gesture),
///                          or 4+ fingers land (clean release so system gestures take over)
///   Idle    → ResizeArmed : Alt held + exactly 2 fingers (AltWindowResize enabled) — AltSnap-style
///                           window resize, mirroring Alt+right-drag; a gesture-scoped low-level
///                           mouse hook swallows wheel events so the window content does not scroll;
///                           only arms from Idle so a hold-resume with 2 fingers keeps resuming the drag
///   ResizeArmed → Resizing : trusted 2-finger movement reaches StartThreshold and a valid window
///                            is under the cursor (otherwise back to Idle)
///   Resizing   : injects cursor deltas; the window edges nearest to the gesture-start cursor follow it
///   Resizing  → Idle      : Alt released, fingers lifted / count changed, or input silence
///
/// Four-finger namespace (requires the system "four-finger gestures" to be set to None):
///   Idle    → FourArmed   : exactly 4 fingers, no Alt, FourFingerGestures enabled
///   FourArmed → FourSwiping : horizontal swipe past FourFingerSwipeThreshold (dominant axis) —
///                             nothing fires yet; the commit happens at lift
///   FourArmed → tap        : silent lift within FourFingerTapMaxMs with little movement →
///                             synthesized Ctrl+Alt+Shift+Tab (reverse persistent switcher)
///   FourSwiping : tracks the centroid; when the gesture ends (fingers leave / count drops /
///                 stillness beyond FourFingerLiftMs) the TOTAL displacement decides: below the
///                 threshold = canceled (swiping back undoes the switch, native semantics),
///                 otherwise switch desktop 1..3 times by displacement (Win+Ctrl+Left/Right,
///                 native direction: right swipe = previous desktop)
/// </summary>
public class DragEngine {
    private enum State {
        Idle,
        Armed,
        Dragging,
        Holding,
        ResizeArmed,
        Resizing,
        FourArmed,
        FourSwiping
    }

    private const int TrustDelayMs = 40; // a contact must persist this long before its movement counts

    private readonly AppConfig _config;
    private State _state = State.Idle;
    private bool _altMode; // Alt held at drag start: move the window under the cursor instead of pressing a button

    private TouchpadContact[] _prevContacts = Array.Empty<TouchpadContact>();
    private long _prevTime;

    private readonly Dictionary<int, long> _quarantine = new();
    private readonly HashSet<int> _trusted = new();

    private float _armedMovement;

    private long _holdStartTime;
    private TouchpadContact? _holdSinglePrev;

    // Four-finger gesture bookkeeping (swipe/tap recognition).
    private readonly Dictionary<int, (float x, float y)> _fourAnchors = new();
    private float _fourAnchorX, _fourAnchorY; // centroid at gesture start
    private float _fourCurrentX;              // latest centroid X (commit-at-lift evaluation)
    private long _fourStartTime;
    private float _fourMaxMove;               // largest per-finger displacement from its anchor
    private bool _tapCandidate;               // partial lift: maybe a tap, confirmed by silence
    private long _tapCandidateStart;
    private float _tapCandidateMove;

    public DragEngine(AppConfig config) {
        _config = config;
    }

    /// <summary>
    /// Alt mode must track the physical Alt key for the whole gesture: releasing Alt
    /// immediately drops the window (ending the drag), so the remaining fingers fall back
    /// to normal system behavior (e.g. two-finger scroll) instead of stacking on top of it.
    /// </summary>
    private bool EndAltDragIfAltReleased() {
        if (!_altMode || AltWindowDragger.IsAltDown()) return false;
        Logger.Log("engine: alt released → end window drag");
        ReleaseDragPress();
        _state = State.Idle;
        ResetQuarantine();
        return true;
    }

    private void StartDragPress() {
        bool altDown = AltWindowDragger.IsAltDown();
        _altMode = _config.AltWindowDrag && altDown;
        Logger.Log($"engine: drag press (alt detected: {altDown}, alt mode: {_altMode})");
        if (_altMode && AltWindowDragger.Start()) return; // the window follows the cursor directly
        _altMode = false;
        MouseInjector.ButtonDown(_config.DragButton);
    }

    private void ReleaseDragPress() {
        if (_altMode) AltWindowDragger.End();
        else MouseInjector.ButtonUp(_config.DragButton);
    }

    public void ForceRelease() {
        if (_state is State.Dragging or State.Holding) {
            ReleaseDragPress();
            Logger.Log("engine: force release");
        } else if (_state is State.ResizeArmed or State.Resizing) {
            ScrollSuppressor.Remove();
            AltWindowResizer.End();
            Logger.Log("engine: force release resize");
        }
        _state = State.Idle;
        ResetQuarantine();
        _armedMovement = 0;
    }

    public void OnContacts(TouchpadContact[] contacts) {
        long now = NativeMethods.NowMs();
        long gap = now - _prevTime;
        bool liftedGap = gap > _config.LiftedSilenceMs;

        switch (_state) {
            case State.Idle:
                // Keep the trust bookkeeping warm: a finger that was already moving stays
                // trusted, so the progressive trigger (land the other two fingers without
                // lifting) can fire as soon as the third finger lands, without waiting
                // out the quarantine delay again.
                UpdateTrust(contacts, now);
                if (contacts.Length == 3) {
                    Logger.Verbose("idle → armed");
                    _state = State.Armed;
                    _armedMovement = 0;
                    _tapCandidate = false;
                } else if (contacts.Length == 2 && _config.AltWindowResize && AltWindowDragger.IsAltDown()) {
                    Logger.Verbose("idle → resize armed (alt + 2 fingers)");
                    ScrollSuppressor.Install(); // from touchdown on, the window content must not scroll
                    _state = State.ResizeArmed;
                    _armedMovement = 0;
                    _tapCandidate = false;
                } else if (contacts.Length == 4 && _config.FourFingerGestures) {
                    Logger.Verbose("idle → four armed");
                    EnterFourArmed(contacts, now);
                    _tapCandidate = false;
                }
                break;

            case State.Armed:
                if (liftedGap) {
                    Logger.Verbose("armed → idle (lifted)");
                    _state = State.Idle;
                    ResetQuarantine();
                    break;
                }
                if (contacts.Length == 3) {
                    (bool found, float dx, float dy, float dist) = GetMaxTrustedDelta(contacts, now);
                    if (found) _armedMovement += dist;
                    if (_armedMovement >= _config.StartThreshold) {
                        Logger.Log($"engine: start drag (movement {_armedMovement:F1} >= {_config.StartThreshold})");
                        StartDragPress();
                        _state = State.Dragging;
                        if (found) {
                            InjectDelta(dx, dy, gap);
                            Logger.Log($"engine: first injected move ({dx:F1}, {dy:F1})");
                        }
                    }
                } else {
                    Logger.Verbose($"armed → idle (fingers={contacts.Length})");
                    _state = State.Idle;
                    ResetQuarantine();
                }
                break;

            case State.Dragging:
                if (EndAltDragIfAltReleased()) break;
                if (liftedGap) {
                    if (_config.HoldEnabled) {
                        Logger.Log("engine: fingers lifted → holding");
                        _state = State.Holding;
                        _holdStartTime = now;
                        _holdSinglePrev = null;
                    } else {
                        Logger.Log("engine: fingers lifted → release");
                        ReleaseDragPress();
                        _state = State.Idle;
                        ResetQuarantine();
                    }
                    break;
                }
                if (contacts.Length >= 4) {
                    // 4+ fingers is the system gesture namespace (e.g. desktop swipe): release cleanly.
                    Logger.Log("engine: 4+ fingers → release");
                    ReleaseDragPress();
                    _state = State.Idle;
                    ResetQuarantine();
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

                if (_altMode) AltWindowDragger.OnCursorMoved(); // also follows system-takeover moves
                break;

            case State.Holding:
                if (EndAltDragIfAltReleased()) break;
                if (contacts.Length >= 4) {
                    Logger.Log("engine: 4+ fingers during hold → release");
                    ReleaseDragPress();
                    _state = State.Idle;
                    ResetQuarantine();
                    break;
                }
                if (contacts.Length >= 2 && _config.HoldResumeEnabled) {
                    Logger.Log("engine: fingers back → resume drag");
                    ResetQuarantine(); // avoid a cursor jump from the old contact references
                    _state = State.Dragging;
                } else if (contacts.Length == 1) {
                    TouchpadContact contact = contacts[0];
                    if (_holdSinglePrev.HasValue && _holdSinglePrev.Value.ContactId == contact.ContactId) {
                        float move = _holdSinglePrev.Value.GetDist2D(contact);
                        if (move > _config.HoldExitMoveThreshold) {
                            Logger.Log("engine: single finger move during hold → release");
                            ReleaseDragPress();
                            _state = State.Idle;
                            ResetQuarantine();
                        }
                    }
                    if (_state == State.Holding) _holdSinglePrev = contact;
                }
                break;

            case State.ResizeArmed:
                if (liftedGap || contacts.Length != 2 || !AltWindowDragger.IsAltDown()) {
                    Logger.Verbose("resize armed → idle");
                    ScrollSuppressor.Remove();
                    _state = State.Idle;
                    ResetQuarantine();
                    break;
                }
                {
                    // Alt+two-finger is a deliberate chord, so arming skips the 40ms trust
                    // quarantine (startup latency); a landing-coordinate correction is absorbed
                    // by clamping the per-frame delta instead.
                    (bool found, float dx, float dy, float dist) = GetMaxMatchedDelta(contacts);
                    if (found) _armedMovement += dist;
                    if (_armedMovement >= _config.StartThreshold) {
                        Logger.Log($"engine: start resize (movement {_armedMovement:F1} >= {_config.StartThreshold})");
                        if (AltWindowResizer.Start(_config.AltResizeCenterPercent, _config.AltResizeSidesPercent)) {
                            _state = State.Resizing;
                            if (found) AltWindowResizer.OnDelta(dx, dy);
                        } else {
                            Logger.Log("engine: resize target invalid → idle");
                            _state = State.Idle;
                            ResetQuarantine();
                        }
                    }
                }
                break;

            case State.Resizing:
                if (!AltWindowDragger.IsAltDown()) {
                    Logger.Log("engine: alt released → end window resize");
                    ScrollSuppressor.Remove();
                    AltWindowResizer.End();
                    _state = State.Idle;
                    ResetQuarantine();
                    break;
                }
                if (liftedGap || contacts.Length != 2) {
                    // Finger count changed (3 = the drag can take over from Idle next frame) or fingers lifted.
                    Logger.Log("engine: resize end");
                    ScrollSuppressor.Remove();
                    AltWindowResizer.End();
                    _state = State.Idle;
                    ResetQuarantine();
                    break;
                }
                {
                    (bool found, float dx, float dy, float _) = GetMaxTrustedDelta(contacts, now);
                    if (found) AltWindowResizer.OnDelta(dx, dy); // raw 1:1 — no speed/accel curve
                }
                break;

            case State.FourArmed:
                if (liftedGap) {
                    // Fresh touchdown after a silence; the previous gesture's tap (if any) was
                    // already evaluated by Tick during the silence.
                    EnterFourArmed(contacts, now);
                    break;
                }
                if (contacts.Length != 4) {
                    // Partial lift: keep a tap candidate. The remaining fingers either lift soon
                    // (silence confirms the tap) or continue as another gesture (any state entry clears it).
                    _tapCandidate = true;
                    _tapCandidateStart = _fourStartTime;
                    _tapCandidateMove = _fourMaxMove;
                    _state = State.Idle;
                    break;
                }
                _fourMaxMove = MathF.Max(_fourMaxMove, UpdateFourAnchors(contacts));
                {
                    var (cx, cy) = Centroid(contacts);
                    float dxn = cx - _fourAnchorX;
                    if (MathF.Abs(dxn) >= _config.FourFingerSwipeThreshold &&
                        MathF.Abs(dxn) > MathF.Abs(cy - _fourAnchorY) * 1.5f) {
                        // Nothing fires yet — the commit happens at lift, so the user can still
                        // swipe back below the threshold to cancel (native semantics).
                        Logger.Verbose("four armed → swiping (commit at lift)");
                        _fourCurrentX = cx;
                        _state = State.FourSwiping;
                    }
                }
                break;

            case State.FourSwiping:
                if (liftedGap) break; // fingers held still (pad stops reporting); Tick's stillness window decides
                if (contacts.Length == 4) {
                    _fourCurrentX = Centroid(contacts).x;
                    break;
                }
                // Finger count dropped: a real lift is in progress — evaluate now.
                FinishFourSwipe();
                break;
        }

        _prevContacts = contacts;
        _prevTime = now;
    }

    /// <summary>Called periodically to detect finger lift (input silence) and hold timeouts.</summary>
    public void Tick() {
        long now = NativeMethods.NowMs();
        if (_state is State.Dragging or State.Holding && EndAltDragIfAltReleased()) return;
        switch (_state) {
            case State.Idle:
                // A partial lift left a tap candidate; silence confirms that the fingers really left.
                if (_tapCandidate && now - _prevTime > _config.LiftedSilenceMs) {
                    if (_prevTime - _tapCandidateStart <= _config.FourFingerTapMaxMs &&
                        _tapCandidateMove <= _config.FourFingerTapTolerance) {
                        Logger.Log("engine: 4-finger tap (after partial lift) → reverse task switcher");
                        KeyInjector.ReverseTaskSwitcher();
                    }
                    _tapCandidate = false;
                }
                break;

            case State.Dragging:
                if (_altMode) AltWindowDragger.OnCursorMoved(); // covers system-takeover frames without WM_INPUT
                if (now - _prevTime > _config.LiftedSilenceMs) {
                    if (_config.HoldEnabled) {
                        Logger.Log("engine: input silent → holding");
                        _state = State.Holding;
                        _holdStartTime = now;
                        _holdSinglePrev = null;
                    } else {
                        Logger.Log("engine: input silent → release");
                        ReleaseDragPress();
                        _state = State.Idle;
                        ResetQuarantine();
                    }
                }
                break;

            case State.Holding when now - _holdStartTime > _config.HoldTimeMs:
                Logger.Log("engine: hold timeout → release");
                ReleaseDragPress();
                _state = State.Idle;
                ResetQuarantine();
                break;

            case State.Resizing:
                if (!AltWindowDragger.IsAltDown() || now - _prevTime > _config.LiftedSilenceMs) {
                    Logger.Log(!AltWindowDragger.IsAltDown()
                        ? "engine: alt released → end window resize"
                        : "engine: input silent → end window resize");
                    ScrollSuppressor.Remove();
                    AltWindowResizer.End();
                    _state = State.Idle;
                    ResetQuarantine();
                }
                break;

            case State.FourArmed:
                if (now - _prevTime > _config.LiftedSilenceMs) {
                    if (_prevTime - _fourStartTime <= _config.FourFingerTapMaxMs &&
                        _fourMaxMove <= _config.FourFingerTapTolerance) {
                        Logger.Log("engine: 4-finger tap → reverse task switcher");
                        KeyInjector.ReverseTaskSwitcher();
                    }
                    _state = State.Idle;
                    ResetQuarantine();
                }
                break;

            case State.FourSwiping:
                // Stillness window: on this pad a stationary hand stops the reports, so silence is
                // ambiguous (holding vs lifted). Holding longer than this commits at the held position.
                if (now - _prevTime > Math.Max(_config.LiftedSilenceMs, _config.FourFingerLiftMs)) {
                    Logger.Verbose("engine: four-finger stillness → commit");
                    FinishFourSwipe();
                }
                break;
        }
    }

    // ---------- Four-finger swipe/tap recognition ----------

    /// <summary>Commits the swipe: the total displacement at gesture end decides whether (and how
    /// many times) to switch desktops — swiping back below the threshold cancels, native direction
    /// (right swipe = previous desktop).</summary>
    private void FinishFourSwipe() {
        float total = _fourCurrentX - _fourAnchorX;
        if (MathF.Abs(total) >= _config.FourFingerSwipeThreshold) {
            int count = Math.Clamp((int)(MathF.Abs(total) / _config.FourFingerSwipeStride), 1, 3);
            Logger.Log($"engine: 4-finger swipe committed ({total:F0}) → {(total > 0 ? "previous" : "next")} desktop" +
                       (count > 1 ? $" ×{count}" : ""));
            for (int i = 0; i < count; i++) KeyInjector.SwitchDesktop(total < 0);
        } else {
            Logger.Verbose("engine: 4-finger swipe canceled (returned before lift)");
        }
        _tapCandidate = false;
        _state = State.Idle;
        ResetQuarantine();
    }

    private void EnterFourArmed(TouchpadContact[] contacts, long now) {
        _state = State.FourArmed;
        _fourStartTime = now;
        _fourAnchors.Clear();
        foreach (var c in contacts) _fourAnchors[c.ContactId] = (c.X, c.Y);
        var (x, y) = Centroid(contacts);
        _fourAnchorX = x;
        _fourAnchorY = y;
        _fourCurrentX = x;
        _fourMaxMove = 0;
    }

    /// <summary>Records anchors for newly seen contacts and returns the largest displacement seen this frame.</summary>
    private float UpdateFourAnchors(TouchpadContact[] contacts) {
        float max = 0;
        foreach (var c in contacts) {
            if (_fourAnchors.TryGetValue(c.ContactId, out var a)) {
                float d = MathF.Sqrt((c.X - a.x) * (c.X - a.x) + (c.Y - a.y) * (c.Y - a.y));
                if (d > max) max = d;
            } else {
                _fourAnchors[c.ContactId] = (c.X, c.Y);
            }
        }
        return max;
    }

    private static (float x, float y) Centroid(TouchpadContact[] contacts) {
        float sx = 0, sy = 0;
        foreach (var c in contacts) {
            sx += c.X;
            sy += c.Y;
        }
        return (sx / contacts.Length, sy / contacts.Length);
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

    /// <summary>Like GetMaxTrustedDelta but ignores the trust quarantine, and clamps each frame's
    /// delta so a landing-coordinate correction (first report vs second) cannot fake movement.
    /// Used for resize arming where the 40ms quarantine would be pure startup latency.</summary>
    private (bool found, float dx, float dy, float dist) GetMaxMatchedDelta(TouchpadContact[] contacts) {
        const float clamp = 25f;
        UpdateTrust(contacts, NativeMethods.NowMs()); // keep the bookkeeping consistent

        float bestDist = 0;
        (bool found, float dx, float dy, float dist) best = (false, 0, 0, 0);

        foreach (var contact in contacts) {
            foreach (var prev in _prevContacts) {
                if (prev.ContactId != contact.ContactId) continue;
                float dist = contact.GetDist2D(prev);
                if (dist > bestDist) {
                    float scale = dist > clamp ? clamp / dist : 1f;
                    bestDist = dist;
                    best = (true, (contact.X - prev.X) * scale, (contact.Y - prev.Y) * scale, MathF.Min(dist, clamp));
                }
                break;
            }
        }

        return best;
    }

    private void UpdateTrust(TouchpadContact[] contacts, long now) {
        _trusted.RemoveWhere(id => contacts.All(c => c.ContactId != id));

        var expiredQuarantine = new List<int>();
        foreach (var (id, firstSeen) in _quarantine) {
            if (contacts.All(c => c.ContactId != id)) {
                expiredQuarantine.Add(id);
            } else if (now - firstSeen > TrustDelayMs) {
                expiredQuarantine.Add(id);
                _trusted.Add(id);
            }
        }
        foreach (int id in expiredQuarantine) _quarantine.Remove(id);

        foreach (var contact in contacts) {
            if (!_trusted.Contains(contact.ContactId) && !_quarantine.ContainsKey(contact.ContactId))
                _quarantine[contact.ContactId] = now;
        }
    }

    private void ResetQuarantine() {
        _quarantine.Clear();
        _trusted.Clear();
        _prevContacts = Array.Empty<TouchpadContact>();
    }

    // ---------- Speed & acceleration (from ThreeFingerDragOnWindows DistanceManager) ----------

    private void InjectDelta(float dx, float dy, long elapsedMs) {
        (float x, float y) = ComputeScaled(dx, dy, elapsedMs);
        MouseInjector.ShiftCursorPosition(x, y);
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
