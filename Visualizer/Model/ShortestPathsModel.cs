using System.Diagnostics;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <param name="Rooms">Roughly how many rooms to generate.</param>
/// <param name="DoorFraction">Chance that two neighbouring rooms share a door.</param>
/// <param name="OneWayFraction">Chance that a door only opens one way.</param>
public sealed record PathsSpec(int Rooms, double DoorFraction, double OneWayFraction, int Seed);

/// <summary>
/// A floor plan of rooms joined by doors, solved for every pair of rooms with FloydWarshall. Room
/// r * Cols + c sits at row r, column c. An edge is a door walked through in that direction, weighted by
/// how long it takes. Also holds a step-by-step copy of the algorithm for the animation.
/// </summary>
public sealed class ShortestPathsModel {
    /// <summary>How much a fast door divides its walking time by.</summary>
    public const float FastDoorFactor = 6;

    public int Rows { get; private init; }
    public int Cols { get; private init; }
    public int N { get; private init; }
    public int EdgeCount { get; private init; }

    /// <summary>Input weights, n*n, PositiveInfinity for no door. Kept current as doors are made fast.</summary>
    public float[] Weights { get; private init; } = [];
    public float[] Distances { get; private init; } = [];
    /// <summary>Next[a * N + b] is the room to step into first when walking from a to b, or -1.</summary>
    public int[] Next { get; private init; } = [];
    public TimeSpan SolveTime { get; private init; }

    /// <summary>The same rooms and doors without weights, for Tarjan's order and the cross-checks.</summary>
    public GraphModel Graph { get; private init; } = null!;
    public int[] IdOrder { get; private init; } = [];
    /// <summary>Tarjan's dependency order: if a reaches b, b sits at or before a.</summary>
    public int[] TarjanOrder { get; private init; } = [];

    public long ReachablePairs { get; private init; }
    public bool ComponentsMatchMutualReach { get; private init; }
    public bool ConnectedMatchesSymmetricReach { get; private init; }
    public bool TarjanOrderIsTriangular { get; private init; }

    /// <summary>Doors made fast, as (lower room, higher room).</summary>
    public HashSet<(int, int)> FastDoors { get; } = [];

    // The animation's own copy of the textbook loop, with its own next-room table.
    public float[] Sweep { get; private set; } = [];
    public int[] SweepNext { get; private set; } = [];
    public int SweepPivot { get; private set; } = -1;
    public bool Sweeping => SweepPivot >= 0 && SweepPivot < N;
    public bool SweepDone => SweepPivot >= N;
    /// <summary>When the animation finishes: how many of its distances equal FloydWarshall.Solve on the same weights.</summary>
    public int SweepCellsMatchingLibrary { get; private set; }

    public bool HasDoor(int from, int to) => !float.IsPositiveInfinity(Weights[from * N + to]);

    public static ShortestPathsModel Build(PathsSpec spec, CancellationToken ct = default) {
        var rng = new Random(spec.Seed);
        var from = new List<int>();
        var to = new List<int>();
        var weight = new List<float>();

        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(spec.Rooms * 1.6)));
        var rows = Math.Max(1, (int)Math.Ceiling(spec.Rooms / (double)cols));
        var n = rows * cols;
        void Door(int a, int b) {
            if (rng.NextDouble() >= spec.DoorFraction) { return; }
            // Every door takes about as long, with a little variation so routes do not tie.
            var w = (float)(1 + 0.25 * rng.NextDouble());
            var oneWay = rng.NextDouble() < spec.OneWayFraction;
            var forward = rng.NextDouble() < 0.5;
            if (!oneWay || forward) { from.Add(a); to.Add(b); weight.Add(w); }
            if (!oneWay || !forward) { from.Add(b); to.Add(a); weight.Add(w); }
        }
        for (var r = 0; r < rows; r++) {
            for (var c = 0; c < cols; c++) {
                var v = r * cols + c;
                if (c + 1 < cols) { Door(v, v + 1); }
                if (r + 1 < rows) { Door(v, v + cols); }
            }
        }
        ct.ThrowIfCancellationRequested();

        var weights = new float[n * n];
        Array.Fill(weights, float.PositiveInfinity);
        for (var i = 0; i < from.Count; i++) { weights[from[i] * n + to[i]] = weight[i]; }

        var distances = (float[])weights.Clone();
        var next = new int[n * n];
        var sw = Stopwatch.StartNew();
        FloydWarshall.Solve(distances, next, n);
        var solveTime = sw.Elapsed;
        ct.ThrowIfCancellationRequested();

        var graph = GraphModel.Build(n, from.ToArray(), to.ToArray(), ct: ct);
        var tarjanOrder = graph.DependencyOrder.Take(n).ToArray();

        // Cross-checks, taking reachable as "distance is finite".
        var sccMatches = true;
        long reachable = 0;
        for (var a = 0; a < n && sccMatches; a++) {
            for (var b = 0; b < n; b++) {
                var ab = !float.IsPositiveInfinity(distances[a * n + b]);
                if (ab) { reachable++; }
                var ba = !float.IsPositiveInfinity(distances[b * n + a]);
                if ((graph.IslandOf[a] == graph.IslandOf[b]) != (ab && ba)) { sccMatches = false; break; }
            }
        }

        var positionOf = new int[n];
        for (var p = 0; p < n; p++) { positionOf[tarjanOrder[p]] = p; }
        var triangular = true;
        for (var a = 0; a < n && triangular; a++) {
            for (var b = 0; b < n; b++) {
                if (!float.IsPositiveInfinity(distances[a * n + b]) && graph.IslandOf[a] != graph.IslandOf[b] && positionOf[b] > positionOf[a]) {
                    triangular = false;
                    break;
                }
            }
        }

        var symmetric = new float[n * n];
        for (var a = 0; a < n; a++) {
            for (var b = 0; b < n; b++) { symmetric[a * n + b] = Math.Min(weights[a * n + b], weights[b * n + a]); }
        }
        FloydWarshall.Solve(symmetric, Span<int>.Empty, n);
        var plain = new Graph().EnsureIndex(n - 1);
        for (var i = 0; i < from.Count; i++) { plain.AddEdge(from[i], to[i]); }
        int[]? ccOrder = null;
        Segment[]? ccSegments = null;
        var ccCount = plain.ConnectedComponents(ref ccOrder, ref ccSegments);
        var ccOf = new int[n];
        for (var c = 0; c < ccCount; c++) {
            foreach (var v in ccOrder.AsSpan(ccSegments[c].Offset, ccSegments[c].Size)) { ccOf[v] = c; }
        }
        var ccMatches = true;
        for (var a = 0; a < n && ccMatches; a++) {
            for (var b = 0; b < n; b++) {
                if ((ccOf[a] == ccOf[b]) != !float.IsPositiveInfinity(symmetric[a * n + b])) { ccMatches = false; break; }
            }
        }

        return new ShortestPathsModel {
            Rows = rows,
            Cols = cols,
            N = n,
            EdgeCount = from.Count,
            Weights = weights,
            Distances = distances,
            Next = next,
            SolveTime = solveTime,
            Graph = graph,
            IdOrder = Enumerable.Range(0, n).ToArray(),
            TarjanOrder = tarjanOrder,
            ReachablePairs = reachable,
            ComponentsMatchMutualReach = sccMatches,
            ConnectedMatchesSymmetricReach = ccMatches,
            TarjanOrderIsTriangular = triangular,
        };
    }

    /// <summary>Diagonal blocks for Tarjan's order: one per strongly connected component.</summary>
    public IReadOnlyList<(int start, int size)> TarjanBlocks() =>
        Graph.Islands.Take(Graph.IslandCount).Select(s => (s.Offset, s.Size)).ToArray();

    public void StartSweep() {
        var n = N;
        Sweep = (float[])Weights.Clone();
        SweepNext = new int[n * n];
        for (var i = 0; i < n; i++) {
            for (var j = 0; j < n; j++) {
                SweepNext[i * n + j] = i == j ? i : float.IsPositiveInfinity(Sweep[i * n + j]) ? -1 : j;
            }
            Sweep[i * n + i] = 0;
        }
        SweepPivot = 0;
        SweepCellsMatchingLibrary = 0;
    }

    /// <summary>
    /// One step of the textbook loop, written the same way as the library's: every pair may now stop over
    /// in room SweepPivot.
    /// </summary>
    public void StepSweep() {
        var n = N;
        if (!Sweeping) { return; }
        var k = SweepPivot;
        for (var i = 0; i < n; i++) {
            var throughK = Sweep[i * n + k];
            if (float.IsPositiveInfinity(throughK)) { continue; }
            for (var j = 0; j < n; j++) {
                var length = throughK + Sweep[k * n + j];
                if (length < Sweep[i * n + j]) {
                    Sweep[i * n + j] = length;
                    SweepNext[i * n + j] = SweepNext[i * n + k];
                }
            }
        }
        SweepPivot++;
        if (SweepDone) {
            // Compare with a fresh solve: after fast doors, Distances come from Relax, which may differ by rounding.
            var solved = (float[])Weights.Clone();
            FloydWarshall.Solve(solved, Span<int>.Empty, n);
            var same = 0;
            for (var c = 0; c < n * n; c++) {
                if (Sweep[c] == solved[c] || (float.IsPositiveInfinity(Sweep[c]) && float.IsPositiveInfinity(solved[c]))) { same++; }
            }
            SweepCellsMatchingLibrary = same;
        }
    }

    public void FinishSweep() {
        while (Sweeping) { StepSweep(); }
    }

    public sealed record FastDoorResult(int A, int B, int[] Changed, double WorstDifference, bool MatchesResolve, TimeSpan Time);

    /// <summary>
    /// Makes the door between neighbouring rooms a and b fast, in each direction it opens, with
    /// FloydWarshall.Relax, then checks the result against a full re-solve. Returns null if there is no door
    /// or it is already fast. Lowering never changes which pairs are reachable, so the Tarjan-based checks
    /// stay true. Distances may differ from the re-solve by float rounding, so the check allows 1e-5 relative.
    /// </summary>
    public FastDoorResult? MakeDoorFast(int a, int b) {
        var n = N;
        var key = (Math.Min(a, b), Math.Max(a, b));
        if (FastDoors.Contains(key) || (!HasDoor(a, b) && !HasDoor(b, a))) { return null; }
        FinishSweep();

        var before = (float[])Distances.Clone();
        var sw = Stopwatch.StartNew();
        foreach (var (x, y) in new[] { (a, b), (b, a) }) {
            if (!HasDoor(x, y)) { continue; }
            var w = Weights[x * n + y] / FastDoorFactor;
            FloydWarshall.Relax(Distances, Next, n, x, y, w);
            Weights[x * n + y] = w;
        }
        var time = sw.Elapsed;
        FastDoors.Add(key);

        var changed = new List<int>();
        for (var c = 0; c < n * n; c++) {
            if (Distances[c] != before[c]) { changed.Add(c); }
        }

        var fresh = (float[])Weights.Clone();
        FloydWarshall.Solve(fresh, Span<int>.Empty, n);
        var worst = 0.0;
        var ok = true;
        for (var c = 0; c < n * n; c++) {
            if (float.IsPositiveInfinity(fresh[c]) != float.IsPositiveInfinity(Distances[c])) { ok = false; continue; }
            if (float.IsPositiveInfinity(fresh[c])) { continue; }
            var rel = Math.Abs(fresh[c] - Distances[c]) / Math.Max(1e-9, Math.Abs(fresh[c]));
            worst = Math.Max(worst, rel);
            if (rel > 1e-5) { ok = false; }
        }

        return new FastDoorResult(a, b, changed.ToArray(), worst, ok, time);
    }

    /// <summary>The route from one room to another, from the library's answer.</summary>
    public int[] PathBetween(int from, int to) {
        var buffer = new int[N];
        var count = FloydWarshall.Path(Next, N, from, to, buffer);
        return count < 1 ? [] : buffer.AsSpan(0, count).ToArray();
    }

    /// <summary>The best route known so far by the animation, following its own next-room table.</summary>
    public int[] SweepPathBetween(int from, int to) {
        var n = N;
        if (SweepNext[from * n + to] < 0) { return []; }
        var path = new List<int> { from };
        var v = from;
        while (v != to && path.Count <= n) {
            v = SweepNext[v * n + to];
            if (v < 0) { return []; }
            path.Add(v);
        }
        return path.ToArray();
    }
}
