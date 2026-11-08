using FluentAssertions;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

public class FloydWarshallTest {
    private const float X = float.PositiveInfinity;

    // 0 -> 1 costs 4, 0 -> 2 costs 1, 2 -> 1 costs 2, 1 -> 3 costs 1. Nothing leaves 3.
    private static float[] Weights() => [
        0, 4, 1, X,
        X, 0, X, 1,
        X, 2, 0, X,
        X, X, X, 0,
    ];

    [Fact]
    public void ShortestDistances() {
        var distances = Weights();
        FloydWarshall.Solve(distances, [], 4);

        distances.Should().Equal(
            0, 3, 1, 4,
            X, 0, X, 1,
            X, 2, 0, 3,
            X, X, X, 0);
    }

    [Fact]
    public void PathFollowsNext() {
        var distances = Weights();
        var next = new int[16];
        FloydWarshall.Solve(distances, next, 4);
        var path = new int[4];

        FloydWarshall.Path(next, 4, 0, 3, path).Should().Be(4);
        path.Should().Equal(0, 2, 1, 3);
        FloydWarshall.Path(next, 4, 2, 2, path).Should().Be(1);
        FloydWarshall.Path(next, 4, 3, 0, path).Should().Be(-1);

        // Too short: nothing is written and the needed length comes back.
        FloydWarshall.Path(next, 4, 0, 3, new int[2]).Should().Be(4);
    }

    [Fact]
    public void RelaxMatchesSolve() {
        // Adding 0 -> 3 at cost 2 beats the old route 0 -> 2 -> 1 -> 3, which costs 4.
        var relaxed = Weights();
        var next = new int[16];
        FloydWarshall.Solve(relaxed, next, 4);
        FloydWarshall.Relax(relaxed, next, 4, 0, 3, 2);

        var solved = Weights();
        solved[0 * 4 + 3] = 2;
        FloydWarshall.Solve(solved, [], 4);

        relaxed.Should().Equal(solved);
        var path = new int[4];
        FloydWarshall.Path(next, 4, 0, 3, path).Should().Be(2);
        path[..2].Should().Equal(0, 3);
    }

    [Fact]
    public void IntSumsSaturate() {
        // int.MaxValue means no edge. 0 -> 1 -> 2 would overflow, so 0 -> 2 stays unreachable.
        const int M = int.MaxValue;
        int[] distances = [
            0, M - 1, M,
            M, 0, M - 1,
            M, M, 0,
        ];
        FloydWarshall.Solve(distances, [], 3);

        distances.Should().Equal(
            0, M - 1, M,
            M, 0, M - 1,
            M, M, 0);
    }

    [Fact]
    public void NegativeWeightsAreRejected() {
        float[] distances = [0, -1, X, 0];
        FluentActions.Invoking(() => FloydWarshall.Solve(distances, [], 2))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
