using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>A counter on its own cache line so workers never false-share.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct PaddedInt {
    [FieldOffset(64)] public int Value;
}

/// <summary>
/// Shared progress written by scan workers and sampled by the renderer without locks.
/// Every vertex slot is written by exactly one worker, and every per-worker slot by its owner,
/// so readers only ever see "not yet" or a complete value; no slot can tear or be half-written.
/// </summary>
public sealed class ScanState {
    public const int NotProcessed = -1;

    public readonly int N;
    public readonly int WorkerCount;
    public readonly int[] ProcessedBy;   // vertex -> worker id or NotProcessed
    public readonly long[] StartedAt;    // Stopwatch ticks
    public readonly long[] FinishedAt;
    public readonly PaddedInt[] WorkerDone;
    public readonly PaddedInt[] WorkerCurrent; // vertex being processed, -1 when idle
    public readonly PaddedInt[] WorkerIsland;

    private int _done;
    public long StartTicks;
    public long EndTicks;

    public ScanState(int n, int workerCount) {
        N = n;
        WorkerCount = workerCount;
        ProcessedBy = new int[n];
        Array.Fill(ProcessedBy, NotProcessed);
        StartedAt = new long[n];
        FinishedAt = new long[n];
        WorkerDone = new PaddedInt[workerCount];
        WorkerCurrent = new PaddedInt[workerCount];
        WorkerIsland = new PaddedInt[workerCount];
        for (var w = 0; w < workerCount; w++) { WorkerCurrent[w].Value = -1; WorkerIsland[w].Value = -1; }
    }

    public int Done => Volatile.Read(ref _done);
    public bool Finished => Volatile.Read(ref EndTicks) != 0;

    public TimeSpan Elapsed {
        get {
            var start = Volatile.Read(ref StartTicks);
            if (start == 0) { return TimeSpan.Zero; }
            var end = Volatile.Read(ref EndTicks);
            if (end == 0) { end = Stopwatch.GetTimestamp(); }
            return TimeSpan.FromSeconds((end - start) / (double)Stopwatch.Frequency);
        }
    }

    internal void MarkDone(int vertex, int worker, long started, long finished) {
        StartedAt[vertex] = started;
        FinishedAt[vertex] = finished;
        Volatile.Write(ref ProcessedBy[vertex], worker);
        Volatile.Write(ref WorkerDone[worker].Value, WorkerDone[worker].Value + 1);
        Interlocked.Increment(ref _done);
    }
}
