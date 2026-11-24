using FluentAssertions;
using P = (float X, float Y, float Z);

namespace Yewnyx.Spatial.Test;

public class TreeRoutesTest {
    private const float Radius = 0.51f;

    private sealed record Planned(RouteResult Result, P[] Points) {
        public RouteStatus Status => Result.Status;
        public RouteEnds Ends => Result.Ends;
        // One straight leg from start to goal, with nothing searched.
        public bool Direct => Result.Status == RouteStatus.Found && Result.Count == 1 && Result.Expanded == 0;
    }

    // One scene as a tree and as a dense map, the regions of one radius both ways, and the two
    // searches' arrays, so that a route over the tree can be held against the dense search's.
    private sealed class Level {
        public readonly ShapeScene Shapes;
        public readonly TreeScene Scene;
        public readonly GridShape Shape;
        public readonly SizeClass Size;
        public readonly byte[] Region, DenseRegion;
        private readonly float[] _denseSums;
        private readonly int[] _denseLinks, _denseHeap;

        public Level(GridShape shape, ShapeScene shapes, params Solid[] seeds) : this(shape, shapes, Radius, seeds) { }

        public Level(GridShape shape, ShapeScene shapes, float radius, params Solid[] seeds) {
            Shape = shape;
            Shapes = shapes;
            Scene = new TreeScene(shape, shapes.Set);
            var tree = Scene.Tree;
            SizeClass.TryMake(radius, tree, out Size).Should().BeTrue();
            if (seeds.Length == 0) { seeds = [Solid.Box(new Box(-1000, -1000, -1000, 1000, 1000, 1000))]; }
            var region = new byte[TreeRegions.MostLength(tree)];
            TreeRegions.FromSeeds(tree, Size, seeds, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out var length, out _, out _).Should().BeTrue();
            Region = region[..length];
            DenseRegion = new byte[shape.CellCount];
            Regions.FromSeeds(Scene.Map, Size, default, seeds, DenseRegion, new int[Regions.QueueLength(shape)], out _, out _).Should().BeTrue();
            _denseSums = new float[Routes.SumsLength(shape)];
            _denseLinks = new int[Routes.LinksLength(shape)];
            _denseHeap = new int[Routes.HeapLength(shape)];
        }

        public Planned Find(P from, P to, bool shapes = true, bool straighten = true, int places = 1 << 13, int room = 400, byte[]? region = null, int filled = 0, float leeway = 0.25f) {
            var points = new float[3 * room];
            var sums = new float[TreeRoutes.SumsLength(places)];
            var links = new int[TreeRoutes.LinksLength(places)];
            // The arrays need not be clear: a search is given them full of something else.
            Array.Fill(sums, filled);
            Array.Fill(links, filled);
            var result = TreeRoutes.Find(Scene.Tree, Size, shapes ? Shapes.Set : default, region ?? Region, from.X, from.Y, from.Z, to.X, to.Y, to.Z, straighten, sums, links, points, leeway);
            return new Planned(result, Listed(result, points, room));
        }

        public Planned Dense(P from, P to, bool shapes = true, bool straighten = true, int room = 400) {
            var points = new float[3 * room];
            var result = Routes.Find(Scene.Map, Size, shapes ? Shapes.Set : default, DenseRegion, default, from.X, from.Y, from.Z, to.X, to.Y, to.Z, straighten,
                _denseSums, _denseLinks, _denseHeap, points);
            return new Planned(result, Listed(result, points, room));
        }

        private static P[] Listed(RouteResult result, float[] points, int room) {
            var held = Math.Min(result.Count, room);
            var list = new P[held];
            for (var i = 0; i < held; i++) { list[i] = (points[3 * i], points[3 * i + 1], points[3 * i + 2]); }
            return list;
        }

        public P Centre(int x, int y, int z) {
            Shape.CentreOf(x, y, z, out var cx, out var cy, out var cz);
            return (cx, cy, cz);
        }

        public bool IsOpenCentre(P point, out int x, out int y, out int z) {
            x = (int)Math.Floor((point.X - Shape.OriginX) / Shape.CellSize);
            y = (int)Math.Floor((point.Y - Shape.OriginY) / Shape.CellSize);
            z = (int)Math.Floor((point.Z - Shape.OriginZ) / Shape.CellSize);
            var index = Shape.IndexOf(x, y, z);
            return index >= 0 && DenseRegion[index] != 0 && Centre(x, y, z) == point;
        }

        // A step: from the centre of one open cell to the centre of one that touches it.
        public bool IsStep(P a, P b) =>
            IsOpenCentre(a, out var ax, out var ay, out var az) && IsOpenCentre(b, out var bx, out var by, out var bz) &&
            Math.Abs(ax - bx) <= 1 && Math.Abs(ay - by) <= 1 && Math.Abs(az - bz) <= 1;

        // The least distance to any shape along a leg, by the shapes themselves in double, every 2 mm.
        public double Room(P a, P b) {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 0.002));
            var least = double.PositiveInfinity;
            for (var k = 0; k <= steps; k++) {
                var t = (double)k / steps;
                least = Math.Min(least, Shapes.Exact(a.X + dx * t, a.Y + dy * t, a.Z + dz * t));
            }
            return least;
        }

        // What a route promises, held against the shapes themselves: a step keeps the class's
        // guarantee and any other leg the full radius. The first leg is let off when the route
        // says it is unproven.
        public void ShouldBeSound(Planned planned, P from, P to) {
            planned.Status.Should().Be(RouteStatus.Found);
            planned.Points.Length.Should().Be(planned.Result.Count).And.BeGreaterThan(0);
            var at = from;
            for (var i = 0; i < planned.Points.Length; i++) {
                var point = planned.Points[i];
                if (i > 0 || !planned.Ends.HasFlag(RouteEnds.FirstLegUnproven)) {
                    Room(at, point).Should().BeGreaterThanOrEqualTo((IsStep(at, point) ? Size.Guarantee : Size.Radius) - 1e-4, $"on leg {i}, from {at} to {point}");
                }
                at = point;
            }
            if (planned.Ends.HasFlag(RouteEnds.FirstLegUnproven)) { IsOpenCentre(planned.Points[0], out _, out _, out _).Should().BeTrue(); }
            if (planned.Ends.HasFlag(RouteEnds.StopsShortOfGoal)) {
                IsOpenCentre(at, out _, out _, out _).Should().BeTrue();
            } else {
                at.Should().Be(to);
            }
        }

        public static double Length(P from, P[] points) {
            var length = 0.0;
            var at = from;
            foreach (var point in points) {
                double dx = point.X - at.X, dy = point.Y - at.Y, dz = point.Z - at.Z;
                length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                at = point;
            }
            return length;
        }

        // Centres of open cells of the largest region, a stride apart in the order of the cells.
        public List<P> OpenCentres(int stride) {
            var list = new List<P>();
            for (var i = stride / 2; i < Shape.CellCount; i += stride) {
                if (DenseRegion[i] == 1) { list.Add(Centre(i % Shape.CountX, i / Shape.CountX % Shape.CountY, i / Shape.LayerLength)); }
            }
            return list;
        }
    }

    // 50 by 20 by 50 m of open air over a floor, with a wall across it at x = 25 that leaves a
    // gap of 10 m at the far end of z.
    private static Level Walled() => new(new GridShape(0, 0, 0, 0.5f, 100, 40, 100), new ShapeScene([
        Solid.Box(new Box(-1, -1, -1, 51, 0.2f, 51)),
        Solid.Box(new Box(24.8f, -1, -1, 25.2f, 21, 40)),
    ]));

    private static readonly Solid[] Mixed = [
        Solid.Box(new Box(-1, -1, -1, 20, 0.3f, 20)),
        ShapeScene.TurnedBox(6, 3, 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(11, 4.5f, 8, 1.6f),
        Solid.Capsule(3, 1, 9, 14, 7, 2, 0.35f),
        Solid.Box(new Box(15.5f, 0, 0, 30, 30, 30)),
    ];

    // Shapes of every kind in a grid of 37 by 22 by 29 cells, no side a multiple of four.
    private static Level Cluttered() => new(new GridShape(-0.3f, -0.7f, 0.2f, 0.5f, 37, 22, 29),
        new ShapeScene(Mixed, [9, 0, 3, 9, 8, 3, 9, 8, 11, 9, 0, 11, 0, 7.5f, 1, 16, 6.5f, 2, 3, 2.5f, 11.5f], [0, 1, 2, 0, 2, 3, 4, 5, 6]));

    [Fact]
    public void ThroughOpenAirARouteIsOneLegAndNothingIsSearched() {
        var level = Walled();
        P from = (3.1f, 4.2f, 2.7f), to = (21.3f, 16.4f, 46.9f);
        var planned = level.Find(from, to);
        planned.Direct.Should().BeTrue();
        planned.Points.Should().Equal(to);
        level.ShouldBeSound(planned, from, to);
        // With no shapes to ask, the tree alone proves it.
        level.Find(from, to, shapes: false).Direct.Should().BeTrue();
    }

    [Fact]
    public void RoundAWallTheWayCutsAcrossOpenCubes() {
        var level = Walled();
        P from = (10.25f, 8.25f, 10.25f), to = (40.25f, 8.25f, 10.25f);
        var planned = level.Find(from, to);
        level.ShouldBeSound(planned, from, to);
        planned.Direct.Should().BeFalse("the wall is in the way");
        var dense = level.Dense(from, to);
        dense.Status.Should().Be(RouteStatus.Found);
        // Round the wall's end: about 33.5 m there and 33.5 m back.
        Level.Length(from, planned.Points).Should().BeInRange(Level.Length(from, dense.Points) - 0.01, Level.Length(from, dense.Points) * 1.03);
        planned.Result.Expanded.Should().BeInRange(100, 3000, "a place is an open cube of 8 m or of 2 m, or a brick along the wall or the floor");
        dense.Result.Expanded.Should().BeGreaterThan(50 * planned.Result.Expanded);

        // With no leeway the search looks at every place a cheaper way could pass through. Here, where the wall
        // makes the way twice the straight line, that is little more, and the route is no shorter for it.
        var patient = level.Find(from, to, leeway: 0f);
        level.ShouldBeSound(patient, from, to);
        patient.Result.Expanded.Should().BeGreaterThanOrEqualTo(planned.Result.Expanded);
        Level.Length(from, planned.Points).Should().BeLessThan(Level.Length(from, patient.Points) * 1.01);
        planned.Points.Length.Should().BeLessThanOrEqualTo(4);

        // Left as the way was found: centres of cells, each piece a step or a cut across one open cube.
        var cells = level.Find(from, to, straighten: false);
        level.ShouldBeSound(cells, from, to);
        cells.Points[0].Should().Be(from);
        cells.Points.Length.Should().BeGreaterThan(planned.Points.Length);
        cells.Result.Cost.Should().BeApproximately((float)Level.Length(from, cells.Points), 1e-3f, "the cost of a way with no costs is its length");
        var cuts = 0;
        for (var i = 1; i < cells.Points.Length; i++) {
            level.IsOpenCentre(cells.Points[i], out _, out _, out _).Should().BeTrue();
            if (!level.IsStep(cells.Points[i - 1], cells.Points[i])) { cuts++; }
        }
        cuts.Should().BeGreaterThan(3);
    }

    [Fact]
    public void TheTreeFindsTheRoutesTheDenseSearchFinds() {
        var level = Cluttered();
        var centres = level.OpenCentres(197);
        centres.Count.Should().BeGreaterThan(30);
        int routes = 0, searched = 0, longer = 0;
        double longest = 0, total = 0, denseTotal = 0;
        for (var i = 0; i < centres.Count; i++) {
            for (var j = i % 4; j < centres.Count; j += 4) {
                if (i == j) { continue; }
                P from = centres[i], to = centres[j];
                var planned = level.Find(from, to);
                var dense = level.Dense(from, to);
                dense.Status.Should().Be(RouteStatus.Found);
                level.ShouldBeSound(planned, from, to);
                // A straight leg is clear over the tree where it is over the dense map: the same test at the same points.
                planned.Direct.Should().Be(dense.Direct, $"from {from} to {to}");
                planned.Ends.Should().Be(dense.Ends);
                double length = Level.Length(from, planned.Points), denseLength = Level.Length(from, dense.Points);
                longest = Math.Max(longest, length / denseLength);
                if (length > 1.1 * denseLength) { longer++; }
                total += length; denseTotal += denseLength;
                routes++;
                if (!planned.Direct) { searched++; }
            }
        }
        routes.Should().BeGreaterThan(250);
        searched.Should().BeGreaterThan(80);
        // Among shapes as close as these the tree's places are at their coarsest against the way's length.
        (total / denseTotal).Should().BeInRange(0.99, 1.02, "on the whole the routes are as long as the dense search's");
        longer.Should().BeLessThan(routes / 40, "few are a tenth longer");
        longest.Should().BeLessThan(1.3, "and none is much longer than that");
    }

    [Fact]
    public void WhereThereIsNoOpenAirEveryPlaceIsOfABrick() {
        // A floor and a ceiling 1.9 m apart, with posts and a wall between: no cell is a metre from everything, so
        // no cell holds the cap and the tree has no open cube. The way goes from brick to brick, and through each
        // by the dense search's steps.
        var level = new Level(new GridShape(0, 0, 0, 0.5f, 40, 4, 40), new ShapeScene([
            Solid.Box(new Box(-1, -1, -1, 21, 0.05f, 21)),
            Solid.Box(new Box(-1, 1.95f, -1, 21, 3, 21)),
            Solid.Capsule(6, -1, 5, 6, 3, 5, 0.3f),
            Solid.Capsule(7.4f, -1, 12, 7.4f, 3, 12, 0.45f),
            Solid.Box(new Box(10, -1, 3, 10.4f, 3, 15)),
            Solid.Capsule(14, -1, 9, 15, 3, 10, 0.2f),
        ]));
        level.Scene.Bricks.Should().NotContain(TreeScene.Cap);
        var centres = level.OpenCentres(131);
        centres.Count.Should().BeGreaterThan(20);
        var searched = 0;
        double total = 0, denseTotal = 0, longest = 0;
        for (var i = 0; i < centres.Count; i++) {
            for (var j = i % 3; j < centres.Count; j += 3) {
                if (i == j) { continue; }
                P from = centres[i], to = centres[j];
                var cells = level.Find(from, to, straighten: false);
                var dense = level.Dense(from, to, straighten: false);
                level.ShouldBeSound(cells, from, to);
                // Every piece of the way is a step, and the way's cost is its length.
                for (var k = 1; k < cells.Points.Length; k++) { level.IsStep(cells.Points[k - 1], cells.Points[k]).Should().BeTrue(); }
                cells.Result.Cost.Should().BeApproximately((float)Level.Length(from, cells.Points), 2e-3f);
                cells.Result.Cost.Should().BeGreaterThanOrEqualTo(dense.Result.Cost - 2e-3f, "the dense search's way is the cheapest by steps");
                total += cells.Result.Cost; denseTotal += dense.Result.Cost;
                longest = Math.Max(longest, cells.Result.Cost / Math.Max(dense.Result.Cost, 1f));
                searched++;
            }
        }
        searched.Should().BeGreaterThan(100);
        (total / denseTotal).Should().BeLessThan(1.08, "left as cells the ways are a little longer on the whole than the cheapest");
        longest.Should().BeLessThan(1.5);
    }

    [Fact]
    public void EndsInTwoRegionsAreUnreachableAndNothingIsSearched() {
        // A sheet across x at 9.1 m cuts the grid in two, and both halves are seeded: two regions,
        // whose open cells lie together in the bricks along the sheet.
        var sheet = new ShapeScene([], [9.1f, -5, -5, 9.1f, 30, -5, 9.1f, 30, 30, 9.1f, -5, 30], [0, 1, 2, 0, 2, 3]);
        var grid = new GridShape(0, 0, 0, 0.5f, 40, 10, 20);
        foreach (var seeds in new[] { new[] { Solid.Sphere(3, 2, 5, 0.5f), Solid.Sphere(15, 2, 5, 0.5f) }, [Solid.Sphere(15, 2, 5, 0.5f), Solid.Sphere(3, 2, 5, 0.5f)] }) {
            var level = new Level(grid, sheet, seeds);
            // The last open cell before the sheet and the first beyond it: in one brick, in two regions.
            P near = (8.25f, 2.25f, 5.25f), far = (9.75f, 2.25f, 5.25f);
            var across = level.Find(near, far);
            (across.Status, across.Result.Expanded, across.Result.Count).Should().Be((RouteStatus.Unreachable, 0, 0));
            level.Dense(near, far).Status.Should().Be(RouteStatus.Unreachable);
            level.Find((3.25f, 3.25f, 8.25f), (16.75f, 1.25f, 2.25f)).Status.Should().Be(RouteStatus.Unreachable);
            // Along the sheet on either side there is a way, through those same bricks.
            foreach (var side in new[] { near, far }) {
                P to = (side.X, 3.75f, 8.75f);
                var along = level.Find(side, to);
                level.ShouldBeSound(along, side, to);
            }
        }
        // Seeded from one side only, the far side has no region: an end there finds no open centre it can see.
        var half = new Level(grid, sheet, Solid.Sphere(3, 2, 5, 0.5f));
        half.Find((3.25f, 2.25f, 5.25f), (15.25f, 2.25f, 5.25f)).Status.Should().Be(RouteStatus.Unreachable);
        half.Dense((3.25f, 2.25f, 5.25f), (15.25f, 2.25f, 5.25f)).Status.Should().Be(RouteStatus.Unreachable);
    }

    [Fact]
    public void AnEndThatIsNotInAnOpenCellTakesTheCentreTheDenseSearchTakes() {
        var level = Cluttered();
        // Ends on the floor, against the wall, beside the sphere and the sheet, and outside the grid: what each
        // comes to is what the dense search says, point for point at the two ends.
        P[] ends = [(4.1f, 0.35f, 12.2f), (15.4f, 5.1f, 6.3f), (11, 4.5f, 9.75f), (8.9f, 3, 6), (9.2f, 3, 6), (2, 6, 13), (-0.5f, 5, 5), (13.3f, 0.5f, 12.6f), (11, 4.5f, 8)];
        var flagged = 0;
        foreach (var from in ends) {
            foreach (var to in ends) {
                if (from == to) { continue; }
                var planned = level.Find(from, to);
                var dense = level.Dense(from, to);
                (planned.Status, planned.Ends).Should().Be((dense.Status, dense.Ends), $"from {from} to {to}");
                if (planned.Status != RouteStatus.Found) { continue; }
                level.ShouldBeSound(planned, from, to);
                planned.Points[^1].Should().Be(dense.Points[^1]);
                if (planned.Ends.HasFlag(RouteEnds.FirstLegUnproven)) { planned.Points[0].Should().Be(dense.Points[0]); }
                if (planned.Ends != RouteEnds.None) { flagged++; }
            }
        }
        flagged.Should().BeGreaterThan(5);
        level.Find(ends[0], (11, 4.5f, 8)).Status.Should().Be(RouteStatus.Unreachable, "the middle of the sphere sees no open centre");
    }

    [Fact]
    public void ASearchThatFillsItsArraysSaysSoAndNotThatThereIsNoWay() {
        var level = Walled();
        P from = (10.25f, 8.25f, 10.25f), to = (40.25f, 8.25f, 10.25f);
        var whole = level.Find(from, to);
        whole.Status.Should().Be(RouteStatus.Found);
        // Room for 24 places, taken up or waiting: the way round the wall needs more.
        var full = level.Find(from, to, places: 24);
        (full.Status, full.Result.Count, full.Points.Length).Should().Be((RouteStatus.SearchFull, 0, 0));
        full.Result.Expanded.Should().BeInRange(1, 24);
        // Just enough room for what the whole search held finds the same route; the arrays hold a third more slots than places.
        (TreeRoutes.LinksLength(24), TreeRoutes.SumsLength(24)).Should().Be((5 * 32, 2 * 32));
        (TreeRoutes.LinksLength(25), TreeRoutes.SumsLength(25)).Should().Be((5 * 64, 2 * 64));
        (TreeRoutes.LinksLength(1), TreeRoutes.LinksLength(12), TreeRoutes.LinksLength(13)).Should().Be((5 * 16, 5 * 16, 5 * 32));
        (TreeRoutes.LinksLength(0), TreeRoutes.SumsLength(-3), TreeRoutes.LinksLength(int.MaxValue)).Should().Be((0, 0, 0));
        // With little room, 512 slots or 16, a search either finds what a search with room finds or says
        // it is full: places that fall together in so small a table are looked for round its end.
        var cluttered = Cluttered();
        var centres = cluttered.OpenCentres(397);
        int small = 0, filled = 0;
        for (var i = 0; i + 1 < centres.Count; i++) {
            for (var j = i + 1; j < centres.Count; j += 3) {
                var tight = cluttered.Find(centres[i], centres[j], places: (i + j) % 2 == 0 ? 384 : 12);
                if (tight.Status == RouteStatus.SearchFull) { filled++; continue; }
                var roomy = cluttered.Find(centres[i], centres[j]);
                tight.Result.Should().Be(roomy.Result);
                tight.Points.Should().Equal(roomy.Points);
                if (tight.Result.Expanded > 0) { small++; }
            }
        }
        small.Should().BeGreaterThan(10);
        filled.Should().BeGreaterThan(10);
        // A straight leg needs no room at all beyond the least.
        level.Find((3, 4, 3), (20, 16, 45), places: 1).Direct.Should().BeTrue();
        // Arrays longer than a power of two of slots are used up to the power of two; with no leeway given it is 1.
        whole = level.Find(from, to, leeway: 1f);
        var points = new float[300];
        var odd = TreeRoutes.Find(level.Scene.Tree, level.Size, level.Shapes.Set, level.Region, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true,
            new float[2 * 131072 + 7], new int[5 * 262143], points);
        (odd.Status, odd.Count, odd.Expanded).Should().Be((whole.Status, whole.Result.Count, whole.Result.Expanded));
    }

    [Fact]
    public void WhatTheArraysHeldBeforeChangesNothing() {
        var level = Cluttered();
        var centres = level.OpenCentres(397);
        for (var i = 0; i + 1 < centres.Count; i++) {
            foreach (var straighten in new[] { true, false }) {
                var clean = level.Find(centres[i], centres[^(i + 1)], straighten: straighten);
                foreach (var filled in new[] { -1, 1, 0x7F7F7F7F }) {
                    var again = level.Find(centres[i], centres[^(i + 1)], straighten: straighten, filled: filled);
                    again.Result.Should().Be(clean.Result);
                    again.Points.Should().Equal(clean.Points);
                }
            }
        }
    }

    [Fact]
    public void ABufferTooShortSaysHowManyPointsThereAre() {
        var level = Walled();
        P from = (10.25f, 8.25f, 10.25f), to = (40.25f, 8.25f, 10.25f);
        var whole = level.Find(from, to);
        whole.Points.Length.Should().BeGreaterThan(1);
        var cut = level.Find(from, to, room: whole.Points.Length - 1);
        (cut.Status, cut.Result.Count).Should().Be((RouteStatus.BufferTooShort, whole.Points.Length));
        cut.Points.Should().Equal(whole.Points[..^1]);
        level.Find(from, to, room: 0).Result.Count.Should().Be(whole.Points.Length);
        level.Find(from, to, room: whole.Points.Length).Status.Should().Be(RouteStatus.Found);
    }

    [Fact]
    public void WhatCannotBeUsedIsRefused() {
        var level = Walled();
        var tree = level.Scene.Tree;
        var set = level.Shapes.Set;
        P from = (10.25f, 8.25f, 10.25f), to = (40.25f, 8.25f, 10.25f);
        float[] sums = new float[TreeRoutes.SumsLength(1 << 16)], points = new float[300];
        var links = new int[TreeRoutes.LinksLength(1 << 16)];
        RouteResult Find(in WallTree which, SizeClass size, byte[] region, P a, P b, float[] s, int[] l) =>
            TreeRoutes.Find(which, size, level.Shapes.Set, region, a.X, a.Y, a.Z, b.X, b.Y, b.Z, true, s, l, points);

        Find(tree, level.Size, level.Region, from, to, sums, links).Status.Should().Be(RouteStatus.Found);
        Find(default, level.Size, level.Region, from, to, sums, links).Should().Be(default(RouteResult), "no tree");
        Find(tree, default, level.Region, from, to, sums, links).Should().Be(default(RouteResult), "no class");
        SizeClass.TryMake(Radius, new WallMap(level.Shape, 1f / 64f, TreeScene.Cap, level.Scene.Dense), out var other).Should().BeTrue();
        Find(tree, other, level.Region, from, to, sums, links).Status.Should().Be(RouteStatus.Refused, "a class made for another unit");
        Find(tree, level.Size, [], from, to, sums, links).Status.Should().Be(RouteStatus.Refused, "no regions");
        Find(tree, level.Size, level.Region[..^1], from, to, sums, links).Status.Should().Be(RouteStatus.Refused, "regions cut short");
        Find(tree, level.Size, new byte[level.Region.Length], from, to, sums, links).Status.Should().Be(RouteStatus.Refused, "the bytes of a flood that was refused");
        Find(tree, level.Size, level.Region, from, to, new float[2 * 16 - 1], links).Status.Should().Be(RouteStatus.Refused, "fewer than 16 slots");
        Find(tree, level.Size, level.Region, from, to, sums, new int[5 * 16 - 1]).Status.Should().Be(RouteStatus.Refused);
        Find(tree, level.Size, level.Region, from, to, new float[2 * 16], new int[5 * 16]).Status.Should().Be(RouteStatus.SearchFull, "16 slots are enough to be let in");
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }) {
            Find(tree, level.Size, level.Region, (bad, 8, 10), to, sums, links).Status.Should().Be(RouteStatus.Refused);
            Find(tree, level.Size, level.Region, from, (40, 8, bad), sums, links).Status.Should().Be(RouteStatus.Refused);
            level.Find(from, to, leeway: bad).Status.Should().Be(RouteStatus.Refused);
        }
        level.Find(from, to, leeway: -0.01f).Status.Should().Be(RouteStatus.Refused);
        // A point far outside the grid is not refused: it has no open centre within reach.
        Find(tree, level.Size, level.Region, (500, 8, 10), to, sums, links).Status.Should().Be(RouteStatus.Unreachable);
        _ = set;
    }

    // The dense search's room: 8 m each way, with a floor.
    private static readonly GridShape Room = new(0, 0, 0, 0.5f, 16, 16, 16);
    private static readonly Solid Floor = Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f));

    [Fact]
    public void AStraightLegIsHeldToWhatTheDenseSearchHoldsItTo() {
        // A leg of 5 m and beside it a ball of 2 cm, level with the middle between two looks at the tree. Half a
        // millimetre inside the radius the leg is not taken, and 3 mm clear of it it is.
        P from = (1.25f, 4.25f, 2.25f), to = (6.25f, 4.25f, 2.25f);
        Level With(float gap) => new(Room, new ShapeScene([Floor, Solid.Sphere(3.8125f, 4.25f + gap + 0.02f, 2.25f, 0.02f)]));
        var inside = With(0.5095f);
        inside.Find(from, to).Direct.Should().BeFalse();
        inside.ShouldBeSound(inside.Find(from, to), from, to);
        With(0.513f).Find(from, to).Direct.Should().BeTrue();

        // Down a corridor 1.2 m wide the shapes prove what the tree cannot.
        var corridor = new Level(Room, new ShapeScene([Floor, Solid.Box(new Box(3, -1, 0, 5, 3.67f, 9)), Solid.Box(new Box(3, 4.87f, 0, 5, 9, 9))]));
        P near = (1.75f, 4.27f, 1.25f), far = (6.25f, 4.27f, 1.25f);
        corridor.Find(near, far).Direct.Should().BeTrue("a ball of 0.51 m clears both walls by 0.09 m");
        var without = corridor.Find(near, far, shapes: false);
        without.Direct.Should().BeFalse("the tree proves no more than 0.54 m at the centres, put down to a fifteenth of the cap, and less between them");
        without.Result.Count.Should().BeGreaterThan(4, "through the corridor it goes cell by cell");
        corridor.ShouldBeSound(without, near, far);

        // A post 2 cm thick, and a sheet with no thickness, across the line between the ends.
        var post = new Level(Room, new ShapeScene([Floor, Solid.Capsule(4.06f, 4, 0, 4.06f, 4, 9, 0.01f)]));
        var sheet = new Level(Room, new ShapeScene([Floor], [4.06f, 2, 0.2f, 4.06f, 6, 0.2f, 4.06f, 2, 5, 4.06f, 6, 5], [0, 1, 2, 1, 3, 2]));
        P left = (1.25f, 4, 1.25f), right = (6.75f, 4, 1.25f);
        foreach (var level in new[] { post, sheet }) {
            foreach (var shapes in new[] { true, false }) {
                var route = level.Find(left, right, shapes);
                route.Direct.Should().BeFalse();
                level.ShouldBeSound(route, left, right);
            }
        }
        // Past the post to one side: 6 mm clear of the radius the leg is taken, and 4 mm inside it it is not.
        post.Find((1.25f, 4.526f, 1.25f), (6.75f, 4.526f, 1.25f)).Direct.Should().BeTrue();
        post.Find((1.25f, 4.516f, 1.25f), (6.75f, 4.516f, 1.25f)).Direct.Should().BeFalse();
        post.Find((1.25f, 3.474f, 2.25f), (6.75f, 3.474f, 2.75f)).Direct.Should().BeTrue();
        post.Find((1.25f, 3.484f, 2.25f), (6.75f, 3.484f, 2.75f)).Direct.Should().BeFalse();

        // A speck in open air, on the line of a leg, 0.8 m beyond the face of an open cube of 2 m: as near a
        // cube as anything can be. The points inside the cube are passed over, and those beyond it are not.
        var speck = new Level(new GridShape(0, 0, 0, 0.5f, 32, 12, 12), new ShapeScene([Solid.Sphere(10.8f, 3, 3, 0.02f)]));
        WallTrees.HasRoom(speck.Scene.Tree, 9.25f, 3, 3, 0.9f).Should().BeTrue("the cube that ends at 10 m is open");
        speck.Find((2.25f, 3, 3), (13.75f, 3, 3)).Direct.Should().BeFalse();
        speck.Find((2.25f, 3, 3), (13.75f, 3, 3), shapes: false).Direct.Should().BeFalse();
        speck.Find((13.75f, 3, 3), (2.25f, 3, 3)).Direct.Should().BeFalse("and from the other side");
        speck.Find((2.25f, 3.6f, 3), (13.75f, 3.6f, 3)).Direct.Should().BeTrue("0.58 m to one side it is clear");
        speck.Dense((2.25f, 3.6f, 3), (13.75f, 3.6f, 3)).Direct.Should().BeTrue();

        // A leg whose first point the tree cannot prove and whose second it can: the first is measured all the
        // same. A body of 0.6 m that starts 0.55 m from a wall, 5 cm too near, and heads straight away from it.
        var wall = new Level(new GridShape(0, 0, 0, 0.5f, 40, 16, 16), new ShapeScene([Solid.Box(new Box(0, 0, 0, 3, 8, 8))]), 0.6f);
        P beside = (3.55f, 4.25f, 4.25f), away = (6.55f, 4.25f, 4.25f);
        wall.Room(beside, away).Should().BeApproximately(0.55, 1e-3);
        wall.Find(beside, away).Direct.Should().BeFalse();
        wall.Dense(beside, away).Direct.Should().BeFalse();
        wall.ShouldBeSound(wall.Find(beside, away), beside, away);
        wall.Find((3.65f, 4.25f, 4.25f), away).Direct.Should().BeTrue("5 cm farther out the whole leg is clear");

        // The largest body the cap allows, 0.93 m, and a leg that leaves an open cube of 8 m to end 0.64 m from
        // a wall. Beyond the cube's cells every point is asked: for a body this large, 0.4 m beyond them can be
        // too near a shape.
        var large = new Level(new GridShape(0, 0, 0, 0.5f, 40, 24, 24), new ShapeScene([Solid.Box(new Box(16.76f, 0, 0, 18, 12, 12))]), 0.93f);
        WallTrees.HasRoom(large.Scene.Tree, 12, 4.25f, 4.25f, 0.9f).Should().BeTrue();
        WallTrees.HasRoom(large.Scene.Tree, 15.7f, 4.25f, 4.25f, 0.9f).Should().BeTrue("the cells from 8 to 16 m along x are one open cube");
        P inCube = (12, 4.25f, 4.25f), nearWall = (16.12f, 4.25f, 4.25f);
        large.Room(inCube, nearWall).Should().BeApproximately(0.64, 1e-3);
        large.Find(inCube, nearWall).Direct.Should().BeFalse();
        large.Dense(inCube, nearWall).Direct.Should().BeFalse();
        large.Find(inCube, (15.8f, 4.25f, 4.25f)).Direct.Should().BeTrue("0.96 m from the wall it is clear");
    }

    [Fact]
    public void AmongPostsInOpenAirALegIsClearWhereItIsOverTheDenseMap() {
        // 32 by 16 by 32 m over a floor, with posts, a slab in the air and a ball: open cubes of 8 m and of 2 m
        // between them. Legs from end to end of it, with shapes to ask and without.
        var level = new Level(new GridShape(0, 0, 0, 0.5f, 64, 32, 64), new ShapeScene([
            Solid.Box(new Box(-1, -1, -1, 33, 0.2f, 33)),
            Solid.Capsule(8.3f, -1, 7.6f, 8.3f, 17, 7.6f, 0.3f), Solid.Capsule(16.1f, -1, 16.4f, 16.1f, 17, 16.4f, 0.25f),
            Solid.Capsule(24.7f, -1, 9.2f, 24.7f, 17, 9.2f, 0.3f), Solid.Capsule(9.4f, -1, 24.9f, 22.6f, 17, 24.9f, 0.2f),
            Solid.Box(new Box(14, 6, 10, 18, 6.4f, 13)), Solid.Sphere(23.5f, 9.5f, 23.5f, 1.2f),
        ]));
        var ends = new List<P>();
        for (var i = 0; i < 4; i++) {
            for (var j = 0; j < 3; j++) {
                for (var k = 0; k < 4; k++) { ends.Add((3.3f + i * 8.47f, 2.2f + j * 5.3f, 2.6f + k * 8.93f)); }
            }
        }
        int legs = 0, clear = 0, proven = 0;
        long patient = 0, hurried = 0;
        for (var i = 0; i < ends.Count; i++) {
            for (var j = (i * 7 + 3) % 5; j < ends.Count; j += 5) {
                if (i == j) { continue; }
                var planned = level.Find(ends[i], ends[j]);
                planned.Direct.Should().Be(level.Dense(ends[i], ends[j]).Direct, $"from {ends[i]} to {ends[j]}");
                var blind = level.Find(ends[i], ends[j], shapes: false);
                blind.Direct.Should().Be(level.Dense(ends[i], ends[j], shapes: false).Direct, $"from {ends[i]} to {ends[j]} with no shapes");
                legs++;
                if (planned.Direct) { clear++; }
                if (blind.Direct) { proven++; }
                hurried += planned.Result.Expanded;
                patient += level.Find(ends[i], ends[j], leeway: 0f).Result.Expanded;
            }
        }
        legs.Should().BeGreaterThan(100);
        clear.Should().BeInRange(legs / 4, legs - 20);
        proven.Should().BeInRange(legs / 4, clear);
        // The routes that are not one leg: with a leeway the search takes up far fewer places.
        hurried.Should().BeGreaterThan(0);
        patient.Should().BeGreaterThan(2 * hurried);
    }

    [Theory]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    public void RoundTheEndOfASheetTheShortWayIsFoundAndNotTheLongWayThroughOneBrick(int axis, bool over) {
        // Open air, and one sheet square to an axis, through the middle of a row of bricks. It ends at a brick's
        // face along the second axis, so the short way from one side to the other is round that end, through
        // the next brick. Start and goal are 1.5 m apart, in one brick, on either side of the sheet. Where the
        // sheet's top is a metre up that brick, the two sides are joined inside the brick too, over the top: a
        // way three times as long. Where the sheet goes on up, they are two places of the one brick.
        P At(float a, float u, float v) => axis == 0 ? (a, u, v) : axis == 1 ? (v, a, u) : (u, v, a);
        var top = over ? 5f : 14f;
        var corners = new List<float>();
        foreach (var corner in new[] { At(5, 4, -2), At(5, 14, -2), At(5, 14, top), At(5, 4, top) }) { corners.AddRange([corner.X, corner.Y, corner.Z]); }
        var level = new Level(new GridShape(0, 0, 0, 0.5f, 24, 24, 24), new ShapeScene([], corners.ToArray(), [0, 1, 2, 0, 2, 3]));
        P from = At(4.25f, 4.25f, 4.25f), to = At(5.75f, 4.25f, 4.25f);
        var dense = level.Dense(from, to);
        dense.Status.Should().Be(RouteStatus.Found);
        Level.Length(from, dense.Points).Should().BeInRange(2.4, 3.2, "round the end of the sheet");
        foreach (var leeway in new[] { 0.25f, 0f }) {
            var planned = level.Find(from, to, leeway: leeway);
            level.ShouldBeSound(planned, from, to);
            Level.Length(from, planned.Points).Should().BeLessThan(Level.Length(from, dense.Points) * 1.15, $"with a leeway of {leeway}");
        }
        // And from a little way off on one side to the far side.
        P off = At(2.25f, 6.75f, 3.25f);
        var longer = level.Find(off, to);
        level.ShouldBeSound(longer, off, to);
        Level.Length(off, longer.Points).Should().BeLessThan(Level.Length(off, level.Dense(off, to).Points) * 1.15);
    }

    [Fact]
    public void ThroughABrickTheStepsAreTheDenseSearchsWithItsRulesForDiagonals() {
        // A thin post up through the corner that four cells of one brick share, for a body small enough that
        // all four are open: across the corner it would pass through the post.
        var level = new Level(Room, new ShapeScene([Floor, Solid.Capsule(4.5f, 4.5f, 0, 4.5f, 4.5f, 9, 0.001f)]), 0.33f);
        void Same(P from, P to, int points, float cost) {
            var cells = level.Find(from, to, straighten: false, leeway: 0f);
            level.ShouldBeSound(cells, from, to);
            (cells.Result.Count, cells.Result.Cost).Should().Be((points, cost), $"from {from} to {to}");
            var dense = level.Dense(from, to, straighten: false);
            (dense.Result.Count, dense.Result.Cost).Should().Be((points, cost), "as the dense search has it");
        }
        Same(level.Centre(8, 8, 4), level.Centre(9, 9, 4), 3, 1f);
        // From a cell this near a shape no diagonal is taken at all, not even one that leads away from the post.
        Same(level.Centre(8, 8, 4), level.Centre(9, 9, 5), 4, 1.5f);
        // Across a corner with nothing near, the diagonal is taken: across a square, and across a cube.
        Same(level.Centre(8, 10, 5), level.Centre(9, 11, 5), 2, 0.70710677f);
        Same(level.Centre(8, 10, 5), level.Centre(9, 11, 6), 2, 0.8660254f);

        // A speck beside one of the six cells that a step across a cube passes between: both ends have the room,
        // that cell is closed, and the step is not taken.
        var speck = new Level(Room, new ShapeScene([Solid.Sphere(4.45f, 5.5f, 5.5f, 0.01f)]));
        P low = speck.Centre(9, 9, 9), high = speck.Centre(10, 10, 10);
        speck.IsOpenCentre(speck.Centre(9, 10, 10), out _, out _, out _).Should().BeFalse();
        var round = speck.Find(low, high, straighten: false, leeway: 0f);
        speck.ShouldBeSound(round, low, high);
        round.Result.Cost.Should().BeApproximately(speck.Dense(low, high, straighten: false).Result.Cost, 1e-4f);
        round.Result.Cost.Should().BeApproximately(1.2071068f, 1e-4f, "across a square and then a step");

        // A body of 0.30 m, for which a step across a cube needs more room at its ends than one across a square, and a speck
        // 0.42 m from the first cell, on the side away from the cube's diagonal: that end has the room for a square, not a cube.
        var start = level.Centre(8, 10, 5);
        var s = 0.42f / MathF.Sqrt(3);
        var near = new Level(Room, new ShapeScene([Floor, Solid.Sphere(start.X - s, start.Y - s, start.Z - s, 0.01f)]), 0.30f);
        var across = near.Find(start, near.Centre(9, 11, 6), straighten: false, leeway: 0f);
        near.ShouldBeSound(across, start, near.Centre(9, 11, 6));
        (across.Result.Count, across.Result.Cost).Should().Be((3, near.Dense(start, near.Centre(9, 11, 6), straighten: false).Result.Cost));
        across.Result.Cost.Should().BeApproximately(1.2071068f, 1e-4f, "across a square and then a step, not across the cube");
    }

    [Fact]
    public void AnEndBesideASheetTakesItsOwnSideAndNoCentreBeyondItsReach() {
        // A sheet at x = 4.2, not as wide as the room, and an end 0.1 m from it. The nearest open centre is
        // through the sheet; the nearest it can see is on its own side.
        var level = new Level(Room, new ShapeScene([Floor], [4.2f, 1, 0.2f, 4.2f, 7, 0.2f, 4.2f, 1, 7, 4.2f, 7, 7], [0, 1, 2, 1, 3, 2]));
        P from = (4.1f, 4.25f, 3.25f), to = (1.25f, 1.25f, 3.25f);
        var route = level.Find(from, to);
        route.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        route.Points[0].Should().Be(level.Centre(6, 8, 6));
        level.ShouldBeSound(route, from, to);
        var blind = level.Find(from, to, shapes: false);
        blind.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        blind.Points[0].Should().Be(level.Centre(9, 8, 6), "with no shapes to see by, the nearest");

        // An end outside the grid: 1.65 m from the nearest open centre it takes it, and 1.9 m from it, which is
        // inside the box of cells it looks through and beyond its reach of 1.749 m, it does not.
        var outside = level.Find((-1.4f, 2.25f, 2.25f), to);
        outside.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        outside.Points[0].Should().Be(level.Centre(0, 4, 4));
        level.Find((-1.65f, 2.25f, 2.25f), to).Status.Should().Be(RouteStatus.Unreachable);
        level.Dense((-1.65f, 2.25f, 2.25f), to).Status.Should().Be(RouteStatus.Unreachable);
        // A goal lying on the floor: the route stops at the open centre above it.
        var resting = level.Find(to, (6.6f, 6.4f, 0.2f));
        resting.Ends.Should().Be(RouteEnds.StopsShortOfGoal);
        resting.Points[^1].Should().Be(level.Dense(to, (6.6f, 6.4f, 0.2f)).Points[^1]);
    }

    [Fact]
    public void OutOfAnOpenCubeTheWayLeavesByWhicheverPartOfAFaceLeadsOn() {
        // The room that is one open cube for a body of 0.9 m, with its one way out in the middle of a face.
        var level = new Level(new GridShape(0, 0, 0, 0.5f, 48, 48, 48), new ShapeScene(TreeScene.CubeRoom()), 0.9f);
        P from = (9.25f, 14.25f, 14.25f), to = (21.75f, 10.25f, 10.25f);
        foreach (var (a, b) in new[] { (from, to), (to, from) }) {
            var planned = level.Find(a, b);
            level.ShouldBeSound(planned, a, b);
            planned.Direct.Should().BeFalse("the passage's wall is in the way");
            Level.Length(a, planned.Points).Should().BeLessThan(Level.Length(a, level.Dense(a, b).Points) * 1.05);
        }
    }

    [Fact]
    public void StartAndGoalInOneOpenCubeAreJoinedByOneCut() {
        var level = Walled();
        // Two cells of the open cube of 8 m that begins at 8 m each way.
        P from = level.Centre(17, 17, 17), to = level.Centre(30, 28, 22);
        var cells = level.Find(from, to, straighten: false);
        level.ShouldBeSound(cells, from, to);
        cells.Points.Should().Equal(from, to);
        cells.Result.Expanded.Should().Be(2, "the cube and the goal in it");
        cells.Result.Cost.Should().BeApproximately((float)Level.Length(from, cells.Points), 1e-4f, "the cut is the way's whole cost");
        // And a route from a point to itself has that point.
        var still = level.Find(from, from);
        (still.Status, still.Direct).Should().Be((RouteStatus.Found, true));
        level.Find(from, from, straighten: false).Points.Should().Equal(from);
    }
}
