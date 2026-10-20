using System.Runtime.InteropServices;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Keyboard injection helpers: startup probe and Win-menu suppression.
/// </summary>
public static class KeyInjector {
    /// <summary>Startup probe: proves the keyboard injection channel works in this process
    /// (anti-keylogger software can block synthesized keyboard input per process).</summary>
    public static bool SelfTest() {
        SendInput(1, new[] { new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x7C /* F13 */ } } },
            Marshal.SizeOf<INPUTK>());
        bool seen = (GetAsyncKeyState(0x7C) & 0x8000) != 0;
        SendInput(1, new[] { new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x7C, dwFlags = KEYEVENTF_KEYUP } } },
            Marshal.SizeOf<INPUTK>());
        return seen;
    }

    /// <summary>A lone Win key release opens the Start menu; any other keystroke in between cancels it.
    /// Taps an unassigned virtual key (the same trick AutoHotkey uses) while Win is held.</summary>
    public static void MaskKeyRelease() {
        const ushort Unassigned = 0xE8;
        SendInput(2, new[] {
            new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = Unassigned } },
            new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = Unassigned, dwFlags = KEYEVENTF_KEYUP } }
        }, Marshal.SizeOf<INPUTK>());
    }
}
