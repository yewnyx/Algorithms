namespace Yewnyx.Algorithms.Visualizer.Model;

/// <param name="Clusters">Groups of vertices joined by edges; nothing links two clusters.</param>
/// <param name="LoneFraction">Share of clusters that are a single vertex with no edges.</param>
/// <param name="IslandsPerCluster">Mean number of strongly connected islands in a cluster.</param>
/// <param name="IslandSize">Mean vertices per island. Size 1 islands have no cycle.</param>
/// <param name="ExtraLinks">Extra forward links per island, on top of the ones that keep a cluster connected.</param>
public sealed record ClusterSpec(int Clusters, double LoneFraction, double IslandsPerCluster, double IslandSize, double ExtraLinks, int Seed);

/// <param name="TruthCluster">Generated cluster of each vertex: the expected connected component.</param>
/// <param name="TruthIsland">Generated island of each vertex: the expected strongly connected component.</param>
public sealed record GeneratedClusters(int VertexCount, int[] From, int[] To, int[] TruthCluster, int ClusterCount, int[] TruthIsland, int IslandCount);

public static class ClusterGenerator {
    /// <summary>
    /// Builds clusters of islands. Each island is a ring plus a chord, so it is strongly connected. Islands
    /// inside a cluster are linked only forward, from earlier to later islands, so no cycle spans two
    /// islands. Every island after the first gets a link from an earlier one, which keeps the cluster
    /// connected when edge direction is ignored. Vertex ids are shuffled.
    /// </summary>
    public static GeneratedClusters Generate(ClusterSpec spec, CancellationToken ct = default) {
        var rng = new Random(spec.Seed);
        var islandSizes = new List<int>();
        var clusterIslandCount = new int[spec.Clusters];
        for (var c = 0; c < spec.Clusters; c++) {
            if (rng.NextDouble() < spec.LoneFraction) {
                clusterIslandCount[c] = 1;
                islandSizes.Add(1);
                continue;
            }
            var islands = Math.Max(1, Around(rng, spec.IslandsPerCluster));
            clusterIslandCount[c] = islands;
            for (var i = 0; i < islands; i++) { islandSizes.Add(Math.Max(1, Around(rng, spec.IslandSize))); }
        }
        var n = 0L;
        foreach (var s in islandSizes) { n += s; }
        if (n > 2_000_000) { throw new ArgumentException($"{n:N0} vertices is too many; lower the sizes."); }

        var vertexCount = (int)n;
        var perm = new int[vertexCount];
        for (var i = 0; i < vertexCount; i++) { perm[i] = i; }
        for (var i = vertexCount - 1; i > 0; i--) {
            var j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        var truthCluster = new int[vertexCount];
        var truthIsland = new int[vertexCount];
        var from = new List<int>(vertexCount * 2);
        var to = new List<int>(vertexCount * 2);
        void Edge(int a, int b) {
            from.Add(perm[a]);
            to.Add(perm[b]);
        }

        var next = 0;
        var island = 0;
        var islandStart = new List<int>();
        for (var c = 0; c < spec.Clusters; c++) {
            if ((c & 0xFFF) == 0) { ct.ThrowIfCancellationRequested(); }
            islandStart.Clear();
            for (var i = 0; i < clusterIslandCount[c]; i++) {
                var size = islandSizes[island];
                var start = next;
                next += size;
                islandStart.Add(start);
                for (var v = start; v < start + size; v++) {
                    truthCluster[perm[v]] = c;
                    truthIsland[perm[v]] = island;
                }
                if (size > 1) {
                    for (var k = 0; k < size; k++) { Edge(start + k, start + (k + 1) % size); }
                    if (size > 3) { Edge(start + rng.Next(size), start + rng.Next(size)); }
                }
                island++;
            }
            islandStart.Add(next);

            // Forward links between islands of this cluster only.
            var count = clusterIslandCount[c];
            for (var i = 1; i < count; i++) {
                var earlier = rng.Next(i);
                Edge(Pick(rng, islandStart, earlier), Pick(rng, islandStart, i));
                var extra = Around(rng, spec.ExtraLinks);
                for (var e = 0; e < extra; e++) {
                    var a = rng.Next(i);
                    Edge(Pick(rng, islandStart, a), Pick(rng, islandStart, i));
                }
            }
        }

        return new GeneratedClusters(vertexCount, from.ToArray(), to.ToArray(), truthCluster, spec.Clusters, truthIsland, island);
    }

    private static int Pick(Random rng, List<int> starts, int island) =>
        starts[island] + rng.Next(starts[island + 1] - starts[island]);

    /// <summary>A whole number that averages to <paramref name="mean"/>.</summary>
    private static int Around(Random rng, double mean) {
        if (mean <= 0) { return 0; }
        var spread = rng.NextDouble() * 2 * mean;
        var whole = (int)spread;
        return whole + (rng.NextDouble() < spread - whole ? 1 : 0);
    }
}
