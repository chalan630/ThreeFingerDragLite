using System.Runtime.InteropServices;
using ThreeFingerDragLite.Engine;
using ThreeFingerDragLite.Native;
using ThreeFingerDragLite.Touchpad;
using ThreeFingerDragLite.Ui;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite;

/// <summary>
/// Hidden top-level window that receives touchpad raw input and hosts the tray icon + timer.
/// </summary>
public class App : IDisposable {
    private const string WindowClassName = "ThreeFingerDragLiteWnd";

    private static WndProc _wndProcDelegate = null!; // keep alive for the lifetime of the process

    private readonly AppConfig _config;
    private readonly ContactAssembler _assembler = new();
    private DragEngine _engine;
    private TrayIcon? _trayIcon;
    private IntPtr _hwnd;
    private bool _wmInputSeen; // one-time diagnostic: proves raw input delivery

    public App(AppConfig config) {
        _config = config;
        _engine = new DragEngine(config);
    }

    public int Run() {
        IntPtr hInstance = GetModuleHandleW(null);

        _wndProcDelegate = WndProc;
        var wndClass = new WNDCLASSW {
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        if (RegisterClassW(ref wndClass) == 0) {
            Logger.Log("RegisterClassW failed. Error: " + Marshal.GetLastWin32Error());
            return 1;
        }

        _hwnd = CreateWindowExW(0, WindowClassName, "ThreeFingerDragLite", 0 /* WS_OVERLAPPED, never shown */,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) {
            Logger.Log("CreateWindowExW failed. Error: " + Marshal.GetLastWin32Error());
            return 1;
        }

        bool touchpadExists = TouchpadHelper.Exists();
        bool registered = TouchpadHelper.RegisterInput(_hwnd);
        Logger.Log($"Touchpad exists: {touchpadExists}, raw input registered: {registered}");
        if (!touchpadExists)
            Logger.Log("WARNING: no precision touchpad detected. The tool stays running in case one appears.");
        else if (!registered)
            Logger.Log("ERROR: failed to register touchpad raw input. Exiting.");
        if (touchpadExists && !registered) return 1;

        _trayIcon = new TrayIcon(_hwnd, _config, ApplyConfig, Quit);
        SetTimer(_hwnd, (IntPtr)1, 30, IntPtr.Zero);

        // Self-test: prove that THIS process can inject input at all (sandbox/integrity probe).
        RunInjectionSelfTest();

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0)) {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return 0;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
        // This is a resident process: a malformed input report must never take it down.
        try {
            switch (msg) {
                case WM_INPUT: {
                    if (!_wmInputSeen) {
                        _wmInputSeen = true;
                        Logger.Log("diag: first WM_INPUT received (touchpad reports are flowing)");
                    }
                    if (!_config.Enabled) break;
                    var (device, contacts, declaredCount) = TouchpadHelper.ParseInput(lParam);
                    if (contacts == null) {
                        Logger.Verbose("diag: WM_INPUT parse returned no contacts");
                        break;
                    }
                    Logger.Verbose($"diag: WM_INPUT parsed {contacts.Count} contacts, declared={declaredCount}");

                    foreach (var frame in _assembler.Receive(contacts, declaredCount)) {
                        if (frame.Length == 0) continue;
                        if (Logger.IsVerbose)
                            Logger.Verbose("contacts: " + string.Join(", ", frame));
                        _engine.OnContacts(frame);
                    }
                    break;
                }

                case WM_INPUT_DEVICE_CHANGE:
                    Logger.Log($"Touchpad device change (wParam={wParam}), exists={TouchpadHelper.Exists()}");
                    break;

                case WM_TIMER:
                    if (!_config.Enabled) _engine.ForceRelease();
                    else _engine.Tick();
                    break;

                case WM_APP_TRAY:
                    if (_trayIcon != null && _trayIcon.HandleCallback(wParam, lParam)) break;
                    break;

                case WM_DESTROY:
                    _engine.ForceRelease();
                    KillTimer(hWnd, (IntPtr)1);
                    PostQuitMessage(0);
                    break;
            }
        } catch (Exception ex) {
            Logger.Log($"WndProc error (msg=0x{msg:X}): {ex.Message}");
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void RunInjectionSelfTest() {
        try {
            GetCursorPos(out POINT before);
            var input = new INPUT[1];
            input[0].type = INPUT_MOUSE;
            input[0].mi.dx = 25;
            input[0].mi.dy = 15;
            input[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            uint sent = SendInput(1, input, Marshal.SizeOf<INPUT>());
            System.Threading.Thread.Sleep(150);
            GetCursorPos(out POINT after);
            Logger.Log($"self-test: SendInput rc={sent}, cursor moved by ({after.X - before.X},{after.Y - before.Y})");
            // move back so the test is invisible to the user
            input[0].mi.dx = -25;
            input[0].mi.dy = -15;
            SendInput(1, input, Marshal.SizeOf<INPUT>());
        } catch (Exception ex) {
            Logger.Log("self-test failed: " + ex.Message);
        }
    }

    private void ApplyConfig() {
        // Reload the file (the user may have edited it) and apply live toggles.
        AppConfig fresh = AppConfig.Load();
        _config.Enabled = fresh.Enabled;
        _config.DragButton = fresh.DragButton;
        _config.Speed = fresh.Speed;
        _config.Acceleration = fresh.Acceleration;
        _config.StartThreshold = fresh.StartThreshold;
        _config.HoldEnabled = fresh.HoldEnabled;
        _config.HoldTimeMs = fresh.HoldTimeMs;
        _config.HoldResumeEnabled = fresh.HoldResumeEnabled;
        _config.TakeoverWhenSingleFinger = fresh.TakeoverWhenSingleFinger;
        _config.AltWindowDrag = fresh.AltWindowDrag;
        _config.LiftedSilenceMs = fresh.LiftedSilenceMs;
        _config.HoldExitMoveThreshold = fresh.HoldExitMoveThreshold;

        if (!_config.Enabled) _engine.ForceRelease();
        Logger.SetEnabled(fresh.Logging);
        Logger.SetVerbose(fresh.VerboseLog);
        Logger.Log("config applied: " +
                   $"enabled={_config.Enabled} hold={_config.HoldEnabled} takeover={_config.TakeoverWhenSingleFinger} " +
                   $"speed={_config.Speed} accel={_config.Acceleration} threshold={_config.StartThreshold}");
    }

    private void Quit() {
        Logger.Log("quitting");
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    public void Dispose() {
        _trayIcon?.Dispose();
    }
}
