using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Shows a fixed cursor shape for the duration of the Alt+two-finger resize (AltSnap-style
/// resize arrows). SetCursor only affects windows owned by the calling thread, so a hook cannot
/// change the shape over other applications; instead a virtual-screen-sized, nearly invisible
/// topmost window sits under the pointer and answers WM_SETCURSOR itself (the same trick AltSnap
/// uses). It is shown only while a resize is active; Hide is idempotent and called on every exit path.
/// </summary>
public static class CursorOverlay {
    private const string ClassName = "ThreeFingerDragLiteCursorOverlay";

    private static readonly WndProc Proc = OverlayProc; // static: keep the delegate alive
    private static bool _classRegistered;
    private static IntPtr _hwnd;
    private static IntPtr _cursor;

    public static void Show(IntPtr hCursor) {
        _cursor = hCursor;
        if (_hwnd == IntPtr.Zero && !Create()) return;
        // Re-fit every time: the monitor layout may have changed since the last gesture.
        SetWindowPos(_hwnd, HWND_TOPMOST,
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN),
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static void Hide() {
        _cursor = IntPtr.Zero;
        if (_hwnd == IntPtr.Zero) return;
        // Hidden rather than destroyed: creating a screen-sized layered window on the input
        // thread at gesture start would itself cost a visible hitch.
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }

    private static bool Create() {
        IntPtr hInstance = GetModuleHandleW(null);
        if (!_classRegistered) {
            var wndClass = new WNDCLASSW { lpfnWndProc = Proc, hInstance = hInstance, lpszClassName = ClassName };
            if (RegisterClassW(ref wndClass) == 0) {
                Logger.Log("cursor-overlay: RegisterClassW failed. Error: " + Marshal.GetLastWin32Error());
                return false;
            }
            _classRegistered = true;
        }

        _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) {
            Logger.Log("cursor-overlay: CreateWindowExW failed. Error: " + Marshal.GetLastWin32Error());
            return false;
        }
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA); // alpha 1/255: invisible yet still hit-testable
        return true;
    }

    private static IntPtr OverlayProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
        switch (msg) {
            case WM_SETCURSOR when _cursor != IntPtr.Zero:
                SetCursor(_cursor);
                return new IntPtr(1);
            case WM_MOUSEACTIVATE:
                return new IntPtr(3); // MA_NOACTIVATE
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
