using System.Diagnostics;
using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// AltSnap-style window dragging for modifier + three-finger drag.
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
    private static int _width, _height;
    private static bool _active;
    private static int _lastLeft = int.MinValue, _lastTop = int.MinValue;

    // Edge docking: evaluated on every drag frame from the cursor position alone (no window enumeration).
    private enum Zone { None, Left, Right, Top }
    private const int EdgeBand = 5; // px from the screen edge (AltSnap AeroThreshold default)
    private static bool _dockEnabled;
    private static Zone _zone;
    private static RECT _insets;
    private static int _freeLeft, _freeTop; // where the window would be if not docked
    private static RECT _mon, _work;
    private static bool _monValid, _edgeL, _edgeR, _edgeT;

    private const int ReadbackQuietMs = 50;
    private static int _intervalMs;
    private static long _lastPost;
    private static bool _hasPending;
    private static int _pendingLeft, _pendingTop;
    private static float _px, _py;          // commanded cursor position
    private static int _setX = int.MinValue, _setY = int.MinValue; // last injected absolute position
    private static long _lastDeltaMs;
    private static int _virtLeft, _virtTop, _virtRight, _virtBottom;

    public static bool IsAltDown() => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    /// <summary>Begins tracking the window under the cursor. Returns false when the cursor is not over a movable window (then the caller falls back to a normal button drag).</summary>
    public static bool Start(bool edgeDock) {
        _active = false;
        _hwnd = IntPtr.Zero;

        if (!TryResolveTarget(out IntPtr hwnd, out RECT rect, out POINT cursor)) return false;

        _hwnd = hwnd;
        _dockEnabled = edgeDock;
        _zone = Zone.None;
        _monValid = false;
        _insets = edgeDock ? WindowProbe.FrameInsets(hwnd, rect) : default;
        _freeLeft = rect.left;
        _freeTop = rect.top;
        _px = cursor.X;
        _py = cursor.Y;
        _setX = cursor.X;
        _setY = cursor.Y;
        _lastDeltaMs = 0;
        _virtLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtRight = _virtLeft + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtBottom = _virtTop + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _startLeft = rect.left;
        _startTop = rect.top;
        _width = rect.right - rect.left;
        _height = rect.bottom - rect.top;
        _lastLeft = int.MinValue;
        _lastTop = int.MinValue;
        _hasPending = false;
        _lastPost = 0;
        // Half a refresh period: input frames arrive every ~8ms, so a full period would alias to ~40Hz.
        // A move never triggers a relayout, so unlike resize no per-window probing is needed.
        _intervalMs = Math.Max(1, 1000 / WindowProbe.RefreshRate() / 2);
        _active = true;
        Logger.Log($"alt-drag: tracking window 0x{hwnd:X}");
        return true;
    }

    /// <summary>
    /// Resolves the resize/drag target: the root window under the cursor, excluding the desktop,
    /// the shell, cloaked (UWP-suspended) windows and windows of higher-integrity processes we are
    /// not allowed to touch. A maximized window is restored first (matching AltSnap behavior); if
    /// the app restores asynchronously the target is rejected.
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

        if (WindowProbe.IsHigherIntegrity(h)) {
            Logger.Log($"window 0x{h:X} belongs to an elevated process; Windows blocks moving/resizing it from a normal-privilege process");
            Notifier.Show("无法操作该窗口", "目标窗口以管理员权限运行。请以管理员身份重新启动 ThreeFingerDragLite 后再试。");
            return false;
        }

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
        // Right after our own absolute injection a read-back can still return the previous position
        // (SendInput is applied asynchronously); trust the commanded position until the system settles.
        if (NowMs() - _lastDeltaMs < ReadbackQuietMs) return;
        if (!GetCursorPos(out POINT cursor)) return;
        if (cursor.X == _setX && cursor.Y == _setY) return;
        _px = cursor.X;
        _py = cursor.Y;
        Follow(cursor.X, cursor.Y);
    }

    /// <summary>
    /// Drives the cursor and window from the same scaled finger delta: the cursor is placed with an absolute
    /// injection and the window follows the commanded position directly. Reading the cursor back instead
    /// lags a frame behind the injection (ballistics + async input), which shows up as judder in the window.
    /// </summary>
    public static void OnDelta(float dx, float dy) {
        if (!_active) return;
        _px = Math.Clamp(_px + dx, _virtLeft, _virtRight - 1);
        _py = Math.Clamp(_py + dy, _virtTop, _virtBottom - 1);
        int cx = (int)MathF.Round(_px), cy = (int)MathF.Round(_py);
        _lastDeltaMs = NowMs();
        long t0 = Stopwatch.GetTimestamp();
        if (cx != _setX || cy != _setY) {
            _setX = cx;
            _setY = cy;
            MouseInjector.SetCursorAbsolute(cx, cy);
        }
        long t1 = Stopwatch.GetTimestamp();
        Follow(cx, cy);
        long t2 = Stopwatch.GetTimestamp();
        double injectMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
        double followMs = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
        if (injectMs > 8 || followMs > 8)
            Logger.Log($"alt-drag: slow frame (inject {injectMs:F1} ms, follow {followMs:F1} ms)");
    }

    private static void Follow(int cursorX, int cursorY) {
        var cursor = new POINT { X = cursorX, Y = cursorY };

        int left = _startLeft + cursor.X - _startCursorX;
        int top = _startTop + cursor.Y - _startCursorY;
        _freeLeft = left;
        _freeTop = top;

        Zone zone = _dockEnabled ? DetectZone(cursor) : Zone.None;
        if (zone != _zone) {
            EnterZone(zone);
            return;
        }
        if (zone == Zone.None) Place(left, top);
    }

    private static Zone DetectZone(POINT cursor) {
        if (!_monValid || cursor.X < _mon.left || cursor.X >= _mon.right || cursor.Y < _mon.top || cursor.Y >= _mon.bottom)
            RefreshMonitor(cursor);
        if (!_monValid) return Zone.None;
        if (_edgeT && cursor.Y <= _mon.top + EdgeBand) return Zone.Top;
        if (_edgeL && cursor.X <= _mon.left + EdgeBand) return Zone.Left;
        if (_edgeR && cursor.X >= _mon.right - 1 - EdgeBand) return Zone.Right;
        return Zone.None;
    }

    // An edge is dockable only when no other monitor lies beyond it, so crossing between monitors never docks.
    private static void RefreshMonitor(POINT cursor) {
        _monValid = false;
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return;
        _mon = info.rcMonitor;
        _work = info.rcWork;
        int midX = (_mon.left + _mon.right) / 2, midY = (_mon.top + _mon.bottom) / 2;
        _edgeL = MonitorFromPoint(new POINT { X = _mon.left - 1, Y = midY }, MONITOR_DEFAULTTONULL) == IntPtr.Zero;
        _edgeR = MonitorFromPoint(new POINT { X = _mon.right, Y = midY }, MONITOR_DEFAULTTONULL) == IntPtr.Zero;
        _edgeT = MonitorFromPoint(new POINT { X = midX, Y = _mon.top - 1 }, MONITOR_DEFAULTTONULL) == IntPtr.Zero;
        _monValid = true;
    }

    private static void EnterZone(Zone zone) {
        _zone = zone;
        _hasPending = false;
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        if (zone == Zone.None) {
            SetWindowPos(_hwnd, IntPtr.Zero, _freeLeft, _freeTop, _width, _height, flags);
            _lastLeft = _freeLeft;
            _lastTop = _freeTop;
            Logger.Log("alt-drag: undock");
            return;
        }

        int x = _work.left, y = _work.top, w = _work.right - _work.left, h = _work.bottom - _work.top;
        if (zone == Zone.Left) w /= 2;
        else if (zone == Zone.Right) { x += w / 2; w -= w / 2; }
        // Extend by the invisible resize borders so the visible frame lands exactly on the work area.
        SetWindowPos(_hwnd, IntPtr.Zero, x - _insets.left, y - _insets.top,
            w + _insets.left + _insets.right, h + _insets.top + _insets.bottom, flags);
        _lastLeft = int.MinValue;
        _lastTop = int.MinValue;
        Logger.Log($"alt-drag: dock {zone}");
    }

    // Commits a top-edge dock as a real maximize, remembering the pre-dock rectangle as the restore size.
    private static void CommitMaximize() {
        if (!IsWindow(_hwnd)) return;
        int ox = _work.left - _mon.left, oy = _work.top - _mon.top; // rcNormalPosition is in work-area coordinates
        var placement = new WINDOWPLACEMENT {
            length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = SW_SHOWMAXIMIZED,
            ptMinPosition = new POINT { X = -1, Y = -1 },
            ptMaxPosition = new POINT { X = -1, Y = -1 },
            rcNormalPosition = new RECT {
                left = _freeLeft - ox,
                top = _freeTop - oy,
                right = _freeLeft - ox + _width,
                bottom = _freeTop - oy + _height
            }
        };
        SetWindowPlacement(_hwnd, ref placement);
        Logger.Log("alt-drag: maximized");
    }

    /// <summary>Fingers lifted. Without a hold the drag is over (commits a top dock); with a hold it stays resumable.</summary>
    public static void Release(bool hold) {
        Flush(force: true);
        if (_active && !hold) End();
    }

    /// <summary>Fingers came back: re-anchor so the window follows the cursor from where it is now.</summary>
    public static void Regrab() {
        if (!_active) return;
        _hasPending = false;
        if (!GetCursorPos(out POINT cursor) || !GetWindowRect(_hwnd, out RECT rect)) return;
        _startCursorX = cursor.X;
        _startCursorY = cursor.Y;
        _px = cursor.X;
        _py = cursor.Y;
        _setX = cursor.X;
        _setY = cursor.Y;
        _startLeft = rect.left;
        _startTop = rect.top;
    }

    public static void End() {
        Flush(force: true);
        if (_active && _zone == Zone.Top) CommitMaximize();
        _zone = Zone.None;
        _active = false;
        _hwnd = IntPtr.Zero;
    }

    private static void Place(int left, int top) {
        _pendingLeft = left;
        _pendingTop = top;
        _hasPending = true;
        Flush();
    }

    public static bool HasPending => _active && _hasPending;

    /// <summary>Posts the newest pending position once the post interval has elapsed. Safe to call every timer tick.</summary>
    public static void Flush(bool force = false) {
        if (!_active || !_hasPending) return;
        if (!force && Stopwatch.GetElapsedTime(_lastPost).TotalMilliseconds < _intervalMs) return;
        Post(_pendingLeft, _pendingTop);
    }

    private static void Post(int left, int top) {
        _hasPending = false;
        if (left == _lastLeft && top == _lastTop) return;
        _lastLeft = left;
        _lastTop = top;
        _lastPost = Stopwatch.GetTimestamp();
        // Async and coalesced to the refresh rate: a slow target app (browser relayout) must neither block
        // the input thread nor accumulate a backlog of stale positions in its queue that it replays late.
        SetWindowPos(_hwnd, IntPtr.Zero, left, top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }
}
