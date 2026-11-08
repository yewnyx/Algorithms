using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Graphs;

public sealed class Graph {
    private readonly SortedSet<Edge> _edges = new();
    private int _maxIndex = -1;

    // Edges in CSR form: _targets[_rowStart[v] .. _rowStart[v + 1]) are v's out-edges.
    private int[] _targets = [];
    private int[] _rowStart = [];

    private Vertex[] _vertices = [];
    private int[] _stack = [];
    private int _stackIndex;
    private int _index;

    private int[] _dependencyOrder = [];
    private int _dependencyOrderIndex;
    private Segment[] _components = [];
    private int _componentIndex;

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
    /// Writes every vertex to <paramref name="dependencyOrder"/>, grouped into strongly connected
    /// components. For every edge A -> B, B's component is written no later than A's.
    /// Null or undersized buffers are replaced. Returns the number of components.
    /// </summary>
    public int Tarjan([NotNull] ref int[]? dependencyOrder, [NotNull] ref Segment[]? stronglyConnectedComponents) {
        _prepare(ref dependencyOrder, ref stronglyConnectedComponents);

        for (var v = 0; v < _vertices.Length; v++) {
            if (_vertices[v].index == -1) { _strongConnect(v); }
        }

        return _componentIndex;
    }

    private void _prepare([NotNull] ref int[]? dependencyOrder, [NotNull] ref Segment[]? stronglyConnectedComponents) {
        var vertexCount = VertexCount;
        var edgeCount = _edges.Count;

        if (dependencyOrder is null || dependencyOrder.Length < vertexCount) { dependencyOrder = new int[vertexCount]; }
        if (stronglyConnectedComponents is null || stronglyConnectedComponents.Length < vertexCount) {
            stronglyConnectedComponents = new Segment[vertexCount];
        }
        _dependencyOrder = dependencyOrder;
        _dependencyOrderIndex = 0;
        _components = stronglyConnectedComponents;
        _componentIndex = 0;

        if (_vertices.Length != vertexCount) { _vertices = new Vertex[vertexCount]; }
        Array.Fill(_vertices, Vertex.New());

        if (_stack.Length < vertexCount) { _stack = new int[vertexCount]; }
        _stackIndex = 0;
        _index = 0;

        if (_targets.Length != edgeCount) { _targets = new int[edgeCount]; }
        if (_rowStart.Length != vertexCount + 1) { _rowStart = new int[vertexCount + 1]; }
        _rowStart.AsSpan().Clear();

        var i = 0;
        foreach (var (from, to) in _edges) {
            _targets[i++] = to;
            _rowStart[from + 1]++;
        }
        for (var v = 0; v < vertexCount; v++) { _rowStart[v + 1] += _rowStart[v]; }
    }

    private void _strongConnect(int vIndex) {
        ref var v = ref _vertices[vIndex];
        v.index = v.lowlink = _index++;
        v.onStack = true;
        _stack[_stackIndex++] = vIndex;

        for (var i = _rowStart[vIndex]; i < _rowStart[vIndex + 1]; i++) {
            var wIndex = _targets[i];
            ref var w = ref _vertices[wIndex];
            if (w.index == -1) {
                _strongConnect(wIndex);
                v.lowlink = Math.Min(v.lowlink, w.lowlink);
            } else if (w.onStack) {
                v.lowlink = Math.Min(v.lowlink, w.index);
            }
        }

        if (v.lowlink != v.index) { return; }

        var start = _dependencyOrderIndex;
        int popped;
        do {
            popped = _stack[--_stackIndex];
            _vertices[popped].onStack = false;
            _dependencyOrder[_dependencyOrderIndex++] = popped;
        } while (popped != vIndex);

        _components[_componentIndex++] = new Segment(start, _dependencyOrderIndex - start);
    }
}

public struct Vertex {
    public int index;
    public int lowlink;
    public bool onStack;

    public static Vertex New() => new() { index = -1, lowlink = -1, onStack = false };
}
