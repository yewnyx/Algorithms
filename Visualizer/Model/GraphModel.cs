using System.Diagnostics;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>
/// A generated graph plus everything derived from one Tarjan pass: islands (strongly connected
/// components), execution order, the island-level dependency DAG, and parallel waves.
/// Edge A -> B means A must happen before B.
/// </summary>
public sealed class GraphModel {
    public int N { get; private init; }
    public int E => EdgeFrom.Length;
    public int[] EdgeFrom { get; private init; } = [];
    public int[] EdgeTo { get; private init; } = [];

    // Tarjan output. DependencyOrder is most-dependent first; Islands are slices into it.
    public int[] DependencyOrder { get; private init; } = [];
    public Segment[] Islands { get; private init; } = [];
    public int IslandCount { get; private init; }
    public TimeSpan TarjanTime { get; private init; }

    /// <summary>True when Tarjan's islands are exactly the generated ones; null when there is nothing to compare.</summary>
    public bool? MatchesTruth { get; private init; }

    // Derived per vertex.
    public int[] IslandOf { get; private init; } = [];
    public int[] ExecutionOrder { get; private init; } = []; // k -> vertex, dependencies first
    public int[] ExecutionIndex { get; private init; } = []; // vertex -> k
    public int[] WaveSlot { get; private init; } = [];       // vertex -> position within its wave, island-contiguous

    // Derived per island.
    public int[] IslandSize { get; private init; } = [];
    public int[] WaveOf { get; private init; } = [];
    public int MaxIslandSize { get; private init; }
    public int NonTrivialIslands { get; private init; }

    // Island DAG in CSR form: dependents of island i are IslandDepTargets[IslandDepStart[i] .. IslandDepStart[i+1]).
    public int[] IslandDepStart { get; private init; } = [];
    public int[] IslandDepTargets { get; private init; } = [];
    public int[] IslandInDegree { get; private init; } = [];

    public int WaveCount { get; private init; }
    public int[] WaveSize { get; private init; } = [];

    // Vertex adjacency in both directions, for highlighting.
    public int[] OutStart { get; private init; } = [];
    public int[] OutTargets { get; private init; } = [];
    public int[] InStart { get; private init; } = [];
    public int[] InTargets { get; private init; } = [];

    public static GraphModel Build(GeneratedGraph generated, int n, CancellationToken ct = default) =>
        Build(n, generated.From, generated.To, generated.TruthIsland, generated.TruthIslandCount, ct);

    public static GraphModel Build(int n, int[] from, int[] to, int[]? truth = null, int truthCount = 0, CancellationToken ct = default) {
        var graph = new Graph().EnsureIndex(n - 1);
        for (var i = 0; i < from.Length; i++) { graph.AddEdge(from[i], to[i]); }
        ct.ThrowIfCancellationRequested();

        int[] order = [];
        Segment[] islands = [];
        var sw = Stopwatch.StartNew();
        var count = graph.Tarjan(ref order, ref islands);
        sw.Stop();
        ct.ThrowIfCancellationRequested();

        var islandOf = new int[n];
        var islandSize = new int[count];
        var maxIsland = 0;
        var nonTrivial = 0;
        for (var i = 0; i < count; i++) {
            var (offset, size) = islands[i];
            islandSize[i] = size;
            if (size > maxIsland) { maxIsland = size; }
            if (size > 1) { nonTrivial++; }
            for (var j = offset; j < offset + size; j++) { islandOf[order[j]] = i; }
        }

        var executionOrder = new int[n];
        var executionIndex = new int[n];
        for (var k = 0; k < n; k++) {
            var v = order[n - 1 - k];
            executionOrder[k] = v;
            executionIndex[v] = k;
        }

        // Island DAG: dedupe cross-island edges by sorting packed keys.
        var keys = new List<long>();
        for (var i = 0; i < from.Length; i++) {
            var a = islandOf[from[i]];
            var b = islandOf[to[i]];
            if (a != b) { keys.Add((long)a * count + b); }
        }
        keys.Sort();
        var depStart = new int[count + 1];
        var depTargets = new List<int>(keys.Count);
        var inDegree = new int[count];
        long previous = -1;
        foreach (var key in keys) {
            if (key == previous) { continue; }
            previous = key;
            var a = (int)(key / count);
            var b = (int)(key % count);
            depStart[a + 1]++;
            depTargets.Add(b);
            inDegree[b]++;
        }
        for (var i = 0; i < count; i++) { depStart[i + 1] += depStart[i]; }
        var depTargetArray = depTargets.ToArray();

        // Waves: walk islands dependencies-first (highest index first) and push to dependents.
        var waveOf = new int[count];
        var waveCount = count == 0 ? 0 : 1;
        for (var i = count - 1; i >= 0; i--) {
            var next = waveOf[i] + 1;
            for (var j = depStart[i]; j < depStart[i + 1]; j++) {
                var d = depTargetArray[j];
                if (waveOf[d] < next) {
                    waveOf[d] = next;
                    if (next + 1 > waveCount) { waveCount = next + 1; }
                }
            }
        }
        var waveSize = new int[waveCount];
        var waveSlot = new int[n];
        for (var i = count - 1; i >= 0; i--) {
            var (offset, size) = islands[i];
            var w = waveOf[i];
            for (var j = offset; j < offset + size; j++) { waveSlot[order[j]] = waveSize[w]++; }
        }

        // Same partition: every Tarjan island is inside one generated island, and the counts agree.
        bool? matches = null;
        if (truth is not null) {
            matches = count == truthCount;
            for (var i = 0; i < count && matches == true; i++) {
                var (offset, size) = islands[i];
                var expected = truth[order[offset]];
                for (var j = offset + 1; j < offset + size; j++) {
                    if (truth[order[j]] != expected) { matches = false; break; }
                }
            }
        }

        BuildCsr(n, from, to, out var outStart, out var outTargets);
        BuildCsr(n, to, from, out var inStart, out var inTargets);

        return new GraphModel {
            N = n,
            EdgeFrom = from,
            EdgeTo = to,
            DependencyOrder = order,
            Islands = islands,
            IslandCount = count,
            TarjanTime = sw.Elapsed,
            MatchesTruth = matches,
            IslandOf = islandOf,
            ExecutionOrder = executionOrder,
            ExecutionIndex = executionIndex,
            WaveSlot = waveSlot,
            IslandSize = islandSize,
            WaveOf = waveOf,
            MaxIslandSize = maxIsland,
            NonTrivialIslands = nonTrivial,
            IslandDepStart = depStart,
            IslandDepTargets = depTargetArray,
            IslandInDegree = inDegree,
            WaveCount = waveCount,
            WaveSize = waveSize,
            OutStart = outStart,
            OutTargets = outTargets,
            InStart = inStart,
            InTargets = inTargets,
        };
    }

    private static void BuildCsr(int n, int[] source, int[] target, out int[] start, out int[] targets) {
        start = new int[n + 1];
        targets = new int[source.Length];
        for (var i = 0; i < source.Length; i++) { start[source[i] + 1]++; }
        for (var v = 0; v < n; v++) { start[v + 1] += start[v]; }
        var fill = new int[n];
        for (var i = 0; i < source.Length; i++) {
            var s = source[i];
            targets[start[s] + fill[s]++] = target[i];
        }
    }

    public ReadOnlySpan<int> IslandVertices(int island) => DependencyOrder.AsSpan(Islands[island].Offset, Islands[island].Size);
    public ReadOnlySpan<int> Dependents(int island) => IslandDepTargets.AsSpan(IslandDepStart[island], IslandDepStart[island + 1] - IslandDepStart[island]);
    public ReadOnlySpan<int> OutNeighbours(int v) => OutTargets.AsSpan(OutStart[v], OutStart[v + 1] - OutStart[v]);
    public ReadOnlySpan<int> InNeighbours(int v) => InTargets.AsSpan(InStart[v], InStart[v + 1] - InStart[v]);
}
