using FluentAssertions;
using Xunit.Abstractions;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

public class GraphTest
{
    private readonly Graph graph = new();
    private readonly ITestOutputHelper _output;
    
    public GraphTest(ITestOutputHelper output) { _output = output; }
    
    [Fact]
    public void TestDependencyOrdering() {
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
        //     1 -> 4
        //     3 -> 6
        //     5 -> 8
        //     7 -> 10
        //     9 -> 12
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
            (1, 4),
            (3, 6),
            (5, 8),
            (7, 10),
            (9, 12));

        int[] dependencyOrder = [];
        Segment[] stronglyConnectedComponents = [];
        var count = graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        // _output.WriteLine("Dependency Ordering: " + string.Join(", ", dependencyOrder));
        dependencyOrder.Should().BeEquivalentTo([14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0]);
        int[][] stronglyConnectedComponentsTest = [
            [14,],
            [13,],
            [12,],
            [11,],
            [10,],
            [9,],
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
        count.Should().Be(stronglyConnectedComponentsTest.Length);
        for (var i = 0; i < count; i++) {
            var component = dependencyOrder[stronglyConnectedComponents[i]];
            // _output.WriteLine($"Component {i}: " + string.Join(", ", component));
            component.Should().BeEquivalentTo(stronglyConnectedComponentsTest[i]);
        }
    }

    [Fact]
    public void TestNullBuffers() {
        graph.AddEdges((0, 1), (1, 0), (1, 2));
        int[]? dependencyOrder = null;
        Segment[]? stronglyConnectedComponents = null;
        var count = graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        count.Should().Be(2);
        dependencyOrder[stronglyConnectedComponents[0]].Should().Equal(2);
    }

    [Fact]
    public void TestEnsureIndex() {
        graph.EnsureIndex(3);
        int[] dependencyOrder = [];
        Segment[] stronglyConnectedComponents = [];
        var count = graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        // _output.WriteLine("Dependency Ordering: " + string.Join(", ", dependencyOrder));
        dependencyOrder.Should().BeEquivalentTo([0, 1, 2, 3]);
        int[][] stronglyConnectedComponentsTest = [
            [0,],
            [1,],
            [2,],
            [3,],
        ];
        count.Should().Be(stronglyConnectedComponentsTest.Length);
        for (var i = 0; i < count; i++) {
            var component = dependencyOrder[stronglyConnectedComponents[i]];
            // _output.WriteLine($"Component {i}: " + string.Join(", ", component));
            component.Should().BeEquivalentTo(stronglyConnectedComponentsTest[i]);
        }
    }
}