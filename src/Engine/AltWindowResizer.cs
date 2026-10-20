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
/// Region model (nine-zone, per the AltSnap feel):
/// each axis is split into a central band of AltResizeCenterPercent% of the axis' size plus the
/// two parts on either side:
///   - cursor in the central band on an axis → BOTH edges of that axis move symmetrically
///     (1:1 with the drag, axis center fixed)
///   - cursor in the left/right (or top/bottom) part → only that nearest edge of the axis moves
/// Combined: center×center = all four edges (AltSnap's "all directions" mode); left/right part
/// with vertical center = width only; top/bottom part with horizontal center = height only;
/// corner quadrants = the two nearest edges (classic corner resize). No dead zones.
/// The matching resize-arrow cursor is exposed in <see cref="ZoneCursor"/>.
/// </summary>
public static class AltWindowResizer {
    private const int MinWidth = 120;
    private const int MinHeight = 80;

    private static IntPtr _hwnd;
    private static RECT _base; // window rect at gesture start
    private static int _startCursorX, _startCursorY;
    private static float _accX, _accY; // accumulated raw finger delta
    private static int _horizMode, _vertMode; // 1=near(left/top), 2=far(right/bottom), 3=both
    private static bool _active;

    /// <summary>The resize-arrow cursor matching the grabbed zone (set by the last Start call).</summary>
    public static IntPtr ZoneCursor { get; private set; }

    /// <summary>Begins resizing the window under the cursor; <paramref name="centerPercent"/> is
    /// the width of the central symmetric region in percent of the window (0 = plain nearest-edge).
    /// Returns false when there is no valid target.</summary>
    public static bool Start(int centerPercent) {
        _active = false;
        _hwnd = IntPtr.Zero;
        _accX = 0;
        _accY = 0;

        if (!AltWindowDragger.TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _base = rect;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _horizMode = AxisMode(cursor.X, rect.left, rect.right, centerPercent);
        _vertMode = AxisMode(cursor.Y, rect.top, rect.bottom, centerPercent);
        ZoneCursor = ZoneCursorFor(_horizMode, _vertMode);

        _active = true;
        Logger.Log($"alt-resize: tracking window 0x{hwnd:X} (h: {AxisDesc(_horizMode)}, v: {AxisDesc(_vertMode)})");
        return true;
    }

    /// <summary>Accumulates a RAW finger delta (no speed/acceleration curve — resize tracks 1:1
    /// like AltSnap with a real mouse), moves the cursor in lockstep (absolute) and applies it to
    /// the grabbed edges. Call once per gesture frame.</summary>
    public static void OnDelta(float dx, float dy) {
        if (!_active) return;
        _accX += dx;
        _accY += dy;
        int adx = (int)MathF.Round(_accX);
        int ady = (int)MathF.Round(_accY);
        MouseInjector.SetCursorAbsolute(_startCursorX + adx, _startCursorY + ady);

        int left = _base.left, right = _base.right;
        switch (_horizMode) {
            case 3: left = Round(_base.left - adx / 2f); right = Round(_base.right + adx / 2f); break;
            case 2: right = _base.right + adx; break;
            case 1: left = _base.left + adx; break;
            // 0: dead axis — base edges stay
        }

        int top = _base.top, bottom = _base.bottom;
        switch (_vertMode) {
            case 3: top = Round(_base.top - ady / 2f); bottom = Round(_base.bottom + ady / 2f); break;
            case 2: bottom = _base.bottom + ady; break;
            case 1: top = _base.top + ady; break;
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

        // Async: a slow target app (browser relayout) must never block the raw-input thread and
        // back up gesture frames — pending positions coalesce instead.
        SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    public static void End() {
        _active = false;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>1=near(left/top), 2=far(right/bottom), 3=both(center band).</summary>
    private static int AxisMode(float pos, int lo, int hi, int centerPercent) {
        float size = hi - lo;
        if (size <= 0) return 1;
        float center = (lo + hi) / 2f;
        if (centerPercent > 0 && MathF.Abs(pos - center) <= size * centerPercent / 200f) return 3;
        return pos < center ? 1 : 2;
    }

    private static IntPtr ZoneCursorFor(int h, int v) {
        if (h == 3 && v == 3) return LoadCursorW(IntPtr.Zero, IDC_SIZEALL);
        if (h == 3) return LoadCursorW(IntPtr.Zero, IDC_SIZENS);
        if (v == 3) return LoadCursorW(IntPtr.Zero, IDC_SIZEWE);
        bool diagonalNWSE = (h == 2) == (v == 2); // right+bottom or left+top
        return LoadCursorW(IntPtr.Zero, diagonalNWSE ? IDC_SIZENWSE : IDC_SIZENESW);
    }

    private static string AxisDesc(int mode) => mode switch {
        3 => "both", 2 => "right/bottom", _ => "left/top"
    };

    private static int Round(float v) => (int)MathF.Round(v);
}
