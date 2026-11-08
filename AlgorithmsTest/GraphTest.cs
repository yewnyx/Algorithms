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
    public void TestSpanBuffers() {
        graph.AddEdges((0, 1), (1, 0), (1, 2));
        int[] buffer = [-1, -1, -1, -1, -1, -1];
        var components = new Segment[3];

        // Output goes into the middle of a larger buffer; nothing outside the slice is touched.
        var count = graph.Tarjan(buffer.AsSpan(2, 3), components);

        count.Should().Be(2);
        buffer.Should().Equal(-1, -1, 2, 1, 0, -1);
        graph.Invoking(g => g.Tarjan(new int[2], components)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TestEdgeOrderAndDuplicatesDoNotMatter() {
        var inOrder = new Graph().AddEdges((0, 1), (1, 2), (2, 0), (2, 3), (3, 4), (4, 3), (1, 4));
        // Same edges, reversed, with (1, 2) added twice.
        var shuffled = new Graph().AddEdges((1, 4), (4, 3), (3, 4), (2, 3), (2, 0), (1, 2), (0, 1), (1, 2));

        int[]? expectedOrder = null, actualOrder = null;
        Segment[]? expectedComponents = null, actualComponents = null;
        var expectedCount = inOrder.Tarjan(ref expectedOrder, ref expectedComponents);
        var actualCount = shuffled.Tarjan(ref actualOrder, ref actualComponents);

        actualCount.Should().Be(expectedCount);
        actualOrder.Should().Equal(expectedOrder);
        actualComponents[..actualCount].Should().Equal(expectedComponents[..expectedCount]);
    }

    [Fact]
    public void TestClear() {
        graph.AddEdges((0, 1), (1, 2), (2, 3), (3, 0));
        int[] dependencyOrder = [];
        Segment[] stronglyConnectedComponents = [];
        graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents).Should().Be(1);

        // The smaller graph runs in buffers sized for the larger one.
        graph.Clear().AddEdge(0, 1);
        var count = graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        count.Should().Be(2);
        dependencyOrder[..2].Should().Equal(1, 0);
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

    [Fact]
    public void TestDeepChain() {
        // 0 -> 1 -> 2 -> ... -> 199999, deep enough to overflow a recursive traversal.
        const int length = 200_000;
        for (var i = 0; i < length - 1; i++) { graph.AddEdge(i, i + 1); }

        int[] dependencyOrder = [];
        Segment[] stronglyConnectedComponents = [];
        var count = graph.Tarjan(ref dependencyOrder, ref stronglyConnectedComponents);

        count.Should().Be(length);
        dependencyOrder[0].Should().Be(length - 1);
        dependencyOrder[length - 1].Should().Be(0);
    }
}