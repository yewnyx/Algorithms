using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class TreeCostsTest {
    private const float Radius = 0.51f;

    private static readonly Solid[] Mixed = [
        Solid.Box(new Box(-1, -1, -1, 20, 0.3f, 20)),
        ShapeScene.TurnedBox(6, 3, 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(11, 4.5f, 8, 1.6f),
        Solid.Capsule(3, 1, 9, 14, 7, 2, 0.35f),
        Solid.Box(new Box(15.5f, 0, 0, 30, 30, 30)),
    ];

    private static readonly GridShape Odd = new(-0.3f, -0.7f, 0.2f, 0.5f, 37, 22, 29);

    private static readonly ShapeScene MixedShapes = new(Mixed);

    private static TreeScene Scene() => new(Odd, MixedShapes.Set);

    // A tree's costs as a dense grid would hold them: each cell its leaf's cost, and 0 where it is in no leaf.
    private static byte[] Spread(WallTree tree, byte[] layer) {
        var grid = tree.Shape;
        var cost = new byte[grid.CellCount];
        for (var z = 0; z < grid.CountZ; z++) {
            for (var y = 0; y < grid.CountY; y++) {
                for (var x = 0; x < grid.CountX; x++) {
                    grid.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                    if (TreeCosts.At(tree, layer, cx, cy, cz, out var value)) { cost[grid.IndexOf(x, y, z)] = value; }
                }
            }
        }
        return cost;
    }

    [Fact]
    public void AFillGivesEveryLeafThatHoldsAFilledCellsCentreTheCost() {
        var scene = Scene();
        var tree = scene.Tree;
        var layer = new byte[TreeCosts.LayerLength(tree)];
        layer.Length.Should().BeGreaterThan(100);
        var solid = Solid.Sphere(8, 6, 12, 2.2f);
        var dense = new byte[Odd.CellCount];
        CellCost.Fill(Odd, solid, 40, dense).Should().BeTrue();
        TreeCosts.Fill(tree, solid, 40, layer, out var changed, out var opened).Should().BeTrue();
        (changed > 0, opened).Should().Be((true, 0));
        var spread = Spread(tree, layer);
        int filled = 0, more = 0;
        for (var cell = 0; cell < Odd.CellCount; cell++) {
            Odd.CentreOf(cell % Odd.CountX, cell / Odd.CountX % Odd.CountY, cell / Odd.LayerLength, out var cx, out var cy, out var cz);
            var inLeaf = TreeCosts.At(tree, layer, cx, cy, cz, out _);
            if (dense[cell] == 40 && inLeaf) { filled++; spread[cell].Should().Be(40, "a cell the solid holds gives its leaf the cost"); }
            if (dense[cell] == 0 && spread[cell] == 40) { more++; }
        }
        filled.Should().BeGreaterThan(50);
        more.Should().BeGreaterThan(0, "a leaf's cost is all its cells', not only those the solid holds");
        // The same fill again changes nothing.
        TreeCosts.Fill(tree, solid, 40, layer, out changed, out opened).Should().BeTrue();
        (changed, opened).Should().Be((0, 0));
        // Closed, then part of it opened: the leaves opened are counted.
        TreeCosts.Fill(tree, solid, CellCost.Closed, layer, out changed, out opened).Should().BeTrue();
        (changed > 0, opened).Should().Be((true, 0));
        TreeCosts.Fill(tree, Solid.Sphere(8, 6, 12, 1f), 20, layer, out changed, out opened).Should().BeTrue();
        (changed > 0, opened).Should().Be((true, changed));
        // What cannot be used.
        TreeCosts.Fill(tree, solid, 40, new byte[layer.Length - 1], out _, out _).Should().BeFalse();
        TreeCosts.Fill(tree, solid, 40, new byte[layer.Length + 1], out _, out _).Should().BeFalse();
        TreeCosts.Fill(tree, Solid.Sphere(float.NaN, 6, 12, 1), 40, layer, out _, out _).Should().BeFalse();
        TreeCosts.At(tree, layer[..^1], 8, 6, 12, out _).Should().BeFalse();
        TreeCosts.At(tree, layer, 8, 60, 12, out var outside).Should().BeFalse();
        outside.Should().Be(0);
    }

    [Fact]
    public void AFloodOverATreesCostsIsTheDenseFloodOverTheSameCostsInItsCells() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        // A slab closed right across the room, a hole closed in the floor's air, and costs that close nothing.
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(8.2f, -1, -1, 8.8f, 30, 30)), CellCost.Closed, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Sphere(3, 4, 4, 1.2f), CellCost.Closed, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(12, 0, 0, 14, 10, 14)), 64, layer, out _, out _).Should().BeTrue();
        var spread = Spread(tree, layer);
        Solid[] seeds = [Solid.Sphere(2, 6, 10, 0.8f), Solid.Sphere(13, 6, 10, 0.8f)];

        var dense = new byte[Odd.CellCount];
        Regions.FromSeeds(scene.Map, size, spread, seeds, dense, new int[Regions.QueueLength(Odd)], out var denseReached, out var denseEmpty).Should().BeTrue();
        var region = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, layer, seeds, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)],
            out var length, out var reached, out var empty).Should().BeTrue();
        (reached, empty).Should().Be((denseReached, denseEmpty));
        region = region[..length];
        var shut = 0;
        for (var cell = 0; cell < Odd.CellCount; cell++) {
            Odd.CentreOf(cell % Odd.CountX, cell / Odd.CountX % Odd.CountY, cell / Odd.LayerLength, out var cx, out var cy, out var cz);
            TreeRegions.IsOpen(tree, region, cx, cy, cz).Should().Be(dense[cell] != 0, $"at {cx}, {cy}, {cz}");
            if (spread[cell] == CellCost.Closed) { shut++; TreeRegions.IsOpen(tree, region, cx, cy, cz).Should().BeFalse(); }
        }
        shut.Should().BeGreaterThan(100);
        // The slab splits the room: the two seeds make two regions.
        TreeRegions.IsOpen(tree, region, 2, 6, 10).Should().BeTrue();
        TreeRegions.IsOpen(tree, region, 13, 6, 10).Should().BeTrue();
        // Without the costs, one.
        var whole = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, seeds, whole, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out _, out var all, out _).Should().BeTrue();
        all.Should().BeGreaterThan(reached);
        // The costs held are those of the leaves reached: a cheap value beyond the slab, where no seed reached, is not among them.
        var beyond = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(-5, -5, -5, 8.2f, 40, 40)), 40, beyond, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(8.8f, -5, -5, 40, 40, 40)), 5, beyond, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(8.2f, -1, -1, 8.8f, 30, 30)), CellCost.Closed, beyond, out _, out _).Should().BeTrue();
        var oneSide = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, beyond, [Solid.Sphere(2, 6, 10, 0.8f)], oneSide, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out var used, out _, out _).Should().BeTrue();
        var marks = new ulong[4];
        TreeRegions.CostsIn(tree, oneSide[..used], beyond, marks).Should().BeTrue();
        CellCost.Least(marks, []).Should().Be(40, "only the side a seed reached");
        // A layer that is not the tree's is refused.
        TreeRegions.FromSeeds(tree, size, layer[..^1], seeds, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out _, out _, out _).Should().BeFalse();
    }
    // A route over a tree's costs: its points from the start on, and what it came to.
    private sealed record Routed(RouteResult Result, (float X, float Y, float Z)[] Points);

    private static Routed Route(WallTree tree, SizeClass size, ShapeSet set, byte[] region, byte[] layer, (float X, float Y, float Z) from, (float X, float Y, float Z) to,
        byte least, byte[]? table = null, bool straighten = true) {
        var points = new float[3 * 400];
        var result = TreeRoutes.Find(tree, size, set, region, layer, from.X, from.Y, from.Z, to.X, to.Y, to.Z, straighten,
            new float[TreeRoutes.SumsLength(24 * 1024)], new int[TreeRoutes.LinksLength(24 * 1024)], points, least, 0.25f, table ?? []);
        var list = new List<(float X, float Y, float Z)> { from };
        for (var i = 0; i < Math.Min(result.Count, 400); i++) { list.Add((points[3 * i], points[3 * i + 1], points[3 * i + 2])); }
        return new Routed(result, list.ToArray());
    }

    // How many points along a route, one every 2 cm, lie in leaves that cost at least so much.
    private static int Samples(WallTree tree, byte[] layer, Routed route, int atLeast) {
        var count = 0;
        for (var i = 1; i < route.Points.Length; i++) {
            var (ax, ay, az) = route.Points[i - 1];
            var (bx, by, bz) = route.Points[i];
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) / 0.02));
            for (var k = 0; k <= steps; k++) {
                var f = (float)k / steps;
                if (TreeCosts.At(tree, layer, ax + (bx - ax) * f, ay + (by - ay) * f, az + (bz - az) * f, out var value) && value >= atLeast) { count++; }
            }
        }
        return count;
    }

    private static byte Least(WallTree tree, byte[] region, byte[] layer, byte[]? table = null) {
        var present = new ulong[4];
        TreeRegions.CostsIn(tree, region, layer, present).Should().BeTrue();
        return CellCost.Least(present, table ?? []);
    }

    // Open centres all over the scene, a few hundred.
    private static List<(float X, float Y, float Z)> Ends(WallTree tree, byte[] region) {
        var ends = new List<(float X, float Y, float Z)>();
        for (var cell = 0; cell < Odd.CellCount; cell += 53) {
            Odd.CentreOf(cell % Odd.CountX, cell / Odd.CountX % Odd.CountY, cell / Odd.LayerLength, out var cx, out var cy, out var cz);
            if (TreeRegions.IsOpen(tree, region, cx, cy, cz)) { ends.Add((cx, cy, cz)); }
        }
        return ends;
    }

    private static byte[] Flood(WallTree tree, SizeClass size, byte[] layer) {
        var region = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, layer, [Solid.Box(new Box(-5, -5, -5, 40, 40, 40))], region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)],
            out var length, out _, out _).Should().BeTrue();
        return region[..length];
    }

    [Fact]
    public void ARouteWithATableIsTheRouteOverTheCostsPutThroughIt() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        // What the leaves are: danger across the middle, fog in a corner, some of both.
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(7, -1, -1, 9.5f, 30, 30)), 0x80, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(-1, -1, 9, 6, 30, 30)), 0x0C, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Sphere(12, 5, 4, 2.5f), 0x3F, layer, out _, out _).Should().BeTrue();
        var region = Flood(tree, size, layer);
        // Danger dear, and clear cells dear so the least is above 0.
        var table = new byte[CellCost.TableLength];
        for (var b = 0; b < CellCost.Closed; b++) { table[b] = (byte)(10 * (b >> 4) + 30 - 2 * (b & 15)); }
        table[CellCost.Closed] = CellCost.Closed;
        var mapped = layer.Select(b => table[b]).ToArray();
        var least = Least(tree, region, layer, table);
        least.Should().Be(Least(tree, region, mapped)).And.BeGreaterThan(0);
        var ends = Ends(tree, region);
        int found = 0, differ = 0;
        for (var k = 0; k + 1 < ends.Count; k += 3) {
            var (from, to) = (ends[k], ends[ends.Count - 1 - k]);
            var withTable = Route(tree, size, MixedShapes.Set, region, layer, from, to, least, table);
            var throughIt = Route(tree, size, MixedShapes.Set, region, mapped, from, to, least);
            (withTable.Result.Status, withTable.Result.Ends, withTable.Result.Count, withTable.Result.Expanded, withTable.Result.Cost)
                .Should().Be((throughIt.Result.Status, throughIt.Result.Ends, throughIt.Result.Count, throughIt.Result.Expanded, throughIt.Result.Cost), $"from {from} to {to}");
            withTable.Points.Should().Equal(throughIt.Points);
            if (withTable.Result.Status != RouteStatus.Found) { continue; }
            found++;
            var without = Route(tree, size, MixedShapes.Set, region, [], from, to, 0);
            if (!without.Points.SequenceEqual(withTable.Points)) { differ++; }
        }
        found.Should().BeGreaterThan(30);
        differ.Should().BeGreaterThan(0, "the costs change the way");
    }

    [Fact]
    public void ADearLeafIsGoneRoundAndAClosedOneIsNeverPassedThrough() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        var layer = new byte[TreeCosts.LayerLength(tree)];
        // A dear block in the middle of the room, and a closed slab with the room's far end beyond it.
        TreeCosts.Fill(tree, Solid.Box(new Box(6.5f, 3, 4, 9.5f, 8, 9)), 200, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(-1, 8.5f, -1, 30, 9, 30)), CellCost.Closed, layer, out _, out _).Should().BeTrue();
        var region = Flood(tree, size, layer);
        var least = Least(tree, region, layer);
        // Across the dear block: the route with costs keeps out of it, and the one with none goes through.
        var across = Route(tree, size, MixedShapes.Set, region, layer, (3.2f, 5.7f, 6.7f), (12.7f, 5.7f, 6.7f), least);
        var through = Route(tree, size, MixedShapes.Set, region, [], (3.2f, 5.7f, 6.7f), (12.7f, 5.7f, 6.7f), 0);
        (across.Result.Status, through.Result.Status).Should().Be((RouteStatus.Found, RouteStatus.Found));
        Samples(tree, layer, through, 200).Should().BeGreaterThan(0);
        Samples(tree, layer, across, 200).Should().Be(0);
        across.Result.Cost.Should().BeLessThan(through.Result.Cost * (1 + 200 / 16f));
        // Everywhere: no route passes through a closed leaf, and none reaches beyond the slab.
        var ends = Ends(tree, region);
        var found = 0;
        for (var k = 0; k + 1 < ends.Count; k += 2) {
            var route = Route(tree, size, MixedShapes.Set, region, layer, ends[k], ends[ends.Count - 1 - k], least);
            if (route.Result.Status != RouteStatus.Found) { continue; }
            found++;
            Samples(tree, layer, route, CellCost.Closed).Should().Be(0, $"from {ends[k]} to {ends[ends.Count - 1 - k]}");
        }
        found.Should().BeGreaterThan(30);

        // A block closed by a table for its kind of agent only: routes go round it, and one that starts in it is routed out of it.
        var shut = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(4, 3, 3, 6, 9, 10)), 90, shut, out _, out _).Should().BeTrue();
        var shutRegion = Flood(tree, size, shut);
        var table = Enumerable.Range(0, CellCost.TableLength).Select(b => b == 90 ? CellCost.Closed : (byte)b).ToArray();
        // A slab the table closes right across the room: a way from one side to the other is not found through it.
        var slab = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(7.2f, -5, -5, 8.2f, 40, 40)), 90, slab, out _, out _).Should().BeTrue();
        var slabRegion = Flood(tree, size, slab);
        var overSlab = Route(tree, size, MixedShapes.Set, slabRegion, slab, (3.2f, 5.7f, 6.7f), (12.7f, 5.7f, 6.7f), 0, table);
        overSlab.Result.Status.Should().NotBe(RouteStatus.Found, "no way crosses a slab closed to its kind of agent");
        Route(tree, size, MixedShapes.Set, slabRegion, slab, (3.2f, 5.7f, 6.7f), (12.7f, 5.7f, 6.7f), 0).Result.Status.Should().Be(RouteStatus.Found);
        // Among the open cells: ends outside the block are joined round it, and one inside it is routed out of it.
        var cellsOf = Ends(tree, shutRegion);
        bool InBlock((float X, float Y, float Z) p) => TreeCosts.At(scene.Tree, shut, p.X, p.Y, p.Z, out var v) && v == 90;
        var outside = cellsOf.Where(p => !InBlock(p)).ToList();
        var inside = cellsOf.Where(InBlock).ToList();
        inside.Count.Should().BeGreaterThan(0);
        int crossed = 0, kept = 0;
        for (var k = 0; k + 1 < outside.Count; k++) {
            var (from, to) = (outside[k], outside[outside.Count - 1 - k]);
            var plain = Route(tree, size, MixedShapes.Set, shutRegion, shut, from, to, 0);
            if (Samples(tree, shut, plain, 90) == 0) { continue; }
            crossed++;
            var priced = Route(tree, size, MixedShapes.Set, shutRegion, shut, from, to, 0, table);
            if (priced.Result.Status != RouteStatus.Found) { continue; }
            kept++;
            Samples(tree, shut, priced, 90).Should().Be(0, $"from {from} to {to}, round the block");
        }
        (crossed > 0, kept > 0).Should().Be((true, true), "some ways without the table cross the block, and with it are found round it");
        var outOf = Route(tree, size, MixedShapes.Set, shutRegion, shut, inside[0], outside[^1], 0, table);
        outOf.Result.Status.Should().Be(RouteStatus.Found);
        outOf.Result.Ends.Should().HaveFlag(RouteEnds.FirstLegUnproven, "no leg leaves a leaf its kind of agent may not be in");
        // As over counts with the same costs in every cell: a start in the block is routed out where its cell is beside an
        // open cell out of it, and is unreachable where it is not; a goal in it is unreachable.
        var spread = Spread(tree, shut);
        var dense = new byte[Odd.CellCount];
        Solid[] all = [Solid.Box(new Box(-5, -5, -5, 40, 40, 40))];
        Regions.FromSeeds(scene.Map, size, spread, all, dense, new int[Regions.QueueLength(Odd)], out _, out _).Should().BeTrue();
        RouteStatus Dense((float X, float Y, float Z) from, (float X, float Y, float Z) to) =>
            Routes.Find(scene.Map, size, MixedShapes.Set, dense, spread, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true, new float[2 * Odd.CellCount],
                new int[Odd.CellCount], new int[Odd.CellCount], new float[3 * 2000], 0, 0f, table).Status;
        var starts = new List<(float X, float Y, float Z)>();
        for (var cell = 0; cell < Odd.CellCount; cell++) {
            Odd.CentreOf(cell % Odd.CountX, cell / Odd.CountX % Odd.CountY, cell / Odd.LayerLength, out var cx, out var cy, out var cz);
            if (dense[cell] != 0 && InBlock((cx, cy, cz))) { starts.Add((cx, cy, cz)); }
        }
        int routedOut = 0, notOut = 0;
        for (var k = 0; k < starts.Count; k += 3) {
            var to = outside[k % outside.Count];
            var tree1 = Route(tree, size, MixedShapes.Set, shutRegion, shut, starts[k], to, 0, table).Result.Status;
            tree1.Should().Be(Dense(starts[k], to), $"from {starts[k]} to {to}");
            if (tree1 == RouteStatus.Found) { routedOut++; } else { notOut++; }
            Route(tree, size, MixedShapes.Set, shutRegion, shut, to, starts[k], 0, table).Result.Status.Should().Be(RouteStatus.Unreachable);
            Dense(to, starts[k]).Should().Be(RouteStatus.Unreachable);
        }
        (routedOut > 5, notOut > 5).Should().Be((true, true));
    }

    // What a route costs as flown over the costs, sampled every 2 cm.
    private static double Flown(WallTree tree, byte[] layer, Routed route) {
        var flown = 0d;
        for (var i = 1; i < route.Points.Length; i++) {
            var (ax, ay, az) = route.Points[i - 1];
            var (bx, by, bz) = route.Points[i];
            var length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            var steps = Math.Max(1, (int)Math.Ceiling(length / 0.001));
            for (var k = 0; k < steps; k++) {
                var f = (k + 0.5f) / steps;
                TreeCosts.At(tree, layer, ax + (bx - ax) * f, ay + (by - ay) * f, az + (bz - az) * f, out var value);
                flown += length / steps * (1 + value / 16d);
            }
        }
        return flown;
    }

    [Fact]
    public void ARouteCostsNoMoreAsFlownThanItsWayAndALeastCostSavesWork() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        // A dear slab right across the room, which every way between its two sides must cross.
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(7, -1, -1, 9, 30, 30)), 160, layer, out _, out _).Should().BeTrue();
        var region = Flood(tree, size, layer);
        var ends = Ends(tree, region);
        int compared = 0, straighter = 0;
        for (var k = 0; k + 1 < ends.Count; k += 2) {
            var (from, to) = (ends[k], ends[ends.Count - 1 - k]);
            var pulled = Route(tree, size, MixedShapes.Set, region, layer, from, to, 0);
            if (pulled.Result.Status != RouteStatus.Found) { continue; }
            var cells = Route(tree, size, MixedShapes.Set, region, layer, from, to, 0, null, false);
            compared++;
            var (a, b) = (Flown(tree, layer, pulled), Flown(tree, layer, cells));
            a.Should().BeLessThanOrEqualTo(b * 1.001 + 1e-3, $"from {from} to {to}: pulled straight it costs no more than its way");
            if (a < b * 0.99) { straighter++; }
        }
        compared.Should().BeGreaterThan(30);
        straighter.Should().BeGreaterThan(0);
        // Every leaf costs 32: told so, a search that must go round takes up fewer places.
        var dear = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(-5, -5, -5, 40, 40, 40)), 32, dear, out _, out _).Should().BeTrue();
        var dearRegion = Flood(tree, size, dear);
        int searched = 0, fewer = 0;
        for (var k = 0; k + 1 < ends.Count; k += 3) {
            var told = Route(tree, size, MixedShapes.Set, dearRegion, dear, ends[k], ends[ends.Count - 1 - k], 32);
            var untold = Route(tree, size, MixedShapes.Set, dearRegion, dear, ends[k], ends[ends.Count - 1 - k], 0);
            if (untold.Result.Expanded == 0 || told.Result.Expanded == 0) { continue; }
            searched++;
            if (told.Result.Expanded < untold.Result.Expanded) { fewer++; }
        }
        searched.Should().BeGreaterThan(10);
        fewer.Should().BeGreaterThan(searched / 2);
    }

    [Fact]
    public void TheStraightLegIsTakenAtOnceWhereEveryLeafCostsTheLeast() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(-5, -5, -5, 40, 40, 40)), 32, layer, out _, out _).Should().BeTrue();
        var region = Flood(tree, size, layer);
        var least = Least(tree, region, layer);
        least.Should().Be(32);
        (float, float, float) from = (2.2f, 5.7f, 3.2f), to = (5.7f, 5.2f, 12.2f);
        var atOnce = Route(tree, size, MixedShapes.Set, region, layer, from, to, least);
        (atOnce.Result.Status, atOnce.Result.Expanded, atOnce.Result.Count).Should().Be((RouteStatus.Found, 0, 1));
        // Told nothing of the least, every leaf costs more than it, and the way is searched.
        Route(tree, size, MixedShapes.Set, region, layer, from, to, 0).Result.Expanded.Should().BeGreaterThan(0);
        // What cannot be used.
        Route(tree, size, MixedShapes.Set, region, layer[..^1], from, to, 0).Result.Status.Should().Be(RouteStatus.Refused);
        Route(tree, size, MixedShapes.Set, region, layer, from, to, 0, new byte[255]).Result.Status.Should().Be(RouteStatus.Refused);
        TreeRegions.CostsIn(tree, region, layer[..^1], new ulong[4]).Should().BeFalse();
    }

    private static byte[] BuildField(WallTree tree, byte[] region, byte[] layer, TravelSeed[] seeds, byte[]? table = null) {
        TreeFields.Measure(tree, region, seeds, out var length, out var places, out var work).Should().BeTrue();
        var bytes = new byte[length];
        TreeFields.Build(tree, region, layer, seeds, 0.02f, bytes, new float[places], new int[work], out var written, out _, out _, table ?? []).Should().BeTrue();
        return bytes[..written];
    }

    // A follower of a field over the tree's costs: the points it flies through, until it arrives or 4,000 steps.
    private static (bool Arrived, List<(float X, float Y, float Z)> Path) Follow(TreeField field, (float X, float Y, float Z) from) {
        float x = from.X, y = from.Y, z = from.Z, tx = 0, ty = 0, tz = 0;
        var path = new List<(float X, float Y, float Z)> { from };
        var place = -1;
        for (var i = 0; i < 4000; i++) {
            if (!TreeFields.TravelAt(field, x, y, z, out var travel, out var here)) { return (false, path); }
            if (travel < 0.5f) { return (true, path); }
            var left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
            if (here != place || left < 0.01f) {
                if (!TreeFields.Next(field, x, y, z, out tx, out ty, out tz, out _, out _)) { return (travel < 0.5f, path); }
                place = here;
                left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
            }
            var step = MathF.Min(0.125f, left);
            x += (tx - x) / left * step; y += (ty - y) / left * step; z += (tz - z) / left * step;
            path.Add((x, y, z));
        }
        return (false, path);
    }

    [Fact]
    public void AFieldWithATableIsTheFieldOverTheCostsPutThroughIt() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(7, -1, -1, 9.5f, 30, 30)), 0x80, layer, out _, out _).Should().BeTrue();
        TreeCosts.Fill(tree, Solid.Box(new Box(-1, -1, 9, 6, 30, 30)), 0x0C, layer, out _, out _).Should().BeTrue();
        var region = Flood(tree, size, layer);
        var table = new byte[CellCost.TableLength];
        for (var b = 0; b < CellCost.Closed; b++) { table[b] = (byte)(10 * (b >> 4) + 30 - 2 * (b & 15)); }
        table[CellCost.Closed] = CellCost.Closed;
        var mapped = layer.Select(b => table[b]).ToArray();
        TravelSeed[] seeds = [TravelSeed.FromPoint(Solid.Sphere(13, 5.5f, 11, 1f), 13, 5.5f, 11)];
        var withTable = BuildField(tree, region, layer, seeds, table);
        var throughIt = BuildField(tree, region, mapped, seeds);
        withTable.Should().Equal(throughIt);
        var without = BuildField(tree, region, [], seeds);
        without.Should().NotEqual(withTable, "the costs change the field");
        var one = new TreeField(tree, region, withTable, seeds, 0.02f, layer, table);
        var other = new TreeField(tree, region, throughIt, seeds, 0.02f, mapped, []);
        var asked = 0;
        foreach (var at in Ends(tree, region)) {
            var found = TreeFields.TravelAt(one, at.X, at.Y, at.Z, out var travel, out var place);
            TreeFields.TravelAt(other, at.X, at.Y, at.Z, out var travel2, out var place2).Should().Be(found);
            (travel, place).Should().Be((travel2, place2));
            TreeFields.Next(one, at.X, at.Y, at.Z, out var nx, out var ny, out var nz, out _, out _).Should().Be(TreeFields.Next(other, at.X, at.Y, at.Z, out var mx, out var my, out var mz, out _, out _));
            (nx, ny, nz).Should().Be((mx, my, mz));
            if (found) { asked++; }
        }
        asked.Should().BeGreaterThan(100);
    }

    [Fact]
    public void AFieldGoesRoundADearLeafAndNeverIntoAClosedOne() {
        var scene = Scene();
        var tree = scene.Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        var layer = new byte[TreeCosts.LayerLength(tree)];
        TreeCosts.Fill(tree, Solid.Box(new Box(6.5f, 3, 4, 9.5f, 8, 9)), 200, layer, out _, out _).Should().BeTrue();
        // A table that closes one more block for its kind of agent.
        TreeCosts.Fill(tree, Solid.Box(new Box(2, 2, 2, 4, 9, 4)), 90, layer, out _, out _).Should().BeTrue();
        var table = Enumerable.Range(0, CellCost.TableLength).Select(b => b == 90 ? CellCost.Closed : (byte)b).ToArray();
        var region = Flood(tree, size, layer);
        TravelSeed[] seeds = [TravelSeed.FromPoint(Solid.Sphere(12.7f, 5.7f, 6.7f, 1f), 12.7f, 5.7f, 6.7f)];
        var field = new TreeField(tree, region, BuildField(tree, region, layer, seeds, table), seeds, 0.02f, layer, table);
        var plain = new TreeField(tree, region, BuildField(tree, region, [], seeds), seeds, 0.02f);
        // Across the dear block: with costs the follower keeps out of it; with none it goes through.
        var (arrived, path) = Follow(field, (3.2f, 5.7f, 6.7f));
        var (plainArrived, plainPath) = Follow(plain, (3.2f, 5.7f, 6.7f));
        (arrived, plainArrived).Should().Be((true, true));
        int In(List<(float X, float Y, float Z)> p, int value) => p.Count(q => TreeCosts.At(scene.Tree, layer, q.X, q.Y, q.Z, out var v) && v == value);
        In(plainPath, 200).Should().BeGreaterThan(0);
        In(path, 200).Should().Be(0);
        // From all over: followers arrive, and none enters the block the table closes, nor starts in it.
        var tried = 0;
        foreach (var at in Ends(tree, region)) {
            if (!TreeFields.TravelAt(field, at.X, at.Y, at.Z, out _, out _)) { continue; }
            if (TreeCosts.At(tree, layer, at.X, at.Y, at.Z, out var startValue) && startValue == 90) { continue; }
            tried++;
            var (did, steps) = Follow(field, at);
            did.Should().BeTrue($"a follower from {at} arrives");
            In(steps, 90).Should().Be(0, $"a follower from {at} keeps out of the block closed to it");
        }
        tried.Should().BeGreaterThan(100);
        TreeFields.TravelAt(field, 3, 5.5f, 3, out _, out _).Should().BeFalse("the field does not reach a leaf its table closes");
        TreeFields.TravelAt(plain, 3, 5.5f, 3, out _, out _).Should().BeTrue();
        // A seed that reaches into the block the table closes starts no cell there, nor one whose way to its point crosses it.
        TravelSeed[] across = [TravelSeed.FromPoint(Solid.Sphere(3, 5.5f, 5.6f, 4.5f), 3, 5.5f, 5.6f)];
        TreeCosts.At(tree, layer, 3, 5.5f, 5.6f, out var seedValue).Should().BeTrue();
        seedValue.Should().NotBe(90);
        TreeCosts.At(tree, layer, 3, 5.5f, 3.4f, out var inBlock).Should().BeTrue();
        inBlock.Should().Be(90, "the seed reaches into the block");
        var half = new TreeField(tree, region, BuildField(tree, region, layer, across, table), across, 0.02f, layer, table);
        // Asked at every open cell: none in the block answers, and no point to fly to lies across it.
        int reached = 0, inBlockCells = 0, behind = 0;
        for (var cell = 0; cell < Odd.CellCount; cell++) {
            Odd.CentreOf(cell % Odd.CountX, cell / Odd.CountX % Odd.CountY, cell / Odd.LayerLength, out var cx, out var cy, out var cz);
            if (!TreeRegions.IsOpen(tree, region, cx, cy, cz)) { continue; }
            TreeCosts.At(tree, layer, cx, cy, cz, out var value);
            if (value == 90) { inBlockCells++; }
            if (!TreeFields.TravelAt(half, cx, cy, cz, out var travel, out _)) { continue; }
            reached++;
            value.Should().NotBe(90, $"no travel at ({cx}, {cy}, {cz}), in the block");
            // Behind the block from the seed's point, a tenth of the straight way or more through it, the way goes round: farther.
            var straight = MathF.Sqrt((cx - 3) * (cx - 3) + (cy - 5.5f) * (cy - 5.5f) + (cz - 5.6f) * (cz - 5.6f));
            var inside = 0;
            for (var k = 1; k < 100; k++) { if (TreeCosts.At(tree, layer, cx + (3 - cx) * k / 100f, cy + (5.5f - cy) * k / 100f, cz + (5.6f - cz) * k / 100f, out var by) && by == 90) { inside++; } }
            if (inside >= 10) { behind++; travel.Should().BeGreaterThan(straight + 0.05f, $"at ({cx}, {cy}, {cz}), behind the block"); }
            if (!TreeFields.Next(half, cx, cy, cz, out var nx, out var ny, out var nz, out _, out _)) { continue; }
            for (var k = 0; k <= 100; k++) {
                var f = k / 100f;
                TreeCosts.At(tree, layer, cx + (nx - cx) * f, cy + (ny - cy) * f, cz + (nz - cz) * f, out var on);
                on.Should().NotBe(90, $"from ({cx}, {cy}, {cz}) the point to fly to is ({nx}, {ny}, {nz})");
            }
        }
        reached.Should().BeGreaterThan(1000, $"in the block {inBlockCells}");
        (inBlockCells > 20, behind > 20).Should().Be((true, true));
        // What cannot be used, and a field asked without the costs it was built with.
        TreeFields.Measure(tree, region, seeds, out var length, out var places, out var work).Should().BeTrue();
        TreeFields.Build(tree, region, layer[..^1], seeds, 0.02f, new byte[length], new float[places], new int[work], out _, out _, out _).Should().BeFalse();
        TreeFields.Build(tree, region, layer, seeds, 0.02f, new byte[length], new float[places], new int[work], out _, out _, out _, new byte[255]).Should().BeFalse();
        TreeFields.TravelAt(new TreeField(tree, region, field.Bytes.ToArray(), seeds, 0.02f, layer[..^1], table), 3.2f, 5.7f, 6.7f, out _, out _).Should().BeFalse();
    }

}
