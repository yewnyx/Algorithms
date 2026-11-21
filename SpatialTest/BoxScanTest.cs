using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class BoxScanTest {
    // Unit cubes in a row along x, a gap of 1 between them:
    //   0: x 0..1    1: x 2..3    2: x 4..5    3: x 6..7    4: x 8..9      (y and z 0..1)
    private static Box Cube(float x) => new(x, 0, 0, x + 1, 1, 1);
    private static readonly Box[] Row = [Cube(0), Cube(2), Cube(4), Cube(6), Cube(8)];

    // A box with a different extent on every axis: x 1..4, y 2..6, z 3..8.
    private static readonly Box Slab = new(1, 2, 3, 4, 6, 8);

    private sealed class Packed {
        public readonly float[] MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public Packed(Box[] boxes) {
            var n = boxes.Length;
            (MinX, MinY, MinZ, MaxX, MaxY, MaxZ) = (new float[n], new float[n], new float[n], new float[n], new float[n], new float[n]);
            BoxScan.Pack(boxes, MinX, MinY, MinZ, MaxX, MaxY, MaxZ).Should().BeTrue();
        }

        public BoxColumns Columns => new(MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
    }

    [Fact]
    public void InSphereFindsTheBoxesWithinTheRadius() {
        var row = new Packed(Row);
        var results = new int[5];

        // From the middle of cube 2, cubes 1 and 3 are 1.5 away and cubes 0 and 4 are 3.5 away.
        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 1.6f, results, out var total).Should().Be(3);
        total.Should().Be(3);
        results[..3].Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ABoxThatOnlyTouchesTheSphereMatches() {
        var row = new Packed(Row);
        var results = new int[5];

        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 1.5f, results, out _).Should().Be(3);
        results[..3].Should().Equal(1, 2, 3);
    }

    [Fact]
    public void TheDistanceIsToTheNearestPointOfTheBox() {
        var cube = new Packed([Cube(0)]);
        var results = new int[1];

        // The nearest point of the cube to (4, 5, 1) is its corner (1, 1, 1): 3 along x, 4 along y.
        BoxScan.InSphere(cube.Columns, 4, 5, 1, 5, results, out _).Should().Be(1);
        BoxScan.InSphere(cube.Columns, 4, 5, 1, 4.99f, results, out _).Should().Be(0);
    }

    [Theory]
    [InlineData(0, 4, 5, 1)]     // 1 below on x
    [InlineData(6, 4, 5, 2)]     // 2 above on x
    [InlineData(2, 0, 5, 2)]     // 2 below on y
    [InlineData(2, 9, 5, 3)]     // 3 above on y
    [InlineData(2, 4, 0, 3)]     // 3 below on z
    [InlineData(2, 4, 12, 4)]    // 4 above on z
    [InlineData(2, 4, 5, 0)]     // inside
    public void TheDistanceCountsEachAxisFromBelowAndAbove(float x, float y, float z, float distance) {
        var slab = new Packed([Slab]);
        var results = new int[1];
        var distances = new float[1];

        BoxScan.InSphere(slab.Columns, x, y, z, distance, results, out _).Should().Be(1);
        if (distance > 0) { BoxScan.InSphere(slab.Columns, x, y, z, distance - 0.01f, results, out _).Should().Be(0); }
        BoxScan.Nearest(slab.Columns, x, y, z, 100, results, distances).Should().Be(1);
        distances[0].Should().Be(distance * distance);
    }

    [Fact]
    public void PackWritesEachCoordinateToItsColumn() {
        var slab = new Packed([Slab]);

        (slab.MinX[0], slab.MinY[0], slab.MinZ[0]).Should().Be((1f, 2f, 3f));
        (slab.MaxX[0], slab.MaxY[0], slab.MaxZ[0]).Should().Be((4f, 6f, 8f));
    }

    [Fact]
    public void ResultsStopAtTheBufferButTheTotalCountsEverything() {
        var row = new Packed(Row);
        int[] buffer = [-1, -1, -1, -1];

        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 100, buffer.AsSpan(1, 2), out var total).Should().Be(2);
        total.Should().Be(5);
        buffer.Should().Equal(-1, 0, 1, -1);
    }

    [Fact]
    public void NearestReturnsTheNearestFirst() {
        var row = new Packed(Row);
        var results = new int[3];
        var distances = new float[3];

        // Cubes 1 and 3 are equally far, so the lower index comes first.
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(3);
        results.Should().Equal(2, 1, 3);
        distances.Should().Equal(0, 2.25f, 2.25f);
    }

    [Fact]
    public void NearestBreaksTiesByTheLowerIndex() {
        var row = new Packed(Row);
        var results = new int[2];
        var distances = new float[2];

        // Only one of the two cubes at 1.5 fits.
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(2);
        results.Should().Equal(2, 1);
    }

    [Fact]
    public void NearestKeepsOnlyWhatIsWithinTheDistance() {
        var row = new Packed(Row);
        var results = new int[5];
        var distances = new float[5];

        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 2.25f, results, distances).Should().Be(3);
        results[..3].Should().Equal(2, 1, 3);
    }

    [Fact]
    public void NearestWritesAsManyAsTheShorterBufferHolds() {
        var row = new Packed(Row);
        int[] results = [-1, -1, -1];
        var distances = new float[1];

        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(1);
        results.Should().Equal(2, -1, -1);

        // No room at all.
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, [], []).Should().Be(0);
    }

    [Fact]
    public void MoreBoxesThanOnePassHolds() {
        // 300 cubes in the same row: cube i spans x 2i..2i+1. A pass takes 128 boxes, so there are
        // two full passes and a last one of 44.
        var boxes = new Box[300];
        for (var i = 0; i < boxes.Length; i++) { boxes[i] = Cube(2 * i); }
        var row = new Packed(boxes);
        var results = new int[4];
        var distances = new float[4];

        // Between cubes 127 and 128, where the first pass ends and the second begins.
        BoxScan.InSphere(row.Columns, 255.5f, 0.5f, 0.5f, 0.6f, results, out var total).Should().Be(2);
        total.Should().Be(2);
        results[..2].Should().Equal(127, 128);
        BoxScan.Nearest(row.Columns, 255.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(4);
        results.Should().Equal(127, 128, 126, 129);

        // In the last, shorter pass, and at the very last box.
        BoxScan.InSphere(row.Columns, 580.5f, 0.5f, 0.5f, 1.6f, results, out _).Should().Be(3);
        results[..3].Should().Equal(289, 290, 291);
        BoxScan.Nearest(row.Columns, 580.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(4);
        results.Should().Equal(290, 289, 291, 288);
        BoxScan.InSphere(row.Columns, 700, 0.5f, 0.5f, 101, results, out total).Should().Be(1);
        total.Should().Be(1);
        results[0].Should().Be(299);
    }

    [Fact]
    public void AWarmQueryDoesntAllocate() {
        var row = new Packed(Row);
        var results = new int[5];
        var distances = new float[5];
        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, out _);
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, distances);

        var before = GC.GetAllocatedBytesForCurrentThread();
        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, out _);
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, 100, results, distances);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void NoBoxes() {
        var none = new Packed([]);
        var results = new int[2];
        var distances = new float[2];

        BoxScan.InSphere(none.Columns, 0, 0, 0, 100, results, out var total).Should().Be(0);
        total.Should().Be(0);
        BoxScan.Nearest(none.Columns, 0, 0, 0, 100, results, distances).Should().Be(0);
    }

    [Fact]
    public void AnEmptyResultsBufferStillCounts() {
        var row = new Packed(Row);

        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, 1.6f, [], out var total).Should().Be(0);
        total.Should().Be(3);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ColumnsOfDifferentLengthsAreRefused(int shortColumn) {
        var row = new Packed(Row);
        // One column in turn is a box short.
        ReadOnlySpan<float> Column(float[] column, int index) => index == shortColumn ? column.AsSpan(0, 4) : column;
        var uneven = new BoxColumns(Column(row.MinX, 0), Column(row.MinY, 1), Column(row.MinZ, 2),
            Column(row.MaxX, 3), Column(row.MaxY, 4), Column(row.MaxZ, 5));
        int[] results = [-1, -1];
        float[] distances = [-1, -1];

        // Nothing is read or written, and -1 says why.
        uneven.Count.Should().Be(-1);
        BoxScan.InSphere(uneven, 4.5f, 0.5f, 0.5f, 100, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxScan.Nearest(uneven, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);
        results.Should().Equal(-1, -1);
        distances.Should().Equal(-1, -1);
    }

    [Fact]
    public void PackRefusesAShortColumn() {
        float[] minX = [-1, -1, -1, -1, -1], rest = new float[5], shortColumn = new float[4];

        BoxScan.Pack(Row, minX, rest, rest, rest, rest, shortColumn).Should().BeFalse();
        minX.Should().Equal(-1, -1, -1, -1, -1);
    }

    [Fact]
    public void ARadiusOrPointThatMakesNoSenseMatchesNothing() {
        var row = new Packed(Row);
        var results = new int[5];
        var distances = new float[5];

        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, -1, results, out var total).Should().Be(0);
        total.Should().Be(0);
        BoxScan.InSphere(row.Columns, 4.5f, 0.5f, 0.5f, float.NaN, results, out total).Should().Be(0);
        total.Should().Be(0);
        BoxScan.InSphere(row.Columns, float.NaN, 0.5f, 0.5f, 100, results, out total).Should().Be(0);
        total.Should().Be(0);
        BoxScan.Nearest(row.Columns, 4.5f, float.NaN, 0.5f, 100, results, distances).Should().Be(0);
        BoxScan.Nearest(row.Columns, 4.5f, 0.5f, 0.5f, float.NaN, results, distances).Should().Be(0);
    }

    [Fact]
    public void ANotANumberCoordinateLeavesThatSideOfTheBoxOpen() {
        var results = new int[5];

        // Cube 1 (x 2..3) with no lower x: it reaches down the row without limit, and still ends at 3.
        var openBelow = new Packed([Cube(0), new(float.NaN, 0, 0, 3, 1, 1), Cube(4), Cube(6), Cube(8)]);
        BoxScan.InSphere(openBelow.Columns, -50, 0.5f, 0.5f, 0.1f, results, out _).Should().Be(1);
        results[0].Should().Be(1);
        BoxScan.InSphere(openBelow.Columns, 4.5f, 0.5f, 0.5f, 1.4f, results, out _).Should().Be(1);
        results[0].Should().Be(2);
        BoxScan.InSphere(openBelow.Columns, 4.5f, 0.5f, 0.5f, 1.6f, results, out _).Should().Be(3);
        results[..3].Should().Equal(1, 2, 3);

        // With no upper x: it starts at 2 and reaches up the row without limit.
        var openAbove = new Packed([Cube(0), new(2, 0, 0, float.NaN, 1, 1), Cube(4), Cube(6), Cube(8)]);
        BoxScan.InSphere(openAbove.Columns, 50, 0.5f, 0.5f, 0.1f, results, out _).Should().Be(1);
        results[0].Should().Be(1);
        BoxScan.InSphere(openAbove.Columns, 0.5f, 0.5f, 0.5f, 1.4f, results, out _).Should().Be(1);
        results[0].Should().Be(0);
    }

    [Fact]
    public void BoxesCompareByTheirCorners() {
        (Slab == new Box(1, 2, 3, 4, 6, 8)).Should().BeTrue();
        Slab.Equals((object)new Box(1, 2, 3, 4, 6, 8)).Should().BeTrue();
        Slab.GetHashCode().Should().Be(new Box(1, 2, 3, 4, 6, 8).GetHashCode());

        // Each coordinate counts.
        Box[] others = [new(0, 2, 3, 4, 6, 8), new(1, 0, 3, 4, 6, 8), new(1, 2, 0, 4, 6, 8), new(1, 2, 3, 0, 6, 8), new(1, 2, 3, 4, 0, 8), new(1, 2, 3, 4, 6, 0)];
        foreach (var other in others) {
            (Slab != other).Should().BeTrue();
            Slab.Equals(other).Should().BeFalse();
        }
    }
}
