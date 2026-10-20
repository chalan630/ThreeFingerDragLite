using ThreeFingerDragLite.Native;
using static ThreeFingerDragLite.Native.NativeMethods;

namespace ThreeFingerDragLite;

public static class Program {
    [STAThread]
    public static int Main() {
        // Physical (non-virtualized) coordinates are required for the Alt window dragger
        // to hit-test correctly on scaled monitors.
        try {
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        } catch {
            // Pre-1703 Windows: fall back to default awareness.
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\ThreeFingerDragLite_SingleInstance", out bool createdNew);
        if (!createdNew) return 0;

        AppConfig config = AppConfig.Load();
        Logger.Initialize(config);
        Logger.SetVerbose(config.VerboseLog);
        AppConfig.EnsureDocs();
        Logger.Log($"starting, config: enabled={config.Enabled} hold={config.HoldEnabled} takeover={config.TakeoverWhenSingleFinger} " +
                   $"speed={config.Speed} accel={config.Acceleration} threshold={config.StartThreshold} holdTime={config.HoldTimeMs}ms");

        using var app = new App(config);
        int exitCode = app.Run();
        return exitCode;
    }
}
