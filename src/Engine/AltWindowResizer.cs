using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window resizing for Alt + two-finger drag (the equivalent of AltSnap's
/// Alt+right-button drag). Same philosophy as AltWindowDragger: no button is simulated at all,
/// the cursor keeps following the fingers (injected relative moves) and this class resizes the
/// window under the cursor via SetWindowPos.
///
/// Region model (mirrors AltSnap's ResizeCenter=1 "All directions"):
/// each axis is split by a central band of AltResizeCenterPercent% of that axis' size.
///   - cursor inside the band  → BOTH edges of that axis move symmetrically (1:1 with the drag,
///     window center on that axis stays fixed); inside both bands → all four edges (AltSnap's
///     center "all directions" mode)
///   - cursor outside the band → only the nearest edge of that axis follows the cursor,
///     the opposite edge stays fixed
/// A maximized window is restored first.
/// </summary>
public static class AltWindowResizer {
    private const int MinWidth = 120;
    private const int MinHeight = 80;

    private static IntPtr _hwnd;
    private static RECT _base; // window rect at gesture start
    private static int _startCursorX, _startCursorY;
    private static bool _horizBoth, _vertBoth;         // center band on that axis
    private static bool _moveRightEdge, _moveBottomEdge; // nearest edge when not in the band
    private static bool _active;

    /// <summary>Begins resizing the window under the cursor. <paramref name="centerPercent"/> is the
    /// width of the central "all directions" region in percent of the window (0 = plain nearest-edge
    /// quadrants). Returns false when there is no valid target.</summary>
    public static bool Start(int centerPercent) {
        _active = false;
        _hwnd = IntPtr.Zero;

        if (!AltWindowDragger.TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _base = rect;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;

        float cx = (rect.left + rect.right) / 2f;
        float cy = (rect.top + rect.bottom) / 2f;
        float bandX = (rect.right - rect.left) * centerPercent / 200f; // half-band (percent covers the full axis)
        float bandY = (rect.bottom - rect.top) * centerPercent / 200f;
        _horizBoth = centerPercent > 0 && MathF.Abs(cursor.X - cx) <= bandX;
        _vertBoth = centerPercent > 0 && MathF.Abs(cursor.Y - cy) <= bandY;
        _moveRightEdge = cursor.X >= cx;
        _moveBottomEdge = cursor.Y >= cy;

        _active = true;
        Logger.Log($"alt-resize: tracking window 0x{hwnd:X} " +
                   $"(h: {AxisDesc(_horizBoth, _moveRightEdge)}, v: {AxisDesc(_vertBoth, _moveBottomEdge)})");
        return true;
    }

    /// <summary>Applies the cursor delta to the selected edges. Call on every gesture frame after injecting the move.</summary>
    public static void OnCursorMoved() {
        if (!_active) return;
        if (!GetCursorPos(out POINT cursor)) return;
        int dx = cursor.X - _startCursorX;
        int dy = cursor.Y - _startCursorY;

        // Horizontal: both edges symmetric (center fixed) or the single nearest edge.
        int left, right;
        if (_horizBoth) {
            left = Round(_base.left - dx / 2f);
            right = Round(_base.right + dx / 2f);
        } else if (_moveRightEdge) {
            left = _base.left;
            right = _base.right + dx;
        } else {
            left = _base.left + dx;
            right = _base.right;
        }

        // Vertical: same.
        int top, bottom;
        if (_vertBoth) {
            top = Round(_base.top - dy / 2f);
            bottom = Round(_base.bottom + dy / 2f);
        } else if (_moveBottomEdge) {
            top = _base.top;
            bottom = _base.bottom + dy;
        } else {
            top = _base.top + dy;
            bottom = _base.bottom;
        }

        // Clamp to the minimum size without moving whatever is fixed (in symmetric mode the base center).
        int width = Math.Max(MinWidth, right - left);
        int height = Math.Max(MinHeight, bottom - top);
        if (_horizBoth) {
            float c = (_base.left + _base.right) / 2f;
            left = Round(c - width / 2f);
            right = left + width;
        } else if (_moveRightEdge) {
            right = left + width;
        } else {
            left = right - width;
        }
        if (_vertBoth) {
            float c = (_base.top + _base.bottom) / 2f;
            top = Round(c - height / 2f);
            bottom = top + height;
        } else if (_moveBottomEdge) {
            bottom = top + height;
        } else {
            top = bottom - height;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void End() {
        _active = false;
        _hwnd = IntPtr.Zero;
    }

    private static string AxisDesc(bool both, bool positiveEdge) =>
        both ? "both" : positiveEdge ? "right/bottom" : "left/top";

    private static int Round(float v) => (int)MathF.Round(v);
}
