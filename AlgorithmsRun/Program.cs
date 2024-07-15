using Yewnyx.Algorithms;

Console.WriteLine("""
digraph G {
  0 -> 1
  1 -> 2
  2 -> 3
  3 -> 4
  4 -> 5
  5 -> 6
  6 -> 7
  7 -> 8
  8 -> 9
  9 -> 10
  10 -> 11
  11 -> 12
  12 -> 13
  13 -> 14
  14 -> 9
  1 -> 4
  3 -> 6
  5 -> 8
  7 -> 10
  9 -> 12
}
""");

var graph1 = new Graph()
    .AddEdges(
        (0, 1),
        (1, 2),
        (2, 3),
        (3, 4),
        (4, 5),
        (5, 6),
        (6, 7),
        (7, 8),
        (8, 9),
        (9, 10),
        (10, 11),
        (11, 12),
        (12, 13),
        (13, 14),
        (14, 9), // This edge creates a cycle
        (1, 4),
        (3, 6),
        (5, 8),
        (7, 10),
        (9, 12));

int[] dependencyOrder1 = null!;
ArraySegment<int>[] stronglyConnectedComponents1 = null!;
graph1.Tarjan(ref dependencyOrder1, ref stronglyConnectedComponents1);

Console.WriteLine("""
digraph G {
  0 -> 1
  1 -> 2
  2 -> 3
  3 -> 4
  4 -> 5
  5 -> 6
  6 -> 7
  7 -> 8
  8 -> 9
  0 -> 3
  1 -> 4
  5 -> 8
  7 -> 9
  8 -> 6
}
""");
var graph2 = new Graph()
    .AddEdge(0, 1)
    .AddEdge(1, 2)
    .AddEdge(2, 3)
    .AddEdge(3, 4)
    .AddEdge(4, 5)
    .AddEdge(5, 6)
    .AddEdge(6, 7)
    .AddEdge(7, 8)
    .AddEdge(8, 9)
    .AddEdge(0, 3)
    .AddEdge(1, 4)
    .AddEdge(5, 8)
    .AddEdge(7, 9)
    .AddEdge(8, 6);
int[] dependencyOrder2 = null!;
ArraySegment<int>[] stronglyConnectedComponents2 = null!;
graph2.Tarjan(ref dependencyOrder2, ref stronglyConnectedComponents2);

Console.WriteLine("""
digraph G {
  0
  1 -> 2
  2 -> 3
  3 -> 1
  4 -> 2
  4 -> 3
  4 -> 5
  5 -> 4
  5 -> 6
  6 -> 3
  6 -> 7
  7 -> 6
  8 -> 5
  8 -> 7
  8 -> 8
}
""");
var graph3 = new Graph()
    .AddEdge(1, 2)
    .AddEdge(2, 3)
    .AddEdge(3, 1)
    .AddEdge(4, 2)
    .AddEdge(4, 3)
    .AddEdge(4, 5)
    .AddEdge(5, 4)
    .AddEdge(5, 6)
    .AddEdge(6, 3)
    .AddEdge(6, 7)
    .AddEdge(7, 6)
    .AddEdge(8, 5)
    .AddEdge(8, 7)
    .AddEdge(8, 8);
int[] dependencyOrder3 = null!;
ArraySegment<int>[] stronglyConnectedComponents3 = null!;
graph3.Tarjan(ref dependencyOrder3, ref stronglyConnectedComponents3);

Console.WriteLine("End");