namespace ThreeFingerDragLite;

public static class Logger {
    private static readonly object Lock = new();
    private static bool _enabled = true;
    private static bool _verbose;
    private static string _path = null!;

    public static void Initialize(AppConfig config) {
        _enabled = config.Logging;
        _verbose = config.VerboseLog;
        _path = Path.Combine(AppConfig.Directory, "log.txt");
        if (_enabled) PrepareFile();
    }

    /// <summary>Allows turning logging on/off at runtime from a config reload.</summary>
    public static void SetEnabled(bool enabled) {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled) PrepareFile();
    }

    private static void PrepareFile() {
        try {
            Directory.CreateDirectory(AppConfig.Directory);
            if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024) {
                string old = Path.Combine(AppConfig.Directory, "log.old.txt");
                File.Delete(old);
                File.Move(_path, old);
            }
            File.AppendAllText(_path, $"\r\n===== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n");
        } catch {
            // Logging must never take the app down.
        }
    }

    public static void SetVerbose(bool verbose) => _verbose = verbose;

    public static bool IsVerbose => _verbose;

    public static void Log(string message) {
        if (!_enabled) return;
        lock (Lock) {
            try {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}\r\n");
            } catch {
                // ignore
            }
        }
    }

    public static void Verbose(string message) {
        if (_verbose) Log(message);
    }
}
