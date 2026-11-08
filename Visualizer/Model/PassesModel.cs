using System.Diagnostics;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>
/// One graph run through the three passes callers combine:
/// ConnectedComponents (direction ignored), Tarjan rooted at the component vertices (directed, so its
/// output splits exactly on the component segments), and Tarjan rooted at vertices the user picks.
/// </summary>
public sealed class PassesModel {
    private readonly Graph _graph;
    private int[] _rootsCopy = [];

    public GraphModel Scc { get; private init; }
    public int N => Scc.N;

    // ConnectedComponents output.
    public int[] CcOrder { get; private init; } = [];
    public Segment[] CcSegments { get; private init; } = [];
    public int CcCount { get; private init; }
    public int[] CcOf { get; private init; } = [];
    public bool? CcMatchesTruth { get; private init; }

    // Tarjan rooted at the CC vertices, in CC order.
    public int[] RootedOrder { get; private init; } = [];
    public Segment[] RootedSegments { get; private init; } = [];
    public int RootedCount { get; private init; }
    public int[] RootedSccOf { get; private init; } = []; // vertex -> index into RootedSegments
    public bool RootedAlignsWithCc { get; private init; }

    public TimeSpan CcTime { get; private init; }
    public TimeSpan RootedTime { get; private init; }

    // Shared layout for both panes, normalised to 0..1.
    public float[] X { get; private init; } = [];
    public float[] Y { get; private init; } = [];

    private PassesModel(Graph graph, GraphModel scc) {
        _graph = graph;
        Scc = scc;
    }

    public static PassesModel Build(GeneratedClusters g, CancellationToken ct = default) {
        var n = g.VertexCount;
        var graph = new Graph().EnsureIndex(n - 1);
        for (var i = 0; i < g.From.Length; i++) { graph.AddEdge(g.From[i], g.To[i]); }
        ct.ThrowIfCancellationRequested();

        var scc = GraphModel.Build(n, g.From, g.To, g.TruthIsland, g.IslandCount, ct);

        var ccOrder = new int[n];
        var ccSegments = new Segment[n];
        var sw = Stopwatch.StartNew();
        var ccCount = graph.ConnectedComponents(ccOrder, ccSegments);
        var ccTime = sw.Elapsed;

        var ccOf = new int[n];
        var ccMatches = ccCount == g.ClusterCount;
        for (var c = 0; c < ccCount; c++) {
            var (offset, size) = ccSegments[c];
            var expected = g.TruthCluster[ccOrder[offset]];
            for (var j = offset; j < offset + size; j++) {
                ccOf[ccOrder[j]] = c;
                if (g.TruthCluster[ccOrder[j]] != expected) { ccMatches = false; }
            }
        }

        // Roots must not share memory with the output, so they come from the CC array.
        var rootedOrder = new int[n];
        var rootedSegments = new Segment[n];
        sw.Restart();
        var rootedCount = graph.Tarjan(ccOrder, rootedOrder, rootedSegments);
        var rootedTime = sw.Elapsed;

        var rootedSccOf = new int[n];
        for (var s = 0; s < rootedCount; s++) {
            var (offset, size) = rootedSegments[s];
            for (var j = offset; j < offset + size; j++) { rootedSccOf[rootedOrder[j]] = s; }
        }

        // Every CC segment must hold exactly the vertices written in the same span of the rooted output,
        // and no SCC segment may straddle a CC boundary.
        var aligned = true;
        var seg = 0;
        for (var c = 0; c < ccCount && aligned; c++) {
            var (offset, size) = ccSegments[c];
            for (var j = offset; j < offset + size; j++) {
                if (ccOf[rootedOrder[j]] != c) { aligned = false; break; }
            }
            while (seg < rootedCount && rootedSegments[seg].Offset < offset + size) {
                if (rootedSegments[seg].Offset + rootedSegments[seg].Size > offset + size) { aligned = false; }
                seg++;
            }
        }

        var model = new PassesModel(graph, scc) {
            CcOrder = ccOrder,
            CcSegments = ccSegments,
            CcCount = ccCount,
            CcOf = ccOf,
            CcMatchesTruth = ccMatches,
            RootedOrder = rootedOrder,
            RootedSegments = rootedSegments,
            RootedCount = rootedCount,
            RootedSccOf = rootedSccOf,
            RootedAlignsWithCc = aligned,
            CcTime = ccTime,
            RootedTime = rootedTime,
            X = new float[n],
            Y = new float[n],
        };
        model.Layout();
        return model;
    }

    /// <summary>Width over height of the area the layout packs into; the panes are about this wide.</summary>
    private const double Aspect = 1.8;

    /// <summary>
    /// One box per component in CC order, packed in shelves, each with area proportional to its size.
    /// Inside a box, x is depth along edge direction and y spreads each depth's vertices.
    /// </summary>
    private void Layout() {
        var n = N;
        if (n == 0) { return; }
        var waveOf = Scc.WaveOf;
        var islandOf = Scc.IslandOf;

        // Per component: deepest wave, relative to its own shallowest one, and vertices per wave.
        var minWave = new int[CcCount];
        var maxWave = new int[CcCount];
        Array.Fill(minWave, int.MaxValue);
        for (var v = 0; v < n; v++) {
            var c = CcOf[v];
            var w = waveOf[islandOf[v]];
            if (w < minWave[c]) { minWave[c] = w; }
            if (w > maxWave[c]) { maxWave[c] = w; }
        }
        var waveBase = new int[CcCount + 1];
        for (var c = 0; c < CcCount; c++) { waveBase[c + 1] = waveBase[c] + (maxWave[c] - minWave[c] + 1); }
        var waveCount = new int[waveBase[CcCount]];
        var slot = new int[n];
        // Walk in rooted output order so each depth's vertices sit island by island.
        foreach (var v in RootedOrder.AsSpan(0, n)) {
            var c = CcOf[v];
            var w = waveBase[c] + waveOf[islandOf[v]] - minWave[c];
            slot[v] = waveCount[w]++;
        }

        // Shelf packing in a unit square: box side = sqrt(size) * unit; shrink unit until everything fits.
        var sides = new double[CcCount];
        for (var c = 0; c < CcCount; c++) { sides[c] = Math.Sqrt(CcSegments[c].Size); }
        double lo = 0, hi = 1.0 / Math.Max(1, sides.Max());
        for (var iter = 0; iter < 40; iter++) {
            var mid = (lo + hi) / 2;
            if (Fits(sides, mid)) { lo = mid; } else { hi = mid; }
        }
        var unit = lo;

        double x = 0, y = 0, shelf = 0;
        for (var c = 0; c < CcCount; c++) {
            var side = sides[c] * unit;
            if (x + side > Aspect + 1e-9) { x = 0; y += shelf; shelf = 0; }
            var depth = maxWave[c] - minWave[c] + 1;
            var (offset, size) = CcSegments[c];
            for (var j = offset; j < offset + size; j++) {
                var v = CcOrder[j];
                var w = waveBase[c] + waveOf[islandOf[v]] - minWave[c];
                var local = waveOf[islandOf[v]] - minWave[c];
                X[v] = (float)((x + side * (0.1 + 0.8 * (local + 0.5) / depth)) / Aspect);
                Y[v] = (float)(y + side * (0.1 + 0.8 * (slot[v] + 0.5) / waveCount[w]));
            }
            x += side;
            shelf = Math.Max(shelf, side);
        }
    }

    private bool Fits(double[] sides, double unit) {
        double x = 0, y = 0, shelf = 0;
        foreach (var s in sides) {
            var side = s * unit;
            if (x + side > Aspect + 1e-9) { x = 0; y += shelf; shelf = 0; }
            x += side;
            shelf = Math.Max(shelf, side);
        }
        return y + shelf <= 1 + 1e-9;
    }

    /// <summary>Result of Tarjan rooted at user-picked vertices.</summary>
    public sealed class RootQuery {
        public int[] Roots = [];
        public int[] Order = [];
        public Segment[] Segments = [];
        public int ComponentCount;
        public int Written;
        /// <summary>Per root, in root order: how many vertices it first reached. 0 means an earlier root already covered it.</summary>
        public int[] BandSize = [];
        /// <summary>Per vertex: index of the root that first reached it, or -1 if no root reaches it.</summary>
        public int[] RootOf = [];
        /// <summary>Per vertex: position in the rooted output, or -1.</summary>
        public int[] Position = [];
        public bool BandsMatchOutput;
        public TimeSpan Time;
    }

    public RootQuery QueryRoots(IReadOnlyList<int> roots) {
        var n = N;
        var q = new RootQuery {
            Roots = roots.ToArray(),
            Order = new int[n],
            Segments = new Segment[n],
            RootOf = new int[n],
            Position = new int[n],
            BandSize = new int[roots.Count],
        };
        if (_rootsCopy.Length < roots.Count) { _rootsCopy = new int[roots.Count]; }
        for (var i = 0; i < roots.Count; i++) { _rootsCopy[i] = roots[i]; }

        var sw = Stopwatch.StartNew();
        q.ComponentCount = _graph.Tarjan(_rootsCopy.AsSpan(0, roots.Count), q.Order, q.Segments);
        q.Time = sw.Elapsed;
        q.Written = 0;
        for (var s = 0; s < q.ComponentCount; s++) { q.Written += q.Segments[s].Size; }

        // Which root reached each vertex first: walk out from each root in turn over unclaimed vertices.
        Array.Fill(q.RootOf, -1);
        Array.Fill(q.Position, -1);
        var stack = new Stack<int>();
        for (var r = 0; r < roots.Count; r++) {
            var root = roots[r];
            if (q.RootOf[root] >= 0) { continue; }
            q.RootOf[root] = r;
            stack.Push(root);
            var count = 0;
            while (stack.Count > 0) {
                var v = stack.Pop();
                count++;
                foreach (var w in Scc.OutNeighbours(v)) {
                    if (q.RootOf[w] >= 0) { continue; }
                    q.RootOf[w] = r;
                    stack.Push(w);
                }
            }
            q.BandSize[r] = count;
        }

        // The library promises each root's first-reached vertices form one contiguous band, in root order.
        var ok = true;
        var p = 0;
        for (var r = 0; r < roots.Count && ok; r++) {
            for (var k = 0; k < q.BandSize[r]; k++, p++) {
                if (p >= q.Written || q.RootOf[q.Order[p]] != r) { ok = false; break; }
            }
        }
        q.BandsMatchOutput = ok && p == q.Written;
        for (var i = 0; i < q.Written; i++) { q.Position[q.Order[i]] = i; }
        return q;
    }

    /// <summary>
    /// Rebuilds the graph from the same edges in shuffled order with duplicates, reruns all three passes,
    /// and counts output positions that differ. The contract says every count is zero.
    /// </summary>
    public (int cc, int rooted, int query) CheckDeterminism(GeneratedClusters g, IReadOnlyList<int> roots, int seed) {
        var n = N;
        var rng = new Random(seed);
        var order = Enumerable.Range(0, g.From.Length).OrderBy(_ => rng.Next()).ToArray();
        var shuffled = new Graph().EnsureIndex(n - 1);
        foreach (var i in order) {
            shuffled.AddEdge(g.From[i], g.To[i]);
            if (rng.NextDouble() < 0.3) { shuffled.AddEdge(g.From[i], g.To[i]); }
        }

        var ccOrder = new int[n];
        var ccSegments = new Segment[n];
        var ccCount = shuffled.ConnectedComponents(ccOrder, ccSegments);
        var ccDiff = Diff(CcOrder, ccOrder, n) + (ccCount == CcCount ? 0 : 1);

        var rootedOrder = new int[n];
        var rootedSegments = new Segment[n];
        var rootedCount = shuffled.Tarjan(CcOrder, rootedOrder, rootedSegments);
        var rootedDiff = Diff(RootedOrder, rootedOrder, n) + (rootedCount == RootedCount ? 0 : 1);
        for (var s = 0; s < Math.Min(rootedCount, RootedCount); s++) {
            if (rootedSegments[s] != RootedSegments[s]) { rootedDiff++; }
        }

        var queryDiff = 0;
        if (roots.Count > 0) {
            var mine = QueryRoots(roots);
            var theirs = new int[n];
            var theirSegments = new Segment[n];
            var rootsArray = roots.ToArray();
            var count = shuffled.Tarjan(rootsArray, theirs, theirSegments);
            queryDiff = Diff(mine.Order, theirs, mine.Written) + (count == mine.ComponentCount ? 0 : 1);
        }
        return (ccDiff, rootedDiff, queryDiff);
    }

    private static int Diff(int[] a, int[] b, int n) {
        var d = 0;
        for (var i = 0; i < n; i++) { if (a[i] != b[i]) { d++; } }
        return d;
    }
}
