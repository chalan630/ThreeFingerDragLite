using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>Injects mouse button events and relative cursor movement via SendInput.</summary>
public static class MouseInjector {
    // Moving the cursor does not work with floating point values;
    // decimal parts are kept and accumulated across calls.
    private static float _decimalX;
    private static float _decimalY;

    private static readonly INPUT[] InputBuffer = new INPUT[1];

    public static void ShiftCursorPosition(float x, float y) {
        int intX = (int)(x + _decimalX);
        int intY = (int)(y + _decimalY);
        _decimalX = x + _decimalX - intX;
        _decimalY = y + _decimalY - intY;
        Send(InputBuffer, MOUSEEVENTF_MOVE, dx: intX, dy: intY);
    }

    public static void ButtonDown(DragButton button) => Send(InputBuffer, ButtonFlag(button, down: true));
    public static void ButtonUp(DragButton button) => Send(InputBuffer, ButtonFlag(button, down: false));

    private static int ButtonFlag(DragButton button, bool down) => button switch {
        DragButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        DragButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
    };

    private static void Send(INPUT[] input, int flags, int dx = 0, int dy = 0) {
        input[0].type = INPUT_MOUSE;
        input[0].mi.dx = dx;
        input[0].mi.dy = dy;
        input[0].mi.dwFlags = flags;

        if (NativeMethods.SendInput(1, input, Marshal.SizeOf<INPUT>()) == 0)
            Logger.Log("SendInput failed. Error: " + Marshal.GetLastWin32Error());
    }
}
