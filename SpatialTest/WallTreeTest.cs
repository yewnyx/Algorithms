using System.Buffers.Binary;
using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class WallTreeTest {
    private const float Unit = 1f / 128f;
    private const byte Cap = 128;

    // A tree built from shapes, held as the arrays a caller would keep.
    private sealed class Built {
        public readonly GridShape Grid;
        public readonly WallNode[] Nodes;
        public readonly byte[] Bricks;
        public readonly int MostNodes, MostBricks;

        public Built(GridShape grid, in ShapeSet shapes, float unit = Unit, byte cap = Cap) {
            Grid = grid;
            WallTrees.Measure(grid, shapes, unit, cap, out MostNodes, out MostBricks).Should().BeTrue();
            var nodes = new WallNode[MostNodes];
            var bricks = new byte[MostBricks * WallTrees.BrickBytes];
            WallTrees.Build(grid, shapes, unit, cap, nodes, bricks, out var nodeCount, out var brickCount).Should().BeTrue();
            Nodes = nodes[..nodeCount];
            Bricks = bricks[..(brickCount * WallTrees.BrickBytes)];
        }

        public int BrickCount => Bricks.Length / WallTrees.BrickBytes;

        public WallTree Tree(float unit = Unit, byte cap = Cap) => new(Grid, unit, cap, Nodes, Bricks);

        // How many open cubes the tree has, and the most cells along a side of any of them.
        public (int Count, int LargestSide) OpenCubes() {
            int count = 0, largest = 0, at = 0, inLevel = 1;
            for (var level = WallTrees.Levels(Grid) - 1; level >= 0; level--) {
                var children = 0;
                for (var i = 0; i < inLevel; i++) {
                    var open = System.Numerics.BitOperations.PopCount(Nodes[at + i].Open);
                    children += System.Numerics.BitOperations.PopCount(Nodes[at + i].Mixed);
                    count += open;
                    if (open > 0) { largest = Math.Max(largest, 4 << (2 * level)); }
                }
                at += inLevel;
                inLevel = children;
            }
            return (count, largest);
        }
    }

    private static byte[] Dense(GridShape grid, in ShapeSet shapes, float unit = Unit, byte cap = Cap) {
        var map = new byte[grid.CellCount];
        for (var z = 0; z < grid.CountZ; z++) {
            WallDistance.MarkLayer(grid, shapes, unit, cap, z, map.AsSpan(z * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        return map;
    }

    // A dense map with every count put down as a tree keeps it: to which fifteenth of the cap it
    // is in, and back to the least count in that fifteenth.
    private static byte[] PutDown(byte[] dense, byte cap) {
        var kept = new byte[dense.Length];
        for (var i = 0; i < dense.Length; i++) { kept[i] = (byte)((dense[i] * 15 / cap * cap + 14) / 15); }
        return kept;
    }

    // Points all through a grid and a little beyond it: every cell's centre, and places between
    // centres, on faces and in the outer half cell, at steps that line up with nothing.
    private static IEnumerable<(float x, float y, float z)> Points(GridShape grid) {
        for (var z = 0; z < grid.CountZ; z++) {
            for (var y = 0; y < grid.CountY; y++) {
                for (var x = 0; x < grid.CountX; x++) {
                    grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    yield return (cx, cy, cz);
                }
            }
        }
        float lengthX = grid.CountX * grid.CellSize, lengthY = grid.CountY * grid.CellSize, lengthZ = grid.CountZ * grid.CellSize;
        for (var i = 0; i < 23; i++) {
            for (var j = 0; j < 19; j++) {
                for (var k = 0; k < 17; k++) {
                    yield return (grid.OriginX + lengthX * (i - 0.63f) / 21.3f, grid.OriginY + lengthY * (j - 0.41f) / 17.7f, grid.OriginZ + lengthZ * (k - 0.27f) / 15.9f);
                }
            }
        }
    }

    // The tree gives, at every point, what the dense map of the same shapes gives with its counts
    // put down as a tree keeps them, to the bit, and the same yes or no for every radius. It never
    // gives more room than the dense map as it is, and at a cell's centre less by under a fifteenth of the cap.
    private static void AgreesWithDense(Built built, byte[] dense, float unit = Unit, byte cap = Cap) {
        var map = new WallMap(built.Grid, unit, cap, PutDown(dense, cap));
        var whole = new WallMap(built.Grid, unit, cap, dense);
        var tree = built.Tree(unit, cap);
        float[] radii = [0f, 0.2f, 0.51f, 0.9f, cap * unit - 0.07f, cap * unit - 0.06f, cap * unit];
        var count = 0;
        foreach (var (x, y, z) in Points(built.Grid)) {
            var inDense = WallDistance.RoomAt(map, x, y, z, out var denseRoom);
            var inTree = WallTrees.RoomAt(tree, x, y, z, out var treeRoom);
            if (inTree != inDense || BitConverter.SingleToInt32Bits(treeRoom) != BitConverter.SingleToInt32Bits(denseRoom)) {
                throw new Xunit.Sdk.XunitException($"At {x}, {y}, {z} the dense map put down gives {inDense} {denseRoom} and the tree {inTree} {treeRoom}.");
            }
            WallDistance.RoomAt(whole, x, y, z, out var wholeRoom);
            if (treeRoom > wholeRoom) {
                throw new Xunit.Sdk.XunitException($"At {x}, {y}, {z} the tree gives {treeRoom}, more than the dense map's {wholeRoom}.");
            }
            foreach (var radius in radii) {
                if (WallTrees.HasRoom(tree, x, y, z, radius) != WallDistance.HasRoom(map, x, y, z, radius)) {
                    throw new Xunit.Sdk.XunitException($"At {x}, {y}, {z} and a radius of {radius} the two differ; the room is {denseRoom}.");
                }
            }
            count++;
        }
        count.Should().BeGreaterThan(built.Grid.CellCount);
        for (var z = 0; z < built.Grid.CountZ; z++) {
            for (var y = 0; y < built.Grid.CountY; y++) {
                for (var x = 0; x < built.Grid.CountX; x++) {
                    built.Grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    WallDistance.RoomAt(whole, cx, cy, cz, out var wholeRoom);
                    WallTrees.RoomAt(tree, cx, cy, cz, out var treeRoom);
                    if (wholeRoom - treeRoom >= cap * unit / 15f) {
                        throw new Xunit.Sdk.XunitException($"At the centre of cell {x}, {y}, {z} the tree gives {treeRoom}, a fifteenth of the cap or more below the dense map's {wholeRoom}.");
                    }
                }
            }
        }
    }

    private static readonly Solid[] Mixed = [
        Solid.Box(new Box(-1, -1, -1, 20, 0.3f, 20)),
        ShapeScene.TurnedBox(6, 3, 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(11, 4.5f, 8, 1.6f),
        Solid.Capsule(3, 1, 9, 14, 7, 2, 0.35f),
        Solid.Box(new Box(15.5f, 0, 0, 30, 30, 30)),
    ];
    private static readonly float[] MixedCorners = [9, 0, 3, 9, 8, 3, 9, 8, 11, 9, 0, 11, 0, 7.5f, 1, 16, 6.5f, 2, 3, 2.5f, 11.5f];
    private static readonly int[] MixedTriangles = [0, 1, 2, 0, 2, 3, 4, 5, 6];

    // 37 by 22 by 29 cells: no side a multiple of four, so the root's cube reaches past the grid on every side but its first.
    private static readonly GridShape Odd = new(-0.3f, -0.7f, 0.2f, 0.5f, 37, 22, 29);

    [Fact]
    public void ATreeGivesWhatTheDenseMapGivesAtEveryPoint() {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var built = new Built(Odd, scene.Set);
        AgreesWithDense(built, Dense(Odd, scene.Set));
        // What this tree comes to. A change here is a change to what is kept: raise WallTrees.Version with it.
        (built.Nodes.Length, built.BrickCount, built.OpenCubes().Count).Should().Be((9, 304, 80));
        built.BrickCount.Should().BeLessThan(37 * 22 * 29 / 64, "counts are kept near the surfaces only");
    }

    [Fact]
    public void ATreeOverGroundGivesWhatTheDenseMapGives() {
        // The wall across the ground, and a sphere half in it: blocks at more than one spacing, solid below.
        var scene = new HeightScene((u, w) => 1.5 + 0.4 * Math.Sin(0.9 * u) * Math.Cos(0.7 * w) + (u > 6.2 ? 1.5 : 0), 20, 12, 0.5f, 0.5f, 0.25f,
            solids: [Solid.Sphere(3, 2.2f, 3, 0.8f)]);
        var grid = new GridShape(-1, -1.75f, -1, 0.5f, 24, 14, 16);
        var built = new Built(grid, scene.Set);
        AgreesWithDense(built, Dense(grid, scene.Set));

        // Turned and tilted, the same ground is looked at more closely and comes to the same counts.
        var box = ShapeScene.TurnedBox(0, 0, 0, 1, 1, 1, 37, 21);
        var turned = new HeightScene((u, w) => 1.5 + 0.4 * Math.Sin(0.9 * u) * Math.Cos(0.7 * w) + (u > 6.2 ? 1.5 : 0), 20, 12, 0.5f, 0.5f, 0.25f, place: packed =>
            packed.Placed(2, -1, 3, box.UX, box.UY, box.UZ, box.VX, box.VY, box.VZ, box.WX, box.WY, box.WZ, packed.Lowest - 2));
        var around = new GridShape(-4, -5, -3, 0.5f, 30, 26, 28);
        AgreesWithDense(new Built(around, turned.Set), Dense(around, turned.Set));
    }

    [Fact]
    public void GroundThatRisesJustBesideACubeIsFeltInIt() {
        // Ground at 1 m that steps up to 6 m between 8 and 8.5 m along x. Its heights are kept in
        // blocks of 8 m, so the step lies in the second block, at the edge of the first. The
        // cubes of cells that end at 8 m are over low ground only, and the step is within a metre
        // of their last cells.
        var scene = new HeightScene((u, _) => u > 8.1 ? 6 : 1, 40, 24, 0.5f, 0.5f, 0.25f);
        var grid = new GridShape(0, 0, 0, 0.5f, 40, 16, 24);
        var dense = Dense(grid, scene.Set);
        grid.CentreOf(15, 8, 10, out var x, out var y, out var z);
        (x, y).Should().Be((7.75f, 4.25f));
        dense[grid.IndexOf(15, 8, 10)].Should().BeInRange(30, 127, "the last cell before 8 m, 3 m above the low ground, is less than a metre from the step");
        AgreesWithDense(new Built(grid, scene.Set), dense);
    }

    [Fact]
    public void AShapeWhoseBoxIsNearAndWhichIsNotLeavesOpenCubesOfNoMoreThanSixteenCells() {
        // One large triangle, tilted across the far corner of a grid of 100 cells each way. The
        // box round it holds the whole grid, so every cube is looked at; the triangle itself is
        // more than two metres from the first cube of 64 cells, whose cells all come out open.
        var grid = new GridShape(0, 0, 0, 0.5f, 100, 100, 100);
        var scene = new ShapeScene([], [100, 0, 0, 0, 100, 0, 0, 0, 100], [0, 1, 2]);
        var built = new Built(grid, scene.Set);
        built.OpenCubes().LargestSide.Should().Be(16);
        built.BrickCount.Should().BeGreaterThan(1000, "along the triangle");
        var block = new byte[WallTrees.BlockLength(built.Nodes.Length, built.BrickCount)];
        WallTrees.Write(built.Tree(), scene.Set, block, out _).Should().BeTrue();
        WallTrees.TryRead(block, scene.Set, out _).Should().BeTrue();
        // Every cell of the first cube of 64 is open, and it is kept as 64 cubes of 16.
        var tree = built.Tree();
        for (var i = 0; i < 4; i++) {
            WallTrees.HasRoom(tree, 4 + i * 8, 28 - i * 8, 4 + i * 5, 0.9f).Should().BeTrue();
        }
        var dense = new WallMap(grid, Unit, Cap, PutDown(Dense(grid, scene.Set), Cap));
        foreach (var (x, y, z) in Points(new GridShape(20, 20, 20, 0.5f, 30, 30, 30))) {
            WallDistance.RoomAt(dense, x, y, z, out var room).Should().BeTrue();
            WallTrees.RoomAt(tree, x, y, z, out var same).Should().BeTrue();
            same.Should().Be(room);
        }
    }

    [Theory]
    [InlineData(1f / 64f, 60)]
    [InlineData(1f / 128f, 255)]
    [InlineData(0.05f, 7)]
    public void AnotherUnitAndCapGiveWhatTheDenseMapGives(float unit, byte cap) {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        AgreesWithDense(new Built(Odd, scene.Set, unit, cap), Dense(Odd, scene.Set, unit, cap), unit, cap);
    }

    [Fact]
    public void ABrickWhoseCellsAllReadZeroIsNotKept() {
        // Two bricks along x. The first is inside the box. The second's cells are inside it but for the last
        // layer, whose centres are 0.15 m from it: 2 counts of a sixteenth, under a fifteenth of a cap of 128.
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 4, 4);
        var scene = new ShapeScene([Solid.Box(new Box(-1, -1, -1, 3.6f, 3, 3))]);
        var dense = Dense(grid, scene.Set, 1f / 16f, 128);
        dense[grid.IndexOf(7, 0, 0)].Should().Be(2);
        var built = new Built(grid, scene.Set, 1f / 16f, 128);
        built.BrickCount.Should().Be(0, "every cell reads 0 at four bits");
        AgreesWithDense(built, dense, 1f / 16f, 128);
    }

    [Fact]
    public void OpenAirIsKeptAsOpenCubesOfNoMoreThanSixteenCells() {
        // 100 cells each way with one small sphere in a corner: the root's cube is 256 cells, in three levels of nodes.
        var grid = new GridShape(0, 0, 0, 0.5f, 100, 100, 100);
        WallTrees.Levels(grid).Should().Be(3);
        var scene = new ShapeScene([Solid.Sphere(2, 2, 2, 0.5f)]);
        var built = new Built(grid, scene.Set);
        var (count, largest) = built.OpenCubes();
        largest.Should().Be(16, "an open cube is never larger");
        built.BrickCount.Should().BeInRange(8, 64, "only round the sphere");
        (built.Nodes.Length * 24 + built.Bricks.Length).Should().BeLessThan(grid.CellCount / 100);
        // The grid is 6 and a quarter cubes of 16 cells each way: 216 whole ones, less the few the sphere is in.
        count.Should().BeGreaterThan(216 - 8);

        var tree = built.Tree();
        WallTrees.RoomAt(tree, 30, 30, 30, out var room).Should().BeTrue();
        room.Should().Be(1f, "the cap, in the open");
        WallTrees.HasRoom(tree, 30, 30, 30, 0.9f).Should().BeTrue();
        // Where eight open cells prove least: on a face of the eight, at its middle. The tree says yes for exactly what the room there is, and no for a hair more.
        WallTrees.RoomAt(tree, 30.25f, 30.5f, 30.5f, out var least).Should().BeTrue();
        least.Should().BeApproximately(MathF.Sqrt(1f - 0.125f) - (float)Shapes.Slack, 1e-6f);
        WallTrees.HasRoom(tree, 30.25f, 30.5f, 30.5f, least).Should().BeTrue();
        WallTrees.HasRoom(tree, 30.25f, 30.5f, 30.5f, MathF.BitIncrement(least)).Should().BeFalse();
        WallTrees.HasRoom(tree, 2, 2, 2, 0.01f).Should().BeFalse("inside the sphere");
    }

    [Fact]
    public void WithNoShapesEveryCellIsOpenAndUnderOneSolidEveryCellIsZero() {
        // Sides that are multiples of four cells: no brick lies across the grid's edge.
        var grid = new GridShape(1, 2, 3, 0.5f, 40, 8, 20);
        var open = new Built(grid, default);
        open.BrickCount.Should().Be(0);
        AgreesWithDense(open, Dense(grid, default));

        // With other sides, the bricks along the far faces hold the cap inside the grid and 0 outside it.
        var odd = new Built(Odd, default);
        odd.BrickCount.Should().BeGreaterThan(0);
        AgreesWithDense(odd, Dense(Odd, default));

        var buried = new ShapeScene([Solid.Box(new Box(-5, -5, -5, 40, 40, 40))]);
        var solid = new Built(grid, buried.Set);
        solid.BrickCount.Should().Be(0);
        solid.Nodes.Length.Should().Be(1, "the root alone: every child is a cube of zeros");
        (solid.Nodes[0].Mixed, solid.Nodes[0].Open).Should().Be((0UL, 0UL));
        AgreesWithDense(solid, Dense(grid, buried.Set));
    }

    [Fact]
    public void ShapesCutAtTheGridsBoxGrownAsMarkingLooksGiveTheSameTree() {
        // Shapes round and across the grid's edges. Cut: only those whose box comes within the cap, a unit and a cell of the grid's.
        var grid = new GridShape(0, 0, 0, 0.5f, 20, 20, 20);
        var all = new List<Solid>();
        for (var i = 0; i < 90; i++) {
            float x = (i * 37 % 19) - 4 + 0.31f * (i % 3), y = (i * 53 % 17) - 3 + 0.17f * (i % 5), z = (i * 71 % 18) - 4 + 0.43f * (i % 2);
            all.Add(i % 3 == 0 ? Solid.Sphere(x, y, z, 0.3f + 0.2f * (i % 4)) : i % 3 == 1 ? Solid.Capsule(x, y, z, x + 1.5f, y - 1, z + 0.5f, 0.25f) : ShapeScene.TurnedBox(x, y, z, 1.2, 0.2, 0.4, i * 17, i * 29));
        }
        var whole = new ShapeScene(all.ToArray());
        var grow = Cap * Unit + Unit + grid.CellSize;
        var cut = new List<Solid>();
        for (var i = 0; i < all.Count; i++) {
            var box = whole.Boxes[i];
            if (box.MinX <= 10 + grow && box.MaxX >= -grow && box.MinY <= 10 + grow && box.MaxY >= -grow && box.MinZ <= 10 + grow && box.MaxZ >= -grow) { cut.Add(all[i]); }
        }
        cut.Count.Should().BeInRange(20, 70, "some are left out and some are not");
        var built = new Built(grid, new ShapeScene(cut.ToArray()).Set);
        AgreesWithDense(built, Dense(grid, whole.Set));
    }

    [Fact]
    public void MeasureSaysAtLeastWhatABuildUsesAndABuildNeedsThatMuchRoom() {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var built = new Built(Odd, scene.Set);
        built.MostNodes.Should().BeGreaterThanOrEqualTo(built.Nodes.Length);
        built.MostBricks.Should().BeGreaterThan(built.BrickCount, "some bricks turn out to hold one count all through");
        built.MostBricks.Should().BeLessThan(2 * built.BrickCount);

        var nodes = new WallNode[built.MostNodes];
        var bricks = new byte[built.MostBricks * WallTrees.BrickBytes];
        WallTrees.Build(Odd, scene.Set, Unit, Cap, nodes.AsSpan(0, built.MostNodes - 1), bricks, out var nodeCount, out var brickCount).Should().BeFalse("one node short");
        (nodeCount, brickCount).Should().Be((0, 0));
        WallTrees.Build(Odd, scene.Set, Unit, Cap, nodes, bricks.AsSpan(0, bricks.Length - 1), out _, out _).Should().BeFalse("one byte short");
    }

    [Fact]
    public void ARefusedBuildGivesNoTree() {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var nodes = new WallNode[1000];
        var bricks = new byte[1000 * WallTrees.BrickBytes];
        bool Builds(GridShape grid, in ShapeSet shapes, float unit = Unit, byte cap = Cap) {
            var built = WallTrees.Build(grid, shapes, unit, cap, nodes, bricks, out var nodeCount, out var brickCount);
            if (!built) { (nodeCount, brickCount).Should().Be((0, 0)); }
            return built;
        }
        Builds(new GridShape(0, 0, 0, 0.5f, 8, 8, 8), scene.Set).Should().BeTrue();
        Builds(new GridShape(0, 0, 0, 0f, 8, 8, 8), scene.Set).Should().BeFalse("a cell size of nothing");
        Builds(new GridShape(0, 0, 0, 0.5f, 8, 0, 8), scene.Set).Should().BeFalse("an axis with no cells");
        Builds(new GridShape(0, 0, 0, 0.5f, 8, 8, 8), scene.Set, unit: 0f).Should().BeFalse();
        Builds(new GridShape(0, 0, 0, 0.5f, 8, 8, 8), scene.Set, cap: 0).Should().BeFalse();
        // A triangle that names a corner that is not there: the shapes are damaged. Measuring
        // looks at boxes only and does not meet it; marking does.
        int[] damaged = [0, 1, 2, 0, 2, 9, 4, 5, 6];
        var grid = new GridShape(0, 0, 0, 0.5f, 40, 20, 30);
        WallTrees.Measure(grid, new ShapeSet(scene.Solids, scene.Corners, damaged, scene.Boxes, scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order), Unit, Cap, out _, out _).Should().BeTrue();
        Builds(grid, new ShapeSet(scene.Solids, scene.Corners, damaged, scene.Boxes, scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order)).Should().BeFalse();
        WallTrees.Measure(grid, new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes.AsSpan(1), scene.Nodes, scene.Order), Unit, Cap, out var mostNodes, out var mostBricks).Should().BeFalse();
        (mostNodes, mostBricks).Should().Be((0, 0));
        Builds(new GridShape(0, 0, 0, 0.5f, 8, 8, 8), new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes.AsSpan(1), scene.Nodes, scene.Order)).Should().BeFalse("spans that do not belong together");

        // A tree with no nodes, which is what a caller has after a refusal, answers no question.
        var none = new WallTree(new GridShape(0, 0, 0, 0.5f, 8, 8, 8), Unit, Cap, default, default);
        none.IsValid.Should().BeFalse();
        WallTrees.RoomAt(none, 1, 1, 1, out var room).Should().BeFalse();
        room.Should().Be(0f);
        WallTrees.HasRoom(none, 1, 1, 1, 0f).Should().BeFalse();
    }

    // Eight cells each way with a ball in one corner: a tree small enough to write out.
    private static readonly GridShape Small = new(0, 0, 0, 0.5f, 8, 8, 8);

    private static readonly ShapeScene SmallScene = new([Solid.Sphere(0.6f, 0.6f, 0.6f, 0.45f)]);

    private static byte[] Written() {
        var built = new Built(Small, SmallScene.Set, 1f / 16f, 8);
        var block = new byte[WallTrees.BlockLength(built.Nodes.Length, built.BrickCount)];
        block.AsSpan().Fill(0xFF);
        WallTrees.Write(built.Tree(1f / 16f, 8), SmallScene.Set, block, out var length).Should().BeTrue();
        length.Should().Be(block.Length);
        return block;
    }

    // Where the parts of that block start: a header, the root, one brick.
    private const int NodesAt = 64, BricksAt = 88, Length = 120;

    // The block that tree makes, byte for byte: eight bytes of the header are the fingerprint of the one
    // ball. If this changes, the layout, the counts or the fingerprint changed:
    // raise WallTrees.Version, so that stored blocks are made again and not misread.
    private const string Fixed =
        "59575452010000000000000000000000000000000000003F0800000008000000080000000000803D08000000010000000100000002EC7A791069E10600000000" +
        "010000000000000032003300000000000000000000000000" +
        "13F901F779FFFFFF01F700F557FDFFFF79FF57FDDFFFFFFFFFFFFFFFFFFFFFFF";

    private static void Put(byte[] block, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(at), value);

    private static void Put(byte[] block, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at), value);

    [Fact]
    public void TheSameShapesGiveTheSameBlockByteForByte() {
        // The buffer was filled with ones first: nothing of that shows in the header's spare bytes.
        Convert.ToHexString(Written()).Should().Be(Fixed);
    }

    [Fact]
    public void TheFixedBlockGivesItsKnownAnswers() {
        var block = Convert.FromHexString(Fixed);
        block.Length.Should().Be(Length);
        WallTrees.TryRead(block, SmallScene.Set, out var tree).Should().BeTrue();
        (tree.Shape.CountX, tree.Shape.CellSize, tree.Unit, tree.Cap, tree.Levels, tree.Nodes.Length, tree.Bricks.Length).Should().Be((8, 0.5f, 1f / 16f, (byte)8, 1, 1, 32));
        // The root: its first child is the one brick, round the ball, and the seven other cubes of four cells in the grid are open.
        (tree.Nodes[0].Mixed, tree.Nodes[0].Open, tree.Nodes[0].First, tree.Nodes[0].FirstLeaf).Should().Be((1UL, 0x330032UL, 0, 0));
        // The centre of the cell at 2, 2, 2 is 1.25 m along each axis: 0.676 m from the ball, which is 10 sixteenths rounded down, and the cap is 8.
        WallTrees.RoomAt(tree, 1.25f, 1.25f, 1.25f, out var room).Should().BeTrue();
        room.Should().BeApproximately(0.5f - (float)Shapes.Slack, 1e-6f, "the cap less the slack");
        // The cell at 1, 1, 0 has its centre 0.397 m from the ball's centre and so inside the ball's 0.45 m.
        WallTrees.RoomAt(tree, 0.75f, 0.75f, 0.25f, out room).Should().BeTrue();
        room.Should().Be(0f);
        // The cell at 2, 0, 0: 0.819 m from the centre, 0.369 m from the ball, 5 sixteenths.
        WallTrees.RoomAt(tree, 1.25f, 0.25f, 0.25f, out room).Should().BeTrue();
        room.Should().BeApproximately(5f / 16f - (float)Shapes.Slack, 1e-6f);
        WallTrees.HasRoom(tree, 3, 3, 3, 0.4f).Should().BeTrue();
        WallTrees.RoomAt(tree, 4.1f, 1, 1, out _).Should().BeFalse("outside the grid");
    }

    [Fact]
    public void ABlockThatIsNotWholeIsRefused() {
        var good = Written();
        WallTrees.TryRead(good, SmallScene.Set, out _).Should().BeTrue();
        WallTrees.TryRead([], SmallScene.Set, out _).Should().BeFalse();
        WallTrees.TryRead(good.AsSpan(0, 63), SmallScene.Set, out _).Should().BeFalse("shorter than a header");
        WallTrees.TryRead(good.AsSpan(0, good.Length - 1), SmallScene.Set, out _).Should().BeFalse("one byte short");
        WallTrees.TryRead(good.Append((byte)0).ToArray(), SmallScene.Set, out _).Should().BeFalse("one byte long");

        byte[] Changed(int at, int value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }
        byte[] Bits(int at, ulong value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }
        WallTrees.TryRead(Changed(0, 0x59575452), SmallScene.Set, out _).Should().BeFalse("written the other way round");
        WallTrees.TryRead(Changed(4, WallTrees.Version + 1), SmallScene.Set, out _).Should().BeFalse("a newer version");
        WallTrees.TryRead(Changed(4, WallTrees.Version - 1), SmallScene.Set, out _).Should().BeFalse("an older version");
        WallTrees.TryRead(Changed(20, 0), SmallScene.Set, out _).Should().BeFalse("a cell size of nothing");
        WallTrees.TryRead(Changed(24, 0), SmallScene.Set, out _).Should().BeFalse("no cells along x");
        WallTrees.TryRead(Changed(24, 17), SmallScene.Set, out _).Should().BeFalse("a grid whose root has another level");
        WallTrees.TryRead(Changed(36, 0), SmallScene.Set, out _).Should().BeFalse("a unit of nothing");
        WallTrees.TryRead(Changed(40, 0), SmallScene.Set, out _).Should().BeFalse("a cap of nothing");
        WallTrees.TryRead(Changed(40, 256), SmallScene.Set, out _).Should().BeFalse("a cap that is no byte");
        WallTrees.TryRead(Changed(44, 2), SmallScene.Set, out _).Should().BeFalse("a node more than there is");
        WallTrees.TryRead(Changed(44, 0), SmallScene.Set, out _).Should().BeFalse("no root");
        WallTrees.TryRead(Changed(48, 0), SmallScene.Set, out _).Should().BeFalse("a brick fewer than there is");

        // The root says its first child is mixed: the one brick, which is the first.
        WallTrees.TryRead(Bits(NodesAt, 0UL), SmallScene.Set, out _).Should().BeFalse("a brick that nothing leads to");
        WallTrees.TryRead(Bits(NodesAt, 1UL | (1UL << 2)), SmallScene.Set, out _).Should().BeFalse("a second mixed child, with no brick");
        WallTrees.TryRead(Bits(NodesAt, 3UL), SmallScene.Set, out _).Should().BeFalse("a child that is both mixed and open, and a brick too few");
        WallTrees.TryRead(Bits(NodesAt + 8, 0x330032UL | 1UL), SmallScene.Set, out _).Should().BeFalse("the child with the brick is open as well: nothing else about the block is wrong");
        // A second node that nothing leads to, with the header saying two.
        var longer = new byte[good.Length + 24];
        good.AsSpan(0, NodesAt + 24).CopyTo(longer);
        good.AsSpan(NodesAt + 24).CopyTo(longer.AsSpan(NodesAt + 48));
        Put(longer, 44, 2);
        WallTrees.TryRead(longer, SmallScene.Set, out _).Should().BeFalse("a node more than the root leads to");
        WallTrees.TryRead(Changed(NodesAt + 16, 1), SmallScene.Set, out _).Should().BeFalse("bricks that do not begin at the first");
        WallTrees.TryRead(Changed(NodesAt + 16, -1), SmallScene.Set, out _).Should().BeFalse();
        WallTrees.TryRead(Changed(NodesAt + 20, 1), SmallScene.Set, out _).Should().BeFalse("open cubes that are not counted from none");
        WallTrees.TryRead(Changed(52, BinaryPrimitives.ReadInt32LittleEndian(good.AsSpan(52)) ^ 1), SmallScene.Set, out _).Should().BeFalse("the fingerprint of other shapes");
        WallTrees.TryRead(Changed(56, BinaryPrimitives.ReadInt32LittleEndian(good.AsSpan(56)) ^ (1 << 30)), SmallScene.Set, out _).Should().BeFalse();
        // What may differ and still be read: which cubes are open, and what the brick holds.
        WallTrees.TryRead(Bits(NodesAt + 8, 0UL), SmallScene.Set, out var closed).Should().BeTrue();
        WallTrees.RoomAt(closed, 3, 3, 3, out var room).Should().BeTrue();
        room.Should().Be(0f, "a cube that is no longer open is a cube of zeros");
    }

    [Fact]
    public void ABlockIsReadOnlyBesideTheShapesItWasWrittenWith() {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var built = new Built(Odd, scene.Set);
        var block = new byte[WallTrees.BlockLength(built.Nodes.Length, built.BrickCount)];
        WallTrees.Write(built.Tree(), scene.Set, block, out _).Should().BeTrue();
        WallTrees.TryRead(block, scene.Set, out _).Should().BeTrue();
        // The same shapes, kept somewhere else, are the same shapes.
        WallTrees.TryRead(block, new ShapeScene((Solid[])Mixed.Clone(), (float[])MixedCorners.Clone(), (int[])MixedTriangles.Clone()).Set, out _).Should().BeTrue();

        // Any other shapes are refused: one number of one shape a hair off, one shape more or fewer, none at all.
        bool ReadsBeside(Solid[] solids, float[] corners, int[] triangles) => WallTrees.TryRead(block, new ShapeScene(solids, corners, triangles).Set, out _);
        var moved = (Solid[])Mixed.Clone();
        moved[2] = Solid.Sphere(11, 4.5f, 8, MathF.BitIncrement(1.6f));
        ReadsBeside(moved, MixedCorners, MixedTriangles).Should().BeFalse("a sphere whose radius is the next float up");
        var corners = (float[])MixedCorners.Clone();
        corners[^1] = MathF.BitDecrement(corners[^1]);
        ReadsBeside(Mixed, corners, MixedTriangles).Should().BeFalse("a corner the next float down");
        ReadsBeside(Mixed, MixedCorners, [0, 1, 2, 0, 2, 3, 4, 6, 5]).Should().BeFalse("a triangle's corners in another order");
        ReadsBeside(Mixed, MixedCorners, [0, 1, 2, 0, 2, 3]).Should().BeFalse("a triangle fewer");
        ReadsBeside(Mixed[..^1], MixedCorners, MixedTriangles).Should().BeFalse("a solid fewer");
        ReadsBeside([.. Mixed, Solid.Sphere(100, 100, 100, 1)], MixedCorners, MixedTriangles).Should().BeFalse("a solid more, far from the grid");
        ReadsBeside(Mixed, [], []).Should().BeFalse();
        WallTrees.TryRead(block, default, out _).Should().BeFalse("no shapes");
        WallTrees.TryRead(block, new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes.AsSpan(1), scene.Nodes, scene.Order), out _).Should().BeFalse("spans that do not belong together");
        WallTrees.Write(built.Tree(), new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes.AsSpan(1), scene.Nodes, scene.Order), block, out _).Should().BeFalse();

        // Ground is held to as well: one height of it changed, and the same ground a step along.
        Func<double, double, double> rolling = (u, w) => 1.5 + 0.4 * Math.Sin(0.9 * u) * Math.Cos(0.7 * w);
        var ground = new HeightScene(rolling, 20, 12, 0.5f, 0.5f, 0.25f);
        var grid = new GridShape(-1, -1.75f, -1, 0.5f, 24, 14, 16);
        var over = new Built(grid, ground.Set);
        var groundBlock = new byte[WallTrees.BlockLength(over.Nodes.Length, over.BrickCount)];
        WallTrees.Write(over.Tree(), ground.Set, groundBlock, out _).Should().BeTrue();
        WallTrees.TryRead(groundBlock, ground.Set, out _).Should().BeTrue();
        WallTrees.TryRead(groundBlock, new HeightScene(rolling, 20, 12, 0.5f, 0.5f, 0.25f).Set, out _).Should().BeTrue();
        WallTrees.TryRead(groundBlock, new HeightScene((u, w) => rolling(u, w) + (u > 4.9 && u < 5.1 && w > 2.9 && w < 3.1 ? 0.5 : 0), 20, 12, 0.5f, 0.5f, 0.25f).Set, out _).Should().BeFalse("one height raised");
        WallTrees.TryRead(groundBlock, new HeightScene(rolling, 20, 12, 0.5f, 0.5f, 0.25f, place: packed => packed.Placed(0.5f, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, packed.Floor)).Set, out _).Should().BeFalse("the same ground half a metre along");
        WallTrees.TryRead(groundBlock, scene.Set, out _).Should().BeFalse();
        // One kept height a step higher and nothing else changed, and one block's entry likewise.
        bool ReadsOver(ushort[] kept, uint[] blocks) =>
            WallTrees.TryRead(groundBlock, new ShapeSet(ground.Solids, [], [], [ground.Field], blocks, kept, ground.Boxes, ground.Nodes.AsSpan(0, ground.NodeCount), ground.Order), out _);
        ReadsOver(ground.Kept, ground.Blocks).Should().BeTrue();
        var kept = (ushort[])ground.Kept.Clone();
        kept[^1]++;
        ReadsOver(kept, ground.Blocks).Should().BeFalse("the last kept height one step up");
        var blocks = (uint[])ground.Blocks.Clone();
        blocks[0] ^= 4;
        ReadsOver(ground.Kept, blocks).Should().BeFalse("a block that begins one height later");
    }

    [Fact]
    public void GroundWhoseSquaresAreLongerOneWayGivesWhatTheDenseMapGives() {
        // Squares of 0.5 m by 0.9 m, 40 by 24 of them: three blocks of sixteen squares along one axis and two along the
        // other, of 8 m and of 14.4 m. A hill, with ground 3 m higher from the second block on along the short
        // way: a cube of cells is judged by the blocks it lies over, which must be found by each axis's own length.
        var scene = new HeightScene((u, w) => 2 + 1.2 * Math.Sin(0.35 * u) * Math.Cos(0.3 * w) + (u > 8.5 ? 3 : 0), 40, 24, 0.5f, 0.9f, 0.25f);
        var grid = new GridShape(-1, -1, -1, 0.5f, 44, 20, 47);
        AgreesWithDense(new Built(grid, scene.Set), Dense(grid, scene.Set));
        // And the other way about.
        var other = new HeightScene((u, w) => 2 + 1.2 * Math.Sin(0.3 * u) * Math.Cos(0.35 * w) + (w > 8.5 ? 3 : 0), 24, 40, 0.9f, 0.5f, 0.25f);
        var across = new GridShape(-1, -1, -1, 0.5f, 47, 20, 44);
        AgreesWithDense(new Built(across, other.Set), Dense(across, other.Set));
    }

    [Fact]
    public void ALongGridHasMoreLevelsAndGivesWhatTheDenseMapGives() {
        // 1,100 cells along one side: the root's cube is 4,096 cells, in five levels of nodes.
        var grid = new GridShape(-3, 0, 0, 0.5f, 1100, 6, 5);
        WallTrees.Levels(grid).Should().Be(5);
        var scene = new ShapeScene([
            Solid.Sphere(1, 1.5f, 1, 0.6f), Solid.Capsule(120, 0, 0, 131, 3, 2.5f, 0.3f), Solid.Box(new Box(300, -1, -1, 300.6f, 2, 4)),
            ShapeScene.TurnedBox(511.7, 1.5, 1.2, 1.5, 0.3, 0.8, 30, 15), Solid.Sphere(546.9f, 3, 2.4f, 1.1f),
        ], [200, 0, 0, 203, 3, 0, 201, 0, 2.5f], [0, 1, 2]);
        var built = new Built(grid, scene.Set);
        AgreesWithDense(built, Dense(grid, scene.Set));
        var block = new byte[WallTrees.BlockLength(built.Nodes.Length, built.BrickCount)];
        WallTrees.Write(built.Tree(), scene.Set, block, out _).Should().BeTrue();
        WallTrees.TryRead(block, scene.Set, out var tree).Should().BeTrue();
        tree.Levels.Should().Be(5);

        // The longest grid there can be a tree for has 2 to the power of 30 cells along a side. One cell more is refused at once.
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 1 << 30, 1, 1)).Should().Be(14);
        var tooLong = new GridShape(0, 0, 0, 0.5f, (1 << 30) + 1, 1, 1);
        tooLong.CellCount.Should().Be((1 << 30) + 1);
        WallTrees.Levels(tooLong).Should().Be(0);
        WallTrees.Measure(tooLong, scene.Set, Unit, Cap, out _, out _).Should().BeFalse();
        WallTrees.Build(tooLong, scene.Set, Unit, Cap, new WallNode[64], new byte[64], out _, out _).Should().BeFalse();
        new WallTree(tooLong, Unit, Cap, built.Nodes, built.Bricks).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ABlockWithAnOpenCubeLargerThanSixteenCellsOrANodeOutOfPlaceIsRefused() {
        // 100 cells each way and nothing in them: three levels of nodes. 100 is a multiple of four, so no brick lies across the grid's edge.
        var grid = new GridShape(0, 0, 0, 0.5f, 100, 100, 100);
        var built = new Built(grid, default);
        built.BrickCount.Should().Be(0);
        var good = new byte[WallTrees.BlockLength(built.Nodes.Length, built.BrickCount)];
        WallTrees.Write(built.Tree(), default, good, out _).Should().BeTrue();
        WallTrees.TryRead(good, default, out var tree).Should().BeTrue();
        tree.Nodes.Length.Should().Be(built.Nodes.Length);
        byte[] Bits(int at, ulong value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }
        byte[] Changed(int at, int value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }

        // A tree that is one root: under a solid that buries the grid, every child is a cube of zeros.
        // With 100 cells a side the root's children are 64 cells, too large to be open. With 20 they are 16.
        var buried = new ShapeScene([Solid.Box(new Box(-5, -5, -5, 60, 60, 60))]);
        foreach (var (side, allowed) in new[] { (100, false), (20, true) }) {
            var solid = new Built(new GridShape(0, 0, 0, 0.5f, side, side, side), buried.Set);
            solid.Nodes.Length.Should().Be(1);
            var one = new byte[WallTrees.BlockLength(1, 0)];
            WallTrees.Write(solid.Tree(), buried.Set, one, out _).Should().BeTrue();
            WallTrees.TryRead(one, buried.Set, out _).Should().BeTrue();
            Put(one, NodesAt + 8, 1UL);
            WallTrees.TryRead(one, buried.Set, out _).Should().Be(allowed, $"one open child and nothing else changed, in a grid of {side} cells a side");
        }

        // The root's children are cubes of 64 cells: the eight in the grid are nodes, and none may be open.
        var root = built.Nodes[0];
        root.Mixed.Should().NotBe(0UL);
        root.Open.Should().Be(0UL);
        WallTrees.TryRead(Bits(NodesAt + 8, 1UL << 63), default, out _).Should().BeFalse("an open cube of 64 cells");
        // The second level's cubes are 16 cells, and may be open. Its nodes must follow one another as their parents say.
        var second = built.Nodes[1];
        second.Open.Should().NotBe(0UL);
        WallTrees.TryRead(Changed(NodesAt + 24 + 16, second.First + 1), buried.Set, out _).Should().BeFalse("a node whose children begin one late");
        WallTrees.TryRead(Changed(NodesAt + 2 * 24 + 20, built.Nodes[2].FirstLeaf + 1), buried.Set, out _).Should().BeFalse("a node whose open cubes are numbered one late");
        WallTrees.TryRead(Bits(NodesAt + 24, second.Mixed | second.Open), default, out _).Should().BeFalse("children that are both mixed and open");
    }

    [Fact]
    public void NoDamageToATreeMakesAQueryReadOutsideItOrRunOn() {
        // Every four bytes of the nodes in turn, set to each of these, in a tree that is used as it
        // is and not read from a block: nothing is checked first. Whatever comes back, nothing
        // throws and every call returns.
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var built = new Built(Odd, scene.Set);
        int[] values = [-1, 0, 1, 2, 7, 1000, int.MaxValue, int.MinValue, 0x55555555];
        var calls = 0;
        for (var at = 0; at < Math.Min(built.Nodes.Length * 24, 24 * 40); at += 4) {
            foreach (var value in values) {
                var nodes = (WallNode[])built.Nodes.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.MemoryMarshal.AsBytes(nodes.AsSpan()).Slice(at), value);
                var tree = new WallTree(Odd, Unit, Cap, nodes, built.Bricks);
                foreach (var (x, y, z) in new[] { (0.1f, 0.1f, 0.5f), (6f, 3f, 5f), (11f, 4.5f, 8f), (17.9f, 10f, 14f), (9f, 9.9f, 0.3f), (3f, 1f, 9f) }) {
                    WallTrees.RoomAt(tree, x, y, z, out _);
                    WallTrees.HasRoom(tree, x, y, z, 0.5f);
                    calls++;
                }
            }
        }
        calls.Should().BeGreaterThan(2000);
        // With bricks cut short, a cell that would be read past them reads as 0.
        var whole = built.Tree();
        var cut = new WallTree(Odd, Unit, Cap, built.Nodes, built.Bricks.AsSpan(0, 64));
        foreach (var (x, y, z) in Points(Odd).Take(5000)) {
            if (!WallTrees.RoomAt(whole, x, y, z, out var room)) { continue; }
            WallTrees.RoomAt(cut, x, y, z, out var less).Should().BeTrue();
            less.Should().BeLessThanOrEqualTo(room);
        }
    }

    [Fact]
    public void LevelsGoUpWithTheLongestSideAndBlockLengthRefusesWhatAnIntCannotHold() {
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 1, 1, 1)).Should().Be(1);
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 16, 3, 9)).Should().Be(1);
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 3, 17, 9)).Should().Be(2);
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 64, 64, 64)).Should().Be(2);
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 900, 98, 900)).Should().Be(4);
        WallTrees.Levels(new GridShape(0, 0, 0, 0.5f, 0, 4, 4)).Should().Be(0);
        WallTrees.BlockLength(2, 4).Should().Be(64 + 2 * 24 + 4 * 32);
        WallTrees.BlockLength(-1, 0).Should().Be(-1);
        WallTrees.BlockLength(0, -1).Should().Be(-1);
        WallTrees.BlockLength(0, int.MaxValue / 32).Should().Be(-1);
    }

    [Fact]
    public void TheDenseMapSaysWhetherThereIsRoomAsItsRoomSays() {
        var scene = new ShapeScene(Mixed, MixedCorners, MixedTriangles);
        var map = new WallMap(Odd, Unit, Cap, Dense(Odd, scene.Set));
        WallDistance.RoomAt(map, 3, 5, 12, out var room).Should().BeTrue();
        room.Should().BeInRange(0.3f, 0.99f);
        WallDistance.HasRoom(map, 3, 5, 12, room).Should().BeTrue();
        WallDistance.HasRoom(map, 3, 5, 12, room + 0.001f).Should().BeFalse();
        WallDistance.HasRoom(map, 3, 5, 12, 0f).Should().BeTrue();
        WallDistance.HasRoom(map, -5, 5, 12, 0f).Should().BeFalse("outside the grid, where room is refused");
        WallDistance.HasRoom(map, 6, 3, 5, 0.01f).Should().BeFalse("inside the turned box");
    }
}
