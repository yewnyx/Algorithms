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
var graph1 = new Graph {
    Vertices = new Vertex[15],
    Edges = [
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
        (14, 9),  // This edge creates a cycle
        (1, 4),
        (3, 6),
        (5, 8),
        (7, 10),
        (9, 12),
    ]
};
Array.Fill(graph1.Vertices, Vertex.New());
graph1.Tarjan();

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
var graph2 = new Graph {
    Vertices = new Vertex[10],
    Edges = [
        (0, 1),
        (1, 2),
        (2, 3),
        (3, 4),
        (4, 5),
        (5, 6),
        (6, 7),
        (7, 8),
        (8, 9),
        (0, 3),
        (1, 4),
        (5, 8),
        (7, 9),
        (8, 6),
    ]
};
Array.Fill(graph2.Vertices, Vertex.New());
graph2.Tarjan();

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
var graph3 = new Graph {
    Vertices = new Vertex[9],
    Edges = [
        (1, 2),
        (2, 3),
        (3, 1),
        (4, 2),
        (4, 3),
        (4, 5),
        (5, 4),
        (5, 6),
        (6, 3),
        (6, 7),
        (7, 6),
        (8, 5),
        (8, 7),
        (8, 8),
    ]
};
Array.Fill(graph3.Vertices, Vertex.New());
graph3.Tarjan();