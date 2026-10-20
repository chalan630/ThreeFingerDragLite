using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window resizing for Alt + two-finger drag (the equivalent of AltSnap's
/// Alt+right-button drag). Same philosophy as AltWindowDragger: no button is simulated at all,
/// the cursor keeps following the fingers (injected relative moves) and this class resizes the
/// window under the cursor via SetWindowPos.
///
/// Edge selection (AltSnap semantics): at gesture start the cursor position decides which
/// edges move — the vertical edge nearer to the cursor follows horizontal movement, the
/// horizontal edge nearer to it follows vertical movement, and the opposite edges stay fixed.
/// Starting in a corner resizes that corner; starting near an edge resizes that edge only.
/// </summary>
public static class AltWindowResizer {
    private const int MinWidth = 120;
    private const int MinHeight = 80;

    private static IntPtr _hwnd;
    private static RECT _base; // window rect at gesture start
    private static int _startCursorX, _startCursorY;
    private static bool _moveRightEdge, _moveBottomEdge;
    private static bool _active;

    /// <summary>Begins resizing the window under the cursor. Returns false when there is no valid target.</summary>
    public static bool Start() {
        _active = false;
        _hwnd = IntPtr.Zero;

        if (!AltWindowDragger.TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _base = rect;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _moveRightEdge = cursor.X >= (rect.left + rect.right) / 2;
        _moveBottomEdge = cursor.Y >= (rect.top + rect.bottom) / 2;
        _active = true;
        Logger.Log($"alt-resize: tracking window 0x{hwnd:X} " +
                   $"(edges: {(_moveRightEdge ? "right" : "left")}+{(_moveBottomEdge ? "bottom" : "top")})");
        return true;
    }

    /// <summary>Applies the cursor delta to the selected edges. Call on every gesture frame after injecting the move.</summary>
    public static void OnCursorMoved() {
        if (!_active) return;
        if (!GetCursorPos(out POINT cursor)) return;
        int dx = cursor.X - _startCursorX;
        int dy = cursor.Y - _startCursorY;

        int left = _base.left, top = _base.top, right = _base.right, bottom = _base.bottom;
        if (_moveRightEdge) right += dx; else left += dx;
        if (_moveBottomEdge) bottom += dy; else top += dy;

        // Clamp to the minimum size without moving the fixed edges.
        int width = Math.Max(MinWidth, right - left);
        int height = Math.Max(MinHeight, bottom - top);
        if (_moveRightEdge) right = left + width; else left = right - width;
        if (_moveBottomEdge) bottom = top + height; else top = bottom - height;

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void End() {
        _active = false;
        _hwnd = IntPtr.Zero;
    }
}
