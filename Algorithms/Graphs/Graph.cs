using System;
using System.Collections.Generic;
using System.Linq;

namespace Yewnyx.Algorithms.Graphs;

public sealed class Graph {
    private Vertex[] Vertices;
    private int _maxIndex = -1;

    private SortedSet<Edge> _edges = new();
    private Edge[] _edgesArray;
    private int[] _edgeSkipList;

    private int[] _stack;
    private int _stackIndex;

    private int _dependencyOrderIndex;
    private int _sccIndex;

    public Graph AddEdges(params Edge[] edges) {
        _edges.UnionWith(edges);
        _maxIndex = Math.Max(_maxIndex, edges.Max(e => Math.Max(e.From, e.To)));
        return this;
    }

    public Graph AddEdge(Edge edge) {
        _edges.Add(edge);
        _maxIndex = Math.Max(_maxIndex, Math.Max(edge.From, edge.To));
        return this;
    }

    
    public Graph AddEdge(int from, int to) {
        _edges.Add((from, to));
        _maxIndex = Math.Max(_maxIndex, Math.Max(from, to));
        return this;
    }
    
    private void _prepare(ref int[] dependencyOrder, ref Segment[] stronglyConnectedComponents) {
        var vLength = _maxIndex + 1;

        if (Vertices == null || Vertices.Length != vLength) { Vertices = new Vertex[vLength]; }
        Array.Fill(Vertices, Vertex.New());
        
        if (dependencyOrder == null || vLength > dependencyOrder.Length) { dependencyOrder = new int[vLength]; }
        Array.Fill(dependencyOrder, -1);
        _dependencyOrderIndex = -1;

        if (stronglyConnectedComponents == null || vLength > stronglyConnectedComponents.Length) {
            stronglyConnectedComponents = new Segment[vLength];
        }
        Array.Fill(stronglyConnectedComponents, new Segment(-1, -1));
        _sccIndex = -1;

        _stackIndex = 0;
        if (_stack == null || vLength > _stack.Length) { _stack = new int[vLength]; }
        Array.Fill(_stack, -1);

        if (_edgesArray == null || _edgesArray.Length != _edges.Count) { _edgesArray = new Edge[_edges.Count]; }
        _edges.CopyTo(_edgesArray);
        
        if (_edgeSkipList == null || _edgeSkipList.Length != _edges.Count) { _edgeSkipList = new int[_edges.Count]; }
        Array.Fill(_edgeSkipList, -1);

        var previousFrom = -1;
        var currentNextEdge = -1;
        for (var i = _edgesArray.Length - 1; i >= 0; i--) {
            var from = _edgesArray[i].From;
            if (from != previousFrom) {
                currentNextEdge = i;
                previousFrom = from;
            }
            _edgeSkipList[i] = currentNextEdge + 1;
            currentNextEdge = i;
        }
    }
    
    public void Tarjan(ref int[] dependencyOrder, ref Segment[] stronglyConnectedComponents) {
        _prepare(ref dependencyOrder, ref stronglyConnectedComponents);

        var vLength = Vertices.Length;
        var index = 0;
        for (var i = 0; i < vLength; i++) {
            if (Vertices[i].index == -1) {
                _strongConnect(i, ref index, ref dependencyOrder, ref stronglyConnectedComponents);
            }
        }
    }

    private void _push(int value) => _stack[_stackIndex++] = value;

    private int _pop() => _stack[--_stackIndex];
    
    private void _strongConnect(int vIndex, ref int index, ref int[] dependencyOrder, ref Segment[] stronglyConnectedComponents) {
        ref var v = ref Vertices[vIndex];
        v.index = index;
        v.lowlink = index;
        index++;
        _push(vIndex);
        v.onStack = true;

        // Loops over all edges from v; use skip list for better performance
        var edgeIndex = 0;
        while(edgeIndex < _edgesArray.Length) {
            var (uIndex, wIndex) = _edgesArray[edgeIndex];
            if (uIndex == vIndex) {
                ref var w = ref Vertices[wIndex];
                if (w.index == -1) {
                    _strongConnect(wIndex, ref index, ref dependencyOrder, ref stronglyConnectedComponents);
                    v.lowlink = Math.Min(v.lowlink, w.lowlink);
                } else if (w.onStack) {
                    v.lowlink = Math.Min(v.lowlink, w.index);
                }
                edgeIndex++;
            } else {
                edgeIndex = _edgeSkipList[edgeIndex];
            }
        }

        if (v.lowlink == v.index) {
            _sccIndex++;
            var count = 0;
            int wIndex;
            do {
                wIndex = _pop();
                ref var w = ref Vertices[wIndex];
                w.onStack = false;
                dependencyOrder[++_dependencyOrderIndex] = wIndex;
                count++;
            } while (wIndex != vIndex);
            
            stronglyConnectedComponents[_sccIndex] = new Segment(_dependencyOrderIndex - count + 1, count);
        }
    }
}

public struct Vertex {
    public int index;
    public int lowlink;
    public bool onStack;

    public static Vertex New() => new() { index = -1, lowlink = -1, onStack = false };
}