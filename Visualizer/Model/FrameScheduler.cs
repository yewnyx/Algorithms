using System.Diagnostics;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>One task as the scheduler sees it: how many nodes of each role, and whether it is pinned.</summary>
public readonly record struct TaskInfo(int Inputs, int Computes, int Outputs, bool Pinned, double Activity) {
    public int Nodes => Inputs + Computes + Outputs;
}

/// <param name="BudgetMs">Time per frame for the rotation.</param>
/// <param name="PinCap">Most tasks that may be pinned; pinned tasks past the cap join the rotation.</param>
/// <param name="VisitUs">Fixed cost of visiting a task, before any node runs.</param>
/// <param name="EditApplyUs">Cost of applying queued edits at the start of a rotation. Not counted against the budget.</param>
/// <param name="Jitter">Random spread applied to each visit's cost, as a fraction.</param>
public sealed record SchedulerSettings(
    double BudgetMs, int PinCap, double InputUs, double ComputeUs, double OutputUs,
    double VisitUs, double EditApplyUs, double Jitter, double Fps, int Seed) {
    public double BudgetUs => BudgetMs * 1000.0;
    public double FrameMs => 1000.0 / Math.Max(1, Fps);
}

public enum Lane : byte { Pinned, Rotation }

public struct SliceSegment {
    public int Task;
    public float CostUs;
    public Lane Lane;
    public bool Changed;
}

/// <summary>What the scheduler did in one frame.</summary>
public sealed class SchedulerFrame {
    public long Frame;
    public long Rotation;
    public float EditUs;
    public float PinnedUs;
    public float RotationUs;
    public bool StartedRotation;
    public bool CompletedRotation;
    public readonly List<SliceSegment> Segments = new();

    /// <summary>Time compared against the budget.</summary>
    public float CountedUs => PinnedUs + RotationUs;
    public float TotalUs => EditUs + CountedUs;
}

public static class TaskSplit {
    /// <summary>
    /// Splits the generated nodes into tasks: each connected component, edge direction ignored, is one task.
    /// Components come out ordered by their lowest node, which is the rotation order.
    /// </summary>
    public static (TaskInfo[] tasks, bool matchesTruth, TimeSpan time) Split(GeneratedTasks g) {
        var graph = new Graph().EnsureIndex(g.NodeCount - 1);
        for (var i = 0; i < g.From.Length; i++) { graph.AddEdge(g.From[i], g.To[i]); }

        int[]? order = null;
        Segment[]? components = null;
        var sw = Stopwatch.StartNew();
        var count = graph.ConnectedComponents(ref order, ref components);
        sw.Stop();

        var tasks = new TaskInfo[count];
        var matches = count == g.TaskCount;
        for (var c = 0; c < count; c++) {
            var (offset, size) = components[c];
            int inputs = 0, computes = 0, outputs = 0;
            var pinned = false;
            var expected = g.TruthTask[order[offset]];
            for (var j = offset; j < offset + size; j++) {
                var v = order[j];
                switch (g.Role[v]) {
                    case NodeRole.Input: inputs++; break;
                    case NodeRole.Compute: computes++; break;
                    default: outputs++; break;
                }
                pinned |= g.Pinned[v];
                if (g.TruthTask[v] != expected) { matches = false; }
            }
            tasks[c] = new TaskInfo(inputs, computes, outputs, pinned, g.Activity);
        }
        return (tasks, matches, sw.Elapsed);
    }
}

/// <summary>
/// A deterministic replay, in simulated time, of a frame-budgeted round-robin scheduler. One call to Step
/// is one frame. The rules are kept literal, including the ones that look odd:
/// - queued edits apply only when the rotation starts over, and their cost is not counted against the budget;
/// - pinned tasks run every frame, unbudgeted, but their cost counts against the budget;
/// - the rotation is a do-while, so at least one task runs per frame even when over budget;
/// - the budget is checked only between tasks, so a frame can overshoot by one task;
/// - finishing the rotation ends the frame at once, even with budget left, so the next one starts next frame.
/// Within a task, input nodes always run; if nothing changed the task stops there, otherwise its compute
/// and output nodes run too.
/// </summary>
public sealed class FrameScheduler {
    private readonly TaskInfo[] _tasks;
    private readonly int[] _rotation;
    private readonly int[] _pinned;
    private readonly SchedulerFrame[] _history;
    private readonly int[] _recentRotations = new int[64];
    private readonly Random _rng;
    private int _cursor;
    private long _rotationStartFrame;
    private int _recentRotationCount;
    private double _overshootSum;
    private long _overshootFrames;

    public SchedulerSettings Settings { get; }
    public IReadOnlyList<TaskInfo> Tasks => _tasks;
    public ReadOnlySpan<int> RotationOrder => _rotation;
    public ReadOnlySpan<int> PinnedTasks => _pinned;
    public int OverCap { get; }

    public long FrameCount { get; private set; }
    public long RotationsCompleted { get; private set; }
    public int Cursor => _cursor;
    public int HistoryCapacity => _history.Length;

    // Per task.
    public long[] LastVisitFrame { get; }
    public int[] MaxGap { get; }
    public long[] Visits { get; }
    public bool[] LastChanged { get; }

    public int WorstGap { get; private set; }
    public float WorstFrameUs { get; private set; }

    /// <summary>Largest amount a frame's counted time went past the budget. The rotation stops only after crossing it.</summary>
    public float MaxOvershootUs { get; private set; }
    public double MeanOvershootUs => _overshootFrames == 0 ? 0 : _overshootSum / _overshootFrames;

    /// <summary>Expected cost of running every task once, which is what an unbudgeted scheduler would spend each frame.</summary>
    public double UnbudgetedUs { get; }

    /// <param name="historyCapacity">Frames of history to keep, or 0 to size it so about 8 million segments are kept.</param>
    public FrameScheduler(TaskInfo[] tasks, SchedulerSettings settings, int historyCapacity = 0) {
        _tasks = tasks;
        Settings = settings;
        _rng = new Random(settings.Seed);

        var total = 0.0;
        foreach (var t in tasks) { total += ExpectedCost(t); }
        UnbudgetedUs = total;

        if (historyCapacity <= 0) {
            // A large budget runs thousands of tasks per frame; keep memory bounded by segments, not frames.
            var meanVisit = tasks.Length == 0 ? 1.0 : Math.Max(0.01, total / tasks.Length);
            var perFrame = Math.Min(tasks.Length, settings.BudgetUs / meanVisit + settings.PinCap) + 1;
            historyCapacity = (int)Math.Clamp(8_000_000 / perFrame, 64, 4096);
        }
        _history = new SchedulerFrame[historyCapacity];
        for (var i = 0; i < historyCapacity; i++) { _history[i] = new SchedulerFrame(); }

        // The first PinCap pinned tasks run every frame; pinned tasks past the cap join the rotation,
        // which keeps its order.
        var pinned = new List<int>();
        var rotation = new List<int>(tasks.Length);
        var overCap = 0;
        for (var i = 0; i < tasks.Length; i++) {
            if (tasks[i].Pinned) {
                if (pinned.Count < settings.PinCap) { pinned.Add(i); continue; }
                overCap++;
            }
            rotation.Add(i);
        }
        _pinned = pinned.ToArray();
        _rotation = rotation.ToArray();
        OverCap = overCap;

        LastVisitFrame = new long[tasks.Length];
        Array.Fill(LastVisitFrame, -1);
        MaxGap = new int[tasks.Length];
        Visits = new long[tasks.Length];
        LastChanged = new bool[tasks.Length];
    }

    public double ExpectedCost(TaskInfo t) =>
        Settings.VisitUs + t.Inputs * Settings.InputUs + t.Activity * (t.Computes * Settings.ComputeUs + t.Outputs * Settings.OutputUs);

    public SchedulerFrame? GetFrame(long frame) {
        if (frame < 0 || frame >= FrameCount || frame < FrameCount - _history.Length) { return null; }
        return _history[frame % _history.Length];
    }

    public double AverageRotationFrames {
        get {
            if (_recentRotationCount == 0) { return 0; }
            var n = Math.Min(_recentRotationCount, _recentRotations.Length);
            var sum = 0.0;
            for (var i = 0; i < n; i++) { sum += _recentRotations[i]; }
            return sum / n;
        }
    }

    public int LastRotationFrames => _recentRotationCount == 0 ? 0 : _recentRotations[(_recentRotationCount - 1) % _recentRotations.Length];

    /// <summary>Frames the current rotation has used so far, counting the frame in progress.</summary>
    public int CurrentRotationFrames => (int)(FrameCount - _rotationStartFrame);

    public void Step() {
        var frameIndex = FrameCount;
        var frame = _history[frameIndex % _history.Length];
        frame.Frame = frameIndex;
        frame.Rotation = RotationsCompleted;
        frame.Segments.Clear();
        frame.EditUs = frame.PinnedUs = frame.RotationUs = 0;
        frame.StartedRotation = frame.CompletedRotation = false;

        if (_cursor == 0) {
            frame.StartedRotation = true;
            frame.EditUs = (float)Settings.EditApplyUs;
            _rotationStartFrame = frameIndex;
        }

        var elapsed = 0.0;
        foreach (var t in _pinned) {
            var cost = Visit(t, frameIndex, Lane.Pinned, frame);
            frame.PinnedUs += (float)cost;
            elapsed += cost;
        }

        bool completed;
        if (_rotation.Length == 0) {
            _cursor = 0;
            completed = true;
        } else {
            var budget = Settings.BudgetUs;
            completed = false;
            do {
                var cost = Visit(_rotation[_cursor], frameIndex, Lane.Rotation, frame);
                frame.RotationUs += (float)cost;
                elapsed += cost;
                _cursor++;
                if (_cursor >= _rotation.Length) {
                    _cursor = 0;
                    completed = true;
                    break;
                }
            } while (elapsed < budget);
        }

        frame.CompletedRotation = completed;
        FrameCount = frameIndex + 1;
        if (completed) {
            _recentRotations[_recentRotationCount++ % _recentRotations.Length] = (int)(FrameCount - _rotationStartFrame);
            RotationsCompleted++;
        }
        if (frame.TotalUs > WorstFrameUs) { WorstFrameUs = frame.TotalUs; }
        var over = frame.CountedUs - (float)Settings.BudgetUs;
        if (over > 0) {
            _overshootSum += over;
            _overshootFrames++;
            if (over > MaxOvershootUs) { MaxOvershootUs = over; }
        }
    }

    private double Visit(int t, long frameIndex, Lane lane, SchedulerFrame frame) {
        var info = _tasks[t];
        var cost = Settings.VisitUs + info.Inputs * Settings.InputUs;
        var changed = info.Activity > 0 && _rng.NextDouble() < info.Activity;
        if (changed) { cost += info.Computes * Settings.ComputeUs + info.Outputs * Settings.OutputUs; }
        if (Settings.Jitter > 0) { cost *= 1 + Settings.Jitter * (2 * _rng.NextDouble() - 1); }

        var last = LastVisitFrame[t];
        if (last >= 0) {
            var gap = (int)(frameIndex - last);
            if (gap > MaxGap[t]) { MaxGap[t] = gap; }
            if (gap > WorstGap) { WorstGap = gap; }
        }
        LastVisitFrame[t] = frameIndex;
        Visits[t]++;
        LastChanged[t] = changed;

        frame.Segments.Add(new SliceSegment { Task = t, CostUs = (float)cost, Lane = lane, Changed = changed });
        return cost;
    }
}
