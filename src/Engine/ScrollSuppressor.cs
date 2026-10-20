using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Swallows wheel messages (WM_MOUSEWHEEL / WM_MOUSEHWHEEL) while installed, so the system's
/// two-finger scroll does not reach apps during the Alt+two-finger window resize. Two-finger
/// scroll cannot be blocked at the source in user mode — the wheel events it synthesizes can
/// only be intercepted here, in a low-level mouse hook (the same mechanism AltSnap uses).
///
/// Scope discipline: the hook exists ONLY for the duration of the resize gesture (installed on
/// arming, removed on every exit path). The callback does nothing but check the message code,
/// so it costs microseconds and cannot trip the system's slow-hook removal. Apps consuming raw
/// WM_POINTERWHEEL (UWP/WinUI-style) bypass the legacy wheel path and are not covered.
/// </summary>
public static class ScrollSuppressor {
    private static readonly LowLevelMouseProc Proc = HookProc; // static: keep the delegate alive
    private static IntPtr _hook;
    private static IntPtr _overrideCursor; // resize arrows, re-asserted on every mouse event

    public static bool Installed => _hook != IntPtr.Zero;

    public static void Install() {
        if (Installed) return;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, Proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Logger.Log("scroll-suppressor: SetWindowsHookEx failed. Error: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    /// <summary>Shows the given cursor shape for the duration of the gesture (AltSnap-style
    /// resize arrows). The hook re-asserts it on every mouse event because the app under the
    /// cursor keeps resetting the shape from WM_SETCURSOR.</summary>
    public static void SetOverrideCursor(IntPtr hCursor) => _overrideCursor = hCursor;

    public static void Remove() {
        if (!Installed) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _overrideCursor = IntPtr.Zero; // the next real move lets the app restore its own cursor
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode == 0) {
            int msg = wParam.ToInt32();
            if (_overrideCursor != IntPtr.Zero) SetCursor(_overrideCursor);
            if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL) return new IntPtr(1); // swallow
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
