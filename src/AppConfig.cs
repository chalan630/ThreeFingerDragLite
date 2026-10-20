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

    /// <summary>指针加速度，10 为中性值（内部 a = Acceleration/10，0 关闭）。Sigmoid 曲线：慢速精细、快速省力。</summary>
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

    /// <summary>Alt 增强拖动（AltSnap 兼容）：按住 Alt 三指拖动时直接移动光标下的窗口，而不是模拟鼠标按键。
    /// AltSnap/AltDrag 不响应合成鼠标事件，模拟按键无法触发它们，所以这里改为直接移动窗口。</summary>
    public bool AltWindowDrag { get; set; } = true;

    /// <summary>Alt + 双指拖动调整窗口大小（AltSnap 右键拖动语义）：光标落在窗口哪个象限，最近的
    /// 横竖边就跟随光标移动，对边固定；最大化窗口先还原。绕开按键模拟直接调 API。双指滚动虽无法在
    /// 源头拦截，但手势期间会临时挂低级鼠标钩子吞掉滚轮消息（AltSnap 同款机制），绝大多数应用不会
    /// 滚动；个别走 Pointer 原生通道的应用（UWP/WinUI 类）仍可能漏。</summary>
    public bool AltWindowResize { get; set; } = true;

    /// <summary>Alt+双指缩放时窗口中央"全方向"区域的宽度（占窗口对应边 的百分比，0~100，
    /// 对齐 AltSnap 的 CenterFraction=24）：光标落在中央区域内时四条边同时对称缩放；
    /// 区域外抓最近的边。0 = 关闭中央模式，纯象限抓边。</summary>
    public int AltResizeCenterPercent { get; set; } = 24;

    /// <summary>四指手势接管（默认关闭）：启用前先把系统"四指手势"设为"无"，否则会双重触发。
    /// 接管内容：横滑切换虚拟桌面（合成 Win+Ctrl+方向键）、四指点按打开反向任务切换器
    /// （合成 Ctrl+Alt+Shift+Tab）。Alt+四指拖动则是窗口缩放（见 AltWindowResize）。</summary>
    public bool FourFingerGestures { get; set; } = false;

    /// <summary>四指横滑触发切换桌面的累计位移（触控板原始单位）。越小越灵敏。</summary>
    public int FourFingerSwipeThreshold { get; set; } = 60;

    /// <summary>横滑持续时每累计多少位移再切换一个桌面（连续切换步长）。</summary>
    public int FourFingerSwipeStride { get; set; } = 300;

    /// <summary>四指点按的最长按住时长（毫秒），超过视为非点按。</summary>
    public int FourFingerTapMaxMs { get; set; } = 250;

    /// <summary>四指点按允许的最大指位移（触控板原始单位），超过视为滑动。</summary>
    public int FourFingerTapTolerance { get; set; } = 15;

    /// <summary>横滑的静默判定窗口（毫秒）：手指停住不动触控板会停发报文，超过该时长按"抬指"提交切换。
    /// 调大 = 停顿更宽容（可停更久再滑回撤销），调小 = 切换更快但容易把停顿误判成抬指。</summary>
    public int FourFingerLiftMs { get; set; } = 200;

    /// <summary>判定"手指已全部抬离"的输入静默时长（毫秒）。触控板持续上报约每 8~15ms 一帧。</summary>
    public int LiftedSilenceMs { get; set; } = 60;

    /// <summary>保持期间单指移动多少距离后结束拖动（FastGestures：单指轻微移动即结束）。</summary>
    public int HoldExitMoveThreshold { get; set; } = 5;

    /// <summary>写日志文件。</summary>
    public bool Logging { get; set; } = true;

    /// <summary>记录每一帧触点数据（排查用，日志量很大）。</summary>
    public bool VerboseLog { get; set; } = false;

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
        AltWindowDrag      按住 Alt 三指拖动 = 直接移动光标下的窗口（AltSnap 兼容，
                           最大化窗口自动还原，松开 Alt 立即放下窗口）
        AltWindowResize    按住 Alt 双指拖动 = 调整窗口大小（AltSnap 右键拖动语义）。
                           抓哪个象限动哪条边；落在中央区域（默认 24%，对齐
                           AltSnap CenterFraction）时四条边同时对称缩放；最大化先
                           还原；手势期间自动吞掉滚轮消息防止内容滚动（个别
                           Pointer 原生应用可能漏）
        AltResizeCenterPercent
                           上述中央区域宽度百分比（0~100，0=关闭中央模式）
        FourFingerGestures 四指手势接管（默认关）。启用前必须先把系统设置的”四指手势”
                           改为”无”，否则桌面切换会双重触发。接管内容：
                           横滑 = 切换上/下一个虚拟桌面；四指点按 = 反向任务切换器

        ── 进阶（一般不用改）───────────────────────

        HoldResumeEnabled  保持期间放回至少两根手指即可继续拖
        LiftedSilenceMs    输入静默多久判定“手指已全部抬离”（默认 60 毫秒）
        HoldExitMoveThreshold
                           保持期间单指移动超过该距离即结束拖动（默认 5）
        FourFingerSwipeThreshold
                           四指横滑触发位移（默认 60，越小越灵敏；抬指时总位移落回该值内=撤销）
        FourFingerSwipeStride
                           总位移每超过多少多切一格桌面（默认 300，单次最多 3 格）
        FourFingerTapMaxMs 四指点按最长按住时长（默认 250 毫秒）
        FourFingerTapTolerance
                           四指点按允许的最大指位移（默认 15，超过算滑动）
        FourFingerLiftMs   横滑静默判定窗口（默认 200 毫秒）：停住不动超过该时长即提交切换
        Acceleration       指针加速度，10 为标准（0 关闭）。与 macOS 手感相关的曲线
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
