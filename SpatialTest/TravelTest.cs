using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class TravelTest {
    private const float Unit = 0.01f;
    private const ushort No = Travel.Unreached;

    // A field built over a region array, with what the build said of it.
    private sealed class Built {
        public readonly GridShape Shape;
        public readonly byte[] Region;
        public readonly byte[] Cost;
        public readonly ushort[] Distance;
        public readonly bool Ok;
        public readonly int Reached, Cut;
        public readonly float FieldUnit;

        public Built(GridShape shape, byte[] region, TravelSeed[] seeds, byte[]? cost = null, float unit = Unit) {
            Shape = shape;
            Region = region;
            Cost = cost ?? [];
            FieldUnit = unit;
            Distance = new ushort[shape.CellCount];
            Ok = Travel.Build(shape, region, Cost, seeds, unit, Distance, new float[Travel.TimesLength(shape)], new int[Travel.HeapLength(shape)], out Reached, out Cut);
        }

        public ushort At(int x, int y, int z) => Distance[Shape.IndexOf(x, y, z)];

        public (bool found, float x, float y, float z, float distance) Sample(float x, float y, float z) {
            var found = Travel.Sample(new TravelField(Shape, FieldUnit, Distance), x, y, z, out var dx, out var dy, out var dz, out var distance);
            return (found, dx, dy, dz, distance);
        }
    }

    // A region array with every cell open, or every cell but those a test closes.
    private static byte[] Open(GridShape shape, Func<int, int, int, bool>? closed = null) {
        var region = new byte[shape.CellCount];
        for (var z = 0; z < shape.CountZ; z++) {
            for (var y = 0; y < shape.CountY; y++) {
                for (var x = 0; x < shape.CountX; x++) {
                    if (closed == null || !closed(x, y, z)) { region[shape.IndexOf(x, y, z)] = 1; }
                }
            }
        }
        return region;
    }

    // A small ball round a cell's centre, starting from that centre: a seed for that cell alone, at 0.
    private static TravelSeed CellSeed(GridShape shape, int x, int y, int z) {
        shape.CentreOf(x, y, z, out var cx, out var cy, out var cz);
        return TravelSeed.FromPoint(Solid.Sphere(cx, cy, cz, shape.CellSize * 0.2f), cx, cy, cz);
    }

    // The same field by a plain version of the same method, in double: no heap, the least cell
    // found by looking at every one, and the textbook form of the update.
    private static double[] Plain(GridShape shape, byte[] region, byte[] cost, (int cell, double start)[] starts) {
        int nx = shape.CountX, ny = shape.CountY, nz = shape.CountZ, cells = shape.CellCount;
        var time = new double[cells];
        var settled = new bool[cells];
        Array.Fill(time, double.PositiveInfinity);
        foreach (var (cell, start) in starts) { time[cell] = Math.Min(time[cell], start); }
        bool Passable(int cell) => region[cell] != 0 && (cost.Length == 0 || cost[cell] != 255);
        double Least(int cell, bool low, bool high, int stride) {
            var least = double.PositiveInfinity;
            if (low && settled[cell - stride]) { least = Math.Min(least, time[cell - stride]); }
            if (high && settled[cell + stride]) { least = Math.Min(least, time[cell + stride]); }
            return least;
        }
        while (true) {
            var next = -1;
            for (var cell = 0; cell < cells; cell++) {
                if (!settled[cell] && time[cell] < double.PositiveInfinity && (next < 0 || time[cell] < time[next])) { next = cell; }
            }
            if (next < 0) { break; }
            settled[next] = true;
            int x = next % nx, y = next / nx % ny, z = next / (nx * ny);
            foreach (var (cx, cy, cz) in new[] { (x - 1, y, z), (x + 1, y, z), (x, y - 1, z), (x, y + 1, z), (x, y, z - 1), (x, y, z + 1) }) {
                if (cx < 0 || cx >= nx || cy < 0 || cy >= ny || cz < 0 || cz >= nz) { continue; }
                var cell = cx + nx * (cy + ny * cz);
                if (settled[cell] || !Passable(cell)) { continue; }
                double[] along = [Least(cell, cx > 0, cx < nx - 1, 1), Least(cell, cy > 0, cy < ny - 1, nx), Least(cell, cz > 0, cz < nz - 1, nx * ny)];
                Array.Sort(along);
                var h = shape.CellSize * (cost.Length == 0 ? 1.0 : 1.0 + cost[cell] / 16.0);
                var t = along[0] + h;
                if (t > along[1]) {
                    t = 0.5 * (along[0] + along[1] + Math.Sqrt(Math.Max(0, 2 * h * h - (along[0] - along[1]) * (along[0] - along[1]))));
                    if (t > along[2]) {
                        double s = along[0] + along[1] + along[2], q = along[0] * along[0] + along[1] * along[1] + along[2] * along[2];
                        t = (s + Math.Sqrt(Math.Max(0, s * s - 3 * (q - h * h)))) / 3;
                    }
                }
                time[cell] = Math.Min(time[cell], t);
            }
        }
        return time;
    }

    // 12 cells of half a metre each way.
    private static readonly GridShape Small = new(0, 0, 0, 0.5f, 12, 12, 12);

    // Two rooms with a wall between them at x = 5 and 6, open only where y is 4 to 7 and z below 6,
    // and a pillar in the second room.
    private static bool WallAndPillar(int x, int y, int z) =>
        ((x == 5 || x == 6) && !(y >= 4 && y <= 7 && z < 6)) || (x >= 8 && x <= 9 && y >= 2 && y <= 3);

    [Fact]
    public void TheFieldMatchesAPlainVersionOfTheSameMethod() {
        var region = Open(Small, WallAndPillar);
        var cost = new byte[Small.CellCount];
        // Costs that differ from cell to cell, and a few cells closed.
        for (var i = 0; i < cost.Length; i++) { cost[i] = (byte)(i % 7 == 0 ? 32 : i % 11 == 0 ? 5 : i % 97 == 0 ? 255 : 0); }
        TravelSeed[] seeds = [CellSeed(Small, 1, 1, 1), CellSeed(Small, 2, 10, 3)];
        (int, double)[] starts = [(Small.IndexOf(1, 1, 1), 0), (Small.IndexOf(2, 10, 3), 0)];
        foreach (var costs in new[] { cost, Array.Empty<byte>() }) {
            var field = new Built(Small, region, seeds, costs);
            field.Ok.Should().BeTrue();
            var plain = Plain(Small, region, costs, starts);
            var reached = 0;
            for (var cell = 0; cell < Small.CellCount; cell++) {
                if (double.IsPositiveInfinity(plain[cell])) {
                    field.Distance[cell].Should().Be(No, $"cell {cell}");
                } else {
                    ((double)field.Distance[cell]).Should().BeInRange(plain[cell] / Unit - 1.5, plain[cell] / Unit + 1.5, $"cell {cell}");
                    reached++;
                }
            }
            field.Reached.Should().Be(reached);
            field.Cut.Should().Be(0);
            reached.Should().BeGreaterThan(1000);
        }
    }

    [Fact]
    public void AlongALineOfCellsEveryStepPaysForTheCellItEnters() {
        // One row of ten cells of half a metre. Cell 4 costs three times as much to cross (32), cell 7 twice (16).
        var row = new GridShape(0, 0, 0, 0.5f, 10, 1, 1);
        var cost = new byte[10];
        cost[4] = 32;
        cost[7] = 16;
        // From the low end: 0, 50, 100, 150, then 150 more into cell 4, then 50 a cell, then 100 into cell 7.
        new Built(row, Open(row), [CellSeed(row, 0, 0, 0)], cost).Distance.Should().Equal(0, 50, 100, 150, 300, 350, 400, 500, 550, 600);
        // From the high end the same cells are entered from the other side, and pay the same.
        new Built(row, Open(row), [CellSeed(row, 9, 0, 0)], cost).Distance.Should().Equal(600, 550, 500, 450, 400, 250, 200, 150, 50, 0);
        // The cell a field starts in costs nothing to be in.
        new Built(row, Open(row), [CellSeed(row, 4, 0, 0)], cost).Distance.Should().Equal(200, 150, 100, 50, 0, 50, 100, 200, 250, 300);
        // An empty cost array is every cell normal, as is one of all zeros.
        new Built(row, Open(row), [CellSeed(row, 0, 0, 0)]).Distance.Should().Equal(0, 50, 100, 150, 200, 250, 300, 350, 400, 450);
        new Built(row, Open(row), [CellSeed(row, 0, 0, 0)], new byte[10]).Distance.Should().Equal(0, 50, 100, 150, 200, 250, 300, 350, 400, 450);
    }

    [Fact]
    public void AClosedCellAndACellOfNoRegionAreNeverEntered() {
        var row = new GridShape(0, 0, 0, 0.5f, 10, 1, 1);
        var cost = new byte[10];
        cost[6] = CellCost.Closed;
        var closed = new Built(row, Open(row), [CellSeed(row, 0, 0, 0)], cost);
        closed.Distance.Should().Equal(0, 50, 100, 150, 200, 250, No, No, No, No);
        closed.Reached.Should().Be(6);
        var region = Open(row, (x, _, _) => x == 3);
        new Built(row, region, [CellSeed(row, 0, 0, 0)]).Distance.Should().Equal(0, 50, 100, No, No, No, No, No, No, No);
        // A seed in a closed cell, or in a cell of no region, starts nothing.
        new Built(row, Open(row), [CellSeed(row, 6, 0, 0)], cost).Reached.Should().Be(0);
        new Built(row, region, [CellSeed(row, 3, 0, 0)]).Reached.Should().Be(0);
    }

    [Fact]
    public void InOpenSpaceTheFieldIsCloseToTheStraightLineDistance() {
        // From a ball of cells round a point, each starting at its true distance, in an empty grid of
        // 24 cells each way. Fast marching overstates a distance that is not along an axis: here by
        // up to 0.71 m at 15 m, and by up to 11 parts in 100 just outside the ball. It never
        // understates by more than the rounding to a count.
        var grid = new GridShape(0, 0, 0, 0.5f, 24, 24, 24);
        const float px = 3.1f, py = 2.7f, pz = 3.4f;
        var field = new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(px, py, pz, 1.2f), px, py, pz)]);
        field.Ok.Should().BeTrue();
        field.Reached.Should().Be(grid.CellCount);
        var worst = 0.0;
        for (var z = 0; z < 24; z++) {
            for (var y = 0; y < 24; y++) {
                for (var x = 0; x < 24; x++) {
                    grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    var straight = Math.Sqrt(((double)cx - px) * ((double)cx - px) + ((double)cy - py) * ((double)cy - py) + ((double)cz - pz) * ((double)cz - pz));
                    var travelled = field.At(x, y, z) * (double)Unit;
                    travelled.Should().BeGreaterThan(straight - 1.5 * Unit, $"cell {x}, {y}, {z}");
                    travelled.Should().BeLessThan(straight * 1.05 + 0.35, $"cell {x}, {y}, {z}");
                    worst = Math.Max(worst, travelled - straight);
                }
            }
        }
        worst.Should().BeInRange(0.6, 0.8, "the overstatement is real, and a test that allowed none would be testing nothing");
    }

    [Fact]
    public void RoundACornerTheFieldIsCloseToTheWayRoundIt() {
        // A wall one cell thick along y at x = 10, from y = 0 up to y = 11, in a flat grid 24 by 24.
        // From a seed on one side, a cell behind the wall is reached round its end.
        var grid = new GridShape(0, 0, 0, 0.5f, 24, 24, 1);
        var region = Open(grid, (x, y, _) => x == 10 && y <= 11);
        grid.CentreOf(4, 4, 0, out var sx, out var sy, out var sz);
        var field = new Built(grid, region, [TravelSeed.FromPoint(Solid.Sphere(sx, sy, sz, 0.8f), sx, sy, sz)]);
        // The way round passes the wall's end: its corner cell is (10, 11), so the nearest open
        // centres are in the row y = 12. Through (10, 12) to a cell straight behind the wall.
        grid.CentreOf(10, 12, 0, out var ex, out var ey, out _);
        foreach (var (x, y) in new[] { (15, 4), (18, 2), (13, 8), (20, 10) }) {
            grid.CentreOf(x, y, 0, out var cx, out var cy, out _);
            var round = Math.Sqrt(((double)ex - sx) * ((double)ex - sx) + ((double)ey - sy) * ((double)ey - sy)) +
                        Math.Sqrt(((double)cx - ex) * ((double)cx - ex) + ((double)cy - ey) * ((double)cy - ey));
            var travelled = field.At(x, y, 0) * (double)Unit;
            // Measured over every cell behind the wall: between 6 and 13 parts in 100 over.
            travelled.Should().BeInRange(round, round * 1.15, $"cell {x}, {y}");
        }
        // A cell in the open on the seed's side is nearly its straight-line distance.
        grid.CentreOf(8, 20, 0, out var ox, out var oy, out _);
        var direct = Math.Sqrt(((double)ox - sx) * ((double)ox - sx) + ((double)oy - sy) * ((double)oy - sy));
        (field.At(8, 20, 0) * (double)Unit).Should().BeInRange(direct - 0.02, direct * 1.05 + 0.35);
    }

    [Fact]
    public void ASeedStartsItsCellsAtTheirDistanceFromAPointOrAPlane() {
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var box = Solid.Box(new Box(1.9f, 1.9f, 1.9f, 3.6f, 3.1f, 2.6f));

        // From a point: each cell whose centre is in the box starts at its true distance from it.
        var point = new Built(grid, Open(grid), [TravelSeed.FromPoint(box, 2.25f, 2.25f, 2.25f)]);
        point.At(4, 4, 4).Should().Be(0, "centre 2.25, 2.25, 2.25");
        point.At(6, 4, 4).Should().Be(100);
        point.At(6, 5, 4).Should().Be(112, "sqrt(1 + 0.25) = 1.118");
        point.At(5, 5, 4).Should().Be(71, "sqrt(0.5) = 0.707");
        point.At(7, 4, 4).Should().Be(150, "one cell beyond the box along x: reached from the cell at 100");
        point.At(3, 4, 4).Should().Be(50);

        // From a plane through x = 2.5 that faces along +x: cells in front start at how far in front, cells behind at 0.
        var plane = new Built(grid, Open(grid), [TravelSeed.FromPlane(box, 2.5f, 0, 0, 3, 0, 0)]);
        plane.At(4, 4, 4).Should().Be(0, "x = 2.25 is behind the plane");
        plane.At(5, 4, 4).Should().Be(25, "x = 2.75 is 0.25 in front");
        plane.At(6, 5, 4).Should().Be(75, "x = 3.25 is 0.75 in front");
        plane.At(7, 4, 4).Should().Be(125, "one cell on from the last in the box");
        plane.At(3, 4, 4).Should().Be(50, "behind the plane the field spreads out from 0");

        // A seed starts only the cells whose centres are inside it. A ball of radius 0.8 round a centre
        // holds that cell, the six that share a face with it (0.5 away) and the twelve that share an
        // edge (0.71), but not the eight at its corners (0.87), though they are inside the box round it.
        var ball = new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(2.25f, 2.25f, 2.25f, 0.8f), 2.25f, 2.25f, 2.25f)]);
        ball.At(5, 4, 4).Should().Be(50);
        ball.At(5, 5, 4).Should().Be(71);
        ball.At(5, 5, 5).Should().Be(100, "not started at its true 0.87: reached from three cells at 0.71, which is 0.71 + 0.5 / sqrt(3)");

        // Where two seeds hold the same cell it starts at the lesser.
        var both = new Built(grid, Open(grid), [TravelSeed.FromPoint(box, 2.25f, 2.25f, 2.25f), TravelSeed.FromPoint(box, 3.25f, 2.25f, 2.25f)]);
        both.At(4, 4, 4).Should().Be(0);
        both.At(6, 4, 4).Should().Be(0);
        both.At(5, 4, 4).Should().Be(50);
    }

    [Fact]
    public void ASeedThatHoldsNoCellItCanStartFromStartsNothing() {
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        // No centre inside; outside the grid; in cells of no region.
        new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(2.1f, 2.1f, 2.1f, 0.05f), 2.1f, 2.1f, 2.1f)]).Reached.Should().Be(0);
        new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(20, 20, 20, 1), 20, 20, 20)]).Reached.Should().Be(0);
        var blocked = new Built(grid, Open(grid, (x, _, _) => x < 3), [TravelSeed.FromPoint(Solid.Box(new Box(0, 0, 0, 1.4f, 4, 4)), 0.75f, 2, 2)]);
        blocked.Ok.Should().BeTrue();
        blocked.Reached.Should().Be(0);
        blocked.Distance.Should().OnlyContain(value => value == No);
        // No seeds at all.
        var none = new Built(grid, Open(grid), []);
        none.Ok.Should().BeTrue();
        none.Reached.Should().Be(0);
        // With another seed that does start, the field is that seed's alone.
        new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(20, 20, 20, 1), 20, 20, 20), CellSeed(grid, 1, 1, 1)]).Reached.Should().Be(512);
    }

    // From the centre of every reached cell (or every one in a set), a follower that moves a fifth
    // of a cell at a time along the sampled direction. Returns how many did not end as wanted.
    private static int Stalled(Built field, Func<int, bool> from, Func<float, float, float, float, bool> arrived) {
        var shape = field.Shape;
        var stalled = 0;
        for (var cell = 0; cell < shape.CellCount; cell++) {
            if (field.Distance[cell] == No || !from(cell)) { continue; }
            shape.CentreOf(cell % shape.CountX, cell / shape.CountX % shape.CountY, cell / (shape.CountX * shape.CountY), out var x, out var y, out var z);
            var done = false;
            for (var step = 0; step < 3000 && !done; step++) {
                var (found, dx, dy, dz, travel) = field.Sample(x, y, z);
                if (!found) { break; }
                if (arrived(x, y, z, travel)) { done = true; break; }
                if (dx == 0f && dy == 0f && dz == 0f) { break; }
                x += dx * 0.1f; y += dy * 0.1f; z += dz * 0.1f;
            }
            if (!done) { stalled++; }
        }
        return stalled;
    }

    [Fact]
    public void ASeedNeverStartsACellWhoseStraightWayIsShut() {
        // A wall one cell thick at x = 8, from y = 0 to 15, in a flat grid of 24 by 24: the way round
        // is past its end at y = 16. A goal 0.6 m in front of the wall, with a ball of 2 m, which
        // reaches well behind the wall.
        var grid = new GridShape(0, 0, 0, 0.5f, 24, 24, 1);
        var region = Open(grid, (x, y, _) => x == 8 && y <= 15);
        const float gx = 3.4f, gy = 3.25f, gz = 0.25f;
        var field = new Built(grid, region, [TravelSeed.FromPoint(Solid.Sphere(gx, gy, gz, 2f), gx, gy, gz)]);
        // A cell straight behind the wall, 1.35 m from the goal in a straight line and inside the
        // ball, is not started at that: it is reached the long way round the wall's end.
        grid.CentreOf(9, 6, 0, out var bx, out var by, out _);
        Math.Sqrt(((double)bx - gx) * ((double)bx - gx) + ((double)by - gy) * ((double)by - gy)).Should().BeLessThan(2);
        (field.At(9, 6, 0) * (double)Unit).Should().BeGreaterThan(9, "round the end of the wall at y = 8 m");
        // A cell on the goal's side at the same distance is started at it.
        field.At(4, 8, 0).Should().Be((ushort)Math.Round(Math.Sqrt((2.25 - gx) * (2.25 - gx) + (4.25 - gy) * (4.25 - gy)) * 100));
        // And from every cell behind the wall a follower gets round to the goal.
        Stalled(field, cell => cell % 24 > 8, (x, y, _, travel) => travel < 0.6f && x < 4).Should().Be(0);
        Stalled(field, _ => true, (_, _, _, travel) => travel < 0.6f).Should().Be(0);
    }

    [Fact]
    public void AGoalInACellThatCannotBeEnteredStillStartsTheCellsRoundIt() {
        // A goal can be nearer a wall than a cell can be. Here its own cell, (4, 4, 0), is not one
        // the field can enter. The last cell's length of each way to it is let off, so the cells
        // round it still start, at their true distances from the goal.
        var grid = new GridShape(0, 0, 0, 0.5f, 10, 10, 1);
        var region = Open(grid, (x, y, _) => x == 4 && y == 4);
        const float gx = 2.3f, gy = 2.2f, gz = 0.25f;
        var field = new Built(grid, region, [TravelSeed.FromPoint(Solid.Sphere(gx, gy, gz, 1.2f), gx, gy, gz)]);
        field.At(4, 4, 0).Should().Be(No);
        field.At(5, 4, 0).Should().Be((ushort)Math.Round(Math.Sqrt((2.75 - gx) * (2.75 - gx) + (2.25 - gy) * (2.25 - gy)) * 100));
        field.At(3, 3, 0).Should().Be((ushort)Math.Round(Math.Sqrt((1.75 - gx) * (1.75 - gx) + (1.75 - gy) * (1.75 - gy)) * 100));
        field.At(6, 4, 0).Should().Be((ushort)Math.Round(Math.Sqrt((3.25 - gx) * (3.25 - gx) + (2.25 - gy) * (2.25 - gy)) * 100), "two cells away, straight past the closed one");
        field.Reached.Should().Be(99);
    }

    // Two rooms along x with a wall between them at x = 10 and 11, and an opening in it. The cells
    // within one cell of the wall are closed too, as they are for a body with some size: so the
    // opening that can be passed is one cell narrower on each side than the gap in the wall.
    private static byte[] RoomsWithAnOpening(GridShape grid, int fromY, int toY) =>
        Open(grid, (x, y, _) => x >= 9 && x <= 12 && !(y >= fromY + 1 && y <= toY - 1));

    [Fact]
    public void ThroughAnOpeningEveryFollowerCrossesThePlane() {
        // A gap in the wall from y = 6 to 13, so cells y = 7 to 12 can be passed. The seed is a box
        // as wide as the gap itself, from a little behind the plane at x = 5.5 m (the wall's middle)
        // to 1.5 m in front of it. Its outer columns, y = 6 and 13, lie in front of the wall's ends,
        // where the way to the plane is shut: those cells are not started, and no follower is left
        // standing in front of the door frame.
        var grid = new GridShape(0, 0, 0, 0.5f, 22, 20, 1);
        var region = RoomsWithAnOpening(grid, 6, 13);
        region[grid.IndexOf(10, 9, 0)].Should().Be(1, "in the opening");
        region[grid.IndexOf(10, 6, 0)].Should().Be(0, "the gap's edge is too near the wall's end");
        region[grid.IndexOf(12, 3, 0)].Should().Be(0, "beside the wall");
        region[grid.IndexOf(13, 6, 0)].Should().Be(1, "a cell further into the room, in front of the wall's end");
        var seed = TravelSeed.FromPlane(Solid.Box(new Box(5.2f, 3, 0, 7, 7, 0.5f)), 5.5f, 0, 0, 1, 0, 0);
        var field = new Built(grid, region, [seed]);
        field.At(13, 9, 0).Should().Be(125, "in front of the opening: started at its distance from the plane");
        field.At(13, 6, 0).Should().BeGreaterThan(125, "in front of the wall's end: reached from beside it, not started");
        // From every cell of the far room, a follower ends up across the plane.
        Stalled(field, cell => cell % 22 >= 12, (x, _, _, _) => x < 5.5f).Should().Be(0);
    }

    [Fact]
    public void AnOpeningTooNarrowToPassStartsNothingInFrontOfIt() {
        // The same wall with a gap of two cells, which leaves no cell that can be passed. A seed
        // across it starts the cells behind the plane and none in front: the room in front is not
        // led to a door it cannot use. It is not reached at all, and a sample there is refused.
        var grid = new GridShape(0, 0, 0, 0.5f, 22, 20, 1);
        var region = RoomsWithAnOpening(grid, 9, 10);
        for (var y = 0; y < 20; y++) { region[grid.IndexOf(10, y, 0)].Should().Be(0); }
        var seed = TravelSeed.FromPlane(Solid.Box(new Box(4, 3, 0, 8, 7, 0.5f)), 5.5f, 0, 0, 1, 0, 0);
        var field = new Built(grid, region, [seed]);
        field.Ok.Should().BeTrue();
        field.At(8, 9, 0).Should().Be(0, "behind the plane");
        field.At(13, 9, 0).Should().Be(No, "in front of it, inside the seed's box");
        field.At(18, 4, 0).Should().Be(No);
        field.Reached.Should().Be(9 * 20, "the room behind the plane and no more");
        field.Sample(8f, 4.75f, 0.25f).found.Should().BeFalse();

        // A wall only one cell thick, in front of the plane at x = 5.5 m: the cells of column 11.
        // The cell just beyond it is three quarters of a metre from the plane, and its way there is
        // looked at to within a quarter of a cell of the plane, so the wall is seen and it is not started.
        var thin = Open(grid, (x, _, _) => x == 11);
        var across = new Built(grid, thin, [TravelSeed.FromPlane(Solid.Box(new Box(5.2f, 3, 0, 7, 7, 0.5f)), 5.5f, 0, 0, 1, 0, 0)]);
        across.At(10, 9, 0).Should().Be(0, "behind the plane");
        across.At(12, 9, 0).Should().Be(No, "in front of the thin wall");
        across.At(13, 9, 0).Should().Be(No);
        across.Reached.Should().Be(11 * 20);
    }

    [Fact]
    public void TheWayIsLookedAtEveryQuarterOfACell() {
        // From the centre of cell (5, 0) to a goal at 0.75, 1.75 the straight way crosses a corner
        // of cell (4, 0) for a tenth of a metre, between a quarter and half a metre along. Looked at
        // only every whole cell it would be missed; with that cell closed, the way is shut.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 1);
        const float gx = 0.75f, gy = 1.75f, gz = 0.25f;
        var seed = TravelSeed.FromPoint(Solid.Sphere(gx, gy, gz, 3f), gx, gy, gz);
        new Built(grid, Open(grid), [seed]).At(5, 0, 0).Should().Be(250, "with nothing closed it starts at its true distance");
        var clipped = new Built(grid, Open(grid, (x, y, _) => x == 4 && y == 0), [seed]);
        clipped.At(5, 0, 0).Should().BeGreaterThan(255, "not started: reached from the cells beside it");
        clipped.At(5, 1, 0).Should().Be(224, "its neighbour's way is clear: sqrt(4 + 1)");
    }

    [Fact]
    public void ACellBehindThePlaneStartsWhateverLiesBetweenItAndThePlane() {
        // A plane at x = 3 that faces along +x, and a seed box from 1.6 to 4.4. The column of cells
        // just behind the plane, x = 5, is closed. The cells behind that still start at 0: they are
        // already past the plane, and have no way to it to be looked at.
        var grid = new GridShape(0, 0, 0, 0.5f, 12, 4, 1);
        var field = new Built(grid, Open(grid, (x, _, _) => x == 5), [TravelSeed.FromPlane(Solid.Box(new Box(1.6f, 0, 0, 4.4f, 2, 0.5f)), 3, 0, 0, 1, 0, 0)]);
        field.At(4, 1, 0).Should().Be(0);
        field.At(3, 1, 0).Should().Be(0);
        field.At(2, 1, 0).Should().Be(50, "beyond the box, behind the plane");
        field.At(6, 1, 0).Should().Be(25, "the first cell in front");
        field.Reached.Should().Be(11 * 4);
    }

    [Fact]
    public void AWayThatLeavesTheGridIsNotAnOpenWay() {
        // A goal a metre outside the grid, with a ball that reaches well into it. Every way to it
        // leaves the grid more than a cell before the goal, and what is outside is not known to be
        // open: nothing is started.
        var grid = new GridShape(0, 0, 0, 0.5f, 10, 10, 1);
        var outside = new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(-1, 2.25f, 0.25f, 2.5f), -1, 2.25f, 0.25f)]);
        outside.Ok.Should().BeTrue();
        outside.Reached.Should().Be(0);
        // A goal only 0.3 m outside is within the last cell's length of every way, which is let off.
        var near = new Built(grid, Open(grid), [TravelSeed.FromPoint(Solid.Sphere(-0.3f, 2.25f, 0.25f, 2.5f), -0.3f, 2.25f, 0.25f)]);
        near.Reached.Should().Be(100);
        near.At(0, 4, 0).Should().Be(55, "0.25 + 0.3 from the goal");
    }

    [Fact]
    public void ADistanceThatDoesNotFitIsLeftUnreachedAndCounted() {
        // A row of 17,000 cells of one metre, counted in quarters of a metre: 65,534 counts is
        // 16,383.5 m, so cells 0 to 16,383 fit and the 616 beyond do not.
        var row = new GridShape(0, 0, 0, 1f, 17000, 1, 1);
        var field = new Built(row, Open(row), [CellSeed(row, 0, 0, 0)], unit: 0.25f);
        field.Ok.Should().BeTrue();
        field.At(16383, 0, 0).Should().Be(65532);
        field.At(16384, 0, 0).Should().Be(No, "65,536 counts does not fit, and is not held at the largest that does");
        field.At(16999, 0, 0).Should().Be(No);
        field.Reached.Should().Be(16384);
        field.Cut.Should().Be(616);
        // The last count that fits is 65,534: a row that ends exactly there has nothing cut.
        var just = new GridShape(0, 0, 0, 0.5f, 32768, 1, 1);
        var exact = new Built(just, Open(just), [CellSeed(just, 0, 0, 0)], unit: 0.125f);
        exact.At(16383, 0, 0).Should().Be(65532);
        exact.At(16384, 0, 0).Should().Be(No);
        exact.Cut.Should().Be(16384);
        // 65,535 counts is the mark of a cell that is not reached, so a cell exactly that far is cut too.
        // Cells of 1.25 m counted in quarters of a metre are 5 counts apart: cell 13,107 is at 65,535.
        var fives = new GridShape(0, 0, 0, 1.25f, 13200, 1, 1);
        var edge = new Built(fives, Open(fives), [CellSeed(fives, 0, 0, 0)], unit: 0.25f);
        edge.At(13106, 0, 0).Should().Be(65530);
        edge.At(13107, 0, 0).Should().Be(No);
        edge.Reached.Should().Be(13107);
        edge.Cut.Should().Be(93);
    }

    // Whether every reached cell that is not a start has a face neighbour with a lower count, and
    // a walk from it to its lowest face neighbour, again and again, ends at a start.
    private static void LeadsDownToAStart(Built field, HashSet<int> starts) {
        var shape = field.Shape;
        int nx = shape.CountX, ny = shape.CountY, nz = shape.CountZ;
        int Lowest(int cell) {
            int x = cell % nx, y = cell / nx % ny, z = cell / (nx * ny), best = -1;
            foreach (var (cx, cy, cz) in new[] { (x - 1, y, z), (x + 1, y, z), (x, y - 1, z), (x, y + 1, z), (x, y, z - 1), (x, y, z + 1) }) {
                if (cx < 0 || cx >= nx || cy < 0 || cy >= ny || cz < 0 || cz >= nz) { continue; }
                var next = cx + nx * (cy + ny * cz);
                if (field.Distance[next] != No && field.Distance[next] < field.Distance[cell] && (best < 0 || field.Distance[next] < field.Distance[best])) { best = next; }
            }
            return best;
        }
        var checkedCells = 0;
        for (var cell = 0; cell < shape.CellCount; cell++) {
            if (field.Distance[cell] == No) { continue; }
            checkedCells++;
            var at = cell;
            for (var steps = 0; !starts.Contains(at); steps++) {
                var next = Lowest(at);
                next.Should().BeGreaterThanOrEqualTo(0, $"cell {at} is reached, is not a start, and has no lower neighbour");
                steps.Should().BeLessThan(shape.CellCount);
                at = next;
            }
        }
        checkedCells.Should().Be(field.Reached);
    }

    [Fact]
    public void EveryReachedCellLeadsDownToWhereTheFieldStarts() {
        // Rooms and a pillar.
        var rooms = new Built(Small, Open(Small, WallAndPillar), [CellSeed(Small, 1, 1, 1)]);
        LeadsDownToAStart(rooms, [Small.IndexOf(1, 1, 1)]);
        rooms.Reached.Should().BeGreaterThan(1000);

        // The same with costs that differ from cell to cell.
        var cost = new byte[Small.CellCount];
        for (var i = 0; i < cost.Length; i++) { cost[i] = (byte)(i * 37 % 61); }
        LeadsDownToAStart(new Built(Small, Open(Small, WallAndPillar), [CellSeed(Small, 10, 10, 10), CellSeed(Small, 1, 1, 1)], cost), [Small.IndexOf(10, 10, 10), Small.IndexOf(1, 1, 1)]);

        // A maze one cell wide: walls on every other row, with a gap at alternate ends.
        var flat = new GridShape(0, 0, 0, 0.5f, 15, 15, 1);
        var maze = new Built(flat, Open(flat, (x, y, _) => y % 2 == 1 && x != (y % 4 == 1 ? 14 : 0)), [CellSeed(flat, 0, 0, 0)]);
        LeadsDownToAStart(maze, [flat.IndexOf(0, 0, 0)]);
        maze.Reached.Should().Be(8 * 15 + 7);
        // Along the maze the distance is the length of the way, cell by cell.
        maze.At(14, 0, 0).Should().Be(700);
        maze.At(14, 2, 0).Should().Be(800);
        maze.At(0, 2, 0).Should().Be(1500);
        // Seven rows of fourteen steps and seven gaps of two steps, to the start of the last row; then along it.
        maze.At(14, 14, 0).Should().Be((ushort)((14 * 7 + 2 * 7) * 50));
        maze.At(0, 14, 0).Should().Be((ushort)((14 * 8 + 2 * 7) * 50));
    }

    [Fact]
    public void TheFieldReachesExactlyTheRegionsItsSeedsAreIn() {
        // Three separate rooms along x, the regions 1, 2 and 3. Seeded in the first and the third.
        var grid = new GridShape(0, 0, 0, 0.5f, 15, 4, 4);
        var region = new byte[grid.CellCount];
        for (var cell = 0; cell < region.Length; cell++) {
            var x = cell % 15;
            region[cell] = (byte)(x < 4 ? 1 : x == 4 || x == 9 ? 0 : x < 9 ? 2 : 3);
        }
        var field = new Built(grid, region, [CellSeed(grid, 1, 1, 1), CellSeed(grid, 12, 2, 2)]);
        field.Reached.Should().Be(4 * 16 + 5 * 16);
        for (var cell = 0; cell < region.Length; cell++) {
            (field.Distance[cell] != No).Should().Be(region[cell] == 1 || region[cell] == 3, $"cell {cell}");
        }
    }

    [Fact]
    public void ARefusedBuildLeavesNoCellReached() {
        var grid = new GridShape(0, 0, 0, 0.5f, 6, 6, 6);
        var region = Open(grid);
        TravelSeed[] seeds = [CellSeed(grid, 1, 1, 1)];
        var times = new float[Travel.TimesLength(grid)];
        var heap = new int[Travel.HeapLength(grid)];

        ushort[] Fresh(int length = 216) { var distance = new ushort[length]; distance.AsSpan().Fill(7); return distance; }
        void Refused(bool result, ushort[] distance, string why) {
            result.Should().BeFalse(why);
            distance.Should().OnlyContain(value => value == No, why);
        }

        var good = Fresh();
        Travel.Build(grid, region, [], seeds, Unit, good, times, heap, out var reached, out _).Should().BeTrue();
        reached.Should().Be(216);

        var distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, 0f, distance, times, heap, out _, out _), distance, "a unit of zero");
        distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, -Unit, distance, times, heap, out _, out _), distance, "a negative unit");
        distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, float.NaN, distance, times, heap, out _, out _), distance, "a unit that is not a number");
        distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, 0.126f, distance, times, heap, out _, out _), distance, "a unit of more than a quarter of a cell");
        distance = Fresh();
        Travel.Build(grid, region, [], seeds, 0.125f, distance, times, heap, out _, out _).Should().BeTrue("a quarter of a cell is allowed");
        distance = Fresh();
        Refused(Travel.Build(grid, region.AsSpan(0, 215), [], seeds, Unit, distance, times, heap, out _, out _), distance, "a short region array");
        distance = Fresh();
        Refused(Travel.Build(grid, region, new byte[215], seeds, Unit, distance, times, heap, out _, out _), distance, "a cost array that is neither empty nor whole");
        distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, Unit, distance, times.AsSpan(0, 215), heap, out _, out _), distance, "short times");
        distance = Fresh();
        Refused(Travel.Build(grid, region, [], seeds, Unit, distance, times, heap.AsSpan(0, 431), out _, out _), distance, "a short heap");
        distance = Fresh();
        Refused(Travel.Build(default, region, [], seeds, Unit, distance, times, heap, out _, out _), distance, "a shape that cannot be used");
        var shorter = Fresh(215);
        Refused(Travel.Build(grid, region, [], seeds, Unit, shorter, times, heap, out _, out _), shorter, "a short distance array");

        // A seed that may not be used, after one that would have started the field.
        var ball = Solid.Sphere(1, 1, 1, 0.5f);
        foreach (var (bad, why) in new[] {
                     (TravelSeed.FromPoint(Solid.Sphere(float.NaN, 1, 1, 1), 1, 1, 1), "a solid that may not be stored"),
                     (TravelSeed.FromPoint(ball, float.NaN, 1, 1), "a point that is not a number"),
                     (TravelSeed.FromPoint(ball, 1, float.PositiveInfinity, 1), "a point that is not finite"),
                     (TravelSeed.FromPlane(ball, 1, 1, 1, 0, 0, 0), "a plane with no normal"),
                     (TravelSeed.FromPlane(ball, 1, 1, 1, float.NaN, 0, 0), "a normal that is not a number"),
                     (TravelSeed.FromPlane(ball, 1, 1, float.NaN, 1, 0, 0), "a plane through a point that is not a number"),
                     (default(TravelSeed), "a seed of nothing: a box with no axes"),
                 }) {
            distance = Fresh();
            Refused(Travel.Build(grid, region, [], [seeds[0], bad], Unit, distance, times, heap, out _, out _), distance, why);
        }

        // Only the grid's own cells are written, refused or not.
        var longer = Fresh(220);
        Travel.Build(grid, region, [], seeds, 0f, longer, times, heap, out _, out _).Should().BeFalse();
        longer.AsSpan(0, 216).ToArray().Should().OnlyContain(value => value == No);
        longer.AsSpan(216).ToArray().Should().OnlyContain(value => value == 7);
        longer = Fresh(220);
        Travel.Build(grid, region, [], seeds, Unit, longer, times, heap, out _, out _).Should().BeTrue();
        longer.AsSpan(216).ToArray().Should().OnlyContain(value => value == 7);
    }

    [Fact]
    public void WorkspaceLengthsFollowTheGrid() {
        var grid = new GridShape(0, 0, 0, 0.5f, 6, 5, 4);
        Travel.TimesLength(grid).Should().Be(120);
        Travel.HeapLength(grid).Should().Be(240);
        Travel.TimesLength(default).Should().Be(0);
        Travel.HeapLength(default).Should().Be(0);
        Travel.HeapLength(new GridShape(0, 0, 0, 0.5f, 1290, 1290, 1290)).Should().Be(0, "two ints per cell is more than an int holds");
    }

    // ---- Sample

    // A field written by hand over a grid of half-metre cells, in centimetres.
    private static Built ByHand(GridShape shape, Func<int, int, int, int> count) {
        var field = new Built(shape, Open(shape), []);
        for (var z = 0; z < shape.CountZ; z++) {
            for (var y = 0; y < shape.CountY; y++) {
                for (var x = 0; x < shape.CountX; x++) { field.Distance[shape.IndexOf(x, y, z)] = (ushort)count(x, y, z); }
            }
        }
        return field;
    }

    [Fact]
    public void AFieldThatRisesEvenlyGivesItsExactSlope() {
        // 50 counts a cell along x, which is 1 m of travel per metre: the way down is -x.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var field = ByHand(grid, (x, _, _) => 50 * x);
        var (found, dx, dy, dz, distance) = field.Sample(2.1f, 1.7f, 2.9f);
        found.Should().BeTrue();
        (dx, dy, dz).Should().Be((-1f, 0f, 0f));
        distance.Should().BeApproximately(2.1f - 0.25f, 1e-6f, "0 at the first centre, at x = 0.25");

        // Rising along all three axes at different rates: the way down is against the slope.
        field = ByHand(grid, (x, y, z) => 30 * x + 40 * y + 120 * z);
        (found, dx, dy, dz, distance) = field.Sample(1.6f, 2.2f, 1.9f);
        found.Should().BeTrue();
        // The slope is (0.6, 0.8, 2.4) per metre, of length 2.6.
        dx.Should().BeApproximately(-0.6f / 2.6f, 1e-6f);
        dy.Should().BeApproximately(-0.8f / 2.6f, 1e-6f);
        dz.Should().BeApproximately(-2.4f / 2.6f, 1e-6f);
        distance.Should().BeApproximately(0.6f * 1.35f + 0.8f * 1.95f + 2.4f * 1.65f, 1e-5f);
        // At a centre it is the cell's own distance.
        field.Sample(1.75f, 1.25f, 0.75f).distance.Should().BeApproximately(0.9f + 0.8f + 1.2f, 1e-6f);
    }

    [Fact]
    public void WhereTheSlopeDoesNotTrulyLeadDownTheWayIsCellByCell() {
        // A field that rises only 5 counts a cell along x: a tenth of a metre per metre. Half a
        // cell down that slope the distance is lower by 2.5 cm, less than the tenth of a cell that
        // counts as truly leading down. The way is then towards the centre of the lowest cell that
        // shares a face with the point's own.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var field = ByHand(grid, (x, _, _) => 5 * x);
        var (found, dx, dy, dz, distance) = field.Sample(2.3f, 1.9f, 1.6f);
        found.Should().BeTrue();
        // The point is in cell (4, 3, 3), whose only lower neighbour is (3, 3, 3), centre 1.75, 1.75, 1.75.
        var (wx, wy, wz) = (1.75 - 2.3, 1.75 - 1.9, 1.75 - 1.6);
        var length = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        dx.Should().BeApproximately((float)(wx / length), 1e-6f);
        dy.Should().BeApproximately((float)(wy / length), 1e-6f);
        dz.Should().BeApproximately((float)(wz / length), 1e-6f);
        distance.Should().BeApproximately(0.05f * (2.3f - 0.25f) / 0.5f, 1e-6f);

        // Rising twice as fast as the least that counts, the slope itself is taken.
        (found, dx, dy, dz, distance) = ByHand(grid, (x, _, _) => 20 * x).Sample(2.3f, 1.9f, 1.6f);
        (found, dx, dy, dz).Should().Be((true, -1f, 0f, 0f));
        distance.Should().BeApproximately(0.4f * (2.3f - 0.25f), 1e-6f);

        // Among lower neighbours the lowest is taken: here the one below in y, not the one below in x.
        var twoWays = ByHand(grid, (x, y, _) => 5 * x + 8 * y + 100);
        (found, dx, dy, dz, _) = twoWays.Sample(2.25f, 1.75f, 1.75f);
        found.Should().BeTrue();
        (dx, dy, dz).Should().Be((0f, -1f, 0f));
    }

    [Fact]
    public void TheCellByCellWayLooksAtAllSixCellsThatShareAFace() {
        // Rising 5 counts a cell, towards each of the six ways in turn: too gentle for the slope to
        // count, so the way is cell by cell, and the one lower cell is on the other side each time.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        (Func<int, int, int, int> count, (float, float, float) way)[] cases = [
            ((x, _, _) => 5 * x, (-1f, 0f, 0f)), ((x, _, _) => 5 * (7 - x), (1f, 0f, 0f)),
            ((_, y, _) => 5 * y, (0f, -1f, 0f)), ((_, y, _) => 5 * (7 - y), (0f, 1f, 0f)),
            ((_, _, z) => 5 * z, (0f, 0f, -1f)), ((_, _, z) => 5 * (7 - z), (0f, 0f, 1f)),
        ];
        foreach (var (count, way) in cases) {
            // From the centre of cell (4, 3, 3).
            var (found, dx, dy, dz, _) = ByHand(grid, count).Sample(2.25f, 1.75f, 1.75f);
            found.Should().BeTrue();
            (dx, dy, dz).Should().Be(way);
        }
    }

    [Fact]
    public void WhereNoneOfTheSixIsLowerTheLowestOfAll26IsTaken() {
        // One cell at 100 counts with nothing reached beside it but a cell across a diagonal, at 60.
        // None of the six that share a face is lower; the way is towards the diagonal one.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var field = ByHand(grid, (x, y, z) => (x, y, z) == (4, 3, 3) ? 100 : (x, y, z) == (3, 2, 3) ? 60 : No);
        var (found, dx, dy, dz, distance) = field.Sample(2.25f, 1.75f, 1.75f);
        found.Should().BeTrue();
        dx.Should().BeApproximately(-0.70710678f, 1e-6f);
        dy.Should().BeApproximately(-0.70710678f, 1e-6f);
        dz.Should().Be(0f);
        distance.Should().Be(1f);
        // With two lower cells across diagonals, the lower of them, whichever comes first.
        field.Distance[grid.IndexOf(5, 4, 4)] = 80;
        (found, dx, dy, dz, _) = field.Sample(2.25f, 1.75f, 1.75f);
        dx.Should().BeApproximately(-0.70710678f, 1e-6f, "60 is lower than 80");
        dy.Should().BeApproximately(-0.70710678f, 1e-6f);
        dz.Should().Be(0f);
        field.Distance[grid.IndexOf(5, 4, 4)] = 30;
        (found, dx, dy, dz, _) = field.Sample(2.25f, 1.75f, 1.75f);
        dx.Should().BeApproximately(0.57735027f, 1e-6f);
        dy.Should().BeApproximately(0.57735027f, 1e-6f);
        dz.Should().BeApproximately(0.57735027f, 1e-6f);
        // A lower cell that shares a face comes first, even if a diagonal one is lower still.
        field.Distance[grid.IndexOf(4, 3, 4)] = 90;
        (found, dx, dy, dz, _) = field.Sample(2.25f, 1.75f, 1.75f);
        (dx, dy, dz).Should().Be((0f, 0f, 1f));
    }

    [Fact]
    public void BesideCellsTheFieldDoesNotReachTheWayLeadsBackAmongThoseItDoes() {
        // A corridor one cell wide along x, at y = 3 and z = 3, rising 50 counts a cell. Every cell
        // beside it is unreached, and counts as farther than any reached cell near it.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var field = ByHand(grid, (x, y, z) => y == 3 && z == 3 ? 50 * x : No);
        // On the corridor's middle line the way is straight down it.
        field.Sample(2.3f, 1.75f, 1.75f).Should().Be((true, -1f, 0f, 0f, 2.05f));
        // Off the middle line it is down the corridor and back towards the line.
        var (found, dx, dy, dz, _) = field.Sample(2.3f, 1.9f, 1.6f);
        found.Should().BeTrue();
        dx.Should().BeLessThan(0f, "down the corridor");
        dy.Should().BeLessThan(0f, "back towards its middle from y = 1.9");
        dz.Should().BeGreaterThan(0f, "and from z = 1.6");
        ((double)dx * dx + (double)dy * dy + (double)dz * dz).Should().BeApproximately(1, 1e-6);
        // From a point in an unreached cell beside it, the same.
        (found, dx, dy, dz, _) = field.Sample(2.25f, 2.2f, 1.75f);
        found.Should().BeTrue();
        dx.Should().BeLessThan(0f);
        dy.Should().BeLessThan(0f);
    }

    [Fact]
    public void WhereTheFieldStartsThereIsNoWayDown() {
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        // A flat field has no way down anywhere: the direction is 0, 0, 0.
        var flat = ByHand(grid, (_, _, _) => 300);
        flat.Sample(1.3f, 2.2f, 3.1f).Should().Be((true, 0f, 0f, 0f, 3f));

        // A real field from one cell, centre 2.25, 2.25, 2.25. A little off that centre the way leads back towards it.
        var field = new Built(grid, Open(grid), [CellSeed(grid, 4, 4, 4)]);
        var (found, dx, dy, dz, distance) = field.Sample(2.4f, 2.35f, 2.15f);
        found.Should().BeTrue();
        dx.Should().BeLessThan(0f);
        dy.Should().BeLessThan(0f);
        dz.Should().BeGreaterThan(0f);
        field.Sample(2.4f + 0.05f * dx, 2.35f + 0.05f * dy, 2.15f + 0.05f * dz).distance.Should().BeLessThan(distance);
        // At the centre itself the distance is 0, which is how a follower knows it has arrived.
        // The direction there is the slope of the cells round it and leads nowhere lower.
        (found, _, _, _, distance) = field.Sample(2.25f, 2.25f, 2.25f);
        found.Should().BeTrue();
        distance.Should().Be(0f);
    }

    [Fact]
    public void AFieldThatStartsFromAPlaneLeadsOnAcrossIt() {
        // An opening in the plane x = 2, seeded with a slab from a little behind the plane to 1.5 m
        // in front of it, each cell starting at its distance in front. The grid goes on behind the
        // plane. Followers come down the field towards the plane, and the way keeps leading the same
        // way until they are across it.
        var grid = new GridShape(1, 0, 0, 0.5f, 14, 8, 8);
        var slab = Solid.Box(new Box(1.7f, 0, 0, 3.5f, 4, 4));
        var field = new Built(grid, Open(grid), [TravelSeed.FromPlane(slab, 2, 0, 0, 1, 0, 0)]);
        field.At(1, 3, 3).Should().Be(0, "the cell just behind the plane");
        field.At(2, 3, 3).Should().Be(25, "the first cell in front: its centre is 0.25 from the plane");
        field.At(4, 3, 3).Should().Be(125);
        field.At(7, 3, 3).Should().Be(275, "beyond the slab the field goes on rising");
        field.At(0, 3, 3).Should().Be(50, "and behind the plane it rises again, away from the cells at 0");
        foreach (var x in new[] { 6.4f, 4.1f, 2.9f, 2.3f, 2.25f, 2.1f, 2f, 1.9f }) {
            var (found, dx, dy, dz, distance) = field.Sample(x, 1.8f, 2.2f);
            found.Should().BeTrue($"at x = {x}");
            dx.Should().BeApproximately(-1f, 1e-5f, $"at x = {x}");
            dy.Should().BeApproximately(0f, 1e-5f);
            dz.Should().BeApproximately(0f, 1e-5f);
            distance.Should().BeGreaterThan(0f);
        }
    }

    [Fact]
    public void ASampleWithNoReachedCellRoundItIsRefused() {
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var field = ByHand(grid, (x, _, _) => x < 3 ? 50 * x : No);
        field.Sample(1.0f, 2, 2).found.Should().BeTrue();
        field.Sample(1.6f, 2, 2).found.Should().BeTrue("one of the eight is reached");
        field.Sample(2.5f, 2, 2).Should().Be((false, 0f, 0f, 0f, 0f), "none of the eight is");
        // Outside the grid: within half a cell of its edge some of the eight are in it.
        field.Sample(-0.2f, 2, 2).found.Should().BeTrue();
        field.Sample(-0.3f, 2, 2).Should().Be((false, 0f, 0f, 0f, 0f));
        field.Sample(1, -40, 2).found.Should().BeFalse();
        field.Sample(1, 2, 1e30f).found.Should().BeFalse();
        field.Sample(float.NaN, 2, 2).found.Should().BeFalse();
        field.Sample(1, float.PositiveInfinity, 2).found.Should().BeFalse();
        // A field that cannot be used.
        Travel.Sample(new TravelField(grid, 0f, field.Distance), 1, 2, 2, out _, out _, out _, out _).Should().BeFalse();
        Travel.Sample(new TravelField(grid, float.NaN, field.Distance), 1, 2, 2, out _, out _, out _, out _).Should().BeFalse();
        // A unit that is not finite would give a travel that is not a number.
        new TravelField(grid, float.PositiveInfinity, field.Distance).IsValid.Should().BeFalse();
        Travel.Sample(new TravelField(grid, float.PositiveInfinity, field.Distance), 1, 2, 2, out _, out _, out _, out var travel).Should().BeFalse();
        travel.Should().Be(0f);
        Travel.Sample(new TravelField(grid, Unit, field.Distance.AsSpan(0, 511)), 1, 2, 2, out _, out _, out _, out _).Should().BeFalse();
        Travel.Sample(default, 1, 2, 2, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void FollowingTheWayDownFromAnywhereArrivesWhereTheFieldStarts() {
        // Two rooms and a pillar, with the field starting in a corner of the first room. From the
        // centre of every reached cell, a follower that moves a fifth of a cell at a time along the
        // sampled direction gets within a cell of the start, without ever standing where none of
        // the eight cells round it is reached.
        var field = new Built(Small, Open(Small, WallAndPillar), [CellSeed(Small, 1, 1, 1)]);
        Small.CentreOf(1, 1, 1, out var goalX, out var goalY, out var goalZ);
        var followed = 0;
        for (var cell = 0; cell < Small.CellCount; cell += 7) {
            if (field.Distance[cell] == No) { continue; }
            Small.CentreOf(cell % 12, cell / 12 % 12, cell / 144, out var x, out var y, out var z);
            var arrived = false;
            for (var step = 0; step < 2000 && !arrived; step++) {
                var (found, dx, dy, dz, _) = field.Sample(x, y, z);
                found.Should().BeTrue($"from cell {cell}, at {x}, {y}, {z}");
                var away = Math.Sqrt(((double)x - goalX) * ((double)x - goalX) + ((double)y - goalY) * ((double)y - goalY) + ((double)z - goalZ) * ((double)z - goalZ));
                if (away < 0.5 || (dx == 0f && dy == 0f && dz == 0f)) {
                    away.Should().BeLessThan(0.5, $"from cell {cell}: no way down at {x}, {y}, {z}");
                    arrived = true;
                } else {
                    x += dx * 0.1f; y += dy * 0.1f; z += dz * 0.1f;
                }
            }
            arrived.Should().BeTrue($"from cell {cell}");
            followed++;
        }
        followed.Should().BeGreaterThan(150);
    }
}
