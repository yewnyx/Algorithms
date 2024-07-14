using System;
using System.Collections.Generic;
using System.Linq;

namespace Yewnyx.Algorithms;

public sealed class Graph {
    private Vertex[] Vertices;
    private int _maxIndex = -1;

    private SortedSet<Edge> _edges = new();
    private Edge[] _edgesArray;
    private int[] _edgeSkipList;

    private int[] _stack;
    private int _stackIndex;


    public Graph AddEdges(params Edge[] edges) {
        _edges.UnionWith(edges);
        _maxIndex = Math.Max(_maxIndex, edges.Max(e => Math.Max(e.From, e.To)));
        return this;
    }
        
    public Graph AddEdge(int from, int to) {
        _edges.Add((from, to));
        _maxIndex = Math.Max(_maxIndex, Math.Max(from, to));
        return this;
    }
    
    private void _prepare() {
        var vLength = _maxIndex + 1;

        if (Vertices == null || Vertices.Length != vLength) { Vertices = new Vertex[vLength]; }
        Array.Fill(Vertices, Vertex.New());

        _stackIndex = 0;
        if (_stack == null || _stack.Length != vLength) { _stack = new int[vLength]; }
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
    
    public void Tarjan() {
        _prepare();

        var vLength = Vertices.Length;
        var index = 0;
        for (var i = 0; i < vLength; i++) {
            if (Vertices[i].index == -1) {
                _strongConnect(i, ref index);
            }
        }
    }

    private void _push(int value) => _stack[_stackIndex++] = value;

    private int _pop() => _stack[--_stackIndex];
    
    private void _strongConnect(int vIndex, ref int index) {
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
                    _strongConnect(wIndex, ref index);
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
            // TODO: Optimize this
            var component = new List<int>();
            int wIndex;
            do {
                wIndex = _pop();
                ref var w = ref Vertices[wIndex];
                w.onStack = false;
                component.Add(wIndex);
            } while (wIndex != vIndex);

            var indices = string.Join(", ", component);
            Console.WriteLine($"Strongly connected component: {indices}");
        }
    }
}

public struct Vertex {
    public int index;
    public int lowlink;
    public bool onStack;

    public static Vertex New() => new() { index = -1, lowlink = -1, onStack = false };
}