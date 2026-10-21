using ThreeFingerDragLite.Touchpad;

namespace ThreeFingerDragLite.Engine;

/// <summary>
/// Watches single-finger strokes (the only motion the OS turns into cursor movement) and feeds
/// (touchpad units, cursor px) chunks to the <see cref="GainModel"/>. Cursor displacement is summed
/// over ~80ms chunks because the OS applies it a frame after the report.
/// </summary>
internal sealed class GainSampler {
    private const int ChunkMs = 80;
    private const int GapMs = 50;
    private const float MinChunkUnits = 6f;
    private const float MinRatio = 0.2f, MaxRatio = 6f;

    private readonly GainModel _model;
    private readonly IGestureHost _host;

    private int _id = -1;
    private long _prevTime, _chunkStart;
    private int _prevX, _prevY, _prevCx, _prevCy;
    private float _chunkRaw, _chunkCur;
    private bool _chunkClamped;

    public GainSampler(GainModel model, IGestureHost host) {
        _model = model;
        _host = host;
    }

    public void Frame(TouchpadContact c, long now) {
        _host.CursorPosition(out int cx, out int cy);
        if (c.ContactId != _id || now - _prevTime > GapMs) {
            _model.Poll(now);
            _id = c.ContactId;
            StartChunk(now);
        } else {
            _chunkRaw += Dist(c.X - _prevX, c.Y - _prevY);
            _chunkCur += Dist(cx - _prevCx, cy - _prevCy);
            _chunkClamped |= AtScreenBorder(cx, cy);
            if (now - _chunkStart >= ChunkMs) {
                CloseChunk(now);
                StartChunk(now);
            }
        }
        _prevTime = now;
        _prevX = c.X;
        _prevY = c.Y;
        _prevCx = cx;
        _prevCy = cy;
    }

    /// <summary>Another finger count appeared or the timer found the stroke over.</summary>
    public void Break() => _id = -1;

    public void Idle(long now) {
        if (_id != -1 && now - _prevTime > GapMs) _id = -1;
    }

    private void StartChunk(long now) {
        _chunkStart = now;
        _chunkRaw = _chunkCur = 0;
        _chunkClamped = false;
    }

    private void CloseChunk(long now) {
        if (_chunkClamped || _chunkRaw < MinChunkUnits || _chunkCur <= 0) return;
        float ratio = _chunkCur / _chunkRaw;
        if (ratio < MinRatio || ratio > MaxRatio) return;
        _model.AddChunk(_chunkRaw, _chunkCur, now - _chunkStart);
    }

    // A cursor pinned to the virtual-screen border under-reports the gain.
    private bool AtScreenBorder(int cx, int cy) {
        _host.VirtualScreen(out int l, out int t, out int r, out int b);
        return cx <= l || cy <= t || cx >= r - 1 || cy >= b - 1;
    }

    private static float Dist(int dx, int dy) => MathF.Sqrt((float)dx * dx + (float)dy * dy);
}
