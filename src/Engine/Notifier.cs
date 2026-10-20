namespace ThreeFingerDragLite.Engine;

/// <summary>Rate-limited user-facing notices (tray balloon), wired up by the app at startup.</summary>
public static class Notifier {
    private const long MinIntervalMs = 15_000;
    private static long _lastShown = long.MinValue / 2;

    public static Action<string, string>? Sink { get; set; }

    public static void Show(string title, string message) {
        long now = Native.NativeMethods.NowMs();
        if (now - _lastShown < MinIntervalMs) return;
        _lastShown = now;
        try {
            Sink?.Invoke(title, message);
        } catch (Exception ex) {
            Logger.Log("notifier failed: " + ex.Message);
        }
    }
}
