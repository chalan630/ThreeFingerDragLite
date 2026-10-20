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
/// </summary>
public class DragEngine {
    private enum State {
        Idle,
        Armed,
        Dragging,
        Holding
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
        }

        _prevContacts = contacts;
        _prevTime = now;
    }

    /// <summary>Called periodically to detect finger lift (input silence) and hold timeouts.</summary>
    public void Tick() {
        long now = NativeMethods.NowMs();
        if (_state is State.Dragging or State.Holding && EndAltDragIfAltReleased()) return;
        switch (_state) {
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

        MouseInjector.ShiftCursorPosition(x, y);
    }

    private static double Sigmoid(double value) {
        double k = Math.Exp(value);
        return k / (1.0d + k);
    }
}
