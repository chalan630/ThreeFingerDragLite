using ThreeFingerDragLite;
using ThreeFingerDragLite.Engine;

namespace ReplayTests;

/// <summary>Scriptable <see cref="IGestureHost"/>: a manual clock, settable key state, and an ordered event log.</summary>
internal sealed class FakeHost : IGestureHost {
    public long Now;
    public readonly HashSet<ModifierKey> Keys = new();
    public readonly List<string> Events = new();
    public bool MoveTargetValid = true;
    public bool ResizeTargetValid = true;
    public float CursorDx, CursorDy;
    public float ResizeDx, ResizeDy;
    public bool LastLockAspect;

    public long NowMs() => Now;
    public bool IsKeyDown(ModifierKey key) => Keys.Contains(key);

    public void MouseButtonDown(DragButton button) => Events.Add($"button-down:{button}");
    public void MouseButtonUp(DragButton button) => Events.Add($"button-up:{button}");

    public void MoveCursor(float dx, float dy) {
        CursorDx += dx;
        CursorDy += dy;
        Events.Add("cursor-move");
    }

    public bool WindowMoveStart() {
        Events.Add("move-start");
        return MoveTargetValid;
    }

    public void WindowMoveUpdate() { }
    public void WindowMoveDelta(float dx, float dy) => MoveCursor(dx, dy);
    public void WindowMoveRelease(bool hold) => Events.Add("move-release");
    public void WindowMoveGrab() => Events.Add("move-grab");
    public void WindowMoveEnd() => Events.Add("move-end");

    public bool WindowResizeStart() {
        Events.Add("resize-start");
        return ResizeTargetValid;
    }

    public void WindowResizeDelta(float dx, float dy, bool lockAspect) {
        ResizeDx += dx;
        ResizeDy += dy;
        LastLockAspect = lockAspect;
        Events.Add("resize-delta");
    }

    public void WindowResizeEnd() => Events.Add("resize-end");
    public void WheelGuardOn() => Events.Add("wheel-on");
    public void WheelGuardOff() => Events.Add("wheel-off");
    public void MaskKeyRelease() => Events.Add("mask-key-release");
    public void Tick() { }
    public bool Busy => false;

    public int Count(string name) => Events.Count(e => e == name);
    public bool Has(string name) => Events.Contains(name);
}
