using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window dragging for Alt + three-finger drag.
///
/// Why this exists: AltSnap/AltDrag detect mouse presses through a low-level mouse hook
/// and act on physical presses; synthetic SendInput presses (what a touchpad drag tool
/// emits) are not acted on, so simulating the left button can never trigger them. Instead
/// of simulating a press, the drag engine lets the cursor move as usual (injected
/// relative moves) and this class moves the window under the cursor to follow it.
/// Real-mouse Alt+click dragging keeps working through AltSnap itself.
/// </summary>
public static class AltWindowDragger {
    private static IntPtr _hwnd;
    private static int _startCursorX, _startCursorY;
    private static int _startLeft, _startTop;
    private static bool _active;

    public static bool IsAltDown() => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    /// <summary>Begins tracking the window under the cursor. Returns false when the cursor is not over a movable window (then the caller falls back to a normal button drag).</summary>
    public static bool Start() {
        _active = false;
        _hwnd = IntPtr.Zero;

        if (!TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _startLeft = rect.left;
        _startTop = rect.top;
        _active = true;
        Logger.Log($"alt-drag: tracking window 0x{hwnd:X}");
        return true;
    }

    /// <summary>
    /// Resolves the resize/drag target: the root window under the cursor, excluding the desktop,
    /// the shell and cloaked (UWP-suspended) windows. A maximized window is restored first
    /// (matching AltSnap behavior); if the app restores asynchronously the target is rejected.
    /// Shared by AltWindowDragger and AltWindowResizer.
    /// </summary>
    internal static bool TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor) {
        hwnd = IntPtr.Zero;
        rect = default;
        cursor = default;

        if (!GetCursorPos(out cursor)) return false;
        IntPtr h = WindowFromPoint(cursor);
        if (h == IntPtr.Zero) return false;
        h = GetAncestor(h, GA_ROOT);
        if (h == IntPtr.Zero || h == GetDesktopWindow() || h == GetShellWindow()) return false;
        if (!IsWindow(h)) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        if (IsZoomed(h)) {
            SendMessageW(h, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
            if (IsZoomed(h)) return false;
        }

        if (!GetWindowRect(h, out rect)) return false;
        hwnd = h;
        return true;
    }

    /// <summary>Moves the tracked window to follow the cursor. Call on every drag frame (also covers system-takeover frames where the OS moved the cursor).</summary>
    public static void OnCursorMoved() {
        if (!_active) return;
        if (!GetCursorPos(out POINT cursor)) return;
        SetWindowPos(_hwnd, IntPtr.Zero,
            _startLeft + cursor.X - _startCursorX,
            _startTop + cursor.Y - _startCursorY,
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void End() {
        _active = false;
        _hwnd = IntPtr.Zero;
    }
}
