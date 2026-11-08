using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Graphs;

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
    private int _index;
    private int _dependencyOrderIndex;
    private int _componentIndex;

    /// <summary>
    /// Bumped whenever behaviour callers rely on changes (output order, buffer rules).
    /// Check it once at startup so a rebuilt library can't change behaviour silently.
    /// </summary>
    public static readonly int ContractVersion = 1;

    public int VertexCount => _maxIndex + 1;

    public Graph AddEdges(params Edge[] edges) {
        foreach (var edge in edges) { AddEdge(edge); }
        return this;
    }

    public Graph AddEdge(Edge edge) {
        _edges.Add(edge);
        _maxIndex = Math.Max(_maxIndex, Math.Max(edge.From, edge.To));
        return this;
    }

    public Graph AddEdge(int from, int to) => AddEdge((from, to));

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
    /// <remarks>Both spans need at least <see cref="VertexCount"/> elements.</remarks>
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
    /// <remarks>Null or undersized arrays are replaced.</remarks>
    public int Tarjan([NotNull] ref int[]? dependencyOrder, [NotNull] ref Segment[]? stronglyConnectedComponents) {
        _ensureLength(ref dependencyOrder);
        _ensureLength(ref stronglyConnectedComponents);
        return Tarjan(dependencyOrder.AsSpan(), stronglyConnectedComponents.AsSpan());
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
                } else if (w.onStack) {
                    v.lowlink = Math.Min(v.lowlink, w.index);
                }
                continue;
            }

            if (v.lowlink == v.index) {
                var start = _dependencyOrderIndex;
                int popped;
                do {
                    popped = _stack[--_stackIndex];
                    _vertices[popped].onStack = false;
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
        v.onStack = true;
        _stack[_stackIndex++] = vIndex;
    }
}

public struct Vertex {
    public int index;
    public int lowlink;
    public int nextEdge;
    public bool onStack;

    public static Vertex New() => new() { index = -1, lowlink = -1, onStack = false };
}
