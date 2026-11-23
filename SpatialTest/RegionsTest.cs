using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class RegionsTest {
    private const float Unit = 1f / 128f;
    private const byte Cap = 128;

    // 16 cells of half a metre each way from the origin: centres at 0.25, 0.75 ... 7.75.
    private static readonly GridShape Grid = new(0, 0, 0, 0.5f, 16, 16, 16);

    // A grid marked from a scene, with the regions of one radius flooded from seeds.
    private sealed class Flooded {
        public readonly GridShape Shape;
        public readonly ShapeScene Scene;
        public readonly byte[] Wall;
        public readonly byte[] Region;
        public readonly SizeClass Size;
        public readonly bool Ok;
        public readonly int Reached, Empty;

        public Flooded(GridShape shape, ShapeScene scene, float radius, Solid[] seeds, byte[]? cost = null) {
            Shape = shape;
            Scene = scene;
            Wall = new byte[shape.CellCount];
            for (var z = 0; z < shape.CountZ; z++) {
                WallDistance.MarkLayer(shape, scene.Set, Unit, Cap, z, Wall.AsSpan(z * shape.LayerLength, shape.LayerLength)).Should().BeTrue();
            }
            var wall = new WallMap(shape, Unit, Cap, Wall);
            SizeClass.TryMake(radius, wall, out Size).Should().BeTrue();
            Region = new byte[shape.CellCount];
            Ok = Regions.FromSeeds(wall, Size, cost ?? [], seeds, Region, new int[Regions.QueueLength(shape)], out Reached, out Empty);
        }

        public byte At(float x, float y, float z) {
            ((int)((x - Shape.OriginX) / Shape.CellSize)).Should().BeInRange(0, Shape.CountX - 1);
            return Region[Shape.IndexOf((int)((x - Shape.OriginX) / Shape.CellSize), (int)((y - Shape.OriginY) / Shape.CellSize), (int)((z - Shape.OriginZ) / Shape.CellSize))];
        }

        public bool IsOpen(float x, float y, float z) => Regions.IsOpen(Shape, Region, x, y, z);
    }

    // A box that covers the whole grid: every open cell is a seed.
    private static readonly Solid Everywhere = Solid.Box(new Box(-1, -1, -1, 9, 9, 9));

    // Two rooms side by side along x, with a wall 0.4 thick between them from x = 3.8 to 4.2. The
    // wall has a doorway from y = 3 to 5 and from the floor to z = 3; it is as wide as given.
    private static ShapeScene TwoRooms(float doorFromY = 3, float doorToY = 5) => new([
        Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f)),
        Solid.Box(new Box(3.8f, -1, 0, 4.2f, doorFromY, 9)),
        Solid.Box(new Box(3.8f, doorToY, 0, 4.2f, 9, 9)),
        Solid.Box(new Box(3.8f, doorFromY, 3, 4.2f, doorToY, 9)),
    ]);

    [Fact]
    public void ACellIsOpenWhenItsCentreIsTheRadiusFromEveryShape() {
        // Against the exact distances, so that the cells cannot agree with themselves by accident.
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere]);
        flood.Ok.Should().BeTrue();
        var open = 0;
        for (var z = 0; z < 16; z++) {
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 16; x++) {
                    float cx = 0.25f + x * 0.5f, cy = 0.25f + y * 0.5f, cz = 0.25f + z * 0.5f;
                    var exact = flood.Scene.Exact(cx, cy, cz);
                    var reached = flood.Region[x + 16 * (y + 16 * z)] != 0;
                    // The class's count is the radius rounded up to a whole count, and a stored
                    // distance is the true one rounded down: between the two a cell may be either.
                    if (exact >= 0.51 + 2 * Unit + 2 * Shapes.Slack) { reached.Should().BeTrue($"the centre {cx}, {cy}, {cz} is {exact} from the nearest shape"); }
                    if (exact < 0.51) { reached.Should().BeFalse($"the centre {cx}, {cy}, {cz} is only {exact} from the nearest shape"); }
                    if (reached) { open++; }
                }
            }
        }
        flood.Reached.Should().Be(open);
        open.Should().BeGreaterThan(1500);
    }

    [Fact]
    public void TwoCellsShareARegionExactlyWhenAPathOfFaceStepsJoinsThem() {
        // A scene with a pocket: a closed box of walls round (6, 6, 6) inside the right-hand room.
        var scene = new ShapeScene([
            Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f)),
            Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)), Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)), Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
            Solid.Box(new Box(4.9f, 4.9f, 4.9f, 5.1f, 7.1f, 7.1f)), Solid.Box(new Box(6.9f, 4.9f, 4.9f, 7.1f, 7.1f, 7.1f)),
            Solid.Box(new Box(4.9f, 4.9f, 4.9f, 7.1f, 5.1f, 7.1f)), Solid.Box(new Box(4.9f, 6.9f, 4.9f, 7.1f, 7.1f, 7.1f)),
            Solid.Box(new Box(4.9f, 4.9f, 4.9f, 7.1f, 7.1f, 5.1f)), Solid.Box(new Box(4.9f, 4.9f, 6.9f, 7.1f, 7.1f, 7.1f)),
        ]);
        var flood = new Flooded(Grid, scene, 0.3f, [Everywhere]);
        flood.Ok.Should().BeTrue();

        // A plain search of its own over the same open cells: a number per cell, joined by face steps.
        var open = new bool[4096];
        for (var i = 0; i < 4096; i++) { open[i] = flood.Wall[i] >= flood.Size.Cell; }
        var group = new int[4096];
        var groups = 0;
        for (var start = 0; start < 4096; start++) {
            if (!open[start] || group[start] != 0) { continue; }
            groups++;
            var pending = new Stack<int>();
            pending.Push(start);
            group[start] = groups;
            while (pending.Count > 0) {
                var cell = pending.Pop();
                int x = cell % 16, y = cell / 16 % 16, z = cell / 256;
                foreach (var (nx, ny, nz) in new[] { (x - 1, y, z), (x + 1, y, z), (x, y - 1, z), (x, y + 1, z), (x, y, z - 1), (x, y, z + 1) }) {
                    if (nx < 0 || nx > 15 || ny < 0 || ny > 15 || nz < 0 || nz > 15) { continue; }
                    var next = nx + 16 * (ny + 16 * nz);
                    if (open[next] && group[next] == 0) { group[next] = groups; pending.Push(next); }
                }
            }
        }
        groups.Should().Be(2, "the two rooms joined by the doorway, and the pocket");
        // The same cells are numbered, and two cells share a number in one exactly when they do in the other.
        var pairs = new Dictionary<int, byte>();
        for (var i = 0; i < 4096; i++) {
            (flood.Region[i] != 0).Should().Be(open[i]);
            if (!open[i]) { continue; }
            if (pairs.TryGetValue(group[i], out var number)) { flood.Region[i].Should().Be(number); } else { pairs[group[i]] = flood.Region[i]; }
        }
        pairs.Values.Distinct().Should().HaveCount(2);
        flood.At(6, 6, 6).Should().NotBe(flood.At(1, 1, 1), "the pocket is its own region");
        flood.At(6, 1, 1).Should().Be(flood.At(1, 1, 1), "through the doorway");
    }

    [Fact]
    public void SeedsThatAreJoinedShareARegionAndTheRestIsLeftAlone() {
        // One seed in each room and a third in the doorway: all three are joined.
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Solid.Sphere(2, 2, 2, 0.6f), Solid.Sphere(6, 6, 2, 0.6f), Solid.Box(new Box(3.5f, 3.6f, 0.8f, 4.5f, 4.4f, 2.2f))]);
        flood.Ok.Should().BeTrue();
        flood.Empty.Should().Be(0);
        flood.At(2, 2, 2).Should().Be(1);
        flood.At(6, 6, 2).Should().Be(1);
        flood.At(4, 4, 1.5f).Should().Be(1);

        // With the doorway narrowed to 0.9 m no cell in it is open, and the rooms are two regions.
        flood = new Flooded(Grid, TwoRooms(3.55f, 4.45f), 0.51f, [Solid.Sphere(2, 2, 2, 0.6f), Solid.Sphere(6, 6, 2, 0.6f)]);
        flood.Ok.Should().BeTrue();
        flood.At(2, 2, 2).Should().Be(1);
        flood.At(6, 6, 2).Should().Be(2);

        // Seeded in one room only, the other is open but not reached.
        flood = new Flooded(Grid, TwoRooms(3.55f, 4.45f), 0.51f, [Solid.Sphere(2, 2, 2, 0.6f)]);
        flood.At(2, 2, 2).Should().Be(1);
        flood.At(6, 6, 2).Should().Be(0);
        flood.IsOpen(6, 6, 2).Should().BeFalse("open for the class, but no seed reaches it");
        (flood.Wall[Grid.IndexOf(12, 12, 4)] >= flood.Size.Cell).Should().BeTrue();
    }

    [Fact]
    public void RoomsThatTouchOnlyAtACornerAreTwoRegions() {
        // The flood reads only the wall map, so this one is written by hand: cells of one metre,
        // open where the map holds the cap. Two blocks of four cells, at (1..2, 1..2) and
        // (3..4, 3..4), whose nearest cells (2, 2) and (3, 3) are diagonal to each other.
        var grid = new GridShape(0, 0, 0, 1f, 6, 6, 3);
        var wall = new byte[grid.CellCount];
        foreach (var (x, y) in new[] { (1, 1), (2, 1), (1, 2), (2, 2), (3, 3), (4, 3), (3, 4), (4, 4) }) { wall[grid.IndexOf(x, y, 1)] = Cap; }
        // And two single cells that meet only at one corner point, across a cube's diagonal.
        wall[grid.IndexOf(4, 0, 0)] = Cap;
        wall[grid.IndexOf(5, 1, 1)] = Cap;
        var map = new WallMap(grid, Unit, Cap, wall);
        SizeClass.TryMake(0.6f, map, out var size).Should().BeTrue();
        var region = new byte[grid.CellCount];
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-1, -1, -1, 7, 7, 4))], region, new int[grid.CellCount], out var reached, out _).Should().BeTrue();
        reached.Should().Be(10);
        region[grid.IndexOf(2, 2, 1)].Should().NotBe(0);
        region[grid.IndexOf(1, 1, 1)].Should().Be(region[grid.IndexOf(2, 2, 1)], "one block");
        region[grid.IndexOf(4, 4, 1)].Should().Be(region[grid.IndexOf(3, 3, 1)], "the other block");
        region[grid.IndexOf(3, 3, 1)].Should().NotBe(region[grid.IndexOf(2, 2, 1)], "a step across a square's diagonal is never taken");
        region[grid.IndexOf(5, 1, 1)].Should().NotBe(region[grid.IndexOf(4, 0, 0)], "nor one across a cube's diagonal");
        region.Distinct().Should().HaveCount(5, "closed, and four regions");

        // Seeded in one of each pair only, the other is not reached, whichever of the two is seeded:
        // so no diagonal step is taken in either direction.
        (Solid seed, (int x, int y, int z) reached, (int x, int y, int z) notReached)[] cases = [
            (Solid.Sphere(1.5f, 1.5f, 1.5f, 0.2f), (2, 2, 1), (3, 3, 1)),
            (Solid.Sphere(4.5f, 4.5f, 1.5f, 0.2f), (3, 3, 1), (2, 2, 1)),
            (Solid.Sphere(4.5f, 0.5f, 0.5f, 0.2f), (4, 0, 0), (5, 1, 1)),
            (Solid.Sphere(5.5f, 1.5f, 1.5f, 0.2f), (5, 1, 1), (4, 0, 0)),
        ];
        foreach (var (seed, into, notInto) in cases) {
            Regions.FromSeeds(map, size, [], [seed], region, new int[grid.CellCount], out _, out var empty).Should().BeTrue();
            empty.Should().Be(0);
            region[grid.IndexOf(into.x, into.y, into.z)].Should().Be(1);
            region[grid.IndexOf(notInto.x, notInto.y, notInto.z)].Should().Be(0);
        }
    }

    // A wall map written by hand on a grid of one-metre cells: open where it holds the cap.
    private static (GridShape grid, SizeClass size, byte[] wall) ByHand(int countX, int countY, int countZ, params (int x, int y, int z)[] open) {
        var grid = new GridShape(0, 0, 0, 1f, countX, countY, countZ);
        var wall = new byte[grid.CellCount];
        foreach (var (x, y, z) in open) { wall[grid.IndexOf(x, y, z)] = Cap; }
        SizeClass.TryMake(0.6f, new WallMap(grid, Unit, Cap, wall), out var size).Should().BeTrue();
        return (grid, size, wall);
    }

    // A small ball round a cell's centre: a seed for that cell alone.
    private static Solid SeedAt(int x, int y, int z) => Solid.Sphere(x + 0.5f, y + 0.5f, z + 0.5f, 0.2f);

    [Fact]
    public void TheFloodStepsEveryWayAlongEachAxis() {
        // Three lines of open cells through the middle of a grid of five each way, one along each
        // axis: thirteen cells. Seeded at either end of any of the three, every one is reached, so
        // the flood steps both ways along x, along y and along z.
        var open = new List<(int, int, int)>();
        for (var i = 0; i < 5; i++) { open.Add((i, 2, 2)); open.Add((2, i, 2)); open.Add((2, 2, i)); }
        var (grid, size, wall) = ByHand(5, 5, 5, open.Distinct().ToArray());
        var region = new byte[grid.CellCount];
        foreach (var (x, y, z) in new[] { (0, 2, 2), (4, 2, 2), (2, 0, 2), (2, 4, 2), (2, 2, 0), (2, 2, 4) }) {
            Regions.FromSeeds(new WallMap(grid, Unit, Cap, wall), size, [], [SeedAt(x, y, z)], region, new int[grid.CellCount], out var reached, out var empty).Should().BeTrue();
            reached.Should().Be(13, $"seeded at {x}, {y}, {z}");
            empty.Should().Be(0);
            for (var i = 0; i < grid.CellCount; i++) { region[i].Should().Be(wall[i] == Cap ? (byte)1 : (byte)0); }
        }
    }

    [Fact]
    public void AStepNeverRunsOnFromTheEndOfARowOrALayerIntoTheNext() {
        // In the array the last cell of one row is followed by the first of the next, and the last
        // row of one layer by the first row of the next. Those pairs are far apart in the grid, and
        // a step must not join them, from either one.
        ((int x, int y, int z) a, (int x, int y, int z) b)[] pairs = [
            ((2, 0, 0), (0, 1, 0)),   // along x: the end of a row and the start of the next
            ((2, 2, 0), (0, 0, 1)),   // along x: the end of a layer's last row and the start of the next layer
            ((1, 2, 0), (1, 0, 1)),   // along y: a cell of a layer's last row and the same cell of the next layer's first
        ];
        foreach (var (a, b) in pairs) {
            var (grid, size, wall) = ByHand(3, 3, 3, a, b);
            var region = new byte[grid.CellCount];
            foreach (var (seeded, other) in new[] { (a, b), (b, a) }) {
                Regions.FromSeeds(new WallMap(grid, Unit, Cap, wall), size, [], [SeedAt(seeded.x, seeded.y, seeded.z)], region, new int[grid.CellCount], out var reached, out _).Should().BeTrue();
                reached.Should().Be(1, $"seeded at {seeded}");
                region[grid.IndexOf(seeded.x, seeded.y, seeded.z)].Should().Be(1);
                region[grid.IndexOf(other.x, other.y, other.z)].Should().Be(0, $"{other} is not next to {seeded}");
            }
        }
    }

    [Fact]
    public void ACellIsOpenFromExactlyTheClasssCount() {
        // Written by hand: a row of cells that hold one count less than the class needs, exactly
        // what it needs, and one more.
        var grid = new GridShape(0, 0, 0, 0.5f, 5, 1, 1);
        var map = new WallMap(grid, Unit, Cap, default);
        SizeClass.TryMake(0.51f, map, out var size).Should().BeTrue();
        size.Cell.Should().Be(66);
        byte[] wall = [65, 66, 67, 65, 66];
        var region = new byte[5];
        Regions.FromSeeds(new WallMap(grid, Unit, Cap, wall), size, [], [Solid.Box(new Box(-1, -1, -1, 4, 2, 2))], region, new int[5], out var reached, out _).Should().BeTrue();
        region.Should().Equal(0, 1, 1, 0, 2);
        reached.Should().Be(3);
    }

    [Fact]
    public void ASeedReachesOnlyFromCellsWhoseCentresAreInsideIt() {
        // Two open cells of one metre, at (1, 1, 1) and (3, 3, 1), with nothing open between them.
        // A ball round the first one's centre has a box that holds the second one's centre, but the
        // ball itself does not: the second is not seeded.
        var grid = new GridShape(0, 0, 0, 1f, 5, 5, 3);
        var wall = new byte[grid.CellCount];
        wall[grid.IndexOf(1, 1, 1)] = Cap;
        wall[grid.IndexOf(3, 3, 1)] = Cap;
        var map = new WallMap(grid, Unit, Cap, wall);
        SizeClass.TryMake(0.6f, map, out var size).Should().BeTrue();
        var region = new byte[grid.CellCount];
        Regions.FromSeeds(map, size, [], [Solid.Sphere(1.5f, 1.5f, 1.5f, 2.2f)], region, new int[grid.CellCount], out var reached, out _).Should().BeTrue();
        reached.Should().Be(1);
        region[grid.IndexOf(1, 1, 1)].Should().Be(1);
        region[grid.IndexOf(3, 3, 1)].Should().Be(0);
    }

    [Fact]
    public void ASheetWithNoThicknessIsNeverCrossed() {
        // A sheet of two triangles across the grid at x = 4.1, with nothing else in it. Cells whose
        // centres are 0.3 or more from it are open on both sides, and the two sides never join,
        // though the sheet lies between two columns of centres that are only half a metre apart.
        var scene = new ShapeScene([], [4.1f, -1, -1, 4.1f, 9, -1, 4.1f, 9, 9, 4.1f, -1, 9], [0, 1, 2, 0, 2, 3]);
        var flood = new Flooded(Grid, scene, 0.3f, [Everywhere]);
        flood.Ok.Should().BeTrue();
        flood.At(3.25f, 4, 4).Should().NotBe(0);
        flood.At(4.75f, 4, 4).Should().NotBe(0);
        flood.At(3.25f, 4, 4).Should().NotBe(flood.At(4.75f, 4, 4));
        flood.At(3.75f, 4, 4).Should().NotBe(0, "0.35 from the sheet");
        flood.At(4.25f, 4, 4).Should().Be(0, "0.15 from the sheet");
        flood.Region.Distinct().Should().HaveCount(3, "closed, one side, the other side");
    }

    [Fact]
    public void ACellClosedByCostSplitsARegion() {
        // The doorway's cells are closed: the two rooms are two regions, and the closed cells are in neither.
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, Solid.Box(new Box(3.6f, 2.5f, 0, 4.4f, 5.5f, 4)), CellCost.Closed, cost).Should().BeTrue();
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere], cost);
        flood.Ok.Should().BeTrue();
        flood.At(2, 2, 2).Should().NotBe(0);
        flood.At(6, 6, 2).Should().NotBe(0);
        flood.At(2, 2, 2).Should().NotBe(flood.At(6, 6, 2));
        flood.At(4.1f, 4, 1.5f).Should().Be(0, "closed");
        flood.IsOpen(4.1f, 4, 1.5f).Should().BeFalse();

        // A cost that is not Closed changes nothing, and an empty cost array is all normal.
        Array.Fill(cost, (byte)254);
        var costly = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere], cost);
        var plain = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere]);
        costly.Region.Should().Equal(plain.Region);
        plain.At(2, 2, 2).Should().Be(plain.At(6, 6, 2));
    }

    [Fact]
    public void ASeedWithNoOpenCellIsCountedAndReachesNothing() {
        // The first seed is inside the wall between the rooms; the second holds no cell's centre at all.
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Solid.Box(new Box(3.85f, 0, 4, 4.15f, 2, 6)), Solid.Sphere(2.1f, 2.1f, 2.1f, 0.05f), Solid.Sphere(20, 20, 20, 1)]);
        flood.Ok.Should().BeTrue();
        flood.Empty.Should().Be(3);
        flood.Reached.Should().Be(0);
        flood.Region.Should().OnlyContain(value => value == 0);

        // No seeds at all reach nothing either.
        flood = new Flooded(Grid, TwoRooms(), 0.51f, []);
        flood.Ok.Should().BeTrue();
        flood.Reached.Should().Be(0);
        flood.Empty.Should().Be(0);
    }

    [Fact]
    public void MoreRegionsThanAByteHoldsAreRefused() {
        // A grid of 32 by 32 by 1 cells of one metre, with a wall along every other row and column:
        // 256 single cells, each its own region.
        var grid = new GridShape(0, 0, 0, 1f, 32, 32, 1);
        var wall = new byte[grid.CellCount];
        for (var y = 0; y < 32; y += 2) {
            for (var x = 0; x < 32; x += 2) { wall[x + 32 * y] = Cap; }
        }
        var map = new WallMap(grid, Unit, Cap, wall);
        SizeClass.TryMake(0.6f, map, out var size).Should().BeTrue();
        var region = new byte[grid.CellCount];
        region.AsSpan().Fill(9);
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-1, -1, -1, 33, 33, 2))], region, new int[grid.CellCount], out var reached, out var empty).Should().BeFalse();
        region.Should().OnlyContain(value => value == 0, "a refused flood says no cell is open");
        (reached, empty).Should().Be((0, 0));

        // One fewer is still one too many: a 255th region has no number.
        wall[0] = 0;
        region.AsSpan().Fill(9);
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-1, -1, -1, 33, 33, 2))], region, new int[grid.CellCount], out _, out _).Should().BeFalse();
        region.Should().OnlyContain(value => value == 0);

        // Two fewer and it fits: 254 regions, numbered 1 to 254.
        wall[2] = 0;
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-1, -1, -1, 33, 33, 2))], region, new int[grid.CellCount], out reached, out _).Should().BeTrue();
        reached.Should().Be(254);
        region.Max().Should().Be(254);
    }

    [Fact]
    public void ARefusedFloodLeavesNoCellOpen() {
        var scene = TwoRooms();
        var wall = new byte[Grid.CellCount];
        for (var z = 0; z < 16; z++) { WallDistance.MarkLayer(Grid, scene.Set, Unit, Cap, z, wall.AsSpan(z * 256, 256)).Should().BeTrue(); }
        var map = new WallMap(Grid, Unit, Cap, wall);
        SizeClass.TryMake(0.51f, map, out var size).Should().BeTrue();
        Solid[] seeds = [Everywhere];
        var queue = new int[Regions.QueueLength(Grid)];

        byte[] Fresh() { var region = new byte[Grid.CellCount]; region.AsSpan().Fill(9); return region; }
        void Refused(bool result, byte[] region, string why) {
            result.Should().BeFalse(why);
            region.Should().OnlyContain(value => value == 0, why);
        }

        var good = Fresh();
        Regions.FromSeeds(map, size, [], seeds, good, queue, out var reached, out _).Should().BeTrue();
        reached.Should().BeGreaterThan(1000);

        var region = Fresh();
        Refused(Regions.FromSeeds(map, size, [], seeds, region, queue.AsSpan(0, queue.Length - 1), out _, out _), region, "a short queue");
        region = Fresh();
        Refused(Regions.FromSeeds(map, size, new byte[Grid.CellCount - 1], seeds, region, queue, out _, out _), region, "a cost array that is neither empty nor whole");
        region = Fresh();
        Refused(Regions.FromSeeds(map, default, [], seeds, region, queue, out _, out _), region, "no size class");
        region = Fresh();
        Refused(Regions.FromSeeds(new WallMap(Grid, Unit, Cap, wall.AsSpan(0, wall.Length - 1)), size, [], seeds, region, queue, out _, out _), region, "a short wall map");
        region = Fresh();
        Refused(Regions.FromSeeds(new WallMap(Grid, 1f / 64f, Cap, wall), size, [], seeds, region, queue, out _, out _), region, "a class made for another unit");
        region = Fresh();
        Refused(Regions.FromSeeds(new WallMap(new GridShape(0, 0, 0, 0.25f, 16, 16, 16), Unit, Cap, wall), size, [], seeds, region, queue, out _, out _), region, "a class made for another cell size");
        region = Fresh();
        Refused(Regions.FromSeeds(map, size, [], [Everywhere, Solid.Sphere(float.NaN, 0, 0, 1)], region, queue, out _, out _), region, "a seed that may not be stored, after one that reached cells");
        var shortRegion = new byte[Grid.CellCount - 1];
        shortRegion.AsSpan().Fill(9);
        Refused(Regions.FromSeeds(map, size, [], seeds, shortRegion, queue, out _, out _), shortRegion, "a short region array");

        // Only the grid's own cells are written, refused or not: what follows them in a longer array is left.
        var longer = new byte[Grid.CellCount + 4];
        longer.AsSpan().Fill(9);
        Regions.FromSeeds(map, size, [], seeds, longer, queue.AsSpan(0, queue.Length - 1), out _, out _).Should().BeFalse("refused at once");
        longer.AsSpan(0, Grid.CellCount).ToArray().Should().OnlyContain(value => value == 0);
        longer.AsSpan(Grid.CellCount).ToArray().Should().OnlyContain(value => value == 9);
        longer.AsSpan().Fill(9);
        Regions.FromSeeds(map, size, [], [Everywhere, Solid.Sphere(float.NaN, 0, 0, 1)], longer, queue, out _, out _).Should().BeFalse("refused part way");
        longer.AsSpan(0, Grid.CellCount).ToArray().Should().OnlyContain(value => value == 0);
        longer.AsSpan(Grid.CellCount).ToArray().Should().OnlyContain(value => value == 9);
        longer.AsSpan().Fill(9);
        Regions.FromSeeds(map, size, [], seeds, longer, queue, out _, out _).Should().BeTrue();
        longer.AsSpan(Grid.CellCount).ToArray().Should().OnlyContain(value => value == 9);
    }

    [Fact]
    public void IsOpenAnswersForTheCellThatHoldsThePoint() {
        var flood = new Flooded(Grid, TwoRooms(3.55f, 4.45f), 0.51f, [Solid.Sphere(2, 2, 2, 0.6f)]);
        flood.IsOpen(2, 2, 2).Should().BeTrue("open and reached");
        flood.IsOpen(2.2f, 2.4f, 2.1f).Should().BeTrue("the same cell");
        flood.IsOpen(2, 2, 0.3f).Should().BeFalse("too near the floor");
        flood.IsOpen(4, 1, 5).Should().BeFalse("inside the wall");
        flood.IsOpen(6, 6, 2).Should().BeFalse("open, but no seed reached it");
        flood.IsOpen(-0.1f, 2, 2).Should().BeFalse("outside the grid");
        flood.IsOpen(8, 2, 2).Should().BeFalse("on an upper face, which is in no cell");
        flood.IsOpen(float.NaN, 2, 2).Should().BeFalse();
        Regions.IsOpen(Grid, flood.Region.AsSpan(0, 4095), 2, 2, 2).Should().BeFalse("a short region array");
        Regions.IsOpen(default, flood.Region, 2, 2, 2).Should().BeFalse();

        // With nothing in the grid every cell is open, the first one too: a point outside is still not.
        var empty = new Flooded(Grid, new ShapeScene([]), 0.51f, [Everywhere]);
        empty.Region[0].Should().NotBe(0);
        empty.IsOpen(0.1f, 0.1f, 0.1f).Should().BeTrue();
        empty.IsOpen(-0.1f, 0.1f, 0.1f).Should().BeFalse("outside the grid");
        empty.IsOpen(0.1f, -5, 0.1f).Should().BeFalse();
        empty.IsOpen(0.1f, 0.1f, 8).Should().BeFalse("on an upper face");
        empty.IsOpen(0.1f, float.NaN, 0.1f).Should().BeFalse();
    }

    [Fact]
    public void ItIsTheCellThatIsOpenNotThePoint() {
        // A wall whose face is at x = 4.2. The cells with centres at x = 4.75 are open for a radius
        // of 0.51, being 0.55 from it. A point in such a cell can be 0.3 from the wall: its cell is
        // open, and a body of that radius does not fit at the point itself.
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere]);
        flood.IsOpen(4.75f, 1.25f, 5.25f).Should().BeTrue();
        flood.IsOpen(4.5f, 1.25f, 5.25f).Should().BeTrue("the same cell");
        flood.Scene.Exact(4.5, 1.25, 5.25).Should().BeApproximately(0.3, 1e-6);
    }

    private static (float x, float y, float z)? Nearest(Flooded flood, float x, float y, float z, float reach) =>
        Regions.NearestOpen(flood.Shape, flood.Region, x, y, z, reach, out var ox, out var oy, out var oz) ? (ox, oy, oz) : null;

    private static (float x, float y, float z)? NearestLike(Flooded flood, float x, float y, float z, float reach, float lx, float ly, float lz) =>
        Regions.NearestOpen(flood.Shape, flood.Region, x, y, z, reach, lx, ly, lz, out var ox, out var oy, out var oz) ? (ox, oy, oz) : null;

    [Fact]
    public void TheNearestOpenPointIsACentreByStraightLine() {
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere]);
        // From an open cell: its own centre.
        Nearest(flood, 2.1f, 2.2f, 2.4f, 3f).Should().Be((2.25f, 2.25f, 2.25f));
        // From the floor: the first open layer has its centres at z = 0.75, since 0.25 is too near the floor's top at 0.2.
        Nearest(flood, 2.3f, 2.3f, 0.2f, 3f).Should().Be((2.25f, 2.25f, 0.75f));
        // From inside the wall between the rooms, nearer its left face.
        Nearest(flood, 3.9f, 1.3f, 5.3f, 3f).Should().Be((3.25f, 1.25f, 5.25f));
        // From outside the grid.
        Nearest(flood, -1f, 2.3f, 2.3f, 3f).Should().Be((0.25f, 2.25f, 2.25f));
        // It agrees with a loop over every cell, from a spread of points.
        for (var i = 0; i < 6; i++) {
            for (var j = 0; j < 6; j++) {
                for (var k = 0; k < 6; k++) {
                    float x = -0.5f + i * 1.63f, y = -0.5f + j * 1.57f, z = -0.5f + k * 1.71f;
                    (float x, float y, float z)? best = null;
                    var bestSquared = double.MaxValue;
                    for (var cx = 0; cx < 16; cx++) {
                        for (var cy = 0; cy < 16; cy++) {
                            for (var cz = 0; cz < 16; cz++) {
                                if (flood.Region[cx + 16 * (cy + 16 * cz)] == 0) { continue; }
                                float px = 0.25f + cx * 0.5f, py = 0.25f + cy * 0.5f, pz = 0.25f + cz * 0.5f;
                                var squared = ((double)px - x) * ((double)px - x) + ((double)py - y) * ((double)py - y) + ((double)pz - z) * ((double)pz - z);
                                if (squared < bestSquared && squared <= 2.5 * 2.5) { bestSquared = squared; best = (px, py, pz); }
                            }
                        }
                    }
                    Nearest(flood, x, y, z, 2.5f).Should().Be(best, $"from {x}, {y}, {z}");
                }
            }
        }
    }

    [Fact]
    public void AmongCentresEquallyNearTheLowestInXThenYThenZ() {
        var flood = new Flooded(Grid, new ShapeScene([]), 0.51f, [Everywhere]);
        // Midway between eight centres.
        Nearest(flood, 4, 4, 4, 2f).Should().Be((3.75f, 3.75f, 3.75f));
        // Midway between two along x, then along y, then along z.
        Nearest(flood, 4, 4.25f, 4.25f, 2f).Should().Be((3.75f, 4.25f, 4.25f));
        Nearest(flood, 4.25f, 4, 4.25f, 2f).Should().Be((4.25f, 3.75f, 4.25f));
        Nearest(flood, 4.25f, 4.25f, 4, 2f).Should().Be((4.25f, 4.25f, 3.75f));
    }

    [Fact]
    public void TheReachIsALimitAndACentreExactlyAtItCounts() {
        var flood = new Flooded(Grid, TwoRooms(), 0.51f, [Everywhere]);
        // On the floor's top at z = 0.2... the nearest open centre is 0.75 - 0.2 above a point under it.
        Nearest(flood, 2.25f, 2.25f, 0.25f, 0.5f).Should().Be((2.25f, 2.25f, 0.75f), "exactly half a metre away");
        Nearest(flood, 2.25f, 2.25f, 0.25f, 0.49f).Should().BeNull();
        Nearest(flood, 2.25f, 2.25f, 2.25f, 0f).Should().Be((2.25f, 2.25f, 2.25f), "a reach of nothing still finds the point's own centre");
        Nearest(flood, 2.3f, 2.25f, 2.25f, 0f).Should().BeNull();
        Nearest(flood, 2.25f, 2.25f, 2.25f, -1f).Should().BeNull("a negative reach");
        Nearest(flood, 2.25f, 2.25f, 2.25f, float.NaN).Should().BeNull();
        Nearest(flood, float.NaN, 2.25f, 2.25f, 3f).Should().BeNull();
        Nearest(flood, 2.25f, 2.25f, 2.25f, float.PositiveInfinity).Should().Be((2.25f, 2.25f, 2.25f), "a reach without end is the whole grid");
        Regions.NearestOpen(Grid, flood.Region.AsSpan(0, 4095), 2, 2, 2, 3f, out _, out _, out _).Should().BeFalse("a short region array");
        Regions.NearestOpen(default, flood.Region, 2, 2, 2, 3f, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TheNearestOpenPointInTheSameRegionAsAnotherPassesOverANearerPocket() {
        // Two regions: the rooms, with the doorway too narrow to join them.
        var flood = new Flooded(Grid, TwoRooms(3.55f, 4.45f), 0.51f, [Solid.Sphere(2, 2, 2, 0.6f), Solid.Sphere(6, 6, 2, 0.6f)]);
        // A point inside the wall, nearer the right-hand room.
        Nearest(flood, 4.15f, 1.25f, 5.25f, 3f).Should().Be((4.75f, 1.25f, 5.25f));
        // Asked for the left-hand room's region, by a point in it.
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 3f, 2, 2, 2).Should().Be((3.25f, 1.25f, 5.25f));
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 3f, 6, 6, 2).Should().Be((4.75f, 1.25f, 5.25f));
        // Out of reach in the wanted region, though the other is in reach.
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 0.7f, 2, 2, 2).Should().BeNull();
        // The other point must itself be in an open cell that a seed reached.
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 3f, 4, 1, 5).Should().BeNull("the other point is inside the wall");
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 3f, -1, 2, 2).Should().BeNull("the other point is outside the grid");
        NearestLike(flood, 4.15f, 1.25f, 5.25f, 3f, float.NaN, 2, 2).Should().BeNull();
    }

    [Fact]
    public void NearestIsByStraightLineEvenThroughAWall() {
        // A sheet with no thickness at x = 4.1, with a way round it: one region on both sides. From
        // a point just left of the sheet, the nearest open centre is on its right.
        var scene = new ShapeScene([], [4.1f, -1, -1, 4.1f, 6, -1, 4.1f, 6, 9, 4.1f, -1, 9], [0, 1, 2, 0, 2, 3]);
        var flood = new Flooded(Grid, scene, 0.3f, [Everywhere]);
        flood.At(3.25f, 2, 4).Should().Be(flood.At(4.75f, 2, 4), "joined round the end of the sheet");
        // The centres at x = 3.75 are 0.35 from the sheet and open; those at 4.25 are 0.15 and closed.
        Nearest(flood, 4.05f, 2.25f, 4.25f, 2f).Should().Be((3.75f, 2.25f, 4.25f), "the near side, when it is nearer");
        Nearest(flood, 4.3f, 2.25f, 4.25f, 2f).Should().Be((4.75f, 2.25f, 4.25f));
        // From a point left of the sheet whose own side is farther off: nearest is across it.
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, Solid.Box(new Box(3.3f, 1.9f, 3.9f, 4, 2.6f, 4.6f)), CellCost.Closed, cost).Should().BeTrue();
        var closed = new Flooded(Grid, scene, 0.3f, [Everywhere], cost);
        closed.IsOpen(3.75f, 2.25f, 4.25f).Should().BeFalse("closed by cost");
        Nearest(closed, 4.05f, 2.25f, 4.25f, 2f).Should().Be((3.75f, 1.75f, 4.25f), "still the near side: half a cell along it");
        NearestLike(closed, 4.3f, 2.25f, 4.25f, 2f, 3.25f, 2.25f, 4.25f).Should().Be((4.75f, 2.25f, 4.25f), "across the sheet from the point given as like, since both sides are one region");
    }
}
