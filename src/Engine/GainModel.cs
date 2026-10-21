using System.Text.Json;
using Microsoft.Win32;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// The system's single-finger pointer gain (cursor px per touchpad unit) as a function of finger speed,
/// learned passively from the user's own single-finger strokes. Three-finger motion is injected as
/// mouse input and never passes through the touchpad pointer curve, so this model lets it follow the
/// same feel. Persisted next to config.json and reset when the touchpad CursorSpeed setting changes.
/// </summary>
public sealed class GainModel {
    // Upper speed edges (touchpad units per ms); the last bin is open-ended.
    private static readonly float[] Edges = { 0.05f, 0.1f, 0.2f, 0.35f, 0.6f, 1.0f, 1.6f, 2.5f, float.MaxValue };
    private const int BinCount = 9;
    private const float Decay = 0.99f;
    private const float ValidRaw = 120f;
    private const int MinValidBins = 3;
    private const float MinGain = 0.2f, MaxGain = 6f;
    private const int SaveIntervalMs = 20000;
    private const int SettingsCheckMs = 1000;

    private sealed class Bin {
        public float Raw { get; set; }
        public float Cur { get; set; }
        public float Ms { get; set; }
    }

    private sealed class FileData {
        public int? CursorSpeed { get; set; }
        public Bin[]? Bins { get; set; }
    }

    private readonly Bin[] _bins = new Bin[BinCount];
    private readonly string? _path;
    private readonly Func<int?> _readCursorSpeed;
    private readonly float[] _px = new float[BinCount];   // scratch: valid-bin speeds
    private readonly float[] _py = new float[BinCount];   // scratch: valid-bin gains
    private int _validCount;
    private bool _curveDirty = true;
    private int? _cursorSpeed;
    private bool _dirty;
    private long _lastSave, _lastSettingsCheck;

    /// <param name="path">Persistence file; null keeps the model in memory (tests).</param>
    /// <param name="readCursorSpeed">Reads the touchpad pointer-speed setting; null result = unknown.</param>
    public GainModel(string? path = null, Func<int?>? readCursorSpeed = null) {
        _path = path;
        _readCursorSpeed = readCursorSpeed ?? (() => null);
        for (int i = 0; i < BinCount; i++) _bins[i] = new Bin();
    }

    public static string DefaultPath => Path.Combine(AppConfig.Directory, "gain.json");

    public static int? ReadCursorSpeedFromRegistry() {
        if (!OperatingSystem.IsWindows()) return null;
        try {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            return key?.GetValue("CursorSpeed") as int?;
        } catch {
            return null;
        }
    }

    /// <summary>Creates the production model: persisted gain.json, registry-driven invalidation.</summary>
    public static GainModel CreateDefault() {
        var model = new GainModel(DefaultPath, ReadCursorSpeedFromRegistry);
        model.Load();
        return model;
    }

    public int ValidBins {
        get { EnsureCurve(); return _validCount; }
    }

    /// <summary>Gain for a finger speed in units/ms; null while the model has too little data.</summary>
    public float? Evaluate(float speed) {
        EnsureCurve();
        if (_validCount < MinValidBins) return null;
        float g;
        if (speed <= _px[0]) g = _py[0];
        else if (speed >= _px[_validCount - 1]) g = _py[_validCount - 1];
        else {
            int i = 1;
            while (_px[i] < speed) i++;
            float t = (speed - _px[i - 1]) / (_px[i] - _px[i - 1]);
            g = _py[i - 1] + t * (_py[i] - _py[i - 1]);
        }
        return Math.Clamp(g, MinGain, MaxGain);
    }

    /// <summary>Adds one measured chunk: raw touchpad units, cursor px and duration.</summary>
    public void AddChunk(float raw, float cur, float ms) {
        float speed = raw / Math.Max(1f, ms);
        int bin = 0;
        while (speed >= Edges[bin]) bin++;
        var b = _bins[bin];
        b.Raw = b.Raw * Decay + raw;
        b.Cur = b.Cur * Decay + cur;
        b.Ms = b.Ms * Decay + ms;
        _curveDirty = true;
        _dirty = true;
    }

    public void Reset() {
        foreach (var b in _bins) { b.Raw = 0; b.Cur = 0; b.Ms = 0; }
        _curveDirty = true;
        _dirty = true;
    }

    /// <summary>Throttled housekeeping, called at stroke boundaries: invalidates on a pointer-speed change, saves periodically.</summary>
    public void Poll(long now) {
        if (now - _lastSettingsCheck >= SettingsCheckMs) {
            _lastSettingsCheck = now;
            int? speed = _readCursorSpeed();
            if (speed != null && speed != _cursorSpeed) {
                if (_cursorSpeed != null) {
                    Logger.Log($"gain: touchpad CursorSpeed {_cursorSpeed} -> {speed}, learned curve reset");
                    Reset();
                }
                _cursorSpeed = speed;
                _dirty = true;
            }
        }
        if (_dirty && now - _lastSave >= SaveIntervalMs) Save(now);
    }

    public void Load() {
        if (_path == null) return;
        try {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_path));
            if (data?.Bins is { Length: BinCount }) {
                for (int i = 0; i < BinCount; i++) {
                    var s = data.Bins[i];
                    if (s == null || !float.IsFinite(s.Raw) || !float.IsFinite(s.Cur) || !float.IsFinite(s.Ms)) continue;
                    _bins[i].Raw = s.Raw;
                    _bins[i].Cur = s.Cur;
                    _bins[i].Ms = s.Ms;
                }
                _cursorSpeed = data.CursorSpeed;
                _curveDirty = true;
                Logger.Log($"gain: loaded {Describe()}");
            }
        } catch (Exception ex) {
            Logger.Log("gain: load failed: " + ex.Message);
        }
    }

    public void Save(long now = 0) {
        _lastSave = now;
        if (_path == null || !_dirty) return;
        _dirty = false;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new FileData { CursorSpeed = _cursorSpeed, Bins = _bins }));
            Logger.Log($"gain: saved {Describe()}");
        } catch (Exception ex) {
            Logger.Log("gain: save failed: " + ex.Message);
        }
    }

    /// <summary>One-line curve summary: speed(units/ms)=gain for each valid bin.</summary>
    public string Describe() {
        EnsureCurve();
        if (_validCount == 0) return "no data";
        var parts = new string[_validCount];
        for (int i = 0; i < _validCount; i++) parts[i] = $"{_px[i]:F2}={_py[i]:F2}";
        return (_validCount < MinValidBins ? "(learning) " : "") + string.Join(" ", parts);
    }

    private void EnsureCurve() {
        if (!_curveDirty) return;
        _curveDirty = false;
        _validCount = 0;
        float lastSpeed = -1f;
        foreach (var b in _bins) {
            if (b.Raw < ValidRaw || b.Ms <= 0) continue;
            float speed = b.Raw / b.Ms;
            if (speed <= lastSpeed + 0.005f) continue; // keep the abscissa strictly increasing
            _px[_validCount] = speed;
            _py[_validCount] = b.Cur / b.Raw;
            _validCount++;
            lastSpeed = speed;
        }
    }
}
