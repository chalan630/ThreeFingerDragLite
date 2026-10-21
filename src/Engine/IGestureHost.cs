namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Everything the gesture state machine does to the outside world (clock, keyboard state, mouse
/// injection, window manipulation). <see cref="DragEngine"/> talks only to this seam, so tests can
/// replay recorded touchpad frames against a fake host without touching the real desktop.
/// </summary>
public interface IGestureHost {
    long NowMs();
    bool IsKeyDown(ModifierKey key);

    void MouseButtonDown(DragButton button);
    void MouseButtonUp(DragButton button);
    void MoveCursor(float dx, float dy);
    void CursorPosition(out int x, out int y);
    /// <summary>Virtual-screen bounds (right/bottom exclusive).</summary>
    void VirtualScreen(out int left, out int top, out int right, out int bottom);

    /// <summary>Begins moving the window under the cursor. False when there is no valid target.</summary>
    bool WindowMoveStart();
    /// <summary>Re-syncs the window to the current cursor position (call per drag frame).</summary>
    void WindowMoveUpdate();
    /// <summary>Scaled finger delta of a window move: drives the cursor and the window together (no cursor read-back).</summary>
    void WindowMoveDelta(float dx, float dy);
    /// <summary>Fingers lifted into the hold state: flushes the pending position; the move stays resumable (<see cref="WindowMoveGrab"/>).</summary>
    void WindowMoveRelease(bool hold);
    /// <summary>Fingers came back after a release: re-anchor to the current positions.</summary>
    void WindowMoveGrab();
    /// <summary>Ends the move, flushing the pending position and committing a top dock. Idempotent.</summary>
    void WindowMoveEnd();

    /// <summary>Begins resizing the window under the cursor. False when there is no valid target.</summary>
    bool WindowResizeStart();
    void WindowResizeDelta(float dx, float dy, bool lockAspect);
    /// <summary>Ends the resize, flushing any coalesced update. Idempotent.</summary>
    void WindowResizeEnd();

    /// <summary>Swallow wheel messages (the system's two-finger scroll) while a resize chord is active.</summary>
    void WheelGuardOn();
    void WheelGuardOff();

    /// <summary>Injects an inert key event so releasing the held modifiers triggers no system shortcut
    /// (Start menu after Win, menu-bar / KeyTips activation after Alt, input-language switch after Alt+Shift / Ctrl+Shift).</summary>
    void MaskKeyRelease();

    /// <summary>Periodic work independent of finger input: coalesced move/resize flush.</summary>
    void Tick();
    /// <summary>True while the host needs fast ticks (pending move or resize update).</summary>
    bool Busy { get; }
}
