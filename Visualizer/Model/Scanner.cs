using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yewnyx.Algorithms.Visualizer.Model;

public enum ScanMode { Serial, Parallel }

/// <summary>
/// Walks the scheduled vertices and "performs" each one by spinning for a configurable cost.
/// Serial mode is a plain linear scan over the execution order, which is a valid serial schedule.
/// Parallel mode runs a lock-free dependency-counting scheduler over islands: each island carries
/// an atomic count of unfinished dependencies, a finished island decrements its dependents, and
/// whoever drops a count to zero pushes that island onto a lock-free ready queue.
/// </summary>
public sealed class Scanner : IDisposable {
    private readonly GraphModel _model;
    private readonly Thread[] _threads;
    private readonly int[] _remaining;         // per island: dependencies not yet complete
    private readonly ConcurrentQueue<int> _ready = new();
    private int _islandsDone;
    private int _paused;
    private int _cancelled;
    private int _started;
    private int _running;                      // threads still alive
    private double _nodeCostMicroseconds;

    public ScanState State { get; }
    public ScanMode Mode { get; }

    /// <summary>Simulated cost per vertex. Can be changed while running; workers read it per vertex.</summary>
    public double NodeCostMicroseconds {
        get => Volatile.Read(ref _nodeCostMicroseconds);
        set => Volatile.Write(ref _nodeCostMicroseconds, value);
    }

    public bool IsPaused => Volatile.Read(ref _paused) == 1;
    public bool IsRunning => Volatile.Read(ref _running) > 0;
    public bool HasStarted => Volatile.Read(ref _started) == 1;

    public Scanner(GraphModel model, ScanMode mode, int workers, double nodeCostMicroseconds) {
        _model = model;
        Mode = mode;
        workers = mode == ScanMode.Serial ? 1 : Math.Max(1, workers);
        _nodeCostMicroseconds = nodeCostMicroseconds;
        State = new ScanState(model.N, workers);
        _remaining = new int[model.IslandCount];
        _threads = new Thread[workers];
        for (var w = 0; w < workers; w++) {
            var id = w;
            _threads[w] = new Thread(() => {
                try {
                    if (mode == ScanMode.Serial) { RunSerial(); } else { RunParallel(id); }
                } finally {
                    Interlocked.Decrement(ref _running);
                }
            }) { IsBackground = true, Name = $"scan-{w}" };
        }
    }

    public void Start() {
        if (Interlocked.Exchange(ref _started, 1) == 1) { return; }
        if (Mode == ScanMode.Parallel) {
            Array.Copy(_model.IslandInDegree, _remaining, _remaining.Length);
            for (var i = 0; i < _remaining.Length; i++) { if (_remaining[i] == 0) { _ready.Enqueue(i); } }
        }
        Volatile.Write(ref _running, _threads.Length);
        Volatile.Write(ref State.StartTicks, Stopwatch.GetTimestamp());
        if (_model.N == 0) {
            Volatile.Write(ref State.EndTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _running, 0);
            return;
        }
        foreach (var t in _threads) { t.Start(); }
    }

    public void Pause() => Volatile.Write(ref _paused, 1);
    public void Resume() => Volatile.Write(ref _paused, 0);
    public void Cancel() { Volatile.Write(ref _cancelled, 1); Volatile.Write(ref _paused, 0); }
    public void Dispose() => Cancel();

    private bool WaitWhilePaused() {
        while (Volatile.Read(ref _paused) == 1) {
            if (Volatile.Read(ref _cancelled) == 1) { return false; }
            Thread.Sleep(1);
        }
        return Volatile.Read(ref _cancelled) == 0;
    }

    private void Process(int vertex, int worker) {
        Volatile.Write(ref State.WorkerCurrent[worker].Value, vertex);
        var started = Stopwatch.GetTimestamp();
        var cost = NodeCostMicroseconds;
        if (cost > 0) {
            var until = started + (long)(cost * Stopwatch.Frequency / 1_000_000.0);
            while (Stopwatch.GetTimestamp() < until) { }
        }
        State.MarkDone(vertex, worker, started, Stopwatch.GetTimestamp());
    }

    private void RunSerial() {
        var order = _model.ExecutionOrder;
        for (var k = 0; k < order.Length; k++) {
            if (!WaitWhilePaused()) { return; }
            Process(order[k], 0);
        }
        Volatile.Write(ref State.WorkerCurrent[0].Value, -1);
        Volatile.Write(ref State.EndTicks, Stopwatch.GetTimestamp());
    }

    private void RunParallel(int worker) {
        var islandCount = _model.IslandCount;
        var spinner = new SpinWait();
        while (Volatile.Read(ref _cancelled) == 0) {
            if (Volatile.Read(ref _islandsDone) >= islandCount) { break; }
            if (!_ready.TryDequeue(out var island)) {
                spinner.SpinOnce();
                continue;
            }
            spinner.Reset();

            Volatile.Write(ref State.WorkerIsland[worker].Value, island);
            foreach (var v in _model.IslandVertices(island)) {
                if (!WaitWhilePaused()) { return; }
                Process(v, worker);
            }
            Volatile.Write(ref State.WorkerCurrent[worker].Value, -1);
            Volatile.Write(ref State.WorkerIsland[worker].Value, -1);

            // Release dependents. Interlocked is a full fence, so whichever worker dequeues a
            // dependent next also sees every write this island made.
            foreach (var d in _model.Dependents(island)) {
                if (Interlocked.Decrement(ref _remaining[d]) == 0) { _ready.Enqueue(d); }
            }
            if (Interlocked.Increment(ref _islandsDone) == islandCount) {
                Volatile.Write(ref State.EndTicks, Stopwatch.GetTimestamp());
            }
        }
    }

    /// <summary>Counts cross-island edges whose target started before the source finished.</summary>
    public int CountOrderViolations() {
        var violations = 0;
        var from = _model.EdgeFrom;
        var to = _model.EdgeTo;
        var islandOf = _model.IslandOf;
        for (var i = 0; i < from.Length; i++) {
            var a = from[i];
            var b = to[i];
            if (islandOf[a] == islandOf[b]) { continue; }
            if (State.ProcessedBy[a] == ScanState.NotProcessed || State.ProcessedBy[b] == ScanState.NotProcessed) { continue; }
            if (State.FinishedAt[a] > State.StartedAt[b]) { violations++; }
        }
        return violations;
    }
}
