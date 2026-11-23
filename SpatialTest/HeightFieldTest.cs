using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class HeightFieldTest {
    private const double Slack = Shapes.Slack;

    // Rolling ground with a ripple on it, slight up to 9 m along U and strong beyond: blocks there need every height.
    private static double Hills(double u, double w) =>
        3 + 1.5 * Math.Sin(0.31 * u + 0.4) * Math.Cos(0.23 * w) + 0.3 * Math.Sin(1.7 * u) * Math.Sin(1.3 * w) * (u > 9 ? 1 : 0.1);

    // A plain slope with a wall of four metres across it, between the heights at 11 and 11.5 m.
    private static double Step(double u, double w) => 2 + 0.1 * u - 0.05 * w + (u > 11.2 ? 4 : 0);

    // 40 by 24 squares of half a metre: three blocks by two, the last of each a part block.
    private static HeightScene Ground(Func<double, double, double> ground, float bound = 0.25f, Solid[]? solids = null) =>
        new(ground, 40, 24, 0.5f, 0.5f, bound, solids: solids);

    [Fact]
    public void TheSurfaceKeptIsNowhereBelowTheGroundGivenAndNowhereMoreThanTheGapAboveIt() {
        foreach (var scene in new[] { Ground(Hills), Ground(Step) }) {
            scene.Field.Gap.Should().BeLessThanOrEqualTo(0.25f);
            var most = 0d;
            // Every height given, and three places between each two, in every block that holds the place.
            for (var j = 0; j <= scene.CountW * 4; j++) {
                for (var i = 0; i <= scene.CountU * 4; i++) {
                    double u = i * 0.125, w = j * 0.125, given = scene.GivenAt(u, w);
                    for (var blockW = 0; blockW < scene.BlocksW; blockW++) {
                        for (var blockU = 0; blockU < scene.BlocksU; blockU++) {
                            if (u < blockU * 8 || u > blockU * 8 + 8 || w < blockW * 8 || w > blockW * 8 + 8) { continue; }
                            var over = scene.KeptAt(blockU, blockW, u, w) - given;
                            over.Should().BeGreaterThanOrEqualTo(-1e-9, $"at {u}, {w} in block {blockU}, {blockW}");
                            most = Math.Max(most, over);
                        }
                    }
                }
            }
            most.Should().BeLessThanOrEqualTo(scene.Field.Gap + 1e-9);
            most.Should().BeGreaterThan(0.5 * scene.Field.Gap, "the gap is the most found, not a guess above it");
        }
    }

    [Fact]
    public void APlainSlopeIsKeptAtEveryEighthHeightAndCostsNothing() {
        var scene = Ground((u, w) => 2 + 0.3 * u - 0.2 * w);
        for (var blockW = 0; blockW < scene.BlocksW; blockW++) {
            for (var blockU = 0; blockU < scene.BlocksU; blockU++) { scene.Spacing(blockU, blockW).Should().Be(8); }
        }
        // 3 by 3 heights for a whole block, 2 by 3 and 3 by 2 for the part blocks, 2 by 2 for the corner.
        scene.Kept.Length.Should().Be(2 * 9 + 6 + 2 * 6 + 4);
        scene.Field.Gap.Should().BeLessThanOrEqualTo(2 * scene.Field.Step, "only the rounding up to a step");
    }

    [Fact]
    public void ASteepSpotCostsItsOwnBlocksAndNoOthers() {
        var scene = Ground(Step);
        for (var blockW = 0; blockW < scene.BlocksW; blockW++) {
            scene.Spacing(0, blockW).Should().Be(8);
            scene.Spacing(1, blockW).Should().Be(1, "the wall stands between two heights of this block");
            scene.Spacing(2, blockW).Should().Be(8);
        }
    }

    [Fact]
    public void APeakRaisesTheCornersOfTheFlatPieceItStandsOnAndNoOthers() {
        // Flat ground of 16 by 16 squares of a quarter metre, with one peak of 0.2 m at the height
        // 6 along U and 5 along W. Kept at every 8th height, the peak stands on one of the two
        // triangles of the first square of 2 m: the one on the U side of the line from its first
        // corner to its far one. That triangle's three corners are raised, and the fourth is not.
        var scene = new HeightScene((u, w) => u == 1.5 && w == 1.25 ? 0.2 : 0, 16, 16, 0.25f, 0.25f, 0.25f);
        scene.Spacing(0, 0).Should().Be(8);
        scene.KeptAt(0, 0, 0, 0).Should().BeApproximately(0.2, 1e-3);
        scene.KeptAt(0, 0, 2, 0).Should().BeApproximately(0.2, 1e-3);
        scene.KeptAt(0, 0, 2, 2).Should().BeApproximately(0.2, 1e-3);
        scene.KeptAt(0, 0, 0, 2).Should().BeApproximately(0, 1e-3);
        // No kept height beyond that square is raised.
        scene.KeptAt(0, 0, 4, 0).Should().BeApproximately(0, 1e-3);
        scene.KeptAt(0, 0, 4, 2).Should().BeApproximately(0, 1e-3);
        scene.KeptAt(0, 0, 0, 4).Should().BeApproximately(0, 1e-3);
        scene.KeptAt(0, 0, 2, 4).Should().BeApproximately(0, 1e-3);
        scene.KeptAt(0, 0, 4, 4).Should().BeApproximately(0, 1e-3);
    }

    [Fact]
    public void ABoundOfNothingKeepsEveryHeight() {
        var scene = Ground(Hills, bound: 0f);
        for (var blockW = 0; blockW < scene.BlocksW; blockW++) {
            for (var blockU = 0; blockU < scene.BlocksU; blockU++) { scene.Spacing(blockU, blockW).Should().Be(1); }
        }
        scene.Kept.Length.Should().Be(HeightFields.HeightsLength(40, 24));
        scene.Field.Gap.Should().BeLessThanOrEqualTo(scene.Field.Step, "a height is only rounded up to a step");
    }

    [Fact]
    public void AWiderBoundKeepsFewerHeights() {
        // Of 1118 heights. A change here is a change to what is packed: raise Shapes.Version with it.
        Ground(Hills, bound: 0.05f).Kept.Length.Should().Be(802);
        Ground(Hills, bound: 0.25f).Kept.Length.Should().Be(716);
        Ground(Hills, bound: 1f).Kept.Length.Should().Be(56);
    }

    [Fact]
    public void FlatGroundIsMeasuredFromItsSurfaceItsSidesAndItsFloor() {
        // 16 squares of a metre each way, at a height of 2, solid down to -3.
        var scene = new HeightScene((_, _) => 2, 16, 16, 1f, 1f, 0.25f, place: packed => packed.Placed(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, -3));
        scene.Distance(8, 5, 8).Should().BeApproximately(3f, 1e-5f, "above the surface");
        scene.Distance(8, 1, 8).Should().Be(0f, "under the surface");
        scene.Distance(8, -3, 8).Should().Be(0f, "on the floor");
        scene.Distance(-1.5f, 0, 8).Should().BeApproximately(1.5f, 1e-5f, "beside it, below its rim");
        scene.Distance(8, -4, 8).Should().BeApproximately(1f, 1e-5f, "under the floor");
        scene.Distance(-3, 6, 8).Should().BeApproximately(5f, 1e-5f, "off the rim: 3 out and 4 up");
        scene.Distance(-3, -7, -4).Should().BeApproximately(MathF.Sqrt(9 + 16 + 16), 1e-5f, "off a corner of the floor");
        scene.Distance(8, 60, 8, limit: 9f).Should().Be(9f, "nothing within the limit");
    }

    [Fact]
    public void TheDistanceIsThatToTheSameSolidAsTriangles() {
        // Blocks at every spacing, with the steps where they meet, and two solids that reach into the ground and stand clear of it.
        Solid[] solids = [Solid.Sphere(6, 7.5f, 4, 1.2f), Solid.Capsule(15, 3, 9, 17, 12, 10, 0.4f)];
        var scene = new HeightScene((u, w) => Hills(u, w) + (u > 11.2 ? 4 : 0), 40, 24, 0.5f, 0.5f, 0.25f, solids: solids);
        var others = new ShapeScene(solids);
        var spacings = new HashSet<int>();
        for (var blockU = 0; blockU < scene.BlocksU; blockU++) { spacings.Add(scene.Spacing(blockU, 0)); spacings.Add(scene.Spacing(blockU, 1)); }
        spacings.Count.Should().BeGreaterThan(1, "the ground must not be all of one spacing");

        int inside = 0, beside = 0;
        for (var i = 0; i < 26; i++) {
            for (var j = 0; j < 17; j++) {
                for (var k = 0; k < 19; k++) {
                    float x = -3 + (i + 0.37f) * 1.03f, y = -2.5f + (j + 0.61f) * 0.93f, z = -3 + (k + 0.13f) * 0.97f;
                    var ground = scene.Exact(x, y, z);
                    var exact = Math.Min(ground, others.Exact(x, y, z));
                    ((double)scene.Distance(x, y, z)).Should().BeInRange(exact - Slack, exact + Slack, $"at {x}, {y}, {z}");
                    if (ground == 0) { inside++; } else if (y < 2 && (x < 0 || x > 20 || z < 0 || z > 12)) { beside++; }
                }
            }
        }
        inside.Should().BeGreaterThan(500);
        beside.Should().BeGreaterThan(500);
    }

    [Fact]
    public void AHeightfieldAtAnAngleIsMeasuredAlongItsOwnAxes() {
        var plain = Ground(Hills);
        // Turned about z, then tilted about x, as ShapeScene.TurnedBox turns a box.
        var box = ShapeScene.TurnedBox(0, 0, 0, 1, 1, 1, 37, 21);
        var turned = new HeightScene(Hills, 40, 24, 0.5f, 0.5f, 0.25f, place: packed =>
            packed.Placed(5, -2, 7, box.UX, box.UY, box.UZ, box.VX, box.VY, box.VZ, box.WX, box.WY, box.WZ, packed.Lowest - 2));
        for (var i = 0; i < 12; i++) {
            for (var j = 0; j < 9; j++) {
                for (var k = 0; k < 8; k++) {
                    double u = -3 + i * 2.3, v = -1 + j * 1.1, w = -3 + k * 2.4;
                    float x = (float)(5 + box.UX * u + box.VX * v + box.WX * w), y = (float)(-2 + box.UY * u + box.VY * v + box.WY * w), z = (float)(7 + box.UZ * u + box.VZ * v + box.WZ * w);
                    turned.Distance(x, y, z).Should().BeApproximately(plain.Distance((float)u, (float)v, (float)w), 1e-4f, $"at {u}, {v}, {w} in its own axes");
                }
            }
        }
    }

    [Fact]
    public void EveryCellOverGroundHoldsItsDistanceRoundedDown() {
        const float unit = 1f / 128f;
        const byte cap = 128;
        // 20 by 12 squares of half a metre with the wall across them, and a sphere half in the ground.
        var scene = new HeightScene((u, w) => 1.5 + 0.4 * Math.Sin(0.9 * u) * Math.Cos(0.7 * w) + (u > 6.2 ? 1.5 : 0), 20, 12, 0.5f, 0.5f, 0.25f,
            solids: [Solid.Sphere(3, 2.2f, 3, 0.8f)]);
        var others = new ShapeScene(scene.Solids);
        var grid = new GridShape(-1, -1.75f, -1, 0.5f, 24, 14, 16);
        var map = new byte[grid.CellCount];
        for (var z = 0; z < grid.CountZ; z++) {
            WallDistance.MarkLayer(grid, scene.Set, unit, cap, z, map.AsSpan(z * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        int solid = 0, open = 0;
        for (var z = 0; z < grid.CountZ; z++) {
            for (var y = 0; y < grid.CountY; y++) {
                for (var x = 0; x < grid.CountX; x++) {
                    grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    var exact = Math.Min(scene.Exact(cx, cy, cz), others.Exact(cx, cy, cz));
                    var held = map[grid.IndexOf(x, y, z)];
                    ((double)held * unit).Should().BeLessThanOrEqualTo(exact, $"the cell at {cx}, {cy}, {cz} may not claim more room than there is");
                    if (held < cap) {
                        ((double)held * unit).Should().BeGreaterThan(exact - unit - 2 * Slack, $"the cell at {cx}, {cy}, {cz} may not claim much less");
                    } else {
                        exact.Should().BeGreaterThanOrEqualTo((double)cap * unit, $"the cell at {cx}, {cy}, {cz} holds the cap");
                    }
                    if (exact == 0) { solid++; } else if (held == cap) { open++; }
                }
            }
        }
        solid.Should().BeGreaterThan(500, "cells under the ground");
        open.Should().BeGreaterThan(500, "cells in the open");
    }

    // Flat ground of 16 by 16 squares of a quarter metre, with one peak of 0.2 m a metre in from
    // the first corner. Kept at every 8th height, the peak lifts the four kept heights round it,
    // so over the first 2 m each way the surface kept is flat at 0.2 where the ground is at 0.
    private static HeightScene Lifted(Solid[]? solids = null) =>
        new((u, w) => u == 1 && w == 1 ? 0.2 : 0, 16, 16, 0.25f, 0.25f, 0.25f, solids: solids);

    // The route of a ball from a point to open air, over cells of a fifth of a metre.
    private static RouteStatus RouteFrom(HeightScene scene, float x, float y, float z, float radius = 0.12f) {
        const float unit = 1f / 128f;
        const byte cap = 128;
        var grid = new GridShape(0, -0.4f, 0, 0.2f, 20, 12, 20);
        var wall = new byte[grid.CellCount];
        for (var layer = 0; layer < grid.CountZ; layer++) {
            WallDistance.MarkLayer(grid, scene.Set, unit, cap, layer, wall.AsSpan(layer * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        var map = new WallMap(grid, unit, cap, wall);
        SizeClass.TryMake(radius, map, out var size).Should().BeTrue();
        var region = new byte[grid.CellCount];
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-1, -1, -1, 5, 3, 5))], region, new int[Regions.QueueLength(grid)], out _, out _).Should().BeTrue();
        return Routes.Find(map, size, scene.Set, region, [], x, y, z, 3, 1.5f, 3, true,
            new float[Routes.SumsLength(grid)], new int[Routes.LinksLength(grid)], new int[Routes.HeapLength(grid)], new float[300]).Status;
    }

    [Fact]
    public void AnEndThatRestsOnTheGroundGivenGetsARouteThoughTheSurfaceKeptLiesAboveIt() {
        var scene = Lifted();
        scene.Spacing(0, 0).Should().Be(8);
        scene.Field.Gap.Should().BeApproximately(0.2f, 1e-3f);
        scene.GivenAt(1.5, 0.5).Should().Be(0);
        scene.KeptAt(0, 0, 1.5, 0.5).Should().BeApproximately(0.2, 1e-3);

        // A ball of 0.12 m resting on the ground has its centre 0.08 m under the surface kept:
        // more than the 5 cm that every end is allowed.
        scene.Distance(1.5f, 0.12f, 0.5f).Should().Be(0f);
        RouteFrom(scene, 1.5f, 0.12f, 0.5f).Should().Be(RouteStatus.Found);
        RouteFrom(scene, 1.5f, 0.3f, 0.5f).Should().Be(RouteStatus.Found, "an end above the surface kept, in a cell that is not open");
    }

    [Fact]
    public void AnEndFartherUnderTheSurfaceThanTheGapGetsNoRoute() {
        // A ball of 0.3 m, whose nearest open centre is 0.7 m up and which looks for one within
        // 0.87 m. The gap is 0.2 m and the allowance 5 cm, and the surface kept is at 0.2 m.
        RouteFrom(Lifted(), 1.5f, -0.02f, 0.5f, 0.3f).Should().Be(RouteStatus.Found, "0.22 m under the surface: within the gap and the allowance");
        RouteFrom(Lifted(), 1.5f, -0.1f, 0.5f, 0.3f).Should().Be(RouteStatus.Unreachable, "0.3 m under the surface: under the ground given too");
    }

    [Fact]
    public void AnEndUnderTheSurfaceKeptStillSeesOtherShapes() {
        // A plate over the end, itself under the surface kept: the line of sight out of the ground meets it.
        var scene = Lifted([Solid.Box(new Box(0.5f, 0.16f, 0, 2.5f, 0.18f, 1.5f))]);
        RouteFrom(scene, 1.5f, 0.12f, 0.5f).Should().Be(RouteStatus.Unreachable);
    }

    // A route for a ball over shapes, on a grid marked from them and flooded from everywhere: its result, and its points with the start put first.
    private static RouteResult Route(in ShapeSet shapes, GridShape grid, float radius, (float x, float y, float z) from, (float x, float y, float z) to, out (float x, float y, float z)[] way) {
        const float unit = 1f / 128f;
        const byte cap = 128;
        var wall = new byte[grid.CellCount];
        for (var layer = 0; layer < grid.CountZ; layer++) {
            WallDistance.MarkLayer(grid, shapes, unit, cap, layer, wall.AsSpan(layer * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        var map = new WallMap(grid, unit, cap, wall);
        SizeClass.TryMake(radius, map, out var size).Should().BeTrue();
        var region = new byte[grid.CellCount];
        Regions.FromSeeds(map, size, [], [Solid.Box(new Box(-100, -100, -100, 100, 100, 100))], region, new int[Regions.QueueLength(grid)], out _, out _).Should().BeTrue();
        var points = new float[300];
        var result = Routes.Find(map, size, shapes, region, [], from.x, from.y, from.z, to.x, to.y, to.z, true,
            new float[Routes.SumsLength(grid)], new int[Routes.LinksLength(grid)], new int[Routes.HeapLength(grid)], points);
        way = new (float, float, float)[result.Status == RouteStatus.Found ? result.Count + 1 : 0];
        if (way.Length > 0) { way[0] = from; }
        for (var i = 1; i < way.Length; i++) { way[i] = (points[3 * i - 3], points[3 * i - 2], points[3 * i - 1]); }
        return result;
    }

    // The least distance to the ground along a straight line, by the loop over its faces, every centimetre.
    private static double RoomAlong(HeightScene scene, (float x, float y, float z) a, (float x, float y, float z) b) {
        double dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 0.01));
        var least = double.PositiveInfinity;
        for (var k = 0; k <= steps; k++) { least = Math.Min(least, scene.Exact(a.x + dx * k / steps, a.y + dy * k / steps, a.z + dz * k / steps)); }
        return least;
    }

    [Fact]
    public void ARouteOverARidgeKeepsItsRadiusFromTheGround() {
        // Flat ground of 8 by 4 m with a ridge of 0.6 m across the middle, and a ball of 0.51 m
        // that starts and ends a metre above the flat, either side of the ridge. The straight
        // line between them passes 0.4 m over the ridge. The grid cannot say so, since no cell's
        // centre lies on that line: only the distance to the ground itself refuses it.
        const float radius = 0.51f;
        var scene = new HeightScene((u, _) => u >= 3.5 && u <= 4.5 ? 0.6 : 0, 32, 16, 0.25f, 0.25f, 0.25f);
        (float x, float y, float z) from = (1.5f, 1f, 2f), to = (6.5f, 1f, 2f);
        RoomAlong(scene, from, to).Should().BeLessThan(radius - 0.05, "the straight line comes too near the ridge");

        var result = Route(scene.Set, new GridShape(0, -0.5f, 0, 0.5f, 16, 8, 8), radius, from, to, out var way);
        result.Status.Should().Be(RouteStatus.Found);
        result.Ends.Should().Be(RouteEnds.None);
        way.Length.Should().BeGreaterThan(2, "more than the one straight leg");
        for (var i = 0; i + 1 < way.Length; i++) {
            RoomAlong(scene, way[i], way[i + 1]).Should().BeGreaterThanOrEqualTo(radius - Slack, $"leg {i} keeps the radius from the ground");
        }
    }

    [Fact]
    public void ASecondHeightfieldInASetIsMeasuredAsItself() {
        // Flat ground at 1 m, kept exactly, and 10 m along x from it the lifted ground of the tests above, whose gap is 0.2 m.
        var first = new HeightScene((_, _) => 1, 16, 16, 0.25f, 0.25f, 0.25f);
        var second = Lifted();
        first.Field.Gap.Should().BeLessThan(0.001f);
        HeightField[] fields = [first.Field, second.Field.Placed(10, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, second.Field.Floor).Stored(first.Blocks.Length, first.Kept.Length)];
        uint[] blocks = [.. first.Blocks, .. second.Blocks];
        ushort[] kept = [.. first.Kept, .. second.Kept];
        var boxes = new Box[2];
        var nodes = new BoxNode[Shapes.NodesLength(2)];
        var order = new int[2];
        Shapes.Build([], [], [], fields, blocks, kept, boxes, nodes, order, new int[Shapes.WorkspaceLength(2)], out var nodeCount).Should().BeTrue();
        var set = new ShapeSet([], [], [], fields, blocks, kept, boxes, nodes.AsSpan(0, nodeCount), order);

        float At(in ShapeSet shapes, float x, float y, float z) {
            Shapes.Distance(shapes, x, y, z, 100f, out var distance).Should().BeTrue();
            return distance;
        }
        At(set, 2, 5, 2).Should().BeApproximately(4f, 1e-4f, "above the first");
        At(set, 2, 0.5f, 2).Should().Be(0f, "under the first's surface");
        At(set, 11, 5, 1).Should().BeApproximately(4.8f, 1e-3f, "above the second, whose surface there is at 0.2 m");
        At(set, 11, 0.1f, 1).Should().Be(0f, "under the second's surface");
        At(set, 11, 0.5f, 1).Should().BeApproximately(0.3f, 1e-3f, "above the second, where the first would be solid");

        // An end that rests on the second's ground is under its surface by more than the first's gap allows.
        Route(set, new GridShape(10, -0.4f, 0, 0.2f, 20, 12, 20), 0.12f, (11.5f, 0.12f, 0.5f), (13, 1.5f, 3), out _).Status.Should().Be(RouteStatus.Found);
    }

    [Fact]
    public void BuildRefusesAHeightfieldThatMayNotBeStored() {
        var scene = Ground(Step);
        var field = scene.Field;
        bool Builds(HeightField changed, uint[]? blocks = null, ushort[]? kept = null) =>
            Shapes.Build([], [], [], [changed], blocks ?? scene.Blocks, kept ?? scene.Kept, new Box[1], new BoxNode[Shapes.NodesLength(1)], new int[1], new int[Shapes.WorkspaceLength(1)], out _);

        Builds(field).Should().BeTrue();
        Builds(field.Placed(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, field.Lowest + 0.5f)).Should().BeFalse("a floor above its lowest height");
        Builds(field.Placed(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, float.NaN)).Should().BeFalse("a floor that is not a number");
        Builds(field.Placed(0, 0, 0, 1.01f, 0, 0, 0, 1, 0, 0, 0, 1, field.Floor)).Should().BeFalse("an axis that is not of length 1");
        Builds(field.Placed(0, 0, 0, 1, 0, 0, 0.6f, 0.8f, 0, 0, 0, 1, field.Floor)).Should().BeFalse("axes that are not square to each other");
        Builds(field.Placed(1010, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, field.Floor)).Should().BeFalse("beyond the reach");
        Builds(field.Placed(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, -1100)).Should().BeFalse("a floor beyond the reach");
        Builds(field.Stored(1, 0)).Should().BeFalse("blocks that are not all there");
        Builds(field.Stored(0, 1)).Should().BeFalse("kept heights that are not all there");
        Builds(field.Stored(-1, 0)).Should().BeFalse();
        Builds(field, kept: scene.Kept[..^1]).Should().BeFalse("one kept height short");
        // The last block keeps 2 by 2 heights at the end of them all: read as every height it would run past the end.
        var blocks = (uint[])scene.Blocks.Clone();
        blocks[5] &= ~3u;
        Builds(field, blocks).Should().BeFalse("a block that says it keeps more heights than there are");

        // 20 by 12 squares: the blocks are 12 deep, which a spacing of 8 does not divide.
        var odd = new HeightScene((u, w) => 1 + 0.1 * u, 20, 12, 0.5f, 0.5f, 0.25f);
        odd.Spacing(0, 0).Should().Be(4);
        blocks = (uint[])odd.Blocks.Clone();
        blocks[0] |= 3;
        Shapes.Build([], [], [], [odd.Field], blocks, odd.Kept, new Box[1], new BoxNode[Shapes.NodesLength(1)], new int[1], new int[Shapes.WorkspaceLength(1)], out _)
            .Should().BeFalse("a spacing that does not divide the block's sides");
    }

    [Fact]
    public void PackRefusesWhatItCannotPack() {
        var heights = new float[9 * 5];
        var blocks = new uint[HeightFields.BlocksLength(8, 4)];
        var kept = new ushort[HeightFields.HeightsLength(8, 4)];
        bool Packs(float[] given, int countU = 8, int countW = 4, float sizeU = 1, float sizeW = 1, float bound = 0.25f, int blockRoom = 1, int keptRoom = 45) =>
            HeightFields.Pack(given, countU, countW, sizeU, sizeW, bound, blocks.AsSpan(0, blockRoom), kept.AsSpan(0, keptRoom), out _, out _);

        Packs(heights).Should().BeTrue();
        Packs(heights, countU: 0).Should().BeFalse();
        Packs(heights, countW: 5).Should().BeFalse("heights that are not as many as the counts say");
        Packs(heights, sizeU: 0).Should().BeFalse();
        Packs(heights, sizeW: float.NaN).Should().BeFalse();
        Packs(heights, bound: -0.1f).Should().BeFalse();
        Packs(heights, bound: float.PositiveInfinity).Should().BeFalse();
        Packs(heights, blockRoom: 0).Should().BeFalse("no room for the block");
        Packs(heights, keptRoom: 44).Should().BeFalse("one short of every height");
        var high = (float[])heights.Clone();
        high[7] = 1025;
        Packs(high).Should().BeFalse("a height beyond the reach");
        high[7] = float.NaN;
        Packs(high).Should().BeFalse("a height that is not a number");
        HeightFields.BlocksLength(16385, 4).Should().Be(0, "more squares along a side than a heightfield may have");
        HeightFields.BlocksLength(16384, 16384).Should().Be(1024 * 1024);
        HeightFields.HeightsLength(40, 24).Should().Be(43 * 26, "every height, and those between two blocks once for each");
    }
}
