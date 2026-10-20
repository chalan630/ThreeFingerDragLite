using System.Runtime.InteropServices;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Keyboard chord synthesis for the four-finger gestures. The system's own touchpad gestures are
/// hotkeys underneath (desktop switching is Win+Ctrl+Left/Right), so re-implementing them is the
/// same SendInput channel the drag engine already uses — no hooks, no drivers.
/// </summary>
public static class KeyInjector {
    /// <summary>Presses the given virtual keys in order and releases them in reverse order, in one batch.</summary>
    public static void Chord(params ushort[] vks) {
        var inputs = new INPUTK[vks.Length * 2];
        int i = 0;
        foreach (ushort vk in vks)
            inputs[i++] = new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
        for (int k = vks.Length - 1; k >= 0; k--)
            inputs[i++] = new INPUTK { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vks[k], dwFlags = KEYEVENTF_KEYUP } };
        uint rc = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUTK>());
        if (rc != inputs.Length)
            Logger.Log($"key-inject: SendInput injected {rc}/{inputs.Length} events, error={Marshal.GetLastWin32Error()}");
    }

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

    /// <summary>Switches to the previous/next virtual desktop (Win+Ctrl+Left/Right).</summary>
    public static void SwitchDesktop(bool next) =>
        Chord(VK_CONTROL, VK_LWIN, next ? VK_RIGHT : VK_LEFT);

    /// <summary>Opens the persistent task switcher in reverse order (Ctrl+Alt+Shift+Tab).</summary>
    public static void ReverseTaskSwitcher() =>
        Chord(VK_CONTROL, VK_MENU, VK_SHIFT, VK_TAB);
}
