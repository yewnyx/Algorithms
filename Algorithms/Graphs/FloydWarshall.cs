using System;

namespace Yewnyx.Algorithms.Graphs;

/// <summary>
/// All-pairs shortest paths over an n*n weight matrix, computed in place.
/// </summary>
/// <remarks>
/// Suited to small graphs: O(n^3) time, n*n cells per matrix. Matrices are indexed [from * n + to] and
/// owned by the caller, so nothing is allocated (there are no growing ref-array overloads: the caller
/// sizes everything from n). Results depend only on the inputs.
/// Weights must be 0 or more: a loop with negative total cost would leave some pairs with no shortest path.
/// </remarks>
public static class FloydWarshall {
    /// <summary>All-pairs shortest paths, in place. O(n^3) time.</summary>
    /// <param name="distances">
    /// n*n, indexed [from * n + to].
    /// In: edge weights (0 or more, PositiveInfinity for no edge). The diagonal is set to 0.
    /// Out: shortest path lengths.
    /// </param>
    /// <param name="next">
    /// n*n (empty to skip).
    /// Out: first vertex after <c>from</c> on the path to <c>to</c> (-1 if unreachable).
    /// </param>
    /// <param name="n">Vertex count.</param>
    public static void Solve(Span<float> distances, Span<int> next, int n) {
        _requireMatrices(distances.Length, next.Length, n, allowEmptyNext: true);
        var hasNext = !next.IsEmpty;

        for (var i = 0; i < n; i++) {
            for (var j = 0; j < n; j++) {
                var cell = i * n + j;
                if (i == j) { distances[cell] = 0; }
                var weight = distances[cell];
                if (!(weight >= 0f)) { throw new ArgumentOutOfRangeException(nameof(distances), weight, "Weights must be 0 or more."); }
                if (hasNext) { next[cell] = float.IsPositiveInfinity(weight) ? -1 : j; }
            }
        }

        for (var k = 0; k < n; k++) {
            var rowK = distances.Slice(k * n, n);
            for (var i = 0; i < n; i++) {
                var throughK = distances[i * n + k];
                if (float.IsPositiveInfinity(throughK)) { continue; }
                var rowI = distances.Slice(i * n, n);
                var hop = hasNext ? next[i * n + k] : 0;
                for (var j = 0; j < n; j++) {
                    var length = throughK + rowK[j];
                    if (length < rowI[j]) {
                        rowI[j] = length;
                        if (hasNext) { next[i * n + j] = hop; }
                    }
                }
            }
        }
    }

    /// <inheritdoc cref="Solve(Span{float}, Span{int}, int)"/>
    /// <param name="distances">
    /// n*n, indexed [from * n + to].
    /// In: edge weights (0 or more, int.MaxValue for no edge). The diagonal is set to 0.
    /// Out: shortest path lengths. A path whose length would reach int.MaxValue reads as no path.
    /// </param>
    public static void Solve(Span<int> distances, Span<int> next, int n) {
        _requireMatrices(distances.Length, next.Length, n, allowEmptyNext: true);
        var hasNext = !next.IsEmpty;

        for (var i = 0; i < n; i++) {
            for (var j = 0; j < n; j++) {
                var cell = i * n + j;
                if (i == j) { distances[cell] = 0; }
                var weight = distances[cell];
                if (weight < 0) { throw new ArgumentOutOfRangeException(nameof(distances), weight, "Weights must be 0 or more."); }
                if (hasNext) { next[cell] = weight == int.MaxValue ? -1 : j; }
            }
        }

        for (var k = 0; k < n; k++) {
            var rowK = distances.Slice(k * n, n);
            for (var i = 0; i < n; i++) {
                var throughK = distances[i * n + k];
                if (throughK == int.MaxValue) { continue; }
                var rowI = distances.Slice(i * n, n);
                var hop = hasNext ? next[i * n + k] : 0;
                for (var j = 0; j < n; j++) {
                    // Summed as long: anything at or past int.MaxValue can never beat rowI[j].
                    var length = (long)throughK + rowK[j];
                    if (length < rowI[j]) {
                        rowI[j] = (int)length;
                        if (hasNext) { next[i * n + j] = hop; }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Updates a solved matrix after the edge <paramref name="from"/> -> <paramref name="to"/> is added
    /// or lowered to <paramref name="weight"/>. O(n^2) time. Does nothing if the edge isn't shorter.
    /// </summary>
    /// <remarks>
    /// Raising a weight or removing an edge needs a full Solve. Distances match a fresh Solve up to
    /// float rounding (sums are added in a different order); <paramref name="next"/> may pick a
    /// different path of the same length.
    /// </remarks>
    public static void Relax(Span<float> distances, Span<int> next, int n, int from, int to, float weight) {
        _requireMatrices(distances.Length, next.Length, n, allowEmptyNext: true);
        _requireVertex(from, n, nameof(from));
        _requireVertex(to, n, nameof(to));
        if (!(weight >= 0f)) { throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weights must be 0 or more."); }
        if (!(weight < distances[from * n + to])) { return; }
        var hasNext = !next.IsEmpty;

        // Reading d[i, from] and d[to, j] while writing d[i, j] is safe only because weights are
        // non-negative: nothing written in this pass can lower either of them.
        var rowTo = distances.Slice(to * n, n);
        for (var i = 0; i < n; i++) {
            var toFrom = distances[i * n + from];
            if (float.IsPositiveInfinity(toFrom)) { continue; }
            var start = toFrom + weight;
            var rowI = distances.Slice(i * n, n);
            var hop = i == from ? to : hasNext ? next[i * n + from] : 0;
            for (var j = 0; j < n; j++) {
                var length = start + rowTo[j];
                if (length < rowI[j]) {
                    rowI[j] = length;
                    if (hasNext) { next[i * n + j] = hop; }
                }
            }
        }
    }

    /// <inheritdoc cref="Relax(Span{float}, Span{int}, int, int, int, float)"/>
    /// <remarks>
    /// Raising a weight or removing an edge needs a full Solve. Distances match a fresh Solve exactly;
    /// <paramref name="next"/> may pick a different path of the same length.
    /// </remarks>
    public static void Relax(Span<int> distances, Span<int> next, int n, int from, int to, int weight) {
        _requireMatrices(distances.Length, next.Length, n, allowEmptyNext: true);
        _requireVertex(from, n, nameof(from));
        _requireVertex(to, n, nameof(to));
        if (weight < 0) { throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weights must be 0 or more."); }
        if (weight >= distances[from * n + to]) { return; }
        var hasNext = !next.IsEmpty;

        // Same reasoning as the float overload: non-negative weights keep d[i, from] and d[to, j] fixed.
        var rowTo = distances.Slice(to * n, n);
        for (var i = 0; i < n; i++) {
            var toFrom = distances[i * n + from];
            if (toFrom == int.MaxValue) { continue; }
            var start = (long)toFrom + weight;
            var rowI = distances.Slice(i * n, n);
            var hop = i == from ? to : hasNext ? next[i * n + from] : 0;
            for (var j = 0; j < n; j++) {
                var length = start + rowTo[j];
                if (length < rowI[j]) {
                    rowI[j] = (int)length;
                    if (hasNext) { next[i * n + j] = hop; }
                }
            }
        }
    }

    /// <summary>
    /// Writes the vertices on the shortest path from <paramref name="from"/> to <paramref name="to"/>,
    /// both included, using the <paramref name="next"/> matrix from Solve or Relax.
    /// </summary>
    /// <returns>
    /// The vertex count (1 when from == to, -1 if unreachable). A count above path.Length means path is
    /// too short and nothing was written.
    /// </returns>
    public static int Path(ReadOnlySpan<int> next, int n, int from, int to, Span<int> path) {
        _requireMatrices(next.Length, next.Length, n, allowEmptyNext: false);
        _requireVertex(from, n, nameof(from));
        _requireVertex(to, n, nameof(to));
        if (next[from * n + to] == -1) { return -1; }

        // Counted first, capped at n, so a damaged matrix can't loop forever or write partial paths.
        var count = 1;
        for (var v = from; v != to; count++) {
            v = next[v * n + to];
            if ((uint)v >= (uint)n || count >= n) { return -1; }
        }
        if (count > path.Length) { return count; }

        path[0] = from;
        for (int v = from, i = 1; v != to; i++) {
            v = next[v * n + to];
            path[i] = v;
        }
        return count;
    }

    private static void _requireMatrices(int distancesLength, int nextLength, int n, bool allowEmptyNext) {
        if (n < 0) { throw new ArgumentOutOfRangeException(nameof(n), n, "Must be 0 or more."); }
        var cells = (long)n * n;
        if (distancesLength < cells) { throw new ArgumentException($"Needs at least n*n = {cells} elements.", "distances"); }
        if (!(allowEmptyNext && nextLength == 0) && nextLength < cells) {
            throw new ArgumentException($"Needs at least n*n = {cells} elements (or empty to skip).", "next");
        }
    }

    private static void _requireVertex(int vertex, int n, string paramName) {
        if ((uint)vertex >= (uint)n) { throw new ArgumentOutOfRangeException(paramName, vertex, "Not a vertex."); }
    }
}
