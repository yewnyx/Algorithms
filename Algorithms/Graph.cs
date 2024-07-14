using System;
using System.Collections.Generic;

namespace Yewnyx.Algorithms;

public class Graph
{
    public Vertex[] Vertices;
    public (int from, int to)[] Edges;

    public void Tarjan()
    {
        var vLength = Vertices.Length;
        var index = 0;
        var stack = new ArrayStack(vLength);
        for (var i = 0; i < vLength; i++)
        {
            if (Vertices[i].index == -1)
            {
                StrongConnect(i, ref index, stack);
            }
        }
    }

    private void StrongConnect(int vIndex, ref int index, ArrayStack stack) {
        ref var v = ref Vertices[vIndex];
        v.index = index;
        v.lowlink = index;
        index++;
        stack.Push(vIndex);
        v.onStack = true;

        foreach (var (uIndex, wIndex) in Edges) {
            if (uIndex == vIndex) { // Check if the edge starts from the current vertex
                
                ref var w = ref Vertices[wIndex];
                if (w.index == -1)
                {
                    StrongConnect(wIndex, ref index, stack);
                    v.lowlink = System.Math.Min(v.lowlink, w.lowlink);
                }
                else if (w.onStack)
                {
                    v.lowlink = System.Math.Min(v.lowlink, w.index);
                }
            }
        }

        if (v.lowlink == v.index) {
            var component = new List<int>();

            int wIndex;
            do {
                wIndex = stack.Pop();
                ref var w = ref Vertices[wIndex];
                w.onStack = false;
                component.Add(wIndex);
            } while (wIndex != vIndex);

            var indices = string.Join(", ", component);
            Console.WriteLine($"Strongly connected component: {indices}");
        }
    }
}

public class ArrayStack
{
    public int[] array;
    
    public int index;

    public ArrayStack(int capacity) {
        array = new int[capacity];
        Array.Fill(array, -1);
        index = 0;
    }

    public void Push(int value) => array[index++] = value;

    public int Pop() => array[--index];
}

public struct Vertex
{
    public int index;
    public int lowlink;
    public bool onStack;

    public static Vertex New() => new() { index = -1, lowlink = -1, onStack = false };
}