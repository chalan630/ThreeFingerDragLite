using System.Diagnostics;
using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>Per-window facts the move/resize gestures need: visible frame, DPI, elevation, responsiveness.</summary>
internal static class WindowProbe {
    private static bool? _selfElevated;

    public static RECT FrameInsets(IntPtr hwnd, RECT windowRect) {
        // Win10/11 windows carry invisible resize borders inside GetWindowRect; resizing must work on
        // the visible frame, so expose the per-side difference.
        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
            return default;
        return new RECT {
            left = frame.left - windowRect.left,
            top = frame.top - windowRect.top,
            right = windowRect.right - frame.right,
            bottom = windowRect.bottom - frame.bottom
        };
    }

    /// <summary>Scale factor of the monitor the window is on (1.0 = 96 dpi).</summary>
    public static float DpiScale(IntPtr hwnd) {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1f : dpi / 96f;
    }

    /// <summary>True when the window belongs to a process we cannot manipulate (UIPI): a higher-integrity
    /// (elevated) process while this one is not.</summary>
    public static bool IsHigherIntegrity(IntPtr hwnd) {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == GetCurrentProcessId()) return false;
        _selfElevated ??= IsElevated(GetCurrentProcess());
        if (_selfElevated.Value) return false;

        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        try {
            return IsElevated(process);
        } finally {
            CloseHandle(process);
        }
    }

    private static bool IsElevated(IntPtr process) {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return false;
        try {
            return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
        } finally {
            CloseHandle(token);
        }
    }

    /// <summary>Browsers / Electron relayout slowly on every size change; they get a gentler update rate.</summary>
    public static bool IsSlowToResize(IntPtr hwnd) {
        var buffer = new char[64];
        int length = GetClassNameW(hwnd, buffer, buffer.Length);
        if (length > 0) {
            var name = new ReadOnlySpan<char>(buffer, 0, length);
            if (name.StartsWith("Chrome_WidgetWin") || name.StartsWith("MozillaWindowClass") || name.StartsWith("MozillaDialogClass"))
                return true;
        }
        // Otherwise measure: a window that needs more than a frame to answer a no-op message is busy.
        long start = Stopwatch.GetTimestamp();
        SendMessageTimeoutW(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out _);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds > 10;
    }

    /// <summary>Refresh rate in Hz of the primary display (60 when unknown).</summary>
    public static int RefreshRate() {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 60;
        try {
            int hz = GetDeviceCaps(dc, VREFRESH);
            return hz is >= 24 and <= 500 ? hz : 60;
        } finally {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }
}
