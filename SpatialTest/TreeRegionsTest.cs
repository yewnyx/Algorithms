using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class TreeRegionsTest {
    private const float Radius = 0.51f;

    // What a flood over a tree came to, with the bytes cut to what it used.
    private sealed record Flooded(byte[] Region, int Reached, int Empty, int Kept);

    private static SizeClass Class(TreeScene scene, float radius = Radius) {
        SizeClass.TryMake(radius, scene.Tree, out var size).Should().BeTrue();
        return size;
    }

    // Floods the tree and the dense map from the same seeds. The two must reach the same number
    // of cells, count the same seeds as empty, and say the same of every cell of the grid.
    private static Flooded BothFloods(TreeScene scene, params Solid[] seeds) => BothFloods(scene, Radius, seeds);

    private static Flooded BothFloods(TreeScene scene, float radius, params Solid[] seeds) {
        var tree = scene.Tree;
        var size = Class(scene, radius);
        var dense = new byte[scene.Grid.CellCount];
        Regions.FromSeeds(scene.Map, size, default, seeds, dense, new int[Regions.QueueLength(scene.Grid)], out var denseReached, out var denseEmpty).Should().BeTrue();

        var region = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, seeds, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)],
            out var length, out var reached, out var empty).Should().BeTrue();
        (reached, empty).Should().Be((denseReached, denseEmpty));
        region = region[..length];
        for (var z = 0; z < scene.Grid.CountZ; z++) {
            for (var y = 0; y < scene.Grid.CountY; y++) {
                for (var x = 0; x < scene.Grid.CountX; x++) {
                    scene.Grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    if (TreeRegions.IsOpen(tree, region, cx, cy, cz) != (dense[scene.Grid.IndexOf(x, y, z)] != 0)) {
                        throw new Xunit.Sdk.XunitException($"Cell {x}, {y}, {z} has region {dense[scene.Grid.IndexOf(x, y, z)]} in the dense flood, and the tree says otherwise.");
                    }
                }
            }
        }
        return new Flooded(region, reached, empty, (length - TreeRegions.LeastLength(tree)) / 68);
    }

    private static readonly Solid[] Mixed = [
        Solid.Box(new Box(-1, -1, -1, 20, 0.3f, 20)),
        ShapeScene.TurnedBox(6, 3, 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(11, 4.5f, 8, 1.6f),
        Solid.Capsule(3, 1, 9, 14, 7, 2, 0.35f),
        Solid.Box(new Box(15.5f, 0, 0, 30, 30, 30)),
    ];

    // 37 by 22 by 29 cells: no side a multiple of four, so the last bricks along each axis are only partly in the grid.
    private static readonly GridShape Odd = new(-0.3f, -0.7f, 0.2f, 0.5f, 37, 22, 29);

    // A grid of 20 by 5 by 10 m cut in two by a sheet across x at 9.1 m: a sheet takes no cell, and
    // the cells within the radius of it are closed, so open cells of both halves lie in one brick.
    private static readonly GridShape Halved = new(0, 0, 0, 0.5f, 40, 10, 20);
    private static TreeScene HalvedScene() =>
        new(Halved, new ShapeScene([], [9.1f, -5, -5, 9.1f, 30, -5, 9.1f, 30, 30, 9.1f, -5, 30], [0, 1, 2, 0, 2, 3]).Set);

    [Fact]
    public void AClassForATreeIsTheClassForTheDenseMap() {
        var scene = new TreeScene(Odd, new ShapeScene(Mixed).Set);
        SizeClass.TryMake(Radius, scene.Tree, out var forTree).Should().BeTrue();
        SizeClass.TryMake(Radius, scene.Map, out var forMap).Should().BeTrue();
        forTree.Should().Be(forMap);
        forTree.Fits(scene.Tree).Should().BeTrue();
        default(SizeClass).Fits(scene.Tree).Should().BeFalse();
        forTree.Fits(new WallTree(Odd, TreeScene.Unit, forTree.Cap, scene.Nodes, scene.Bricks)).Should().BeTrue("the least cap it can do with");
        forTree.Fits(new WallTree(Odd, TreeScene.Unit, (byte)(forTree.Cap - 1), scene.Nodes, scene.Bricks)).Should().BeFalse("a cap too low for it");
        forTree.Fits(new WallTree(Odd, 1f / 64f, TreeScene.Cap, scene.Nodes, scene.Bricks)).Should().BeFalse("another unit");
        SizeClass.TryMake(0.25f, scene.Tree, out _).Should().BeFalse("half a cell or less");
        SizeClass.TryMake(Radius, new WallTree(Odd, TreeScene.Unit, 60, scene.Nodes, scene.Bricks), out _).Should().BeFalse("the cap is too low for it");
    }

    [Fact]
    public void AFloodOverATreeReachesTheCellsTheDenseFloodReaches() {
        var scene = new TreeScene(Odd, new ShapeScene(Mixed, [9, 0, 3, 9, 8, 3, 9, 8, 11, 9, 0, 11, 0, 7.5f, 1, 16, 6.5f, 2, 3, 2.5f, 11.5f], [0, 1, 2, 0, 2, 3, 4, 5, 6]).Set);
        var flooded = BothFloods(scene, Solid.Sphere(3, 6, 6, 0.6f));
        flooded.Reached.Should().BeGreaterThan(5000);
        flooded.Empty.Should().Be(0);
        // The same from a seed that holds every cell: the regions that seed finds one after another.
        BothFloods(scene, Solid.Box(new Box(-5, -5, -5, 30, 30, 30))).Reached.Should().BeGreaterThanOrEqualTo(flooded.Reached);
        // A seed inside a shape holds no open cell, and one that has an open cell of a region already made adds nothing.
        BothFloods(scene, Solid.Sphere(3, 6, 6, 0.6f), Solid.Sphere(11, 4.5f, 8, 0.5f), Solid.Sphere(4, 7, 6, 0.6f)).Should()
            .BeEquivalentTo(new { flooded.Reached, Empty = 1 });
    }

    [Fact]
    public void AFloodOverGroundStaysAboveIt() {
        // Rolling ground with a wall across it and a sphere half in it. Under the ground every cell holds 0.
        var scene = new TreeScene(new GridShape(-1, -1.75f, -1, 0.5f, 24, 14, 16),
            new HeightScene((u, w) => 1.5 + 0.4 * Math.Sin(0.9 * u) * Math.Cos(0.7 * w) + (u > 6.2 ? 1.5 : 0), 20, 12, 0.5f, 0.5f, 0.25f,
                solids: [Solid.Sphere(3, 2.2f, 3, 0.8f)]).Set);
        var flooded = BothFloods(scene, Solid.Sphere(2, 4, 2, 0.4f));
        flooded.Reached.Should().BeInRange(2000, 3500, "of 5,376 cells, those above the ground");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 2, 4, 2).Should().BeTrue();
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 2, 0.5f, 2).Should().BeFalse("under the ground");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 2, 40, 2).Should().BeFalse("not in the grid");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, float.NaN, 4, 2).Should().BeFalse();
    }

    [Fact]
    public void AFloodThroughOpenAirGoesCubeByCube() {
        // 100 cells each way with one small sphere in a corner: nearly all of it is open cubes of 16 cells and of 4.
        var scene = new TreeScene(new GridShape(0, 0, 0, 0.5f, 100, 100, 100), new ShapeScene([Solid.Sphere(2, 2, 2, 0.5f)]).Set);
        var flooded = BothFloods(scene, Solid.Sphere(40, 40, 40, 0.5f));
        flooded.Reached.Should().BeInRange(1_000_000 - 200, 1_000_000 - 20, "every cell but those within the radius of the sphere");
        flooded.Kept.Should().Be(0, "every open cell was reached");
        flooded.Region.Length.Should().BeLessThan(2500, "a byte a brick and a byte an open cube, where the dense flood keeps a million bytes");
        TreeRegions.QueueLength(scene.Tree).Should().BeLessThan(2500, "the dense flood's queue is a million ints");
    }

    [Fact]
    public void AFloodFindsItsWayThroughTunnelsThatRunEveryWay() {
        // Four rooms in rock, joined one to the next by tunnels that run aslant: the first along x and y, the
        // second along y and z, the third along z and x. From the first room the last is reached only by going
        // up each axis through the bricks' faces, with the open cells in a different place in each layer, and
        // from the last the first only by going down each.
        var holes = new List<Box> { new(1, 1, 1, 4, 4, 4), new(9, 8, 1, 12, 11, 4), new(9, 16, 8, 12, 19, 11), new(16, 16, 16, 19, 19, 19) };
        for (var k = 0; k <= 14; k++) {
            float step = 0.5f * k;
            holes.Add(new Box(2.4f + step, 1.4f + step, 1.4f, 4.6f + step, 3.6f + step, 3.6f));
            holes.Add(new Box(9.4f, 9.4f + step, 1.4f + step, 11.6f, 11.6f + step, 3.6f + step));
            holes.Add(new Box(9.4f + step, 16.4f, 9.4f + step, 11.6f + step, 18.6f, 11.6f + step));
        }
        var scene = new TreeScene(new GridShape(0, 0, 0, 0.5f, 40, 40, 40), new ShapeScene(TreeScene.Carved(new Box(-2, -2, -2, 22, 22, 22), holes.ToArray())).Set);
        var up = BothFloods(scene, Solid.Sphere(2.5f, 2.5f, 2.5f, 0.5f));
        TreeRegions.IsOpen(scene.Tree, up.Region, 17.75f, 17.75f, 17.75f).Should().BeTrue("the last room, from the first");
        var down = BothFloods(scene, Solid.Sphere(17.5f, 17.5f, 17.5f, 0.5f));
        TreeRegions.IsOpen(scene.Tree, down.Region, 2.25f, 2.25f, 2.25f).Should().BeTrue("the first room, from the last");
        down.Reached.Should().Be(up.Reached);
        up.Reached.Should().BeGreaterThan(400);
    }

    [Fact]
    public void AFloodLeavesAnOpenCubeByAnyPartOfItsFaces() {
        var scene = new TreeScene(new GridShape(0, 0, 0, 0.5f, 48, 48, 48), new ShapeScene(TreeScene.CubeRoom()).Set);
        var flooded = BothFloods(scene, 0.9f, Solid.Sphere(12, 12, 12, 0.5f));
        flooded.Reached.Should().BeGreaterThan(16 * 16 * 16 + 100, "the open cube that the room is, and the passage and the room beyond");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 21.75f, 12.25f, 12.25f).Should().BeTrue("the second room");
        // And back: into the cube from the passage.
        BothFloods(scene, 0.9f, Solid.Sphere(22, 12, 12, 0.5f)).Reached.Should().Be(flooded.Reached);
    }

    [Fact]
    public void ABrickWithCellsTheSeedsDidNotReachIsKeptInFull() {
        var scene = HalvedScene();
        var tree = scene.Tree;
        // From the low side only: the bricks along the sheet hold open cells of the far side, which stay at 0.
        var low = BothFloods(scene, Solid.Sphere(3, 2, 5, 0.5f));
        low.Kept.Should().Be(15, "the bricks the sheet passes through: 3 up by 5 along, the top one cut by the grid");
        TreeRegions.IsOpen(tree, low.Region, 8.25f, 2.25f, 5.25f).Should().BeTrue("the last open cell before the sheet");
        TreeRegions.IsOpen(tree, low.Region, 9.75f, 2.25f, 5.25f).Should().BeFalse("the first open cell beyond the sheet, in the same brick");
        TreeRegions.IsOpen(tree, low.Region, 15, 2.25f, 5.25f).Should().BeFalse();
        // A seed is its solid and not the box round it: this ball's box holds a column of the far side's open
        // cells, of which the ball holds none.
        var ball = BothFloods(scene, Solid.Sphere(7.5f, 2.5f, 5, 2.27f));
        ball.Reached.Should().Be(low.Reached);
        TreeRegions.IsOpen(tree, ball.Region, 9.75f, 2.25f, 5.25f).Should().BeFalse();
        // From the high side only, the other way round.
        var high = BothFloods(scene, Solid.Sphere(15, 2, 5, 0.5f));
        high.Kept.Should().Be(15);
        TreeRegions.IsOpen(tree, high.Region, 8.25f, 2.25f, 5.25f).Should().BeFalse();
        TreeRegions.IsOpen(tree, high.Region, 9.75f, 2.25f, 5.25f).Should().BeTrue();
        // From both: two regions, which meet in those bricks. Together they reach what each reached alone.
        var both = BothFloods(scene, Solid.Sphere(3, 2, 5, 0.5f), Solid.Sphere(15, 2, 5, 0.5f));
        both.Reached.Should().Be(low.Reached + high.Reached);
        both.Kept.Should().Be(15);
        // And in the other order, where the far side's cells are in each record first.
        BothFloods(scene, Solid.Sphere(15, 2, 5, 0.5f), Solid.Sphere(3, 2, 5, 0.5f)).Reached.Should().Be(both.Reached);

        // The bytes need room for the bricks kept in full, and no more than that.
        var size = Class(scene);
        var marks = new ulong[TreeRegions.MarksLength(tree)];
        var queue = new int[TreeRegions.QueueLength(tree)];
        Solid[] seeds = [Solid.Sphere(3, 2, 5, 0.5f), Solid.Sphere(15, 2, 5, 0.5f)];
        var exact = new byte[both.Region.Length];
        TreeRegions.FromSeeds(tree, size, seeds, exact, marks, queue, out var length, out _, out _).Should().BeTrue();
        length.Should().Be(exact.Length);
        exact.Should().Equal(both.Region, "a flood does not depend on what the buffers held, nor on how long they are");
        var short1 = new byte[both.Region.Length - 1];
        TreeRegions.FromSeeds(tree, size, seeds, short1, marks, queue, out length, out var reached, out _).Should().BeFalse("one brick has no room");
        (length, reached).Should().Be((0, 0));
        TreeRegions.IsOpen(tree, short1, 3, 2, 5).Should().BeFalse("after a flood that was refused no cell is open");
        TreeRegions.FromSeeds(tree, size, seeds[..1], new byte[TreeRegions.LeastLength(tree)], marks, queue, out _, out _, out _).Should()
            .BeFalse("the bricks with cells that were not reached have no room either");
    }

    [Fact]
    public void NoMoreRegionsThanAByteCanNumber() {
        // Walls every 2 m along x and along z, 0.4 m thick: 16 by 16 rooms of two open cells by two, four high, each in a brick of its own.
        var walls = new List<Solid>();
        for (var i = 0; i <= 16; i++) {
            walls.Add(Solid.Box(new Box(2 * i - 0.2f, -1, -1, 2 * i + 0.2f, 3, 33)));
            walls.Add(Solid.Box(new Box(-1, -1, 2 * i - 0.2f, 33, 3, 2 * i + 0.2f)));
        }
        var scene = new TreeScene(new GridShape(0, 0, 0, 0.5f, 64, 4, 64), new ShapeScene(walls.ToArray()).Set);
        // 15 rows of 16 rooms and 14 rooms of the last row: 254 regions, the most there can be.
        Solid[] most = [Solid.Box(new Box(0, 0, 0, 32, 2, 30)), Solid.Box(new Box(0, 0, 30, 28, 2, 32))];
        var flooded = BothFloods(scene, most);
        flooded.Reached.Should().Be(254 * 16);
        flooded.Kept.Should().Be(0, "a room's open cells are all of its brick's open cells");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 27, 1, 31).Should().BeTrue("the last room taken");
        TreeRegions.IsOpen(scene.Tree, flooded.Region, 29, 1, 31).Should().BeFalse("the first room left out");

        // One room more is refused, as the dense flood refuses it.
        var tree = scene.Tree;
        var size = Class(scene);
        Solid[] tooMany = [.. most, Solid.Sphere(29, 1, 31, 0.5f)];
        var region = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, tooMany, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)],
            out var length, out var reached, out var empty).Should().BeFalse();
        (length, reached, empty).Should().Be((0, 0, 0));
        TreeRegions.IsOpen(tree, region, 1, 1, 1).Should().BeFalse();
        Regions.FromSeeds(scene.Map, size, default, tooMany, new byte[scene.Grid.CellCount], new int[scene.Grid.CellCount], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TheNearestOpenCellIsTheOneTheDenseFloodGives() {
        var scene = HalvedScene();
        var tree = scene.Tree;
        var size = Class(scene);
        Solid[] seeds = [Solid.Sphere(3, 2, 5, 0.5f)];
        var flooded = BothFloods(scene, seeds);
        var dense = new byte[scene.Grid.CellCount];
        Regions.FromSeeds(scene.Map, size, default, seeds, dense, new int[scene.Grid.CellCount], out _, out _).Should().BeTrue();
        var asked = 0;
        // From corners that eight cells share, where the centres round are equally near: the lowest is taken.
        for (var i = 0; i <= 40; i += 3) {
            for (var j = 0; j <= 10; j += 2) {
                for (var k = 0; k <= 20; k += 3) {
                    var inDense = Regions.NearestOpen(Halved, dense, i * 0.5f, j * 0.5f, k * 0.5f, 1f, out var dx, out var dy, out var dz);
                    var inTree = TreeRegions.NearestOpen(tree, flooded.Region, i * 0.5f, j * 0.5f, k * 0.5f, 1f, out var tx, out var ty, out var tz);
                    (inTree, tx, ty, tz).Should().Be((inDense, dx, dy, dz), $"at the corner {i}, {j}, {k}");
                }
            }
        }
        TreeRegions.NearestOpen(tree, flooded.Region, 3, 2, 5, 1f, out var cornerX, out var cornerY, out var cornerZ).Should().BeTrue();
        (cornerX, cornerY, cornerZ).Should().Be((2.75f, 1.75f, 4.75f));
        foreach (var reach in new[] { 0f, 0.3f, 1.1f, 2.6f }) {
            for (var i = 0; i < 20; i++) {
                for (var j = 0; j < 9; j++) {
                    for (var k = 0; k < 11; k++) {
                        // All through the grid and a little beyond it, at steps that line up with nothing.
                        float x = -1.3f + i * 1.17f, y = -0.9f + j * 0.83f, z = -1.1f + k * 1.09f;
                        var inDense = Regions.NearestOpen(Halved, dense, x, y, z, reach, out var dx, out var dy, out var dz);
                        var inTree = TreeRegions.NearestOpen(tree, flooded.Region, x, y, z, reach, out var tx, out var ty, out var tz);
                        (inTree, tx, ty, tz).Should().Be((inDense, dx, dy, dz), $"at {x}, {y}, {z} within {reach}");
                        if (inTree) { asked++; }
                    }
                }
            }
        }
        asked.Should().BeGreaterThan(1500);
        // From just beyond the sheet the nearest is on the near side, straight through it.
        TreeRegions.NearestOpen(tree, flooded.Region, 9.75f, 2.25f, 5.25f, 2f, out var nearX, out _, out _).Should().BeTrue();
        nearX.Should().Be(8.25f);
        TreeRegions.NearestOpen(tree, flooded.Region, 9.75f, 2.25f, 5.25f, 1.4f, out _, out _, out _).Should().BeFalse();
        TreeRegions.NearestOpen(tree, flooded.Region, 3, 2, 5, -1f, out _, out _, out _).Should().BeFalse();
        TreeRegions.NearestOpen(tree, flooded.Region, 3, 2, 5, float.NaN, out _, out _, out _).Should().BeFalse();
        TreeRegions.NearestOpen(tree, flooded.Region, float.NaN, 2, 5, 1f, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void WhatCannotBeFloodedIsRefused() {
        var scene = HalvedScene();
        var tree = scene.Tree;
        var size = Class(scene);
        Solid[] seeds = [Solid.Sphere(3, 2, 5, 0.5f)];
        int most = TreeRegions.MostLength(tree), marks = TreeRegions.MarksLength(tree), queue = TreeRegions.QueueLength(tree);
        (TreeRegions.LeastLength(tree), marks, queue).Should().Be((8 + queue, scene.BrickCount + (scene.BrickCount + 63) / 64, queue));
        most.Should().Be(TreeRegions.LeastLength(tree) + 68 * scene.BrickCount);

        bool Flood(in WallTree which, SizeClass with, Solid[] from, int regionLength, int marksLength, int queueLength) {
            var region = new byte[regionLength];
            Array.Fill(region, (byte)7);
            var done = TreeRegions.FromSeeds(which, with, from, region, new ulong[marksLength], new int[queueLength], out var length, out var reached, out var empty);
            if (!done) {
                (length, reached, empty).Should().Be((0, 0, 0));
                TreeRegions.IsOpen(which, region, 3, 2, 5).Should().BeFalse();
            }
            return done;
        }
        Flood(tree, size, seeds, most, marks, queue).Should().BeTrue();
        // A flood that is refused leaves nothing of the flood that was in the bytes before.
        var used = new byte[most];
        TreeRegions.FromSeeds(tree, size, seeds, used, new ulong[marks], new int[queue], out _, out _, out _).Should().BeTrue();
        TreeRegions.IsOpen(tree, used, 3, 2, 5).Should().BeTrue();
        TreeRegions.FromSeeds(tree, default, seeds, used, new ulong[marks], new int[queue], out _, out _, out _).Should().BeFalse();
        TreeRegions.IsOpen(tree, used, 3, 2, 5).Should().BeFalse();
        Flood(tree, size, seeds, TreeRegions.LeastLength(tree) - 1, marks, queue).Should().BeFalse("no room for a byte a leaf");
        Flood(tree, size, seeds, 3, marks, queue).Should().BeFalse();
        Flood(tree, size, seeds, 0, marks, queue).Should().BeFalse();
        Flood(tree, size, seeds, most, marks - 1, queue).Should().BeFalse();
        Flood(tree, size, seeds, most, marks, queue - 1).Should().BeFalse();
        Flood(tree, default, seeds, most, marks, queue).Should().BeFalse("no class");
        SizeClass.TryMake(Radius, new WallMap(Halved, 1f / 64f, TreeScene.Cap, scene.Dense), out var other).Should().BeTrue();
        Flood(tree, other, seeds, most, marks, queue).Should().BeFalse("a class made for another unit");
        Flood(default, size, seeds, most, marks, queue).Should().BeFalse("no tree");
        Flood(tree, size, [Solid.Sphere(3, 2, 5, float.NaN)], most, marks, queue).Should().BeFalse("a seed that may not be stored");
        (TreeRegions.MostLength(default), TreeRegions.LeastLength(default), TreeRegions.MarksLength(default), TreeRegions.QueueLength(default)).Should().Be((0, 0, 0, 0));

        // Bytes that are not a flood's of this tree say nothing is open, and nothing is read beyond them.
        var flooded = BothFloods(scene, seeds).Region;
        TreeRegions.IsOpen(tree, flooded, 3, 2, 5).Should().BeTrue();
        TreeRegions.IsOpen(tree, flooded.AsSpan(0, flooded.Length - 1), 3, 2, 5).Should().BeFalse("the last brick kept in full is cut short");
        TreeRegions.IsOpen(tree, flooded.AsSpan(0, 5), 3, 2, 5).Should().BeFalse();
        TreeRegions.IsOpen(tree, default, 3, 2, 5).Should().BeFalse();
        TreeRegions.NearestOpen(tree, flooded.AsSpan(0, flooded.Length - 1), 3, 2, 5, 1f, out _, out _, out _).Should().BeFalse();
        var more = (byte[])flooded.Clone();
        more[4]++;
        TreeRegions.IsOpen(tree, more, 3, 2, 5).Should().BeFalse("it says one more brick is kept than there are bytes for");
        var other2 = new TreeScene(new GridShape(0, 0, 0, 0.5f, 100, 100, 100), new ShapeScene([Solid.Sphere(2, 2, 2, 0.5f)]).Set);
        TreeRegions.IsOpen(other2.Tree, flooded.AsSpan(0, 20), 3, 2, 5).Should().BeFalse("too short for that tree");
    }
}
