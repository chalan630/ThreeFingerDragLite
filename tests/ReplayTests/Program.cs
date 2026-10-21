using ThreeFingerDragLite;
using ThreeFingerDragLite.Engine;
using ThreeFingerDragLite.Touchpad;

namespace ReplayTests;

/// <summary>
/// Dependency-free test runner: synthetic scenarios against <see cref="DragEngine"/> with a fake host.
/// Run: dotnet run --project tests/ReplayTests [-- path\to\log.txt]
/// With a log path, the recorded "contacts:" frames (VerboseLog) are replayed and a summary is printed.
/// </summary>
internal static class Program {
    private static int _failed;
    private static int _passed;

    private static int Main(string[] args) {
        Logger.SetEnabled(false);

        if (args.Length > 0) return ReplayFile(args[0]);

        Run("three fingers drag: press, move, release after hold", ThreeFingerDrag);
        Run("hold disabled: release when fingers lift", HoldDisabled);
        Run("hold resume: fingers back within hold time continue the drag", HoldResume);
        Run("four fingers never arm", FourFingersIgnored);
        Run("modifier + three fingers moves the window, modifier release ends it", ModifierMove);
        Run("window move resumes quickly after a silence release", RegrabAfterSilence);
        Run("late modifier still upgrades to window move", LateModifier);
        Run("modifier + two fingers resizes with averaged delta", ResizeAverage);
        Run("resize ends when modifier released", ResizeModifierRelease);
        Run("resize survives a short pause", ResizeSurvivesPause);
        Run("resize with invalid target returns to idle", ResizeInvalidTarget);
        Run("aspect lock key is forwarded", AspectLock);
        Run("configurable modifier (Ctrl) and action mapping", CustomMapping);
        Run("three-finger action None never arms", ActionNone);
        Run("gain model needs three bins, interpolates and resets", GainModelCurve);
        Run("three-finger gain follows the learned single-finger curve", LearnedGainDrivesDrag);

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test) {
        try {
            test();
            _passed++;
            Console.WriteLine("PASS  " + name);
        } catch (Exception ex) {
            _failed++;
            Console.WriteLine("FAIL  " + name + " — " + ex.Message);
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    private static (DragEngine engine, FakeHost host, AppConfig config) Setup(Action<AppConfig>? tweak = null) {
        var config = new AppConfig { Acceleration = 0 };
        tweak?.Invoke(config);
        var host = new FakeHost { Now = 10_000 };
        return (new DragEngine(config, host), host, config);
    }

    private static TouchpadContact C(int id, int x, int y) => new(id, x, y);

    private static void Frame(DragEngine engine, FakeHost host, int advanceMs, params TouchpadContact[] contacts) {
        host.Now += advanceMs;
        engine.OnContacts(contacts);
    }

    // Moves N fingers right by `step` per frame for `frames` frames, 8ms apart.
    private static void Swipe(DragEngine engine, FakeHost host, int fingers, int frames, int step, ref int x) {
        for (int i = 0; i < frames; i++) {
            x += step;
            var contacts = new TouchpadContact[fingers];
            for (int f = 0; f < fingers; f++) contacts[f] = C(f + 1, x, 300 + f * 40);
            Frame(engine, host, 8, contacts);
        }
    }

    private static void ThreeFingerDrag() {
        var (engine, host, _) = Setup();
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        Check(host.Has("button-down:Left"), "button not pressed");
        Check(host.CursorDx > 0, "cursor did not move");
        Check(host.Count("button-down:Left") == 1, "pressed more than once");

        host.Now += 100;
        engine.Tick(); // input silence → holding
        Check(engine.StateName == "Holding", "expected Holding, got " + engine.StateName);
        Check(!host.Has("button-up:Left"), "released too early");

        host.Now += 1200;
        engine.Tick();
        Check(host.Has("button-up:Left"), "not released after hold timeout");
        Check(engine.StateName == "Idle", "expected Idle, got " + engine.StateName);
    }

    private static void HoldDisabled() {
        var (engine, host, _) = Setup(c => c.HoldEnabled = false);
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        host.Now += 100;
        engine.Tick();
        Check(host.Has("button-up:Left"), "not released on lift");
        Check(engine.StateName == "Idle", "expected Idle");
    }

    private static void HoldResume() {
        var (engine, host, _) = Setup();
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        host.Now += 100;
        engine.Tick();
        Check(engine.StateName == "Holding", "expected Holding");
        Frame(engine, host, 200, C(1, 400, 300), C(2, 400, 340), C(3, 400, 380));
        Check(engine.StateName == "Dragging", "expected Dragging after re-grip, got " + engine.StateName);
        Check(!host.Has("button-up:Left"), "button was released during hold resume");
    }

    private static void FourFingersIgnored() {
        var (engine, host, _) = Setup();
        int x = 100;
        Swipe(engine, host, 4, 30, 5, ref x);
        Check(host.Events.Count == 0, "four fingers triggered: " + string.Join(",", host.Events));
        Check(engine.StateName == "Idle", "expected Idle");
    }

    private static void ModifierMove() {
        var (engine, host, _) = Setup();
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        Check(host.Has("move-start"), "window move not started");
        Check(!host.Has("button-down:Left"), "button pressed in window-move mode");
        Check(host.Count("mask-key-release") == 1, "Alt release must be masked once at gesture start, got " + host.Count("mask-key-release"));
        Check(engine.StateName == "Dragging", "expected Dragging");

        host.Keys.Remove(ModifierKey.Alt);
        Swipe(engine, host, 3, 1, 4, ref x);
        Check(host.Has("move-end"), "window move not ended after modifier release");
        Check(engine.StateName == "Idle", "expected Idle");
    }

    private static void RegrabAfterSilence() {
        var (engine, host, _) = Setup(c => c.HoldEnabled = false);
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        host.Now += 100;
        engine.Tick(); // pad went quiet: released without hold
        Check(host.Has("move-end") && engine.StateName == "Idle", "expected silence release to Idle");
        Check(host.Count("move-start") == 1, "unexpected extra move-start");

        host.Now += 50;
        Swipe(engine, host, 3, 12, 1, ref x); // 12 units total would be needed without the quick regrab
        Check(host.Count("move-start") == 2, "quick regrab did not resume the window move");

        host.Now += 100;
        engine.Tick(); // silence release again, which re-opens the regrab window
        Check(engine.StateName == "Idle", "expected Idle after second silence release");
        host.Now += 600; // past the regrab window
        Swipe(engine, host, 3, 12, 1, ref x);
        Check(host.Count("move-start") == 2, "regrab must expire after the window");
    }

    private static void LateModifier() {
        var (engine, host, _) = Setup(c => c.StartThreshold = 40);
        int x = 100;
        Swipe(engine, host, 3, 2, 3, ref x); // arm, not triggered yet
        host.Keys.Add(ModifierKey.Alt);
        Swipe(engine, host, 3, 20, 4, ref x);
        Check(host.Has("move-start"), "late modifier did not upgrade to window move");
        Check(!host.Has("button-down:Left"), "button pressed despite modifier");
    }

    private static void ResizeAverage() {
        var (engine, host, config) = Setup();
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        // Two fingers: one moves 6 px/frame, the other stays still — the average is 3 px/frame.
        for (int i = 0; i < 25; i++) {
            x += 6;
            Frame(engine, host, 8, C(1, x, 300), C(2, 100, 340));
        }
        Check(host.Has("resize-start"), "resize not started");
        Check(host.Has("wheel-on"), "wheel guard not enabled");
        Check(host.ResizeDx > 0, "no resize delta");
        // Each delta is scaled by AltResizeSpeed; the unscaled average per frame must be ~3.
        float perFrame = host.ResizeDx / host.Count("resize-delta") / config.AltResizeSpeed;
        Check(MathF.Abs(perFrame - 3f) < 0.8f, $"expected average delta ≈3 per frame, got {perFrame:F2}");
        Check(!host.LastLockAspect, "aspect lock on without the key");
    }

    private static void ResizeModifierRelease() {
        var (engine, host, _) = Setup();
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        for (int i = 0; i < 20; i++) {
            x += 5;
            Frame(engine, host, 8, C(1, x, 300), C(2, x, 340));
        }
        Check(engine.StateName == "Resizing", "expected Resizing, got " + engine.StateName);
        host.Keys.Remove(ModifierKey.Alt);
        Frame(engine, host, 8, C(1, x + 5, 300), C(2, x + 5, 340));
        Check(host.Has("resize-end"), "resize not ended");
        Check(host.Has("wheel-off"), "wheel guard not released");
        Check(engine.StateName == "Idle", "expected Idle");
    }

    private static void ResizeSurvivesPause() {
        var (engine, host, _) = Setup();
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        for (int i = 0; i < 20; i++) {
            x += 5;
            Frame(engine, host, 8, C(1, x, 300), C(2, x, 340));
        }
        host.Now += 120; // shorter than the 180ms resize silence window
        engine.Tick();
        Check(engine.StateName == "Resizing", "resize dropped after a 120ms pause");
        host.Now += 200;
        engine.Tick();
        Check(engine.StateName == "Idle", "resize did not end after long silence");
    }

    private static void ResizeInvalidTarget() {
        var (engine, host, _) = Setup();
        host.ResizeTargetValid = false;
        host.Keys.Add(ModifierKey.Alt);
        int x = 100;
        for (int i = 0; i < 20; i++) {
            x += 5;
            Frame(engine, host, 8, C(1, x, 300), C(2, x, 340));
        }
        Check(engine.StateName != "Resizing", "entered Resizing without a valid target");
        Check(host.Has("wheel-off"), "wheel guard left on");
    }

    private static void AspectLock() {
        var (engine, host, _) = Setup();
        host.Keys.Add(ModifierKey.Alt);
        host.Keys.Add(ModifierKey.Shift);
        int x = 100;
        for (int i = 0; i < 20; i++) {
            x += 5;
            Frame(engine, host, 8, C(1, x, 300), C(2, x, 340));
        }
        Check(host.LastLockAspect, "Shift did not lock the aspect ratio");
        // One tap at gesture start (Alt) plus one when Shift engages the lock (after Shift went down).
        Check(host.Count("mask-key-release") == 2, "expected 2 masks (Alt at start, Shift at lock), got " + host.Count("mask-key-release"));
    }

    private static void CustomMapping() {
        var (engine, host, _) = Setup(c => {
            c.GestureModifier = ModifierKey.Ctrl;
            c.ModifiedThreeFingerAction = GestureAction.ResizeWindow;
        });
        host.Keys.Add(ModifierKey.Alt); // the old modifier must now be inert
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        Check(host.Has("button-down:Left"), "Alt should be ignored when Ctrl is the modifier (plain drag expected)");
    }

    private static void ActionNone() {
        var (engine, host, _) = Setup(c => c.ThreeFingerAction = GestureAction.None);
        int x = 100;
        Swipe(engine, host, 3, 20, 4, ref x);
        Check(host.Events.Count == 0, "unexpected events: " + string.Join(",", host.Events));
    }

    private static void GainModelCurve() {
        var model = new GainModel();
        Check(model.Evaluate(1f) == null, "empty model must have no curve");
        model.AddChunk(160, 160, 80);   // 2.0 u/ms, gain 1.0
        model.AddChunk(160, 160, 80);
        Check(model.Evaluate(1f) == null, "one valid bin is not enough");
        for (int i = 0; i < 3; i++) model.AddChunk(60, 54, 80);    // 0.75 u/ms, gain 0.9
        for (int i = 0; i < 7; i++) model.AddChunk(20, 12, 80);    // 0.25 u/ms, gain 0.6
        Check(model.ValidBins == 3, "expected 3 valid bins, got " + model.ValidBins);
        float slow = model.Evaluate(0.01f) ?? -1, mid = model.Evaluate(0.75f) ?? -1, fast = model.Evaluate(9f) ?? -1;
        Check(MathF.Abs(slow - 0.6f) < 0.01f, $"low end clamps to the first bin: {slow}");
        Check(MathF.Abs(mid - 0.9f) < 0.01f, $"mid bin gain: {mid}");
        Check(MathF.Abs(fast - 1.0f) < 0.01f, $"high end clamps to the last bin: {fast}");
        float between = model.Evaluate(0.5f) ?? -1;
        Check(between > 0.6f && between < 0.9f, $"interpolated gain out of range: {between}");
        model.Reset();
        Check(model.Evaluate(1f) == null, "reset must clear the curve");
    }

    private static void LearnedGainDrivesDrag() {
        var config = new AppConfig { Acceleration = 10 };
        var host = new FakeHost { Now = 10_000 };
        var engine = new DragEngine(config, host, new GainModel());

        // (px per frame step, cursor px per frame): gain 1.0 at 0.25 u/ms, 1.5 at 0.75, 2.0 at 2.0.
        foreach (var (step, cursorStep) in new[] { (2, 2), (6, 9), (16, 32) }) {
            for (int s = 0; s < 8; s++) {
                host.CursorX = 960;
                int x = 100;
                host.Now += 100;
                for (int i = 0; i < 25; i++) {
                    x += step;
                    host.CursorX += cursorStep;
                    Frame(engine, host, 8, C(1, x, 300));
                }
            }
        }
        Check(engine.Gain.ValidBins >= 3, "single-finger strokes did not teach the model: " + engine.Gain.Describe());

        int fx = 100;
        host.Now += 200;
        Swipe(engine, host, 3, 30, 16, ref fx);
        double before = host.CursorDx;
        Swipe(engine, host, 3, 20, 16, ref fx);
        double ratio = (host.CursorDx - before) / (20 * 16);
        Check(Math.Abs(ratio - 2.0) < 0.15, $"three-finger gain at 2 u/ms should follow the learned 2.0, got {ratio:F2}");
    }

    private static int ReplayFile(string path) {
        var frames = LogReplay.Parse(File.ReadLines(path));
        if (frames.Count == 0) {
            Console.WriteLine("no 'contacts:' lines found (enable VerboseLog to record them)");
            return 1;
        }

        var config = new AppConfig();
        var host = new FakeHost { Now = frames[0].ms };
        var engine = new DragEngine(config, host);
        string lastState = engine.StateName;
        foreach (var (ms, contacts) in frames) {
            // Fill the gap between frames with timer ticks like the real 30ms WM_TIMER would.
            while (host.Now + 30 < ms) {
                host.Now += 30;
                engine.Tick();
                lastState = Report(engine, lastState, host.Now);
            }
            host.Now = ms;
            engine.OnContacts(contacts);
            lastState = Report(engine, lastState, ms);
        }
        host.Now += 3000;
        engine.Tick();
        Report(engine, lastState, host.Now);

        Console.WriteLine($"frames: {frames.Count}, button-down: {host.Count("button-down:Left")}, " +
                          $"cursor total: ({host.CursorDx:F0}, {host.CursorDy:F0})");
        return 0;
    }

    private static string Report(DragEngine engine, string last, long ms) {
        if (engine.StateName == last) return last;
        Console.WriteLine($"{ms,10}  {last} -> {engine.StateName}");
        return engine.StateName;
    }
}
