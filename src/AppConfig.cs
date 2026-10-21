using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreeFingerDragLite;

public class AppConfig {
    /// <summary>总开关：启用三指拖动。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>拖动按下的鼠标键：left / right / middle。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DragButton DragButton { get; set; } = DragButton.Left;

    /// <summary>光标速度，120 为原版 ThreeFingerDragOnWindows 的中性值（乘以 Speed/120）。越大越快。</summary>
    public int Speed { get; set; } = 120;

    /// <summary>指针加速度：非 0 时三指速度跟随单指学到的系统曲线（gain.json；学成前用内置 Sigmoid 曲线，此时 10 为中性值），0 = 线性。</summary>
    public int Acceleration { get; set; } = 10;

    /// <summary>触发拖动所需的三指累计移动距离（触控板原始单位，约等于像素）。防止轻触/误触。</summary>
    public int StartThreshold { get; set; } = 12;

    /// <summary>拖动状态保持（FastGestures 特性）：所有手指抬离后按键不立即释放。</summary>
    public bool HoldEnabled { get; set; } = true;

    /// <summary>保持时长（毫秒）：全部手指抬离后经过该时长自动释放按键。</summary>
    public int HoldTimeMs { get; set; } = 1000;

    /// <summary>保持期间放回至少两根手指即可继续拖动。</summary>
    public bool HoldResumeEnabled { get; set; } = true;

    /// <summary>系统接管（FastGestures 特性）：拖动中只剩一根手指时，交给系统光标速度继续拖动，消除抖动。</summary>
    public bool TakeoverWhenSingleFinger { get; set; } = true;

    /// <summary>窗口手势修饰键（默认 Alt，AltSnap 同款）：Alt / Ctrl / Shift / Win。
    /// 修饰键按住时，三指/双指手势走下面的"带修饰键"映射；松开修饰键立即结束窗口手势。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModifierKey GestureModifier { get; set; } = ModifierKey.Alt;

    /// <summary>无修饰键时三指手势的动作：Drag（模拟鼠标按键拖动，默认）/ MoveWindow / ResizeWindow / None。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GestureAction ThreeFingerAction { get; set; } = GestureAction.Drag;

    /// <summary>按住修饰键时三指手势的动作（默认 MoveWindow：直接移动光标下的窗口。AltSnap/AltDrag 不响应
    /// 合成鼠标事件，所以不模拟按键而是直接移动窗口）。None = 关闭。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GestureAction ModifiedThreeFingerAction { get; set; } = GestureAction.MoveWindow;

    /// <summary>按住修饰键时双指手势的动作（默认 ResizeWindow：AltSnap 右键拖动语义，九宫格模型——
    /// 左右部分只动宽度、上下部分只动高度、中央四边对称缩放、角落为角缩放；最大化窗口先还原）。
    /// 双指滚动无法在源头拦截，缩放手势期间会临时挂低级鼠标钩子吞掉滚轮消息（AltSnap 同款机制）。None = 关闭。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GestureAction ModifiedTwoFingerAction { get; set; } = GestureAction.ResizeWindow;

    /// <summary>移动窗口时光标碰到屏幕外侧边缘即时贴靠（AltSnap Aero 语义）：顶部=最大化、左/右=半屏；光标离开边缘立即恢复原尺寸。仅在无相邻显示器的外侧边缘生效。</summary>
    public bool EdgeDock { get; set; } = true;

    /// <summary>缩放窗口时按住 AspectLockKey 锁定宽高比。</summary>
    public bool ResizeAspectLock { get; set; } = true;

    /// <summary>锁定宽高比的按键（默认 Shift；与 GestureModifier 相同时不生效）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModifierKey AspectLockKey { get; set; } = ModifierKey.Shift;

    /// <summary>缩放时窗口更新对齐显示器刷新率（合并多余的输入帧，减轻目标应用重排压力）；
    /// 浏览器/Electron 等重排较慢的窗口自动再降频。</summary>
    public bool ResizeSyncToRefresh { get; set; } = true;

    /// <summary>Alt+双指缩放的中央"全方向"区域宽度（占窗口对应边的百分比，0~100，默认 24）：
    /// 光标落在中央区域内时四条边同时对称缩放；左右部分只动最近的竖边（宽度），上下部分只动
    /// 最近的横边（高度），角落自然形成角缩放。0 = 关闭中央模式。</summary>
    public int AltResizeCenterPercent { get; set; } = 24;

    /// <summary>Alt+双指缩放的速度倍率（1 = 与手指 1:1）。触控板原始单位密度约 120px/厘米，
    /// 远低于常见鼠标（800dpi ≈ 300px/厘米），1:1 时同样幅度要滑 2~3 倍距离；默认 3 对齐鼠标手感。</summary>
    public int AltResizeSpeed { get; set; } = 3;

    /// <summary>判定"手指已全部抬离"的输入静默时长（毫秒）。触控板持续上报约每 8~15ms 一帧。</summary>
    public int LiftedSilenceMs { get; set; } = 60;

    /// <summary>保持期间单指移动多少距离后结束拖动（FastGestures：单指轻微移动即结束）。</summary>
    public int HoldExitMoveThreshold { get; set; } = 5;

    /// <summary>写日志文件。</summary>
    public bool Logging { get; set; } = true;

    /// <summary>记录每一帧触点数据（排查用，日志量很大）。</summary>
    public bool VerboseLog { get; set; } = false;

    /// <summary>The configured modifier, with a fallback so a hand-edited value can never leave the chord unreachable.</summary>
    [JsonIgnore]
    public ModifierKey EffectiveModifier => Enum.IsDefined(GestureModifier) ? GestureModifier : ModifierKey.Alt;

    [JsonIgnore]
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThreeFingerDragLite");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "config.json");

    /// <summary>Writes the Chinese configuration reference next to config.json when missing.</summary>
    public static void EnsureDocs() {
        try {
            string docsPath = Path.Combine(Directory, "配置说明.txt");
            if (File.Exists(docsPath)) return;
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(docsPath, DocsText, System.Text.Encoding.UTF8);
        } catch {
            // best-effort documentation
        }
    }

    private const string DocsText = """
        ThreeFingerDragLite 配置说明
        ============================

        配置文件：本目录下的 config.json。修改保存后，托盘菜单点“重新加载配置”生效
        （或重启程序）。常用参数可直接在托盘菜单里点选，无需手改文件。

        ── 常用（托盘菜单可直接调）─────────────────

        Enabled            总开关。false = 完全禁用三指拖动
        DragButton         拖动按下的鼠标键：Left / Middle / Right
        Speed              光标速度，120 为标准值（实际乘 Speed/120）。越大越快
        StartThreshold     触发灵敏度：三指累计移动多少才触发拖动（触控板原始单位）。
                           数值越小越容易触发；误触多就调大（如 20、30）
        HoldEnabled        拖动状态保持：手指全部抬离后按键不立即释放
        HoldTimeMs         保持时长（毫秒）：抬手后多久自动释放（托盘可选 0.5~3 秒）
        TakeoverWhenSingleFinger
                           拖动中只剩一根手指时交给系统光标速度继续拖（防抖动）
        GestureModifier    窗口手势修饰键：Alt（默认）/ Ctrl / Shift / Win。按住它再做手势
                           走下面的"带修饰键"动作；松开立即结束窗口手势
        ThreeFingerAction  无修饰键的三指动作：Drag（模拟鼠标拖动，默认）/ MoveWindow /
                           ResizeWindow / None
        ModifiedThreeFingerAction
                           按住修饰键的三指动作（默认 MoveWindow = 直接移动光标下的窗口，
                           AltSnap 兼容，最大化窗口自动还原）。可改 Drag / ResizeWindow / None
        ModifiedTwoFingerAction
                           按住修饰键的双指动作（默认 ResizeWindow = 调整窗口大小）。
                           九宫格：左右部分只动宽度、上下部分只动高度、角落为角缩放；
                           落在中央区域（默认 24%）时四条边同时对称缩放；手势期间自动
                           吞掉滚轮消息防止内容滚动（个别 Pointer 原生应用可能漏）。
                           可改 MoveWindow / None
        AltResizeCenterPercent
                           中央"四边同缩"区域宽度百分比（0~100，0=关闭）
        AltResizeSpeed     缩放速度倍率（默认 3，对齐鼠标手感；1=与手指 1:1）
        EdgeDock           移动窗口时光标碰到屏幕外侧边缘即时贴靠：顶部最大化、左右半屏；
                           光标移开立即恢复原尺寸（默认开）
        ResizeAspectLock   缩放时按住 AspectLockKey（默认 Shift）锁定宽高比；
                           与 GestureModifier 相同时不生效
        ResizeSyncToRefresh
                           缩放时窗口更新对齐显示器刷新率；浏览器/Electron 窗口自动再降频

        ── 进阶（一般不用改）───────────────────────

        HoldResumeEnabled  保持期间放回至少两根手指即可继续拖
        LiftedSilenceMs    输入静默多久判定“手指已全部抬离”（默认 60 毫秒）
        HoldExitMoveThreshold
                           保持期间单指移动超过该距离即结束拖动（默认 5）
        Acceleration       指针加速度（0 = 线性，非 0 = 开）。开启时程序在你用单指时自动学习
                           系统指针曲线（存于本目录 gain.json），三指速度随之对齐单指；
                           学成前用内置曲线。改了触控板“光标速度”会自动重学，也可在
                           托盘菜单点“重新校准指针曲线”
        Logging            写运行日志（log.txt，出错排查用）
        VerboseLog         记录每一帧触点数据（日志量很大，仅排查用）

        ── 日志 ──────────────────────────────────

        本目录 log.txt（超 1MB 自动轮转为 log.old.txt）。反馈问题时附上该文件。
        """;


    private static readonly JsonSerializerOptions SerializerOptions = new() {
        WriteIndented = true,
        IncludeFields = false
    };

    public static AppConfig Load() {
        try {
            if (File.Exists(FilePath)) {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), SerializerOptions);
                if (config != null) return config;
            }
        } catch (Exception ex) {
            Console.Error.WriteLine("Failed to load config: " + ex.Message);
        }
        return new AppConfig();
    }

    public void Save() {
        try {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SerializerOptions));
        } catch (Exception ex) {
            Console.Error.WriteLine("Failed to save config: " + ex.Message);
        }
    }
}

public enum DragButton {
    Left,
    Right,
    Middle
}

public enum ModifierKey {
    Alt,
    Ctrl,
    Shift,
    Win
}

public enum GestureAction {
    None,
    Drag,
    MoveWindow,
    ResizeWindow
}
