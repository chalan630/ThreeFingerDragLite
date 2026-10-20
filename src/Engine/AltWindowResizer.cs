using System.Diagnostics;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window resizing for modifier + two-finger drag (the equivalent of AltSnap's
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
///   - center band on both axes → all four edges move symmetrically (AltSnap's "all directions")
///   - left/right part with vertical center → only the nearest vertical edge (width only)
///   - top/bottom part with horizontal center → only the nearest horizontal edge (height only)
///   - corner quadrants → the two nearest edges (classic corner resize). No dead zones.
/// The matching resize-arrow cursor is exposed in <see cref="ZoneCursor"/>.
///
/// Geometry is computed on the visible frame (not the window rect with its invisible borders), so
/// the aspect lock acts on what the user actually sees; the invisible insets are
/// added back when the rect is posted. The minimum size scales with the window's monitor DPI.
///
/// Update rate: posting a SetWindowPos for every ~125Hz touchpad frame only floods slow targets
/// (browsers, Electron) with relayouts they cannot present anyway. With sync enabled, posts are
/// aligned to the display refresh interval (slow windows get a longer one); frames in between are
/// coalesced and <see cref="Flush"/> (timer-driven) posts the trailing state.
/// </summary>
public static class AltWindowResizer {
    private const int BaseMinWidth = 120;
    private const int BaseMinHeight = 80;
    private const int SlowIntervalMs = 33;

    private static IntPtr _hwnd;
    private static RECT _base; // visible frame at gesture start
    private static RECT _insets; // window rect minus visible frame, per side
    private static int _startCursorX, _startCursorY;
    private static float _accX, _accY; // accumulated raw finger delta
    private static int _horizMode, _vertMode; // 0=fixed, 1=near(left/top), 2=far(right/bottom), 3=both
    private static bool _active;
    private static int _lastAdx, _lastAdy; // last injected cursor offset
    private static RECT _lastRect; // last window rect posted
    private static int _minWidth, _minHeight;
    private static int _intervalMs;
    private static long _lastPost;
    private static bool _hasPending;
    private static RECT _pending;

    /// <summary>The resize-arrow cursor matching the grabbed zone (set by the last Start call).</summary>
    public static IntPtr ZoneCursor { get; private set; }

    /// <summary>True while a coalesced update waits for <see cref="Flush"/>.</summary>
    public static bool HasPending => _hasPending;

    /// <summary>Begins resizing the window under the cursor; <paramref name="centerPercent"/> is
    /// the width of the central symmetric region in percent of the window (0 = plain nearest-edge).
    /// Returns false when there is no valid target.</summary>
    public static bool Start(int centerPercent, bool syncToRefresh) {
        _active = false;
        _hasPending = false;
        _hwnd = IntPtr.Zero;
        _accX = 0;
        _accY = 0;

        if (!AltWindowDragger.TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _insets = WindowProbe.FrameInsets(hwnd, rect);
        _base = new RECT {
            left = rect.left + _insets.left,
            top = rect.top + _insets.top,
            right = rect.right - _insets.right,
            bottom = rect.bottom - _insets.bottom
        };
        _lastRect = rect;
        _lastAdx = 0;
        _lastAdy = 0;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _horizMode = AxisMode(cursor.X, _base.left, _base.right, centerPercent);
        _vertMode = AxisMode(cursor.Y, _base.top, _base.bottom, centerPercent);
        // Edge-middle zones change one dimension only: the other axis (whose central band the
        // cursor is in) stays fixed instead of growing symmetrically.
        if (_horizMode == 3 && _vertMode != 3) _horizMode = 0;
        else if (_vertMode == 3 && _horizMode != 3) _vertMode = 0;
        ZoneCursor = ZoneCursorFor(_horizMode, _vertMode);

        float dpi = WindowProbe.DpiScale(hwnd);
        _minWidth = (int)MathF.Round(BaseMinWidth * dpi);
        _minHeight = (int)MathF.Round(BaseMinHeight * dpi);

        _intervalMs = 0;
        if (syncToRefresh) {
            _intervalMs = Math.Max(1, 1000 / WindowProbe.RefreshRate());
            if (WindowProbe.IsSlowToResize(hwnd)) _intervalMs = Math.Max(_intervalMs, SlowIntervalMs);
        }
        _lastPost = 0;

        _active = true;
        Logger.Log($"alt-resize: tracking window 0x{hwnd:X} (h: {AxisDesc(_horizMode)}, v: {AxisDesc(_vertMode)}, " +
                   $"min {_minWidth}x{_minHeight}, post interval {_intervalMs}ms)");
        return true;
    }

    /// <summary>Accumulates a RAW finger delta (no speed/acceleration curve — resize tracks 1:1
    /// like AltSnap with a real mouse), moves the cursor in lockstep (absolute) and applies it to
    /// the grabbed edges. Call once per gesture frame.</summary>
    public static void OnDelta(float dx, float dy, bool lockAspect) {
        if (!_active) return;
        _accX += dx;
        _accY += dy;
        int adx = (int)MathF.Round(_accX);
        int ady = (int)MathF.Round(_accY);
        if (adx != _lastAdx || ady != _lastAdy) {
            _lastAdx = adx;
            _lastAdy = ady;
            MouseInjector.SetCursorAbsolute(_startCursorX + adx, _startCursorY + ady);
        }

        int baseW = _base.right - _base.left, baseH = _base.bottom - _base.top;

        // Raw (unclamped) extents of the grabbed edges.
        float left = _base.left, right = _base.right, top = _base.top, bottom = _base.bottom;
        switch (_horizMode) {
            case 3: left -= adx / 2f; right += adx / 2f; break;
            case 2: right += adx; break;
            case 1: left += adx; break;
        }
        switch (_vertMode) {
            case 3: top -= ady / 2f; bottom += ady / 2f; break;
            case 2: bottom += ady; break;
            case 1: top += ady; break;
        }

        int width = Round(right - left), height = Round(bottom - top);
        if (lockAspect && baseW > 0 && baseH > 0) {
            float sx = width / (float)baseW, sy = height / (float)baseH;
            float scale = MathF.Abs(sx - 1) >= MathF.Abs(sy - 1) ? sx : sy;
            scale = MathF.Max(scale, MathF.Max(_minWidth / (float)baseW, _minHeight / (float)baseH));
            width = Round(baseW * scale);
            height = Round(baseH * scale);
        } else {
            // A fixed axis keeps its size even when the window is already below the minimum.
            width = _horizMode == 0 ? baseW : Math.Max(_minWidth, width);
            height = _vertMode == 0 ? baseH : Math.Max(_minHeight, height);
        }

        // Position from the anchor: the edge opposite to the grabbed one stays put, a symmetric
        // (or aspect-driven fixed) axis stays centered on the base center.
        int outLeft = _horizMode switch {
            1 => _base.right - width,
            2 => _base.left,
            3 => Round((_base.left + _base.right) / 2f - width / 2f),
            _ => lockAspect ? Round((_base.left + _base.right) / 2f - width / 2f) : _base.left
        };
        int outTop = _vertMode switch {
            1 => _base.bottom - height,
            2 => _base.top,
            3 => Round((_base.top + _base.bottom) / 2f - height / 2f),
            _ => lockAspect ? Round((_base.top + _base.bottom) / 2f - height / 2f) : _base.top
        };

        // Back to window-rect space (add the invisible borders again).
        var next = new RECT {
            left = outLeft - _insets.left,
            top = outTop - _insets.top,
            right = outLeft + width + _insets.right,
            bottom = outTop + height + _insets.bottom
        };

        // Identical rect (clamped at the minimum size, fixed axis, sub-pixel delta): nothing to post.
        if (Same(next, _lastRect)) {
            _hasPending = false;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_intervalMs > 0 && _lastPost != 0 &&
            Stopwatch.GetElapsedTime(_lastPost, now).TotalMilliseconds < _intervalMs) {
            _pending = next; // coalesced; the timer tick posts the trailing state
            _hasPending = true;
            return;
        }
        Post(next);
    }

    /// <summary>Posts a coalesced update once its interval has elapsed. Safe to call every timer tick.</summary>
    public static void Flush(bool force = false) {
        if (!_active || !_hasPending) return;
        if (!force && _intervalMs > 0 &&
            Stopwatch.GetElapsedTime(_lastPost).TotalMilliseconds < _intervalMs) return;
        Post(_pending);
    }

    public static void End() {
        Flush(force: true);
        _active = false;
        _hasPending = false;
        _hwnd = IntPtr.Zero;
    }

    private static void Post(RECT r) {
        _hasPending = false;
        if (Same(r, _lastRect)) return;
        _lastRect = r;
        _lastPost = Stopwatch.GetTimestamp();
        // Async: a slow target app (browser relayout) must never block the raw-input thread and
        // back up gesture frames.
        SetWindowPos(_hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private static bool Same(RECT a, RECT b) =>
        a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

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
        if (h == 0) return LoadCursorW(IntPtr.Zero, IDC_SIZENS);
        if (v == 0) return LoadCursorW(IntPtr.Zero, IDC_SIZEWE);
        bool diagonalNWSE = (h == 2) == (v == 2); // right+bottom or left+top
        return LoadCursorW(IntPtr.Zero, diagonalNWSE ? IDC_SIZENWSE : IDC_SIZENESW);
    }

    private static string AxisDesc(int mode) => mode switch {
        3 => "both", 2 => "right/bottom", 0 => "fixed", _ => "left/top"
    };

    private static int Round(float v) => (int)MathF.Round(v);
}
