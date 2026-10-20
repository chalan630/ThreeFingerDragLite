using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window resizing for Alt + two-finger drag (the equivalent of AltSnap's
/// Alt+right-button drag). No button is simulated; the window is resized directly via SetWindowPos.
///
/// Tracking model (the smoothness-critical part): the window is NOT driven by reading the cursor
/// back. Injected relative cursor moves pass through pointer ballistics asynchronously, so a
/// GetCursorPos-driven edge lags a frame and amplifies wobble. Instead the scaled finger delta is
/// accumulated here and drives BOTH the window edges and the cursor, the latter through an
/// absolute-position injection (absolute moves bypass ballistics) — window and cursor stay
/// locked to the fingers 1:1 with zero readback latency.
///
/// Region model per axis (mirrors AltSnap's ResizeCenter/SidesFraction):
///   - center band of AltResizeCenterPercent% of the axis → BOTH edges move symmetrically
///     (1:1 with the drag, axis center fixed)
///   - side bands of AltResizeSidesPercent/2 % at each end → that end's edge moves
///   - between the bands → DEAD: that axis does not resize at all (keeps unintended
///     diagonal wobble from touching the other axis, which is what makes AltSnap feel precise)
/// </summary>
public static class AltWindowResizer {
    private const int MinWidth = 120;
    private const int MinHeight = 80;

    private static IntPtr _hwnd;
    private static RECT _base; // window rect at gesture start
    private static int _startCursorX, _startCursorY;
    private static float _accX, _accY; // accumulated scaled finger delta
    private static int _horizMode, _vertMode; // 0=dead, 1=near, 2=far, 3=both
    private static bool _active;

    /// <summary>Begins resizing the window under the cursor; the region model comes from the two
    /// percent parameters (see the class comment). Returns false when there is no valid target.</summary>
    public static bool Start(int centerPercent, int sidesPercent) {
        _active = false;
        _hwnd = IntPtr.Zero;
        _accX = 0;
        _accY = 0;

        if (!AltWindowDragger.TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _base = rect;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _horizMode = AxisMode(cursor.X, rect.left, rect.right, centerPercent, sidesPercent);
        _vertMode = AxisMode(cursor.Y, rect.top, rect.bottom, centerPercent, sidesPercent);

        _active = true;
        Logger.Log($"alt-resize: tracking window 0x{hwnd:X} (h: {AxisDesc(_horizMode)}, v: {AxisDesc(_vertMode)})");
        return true;
    }

    /// <summary>Accumulates a scaled finger delta, moves the cursor in lockstep (absolute) and
    /// applies it to the grabbed edges. Call once per gesture frame.</summary>
    public static void OnScaledDelta(float x, float y) {
        if (!_active) return;
        _accX += x;
        _accY += y;
        int dx = (int)MathF.Round(_accX);
        int dy = (int)MathF.Round(_accY);
        MouseInjector.SetCursorAbsolute(_startCursorX + dx, _startCursorY + dy);

        int left = _base.left, right = _base.right;
        switch (_horizMode) {
            case 3: left = Round(_base.left - dx / 2f); right = Round(_base.right + dx / 2f); break;
            case 2: right = _base.right + dx; break;
            case 1: left = _base.left + dx; break;
            // 0: dead axis — base edges stay
        }

        int top = _base.top, bottom = _base.bottom;
        switch (_vertMode) {
            case 3: top = Round(_base.top - dy / 2f); bottom = Round(_base.bottom + dy / 2f); break;
            case 2: bottom = _base.bottom + dy; break;
            case 1: top = _base.top + dy; break;
        }

        // Clamp to the minimum size without moving whatever is fixed (in symmetric mode the base center).
        int width = Math.Max(MinWidth, right - left);
        int height = Math.Max(MinHeight, bottom - top);
        if (_horizMode == 3) {
            float c = (_base.left + _base.right) / 2f;
            left = Round(c - width / 2f);
            right = left + width;
        } else if (_horizMode == 2) {
            right = left + width;
        } else if (_horizMode == 1) {
            left = right - width;
        }
        if (_vertMode == 3) {
            float c = (_base.top + _base.bottom) / 2f;
            top = Round(c - height / 2f);
            bottom = top + height;
        } else if (_vertMode == 2) {
            bottom = top + height;
        } else if (_vertMode == 1) {
            top = bottom - height;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void End() {
        _active = false;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>0=dead, 1=near(left/top), 2=far(right/bottom), 3=both(center band).</summary>
    private static int AxisMode(float pos, int lo, int hi, int centerPercent, int sidesPercent) {
        float size = hi - lo;
        if (size <= 0) return 0;
        float center = (lo + hi) / 2f;
        if (centerPercent > 0 && MathF.Abs(pos - center) <= size * centerPercent / 200f) return 3;
        float band = size * sidesPercent / 200f;
        if (pos - lo < band) return 1;
        if (hi - pos < band) return 2;
        return 0;
    }

    private static string AxisDesc(int mode) => mode switch {
        3 => "both", 2 => "right/bottom", 1 => "left/top", _ => "none"
    };

    private static int Round(float v) => (int)MathF.Round(v);
}
