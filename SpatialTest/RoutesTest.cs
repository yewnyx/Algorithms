using FluentAssertions;
using P = (float X, float Y, float Z);

namespace Yewnyx.Spatial.Test;

public class RoutesTest {
    private const float Unit = 1f / 128f;
    private const byte Cap = 128;
    private const float Radius = 0.51f;

    // 16 cells of half a metre each way from the origin: centres at 0.25, 0.75 ... 7.75.
    private static readonly GridShape Grid = new(0, 0, 0, 0.5f, 16, 16, 16);

    // A box that covers the whole grid: every open cell is a seed.
    private static readonly Solid Everywhere = Solid.Box(new Box(-1, -1, -1, 9, 9, 9));

    private static readonly Solid Floor = Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f));

    private sealed record Planned(RouteResult Result, P[] Points) {
        public RouteStatus Status => Result.Status;
        public RouteEnds Ends => Result.Ends;
        // One straight leg from start to goal, with nothing searched.
        public bool Direct => Result.Status == RouteStatus.Found && Result.Count == 1 && Result.Expanded == 0;
    }

    // A grid marked from a scene, the regions of one radius, and the search's own arrays, which
    // are used again by every search of the level.
    private sealed class Level {
        public readonly GridShape Shape;
        public readonly ShapeScene Scene;
        public readonly byte[] Wall, Region, Cost;
        public readonly SizeClass Size;
        public readonly float[] Sums;
        public readonly int[] Links, Heap;

        public Level(GridShape shape, ShapeScene scene, float radius = Radius, Solid[]? seeds = null, Action<GridShape, byte[]>? cost = null, byte[]? wall = null) {
            Shape = shape;
            Scene = scene;
            Wall = wall ?? new byte[shape.CellCount];
            if (wall == null) {
                for (var z = 0; z < shape.CountZ; z++) {
                    WallDistance.MarkLayer(shape, scene.Set, Unit, Cap, z, Wall.AsSpan(z * shape.LayerLength, shape.LayerLength)).Should().BeTrue();
                }
            }
            SizeClass.TryMake(radius, Map, out Size).Should().BeTrue();
            Cost = cost == null ? [] : new byte[shape.CellCount];
            cost?.Invoke(shape, Cost);
            Region = new byte[shape.CellCount];
            Regions.FromSeeds(Map, Size, Cost, seeds ?? [Everywhere], Region, new int[Regions.QueueLength(shape)], out _, out _).Should().BeTrue();
            Sums = new float[Routes.SumsLength(shape)];
            Links = new int[Routes.LinksLength(shape)];
            Heap = new int[Routes.HeapLength(shape)];
        }

        public WallMap Map => new(Shape, Unit, Cap, Wall);

        // The least cost among the cells with a region, which every search of the level is told.
        public byte Least => Regions.LeastCost(Shape, Region, Cost);

        public Planned Find(P from, P to, bool shapes = true, bool straighten = true, int room = 200, byte? least = null, float leeway = 0f) {
            var points = new float[3 * room];
            var result = Routes.Find(Map, Size, shapes ? Scene.Set : default, Region, Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, straighten, Sums, Links, Heap, points, least ?? Least, leeway);
            // A search leaves the links as it must find them.
            for (var i = 0; i < Shape.CellCount; i++) {
                if (Links[i] != 0) { throw new InvalidOperationException($"Link {i} was left at {Links[i]}."); }
            }
            var held = Math.Min(result.Count, room);
            var list = new P[held];
            for (var i = 0; i < held; i++) { list[i] = (points[3 * i], points[3 * i + 1], points[3 * i + 2]); }
            return new Planned(result, list);
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
            return index >= 0 && Region[index] != 0 && Centre(x, y, z) == point;
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
                least = Math.Min(least, Scene.Exact(a.X + dx * t, a.Y + dy * t, a.Z + dz * t));
            }
            return least;
        }

        // Whether a leg passes through a cell that is closed by cost, looked at every 2 mm.
        public bool CrossesClosed(P a, P b) {
            if (Cost.Length == 0) { return false; }
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 0.002));
            for (var k = 0; k <= steps; k++) {
                var t = (double)k / steps;
                CellCost.At(Shape, Cost, (float)(a.X + dx * t), (float)(a.Y + dy * t), (float)(a.Z + dz * t), out var value).Should().BeTrue("a leg stays in the grid");
                if (value == CellCost.Closed) { return true; }
            }
            return false;
        }

        // What the cell that holds a point costs.
        public int CostAt(P point) {
            CellCost.At(Shape, Cost, point.X, point.Y, point.Z, out var value).Should().BeTrue();
            return value;
        }

        // The highest cost among the cells a leg passes through, looked at every 2 mm. A cell
        // counts where two looks in a row are in cells that cost as much, so a leg through the
        // very corner of a cell, which one look can land on, does not pass through it.
        public int Highest(P a, P b) {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 0.002));
            int highest = 0, last = CostAt(a);
            for (var k = 1; k <= steps; k++) {
                var t = (double)k / steps;
                var here = CostAt(((float)(a.X + dx * t), (float)(a.Y + dy * t), (float)(a.Z + dz * t)));
                highest = Math.Max(highest, Math.Min(here, last));
                last = here;
            }
            return highest;
        }

        // What a leg costs as flown, worked out apart from the library's walk: the leg is cut
        // wherever it crosses a plane between cells, and each piece costs its length times
        // 1 + cost / 16 of the cell its middle lies in.
        public double Flown(P a, P b) {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z, size = Shape.CellSize;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (Cost.Length == 0) { return length; }
            var cuts = new List<double> { 0, 1 };
            void Cut(double from, double d, double origin, int count) {
                if (d == 0) { return; }
                for (var k = 0; k <= count; k++) {
                    var t = (origin + k * size - from) / d;
                    if (t > 0 && t < 1) { cuts.Add(t); }
                }
            }
            Cut(a.X, dx, Shape.OriginX, Shape.CountX);
            Cut(a.Y, dy, Shape.OriginY, Shape.CountY);
            Cut(a.Z, dz, Shape.OriginZ, Shape.CountZ);
            cuts.Sort();
            var flown = 0.0;
            for (var i = 1; i < cuts.Count; i++) {
                var middle = (cuts[i - 1] + cuts[i]) / 2;
                if (cuts[i] - cuts[i - 1] < 1e-12) { continue; }
                var cost = CostAt(((float)(a.X + dx * middle), (float)(a.Y + dy * middle), (float)(a.Z + dz * middle)));
                flown += (cuts[i] - cuts[i - 1]) * length * (1 + cost / 16.0);
            }
            return flown;
        }

        // What a route costs as flown from its start.
        public double Flown(P from, P[] points) {
            var flown = 0.0;
            var at = from;
            foreach (var point in points) {
                flown += Flown(at, point);
                at = point;
            }
            return flown;
        }

        // What a route promises, held against the shapes themselves: a step keeps the class's
        // guarantee, any other leg the full radius, and none crosses a closed cell. The first leg
        // is let off when the route says it is unproven.
        public void ShouldBeSound(Planned planned, P from, P to) {
            planned.Status.Should().Be(RouteStatus.Found);
            planned.Points.Length.Should().Be(planned.Result.Count).And.BeGreaterThan(0);
            var at = from;
            for (var i = 0; i < planned.Points.Length; i++) {
                var point = planned.Points[i];
                if (i > 0 || !planned.Ends.HasFlag(RouteEnds.FirstLegUnproven)) {
                    Room(at, point).Should().BeGreaterThanOrEqualTo((IsStep(at, point) ? Size.Guarantee : Size.Radius) - 1e-4, $"on leg {i}, from {at} to {point}");
                    CrossesClosed(at, point).Should().BeFalse($"on leg {i}, from {at} to {point}");
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
    }

    // Two rooms side by side along x, with a wall 0.4 thick between them from x = 3.8 to 4.2. The
    // wall has a doorway from y = 3 to 5 and from the floor to z = 3.
    private static ShapeScene TwoRooms() => new([
        Floor,
        Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)),
        Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)),
        Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
    ]);

    [Fact]
    public void InAnEmptyRoomARouteIsOneLegAndNothingIsSearched() {
        var level = new Level(Grid, new ShapeScene([Floor]));
        P from = (1.3f, 1.1f, 1.2f), to = (6.6f, 6.9f, 5.1f);
        var route = level.Find(from, to);
        route.Direct.Should().BeTrue();
        route.Points.Should().Equal(to);
        route.Ends.Should().Be(RouteEnds.None);
        route.Result.Cost.Should().Be(0f);
        level.ShouldBeSound(route, from, to);
        // The same without shapes: the map alone proves a leg this far from everything.
        level.Find(from, to, shapes: false).Direct.Should().BeTrue();
        // A start that is the goal.
        level.Find(from, from).Points.Should().Equal(from);
    }

    [Fact]
    public void RoundOnePillarARouteIsTwoLegs() {
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(3.8f, 3.8f, 0, 4.2f, 4.2f, 9))]));
        P from = (1.25f, 3.75f, 1.25f), to = (6.75f, 5.25f, 1.25f);
        level.Room(from, to).Should().BeLessThan(0.35, "the straight line passes the pillar's corner too near");
        var route = level.Find(from, to);
        route.Result.Count.Should().Be(2);
        route.Result.Expanded.Should().BeGreaterThan(0);
        route.Ends.Should().Be(RouteEnds.None);
        level.IsOpenCentre(route.Points[0], out _, out _, out _).Should().BeTrue("the turn is at a cell's centre");
        route.Points[1].Should().Be(to);
        level.ShouldBeSound(route, from, to);
        // By the map alone the turn is farther out, and the legs as sound.
        var without = level.Find(from, to, shapes: false);
        without.Result.Count.Should().BeGreaterThanOrEqualTo(2);
        level.ShouldBeSound(without, from, to);
    }

    [Fact]
    public void ThroughADoorwayEveryLegKeepsItsPromise() {
        var level = new Level(Grid, TwoRooms());
        P[] left = [(1.25f, 1.25f, 1.25f), (3.1f, 7.4f, 5.6f), (0.6f, 4.1f, 0.9f), (2.75f, 6.25f, 2.75f)];
        P[] right = [(6.75f, 6.75f, 1.25f), (4.9f, 0.8f, 6.2f), (7.3f, 4.0f, 0.8f), (5.25f, 2.25f, 3.25f)];
        foreach (var from in left) {
            foreach (var to in right) {
                foreach (var shapes in new[] { true, false }) {
                    var route = level.Find(from, to, shapes);
                    level.ShouldBeSound(route, from, to);
                    // The map alone cannot always prove the last leg to a goal 0.6 m above the floor.
                    if (shapes) { route.Ends.Should().Be(RouteEnds.None); }
                    var back = level.Find(to, from, shapes);
                    level.ShouldBeSound(back, to, from);
                    back.Result.Cost.Should().BeApproximately(route.Result.Cost, 1e-3f, "with no costs the way back costs the same");
                }
            }
        }
        // Straight through the doorway where that is clear.
        level.Find((2.25f, 4f, 1.25f), (5.75f, 4f, 1.25f)).Direct.Should().BeTrue();
    }

    // The shortest way by a plain search with the same rule for steps, in double: no heap, no
    // estimate, the least cell found by looking at every one.
    private static double Plain(Level level, int start, int goal) {
        var shape = level.Shape;
        int nx = shape.CountX, ny = shape.CountY, nz = shape.CountZ, cells = shape.CellCount;
        var total = new double[cells];
        var done = new bool[cells];
        Array.Fill(total, double.PositiveInfinity);
        total[start] = 0;
        var wanted = level.Region[start];
        bool Open(int x, int y, int z) {
            var index = shape.IndexOf(x, y, z);
            return index >= 0 && level.Region[index] == wanted && (level.Cost.Length == 0 || level.Cost[index] != 255);
        }
        while (true) {
            var current = -1;
            for (var cell = 0; cell < cells; cell++) {
                if (!done[cell] && total[cell] < double.PositiveInfinity && (current < 0 || total[cell] < total[current])) { current = cell; }
            }
            if (current < 0 || current == goal) { break; }
            done[current] = true;
            int cx = current % nx, cy = current / nx % ny, cz = current / (nx * ny);
            for (var dz = -1; dz <= 1; dz++) {
                for (var dy = -1; dy <= 1; dy++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        var axes = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                        if (axes == 0 || !Open(cx + dx, cy + dy, cz + dz)) { continue; }
                        var next = shape.IndexOf(cx + dx, cy + dy, cz + dz);
                        if (axes > 1) {
                            // Both ends far enough out for the diagonal, and every other cell of the square or cube open.
                            var need = axes == 2 ? level.Size.Plane : level.Size.Space;
                            if (level.Wall[current] < need || level.Wall[next] < need) { continue; }
                            var all = true;
                            for (var corner = 1; corner < 7; corner++) {
                                int ox = (corner & 1) * dx, oy = ((corner >> 1) & 1) * dy, oz = ((corner >> 2) & 1) * dz;
                                if (!Open(cx + ox, cy + oy, cz + oz)) { all = false; }
                            }
                            if (!all) { continue; }
                        }
                        var entered = level.Cost.Length == 0 ? 1.0 : 1.0 + level.Cost[next] / 16.0;
                        var reached = total[current] + shape.CellSize * Math.Sqrt(axes) * entered;
                        if (reached < total[next]) { total[next] = reached; }
                    }
                }
            }
        }
        return total[goal];
    }

    // Costs that differ from cell to cell by a fixed rule, some cells closed.
    private static void Patchwork(GridShape shape, byte[] cost) {
        for (var z = 0; z < shape.CountZ; z++) {
            for (var y = 0; y < shape.CountY; y++) {
                for (var x = 0; x < shape.CountX; x++) {
                    var mix = (x * 7 + y * 13 + z * 5) % 11;
                    cost[shape.IndexOf(x, y, z)] = (byte)(mix == 0 ? 255 : mix < 4 ? 0 : mix < 7 ? 12 : mix < 9 ? 40 : 200);
                }
            }
        }
    }

    [Fact]
    public void TheWayCostsWhatAPlainSearchByTheSameStepsFinds() {
        // With no costs, with a patchwork of them, and with the same patchwork where no cell
        // costs less than 12: the search is told the least, and reckons what is left to go at it.
        foreach (var costs in new[] { 0, 1, 2 }) {
            var level = new Level(Grid, new ShapeScene([
                Floor,
                Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)), Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)), Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
                ShapeScene.TurnedBox(6, 2.5, 3, 0.3, 1.2, 2.5, 30), Solid.Sphere(1.8f, 6, 2.5f, 0.9f), Solid.Capsule(5, 6.5f, 0.2f, 7.5f, 5.5f, 4, 0.2f),
            ]), cost: costs == 0 ? null : costs == 1 ? Patchwork : (shape, cost) => {
                Patchwork(shape, cost);
                for (var i = 0; i < cost.Length; i++) { cost[i] = Math.Max(cost[i], (byte)12); }
            });
            level.Least.Should().Be(costs == 2 ? (byte)12 : (byte)0);
            (int x, int y, int z)[] cells = [(1, 1, 2), (6, 14, 9), (2, 9, 12), (13, 13, 3), (14, 2, 13), (9, 8, 6), (12, 6, 1), (0, 15, 15)];
            var compared = 0;
            foreach (var a in cells) {
                foreach (var b in cells) {
                    int start = level.Shape.IndexOf(a.x, a.y, a.z), goal = level.Shape.IndexOf(b.x, b.y, b.z);
                    if (a == b || level.Region[start] == 0 || level.Region[goal] == 0) { continue; }
                    P from = level.Centre(a.x, a.y, a.z), to = level.Centre(b.x, b.y, b.z);
                    var route = level.Find(from, to, straighten: false);
                    route.Status.Should().Be(RouteStatus.Found);
                    var plain = Plain(level, start, goal);
                    ((double)route.Result.Cost).Should().BeApproximately(plain, 1e-4 * plain, $"from {a} to {b}");
                    // The points are the cells the way steps through, and their steps add up to the same.
                    route.Points[0].Should().Be(from);
                    route.Points[^1].Should().Be(to);
                    var sum = 0.0;
                    for (var i = 1; i < route.Points.Length; i++) {
                        level.IsStep(route.Points[i - 1], route.Points[i]).Should().BeTrue();
                        route.Points[i].Should().NotBe(route.Points[i - 1]);
                        level.IsOpenCentre(route.Points[i], out var x, out var y, out var z);
                        double dx = route.Points[i].X - route.Points[i - 1].X, dy = route.Points[i].Y - route.Points[i - 1].Y, dz = route.Points[i].Z - route.Points[i - 1].Z;
                        sum += Math.Sqrt(dx * dx + dy * dy + dz * dz) * (level.Cost.Length == 0 ? 1.0 : 1.0 + level.Cost[level.Shape.IndexOf(x, y, z)] / 16.0);
                    }
                    sum.Should().BeApproximately(plain, 1e-4 * plain);
                    level.ShouldBeSound(route, from, to);
                    // Pulled straight, the same search lies under it.
                    var straight = level.Find(from, to);
                    level.ShouldBeSound(straight, from, to);
                    if (!straight.Direct) { straight.Result.Cost.Should().Be(route.Result.Cost); }
                    compared++;
                }
            }
            compared.Should().BeGreaterThanOrEqualTo(30);
        }
    }

    [Fact]
    public void AStepPaysForTheCellItEnters() {
        // One row of twelve cells, every one open and none far enough from a wall for a straight
        // leg to be proven. Cells 5 and 6 cost three times as much to enter.
        var row = new GridShape(0, 0, 0, 0.5f, 12, 1, 1);
        var wall = new byte[12];
        Array.Fill(wall, (byte)60);
        var level = new Level(row, new ShapeScene([]), radius: 0.4f, wall: wall, cost: (_, cost) => { cost[5] = 32; cost[6] = 32; });
        float Cost(int from, int to) {
            var route = level.Find(level.Centre(from, 0, 0), level.Centre(to, 0, 0), shapes: false, straighten: false);
            route.Status.Should().Be(RouteStatus.Found);
            route.Result.Count.Should().Be(Math.Abs(to - from) + 1);
            return route.Result.Cost;
        }
        Cost(2, 9).Should().Be(5.5f, "five plain cells and two that cost three times");
        Cost(9, 2).Should().Be(5.5f);
        // From inside the area the cell left costs nothing; into it, both of its cells are paid for.
        Cost(5, 9).Should().Be(3.0f, "into 6, 7, 8 and 9");
        Cost(9, 5).Should().Be(4.0f, "into 8, 7, 6 and 5");
        Cost(6, 5).Should().Be(1.5f);
        Cost(6, 7).Should().Be(0.5f);
    }

    // Sets the cost of the cells from one cell to another in x and in y, both included, at every height.
    private static void Area(GridShape shape, byte[] cost, int fromX, int toX, int fromY, int toY, byte value) {
        for (var z = 0; z < shape.CountZ; z++) {
            for (var y = fromY; y <= toY; y++) {
                for (var x = fromX; x <= toX; x++) { cost[shape.IndexOf(x, y, z)] = value; }
            }
        }
    }

    // Holds a route between the centres of two open cells to its word on cost: a leg that is not
    // a step passes through no cell that costs more than the costliest cell of the way between
    // its two ends, and costs no more as flown than that part of the way does from centre to
    // centre. Returns how many such legs there were, and how many passed through a cell with a cost.
    private static (int Legs, int Through) ShouldNotRaiseCost(Level level, P from, P to) {
        var route = level.Find(from, to);
        level.ShouldBeSound(route, from, to);
        route.Ends.Should().Be(RouteEnds.None);
        if (route.Direct) {
            // With nothing searched, the straight leg passes through no cell with a cost at all.
            level.Flown(from, to).Should().BeApproximately(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2) + Math.Pow(to.Z - from.Z, 2)), 1e-9, $"straight from {from} to {to}");
            return (1, 0);
        }
        var way = level.Find(from, to, straighten: false);
        way.Points[0].Should().Be(from);
        way.Points[^1].Should().Be(to);
        route.Result.Cost.Should().Be(way.Result.Cost);
        int at = 0, legs = 0, through = 0;
        foreach (var point in route.Points) {
            var next = Array.IndexOf(way.Points, point, at + 1);
            next.Should().BeGreaterThan(at, "a route's points are cells of the way, in the way's order");
            if (next > at + 1) {
                var most = 0;
                for (var i = at; i <= next; i++) { most = Math.Max(most, level.CostAt(way.Points[i])); }
                var crossed = level.Highest(way.Points[at], point);
                crossed.Should().BeLessThanOrEqualTo(most, $"on the leg from {way.Points[at]} to {point}, of the route from {from} to {to}");
                var stretch = 0.0;
                for (var i = at; i < next; i++) { stretch += level.Flown(way.Points[i], way.Points[i + 1]); }
                level.Flown(way.Points[at], point).Should().BeLessThanOrEqualTo(stretch * (1 + 2e-6), $"on the leg from {way.Points[at]} to {point}, of the route from {from} to {to}");
                legs++;
                through += crossed > 0 ? 1 : 0;
            }
            at = next;
        }
        at.Should().Be(way.Points.Length - 1);
        level.Flown(from, route.Points).Should().BeLessThanOrEqualTo(level.Flown(from, way.Points) * (1 + 2e-6));
        return (legs, through);
    }

    [Fact]
    public void AStraightLegDoesNotCutAcrossACostlierAreaTheWayWentRound() {
        // An empty room with an area in the middle of it, at every height, that costs three times
        // as much to cross. The start and the goal are on either side of it.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => Area(shape, cost, 6, 9, 4, 11, 32));
        P from = level.Centre(2, 8, 4), to = level.Centre(13, 8, 4);
        // Nothing stands between the two, and the straight line crosses the area.
        level.Room(from, to).Should().BeGreaterThan(1.0);
        level.Highest(from, to).Should().Be(32);
        // Going round is cheaper, so the way goes round.
        var way = level.Find(from, to, straighten: false);
        foreach (var point in way.Points) { level.CostAt(point).Should().Be(0); }
        // Pulled straight, it still goes round: a few legs, none of them through the area.
        var route = level.Find(from, to);
        route.Direct.Should().BeFalse();
        route.Result.Count.Should().Be(3, "to one corner of the area, along its side, and on to the goal");
        route.Result.Cost.Should().Be(way.Result.Cost);
        var at = from;
        foreach (var point in route.Points) {
            level.Highest(at, point).Should().Be(0, $"on the leg from {at} to {point}");
            at = point;
        }
        ShouldNotRaiseCost(level, from, to).Legs.Should().BeGreaterThanOrEqualTo(2);
        // The same room with no costs is one straight leg.
        new Level(Grid, new ShapeScene([Floor])).Find(from, to).Direct.Should().BeTrue();
        // With an end in the area, a leg may pass through cells that cost what the end's cell
        // costs. These are one leg each, and searched for first: only the way says what the
        // leg may cost.
        foreach (var (a, b) in new[] { (level.Centre(7, 9, 4), level.Centre(8, 5, 6)), (level.Centre(7, 8, 4), to), (from, level.Centre(8, 8, 4)) }) {
            var one = level.Find(a, b);
            one.Points.Should().Equal(b);
            one.Result.Expanded.Should().BeGreaterThan(0);
            ShouldNotRaiseCost(level, a, b).Should().Be((1, 1));
        }
    }

    [Fact]
    public void AStraightLegCrossesACostlierAreaWhereTheWayDid() {
        // The area reaches from one side of the grid to the other, at every height: there is no
        // way round it.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => Area(shape, cost, 6, 9, 0, 15, 32));
        P from = level.Centre(2, 8, 4), to = level.Centre(13, 8, 4);
        var route = level.Find(from, to);
        route.Points.Should().Equal(to);
        route.Result.Expanded.Should().BeGreaterThan(0, "it is the way's cells that say the leg may cross");
        route.Result.Cost.Should().Be(9.5f, "seven plain cells and four that cost three times");
        ShouldNotRaiseCost(level, from, to).Should().Be((1, 1));

        // A band that costs twice from side to side, and beyond it a block that costs four times
        // and can be gone round. A leg crosses the band, as the way did, and none enters the block.
        var two = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => {
            Area(shape, cost, 6, 7, 0, 15, 16);
            Area(shape, cost, 10, 11, 5, 10, 48);
        });
        P goal = two.Centre(14, 8, 4);
        two.Highest(from, goal).Should().Be(48);
        var round = two.Find(from, goal);
        round.Result.Count.Should().BeInRange(2, 4);
        var at = from;
        var highest = 0;
        foreach (var point in round.Points) {
            highest = Math.Max(highest, two.Highest(at, point));
            at = point;
        }
        highest.Should().Be(16);
        ShouldNotRaiseCost(two, from, goal).Through.Should().BeGreaterThan(0);

        // The highest cost is not all a leg is held to. Here the block costs what the band does,
        // so by the highest cost alone one leg from start to goal may cross both, though the way
        // went round the block. It costs more as flown than the way does, and is not taken.
        var same = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => {
            Area(shape, cost, 6, 7, 0, 15, 32);
            Area(shape, cost, 10, 12, 6, 9, 32);
        });
        var cells = same.Find(from, goal, straighten: false);
        cells.Points.Count(point => same.CostAt(point) == 32).Should().Be(2, "the way crosses the band and goes round the block");
        same.Highest(from, goal).Should().Be(32);
        same.Flown(from, goal).Should().BeGreaterThan(same.Flown(from, cells.Points) + 1);
        var kept = same.Find(from, goal);
        kept.Result.Count.Should().BeInRange(2, 4);
        same.Flown(from, kept.Points).Should().BeLessThan(same.Flown(from, cells.Points));
        at = from;
        foreach (var point in kept.Points) {
            for (var k = 0; k <= 100; k++) {
                P on = (at.X + (point.X - at.X) * k / 100f, at.Y + (point.Y - at.Y) * k / 100f, at.Z + (point.Z - at.Z) * k / 100f);
                (on.X > 5.01f && on.X < 6.49f && on.Y > 3.01f && on.Y < 4.99f).Should().BeFalse($"{on} is in the block");
            }
            at = point;
        }
        ShouldNotRaiseCost(same, from, goal).Through.Should().Be(1, "the leg that crosses the band");
    }

    [Fact]
    public void PullingStraightNeverCostsMoreAsFlownThanTheWayLeftAsCells() {
        // A room with things in it, and an area in the middle of it that costs three times and
        // does not reach the walls. With one end inside, the highest cost lets a leg through any
        // of the area, since the end's own cell is in it: only what the leg costs as flown keeps
        // the route from crossing more of the area than its way did.
        var level = new Level(Grid, new ShapeScene([
            Floor, ShapeScene.TurnedBox(6, 2.5, 3, 0.3, 1.2, 2.5, 30), Solid.Sphere(1.8f, 6, 2.5f, 0.9f), Solid.Capsule(5, 6.5f, 0.2f, 7.5f, 5.5f, 4, 0.2f),
        ]), cost: (shape, cost) => Area(shape, cost, 5, 11, 5, 11, 32));
        (int x, int y, int z)[] cells = [
            (1, 1, 2), (14, 2, 13), (0, 15, 15), (13, 13, 3), (2, 9, 12), (15, 9, 8), (7, 0, 10), (4, 12, 3),   // outside the area
            (6, 6, 4), (10, 10, 9), (8, 8, 6), (10, 6, 12), (6, 10, 2), (9, 7, 14),                          // inside it
        ];
        int routes = 0, straightCostsMore = 0, shorter = 0;
        foreach (var a in cells) {
            foreach (var b in cells) {
                if (a == b || level.Region[Grid.IndexOf(a.x, a.y, a.z)] == 0 || level.Region[Grid.IndexOf(b.x, b.y, b.z)] == 0) { continue; }
                P from = level.Centre(a.x, a.y, a.z), to = level.Centre(b.x, b.y, b.z);
                ShouldNotRaiseCost(level, from, to);
                var route = level.Find(from, to);
                var way = level.Find(from, to, straighten: false);
                double flown = level.Flown(from, route.Points), kept = level.Flown(from, way.Points);
                flown.Should().BeLessThanOrEqualTo(kept * (1 + 2e-6), $"from {a} to {b}");
                routes++;
                // How often the rule had something to decide: the straight line is clear and costs more than the way.
                if (level.Room(from, to) > Radius + 0.003 && level.Flown(from, to) > kept * 1.001) {
                    straightCostsMore++;
                    route.Result.Count.Should().BeGreaterThan(1, $"from {a} to {b}");
                }
                if (flown < kept * 0.999) { shorter++; }
            }
        }
        routes.Should().BeGreaterThan(120);
        straightCostsMore.Should().BeGreaterThan(10);
        shorter.Should().BeGreaterThan(100);
    }

    [Fact]
    public void ALegThatClipsTheCornerOfACostlierCellIsNotTaken() {
        // One column of cells costs more: from 4 to 4.5 in x and in y.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => Area(shape, cost, 8, 8, 8, 8, 32));
        // A line that is inside the column for 6 cm, at its corner: less than a sample of the leg is long.
        P from = (3.29f, 4.75f, 2.25f), to = (4.75f, 3.29f, 2.25f);
        level.Highest(from, to).Should().Be(32);
        var route = level.Find(from, to);
        route.Direct.Should().BeFalse();
        level.ShouldBeSound(route, from, to);
        var at = from;
        foreach (var point in route.Points) {
            level.Highest(at, point).Should().Be(0, $"on the leg from {at} to {point}");
            at = point;
        }
        // The same line 7 mm clear of the corner is taken.
        P beside = (3.24f, 4.75f, 2.25f), besideTo = (4.75f, 3.24f, 2.25f);
        level.Highest(beside, besideTo).Should().Be(0);
        level.Find(beside, besideTo).Direct.Should().BeTrue();
    }

    [Fact]
    public void TheLegBetweenAnEndAndTheCentreOfItsCellIsNotHeldToCost() {
        // The corner of a floor and two walls, their faces at 0.3. A point 0.52 from the walls and
        // 0.68 from the floor is clear for the body, in a cell that is not open. The nearest open
        // centre is the one across that cell's far corner, and the straight line to it passes
        // through the cell above the point's. That cell and the five others that meet the two at
        // the corner cost four times; none of them is open.
        var level = new Level(Grid, new ShapeScene([
            Solid.Box(new Box(-1, -1, -1, 9, 9, 0.3f)), Solid.Box(new Box(-1, -1, 0, 0.3f, 9, 9)), Solid.Box(new Box(-1, -1, 0, 9, 0.3f, 9)),
        ]), cost: (shape, cost) => {
            for (var i = 1; i < 7; i++) { cost[shape.IndexOf(1 + (i & 1), 1 + ((i >> 1) & 1), 1 + (i >> 2))] = 48; }
        });
        P corner = (0.82f, 0.82f, 0.98f), open = level.Centre(2, 2, 2), far = level.Centre(10, 10, 6);
        level.Highest(corner, open).Should().Be(48);
        level.Region[Grid.IndexOf(1, 1, 1)].Should().Be(0);
        level.CostAt(corner).Should().Be(0);
        level.CostAt(open).Should().Be(0);
        level.Region[Grid.IndexOf(2, 2, 2)].Should().NotBe(0);
        for (var i = 1; i < 7; i++) { level.Region[Grid.IndexOf(1 + (i & 1), 1 + ((i >> 1) & 1), 1 + (i >> 2))].Should().Be(0); }
        // A leg into the corner from anywhere else passes through one of the six, so none is taken:
        // the route goes by the open centre. From there the leg is taken whatever lies under it,
        // or the route would not begin, or would stop short.
        level.Highest(far, corner).Should().Be(48);
        var out_ = level.Find(corner, far);
        out_.Ends.Should().Be(RouteEnds.None);
        out_.Points[0].Should().Be(open);
        level.ShouldBeSound(out_, corner, far);
        var back = level.Find(far, corner);
        back.Ends.Should().Be(RouteEnds.None);
        back.Points[^1].Should().Be(corner);
        back.Points[^2].Should().Be(open);
        level.ShouldBeSound(back, far, corner);
        // With every cell kept, the same two legs.
        var cells = level.Find(far, corner, straighten: false);
        cells.Ends.Should().Be(RouteEnds.None);
        cells.Points[^1].Should().Be(corner);
        level.Find(corner, far, straighten: false).Ends.Should().Be(RouteEnds.None);

        // The same with the corner's own cell as costly as the six: a leg from or to the point may
        // pass through cells that cost what the point's cell costs, so it need not go by the open
        // centre. A block that costs more still lies on the straight line to the far point, and
        // the way goes round it, so these are legs of a searched route.
        var seven = new Level(Grid, level.Scene, cost: (shape, cost) => {
            for (var i = 0; i < 7; i++) { cost[shape.IndexOf(1 + (i & 1), 1 + ((i >> 1) & 1), 1 + (i >> 2))] = 48; }
            Area(shape, cost, 5, 7, 5, 7, 200);
        });
        seven.CostAt(corner).Should().Be(48);
        seven.Highest(corner, far).Should().Be(200);
        var first = seven.Find(corner, far);
        first.Direct.Should().BeFalse();
        first.Ends.Should().Be(RouteEnds.None);
        first.Points[0].Should().NotBe(open);
        seven.ShouldBeSound(first, corner, far);
        var last = seven.Find(far, corner);
        last.Ends.Should().Be(RouteEnds.None);
        last.Points[^1].Should().Be(corner);
        seven.ShouldBeSound(last, far, corner);
        seven.Flown(far, last.Points).Should().BeLessThanOrEqualTo(seven.Flown(far, seven.Find(far, corner, straighten: false).Points) * (1 + 2e-6));
    }

    [Fact]
    public void ALegMayEndInTheOneCostlierCellOfItsStretchAndLeaveFromIt() {
        // Wall distances written by hand: a passage two cells wide along x, and another two cells
        // wide along y from its end, with all the room there is. Every other cell has none, and is
        // not open. A straight leg is proven only along one arm; the cell where the arms meet is
        // where a route turns.
        var wall = new byte[Grid.CellCount];
        for (var z = 0; z < 16; z++) {
            for (var x = 1; x <= 11; x++) { wall[Grid.IndexOf(x, 4, z)] = Cap; wall[Grid.IndexOf(x, 5, z)] = Cap; }
            for (var y = 4; y <= 13; y++) { wall[Grid.IndexOf(10, y, z)] = Cap; wall[Grid.IndexOf(11, y, z)] = Cap; }
        }
        // The four cells where the arms meet cost three times. The cheapest way enters one of them only.
        var level = new Level(Grid, new ShapeScene([]), wall: wall, cost: (shape, cost) => Area(shape, cost, 10, 11, 4, 5, 32));
        P from = level.Centre(2, 4, 4), to = level.Centre(10, 12, 4), turn = level.Centre(10, 5, 4);
        var way = level.Find(from, to, shapes: false, straighten: false);
        way.Points.Count(point => level.CostAt(point) == 32).Should().Be(1);
        way.Points.Should().Contain(turn);
        // The leg to that cell ends in a cell that costs more than every other of its stretch, and
        // the leg on from it begins in one. Both are taken: the cells at a leg's two ends are of
        // the stretch it replaces.
        var route = level.Find(from, to, shapes: false);
        route.Points.Should().Equal(turn, to);
        route.Ends.Should().Be(RouteEnds.None);
    }

    [Fact]
    public void ALegIsHeldToItsOwnStretchOfTheWayAndNotToTheWholeWay() {
        // Two rooms and a doorway. Every way from the start to the doorway crosses a band that
        // costs three times. In the far room a block that costs as much lies between the doorway
        // and the goal, and the way goes round it.
        var level = new Level(Grid, TwoRooms(), cost: (shape, cost) => {
            Area(shape, cost, 2, 3, 0, 15, 32);
            Area(shape, cost, 11, 12, 7, 9, 32);
        });
        P from = level.Centre(1, 2, 2), to = level.Centre(14, 8, 2);
        var way = level.Find(from, to, straighten: false);
        way.Points.Count(point => level.CostAt(point) == 32).Should().Be(2, "the band's two cells, and none of the block's");
        // The legs that follow the band are of a stretch that crosses nothing costly, so none of
        // them crosses the block, though the way as a whole has crossed cells that cost as much.
        var (legs, through) = ShouldNotRaiseCost(level, from, to);
        legs.Should().BeGreaterThanOrEqualTo(3);
        through.Should().Be(1, "the first leg crosses the band");
    }

    [Fact]
    public void ALegToAGoalMayPassThroughACellThatCostsWhatTheGoalsOwnCellDoes() {
        // Wall distances written by hand: every cell has all the room there is, but for one,
        // which is not open. The goal is in that one, 0.1 m into it, and the cell costs four
        // times. The only shape is far below, so every leg is clear, and only cost decides.
        var wall = new byte[Grid.CellCount];
        Array.Fill(wall, Cap);
        wall[Grid.IndexOf(11, 4, 4)] = 0;
        var scene = new ShapeScene([Solid.Box(new Box(-1, -1, -9, 9, 9, -8))]);
        var level = new Level(Grid, scene, wall: wall, cost: (shape, cost) => {
            cost[shape.IndexOf(11, 4, 4)] = 48;
            Area(shape, cost, 5, 7, 5, 8, 200);
        });
        P goal = (5.6f, 2.25f, 2.25f), beside = level.Centre(10, 4, 4);
        level.CostAt(goal).Should().Be(48);
        level.Region[Grid.IndexOf(11, 4, 4)].Should().Be(0);
        // Along the row that leads to the goal: one leg, to the goal itself. It costs what any
        // way must, from the start's centre to the centre beside the goal and on into the goal's
        // cell, so it is taken with nothing searched.
        P inLine = level.Centre(2, 4, 4);
        var straight = level.Find(inLine, goal);
        straight.Direct.Should().BeTrue();
        level.Find(inLine, goal, straighten: false).Points[^2].Should().Be(beside, "the goal's cell is the open one beside it");
        // From a start that a block of a higher cost hides from the goal: the way comes round the
        // block and down the same row, and the last leg is to the goal, not to the centre beside it.
        P from = level.Centre(2, 9, 4);
        level.Highest(from, goal).Should().Be(200);
        var route = level.Find(from, goal);
        route.Ends.Should().Be(RouteEnds.None);
        route.Result.Count.Should().BeInRange(2, 4);
        route.Points[^1].Should().Be(goal);
        route.Points[^2].Should().NotBe(beside);
        level.Flown(from, route.Points).Should().BeLessThanOrEqualTo(level.Flown(from, level.Find(from, goal, straighten: false).Points) * (1 + 2e-6));
        // The same the other way about: the first leg from that point need not go by the centre beside it.
        var back = level.Find(goal, from);
        back.Ends.Should().Be(RouteEnds.None);
        back.Points[0].Should().NotBe(beside);
        back.Points[^1].Should().Be(from);
    }

    [Fact]
    public void ABumpBetweenTwoLooksAtTheMapIsNotMissed() {
        // A leg of 5 m through open space, which the map is asked about every 0.125 m, and beside
        // it a ball of 2 cm. The ball is level with the middle between two of those looks, so it
        // is 6 cm along the leg from the nearest of them. Where the map cannot prove the leg the
        // shapes are asked, at points close enough together that nothing between two is missed.
        P from = (1.25f, 4.25f, 2.25f), to = (6.25f, 4.25f, 2.25f);
        Level With(float gap) => new(Grid, new ShapeScene([Floor, Solid.Sphere(3.8125f, 4.25f + gap + 0.02f, 2.25f, 0.02f)]));
        // Half a millimetre inside the radius: at the two looks beside it the ball is 0.513 away,
        // which would pass.
        var inside = With(0.5095f);
        inside.Room(from, to).Should().BeInRange(0.5094, 0.5099);
        Math.Sqrt(0.5095 * 0.5095 + 0.0625 * 0.0625).Should().BeGreaterThan(Radius + 0.003);
        inside.Find(from, to).Direct.Should().BeFalse();
        inside.ShouldBeSound(inside.Find(from, to), from, to);
        // Three millimetres clear of the radius, the leg is taken.
        With(0.513f).Find(from, to).Direct.Should().BeTrue();
    }

    [Fact]
    public void AnEndTakesNoCentreFartherOffThanItsReach() {
        // Wall distances written by hand: no cell is open but those from one x on. The end is at
        // a centre that is not open. For a radius of 0.51 on half-metre cells an end reaches
        // 1.749 m, and it looks through a box of cells that is wider than that.
        byte[] OpenFrom(int x) {
            var wall = new byte[Grid.CellCount];
            for (var i = 0; i < wall.Length; i++) { wall[i] = i % 16 >= x ? Cap : (byte)0; }
            return wall;
        }
        P end = (2.25f, 4.25f, 2.25f), far = (6.75f, 4.25f, 2.25f);
        // The nearest open centre 1.5 m off is taken.
        var within = new Level(Grid, new ShapeScene([]), wall: OpenFrom(7));
        var route = within.Find(end, far, shapes: false, straighten: false);
        route.Status.Should().Be(RouteStatus.Found);
        route.Points[0].Should().Be(within.Centre(7, 8, 4));
        within.Find(far, end, shapes: false).Ends.Should().Be(RouteEnds.StopsShortOfGoal);
        // 2 m off, it is not: there is no way from the end, or to it.
        var beyond = new Level(Grid, new ShapeScene([]), wall: OpenFrom(8));
        beyond.Find(end, far, shapes: false).Status.Should().Be(RouteStatus.Unreachable);
        beyond.Find(far, end, shapes: false).Status.Should().Be(RouteStatus.Unreachable);
        beyond.Find(far, (4.75f, 4.25f, 2.25f), shapes: false).Status.Should().Be(RouteStatus.Found);
        // The reach is the same whichever way the centre lies: 1.5, 0.5 and 0.5 off is 1.66 m,
        // and 1.5, 1 and 0.5 off is 1.87 m.
        foreach (var (y, reached) in new[] { (9, true), (10, false) }) {
            var wall = new byte[Grid.CellCount];
            wall[Grid.IndexOf(7, y, 5)] = Cap;
            var one = new Level(Grid, new ShapeScene([]), wall: wall);
            one.Find(end, one.Centre(7, y, 5), shapes: false).Status.Should().Be(reached ? RouteStatus.Found : RouteStatus.Unreachable);
        }
    }

    [Fact]
    public void TheMapProvesALegOnlyWhereItHoldsMoreThanTheRadius() {
        // Wall distances written by hand, the same in every cell, and no shapes. A leg is proven
        // from the map where the room it says at every look is the class's LegNeed, which is 4 mm
        // more than the radius: between two looks a leg could otherwise dip inside the radius.
        // The map says least half way between two centres, where every other look falls.
        P from = (1.25f, 4.25f, 2.25f), to = (6.25f, 4.25f, 2.25f);
        int open = 0, taken = 0;
        for (var count = 66; count <= Cap; count++) {
            var wall = new byte[Grid.CellCount];
            Array.Fill(wall, (byte)count);
            var level = new Level(Grid, new ShapeScene([]), wall: wall);
            level.Size.LegNeed.Should().BeInRange(Radius + 0.003f, Radius + 0.005f);
            var room = float.MaxValue;
            for (var k = 0; k <= 40; k++) {
                WallDistance.RoomAt(level.Map, from.X + k * 0.125f, from.Y, from.Z, out var here).Should().BeTrue();
                room = Math.Min(room, here);
            }
            var route = level.Find(from, to, shapes: false);
            route.Status.Should().Be(RouteStatus.Found);
            route.Direct.Should().Be(room >= level.Size.LegNeed, $"with {count} in every cell, and {room} m of room at the least");
            if (room >= Radius && !route.Direct) { open++; }
            if (route.Direct) { taken++; }
        }
        open.Should().Be(1, "at 73 the map says the radius is kept at every look, and that is not enough");
        taken.Should().BeGreaterThan(40);
    }

    [Fact]
    public void OfCentresEquallyNearAnEndTakesTheNextWhenItCannotSeeTheFirst() {
        // Wall distances written by hand: every cell is open but two that share a face, and the
        // end lies in the middle of that face. Eight open centres are equally near it, 0.559 m
        // off. The first of them in x, then y, then z is the one at 7, 7, 4; the next at 7, 8, 3.
        var wall = new byte[Grid.CellCount];
        Array.Fill(wall, Cap);
        wall[Grid.IndexOf(7, 8, 4)] = 0;
        wall[Grid.IndexOf(8, 8, 4)] = 0;
        Solid farBelow = Solid.Box(new Box(-1, -1, -9, 9, 9, -8));
        P end = (4f, 4.25f, 2.25f), far = (6.75f, 6.75f, 2.25f);
        var open = new Level(Grid, new ShapeScene([farBelow]), wall: wall);
        open.Find(end, far, straighten: false).Points[0].Should().Be(open.Centre(7, 7, 4));
        // A small sheet between the end and that first centre, and nothing else: the next is taken.
        var hidden = new Level(Grid, new ShapeScene([farBelow], [3.6f, 4f, 2f, 4.1f, 4f, 2f, 3.6f, 4f, 2.5f, 4.1f, 4f, 2.5f], [0, 1, 2, 1, 3, 2]), wall: wall);
        hidden.Find(end, far, straighten: false).Points[0].Should().Be(hidden.Centre(7, 8, 3));
        hidden.Find(far, end, straighten: false).Points[^1].Should().BeOneOf(end, hidden.Centre(7, 8, 3));
    }

    [Fact]
    public void WhereEveryCellCostsTheSameAStraightLegIsTakenWithNothingSearched() {
        // A room with a pillar in it, where every cell costs three times. No way between two
        // points can cost less than the straight line between them at that cost, so a clear
        // straight leg is the cheapest there is, and is known to be before anything is searched.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(3.6f, 3.6f, 0, 4.4f, 4.4f, 9))]), cost: (shape, cost) => Array.Fill(cost, (byte)32));
        level.Least.Should().Be(32);
        P from = (1.3f, 1.1f, 2.2f), to = (6.4f, 2.9f, 3.3f);
        var route = level.Find(from, to);
        route.Direct.Should().BeTrue();
        level.ShouldBeSound(route, from, to);
        // Told nothing of the least cost, the same leg is found after a search.
        var untold = level.Find(from, to, least: 0);
        untold.Points.Should().Equal(to);
        untold.Result.Expanded.Should().BeGreaterThan(0);

        // Round the pillar there is a search either way. Told the least cost it takes up far fewer
        // cells, as few as in the same room with no costs, and the way costs the same.
        P behind = (6.75f, 6.75f, 2.25f), start = (1.25f, 1.25f, 2.25f);
        var told = level.Find(start, behind);
        var blind = level.Find(start, behind, least: 0);
        told.Result.Expanded.Should().BeGreaterThan(0);
        told.Result.Cost.Should().BeApproximately(blind.Result.Cost, 1e-4f);
        level.ShouldBeSound(told, start, behind);
        level.Flown(start, told.Points).Should().BeApproximately(level.Flown(start, blind.Points), 1e-3, "which side of the pillar is all one");
        var plain = new Level(Grid, level.Scene).Find(start, behind);
        told.Result.Expanded.Should().BeInRange(plain.Result.Expanded * 4 / 5, plain.Result.Expanded * 6 / 5);
        blind.Result.Expanded.Should().BeGreaterThan(5 * told.Result.Expanded);
        told.Result.Cost.Should().BeApproximately(3 * plain.Result.Cost, 1e-4f);

        // One cell with no cost, anywhere, and the least is 0: every route is as if untold.
        var one = new Level(Grid, level.Scene, cost: (shape, cost) => {
            Array.Fill(cost, (byte)32);
            cost[shape.IndexOf(14, 14, 14)] = 0;
        });
        one.Least.Should().Be(0);
        one.Find(from, to).Result.Expanded.Should().Be(untold.Result.Expanded);
        // A cell with no region does not count: here the one that costs nothing is inside the pillar.
        var inside = new Level(Grid, level.Scene, cost: (shape, cost) => {
            Array.Fill(cost, (byte)32);
            cost[shape.IndexOf(8, 8, 4)] = 0;
        });
        inside.Region[Grid.IndexOf(8, 8, 4)].Should().Be(0);
        inside.Least.Should().Be(32);
        inside.Find(from, to).Direct.Should().BeTrue();
    }

    [Fact]
    public void AStraightLegThatIsSureToBeCheapStillPassesThroughNoCostlierCell() {
        // Both ends are a quarter of a metre to one side of their cells' centres. A way goes by
        // those centres, so the straight leg between the ends is half a metre shorter than any
        // way. On it lies one cell that costs half as much again: half a metre of it adds a
        // quarter, so the leg still costs less than any way. It is not taken all the same: the
        // way goes round that cell, and no leg passes through a cell costlier than its way's.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => cost[shape.IndexOf(8, 5, 4)] = 8);
        P from = (1.25f, 2.49f, 2.25f), to = (6.25f, 2.52f, 2.25f);
        level.CostAt(from).Should().Be(0);
        level.CostAt(to).Should().Be(0);
        level.Highest(from, to).Should().Be(8);
        var way = level.Find(from, to, straighten: false);
        level.Flown(from, to).Should().BeLessThan(level.Flown(from, way.Points) - 0.1);
        foreach (var point in way.Points) { level.CostAt(point).Should().Be(0); }
        var route = level.Find(from, to);
        route.Direct.Should().BeFalse();
        var at = from;
        foreach (var point in route.Points) {
            level.Highest(at, point).Should().Be(0, $"on the leg from {at} to {point}");
            at = point;
        }
        // With no cost on that cell the leg is taken at once.
        new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => cost[shape.IndexOf(0, 0, 0)] = 8).Find(from, to).Direct.Should().BeTrue();
    }

    [Fact]
    public void WhatAWayMustCostIsReckonedBetweenTheCentresOfTheTwoEndsCells() {
        // Wall distances written by hand: the two rows of cells at each end of the grid in x are
        // not open, and each end lies a metre beyond the nearest open centre, in line with it.
        // The only shape is far below, so every leg is clear. The cells the two ends belong to
        // cost twice, and so does one cell half way along the straight line between them.
        var wall = new byte[Grid.CellCount];
        for (var i = 0; i < wall.Length; i++) { wall[i] = i % 16 is >= 2 and <= 13 ? Cap : (byte)0; }
        var level = new Level(Grid, new ShapeScene([Solid.Box(new Box(-1, -1, -9, 9, 9, -8))]), wall: wall, cost: (shape, cost) => {
            cost[shape.IndexOf(2, 4, 4)] = 16;
            cost[shape.IndexOf(8, 4, 4)] = 16;
            cost[shape.IndexOf(13, 4, 4)] = 16;
        });
        P from = (0.25f, 2.25f, 2.25f), to = (7.75f, 2.25f, 2.25f);
        var way = level.Find(from, to, straighten: false);
        way.Points[0].Should().Be(level.Centre(2, 4, 4));
        way.Points[^2].Should().Be(level.Centre(13, 4, 4));
        way.Points.Should().NotContain(level.Centre(8, 4, 4), "going round that cell costs less than crossing it");
        // The straight leg is 7.5 m long and costs 9 as flown; the way costs a little less. A way
        // must cost 8 at the least: a metre and a quarter at each end, and 5.5 m between the two
        // centres. Reckoned between the two ends it would be 10, and the leg would be taken.
        level.Flown(from, to).Should().BeApproximately(9, 1e-6);
        level.Flown(from, way.Points).Should().BeInRange(8.8, 8.99);
        var route = level.Find(from, to);
        route.Direct.Should().BeFalse();
        route.Result.Count.Should().BeGreaterThan(1);
        route.Ends.Should().Be(RouteEnds.None);
        level.Flown(from, route.Points).Should().BeLessThanOrEqualTo(level.Flown(from, way.Points) * (1 + 2e-6));
    }

    [Fact]
    public void WithALeewayTheWayCostsNoMoreThanThatMuchOverTheCheapest() {
        // Two rooms with things in them, with no costs, with a patchwork of them, and with the
        // same patchwork where no cell costs less than 12.
        int compared = 0, dearer = 0;
        long cheapestTaken = 0, leewayTaken = 0;
        foreach (var costs in new[] { 0, 1, 2 }) {
            var level = new Level(Grid, new ShapeScene([
                Floor,
                Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)), Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)), Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
                ShapeScene.TurnedBox(6, 2.5, 3, 0.3, 1.2, 2.5, 30), Solid.Sphere(1.8f, 6, 2.5f, 0.9f), Solid.Capsule(5, 6.5f, 0.2f, 7.5f, 5.5f, 4, 0.2f),
            ]), cost: costs == 0 ? null : costs == 1 ? Patchwork : (shape, cost) => {
                Patchwork(shape, cost);
                for (var i = 0; i < cost.Length; i++) { cost[i] = Math.Max(cost[i], (byte)12); }
            });
            (int x, int y, int z)[] cells = [(1, 1, 2), (6, 14, 9), (2, 9, 12), (13, 13, 3), (14, 2, 13), (9, 8, 6), (12, 6, 1), (0, 15, 15)];
            foreach (var a in cells) {
                foreach (var b in cells) {
                    if (a == b || level.Region[Grid.IndexOf(a.x, a.y, a.z)] == 0 || level.Region[Grid.IndexOf(b.x, b.y, b.z)] == 0) { continue; }
                    P from = level.Centre(a.x, a.y, a.z), to = level.Centre(b.x, b.y, b.z);
                    var cheapest = level.Find(from, to, straighten: false);
                    cheapest.Status.Should().Be(RouteStatus.Found);
                    foreach (var leeway in new[] { 0.05f, 0.25f, 1f }) {
                        var way = level.Find(from, to, straighten: false, leeway: leeway);
                        way.Status.Should().Be(RouteStatus.Found);
                        ((double)way.Result.Cost).Should().BeInRange(cheapest.Result.Cost * (1 - 1e-5), cheapest.Result.Cost * (1.0 + leeway) * (1 + 1e-5), $"from {a} to {b} with a leeway of {leeway}");
                        level.ShouldBeSound(way, from, to);
                        // The legs are held to the way that was found, whatever the leeway.
                        var route = level.Find(from, to, leeway: leeway);
                        level.ShouldBeSound(route, from, to);
                        if (!route.Direct) {
                            route.Result.Cost.Should().Be(way.Result.Cost);
                            level.Flown(from, route.Points).Should().BeLessThanOrEqualTo(level.Flown(from, way.Points) * (1 + 2e-6));
                        }
                        compared++;
                        if (way.Result.Cost > cheapest.Result.Cost * 1.0001f) { dearer++; }
                        cheapestTaken += cheapest.Result.Expanded;
                        leewayTaken += way.Result.Expanded;
                    }
                }
            }
        }
        compared.Should().BeGreaterThan(250);
        dearer.Should().BeGreaterThan(20, "a leeway is used: some ways cost more than the cheapest");
        leewayTaken.Should().BeLessThan(cheapestTaken * 9 / 10);
    }

    [Fact]
    public void ALeewayTakesUpFarFewerCells() {
        // A room with a pillar to go round, and no costs. With a leeway the search heads for the
        // goal sooner, and here the way it finds costs the same.
        var scene = new ShapeScene([Floor, Solid.Box(new Box(1.8f, 3.6f, 0, 2.6f, 4.4f, 9))]);
        var plain = new Level(Grid, scene);
        P from = (0.75f, 0.75f, 1.25f), to = (3.75f, 7.25f, 6.25f);
        var cheapest = plain.Find(from, to);
        var quick = plain.Find(from, to, leeway: 0.25f);
        cheapest.Result.Expanded.Should().BeGreaterThan(100);
        quick.Result.Expanded.Should().BeLessThan(cheapest.Result.Expanded / 4);
        ((double)quick.Result.Cost).Should().BeInRange(cheapest.Result.Cost, cheapest.Result.Cost * 1.25 * (1 + 1e-5));
        plain.ShouldBeSound(quick, from, to);
        // No leeway asked for is none: the cheapest way, by as many cells as before.
        plain.Find(from, to, leeway: 0f).Result.Should().Be(cheapest.Result);

        // The same room where the lower half in x costs three times. What is left to go is
        // reckoned at the least cost of any cell, which is the normal one: a third of what a
        // metre costs in that half. A leeway of a quarter makes up little of that, and one of
        // twice as much again makes up all of it, at a third more than the cheapest here.
        var half = new Level(Grid, scene, cost: (shape, cost) => Area(shape, cost, 0, 8, 0, 15, 32));
        half.Least.Should().Be(0);
        var slow = half.Find(from, to);
        slow.Result.Expanded.Should().BeGreaterThan(10 * cheapest.Result.Expanded);
        var some = half.Find(from, to, leeway: 0.25f);
        some.Result.Expanded.Should().BeInRange(slow.Result.Expanded / 2, slow.Result.Expanded);
        ((double)some.Result.Cost).Should().BeInRange(slow.Result.Cost, slow.Result.Cost * 1.25 * (1 + 1e-5));
        var much = half.Find(from, to, leeway: 2f);
        much.Result.Expanded.Should().BeLessThan(slow.Result.Expanded / 8);
        ((double)much.Result.Cost).Should().BeInRange(slow.Result.Cost, slow.Result.Cost * 3 * (1 + 1e-5));
        half.ShouldBeSound(much, from, to);
    }

    [Fact]
    public void TheStraightLegIsTakenAtOnceOnTheLeewaysTerms() {
        // An area that costs three times, and a goal one cell inside it. The straight leg from
        // the start is 2.5 m long, 0.75 m of it in the area: 4 as flown, where no way can cost
        // less than 2.5. It is the cheapest way all the same, which only a search can say.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => Area(shape, cost, 6, 9, 4, 11, 32));
        P from = level.Centre(2, 8, 4), to = level.Centre(7, 8, 4);
        level.Flown(from, to).Should().BeApproximately(4, 1e-9);
        var searched = level.Find(from, to, leeway: 0.5f);
        searched.Points.Should().Equal(to);
        searched.Result.Expanded.Should().BeGreaterThan(0, "4 is more than half as much again as 2.5");
        level.Find(from, to, leeway: 0.7f).Direct.Should().BeTrue("4 is within seven tenths over 2.5");
        level.Find(from, to).Result.Expanded.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ALeewayThatIsNotANumberOrIsBelowNothingIsRefused() {
        var level = new Level(Grid, new ShapeScene([Floor]));
        P from = level.Centre(2, 8, 4), to = level.Centre(7, 8, 4);
        foreach (var leeway in new[] { -0.1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity }) {
            level.Find(from, to, leeway: leeway).Status.Should().Be(RouteStatus.Refused);
        }
        level.Find(from, to, leeway: 0f).Status.Should().Be(RouteStatus.Found);
        level.Find(from, to, leeway: 1000f).Status.Should().Be(RouteStatus.Found);
    }

    [Fact]
    public void TheLeastCostIsOfTheCellsWithARegion() {
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => {
            Array.Fill(cost, (byte)40);
            Area(shape, cost, 3, 5, 3, 5, 24);
        });
        level.Least.Should().Be(24);
        Regions.LeastCost(Grid, level.Region, []).Should().Be(0, "no costs are kept");
        Regions.LeastCost(Grid, new byte[Grid.CellCount], level.Cost).Should().Be(0, "no cell has a region");
        var tooShort = new byte[Grid.CellCount - 1];
        Array.Fill(tooShort, (byte)40);
        Regions.LeastCost(Grid, level.Region, tooShort).Should().Be(0, "an array too short");
        Regions.LeastCost(Grid, level.Region.AsSpan(0, Grid.CellCount - 1), level.Cost).Should().Be(0);
        Regions.LeastCost(default, level.Region, level.Cost).Should().Be(0);
        // A cost that closes a cell is a cost like any other here: every cell with a region closed since.
        var shut = new byte[Grid.CellCount];
        Array.Fill(shut, CellCost.Closed);
        Regions.LeastCost(Grid, level.Region, shut).Should().Be(CellCost.Closed);
    }

    [Fact]
    public void ARouteThatIsFoundHasAtLeastOnePoint() {
        // A start that is the goal, at the centre of an open cell beside a closed one. A leg of no
        // length is a leg, and beside a closed cell none is proven. The route is that centre,
        // which is the goal, with its first leg said to be unproven.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => cost[shape.IndexOf(8, 8, 4)] = CellCost.Closed);
        P centre = level.Centre(7, 8, 4);
        foreach (var straighten in new[] { true, false }) {
            var route = level.Find(centre, centre, straighten: straighten);
            route.Status.Should().Be(RouteStatus.Found);
            route.Points.Should().Equal(centre);
            route.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        }
        // Away from the closed cell the same is one point with nothing to say.
        P open = level.Centre(3, 8, 4);
        var plain = level.Find(open, open);
        plain.Points.Should().Equal(open);
        plain.Ends.Should().Be(RouteEnds.None);

        // A start at a cell's centre, and a goal in the same cell that is nearer the floor than
        // the radius. The route stops short at that centre, which is where it starts: it is the
        // route's one point.
        P low = level.Centre(6, 8, 1), under = (low.X + 0.1f, low.Y - 0.05f, low.Z - 0.15f);
        level.Room(low, low).Should().BeGreaterThan(Radius + 0.003);
        level.Room(under, under).Should().BeLessThan(Radius);
        foreach (var shapes in new[] { true, false }) {
            var route = level.Find(low, under, shapes);
            route.Status.Should().Be(RouteStatus.Found);
            route.Points.Should().Equal(low);
            route.Ends.Should().HaveFlag(RouteEnds.StopsShortOfGoal);
            level.Find(low, under, shapes, straighten: false).Points.Should().Equal(low);
        }
    }

    [Fact]
    public void ALegToAPointOnTheGridsFaceIsWalkedThroughTheCellsOfTheGrid() {
        // The goal lies on the grid's upper face in x, where no cell is. The cells of the next row
        // up in y at the grid's lower face cost more: they are where a cell beyond the last would
        // be looked for, if one were.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => Area(shape, cost, 0, 0, 9, 9, 32));
        P from = level.Centre(12, 8, 4), onFace = (8f, 4.25f, 2.25f);
        level.Find(from, onFace).Direct.Should().BeTrue();
        level.Find(onFace, from).Direct.Should().BeTrue();
        // Along the face itself, and from one face to another.
        level.Find(onFace, (8f, 6.75f, 2.25f)).Direct.Should().BeTrue();
        level.Find((8f, 8f, 8f), (8f, 4.25f, 2.25f)).Direct.Should().BeTrue();
    }

    [Fact]
    public void NoLegPassesThroughACellThatCostsMoreThanTheWayItReplaces() {
        // A room with things in it and areas of four costs: a band from side to side, and three blocks.
        var level = new Level(Grid, new ShapeScene([
            Floor, ShapeScene.TurnedBox(6, 2.5, 3, 0.3, 1.2, 2.5, 30), Solid.Sphere(1.8f, 6, 2.5f, 0.9f), Solid.Capsule(5, 6.5f, 0.2f, 7.5f, 5.5f, 4, 0.2f),
        ]), cost: (shape, cost) => {
            Area(shape, cost, 5, 6, 0, 15, 16);
            Area(shape, cost, 9, 11, 6, 12, 48);
            Area(shape, cost, 1, 3, 9, 13, 32);
            Area(shape, cost, 12, 14, 1, 3, 8);
        });
        (int x, int y, int z)[] cells = [(1, 1, 2), (6, 14, 9), (2, 9, 12), (13, 13, 3), (14, 2, 13), (9, 8, 6), (12, 6, 1), (0, 15, 15), (8, 3, 5), (4, 12, 3), (15, 9, 8), (7, 0, 10)];
        int legs = 0, through = 0, routes = 0;
        foreach (var a in cells) {
            foreach (var b in cells) {
                if (a == b || level.Region[Grid.IndexOf(a.x, a.y, a.z)] == 0 || level.Region[Grid.IndexOf(b.x, b.y, b.z)] == 0) { continue; }
                var (some, costly) = ShouldNotRaiseCost(level, level.Centre(a.x, a.y, a.z), level.Centre(b.x, b.y, b.z));
                legs += some;
                through += costly;
                routes++;
            }
        }
        routes.Should().BeGreaterThan(80);
        legs.Should().BeGreaterThan(routes);
        through.Should().BeGreaterThan(40);
    }

    [Fact]
    public void ADiagonalPastAPostIsNotTaken() {
        // A thin post up through the corner that four cells share, for a body small enough that
        // all four are open: 0.354 m from the post, with a radius of 0.33. Across the corner the
        // body would pass through the post.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Capsule(4, 4, 0, 4, 4, 9, 0.001f)]), radius: 0.33f);
        P from = level.Centre(7, 7, 4), to = level.Centre(8, 8, 4);
        level.Region[level.Shape.IndexOf(7, 7, 4)].Should().NotBe(0);
        level.Region[level.Shape.IndexOf(8, 8, 4)].Should().NotBe(0);
        foreach (var straighten in new[] { false, true }) {
            var route = level.Find(from, to, straighten: straighten);
            route.Result.Count.Should().Be(straighten ? 2 : 3, "round by a cell that shares a face with both");
            route.Result.Cost.Should().Be(1f);
            level.ShouldBeSound(route, from, to);
        }
        // Across a corner with nothing in it the diagonal is taken.
        var free = level.Find(level.Centre(11, 11, 4), level.Centre(12, 12, 4), straighten: false);
        free.Result.Count.Should().Be(2);
        free.Result.Cost.Should().BeApproximately(0.70710678f, 1e-6f);
        // And across a cube.
        var cube = level.Find(level.Centre(11, 11, 4), level.Centre(12, 12, 5), straighten: false);
        cube.Result.Count.Should().Be(2);
        cube.Result.Cost.Should().BeApproximately(0.8660254f, 1e-6f);
        // From a cell this near a shape no diagonal is taken at all, not even one that leads away
        // from the post: three steps to the cell across the cube.
        var past = level.Find(level.Centre(7, 7, 4), level.Centre(8, 8, 5), straighten: false);
        past.Result.Count.Should().Be(4);
        past.Result.Cost.Should().Be(1.5f);
        level.ShouldBeSound(past, level.Centre(7, 7, 4), level.Centre(8, 8, 5));
    }

    [Fact]
    public void ADiagonalNeedsMoreRoomAtBothEndsThanAStep() {
        // Wall distances written by hand, the same in every cell. For a radius of 0.51 a cell is
        // open from 66 counts, a step across a square needs 73 at both ends and one across a cube 80.
        P from = (1.25f, 1.25f, 1.25f), square = (1.75f, 1.75f, 1.25f), cube = (1.75f, 1.75f, 1.75f);
        float Cost(byte count, P to, int steps, Action<byte[]>? change = null) {
            var wall = new byte[Grid.CellCount];
            Array.Fill(wall, count);
            change?.Invoke(wall);
            var level = new Level(Grid, new ShapeScene([]), wall: wall);
            (level.Size.Cell, level.Size.Plane, level.Size.Space).Should().Be(((byte)66, (byte)73, (byte)80));
            var route = level.Find(from, to, shapes: false, straighten: false);
            route.Status.Should().Be(RouteStatus.Found);
            route.Result.Count.Should().Be(steps + 1);
            return route.Result.Cost;
        }
        Cost(72, square, 2).Should().Be(1f, "no diagonal");
        Cost(73, square, 1).Should().BeApproximately(0.70710678f, 1e-6f);
        Cost(79, cube, 2).Should().BeApproximately(1.2071068f, 1e-6f, "across a square and then a step");
        Cost(80, cube, 1).Should().BeApproximately(0.8660254f, 1e-6f);
        // It is needed at both ends: one cell short at either end, and the diagonal is not taken.
        Cost(73, square, 2, wall => wall[Grid.IndexOf(2, 2, 2)] = 72).Should().Be(1f);
        Cost(73, square, 2, wall => wall[Grid.IndexOf(3, 3, 2)] = 72).Should().Be(1f);
        Cost(80, cube, 2, wall => wall[Grid.IndexOf(2, 2, 2)] = 79).Should().BeApproximately(1.2071068f, 1e-6f);
        Cost(80, cube, 2, wall => wall[Grid.IndexOf(3, 3, 3)] = 79).Should().BeApproximately(1.2071068f, 1e-6f);
    }

    [Fact]
    public void ARegionArrayIsTakenAtItsWord() {
        // An empty room whose region array says the cells from x = 6 to 9 are of another region,
        // right across it. Regions flooded by the library never put two regions side by side, but
        // the search reads what it is given: it stays in the start's region.
        var level = new Level(Grid, new ShapeScene([Floor]));
        P from = level.Centre(2, 8, 4), to = level.Centre(13, 8, 4);
        level.Find(from, to, straighten: false).Result.Cost.Should().Be(5.5f);
        for (var z = 0; z < 16; z++) {
            for (var y = 0; y < 16; y++) {
                for (var x = 6; x < 10; x++) {
                    if (level.Region[Grid.IndexOf(x, y, z)] != 0) { level.Region[Grid.IndexOf(x, y, z)] = 2; }
                }
            }
        }
        var cut = level.Find(from, to, straighten: false);
        cut.Status.Should().Be(RouteStatus.Unreachable);
        cut.Result.Expanded.Should().BeGreaterThan(100, "the ends are of one region, so it is searched for");
        // A straight leg does not read regions: with the ends in one region and nothing in the way, it is taken.
        level.Find(from, to).Direct.Should().BeTrue();
        level.Find(from, level.Centre(7, 8, 4)).Status.Should().Be(RouteStatus.Unreachable);
        level.Find(from, level.Centre(7, 8, 4)).Result.Expanded.Should().Be(0);
        level.Find(level.Centre(6, 8, 4), level.Centre(9, 2, 9), straighten: false).Result.Cost.Should().BeGreaterThan(3f);
    }

    [Fact]
    public void RoundTwoWallsTheWayIsPulledAsStraightAsItGoes() {
        // Two walls across most of the room, one from each side: the way is an S. Each turn is
        // taken to the farthest cell of the way that a straight leg is clear to.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(2.5f, -1, 0, 2.9f, 5.5f, 9)), Solid.Box(new Box(5.1f, 2.5f, 0, 5.5f, 9, 9))]));
        P from = (1.25f, 1.25f, 2.25f), to = (6.75f, 6.75f, 2.25f);
        var cells = level.Find(from, to, straighten: false);
        cells.Result.Count.Should().BeGreaterThan(20);
        foreach (var shapes in new[] { true, false }) {
            var route = level.Find(from, to, shapes);
            route.Ends.Should().Be(RouteEnds.None);
            route.Result.Count.Should().BeInRange(3, 6, "a few legs, where the cells' way has more than twenty");
            route.Result.Cost.Should().Be(cells.Result.Cost);
            level.ShouldBeSound(route, from, to);
        }
    }

    // A room with things in it at angles, and a sheet of two triangles.
    private static ShapeScene Cluttered() => new([
        Floor,
        ShapeScene.TurnedBox(4, 4, 2, 0.4, 1.5, 2, 25), ShapeScene.TurnedBox(6.2, 1.8, 3, 0.3, 0.3, 3, 40, 20),
        Solid.Sphere(2, 6, 3, 0.8f), Solid.Capsule(1, 1.5f, 0.2f, 2.5f, 2.5f, 5, 0.15f), Solid.Capsule(5.5f, 6.5f, 0.2f, 5.5f, 6.5f, 8, 0.02f),
    ], [6.5f, 4, 0.2f, 7.5f, 6, 0.2f, 6.5f, 4, 5, 7.5f, 6, 5], [0, 1, 2, 1, 3, 2]);

    [Fact]
    public void AStraightLegIsTakenOnlyWhereTheRadiusIsKeptAllAlongIt() {
        var level = new Level(Grid, Cluttered());
        // Ends in open cells, a little off their centres.
        var ends = new List<P>();
        for (var z = 1; z < 16; z += 3) {
            for (var y = 0; y < 16; y += 3) {
                for (var x = 0; x < 16; x += 3) {
                    if (level.Region[level.Shape.IndexOf(x, y, z)] == 0) { continue; }
                    var centre = level.Centre(x, y, z);
                    ends.Add((centre.X + 0.1f, centre.Y - 0.15f, centre.Z + 0.05f));
                }
            }
        }
        int taken = 0, refused = 0, onlyWithShapes = 0, pairs = 0;
        for (var i = 0; i < ends.Count; i++) {
            for (var j = i + 1; j < ends.Count; j += 2) {
                P from = ends[i], to = ends[j];
                var room = level.Room(from, to);
                var with = level.Find(from, to).Direct;
                var without = level.Find(from, to, shapes: false).Direct;
                // Never taken where the body would come nearer than its radius.
                if (with || without) { room.Should().BeGreaterThanOrEqualTo(Radius - 1e-4, $"from {from} to {to}"); }
                // Always taken, given the shapes, where it clears the radius by 3 mm.
                if (room >= Radius + 0.003) { with.Should().BeTrue($"from {from} to {to}, which clears {room}"); }
                // The map alone never takes what the shapes refuse.
                if (without) { with.Should().BeTrue(); }
                taken += with ? 1 : 0;
                refused += with ? 0 : 1;
                onlyWithShapes += with && !without ? 1 : 0;
                pairs++;
            }
        }
        pairs.Should().BeGreaterThan(1000);
        taken.Should().BeGreaterThan(200);
        refused.Should().BeGreaterThan(200);
        onlyWithShapes.Should().BeGreaterThan(20, "legs that pass near a shape are proven by the shapes and not by the map");
    }

    [Fact]
    public void InOpenSpaceTheMapProvesALegAndNoShapeIsRead() {
        // The same room, with shapes that cannot be read: any question put to them is refused.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(3.8f, 3.8f, 0, 4.2f, 4.2f, 9))]));
        var scene = level.Scene;
        var broken = new BoxNode[scene.NodeCount];
        for (var i = 0; i < broken.Length; i++) { broken[i] = new BoxNode { MinX = -1000, MinY = -1000, MinZ = -1000, MaxX = 1000, MaxY = 1000, MaxZ = 1000, First = 0, Count = 0 }; }
        var points = new float[30];
        RouteResult Find(P from, P to) => Routes.Find(level.Map, level.Size, new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes, broken, scene.Order),
            level.Region, level.Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true, level.Sums, level.Links, level.Heap, points);
        Shapes.Distance(new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes, broken, scene.Order), 1, 1, 1, 1, out _).Should().BeFalse();
        // Far from the pillar and the floor: one leg.
        var open = Find((1.25f, 1.25f, 2.25f), (1.75f, 6.75f, 5.25f));
        open.Status.Should().Be(RouteStatus.Found);
        open.Count.Should().Be(1);
        open.Expanded.Should().Be(0);
        // Past the pillar at 0.55 m, where the map cannot say: the shapes are asked, refuse, and the leg is not taken.
        level.Find((1.25f, 3.25f, 2.25f), (6.75f, 3.25f, 2.25f)).Direct.Should().BeTrue("with shapes that can be read it is");
        level.Find((1.25f, 3.25f, 2.25f), (6.75f, 3.25f, 2.25f), shapes: false).Direct.Should().BeFalse("the map alone does not prove it");
        var near = Find((1.25f, 3.25f, 2.25f), (6.75f, 3.25f, 2.25f));
        near.Status.Should().Be(RouteStatus.Found);
        near.Expanded.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DownANarrowCorridorTheShapesProveWhatTheMapCannot() {
        // A corridor 1.1 m wide along x between two rooms, its middle at y = 4.27: the row of
        // centres at y = 4.25 is 0.53 m from one wall and 0.57 from the other, so it is open and
        // joins the rooms, and no other row is.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(3, -1, 0, 5, 3.72f, 9)), Solid.Box(new Box(3, 4.82f, 0, 5, 9, 9))]));
        P from = (1.75f, 4.27f, 1.25f), to = (6.25f, 4.27f, 1.25f);
        level.Region[level.Shape.IndexOf(8, 8, 2)].Should().NotBe(0);
        level.Region[level.Shape.IndexOf(8, 7, 2)].Should().Be(0);
        level.Region[level.Shape.IndexOf(8, 9, 2)].Should().Be(0);
        level.Room(from, to).Should().BeApproximately(0.55, 1e-3);
        var with = level.Find(from, to);
        with.Direct.Should().BeTrue("a ball of 0.51 m clears both walls by 0.04 m");
        var without = level.Find(from, to, shapes: false);
        without.Direct.Should().BeFalse("the map proves no more than 0.53 m at the centres and less between them");
        without.Result.Count.Should().BeGreaterThan(4, "through the corridor it goes cell by cell");
        level.ShouldBeSound(without, from, to);
    }

    [Fact]
    public void AThinPostOrASheetAcrossALegIsNotPassed() {
        // A post 2 cm thick, and a sheet of two triangles with no thickness, each across the
        // straight line between the ends, and neither as wide as the room.
        var post = new Level(Grid, new ShapeScene([Floor, Solid.Capsule(4.06f, 4, 0, 4.06f, 4, 9, 0.01f)]));
        var sheet = new Level(Grid, new ShapeScene([Floor], [4.06f, 2, 0.2f, 4.06f, 6, 0.2f, 4.06f, 2, 5, 4.06f, 6, 5], [0, 1, 2, 1, 3, 2]));
        P from = (1.25f, 4, 1.25f), to = (6.75f, 4, 1.25f);
        foreach (var level in new[] { post, sheet }) {
            foreach (var shapes in new[] { true, false }) {
                var route = level.Find(from, to, shapes);
                route.Direct.Should().BeFalse();
                route.Result.Count.Should().BeGreaterThan(1);
                level.ShouldBeSound(route, from, to);
            }
        }
        // Past the post to one side: 6 mm clear of the radius the leg is taken, and 4 mm inside it,
        // where the body would touch the post for 13 cm of the way, it is not.
        post.Find((1.25f, 4.526f, 1.25f), (6.75f, 4.526f, 1.25f)).Direct.Should().BeTrue();
        post.Find((1.25f, 4.516f, 1.25f), (6.75f, 4.516f, 1.25f)).Direct.Should().BeFalse();
        post.Find((1.25f, 3.474f, 2.25f), (6.75f, 3.474f, 2.75f)).Direct.Should().BeTrue();
        post.Find((1.25f, 3.484f, 2.25f), (6.75f, 3.484f, 2.75f)).Direct.Should().BeFalse();
    }

    [Fact]
    public void ACellClosedSinceTheRegionsWereMadeIsNotEntered() {
        // The regions say both rooms are one. Then the doorway's cells are closed by cost, and the
        // regions are not made again: the search still reads the costs, and finds no way through.
        var level = new Level(Grid, TwoRooms(), cost: (_, _) => { });
        P from = (1.25f, 4.1f, 1.25f), to = (6.75f, 4.1f, 1.25f);
        level.Find(from, to).Direct.Should().BeTrue();
        CellCost.Fill(Grid, Solid.Box(new Box(3.5f, 2, 0, 4.5f, 6, 9)), CellCost.Closed, level.Cost).Should().BeTrue();
        var shut = level.Find(from, to);
        shut.Status.Should().Be(RouteStatus.Unreachable);
        shut.Result.Expanded.Should().BeGreaterThan(100, "it was searched for, and every cell it took up let go");
        // A cell closed beside the way: the way goes round it, and no step cuts its corner.
        var beside = new Level(Grid, new ShapeScene([Floor]), cost: (_, _) => { });
        P a = beside.Centre(4, 4, 4), b = beside.Centre(9, 9, 4);
        beside.Find(a, b, straighten: false).Result.Cost.Should().BeApproximately(5 * 0.70710678f, 1e-5f);
        beside.Cost[Grid.IndexOf(6, 7, 4)] = CellCost.Closed;
        beside.Cost[Grid.IndexOf(7, 6, 4)] = CellCost.Closed;
        var round = beside.Find(a, b, straighten: false);
        round.Result.Cost.Should().BeGreaterThan(5 * 0.70710678f + 0.01f, "the diagonal between the two closed cells is not taken");
        beside.ShouldBeSound(round, a, b);
    }

    // Two rooms with a wall between them from x = 3.8 to 4.2, and two openings in it from floor
    // to top: one from y = 1 to 3 and one from y = 5 to 7.
    private static ShapeScene TwoOpenings() => new([
        Floor, Solid.Box(new Box(3.8f, -1, 0, 4.2f, 1, 9)), Solid.Box(new Box(3.8f, 3, 0, 4.2f, 5, 9)), Solid.Box(new Box(3.8f, 7, 0, 4.2f, 9, 9)),
    ]);

    [Fact]
    public void AShutDoorIsNotCrossedThoughNothingStandsInIt() {
        // The first opening is shut by cost alone: no shape is in it.
        var level = new Level(Grid, TwoOpenings(), cost: (shape, cost) => CellCost.Fill(shape, Solid.Box(new Box(3.5f, 1, 0, 4.5f, 3, 9)), CellCost.Closed, cost).Should().BeTrue());
        var open = new Level(Grid, TwoOpenings());
        P from = (2.25f, 2.1f, 1.25f), to = (5.75f, 2.1f, 1.25f);
        open.Find(from, to).Direct.Should().BeTrue("with the door open the straight leg is clear");
        level.Room(from, to).Should().BeGreaterThan(0.8);
        level.Region[level.Shape.IndexOf(4, 4, 2)].Should().Be(level.Region[level.Shape.IndexOf(11, 4, 2)], "the rooms are still joined by the other opening");
        foreach (var shapes in new[] { true, false }) {
            var route = level.Find(from, to, shapes);
            route.Direct.Should().BeFalse();
            level.ShouldBeSound(route, from, to);
            route.Points.Should().Contain(point => point.Y > 5 && point.Y < 7, "round by the other opening");
            route.Result.Cost.Should().BeGreaterThan(8);
        }
        // With both shut the rooms are two regions.
        var both = new Level(Grid, TwoOpenings(), cost: (shape, cost) => CellCost.Fill(shape, Solid.Box(new Box(3.5f, 0, 0, 4.5f, 8, 9)), CellCost.Closed, cost).Should().BeTrue());
        var none = both.Find(from, to);
        none.Status.Should().Be(RouteStatus.Unreachable);
        none.Result.Expanded.Should().Be(0);
    }

    [Fact]
    public void ALegThatClipsTheCornerOfAClosedCellIsRefused() {
        // One closed cell in an empty room: (8, 8, 4), from 4 to 4.5 in x and y. The leg crosses
        // its corner at (4.5, 4) for 3 cm, which is less than the gap between two samples.
        var level = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => cost[shape.IndexOf(8, 8, 4)] = CellCost.Closed);
        P from = (3.25f, 2.77f, 2.25f), to = (5.75f, 5.27f, 2.25f);
        level.CrossesClosed(from, to).Should().BeTrue();
        var route = level.Find(from, to);
        route.Direct.Should().BeFalse();
        level.ShouldBeSound(route, from, to);
        // A leg that passes 0.07 m from the cell's corner without entering it is refused too, which
        // errs safe; one that passes 1.06 m from it is taken.
        P beside = (3.25f, 2.65f, 2.25f), besideTo = (5.75f, 5.15f, 2.25f);
        level.CrossesClosed(beside, besideTo).Should().BeFalse();
        level.Find(beside, besideTo).Direct.Should().BeFalse();
        level.Find((3.25f, 1.25f, 2.25f), (7.25f, 5.25f, 2.25f)).Direct.Should().BeTrue();
        // A cell that costs more and is not closed keeps a leg out of the cell alone: a leg a
        // quarter of a metre from it is taken, where the same leg by a closed cell is not.
        var costly = new Level(Grid, new ShapeScene([Floor]), cost: (shape, cost) => cost[shape.IndexOf(8, 8, 4)] = 254);
        costly.Find(from, to).Direct.Should().BeFalse();
        P near = (3.25f, 2.4f, 2.25f), nearTo = (5.75f, 4.9f, 2.25f);
        level.Find(near, nearTo).Direct.Should().BeFalse();
        costly.Find(near, nearTo).Direct.Should().BeTrue();
        costly.Find(beside, besideTo).Direct.Should().BeTrue();
    }

    [Fact]
    public void AGoalNearAWallIsReachedWithShapesAndStoppedShortOfWithout() {
        // A wall whose face is at x = 4.05. The cell from 3.5 to 4 is not open: its centre is
        // 0.3 m from the wall. A goal in it at 3.535 is 0.515 m from the wall.
        var level = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(4.05f, -1, 0, 9, 9, 9))]));
        P from = (1.25f, 1.25f, 1.25f), to = (3.535f, 5.9f, 2.6f);
        level.Region[level.Shape.IndexOf(7, 11, 5)].Should().Be(0);
        var with = level.Find(from, to);
        with.Ends.Should().Be(RouteEnds.None);
        with.Points[^1].Should().Be(to);
        level.ShouldBeSound(with, from, to);
        var without = level.Find(from, to, shapes: false);
        without.Ends.Should().Be(RouteEnds.StopsShortOfGoal);
        level.ShouldBeSound(without, from, to);
        // A goal 0.4 m from the wall is stopped short of either way, at an open centre beside it.
        P near = (3.65f, 5.9f, 2.6f);
        var shortOf = level.Find(from, near);
        shortOf.Ends.Should().Be(RouteEnds.StopsShortOfGoal);
        level.ShouldBeSound(shortOf, from, near);
        var last = shortOf.Points[^1];
        Math.Sqrt(Math.Pow(last.X - near.X, 2) + Math.Pow(last.Y - near.Y, 2) + Math.Pow(last.Z - near.Z, 2)).Should().BeLessThan(0.6);
    }

    [Fact]
    public void AStartInsideItsClearanceGoesFirstToAnOpenCentre() {
        var level = new Level(Grid, TwoRooms());
        // 0.4 m from the wall between the rooms, in an open cell.
        P from = (3.4f, 6.3f, 1.3f), to = (6.75f, 6.75f, 1.25f);
        var route = level.Find(from, to);
        route.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        route.Points[0].Should().Be(level.Centre(6, 12, 2), "the centre of the start's own cell");
        level.ShouldBeSound(route, from, to);
        // Both ends at once.
        P goal = (4.6f, 1.3f, 1.3f);
        var both = level.Find(from, goal);
        both.Ends.Should().Be(RouteEnds.FirstLegUnproven | RouteEnds.StopsShortOfGoal);
        both.Points[^1].Should().Be(level.Centre(9, 2, 2));
        level.ShouldBeSound(both, from, goal);
        // A start that is its own goal, 0.4 m from the wall: a leg of no length is still a leg, and
        // this one is not clear. The route is the cell's centre, with both ends said.
        var still = level.Find(from, from);
        still.Direct.Should().BeFalse();
        still.Ends.Should().Be(RouteEnds.FirstLegUnproven | RouteEnds.StopsShortOfGoal);
        still.Points.Should().Equal(level.Centre(6, 12, 2));
        // The same with every cell kept.
        var cells = level.Find(from, goal, straighten: false);
        cells.Ends.Should().Be(RouteEnds.FirstLegUnproven | RouteEnds.StopsShortOfGoal);
        cells.Points[0].Should().Be(level.Centre(6, 12, 2));
        cells.Points[^1].Should().Be(level.Centre(9, 2, 2));
    }

    [Fact]
    public void AnEndThatIsNotInAnOpenCellTakesTheNearestOpenCentreItCanSee() {
        // A floor whose top is at 0.26: the centres at 0.75 are 0.49 m above it and not open, so
        // the lowest open centre is 0.99 m above the floor.
        var level = new Level(Grid, new ShapeScene([Solid.Box(new Box(-1, -1, -1, 9, 9, 0.26f))]));
        level.Region[level.Shape.IndexOf(3, 3, 1)].Should().Be(0);
        level.Region[level.Shape.IndexOf(3, 3, 2)].Should().NotBe(0);
        // A goal lying on the floor.
        P from = (1.25f, 1.25f, 2.25f), onFloor = (6.6f, 6.4f, 0.26f);
        var route = level.Find(from, onFloor);
        route.Ends.Should().Be(RouteEnds.StopsShortOfGoal);
        route.Points[^1].Should().Be(level.Centre(13, 12, 2), "the open centre above the goal");
        level.ShouldBeSound(route, from, onFloor);
        // Among centres equally near, the lowest in x, then in y: a goal on the corner that four cells share.
        level.Find(from, (6.5f, 6.5f, 0.26f)).Points[^1].Should().Be(level.Centre(12, 12, 2));
        level.Find(from, (6.5f, 6.4f, 0.26f)).Points[^1].Should().Be(level.Centre(12, 12, 2));
        level.Find(from, (6.6f, 6.5f, 0.26f)).Points[^1].Should().Be(level.Centre(13, 12, 2));
        // A goal a little under the floor's top sees nothing, and one deep in the floor neither.
        level.Find(from, (6.6f, 6.4f, 0.15f)).Status.Should().Be(RouteStatus.Unreachable);
        level.Find(from, (6.6f, 6.4f, -0.5f)).Status.Should().Be(RouteStatus.Unreachable);
        // Without shapes there is nothing to see by: the nearest is taken.
        level.Find(from, (6.6f, 6.4f, 0.15f), shapes: false).Ends.Should().Be(RouteEnds.StopsShortOfGoal);

        // A body of 0.43 m resting in the corner of a floor and two walls can plan.
        var corner = new Level(Grid, new ShapeScene([Solid.Box(new Box(-1, -1, -1, 9, 9, 0.26f)), Solid.Box(new Box(-1, -1, 0, 0.3f, 9, 9)), Solid.Box(new Box(-1, -1, 0, 9, 0.3f, 9))]));
        P resting = (0.73f, 0.73f, 0.69f), far = (6.25f, 6.25f, 3.25f);
        corner.Region[corner.Shape.IndexOf(1, 1, 1)].Should().Be(0);
        var plan = corner.Find(resting, far);
        plan.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        plan.Points[0].Should().Be(corner.Centre(2, 2, 2), "the nearest open centre");
        corner.ShouldBeSound(plan, resting, far);
        // An end outside the grid, within reach of an open centre.
        var outside = level.Find((-0.3f, 2.2f, 2.2f), from);
        outside.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        outside.Points[0].Should().Be(level.Centre(0, 4, 4));
        level.Find((-3f, 2.2f, 2.2f), from).Status.Should().Be(RouteStatus.Unreachable);
    }

    [Fact]
    public void AnEndBesideASheetTakesItsOwnSide() {
        // A sheet with no thickness at x = 4.2, not as wide as the room. The end is 0.1 m from it
        // at x = 4.1. The nearest open centre is on the far side, at 4.75; the nearest on its own
        // side is at 3.25.
        var level = new Level(Grid, new ShapeScene([Floor], [4.2f, 1, 0.2f, 4.2f, 7, 0.2f, 4.2f, 1, 7, 4.2f, 7, 7], [0, 1, 2, 1, 3, 2]));
        level.Region[level.Shape.IndexOf(7, 8, 6)].Should().Be(0);
        level.Region[level.Shape.IndexOf(8, 8, 6)].Should().Be(0);
        level.Region[level.Shape.IndexOf(9, 8, 6)].Should().NotBe(0);
        level.Region[level.Shape.IndexOf(6, 8, 6)].Should().NotBe(0);
        P from = (4.1f, 4.25f, 3.25f), to = (1.25f, 1.25f, 3.25f);
        var route = level.Find(from, to);
        route.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        route.Points[0].Should().Be(level.Centre(6, 8, 6));
        level.ShouldBeSound(route, from, to);
        // Without shapes the nearest is taken, which is through the sheet: the flag says the leg is not proven.
        var blind = level.Find(from, to, shapes: false);
        blind.Ends.Should().Be(RouteEnds.FirstLegUnproven);
        blind.Points[0].Should().Be(level.Centre(9, 8, 6));

        // With the sheet right across the room its two sides are two regions.
        var across = new Level(Grid, new ShapeScene([Floor], [4.2f, -1, 0.2f, 4.2f, 9, 0.2f, 4.2f, -1, 9, 4.2f, 9, 9], [0, 1, 2, 1, 3, 2]));
        across.Find(from, to).Status.Should().Be(RouteStatus.Found);
        across.Find(from, (6.75f, 1.25f, 3.25f)).Status.Should().Be(RouteStatus.Unreachable);
    }

    [Fact]
    public void EndsInsideAClosedMeshOrInTwoRegionsAreUnreachable() {
        // A closed box of twelve triangles from 3 to 5 each way, in a room that is seeded outside it.
        float[] corners = [3, 3, 3, 5, 3, 3, 3, 5, 3, 5, 5, 3, 3, 3, 5, 5, 3, 5, 3, 5, 5, 5, 5, 5];
        int[] triangles = [0, 1, 2, 1, 3, 2, 4, 6, 5, 5, 6, 7, 0, 4, 1, 1, 4, 5, 2, 3, 6, 3, 7, 6, 0, 2, 4, 2, 6, 4, 1, 5, 3, 3, 5, 7];
        var mesh = new Level(Grid, new ShapeScene([Floor], corners, triangles), seeds: [Solid.Sphere(1, 1, 1, 0.5f)]);
        mesh.Region[mesh.Shape.IndexOf(8, 8, 8)].Should().Be(0, "open by its distance, and reached by no seed");
        mesh.Region[mesh.Shape.IndexOf(8, 8, 12)].Should().NotBe(0);
        var inside = mesh.Find((3.8f, 4, 4), (4.2f, 4, 4));
        inside.Status.Should().Be(RouteStatus.Unreachable);
        inside.Result.Expanded.Should().Be(0);
        inside.Result.Count.Should().Be(0);
        mesh.Find((1.25f, 1.25f, 1.25f), (4.2f, 4, 4)).Status.Should().Be(RouteStatus.Unreachable);
        // Without the shapes the ends take the nearest open centres, which are outside it.
        mesh.Find((3.8f, 4, 4), (4.2f, 4, 4), shapes: false).Status.Should().Be(RouteStatus.Found);

        // Two rooms seeded apart, with a gap of 1.1 m between them that holds no open row of
        // cells: its middle is at y = 4.5, between two rows. A ball of 0.51 m passes through, and
        // the regions still say there is no way, wherever the ends sit.
        var gap = new Level(Grid, new ShapeScene([Floor, Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3.95f, 9)), Solid.Box(new Box(3.8f, 5.05f, 0, 4.2f, 9, 9))]),
            seeds: [Solid.Sphere(1.25f, 1.25f, 1.25f, 0.1f), Solid.Sphere(6.75f, 6.75f, 1.25f, 0.1f)]);
        byte left = gap.Region[gap.Shape.IndexOf(6, 9, 2)], right = gap.Region[gap.Shape.IndexOf(9, 9, 2)];
        left.Should().NotBe(0);
        right.Should().NotBe(0).And.NotBe(left);
        gap.Room((3.25f, 4.5f, 1.25f), (4.75f, 4.5f, 1.25f)).Should().BeGreaterThan(0.54);
        foreach (var (from, to) in new (P, P)[] { ((3.25f, 4.5f, 1.25f), (4.75f, 4.5f, 1.25f)), ((1.25f, 1.25f, 1.25f), (6.75f, 6.75f, 1.25f)), ((3.4f, 4.5f, 1.25f), (4.6f, 4.5f, 1.25f)) }) {
            var route = gap.Find(from, to);
            route.Status.Should().Be(RouteStatus.Unreachable);
            route.Result.Expanded.Should().Be(0);
        }
        // An end in the gap itself belongs to the side whose centre is nearest.
        gap.Find((3.9f, 4.5f, 1.25f), (1.25f, 1.25f, 1.25f)).Status.Should().Be(RouteStatus.Found);
        gap.Find((3.9f, 4.5f, 1.25f), (6.75f, 6.75f, 1.25f)).Status.Should().Be(RouteStatus.Unreachable);
    }

    [Fact]
    public void ABufferTooShortSaysHowManyPointsThereAre() {
        var level = new Level(Grid, TwoRooms());
        P from = (1.25f, 1.25f, 5.25f), to = (6.75f, 6.75f, 5.25f);
        var whole = level.Find(from, to);
        whole.Status.Should().Be(RouteStatus.Found);
        whole.Result.Count.Should().BeGreaterThan(2);
        for (var room = 0; room < whole.Result.Count; room++) {
            var cut = level.Find(from, to, room: room);
            cut.Status.Should().Be(RouteStatus.BufferTooShort);
            cut.Result.Count.Should().Be(whole.Result.Count);
            cut.Points.Should().Equal(whole.Points.Take(room));
            cut.Ends.Should().Be(whole.Ends);
        }
        level.Find(from, to, room: whole.Result.Count).Points.Should().Equal(whole.Points);
        // The one point of a straight leg.
        level.Find(from, (1.25f, 6.75f, 5.25f), room: 0).Status.Should().Be(RouteStatus.BufferTooShort);
    }

    [Fact]
    public void WhatCannotBeUsedIsRefused() {
        var level = new Level(Grid, TwoRooms());
        var cells = Grid.CellCount;
        var points = new float[30];
        RouteStatus Status(WallMap wall, SizeClass size, ShapeSet shapes, byte[] region, byte[] cost, P from, P to, float[] sums, int[] links, int[]? heap = null) {
            var result = Routes.Find(wall, size, shapes, region, cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true, sums, links, heap ?? level.Heap, points);
            if (result.Status == RouteStatus.Refused) {
                result.Count.Should().Be(0);
                result.Expanded.Should().Be(0);
            }
            return result.Status;
        }
        P a = (1.25f, 1.25f, 1.25f), b = (6.75f, 6.75f, 1.25f);
        var set = level.Scene.Set;
        Status(level.Map, level.Size, set, level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Found);
        Status(new WallMap(Grid, 0f, Cap, level.Wall), level.Size, set, level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "a map with no unit");
        Status(new WallMap(Grid, Unit, Cap, level.Wall.AsSpan(0, cells - 1)), level.Size, set, level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "a short map");
        Status(level.Map, default, set, level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "no class");
        SizeClass.TryMake(Radius, new WallMap(new GridShape(0, 0, 0, 0.4f, 16, 16, 16), Unit, Cap, level.Wall), out var other).Should().BeTrue();
        Status(level.Map, other, set, level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "a class made for other cells");
        Status(level.Map, level.Size, set, new byte[cells - 1], [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "a short region array");
        Status(level.Map, level.Size, set, level.Region, new byte[cells - 1], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "a cost array that is neither empty nor whole");
        Status(level.Map, level.Size, set, level.Region, [], a, b, new float[2 * cells - 1], level.Links).Should().Be(RouteStatus.Refused, "short sums");
        Status(level.Map, level.Size, set, level.Region, [], a, b, level.Sums, new int[cells - 1]).Should().Be(RouteStatus.Refused, "short links");
        Status(level.Map, level.Size, set, level.Region, [], a, b, level.Sums, level.Links, new int[cells - 1]).Should().Be(RouteStatus.Refused, "a short heap");
        var scene = level.Scene;
        Status(level.Map, level.Size, new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes.AsSpan(1), scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order),
            level.Region, [], a, b, level.Sums, level.Links).Should().Be(RouteStatus.Refused, "shapes whose spans do not belong together");
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }) {
            Status(level.Map, level.Size, set, level.Region, [], (bad, 1, 1), b, level.Sums, level.Links).Should().Be(RouteStatus.Refused);
            Status(level.Map, level.Size, set, level.Region, [], a, (6, 6, bad), level.Sums, level.Links).Should().Be(RouteStatus.Refused);
        }
        default(RouteResult).Status.Should().Be(RouteStatus.Refused);
        // The lengths asked for are what is needed, and a grid too large to search needs none.
        Routes.SumsLength(Grid).Should().Be(2 * cells);
        Routes.LinksLength(Grid).Should().Be(cells);
        Routes.HeapLength(Grid).Should().Be(cells);
        Routes.SumsLength(new GridShape(0, 0, 0, 1, 1024, 1024, 64)).Should().Be(0);
        Routes.HeapLength(new GridShape(0, 0, 0, 1, 1024, 1024, 64)).Should().Be(0);
        Routes.LinksLength(new GridShape(0, 0, 0, 1, 1024, 1024, 63)).Should().Be(1024 * 1024 * 63);
        Routes.LinksLength(default).Should().Be(0);
    }

    [Fact]
    public void LinksThatWereNeverClearedStillEndWithASoundAnswer() {
        var level = new Level(Grid, TwoRooms());
        var cells = Grid.CellCount;
        P from = (1.25f, 1.25f, 1.25f), to = (6.75f, 1.25f, 1.25f);
        var clean = level.Find(from, to);
        clean.Result.Expanded.Should().BeGreaterThan(50, "the wall is in the way, so it is searched for");
        var points = new float[600];
        var found = 0;
        Func<int, int>[] fills = [
            _ => 1, _ => 14, _ => int.MaxValue, _ => -1, _ => int.MinValue, i => i, i => i * 32 + 3, i => (i % cells + 1) * 32 + 5,
            i => unchecked(i * (int)2654435761u), i => i % 3 == 0 ? 0 : 27, i => (i % 2 + 1) << 5 | 14,
        ];
        foreach (var fill in fills) {
            // With sums that make every cell look as if it could be reached more cheaply, and with none.
            foreach (var sum in new[] { float.PositiveInfinity, 0f, float.NaN }) {
                int[] links = new int[cells], heap = new int[cells];
                for (var i = 0; i < cells; i++) {
                    links[i] = fill(i);
                    heap[i] = fill(i + cells);
                }
                var filled = new float[2 * cells];
                Array.Fill(filled, sum);
                var result = Routes.Find(level.Map, level.Size, level.Scene.Set, level.Region, level.Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true, filled, links, heap, points);
                result.Status.Should().BeOneOf(RouteStatus.Found, RouteStatus.Unreachable);
                if (result.Status != RouteStatus.Found) { continue; }
                var list = new P[result.Count];
                for (var i = 0; i < list.Length; i++) { list[i] = (points[3 * i], points[3 * i + 1], points[3 * i + 2]); }
                level.ShouldBeSound(new Planned(result, list), from, to);
                found++;
            }
        }
        found.Should().BeGreaterThan(10, "links that only look as if cells wait are seen through");
        // Links of all zero, used over and over, give the same answer each time.
        for (var i = 0; i < 3; i++) {
            var again = level.Find(from, to);
            again.Points.Should().Equal(clean.Points);
            again.Result.Should().Be(clean.Result);
        }
        // One set of arrays, longer than either grid needs, serves grids of two sizes in turn.
        var small = new Level(new GridShape(0, 0, 0, 0.5f, 16, 16, 8), TwoRooms());
        float[] sums = new float[2 * cells + 100], smallPoints = new float[600];
        int[] shared = new int[cells + 50], sharedHeap = new int[cells + 50];
        for (var i = 0; i < 3; i++) {
            foreach (var each in new[] { level, small }) {
                var result = Routes.Find(each.Map, each.Size, each.Scene.Set, each.Region, each.Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true, sums, shared, sharedHeap, smallPoints);
                result.Should().Be(each.Find(from, to).Result);
                shared.Should().OnlyContain(link => link == 0);
            }
        }
    }

    // A table for one kind of agent: what each level of danger (the high four bits) and of fog (the low four) costs it.
    private static byte[] Table(int danger, int fog, bool closeDanger = false) {
        var table = new byte[CellCost.TableLength];
        for (var b = 0; b < CellCost.TableLength; b++) {
            int d = b >> 4, f = b & 15;
            table[b] = closeDanger && d >= 8 ? CellCost.Closed : (byte)Math.Min(254, d * danger + f * fog);
        }
        table[CellCost.Closed] = CellCost.Closed;
        return table;
    }

    [Fact]
    public void ATableGivesWhatTheLayerPutThroughItGives() {
        // The layer says what cells are: danger across the middle, fog in a corner, some of both, and a wall closed for all.
        var level = new Level(Grid, Cluttered(), cost: (shape, layer) => {
            CellCost.Fill(shape, Solid.Box(new Box(3, 0, 0, 4.5f, 8, 8)), 0x80, layer).Should().BeTrue();
            CellCost.Fill(shape, Solid.Box(new Box(5, 2, 0, 7, 5, 8)), 0x0C, layer).Should().BeTrue();
            CellCost.Fill(shape, Solid.Box(new Box(0, 6, 0, 2, 8, 8)), 0x3F, layer).Should().BeTrue();
            CellCost.Fill(shape, Solid.Box(new Box(1, 3, 0, 1.6f, 4, 8)), CellCost.Closed, layer).Should().BeTrue();
        });
        var open = new List<P>();
        for (var i = 0; i < Grid.CellCount; i += 7) {
            if (level.Region[i] == 0) { continue; }
            int x = i % 16, y = i / 16 % 16, z = i / 256;
            open.Add(level.Centre(x, y, z));
        }
        open.Count.Should().BeGreaterThan(60);
        var present = new ulong[4];
        Regions.CostsIn(Grid, level.Region, level.Cost, present).Should().BeTrue();
        CellCost.Least(present, []).Should().Be(Regions.LeastCost(Grid, level.Region, level.Cost));
        var routesDiffer = 0;
        var firstPoints = new Dictionary<int, float[]>();
        // The fourth prefers fog: the clearest cells cost the most, so the least any cell costs is above 0.
        var prefersFog = Table(1, 0).Select((v, b) => b == CellCost.Closed ? v : (byte)(v + 30 - 2 * (b & 15))).ToArray();
        foreach (var table in new[] { Table(12, 0), Table(0, 6), Table(1, 1, closeDanger: true), prefersFog }) {
            var mapped = level.Cost.Select(b => table[b]).ToArray();
            Regions.LeastCost(Grid, level.Region, level.Cost, table).Should().Be(Regions.LeastCost(Grid, level.Region, mapped));
            CellCost.Least(present, table).Should().Be(Regions.LeastCost(Grid, level.Region, mapped), "from the values present, with no pass over the cells");
            float[] a = new float[600], b = new float[600];
            for (var k = 0; k + 1 < open.Count; k += 2) {
                P from = open[k], to = open[open.Count - 1 - k];
                var withTable = Routes.Find(level.Map, level.Size, level.Scene.Set, level.Region, level.Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true,
                    level.Sums, level.Links, level.Heap, a, Regions.LeastCost(Grid, level.Region, level.Cost, table), 0f, table);
                var throughIt = Routes.Find(level.Map, level.Size, level.Scene.Set, level.Region, mapped, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true,
                    level.Sums, level.Links, level.Heap, b, Regions.LeastCost(Grid, level.Region, mapped), 0f);
                (withTable.Status, withTable.Ends, withTable.Count, withTable.Expanded, withTable.Cost).Should().Be((throughIt.Status, throughIt.Ends, throughIt.Count, throughIt.Expanded, throughIt.Cost), $"from {from} to {to}");
                a.AsSpan(0, 3 * Math.Min(200, withTable.Count)).SequenceEqual(b.AsSpan(0, 3 * Math.Min(200, throughIt.Count))).Should().BeTrue();
                if (withTable.Status != RouteStatus.Found) { continue; }
                var points = a.AsSpan(0, 3 * withTable.Count).ToArray();
                if (firstPoints.TryGetValue(k, out var before)) { if (!before.SequenceEqual(points)) { routesDiffer++; } } else { firstPoints[k] = points; }
            }
            // A travel field with the table is the one over the layer put through it.
            TravelSeed[] seeds = [TravelSeed.FromPoint(Solid.Sphere(open[0].X, open[0].Y, open[0].Z, 0.8f), open[0].X, open[0].Y, open[0].Z)];
            ushort[] one = new ushort[Grid.CellCount], other = new ushort[Grid.CellCount];
            Travel.Build(Grid, level.Region, level.Cost, seeds, 0.02f, one, new float[Travel.TimesLength(Grid)], new int[Travel.HeapLength(Grid)], out _, out _, table).Should().BeTrue();
            Travel.Build(Grid, level.Region, mapped, seeds, 0.02f, other, new float[Travel.TimesLength(Grid)], new int[Travel.HeapLength(Grid)], out _, out _).Should().BeTrue();
            one.Should().Equal(other);
        }
        routesDiffer.Should().BeGreaterThan(0, "the kinds of agent go different ways over the same layer");
    }

    [Fact]
    public void TheCostsHeldAreThoseOfTheCellsWithARegion() {
        var grid = new GridShape(0, 0, 0, 0.5f, 4, 1, 1);
        var present = new ulong[] { 9, 9, 9, 9 };
        Regions.CostsIn(grid, [1, 0, 1, 0], [5, 0, 70, 200], present).Should().BeTrue();
        present.Should().Equal(1UL << 5, 1UL << (70 - 64), 0UL, 0UL);
        Regions.CostsIn(grid, [0, 0, 1, 0], [], present).Should().BeTrue();
        present.Should().Equal(1UL, 0UL, 0UL, 0UL);
        Regions.CostsIn(grid, [1, 0, 1, 0], [5, 0, 70], present).Should().BeFalse();
        present.Should().Equal(0UL, 0UL, 0UL, 0UL);
    }

    [Fact]
    public void ATableThatCannotBeUsedIsRefused() {
        var level = new Level(Grid, Cluttered(), cost: (shape, layer) => CellCost.Fill(shape, Solid.Box(new Box(3, 0, 0, 4.5f, 8, 8)), 0x80, layer).Should().BeTrue());
        var opens = CellCost.IsTable(Table(1, 1).Select((v, i) => i == CellCost.Closed ? (byte)0 : v).ToArray());
        opens.Should().BeFalse("a table may not open what the layer closes");
        CellCost.IsTable(new byte[255]).Should().BeFalse();
        CellCost.IsTable([]).Should().BeTrue();
        var bad = new byte[255];
        P from = level.Centre(1, 1, 3), to = level.Centre(14, 14, 3);
        Routes.Find(level.Map, level.Size, level.Scene.Set, level.Region, level.Cost, from.X, from.Y, from.Z, to.X, to.Y, to.Z, true,
            level.Sums, level.Links, level.Heap, new float[600], 0, 0f, bad).Status.Should().Be(RouteStatus.Refused);
        Regions.LeastCost(Grid, level.Region, level.Cost, bad).Should().Be(0);
        CellCost.Least([ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue], bad).Should().Be(0);
        CellCost.Least([1UL << 40, 0, 0], []).Should().Be(0, "too few marks to read");
        CellCost.Least([1UL << 40, 0, 0, 0], []).Should().Be(40);
        Travel.Build(Grid, level.Region, level.Cost, [TravelSeed.FromPoint(Solid.Sphere(from.X, from.Y, from.Z, 0.8f), from.X, from.Y, from.Z)], 0.02f, new ushort[Grid.CellCount],
            new float[Travel.TimesLength(Grid)], new int[Travel.HeapLength(Grid)], out _, out _, bad).Should().BeFalse();
    }

}
