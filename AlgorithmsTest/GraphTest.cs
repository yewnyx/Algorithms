using Xunit.Abstractions;

namespace Yewnyx.Algorithms.Test;

public class GraphTest
{
    private readonly Graph graph = new();
    private readonly ITestOutputHelper _output;
    
    public GraphTest(ITestOutputHelper output) { _output = output; }
    
    [Fact]
    public void Test1()
    {
        // graph.Vertices = new Vertex[15];
        // Array.Fill(graph.Vertices, Vertex.New());
        // graph.Edges = [
        //     (1, 2),
        //     (2, 3),
        //     (3, 4),
        //     (4, 5),
        //     (5, 6),
        //     (6, 7),
        //     (7, 8),
        //     (8, 9),
        //     (9, 10),
        //     (10, 11),
        //     (11, 12),
        //     (12, 13),
        //     (13, 14),
        //     (14, 15),
        //     (15, 3),
        //     (2, 5),
        //     (4, 7),
        //     (6, 9),
        //     (8, 11),
        //     (10, 13),
        // ];
        // graph.Tarjan();
    }
}