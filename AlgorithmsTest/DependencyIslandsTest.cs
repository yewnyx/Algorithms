using FluentAssertions;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

// Tarjan produces two things in one traversal: the strongly connected components (islands),
// and an ordering of those islands from most dependent to least dependent.
public class DependencyIslandsTest {
    // Edge A -> B means A must happen before B.
    //
    // digraph G {
    //     0 -> 1
    //     0 -> 2
    //     1 -> 3
    //     2 -> 3
    //     3 -> 4
    //     4 -> 5
    //     5 -> 3      // cycle {3, 4, 5}
    //     5 -> 6
    //     2 -> 7
    //     7 -> 8
    //     8 -> 7      // cycle {7, 8}
    //     6 -> 9
    //     8 -> 9
    // }
    //
    //            0
    //           / \
    //          1   2 ------.
    //           \ /         \
    //          [3 4 5]     [7 8]
    //              \         /
    //               6       /
    //                \     /
    //                   9
    private static readonly Edge[] Edges = [
        (0, 1),
        (0, 2),
        (1, 3),
        (2, 3),
        (3, 4),
        (4, 5),
        (5, 3),
        (5, 6),
        (2, 7),
        (7, 8),
        (8, 7),
        (6, 9),
        (8, 9),
    ];

    private readonly int[] _dependencyOrder = [];
    private readonly Segment[] _islands = [];
    private readonly int _islandCount;

    public DependencyIslandsTest() {
        _islandCount = new Graph().AddEdges(Edges).Tarjan(ref _dependencyOrder, ref _islands);
    }

    [Fact]
    public void CyclesBecomeIslands() {
        // Order within an island is not meaningful, so islands are compared as sorted sets.
        Islands().Should().BeEquivalentTo(new int[][] {
            [0],
            [1],
            [2],
            [3, 4, 5],
            [6],
            [7, 8],
            [9],
        });
    }

    [Fact]
    public void IslandsAreEmittedMostDependentFirst() {
        var islandOf = IslandOf();
        foreach (var (from, to) in Edges) {
            islandOf[to].Should().BeLessThanOrEqualTo(islandOf[from], $"{to} depends on {from}");
        }

        Islands().Should().BeEquivalentTo(new int[][] {
            [9],
            [6],
            [3, 4, 5],
            [1],
            [7, 8],
            [2],
            [0],
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ReversedOrderIsASerialSchedule() {
        var position = new int[_dependencyOrder.Length];
        for (var i = 0; i < _dependencyOrder.Length; i++) { position[_dependencyOrder[i]] = _dependencyOrder.Length - 1 - i; }

        var islandOf = IslandOf();
        foreach (var (from, to) in Edges) {
            if (islandOf[from] == islandOf[to]) { continue; }
            position[from].Should().BeLessThan(position[to], $"{from} must execute before {to}");
        }
    }

    [Fact]
    public void IslandsCanBeScheduledInParallelWaves() {
        // An island's wave is one past the latest wave of any island it depends on.
        // Walking islands in reverse emission order visits dependencies before dependents.
        var islandOf = IslandOf();
        var waveOf = new int[_islandCount];
        for (var island = _islandCount - 1; island >= 0; island--) {
            foreach (var (from, to) in Edges) {
                if (islandOf[to] == island && islandOf[from] != island) {
                    waveOf[island] = Math.Max(waveOf[island], waveOf[islandOf[from]] + 1);
                }
            }
        }

        var waves = Enumerable.Range(0, waveOf.Max() + 1)
            .Select(wave => Enumerable.Range(0, _islandCount)
                .Where(island => waveOf[island] == wave)
                .SelectMany(island => _dependencyOrder[_islands[island]])
                .Order()
                .ToArray())
            .ToList();

        waves.Should().BeEquivalentTo(new int[][] {
            [0],
            [1, 2],
            [3, 4, 5, 7, 8],
            [6],
            [9],
        }, options => options.WithStrictOrdering());
    }

    private List<int[]> Islands() =>
        Enumerable.Range(0, _islandCount)
            .Select(i => _dependencyOrder[_islands[i]].Order().ToArray())
            .ToList();

    private int[] IslandOf() {
        var islandOf = new int[_dependencyOrder.Length];
        for (var island = 0; island < _islandCount; island++) {
            foreach (var vertex in _dependencyOrder[_islands[island]]) { islandOf[vertex] = island; }
        }
        return islandOf;
    }
}
