namespace Yewnyx.Algorithms.Visualizer.Model;

public enum GraphShape { Wide, Deep, Custom }

/// <param name="NodeCount">Vertices 0..NodeCount-1.</param>
/// <param name="IslandSize">Mean size of a generated island (strongly connected component).</param>
/// <param name="SoloFraction">Share of islands that are a single vertex with no cycle.</param>
/// <param name="Layers">Depth of the island DAG. Many layers make a deep graph, few make a wide one.</param>
/// <param name="LinksPerIsland">Mean number of edges from an island to islands in later layers.</param>
/// <param name="ChordsPerNode">Extra edges inside an island on top of the ring that makes it strongly connected.</param>
/// <param name="Locality">Share of links that land near the same position one or two layers down.</param>
public sealed record GraphSpec(
    int NodeCount, double IslandSize, double SoloFraction, int Layers,
    double LinksPerIsland, double ChordsPerNode, double Locality, int Seed) {

    public int EstimatedIslands => Math.Max(1, (int)(NodeCount / Math.Max(1.0, IslandSize * (1 - SoloFraction) + SoloFraction)));

    public static int LayersFor(GraphShape shape, int estimatedIslands, int custom) => shape switch {
        GraphShape.Deep => Math.Max(1, estimatedIslands / 4),
        GraphShape.Wide => Math.Min(estimatedIslands, 6),
        _ => Math.Max(1, custom),
    };
}

/// <param name="TruthIsland">Generated island of each vertex, for checking Tarjan's answer.</param>
public sealed record GeneratedGraph(int[] From, int[] To, int[] TruthIsland, int TruthIslandCount);

public static class GraphGenerator {
    /// <summary>
    /// Builds islands explicitly: each is a ring through its vertices plus random chords, so it is
    /// strongly connected. Islands are spread over layers, and links only run from a layer to a later
    /// one, so the islands are exactly the strongly connected components. Vertex ids are shuffled so
    /// the input order carries no information.
    /// </summary>
    public static GeneratedGraph Generate(GraphSpec spec, CancellationToken ct = default) {
        var n = spec.NodeCount;
        var rng = new Random(spec.Seed);

        var perm = new int[n];
        for (var i = 0; i < n; i++) { perm[i] = i; }
        for (var i = n - 1; i > 0; i--) {
            var j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        // Partition ranks into islands of size 1 (solo) or 2..2*mean-2.
        var islandStart = new List<int>(n / 2 + 1);
        var maxSize = Math.Max(2, (int)Math.Round(spec.IslandSize * 2) - 2);
        for (var r = 0; r < n;) {
            islandStart.Add(r);
            var size = rng.NextDouble() < spec.SoloFraction || maxSize < 2 ? 1 : 2 + rng.Next(maxSize - 1);
            r += Math.Min(size, n - r);
        }
        var k = islandStart.Count;
        islandStart.Add(n);

        var truth = new int[n];
        for (var i = 0; i < k; i++) {
            for (var r = islandStart[i]; r < islandStart[i + 1]; r++) { truth[perm[r]] = i; }
        }

        var layers = Math.Clamp(spec.Layers, 1, Math.Max(1, k));
        int LayerOf(int island) => (int)((long)island * layers / k);
        int LayerStart(int layer) => (int)(((long)layer * k + layers - 1) / layers);

        var estimate = (long)(n * (1 + spec.ChordsPerNode) + k * spec.LinksPerIsland);
        var seen = new HashSet<long>((int)Math.Min(estimate, int.MaxValue / 4));
        var from = new List<int>((int)Math.Min(estimate, int.MaxValue / 4));
        var to = new List<int>(from.Capacity);

        void Add(int ra, int rb) {
            var a = perm[ra];
            var b = perm[rb];
            if (a == b || !seen.Add((long)a * n + b)) { return; }
            from.Add(a);
            to.Add(b);
        }

        for (var i = 0; i < k; i++) {
            if ((i & 0xFFF) == 0) { ct.ThrowIfCancellationRequested(); }
            var start = islandStart[i];
            var size = islandStart[i + 1] - start;

            // Ring plus chords.
            if (size > 1) {
                for (var j = 0; j < size; j++) { Add(start + j, start + (j + 1) % size); }
                var chords = Stochastic(rng, size * spec.ChordsPerNode);
                for (var c = 0; c < chords; c++) { Add(start + rng.Next(size), start + rng.Next(size)); }
            }

            // Links to later layers.
            var layer = LayerOf(i);
            if (layer + 1 >= layers) { continue; }
            var layerStart = LayerStart(layer);
            var layerWidth = Math.Max(1, LayerStart(layer + 1) - layerStart);
            var links = Stochastic(rng, spec.LinksPerIsland);
            for (var l = 0; l < links; l++) {
                int target;
                if (rng.NextDouble() < spec.Locality) {
                    var next = Math.Min(layers - 1, layer + 1 + (rng.NextDouble() < 0.25 ? 1 : 0));
                    var nextStart = LayerStart(next);
                    var nextWidth = LayerStart(next + 1) - nextStart;
                    var window = Math.Max(1, nextWidth / 10);
                    var position = (int)((long)(i - layerStart) * nextWidth / layerWidth) + rng.Next(-window, window + 1);
                    target = nextStart + Math.Clamp(position, 0, nextWidth - 1);
                } else {
                    var after = LayerStart(layer + 1);
                    target = after + rng.Next(k - after);
                }
                var targetStart = islandStart[target];
                var targetSize = islandStart[target + 1] - targetStart;
                Add(start + rng.Next(size), targetStart + rng.Next(targetSize));
            }
        }

        return new GeneratedGraph(from.ToArray(), to.ToArray(), truth, k);
    }

    private static int Stochastic(Random rng, double mean) {
        var whole = (int)mean;
        return whole + (rng.NextDouble() < mean - whole ? 1 : 0);
    }
}
