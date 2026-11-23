using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class WallDistanceTest {
    private const float Unit = 1f / 128f;
    private const byte Cap = 128;
    private const double Slack = Shapes.Slack;

    // 16 cells of half a metre each way from the origin: centres at 0.25, 0.75 ... 7.75.
    private static readonly GridShape Grid = new(0, 0, 0, 0.5f, 16, 16, 16);

    private static byte[] Mark(GridShape grid, ShapeScene scene, float unit = Unit, byte cap = Cap) {
        var map = new byte[grid.CellCount];
        for (var z = 0; z < grid.CountZ; z++) {
            WallDistance.MarkLayer(grid, scene.Set, unit, cap, z, map.AsSpan(z * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        return map;
    }

    private static (float x, float y, float z) Centre(GridShape grid, int x, int y, int z) =>
        (grid.OriginX + (x + 0.5f) * grid.CellSize, grid.OriginY + (y + 0.5f) * grid.CellSize, grid.OriginZ + (z + 0.5f) * grid.CellSize);

    // Both sides of what a stored count promises, against a loop over every shape in double.
    private static void HoldsBothInequalities(GridShape grid, ShapeScene scene, byte[] map, float unit = Unit, byte cap = Cap) {
        for (var z = 0; z < grid.CountZ; z++) {
            for (var y = 0; y < grid.CountY; y++) {
                for (var x = 0; x < grid.CountX; x++) {
                    var (cx, cy, cz) = Centre(grid, x, y, z);
                    var exact = scene.Exact(cx, cy, cz);
                    var held = map[x + grid.CountX * (y + grid.CountY * z)];
                    ((double)held * unit).Should().BeLessThanOrEqualTo(exact, $"the cell at {cx}, {cy}, {cz} may not claim more room than there is");
                    if (held < cap) {
                        ((double)held * unit).Should().BeGreaterThan(exact - unit - 2 * Slack, $"the cell at {cx}, {cy}, {cz} may not claim much less");
                    } else {
                        exact.Should().BeGreaterThanOrEqualTo((double)cap * unit, $"the cell at {cx}, {cy}, {cz} holds the cap");
                    }
                }
            }
        }
    }

    private static readonly Solid[] MixedSolids = [
        Solid.Box(new Box(0, 0, 0, 8, 8, 0.3f)),
        ShapeScene.TurnedBox(2.4, 5.1, 3.3, 1.5, 0.4, 0.9, 37, 21),
        Solid.Sphere(5.5f, 2.5f, 4f, 1.2f),
        Solid.Capsule(1.1f, 1.3f, 1.2f, 3.3f, 2.2f, 6.1f, 0.35f),
        // Thinner than a cell.
        Solid.Box(new Box(6.02f, 5f, 2f, 6.08f, 7.5f, 6f)),
    ];

    // A wall of two triangles with no thickness at x = 4.6, and one large triangle slanting through the grid.
    private static readonly float[] MixedCorners = [4.6f, 0, 0, 4.6f, 8, 0, 4.6f, 8, 8, 4.6f, 0, 8, 0, 7.5f, 1, 8, 6.5f, 2, 3, 0.5f, 7.5f];
    private static readonly int[] MixedTriangles = [0, 1, 2, 0, 2, 3, 4, 5, 6];

    [Fact]
    public void EveryCellHoldsItsDistanceRoundedDown() {
        var scene = new ShapeScene(MixedSolids, MixedCorners, MixedTriangles);
        var map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        map.Should().Contain((byte)0, "some centres are inside a solid");
        map.Should().Contain(Cap, "some centres are far from everything");
    }

    [Fact]
    public void ACoarserUnitAndALowerCapHoldTheSameInequalities() {
        var scene = new ShapeScene(MixedSolids, MixedCorners, MixedTriangles);
        HoldsBothInequalities(Grid, scene, Mark(Grid, scene, unit: 0.1f, cap: 7), unit: 0.1f, cap: 7);
        HoldsBothInequalities(Grid, scene, Mark(Grid, scene, unit: 1f / 256f, cap: 255), unit: 1f / 256f, cap: 255);
    }

    [Fact]
    public void AGridThatIsNotAtTheOriginHoldsTheSameInequalities() {
        var grid = new GridShape(-3.3f, 1.7f, -0.45f, 0.4f, 21, 13, 17);
        var scene = new ShapeScene(MixedSolids, MixedCorners, MixedTriangles);
        HoldsBothInequalities(grid, scene, Mark(grid, scene));
    }

    [Fact]
    public void NeighbouringCellsDifferByNoMoreThanACell() {
        // True distances one cell apart differ by at most the cell's size; stored ones by that, a count and the slack.
        var map = Mark(Grid, new ShapeScene(MixedSolids, MixedCorners, MixedTriangles));
        var most = (int)(Grid.CellSize / Unit) + 2;
        for (var z = 0; z < 16; z++) {
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 15; x++) {
                    int a = x + 16 * (y + 16 * z), b = y + 16 * (x + 16 * z), c = z + 16 * (y + 16 * x);
                    Math.Abs(map[a] - map[a + 1]).Should().BeLessThanOrEqualTo(most);
                    Math.Abs(map[b] - map[b + 16]).Should().BeLessThanOrEqualTo(most);
                    Math.Abs(map[c] - map[c + 256]).Should().BeLessThanOrEqualTo(most);
                }
            }
        }
    }

    [Fact]
    public void AQuarterTurnOfTheSceneTurnsTheMap() {
        // Every number here is a whole count of 1/64, so the turn is exact: (x, y) goes to (8 - y, x).
        Solid[] solids = [Solid.Box(new Box(1, 2, 0.5f, 3.5f, 2.75f, 6)), Solid.Sphere(5.25f, 5.5f, 3, 1.125f), Solid.Capsule(6.5f, 1, 1.5f, 6.5f, 3, 5.5f, 0.375f)];
        float[] corners = [0.5f, 6.25f, 1, 3, 7.5f, 2, 1.75f, 5, 7];
        Solid[] turned = [Solid.Box(new Box(5.25f, 1, 0.5f, 6, 3.5f, 6)), Solid.Sphere(2.5f, 5.25f, 3, 1.125f), Solid.Capsule(7, 6.5f, 1.5f, 5, 6.5f, 5.5f, 0.375f)];
        float[] turnedCorners = [1.75f, 0.5f, 1, 0.5f, 3, 2, 3, 1.75f, 7];

        var map = Mark(Grid, new ShapeScene(solids, corners, [0, 1, 2]));
        var turnedMap = Mark(Grid, new ShapeScene(turned, turnedCorners, [0, 1, 2]));
        for (var z = 0; z < 16; z++) {
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 16; x++) {
                    turnedMap[(15 - y) + 16 * (x + 16 * z)].Should().Be(map[x + 16 * (y + 16 * z)], $"cell {x}, {y}, {z}");
                }
            }
        }
        map.Should().Contain(value => value > 0 && value < Cap);
    }

    [Fact]
    public void AShapeOutsideTheGridStillReachesIn() {
        // A wall 0.3 beyond the grid's low x face: the first column of centres is 0.55 from it.
        var scene = new ShapeScene([Solid.Box(new Box(-2, -2, -2, -0.3f, 10, 10))]);
        var map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        map[0].Should().Be((byte)Math.Floor((0.55 - Slack) / Unit));
        map[2].Should().Be(Cap, "1.55 away is past the cap of 1");
    }

    [Fact]
    public void NoShapesLeaveEveryCellAtTheCap() {
        Mark(Grid, new ShapeScene([])).Should().OnlyContain(value => value == Cap);
        var layer = new byte[Grid.LayerLength];
        WallDistance.MarkLayer(Grid, default, Unit, Cap, 3, layer).Should().BeTrue("default is a set with no shapes");
        layer.Should().OnlyContain(value => value == Cap);
    }

    [Fact]
    public void APostWhereFourCellsMeetLeavesThemAllOpenButClose() {
        // A post 2 cm thick up the line where four columns of cells meet, at x = y = 4.
        var scene = new ShapeScene([Solid.Capsule(4, 4, -1, 4, 4, 9, 0.01f)]);
        var map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        // Each of the four centres is half a cell's diagonal from the post's axis, less its radius.
        var expected = (byte)Math.Floor((Math.Sqrt(0.125) - 0.01 - Slack) / Unit);
        foreach (var (x, y) in new[] { (7, 7), (8, 7), (7, 8), (8, 8) }) { map[x + 16 * (y + 16 * 5)].Should().Be(expected); }
    }

    [Fact]
    public void ASheetWithNoThicknessIsFeltFromBothSides() {
        // On the boundary between two layers of cells, at z = 4: centres on either side are 0.25 away.
        float[] corners = [-1, -1, 4, 9, -1, 4, 9, 9, 4, -1, 9, 4];
        var scene = new ShapeScene([], corners, [0, 1, 2, 0, 2, 3]);
        var map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        var beside = (byte)Math.Floor((0.25 - Slack) / Unit);
        map[5 + 16 * (5 + 16 * 7)].Should().Be(beside, "just under the sheet");
        map[5 + 16 * (5 + 16 * 8)].Should().Be(beside, "just over the sheet");

        // Across the grid's diagonal, through x = y: the centres on the diagonal lie on it.
        float[] slanted = [-1, -1, -1, 9, 9, -1, 9, 9, 9, -1, -1, 9];
        scene = new ShapeScene([], slanted, [0, 1, 2, 0, 2, 3]);
        map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        map[6 + 16 * (6 + 16 * 3)].Should().Be(0, "a centre on the sheet");
        map[7 + 16 * (6 + 16 * 3)].Should().Be(map[6 + 16 * (7 + 16 * 3)], "one cell to either side");
    }

    [Fact]
    public void TheInsideOfAClosedShellOfTrianglesReadsAsRoom() {
        // A cube of twelve triangles from 2 to 6 on every axis. Nothing says which side is inside,
        // so its middle is 2 from every wall, as the space outside it would be.
        float[] corners = [2, 2, 2, 6, 2, 2, 6, 6, 2, 2, 6, 2, 2, 2, 6, 6, 2, 6, 6, 6, 6, 2, 6, 6];
        int[] triangles = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 2, 6, 1, 6, 5];
        var scene = new ShapeScene([], corners, triangles);
        var map = Mark(Grid, scene);
        HoldsBothInequalities(Grid, scene, map);
        map[8 + 16 * (8 + 16 * 8)].Should().Be(Cap, "the centre nearest the cube's middle");
        // The same cube as a solid box reads 0 there.
        Mark(Grid, new ShapeScene([Solid.Box(new Box(2, 2, 2, 6, 6, 6))]))[8 + 16 * (8 + 16 * 8)].Should().Be(0);
    }

    [Fact]
    public void AGridOfOneCellIsMarked() {
        var grid = new GridShape(0, 0, 0, 1f, 1, 1, 1);
        var scene = new ShapeScene([Solid.Sphere(0.5f, 0.5f, 2.5f, 1.5f)]);
        var map = Mark(grid, scene);
        map[0].Should().Be((byte)Math.Floor((0.5 - Slack) / Unit));
    }

    [Fact]
    public void ARefusedMarkReturnsFalse() {
        var scene = new ShapeScene([Solid.Sphere(1, 1, 1, 1)]);
        var layer = new byte[Grid.LayerLength];
        WallDistance.MarkLayer(Grid, scene.Set, Unit, Cap, 0, layer).Should().BeTrue();
        WallDistance.MarkLayer(Grid, scene.Set, Unit, Cap, 0, new byte[Grid.LayerLength - 1]).Should().BeFalse("a short layer");
        WallDistance.MarkLayer(Grid, scene.Set, 0f, Cap, 0, layer).Should().BeFalse("a unit of zero");
        WallDistance.MarkLayer(Grid, scene.Set, -Unit, Cap, 0, layer).Should().BeFalse();
        WallDistance.MarkLayer(Grid, scene.Set, float.NaN, Cap, 0, layer).Should().BeFalse();
        WallDistance.MarkLayer(Grid, scene.Set, float.PositiveInfinity, Cap, 0, layer).Should().BeFalse();
        WallDistance.MarkLayer(Grid, scene.Set, Unit, 0, 0, layer).Should().BeFalse("a cap of zero");
        WallDistance.MarkLayer(Grid, scene.Set, Unit, Cap, -1, layer).Should().BeFalse();
        WallDistance.MarkLayer(Grid, scene.Set, Unit, Cap, 16, layer).Should().BeFalse("a layer that is not there");
        WallDistance.MarkLayer(default, scene.Set, Unit, Cap, 0, layer).Should().BeFalse();
        var broken = new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, [], scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order);
        WallDistance.MarkLayer(Grid, broken, Unit, Cap, 0, layer).Should().BeFalse("spans that do not belong together");
    }

    [Fact]
    public void ARefusedLayerClaimsNoRoom() {
        // Refused before anything is worked out: what the layer held is not left to be read as room.
        var layer = new byte[Grid.LayerLength];
        layer.AsSpan().Fill(200);
        WallDistance.MarkLayer(Grid, default, 0f, Cap, 0, layer).Should().BeFalse();
        layer.Should().OnlyContain(value => value == 0);

        // Only the layer's own cells are written, refused or not: what follows them in a longer span is left.
        var longer = new byte[Grid.LayerLength + 4];
        longer.AsSpan().Fill(200);
        WallDistance.MarkLayer(Grid, default, 0f, Cap, 0, longer).Should().BeFalse();
        longer.AsSpan(0, Grid.LayerLength).ToArray().Should().OnlyContain(value => value == 0);
        longer.AsSpan(Grid.LayerLength).ToArray().Should().OnlyContain(value => value == 200);

        // Refused part way: a triangle names a corner that is not there, which is found only when
        // a cell near it is measured. By then the layer has been filled with the cap.
        var scene = new ShapeScene([], [4.6f, 0, 0, 4.6f, 8, 0, 4.6f, 8, 8], [0, 1, 2]);
        int[] damaged = [0, 1, 7];
        var set = new ShapeSet(scene.Solids, scene.Corners, damaged, scene.Boxes, scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order);
        layer.AsSpan().Fill(200);
        WallDistance.MarkLayer(Grid, set, Unit, Cap, 5, layer).Should().BeFalse();
        layer.Should().OnlyContain(value => value == 0);
        longer.AsSpan().Fill(200);
        WallDistance.MarkLayer(Grid, set, Unit, Cap, 5, longer).Should().BeFalse();
        longer.AsSpan(0, Grid.LayerLength).ToArray().Should().OnlyContain(value => value == 0);
        longer.AsSpan(Grid.LayerLength).ToArray().Should().OnlyContain(value => value == 200);
    }

    [Fact]
    public void ALayerLongerThanNeededIsFilledOnlyAsFarAsTheLayer() {
        var layer = new byte[Grid.LayerLength + 4];
        layer.AsSpan().Fill(7);
        WallDistance.MarkLayer(Grid, default, Unit, Cap, 0, layer).Should().BeTrue();
        layer.AsSpan(Grid.LayerLength).ToArray().Should().OnlyContain(value => value == 7);
    }

    // ---- RoomAt

    private static float Room(GridShape grid, byte[] map, float x, float y, float z, float unit = Unit, byte cap = Cap) {
        WallDistance.RoomAt(new WallMap(grid, unit, cap, map), x, y, z, out var room).Should().BeTrue();
        return room;
    }

    [Fact]
    public void TheRoomAtAPointIsNeverMoreThanTheTrueDistance() {
        var scene = new ShapeScene(MixedSolids, MixedCorners, MixedTriangles);
        var map = Mark(Grid, scene);
        var above = 0;
        // 23 steps a side, so points fall at every place between centres, the outer half cell included.
        for (var i = 0; i < 23; i++) {
            for (var j = 0; j < 23; j++) {
                for (var k = 0; k < 23; k++) {
                    float x = 8f * (i + 0.31f) / 23, y = 8f * (j + 0.57f) / 23, z = 8f * (k + 0.83f) / 23;
                    var room = Room(Grid, map, x, y, z);
                    ((double)room).Should().BeLessThanOrEqualTo(scene.Exact(x, y, z), $"at {x}, {y}, {z}");
                    if (room > 0) { above++; }
                }
            }
        }
        above.Should().BeGreaterThan(3000, "the bound must say something at most points, not only be safe");
    }

    [Fact]
    public void AtACentreTheRoomIsWhatTheCellHolds() {
        var map = Mark(Grid, new ShapeScene(MixedSolids, MixedCorners, MixedTriangles));
        foreach (var (x, y, z) in new[] { (3, 4, 9), (12, 2, 5), (9, 9, 9), (0, 15, 7), (5, 5, 0) }) {
            var (cx, cy, cz) = Centre(Grid, x, y, z);
            var held = map[x + 16 * (y + 16 * z)] * Unit;
            ((double)Room(Grid, map, cx, cy, cz)).Should().BeInRange(Math.Max(0, held - 2 * Slack), held);
        }
    }

    [Fact]
    public void InAnEmptyGridEveryPointProvesWhatALegNeeds() {
        // With every cell at the cap of 1 m, the least the map can prove is at the middle of four
        // centres in a plane: sqrt(1 - 0.5 * 0.5 / 2). A leg of the 0.51 m class needs 0.514 m.
        var map = Mark(Grid, new ShapeScene([]));
        var least = Math.Sqrt(1 - 0.125) - Slack;
        for (var step = 0; step <= 40; step++) {
            // Along the line midway between four rows of centres, and along one through cube middles.
            ((double)Room(Grid, map, 4f, 4f, 1f + step * 0.125f)).Should().BeGreaterThanOrEqualTo(least - 1e-6);
            ((double)Room(Grid, map, 1f + step * 0.125f, 4f, 4.25f)).Should().BeGreaterThanOrEqualTo(least - 1e-6);
        }
        Room(Grid, map, 4.25f, 4.25f, 4.25f).Should().Be(1f - Shapes.Slack, "a centre holds the cap, less the slack");
        Room(Grid, map, 4f, 4f, 4f).Should().Be(1f, "the middle of eight: the second bound, held to the cap");
        least.Should().BeGreaterThan(Math.Sqrt(0.51 * 0.51 + 0.0625 * 0.0625) + Slack);
    }

    [Fact]
    public void TheSecondBoundProvesMoreInTheMiddleOfEightCells() {
        // Every centre 0.51 from a floor would prove only 0.269 in the middle of eight by the first
        // bound. Here the eight hold 0.75 and 1.0: the first bound gives sqrt(0.78125 - 0.1875) = 0.77
        // and the second 0.25 + sqrt(0.5625 - 0.125) = 0.91.
        var scene = new ShapeScene([Solid.Box(new Box(-1, -1, -1, 9, 9, 3.5f))]);
        var map = Mark(Grid, scene);
        var room = Room(Grid, map, 4f, 4f, 4.5f);
        ((double)room).Should().BeLessThanOrEqualTo(1.0);
        ((double)room).Should().BeGreaterThan(0.9 - 0.01, "the second bound, less two counts of rounding");
    }

    [Fact]
    public void TheSecondBoundReadsTheRightFourCellsOnEverySide() {
        // A wall 0.6 from the nearer four of the eight cells round (4, 4, 4), on each of the six sides
        // in turn, and a point 2.5 cm inside the cube from that side's face: 0.625 from the wall.
        // Through that face the second bound proves 0.025 + sqrt(0.6 * 0.6 - 0.125), about 0.51. If
        // it read the farther four cells for that face it would prove more than there is.
        (Box wall, float x, float y, float z)[] sides = [
            (new Box(-9, -9, -9, 3.15f, 17, 17), 3.775f, 4, 4), (new Box(4.85f, -9, -9, 17, 17, 17), 4.225f, 4, 4),
            (new Box(-9, -9, -9, 17, 3.15f, 17), 4, 3.775f, 4), (new Box(-9, 4.85f, -9, 17, 17, 17), 4, 4.225f, 4),
            (new Box(-9, -9, -9, 17, 17, 3.15f), 4, 4, 3.775f), (new Box(-9, -9, 4.85f, 17, 17, 17), 4, 4, 4.225f),
        ];
        foreach (var (wall, x, y, z) in sides) {
            var scene = new ShapeScene([Solid.Box(wall)]);
            var room = Room(Grid, Mark(Grid, scene), x, y, z);
            ((double)room).Should().BeLessThanOrEqualTo(scene.Exact(x, y, z), $"at {x}, {y}, {z}");
            ((double)room).Should().BeInRange(0.47, 0.53, $"at {x}, {y}, {z}");
        }
    }

    [Fact]
    public void AThinPostInTheMiddleOfEightCellsLeavesNoRoomThere() {
        var scene = new ShapeScene([Solid.Capsule(4, 4, -1, 4, 4, 9, 0.01f)]);
        var map = Mark(Grid, scene);
        Room(Grid, map, 4f, 4f, 4f).Should().Be(0f, "the point is inside the post");
        ((double)Room(Grid, map, 4.2f, 4.1f, 4f)).Should().BeLessThanOrEqualTo(scene.Exact(4.2, 4.1, 4));
    }

    [Fact]
    public void ASmallShapeInTheMiddleOfEightCellsIsNotMissed() {
        // A ball 6 cm across at the middle of eight cells. Every centre is 0.403 from it: more than
        // 0.707 of a cell, less than 0.866. The second bound holds only above 0.866, where no shape
        // can be inside the cube; applied here it would prove 0.44 at a point that is inside the ball.
        var scene = new ShapeScene([Solid.Sphere(4, 4, 4, 0.03f)]);
        var map = Mark(Grid, scene);
        Room(Grid, map, 4f, 4f, 4f).Should().Be(0f);
        foreach (var (x, y, z) in new[] { (4.05f, 4f, 4f), (3.9f, 4.1f, 4.05f), (4.2f, 3.8f, 4.15f) }) {
            ((double)Room(Grid, map, x, y, z)).Should().BeLessThanOrEqualTo(scene.Exact(x, y, z));
        }
    }

    [Fact]
    public void InTheOuterHalfCellTheRoomIsStillSafe() {
        var scene = new ShapeScene([Solid.Box(new Box(-2, -2, -2, -0.3f, 10, 10))]);
        var map = Mark(Grid, scene);
        // 0.01 inside the grid's low x face: the wall is 0.31 away, and the nearest centre 0.24 farther in.
        var room = Room(Grid, map, 0.01f, 3.25f, 3.25f);
        ((double)room).Should().BeLessThanOrEqualTo(0.31);
        ((double)room).Should().BeGreaterThan(0.31 - Unit - 3 * Slack);
        // In a grid of one cell every point but the centre is in the outer half cell.
        var one = new GridShape(0, 0, 0, 1f, 1, 1, 1);
        var oneMap = Mark(one, new ShapeScene([Solid.Sphere(0.5f, 0.5f, 2.5f, 1.5f)]));
        ((double)Room(one, oneMap, 0.5f, 0.5f, 0.9f)).Should().BeLessThanOrEqualTo(0.1);
    }

    [Fact]
    public void OnTheGridsFacesTheRoomIsStillAnsweredAndSafe() {
        // Room needs no cell to belong to, so every face of the grid's box is inside, the upper ones too.
        var scene = new ShapeScene(MixedSolids, MixedCorners, MixedTriangles);
        var map = Mark(Grid, scene);
        foreach (var (x, y, z) in new[] { (8f, 3.3f, 5.1f), (2.7f, 8f, 6.2f), (5.1f, 2.2f, 8f), (8f, 8f, 8f), (0f, 0f, 8f), (0f, 4.4f, 3.3f) }) {
            WallDistance.RoomAt(new WallMap(Grid, Unit, Cap, map), x, y, z, out var room).Should().BeTrue($"at {x}, {y}, {z}");
            ((double)room).Should().BeLessThanOrEqualTo(scene.Exact(x, y, z), $"at {x}, {y}, {z}");
        }
        // In an empty grid a point on an upper face is half a cell from the nearest centre, which holds the cap.
        var empty = Mark(Grid, new ShapeScene([]));
        ((double)Room(Grid, empty, 8f, 4.25f, 4.25f)).Should().BeApproximately(1 - 0.25 - Slack, 1e-6);
    }

    [Fact]
    public void EveryCornerOfTheGridsOwnBoundsHasRoomToAsk() {
        // An origin and a cell size that are not whole counts of anything: the bounds a caller sees
        // are rounded to single, and a corner of them must still be inside.
        var grid = new GridShape(0.1f, 0.1f, 0.1f, 0.3f, 7, 9, 11);
        var map = Mark(grid, new ShapeScene([]));
        var bounds = grid.Bounds;
        foreach (var x in new[] { bounds.MinX, bounds.MaxX }) {
            foreach (var y in new[] { bounds.MinY, bounds.MaxY }) {
                foreach (var z in new[] { bounds.MinZ, bounds.MaxZ }) {
                    WallDistance.RoomAt(new WallMap(grid, Unit, Cap, map), x, y, z, out var room).Should().BeTrue($"at {x}, {y}, {z}");
                    // Half a cell's diagonal from the nearest centre, which holds the cap.
                    ((double)room).Should().BeApproximately(1 - 0.15 * Math.Sqrt(3) - Slack, 1e-5);
                }
            }
        }
        WallDistance.RoomAt(new WallMap(grid, Unit, Cap, map), bounds.MaxX + 0.001f, 1, 1, out _).Should().BeFalse();
        WallDistance.RoomAt(new WallMap(grid, Unit, Cap, map), 1, bounds.MinY - 0.001f, 1, out _).Should().BeFalse();
    }

    [Fact]
    public void ARefusedRoomReturnsFalse() {
        var map = Mark(Grid, new ShapeScene([]));
        WallDistance.RoomAt(new WallMap(Grid, Unit, Cap, map), -0.01f, 1, 1, out _).Should().BeFalse("outside the grid");
        WallDistance.RoomAt(new WallMap(Grid, Unit, Cap, map), 1, 8.01f, 1, out _).Should().BeFalse("past an upper face");
        WallDistance.RoomAt(new WallMap(Grid, Unit, Cap, map), 1, float.NaN, 1, out _).Should().BeFalse();
        WallDistance.RoomAt(new WallMap(Grid, Unit, Cap, map.AsSpan(0, map.Length - 1)), 1, 1, 1, out _).Should().BeFalse("a short array");
        WallDistance.RoomAt(new WallMap(Grid, 0f, Cap, map), 1, 1, 1, out _).Should().BeFalse();
        WallDistance.RoomAt(new WallMap(Grid, Unit, 0, map), 1, 1, 1, out _).Should().BeFalse();
        WallDistance.RoomAt(default, 1, 1, 1, out _).Should().BeFalse();
        new WallMap(Grid, Unit, Cap, map).IsValid.Should().BeTrue();
    }
}
