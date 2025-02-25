using FluentAssertions;
using Xunit.Abstractions;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

public class SCCTest {
    private readonly Graph graph = new();
    private readonly ITestOutputHelper _output;

    public SCCTest(ITestOutputHelper output) { _output = output; }

    [Fact]
    public void Test1() {
        // digraph G {
        //     0 -> 1
        //     1 -> 2
        //     2 -> 3
        //     3 -> 4
        //     4 -> 5
        //     5 -> 6
        //     6 -> 7
        //     7 -> 8
        //     8 -> 9
        //     0 -> 3
        //     1 -> 4
        //     5 -> 8
        //     7 -> 9
        //     8 -> 6
        // }

        graph.AddEdges(
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

        int[] dependencyOrder = null!;
        Segment[] stronglyConnectedComponents = null!;
        graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        // _output.WriteLine("Dependency Ordering: " + string.Join(", ", dependencyOrder));
        dependencyOrder.Should().BeEquivalentTo([14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0]);
        int[][] stronglyConnectedComponentsTest = [
            [14, 13, 12, 11, 10, 9,],
            [8,],
            [7,],
            [6,],
            [5,],
            [4,],
            [3,],
            [2,],
            [1,],
            [0,],
        ];
        for (var i = 0; i < stronglyConnectedComponents.Length; i++) {
            var segment = stronglyConnectedComponents[i];
            if (segment.Size < 1) { break; }

            var component = dependencyOrder[segment];
            // _output.WriteLine($"Component {i}: " + string.Join(", ", component));
            component.Should().BeEquivalentTo(stronglyConnectedComponentsTest[i]);
        }
    }
    
    [Fact]
    public void Test2() {
        // digraph G {
        //     0 -> 1
        //     1 -> 2
        //     2 -> 3
        //     3 -> 4
        //     4 -> 5
        //     5 -> 6
        //     6 -> 7
        //     7 -> 8
        //     8 -> 9
        //     9 -> 10
        //     10 -> 11
        //     11 -> 12
        //     12 -> 13
        //     13 -> 14
        //     14 -> 9
        //     1 -> 4
        //     3 -> 6
        //     5 -> 8
        //     7 -> 10
        //     9 -> 12
        // }

        graph.AddEdge(0, 1)
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
            .AddEdge(8, 6); // This edge creates a cycle

        int[] dependencyOrder = null!;
        Segment[] stronglyConnectedComponents = null!;
        graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        // _output.WriteLine("Dependency Ordering: " + string.Join(", ", dependencyOrder));
        dependencyOrder.Should().BeEquivalentTo([9, 8, 7, 6, 5, 4, 3, 2, 1, 0]);
        int[][] stronglyConnectedComponentsTest = [
            [9,],
            [8, 7, 6,],
            [5,],
            [4,],
            [3,],
            [2,],
            [1,],
            [0,],
        ];
        for (var i = 0; i < stronglyConnectedComponents.Length; i++) {
            var segment = stronglyConnectedComponents[i];
            if (segment.Size < 1) { break; }

            var component = dependencyOrder[segment];
            // _output.WriteLine($"Component {i}: " + string.Join(", ", component));
            component.Should().BeEquivalentTo(stronglyConnectedComponentsTest[i]);
        }
    }
    
    [Fact]
    public void Test3() {
        // digraph G {
        //     0
        //     1 -> 2
        //     2 -> 3
        //     3 -> 1
        //     4 -> 2
        //     4 -> 3
        //     4 -> 5
        //     5 -> 4
        //     5 -> 6
        //     6 -> 3
        //     6 -> 7
        //     7 -> 6
        //     8 -> 5
        //     8 -> 7
        //     8 -> 8
        // }

        graph.AddEdge(1, 2)
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

        int[] dependencyOrder = null!;
        Segment[] stronglyConnectedComponents = null!;
        graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        // _output.WriteLine("Dependency Ordering: " + string.Join(", ", dependencyOrder));
        dependencyOrder.Should().BeEquivalentTo([8, 7, 6, 5, 4, 3, 2, 1, 0]);
        int[][] stronglyConnectedComponentsTest = [
            [0,],
            [3, 2, 1,],
            [7, 6,],
            [5, 4,],
            [8,],
        ];
        for (var i = 0; i < stronglyConnectedComponents.Length; i++) {
            var segment = stronglyConnectedComponents[i];
            if (segment.Size < 1) { break; }

            var component = dependencyOrder[segment];
            // _output.WriteLine($"Component {i}: " + string.Join(", ", component));
            component.Should().BeEquivalentTo(stronglyConnectedComponentsTest[i]);
        }
    }
}