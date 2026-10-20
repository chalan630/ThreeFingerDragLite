using System.Runtime.InteropServices;
using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite.Ui;

/// <summary>System tray icon with the app context menu (pure Win32, no UI framework).</summary>
public class TrayIcon : IDisposable {
    private const uint MenuEnable = 1;
    private const uint MenuHold = 2;
    private const uint MenuTakeover = 3;
    private const uint MenuAltDrag = 4;
    private const uint MenuAltResize = 8;
    private const uint MenuFourGestures = 9;
    private const uint MenuOpenFolder = 5;
    private const uint MenuReload = 6;
    private const uint MenuQuit = 7;

    // Value pickers: command id = base + index (98/99 reserved for "current value" info entries).
    private const uint HoldBase = 100;
    private const uint ButtonBase = 200;
    private const uint SpeedBase = 300;
    private const uint ThresholdBase = 400;

    private static readonly int[] HoldValues = { 500, 1000, 1500, 2000, 3000 };
    private static readonly string[] HoldLabels = { "0.5 秒", "1 秒（默认）", "1.5 秒", "2 秒", "3 秒" };

    private static readonly string[] ButtonLabels = { "左键（默认）", "中键", "右键" };

    private static readonly int[] SpeedValues = { 60, 90, 120, 150, 180, 240 };
    private static readonly string[] SpeedLabels = { "很慢 60", "较慢 90", "标准 120（默认）", "偏快 150", "很快 180", "极快 240" };

    private static readonly int[] ThresholdValues = { 30, 20, 12, 6, 3 };
    private static readonly string[] ThresholdLabels = { "严格 30（防误触）", "较稳 20", "标准 12（默认）", "灵敏 6", "极敏 3" };

    private readonly IntPtr _hwnd;
    private readonly AppConfig _config;
    private readonly Action _onConfigChanged;
    private readonly Action _onQuit;
    private NOTIFYICONDATAW _data;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;

    public TrayIcon(IntPtr hwnd, AppConfig config, Action onConfigChanged, Action onQuit) {
        _hwnd = hwnd;
        _config = config;
        _onConfigChanged = onConfigChanged;
        _onQuit = onQuit;

        _hIcon = LoadAppIcon();
        string version = typeof(TrayIcon).Assembly.GetName().Version?.ToString(3) ?? "1.0";

        _data = new NOTIFYICONDATAW {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = NativeMethods.WM_APP_TRAY,
            hIcon = _hIcon != IntPtr.Zero ? _hIcon : LoadIconW(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */),
            szTip = $"ThreeFingerDragLite v{version} — 三指拖动 · Alt+三指移动窗口"
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref _data);
        if (!_added) Logger.Log("Failed to add tray icon. Error: " + Marshal.GetLastWin32Error());
    }

    private static IntPtr LoadAppIcon() {
        // Preferred: app.ico shipped next to the exe.
        string path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(path)) {
            IntPtr h = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            if (h != IntPtr.Zero) return h;
        }
        // Fallback: the icon embedded in the exe by <ApplicationIcon> (single-file distribution without app.ico).
        return LoadImageW(GetModuleHandleW(null), (IntPtr)1, IMAGE_ICON, 0, 0, 0);
    }

    /// <summary>Handles the tray callback message. Returns true if the message was consumed.</summary>
    public bool HandleCallback(IntPtr wParam, IntPtr lParam) {
        if (wParam != (IntPtr)1) return false; // our uID

        uint mouseEvent = (uint)(lParam.ToInt64() & 0xFFFF);
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_CONTEXTMENU = 0x007B;
        if (mouseEvent is not (WM_LBUTTONUP or WM_RBUTTONUP or WM_CONTEXTMENU)) return true;

        ShowMenu();
        return true;
    }

    private void ShowMenu() {
        IntPtr menu = CreatePopupMenu();
        AppendChecked(menu, MenuEnable, _config.Enabled, "启用三指拖动");
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, "");

        AppendChecked(menu, MenuHold, _config.HoldEnabled, "拖动状态保持");
        AppendChoices(menu, HoldBase, "保持时长", HoldValues, HoldLabels, _config.HoldTimeMs, $"{_config.HoldTimeMs} 毫秒", _config.HoldEnabled);
        AppendChecked(menu, MenuTakeover, _config.TakeoverWhenSingleFinger, "单指系统接管");
        AppendChecked(menu, MenuAltDrag, _config.AltWindowDrag, "Alt+三指移动窗口");
        AppendChecked(menu, MenuAltResize, _config.AltWindowResize, "Alt+双指调整窗口大小");
        AppendChecked(menu, MenuFourGestures, _config.FourFingerGestures, "四指手势（横滑切桌面·点按切换器）");
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, "");

        AppendChoices(menu, ButtonBase, "拖动按键", IndexValues(ButtonLabels.Length), ButtonLabels, (int)_config.DragButton, ButtonLabels[(int)_config.DragButton], _config.Enabled);
        AppendChoices(menu, SpeedBase, "光标速度", SpeedValues, SpeedLabels, _config.Speed, $"速度 {_config.Speed}", _config.Enabled);
        AppendChoices(menu, ThresholdBase, "触发灵敏度", ThresholdValues, ThresholdLabels, _config.StartThreshold, $"阈值 {_config.StartThreshold}", _config.Enabled);
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, "");

        AppendMenuW(menu, MF_STRING, (IntPtr)MenuOpenFolder, "打开配置文件夹");
        AppendMenuW(menu, MF_STRING, (IntPtr)MenuReload, "重新加载配置");
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, "");
        AppendMenuW(menu, MF_STRING, (IntPtr)MenuQuit, "退出");

        GetCursorPos(out POINT p);
        SetForegroundWindow(_hwnd); // required so the menu dismisses when clicking elsewhere
        uint cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, p.X, p.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessageW(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero); // let the menu close cleanly

        switch (cmd) {
            case MenuEnable:
                _config.Enabled = !_config.Enabled;
                SaveAndApply();
                break;
            case MenuHold:
                _config.HoldEnabled = !_config.HoldEnabled;
                SaveAndApply();
                break;
            case MenuTakeover:
                _config.TakeoverWhenSingleFinger = !_config.TakeoverWhenSingleFinger;
                SaveAndApply();
                break;
            case MenuAltDrag:
                _config.AltWindowDrag = !_config.AltWindowDrag;
                SaveAndApply();
                break;
            case MenuAltResize:
                _config.AltWindowResize = !_config.AltWindowResize;
                SaveAndApply();
                break;
            case MenuFourGestures:
                _config.FourFingerGestures = !_config.FourFingerGestures;
                SaveAndApply();
                if (_config.FourFingerGestures)
                    Logger.Log("note: four-finger takeover enabled — set system four-finger gestures to None to avoid double triggers");
                break;
            case MenuOpenFolder:
                OpenConfigFolder();
                break;
            case MenuReload:
                _onConfigChanged();
                break;
            case MenuQuit:
                _onQuit();
                break;
            default:
                ApplyValuePick(cmd);
                break;
        }
    }

    private void ApplyValuePick(uint cmd) {
        if (cmd >= HoldBase && cmd < HoldBase + (uint)HoldValues.Length) {
            _config.HoldTimeMs = HoldValues[cmd - HoldBase];
        } else if (cmd >= ButtonBase && cmd < ButtonBase + (uint)ButtonLabels.Length) {
            _config.DragButton = (DragButton)(cmd - ButtonBase);
        } else if (cmd >= SpeedBase && cmd < SpeedBase + (uint)SpeedValues.Length) {
            _config.Speed = SpeedValues[cmd - SpeedBase];
        } else if (cmd >= ThresholdBase && cmd < ThresholdBase + (uint)ThresholdValues.Length) {
            _config.StartThreshold = ThresholdValues[cmd - ThresholdBase];
        } else {
            return; // informational "current value" entries
        }
        SaveAndApply();
    }

    private void SaveAndApply() {
        _config.Save();
        _onConfigChanged();
    }

    private static void AppendChecked(IntPtr menu, uint id, bool isChecked, string label) =>
        AppendMenuW(menu, MF_STRING | (uint)(isChecked ? MF_CHECKED : 0), (IntPtr)id, label);

    /// <summary>Appends a popup with a checked radio-style value list; grayed when the governing toggle is off; a "current" info entry appears when the value is custom.</summary>
    private static void AppendChoices(IntPtr parent, uint baseId, string title, int[] values, string[] labels, int currentValue, string currentLabel, bool enabled) {
        IntPtr sub = CreatePopupMenu();
        for (int i = 0; i < values.Length; i++)
            AppendChecked(sub, baseId + (uint)i, values[i] == currentValue, labels[i]);
        if (Array.IndexOf(values, currentValue) < 0)
            AppendMenuW(sub, MF_STRING | MF_CHECKED, (IntPtr)(baseId + 98), $"当前值：{currentLabel}");
        AppendMenuW(parent, MF_POPUP | (uint)(enabled ? 0 : MF_GRAYED), sub, title);
    }

    private static int[] IndexValues(int count) => Enumerable.Range(0, count).ToArray();

    private static void OpenConfigFolder() {
        try {
            System.IO.Directory.CreateDirectory(AppConfig.Directory);
            AppConfig.EnsureDocs();
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{AppConfig.FilePath}\"",
                UseShellExecute = true
            });
        } catch (Exception ex) {
            Logger.Log("Failed to open config folder: " + ex.Message);
        }
    }

    public void Dispose() {
        if (_added) Shell_NotifyIconW(NIM_DELETE, ref _data);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }
}
