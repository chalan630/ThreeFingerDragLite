using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>The real-desktop host: Win32 keyboard state, SendInput injection and window APIs.</summary>
public sealed class Win32GestureHost : IGestureHost {
    private readonly AppConfig _config;

    public Win32GestureHost(AppConfig config) {
        _config = config;
    }

    public long NowMs() => NativeMethods.NowMs();

    public bool IsKeyDown(ModifierKey key) {
        switch (key) {
            case ModifierKey.Ctrl: return Down(VK_CONTROL);
            case ModifierKey.Shift: return Down(VK_SHIFT);
            case ModifierKey.Win: return Down(VK_LWIN) || Down(VK_RWIN);
            default: return Down(VK_MENU);
        }
    }

    private static bool Down(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void MouseButtonDown(DragButton button) => MouseInjector.ButtonDown(button);
    public void MouseButtonUp(DragButton button) => MouseInjector.ButtonUp(button);
    public void MoveCursor(float dx, float dy) => MouseInjector.ShiftCursorPosition(dx, dy);

    public void CursorPosition(out int x, out int y) {
        GetCursorPos(out POINT p);
        x = p.X;
        y = p.Y;
    }

    public void VirtualScreen(out int left, out int top, out int right, out int bottom) {
        left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
    }

    public bool WindowMoveStart() => AltWindowDragger.Start(_config.EdgeDock);
    public void WindowMoveDelta(float dx, float dy) => AltWindowDragger.OnDelta(dx, dy);
    public void WindowMoveUpdate() => AltWindowDragger.OnCursorMoved();
    public void WindowMoveRelease(bool hold) => AltWindowDragger.Release(hold);
    public void WindowMoveGrab() => AltWindowDragger.Regrab();
    public void WindowMoveEnd() => AltWindowDragger.End();

    public bool WindowResizeStart() {
        if (!AltWindowResizer.Start(_config.AltResizeCenterPercent, _config.ResizeSyncToRefresh))
            return false;
        ScrollSuppressor.SetOverrideCursor(AltWindowResizer.ZoneCursor);
        return true;
    }

    public void WindowResizeDelta(float dx, float dy, bool lockAspect) => AltWindowResizer.OnDelta(dx, dy, lockAspect);
    public void WindowResizeEnd() => AltWindowResizer.End();

    public void WheelGuardOn() => ScrollSuppressor.Install();
    public void WheelGuardOff() => ScrollSuppressor.Remove();

    public void MaskKeyRelease() => KeyInjector.MaskKeyRelease();

    public void Tick() {
        AltWindowDragger.Flush();
        AltWindowResizer.Flush();
    }

    public bool Busy => AltWindowDragger.HasPending || AltWindowResizer.HasPending;
}
