using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Graphs;

/// <summary>
/// A directed graph of edges between zero-based vertices, which finds strongly connected components in dependency
/// order, and connected components. The graph holds only edges: vertices are the numbers from 0 up to the highest
/// one an edge or <see cref="EnsureIndex"/> has mentioned. Not thread-safe: the graph keeps its working buffers
/// and reuses them from one call to the next.
/// </summary>
/// <remarks>
/// Results are written into caller-provided spans, so the graph never allocates output and the caller
/// decides where results live (a pooled array, a slice of a larger buffer). The <c>ref</c> array
/// overloads are conveniences: they create or grow the arrays, then call the span version.
/// </remarks>
public sealed class Graph {
    private readonly SortedSet<Edge> _edges = new();
    private int _maxIndex = -1;

    // Edges in CSR form: _targets[_rowStart[v] .. _rowStart[v + 1]) are v's out-edges.
    private int[] _targets = [];
    private int[] _rowStart = [];

    private Vertex[] _vertices = [];
    private int[] _stack = [];
    private int _stackIndex;
    private int[] _path = [];
    private int[] _parent = [];
    private int _index;
    private int _dependencyOrderIndex;
    private int _componentIndex;

    /// <summary>
    /// Bumped whenever behaviour callers rely on changes (output order, buffer rules).
    /// Check it once at startup so a rebuilt library can't change behaviour silently.
    /// The order <see cref="Tarjan(Span{int}, Span{Segment})"/> writes in is part of it.
    /// </summary>
    public static readonly int ContractVersion = 1;

    /// <summary>
    /// The number of vertices: one more than the highest index any edge or <see cref="EnsureIndex"/> has mentioned,
    /// or 0.
    /// </summary>
    public int VertexCount => _maxIndex + 1;

    /// <summary>Adds each edge, as <see cref="AddEdge(Edge)"/> does. Returns the graph, for chaining.</summary>
    public Graph AddEdges(params Edge[] edges) {
        foreach (var edge in edges) { AddEdge(edge); }
        return this;
    }

    /// <summary>
    /// Adds a directed edge and makes sure both its vertices exist. Adding an edge that is already there changes nothing.
    /// Returns the graph, for chaining.
    /// </summary>
    public Graph AddEdge(Edge edge) {
        _edges.Add(edge);
        _maxIndex = Math.Max(_maxIndex, Math.Max(edge.From, edge.To));
        return this;
    }

    /// <summary>
    /// Adds the edge <paramref name="from"/> -> <paramref name="to"/>, as <see cref="AddEdge(Edge)"/> does.
    /// </summary>
    public Graph AddEdge(int from, int to) => AddEdge((from, to));

    /// <summary>
    /// Makes sure vertices 0 to <paramref name="indexToEnsure"/> exist, so a vertex without edges still gets a component of
    /// its own. Never lowers <see cref="VertexCount"/>. Returns the graph, for chaining.
    /// </summary>
    public Graph EnsureIndex(int indexToEnsure) {
        _maxIndex = Math.Max(_maxIndex, indexToEnsure);
        return this;
    }

    /// <summary>
    /// Removes every edge and vertex. Internal buffers are kept for reuse.
    /// </summary>
    public Graph Clear() {
        _edges.Clear();
        _maxIndex = -1;
        return this;
    }

    /// <summary>
    /// Writes every vertex to <paramref name="dependencyOrder"/>, grouped into strongly connected
    /// components. For every edge A -> B, B's component is written no later than A's.
    /// Output depends only on the vertex count and the set of edges (not insertion order or duplicates).
    /// Returns the number of components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both spans need at least <see cref="VertexCount"/> elements, or an <see cref="ArgumentException"/> is thrown
    /// before anything is written. <paramref name="stronglyConnectedComponents"/> gets one <see cref="Segment"/> per
    /// component, in the order the components are written, and they tile <paramref name="dependencyOrder"/> from 0 to
    /// <see cref="VertexCount"/>. Elements past what was written are left alone. A graph with no vertices has no
    /// components.
    /// </para>
    /// <para>
    /// The search starts from vertex 0, then each vertex not yet reached in ascending order, and follows out-edges in
    /// ascending order of their target. Vertices of one component come out in the reverse of the order the search
    /// first reached them. Components are written as the search finishes them, which settles the order of components
    /// that don't depend on each other. This order is the contract of <see cref="ContractVersion"/> 1: a change to
    /// it raises the version. The search is iterative, takes O(vertices + edges) time and allocates nothing per
    /// component; the working buffers are kept and reused.
    /// </para>
    /// </remarks>
    /// <param name="dependencyOrder">Receives every vertex once, one component after another.</param>
    /// <param name="stronglyConnectedComponents">Receives the slices of <paramref name="dependencyOrder"/> that hold each component.</param>
    public int Tarjan(Span<int> dependencyOrder, Span<Segment> stronglyConnectedComponents) {
        _requireLength(dependencyOrder.Length, nameof(dependencyOrder));
        _requireLength(stronglyConnectedComponents.Length, nameof(stronglyConnectedComponents));
        _prepare();

        for (var v = 0; v < VertexCount; v++) {
            if (_vertices[v].index == -1) { _strongConnect(v, dependencyOrder, stronglyConnectedComponents); }
        }

        return _componentIndex;
    }

    /// <inheritdoc cref="Tarjan(Span{int}, Span{Segment})"/>
    /// <remarks>
    /// A null array, or one with fewer than <see cref="VertexCount"/> elements, is replaced by a new array of exactly
    /// <see cref="VertexCount"/> elements, so neither is null afterwards; the old contents aren't copied, and in a new
    /// array the segments past the count are empty. An array that is long enough is kept and written in place, and may
    /// be longer than the output: use the returned count and the segments, not the array's length.
    /// </remarks>
    public int Tarjan([NotNull] ref int[]? dependencyOrder, [NotNull] ref Segment[]? stronglyConnectedComponents) {
        _ensureLength(ref dependencyOrder);
        _ensureLength(ref stronglyConnectedComponents);
        return Tarjan(dependencyOrder.AsSpan(), stronglyConnectedComponents.AsSpan());
    }

    /// <summary>
    /// Like <see cref="Tarjan(Span{int}, Span{Segment})"/>, but the search starts only from
    /// <paramref name="roots"/>, in the order given, and vertices no root can reach are skipped.
    /// Everything reachable from an earlier root is written before anything first reached from a later one.
    /// Output depends only on the vertex count, the set of edges and the roots.
    /// Returns the number of components; the vertices written are the ones those components cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both output spans need at least <see cref="VertexCount"/> elements, and <paramref name="roots"/>
    /// must not share memory with <paramref name="dependencyOrder"/> (it is read while output is written).
    /// The checks are made before anything is written: <see cref="ArgumentException"/> for a short span or for roots that
    /// overlap the output, and <see cref="ArgumentOutOfRangeException"/> for a root that is not a vertex.
    /// </para>
    /// <para>
    /// The components cover the start of <paramref name="dependencyOrder"/> and nothing past them is written. A root
    /// that is listed twice, or was reached from an earlier one, is skipped. The order of components is the one
    /// <see cref="Tarjan(Span{int}, Span{Segment})"/> promises, among the vertices written.
    /// </para>
    /// </remarks>
    /// <param name="roots">The vertices to search from, in order.</param>
    /// <param name="dependencyOrder">Receives each vertex the roots reach once, one component after another.</param>
    /// <param name="stronglyConnectedComponents">Receives the slices of <paramref name="dependencyOrder"/> that hold each component.</param>
    public int Tarjan(ReadOnlySpan<int> roots, Span<int> dependencyOrder, Span<Segment> stronglyConnectedComponents) {
        _requireLength(dependencyOrder.Length, nameof(dependencyOrder));
        _requireLength(stronglyConnectedComponents.Length, nameof(stronglyConnectedComponents));
        if (roots.Overlaps(dependencyOrder)) { throw new ArgumentException("Shares memory with dependencyOrder.", nameof(roots)); }
        foreach (var root in roots) {
            if ((uint)root >= (uint)VertexCount) { throw new ArgumentOutOfRangeException(nameof(roots), root, "Not a vertex."); }
        }
        _prepare();

        foreach (var root in roots) {
            if (_vertices[root].index == -1) { _strongConnect(root, dependencyOrder, stronglyConnectedComponents); }
        }

        return _componentIndex;
    }

    /// <inheritdoc cref="Tarjan(ReadOnlySpan{int}, Span{int}, Span{Segment})"/>
    /// <remarks>
    /// A null array, or one with fewer than <see cref="VertexCount"/> elements, is replaced by a new array of exactly
    /// <see cref="VertexCount"/> elements, so neither is null afterwards; in a new array the segments past the count
    /// are empty. An array that is long enough is kept and written in place. <paramref name="roots"/> must not share
    /// memory with <paramref name="dependencyOrder"/>.
    /// </remarks>
    public int Tarjan(
        ReadOnlySpan<int> roots, [NotNull] ref int[]? dependencyOrder, [NotNull] ref Segment[]? stronglyConnectedComponents) {
        _ensureLength(ref dependencyOrder);
        _ensureLength(ref stronglyConnectedComponents);
        return Tarjan(roots, dependencyOrder.AsSpan(), stronglyConnectedComponents.AsSpan());
    }

    /// <summary>
    /// Groups vertices joined by edges in either direction. Groups are ordered by their lowest vertex,
    /// and each group's vertices are written in ascending order.
    /// Output depends only on the vertex count and the set of edges. Returns the number of components.
    /// </summary>
    /// <remarks>
    /// Both spans need at least <see cref="VertexCount"/> elements, or an <see cref="ArgumentException"/> is thrown. A
    /// vertex with no edges is a component of its own. The working buffers are kept in the graph.
    /// </remarks>
    /// <param name="vertices">Receives every vertex once, one component after another.</param>
    /// <param name="components">Receives the slices of <paramref name="vertices"/> that hold each component.</param>
    public int ConnectedComponents(Span<int> vertices, Span<Segment> components) {
        _requireLength(vertices.Length, nameof(vertices));
        _requireLength(components.Length, nameof(components));
        var vertexCount = VertexCount;

        // Union-find in which every set's root is its lowest vertex, so parent[v] <= v always holds.
        if (_parent.Length < vertexCount) { _parent = new int[vertexCount]; }
        var parent = _parent.AsSpan(0, vertexCount);
        for (var v = 0; v < vertexCount; v++) { parent[v] = v; }
        foreach (var (from, to) in _edges) {
            var a = _findRoot(parent, from);
            var b = _findRoot(parent, to);
            if (a < b) { parent[b] = a; } else if (b < a) { parent[a] = b; }
        }

        // Parents point to lower vertices, so one ascending pass points every vertex at its root.
        // next[root] counts the root's vertices, then becomes its write position (reuses _stack).
        if (_stack.Length < vertexCount) { _stack = new int[vertexCount]; }
        var next = _stack.AsSpan(0, vertexCount);
        next.Clear();
        for (var v = 0; v < vertexCount; v++) {
            parent[v] = parent[parent[v]];
            next[parent[v]]++;
        }

        var count = 0;
        var offset = 0;
        for (var v = 0; v < vertexCount; v++) {
            if (parent[v] != v) { continue; }
            var size = next[v];
            components[count++] = new Segment(offset, size);
            next[v] = offset;
            offset += size;
        }
        for (var v = 0; v < vertexCount; v++) { vertices[next[parent[v]]++] = v; }

        return count;
    }

    /// <inheritdoc cref="ConnectedComponents(Span{int}, Span{Segment})"/>
    /// <remarks>
    /// A null array, or one with fewer than <see cref="VertexCount"/> elements, is replaced by a new array of exactly
    /// <see cref="VertexCount"/> elements, so neither is null afterwards; in a new array the segments past the count
    /// are empty. An array that is long enough is kept and written in place.
    /// </remarks>
    public int ConnectedComponents([NotNull] ref int[]? vertices, [NotNull] ref Segment[]? components) {
        _ensureLength(ref vertices);
        _ensureLength(ref components);
        return ConnectedComponents(vertices.AsSpan(), components.AsSpan());
    }

    private static int _findRoot(Span<int> parent, int v) {
        while (parent[v] != v) {
            parent[v] = parent[parent[v]];
            v = parent[v];
        }
        return v;
    }

    private void _requireLength(int length, string paramName) {
        if (length < VertexCount) { throw new ArgumentException($"Needs at least {VertexCount} elements.", paramName); }
    }

    private void _ensureLength<T>([NotNull] ref T[]? array) {
        if (array is null || array.Length < VertexCount) { array = new T[VertexCount]; }
    }

    private void _prepare() {
        var vertexCount = VertexCount;
        var edgeCount = _edges.Count;

        _dependencyOrderIndex = 0;
        _componentIndex = 0;

        if (_vertices.Length < vertexCount) { _vertices = new Vertex[vertexCount]; }
        _vertices.AsSpan(0, vertexCount).Fill(Vertex.New());

        if (_stack.Length < vertexCount) { _stack = new int[vertexCount]; }
        if (_path.Length < vertexCount) { _path = new int[vertexCount]; }
        _stackIndex = 0;
        _index = 0;

        if (_targets.Length < edgeCount) { _targets = new int[edgeCount]; }
        if (_rowStart.Length < vertexCount + 1) { _rowStart = new int[vertexCount + 1]; }
        _rowStart.AsSpan(0, vertexCount + 1).Clear();

        var i = 0;
        foreach (var (from, to) in _edges) {
            _targets[i++] = to;
            _rowStart[from + 1]++;
        }
        for (var v = 0; v < vertexCount; v++) { _rowStart[v + 1] += _rowStart[v]; }
    }

    // Iterative depth-first search. _path holds the vertices on the current DFS path;
    // each vertex keeps its own cursor into _targets, so no recursion is needed.
    // A vertex's index becomes Finished once its component is written, so it can never lower a
    // lowlink again. That stands in for the usual on-stack flag.
    private void _strongConnect(int root, Span<int> dependencyOrder, Span<Segment> components) {
        var depth = 0;
        _path[0] = root;
        _visit(root);

        while (depth >= 0) {
            var vIndex = _path[depth];
            ref var v = ref _vertices[vIndex];

            if (v.nextEdge < _rowStart[vIndex + 1]) {
                var wIndex = _targets[v.nextEdge++];
                ref var w = ref _vertices[wIndex];
                if (w.index == -1) {
                    _path[++depth] = wIndex;
                    _visit(wIndex);
                } else {
                    v.lowlink = Math.Min(v.lowlink, w.index);
                }
                continue;
            }

            if (v.lowlink == v.index) {
                var start = _dependencyOrderIndex;
                int popped;
                do {
                    popped = _stack[--_stackIndex];
                    _vertices[popped].index = Vertex.Finished;
                    dependencyOrder[_dependencyOrderIndex++] = popped;
                } while (popped != vIndex);

                components[_componentIndex++] = new Segment(start, _dependencyOrderIndex - start);
            }

            if (--depth >= 0) {
                ref var parent = ref _vertices[_path[depth]];
                parent.lowlink = Math.Min(parent.lowlink, v.lowlink);
            }
        }
    }

    private void _visit(int vIndex) {
        ref var v = ref _vertices[vIndex];
        v.index = v.lowlink = _index++;
        v.nextEdge = _rowStart[vIndex];
        _stack[_stackIndex++] = vIndex;
    }
}

internal struct Vertex {
    public const int Finished = int.MaxValue;

    public int index;
    public int lowlink;
    public int nextEdge;

    public static Vertex New() => new() { index = -1, lowlink = -1 };
}
