using System.Diagnostics;
using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>Which two axes a slice shows: x and y at one height, or x and z at one y.</summary>
public enum SlicePlane { Xy, Xz }

/// <summary>
/// A few boxes, spheres and capsules in a grid of cubic cells, with the wall distance of every cell marked by
/// <see cref="WallDistance.MarkLayer"/>, so a view can show what the grid holds beside the exact distance
/// <see cref="Shapes.Distance"/> gives from the shapes themselves. Metres throughout. Nothing here moves: the grid is
/// marked once, and again only when the unit or the cap changes.
/// </summary>
public sealed class WallWorld {
    public const float CellSize = 0.5f;
    public const int CountX = 64, CountY = 40, CountZ = 24;
    public static float Width => CountX * CellSize;
    public static float Height => CountY * CellSize;
    public static float Depth => CountZ * CellSize;

    // Where the partitions stand (their lower x) and how high the doors are.
    private const float PartitionA = 10f, PartitionB = 21f, DoorHeight = 3.5f;

    private readonly Box[] _boxes;
    private readonly BoxNode[] _nodes;
    private readonly int[] _order, _workspace;
    private int _nodeCount;

    public GridShape Shape { get; } = new(0, 0, 0, CellSize, CountX, CountY, CountZ);
    public Solid[] Solids { get; }

    /// <summary>One count per cell, x then y then z, as the library stores them.</summary>
    public byte[] Counts { get; } = new byte[CountX * CountY * CountZ];

    /// <summary>Metres per count and the largest count, as last marked.</summary>
    public float Unit { get; private set; }
    public byte Cap { get; private set; }

    /// <summary>How long marking every layer took, in milliseconds.</summary>
    public double MarkMilliseconds { get; private set; }

    public WallWorld(int seed, float unit, byte cap) {
        var random = new Random(seed);
        var solids = new List<Solid>();
        // Two partitions, 0.5 m thick, make three rooms. Each door has a lintel above it: the first is 2.5 m wide, the
        // second 1.5 m, so a body of radius 0.9 m gets through the first and not the second, and a smaller one through both.
        solids.Add(Solid.Box(new Box(PartitionA, 0, 0, PartitionA + 0.5f, 8, Depth)));
        solids.Add(Solid.Box(new Box(PartitionA, 10.5f, 0, PartitionA + 0.5f, Height, Depth)));
        solids.Add(Solid.Box(new Box(PartitionA, 8, DoorHeight, PartitionA + 0.5f, 10.5f, Depth)));
        solids.Add(Solid.Box(new Box(PartitionB, 0, 0, PartitionB + 0.5f, 4.5f, Depth)));
        solids.Add(Solid.Box(new Box(PartitionB, 6, 0, PartitionB + 0.5f, Height, Depth)));
        solids.Add(Solid.Box(new Box(PartitionB, 4.5f, DoorHeight, PartitionB + 0.5f, 6, Depth)));
        // Big enough to read at a glance and few enough to leave room between them.
        for (var i = 0; i < 5; i++) { AddBox(random, i < 2, solids); }
        for (var i = 0; i < 4; i++) { AddSphere(random, solids); }
        for (var i = 0; i < 3; i++) { AddCapsule(random, solids); }
        Solids = solids.ToArray();

        var count = Solids.Length;
        _boxes = new Box[count];
        _nodes = new BoxNode[Shapes.NodesLength(count)];
        _order = new int[count];
        _workspace = new int[Shapes.WorkspaceLength(count)];
        if (!Shapes.Build(Solids, ReadOnlySpan<float>.Empty, ReadOnlySpan<int>.Empty, _boxes, _nodes, _order, _workspace, out _nodeCount)) {
            throw new InvalidOperationException("Shapes.Build rejected the solids.");
        }
        Mark(unit, cap);
    }

    /// <summary>Marks every z layer again with a new unit and cap.</summary>
    public void Mark(float unit, byte cap) {
        var clock = Stopwatch.StartNew();
        var set = Set();
        var length = Shape.LayerLength;
        for (var z = 0; z < CountZ; z++) {
            if (!WallDistance.MarkLayer(Shape, set, unit, cap, z, Counts.AsSpan(z * length, length))) {
                throw new InvalidOperationException("WallDistance.MarkLayer refused the grid.");
            }
        }
        MarkMilliseconds = clock.Elapsed.TotalMilliseconds;
        (Unit, Cap) = (unit, cap);
    }

    private ShapeSet Set() => new(Solids, ReadOnlySpan<float>.Empty, ReadOnlySpan<int>.Empty, _boxes, _nodes.AsSpan(0, _nodeCount), _order);

    /// <summary>The cell holding a coordinate on an axis; a point on the upper face belongs to the last cell.</summary>
    public static int CellOf(float value, int count) => Math.Clamp((int)Math.Floor(value / CellSize), 0, count - 1);

    /// <summary>A point in the grid's box, faces included.</summary>
    public static float Clamp(float value, float extent) => Math.Clamp(value, 0f, extent);

    public byte CountAt(int cellX, int cellY, int cellZ) => Counts[Shape.IndexOf(cellX, cellY, cellZ)];

    /// <summary>What the grid alone proves: no shape is nearer than this. False outside the grid.</summary>
    public bool Room(float x, float y, float z, out float room) =>
        WallDistance.RoomAt(new WallMap(Shape, Unit, Cap, Counts), x, y, z, out room);

    /// <summary>The distance to the nearest shape from the shapes themselves, 0 inside one.</summary>
    public bool Exact(float x, float y, float z, out float distance) => Shapes.Distance(Set(), x, y, z, 1000f, out distance);

    /// <summary>
    /// Where the plane at <paramref name="fixedAt"/> (a z for the x-by-y slice, a y for the x-by-z slice) cuts each solid,
    /// as outlines in the slice's own (u, v) metres, worked out from the solids' public fields. A solid the plane misses gives none.
    /// </summary>
    public List<(double U, double V)[]> Sections(SlicePlane plane, float fixedAt) {
        var outlines = new List<(double U, double V)[]>();
        foreach (var solid in Solids) {
            if (SolidSection.Cut(solid, plane, fixedAt) is { } outline) { outlines.Add(outline); }
        }
        return outlines;
    }

    // ---- Regions: where a body of one radius can be, flooded from seeds. ----

    private readonly byte[] _region = new byte[CountX * CountY * CountZ];
    private readonly int[] _queue = new int[CountX * CountY * CountZ];

    /// <summary>One byte per cell: 0 for a cell that is not open or that no seed reached, else its region's number.</summary>
    public byte[] Region => _region;

    /// <summary>The seeds the flood started from: small boxes in open space, which the view draws.</summary>
    public Solid[] Seeds { get; private set; } = [];

    /// <summary>What the size class needs of the map, when <see cref="Problem"/> is null.</summary>
    public SizeClass Size { get; private set; }

    /// <summary>Why there are no regions, or null if the flood ran: the radius is too small, or needs more than the cap can say.</summary>
    public string? Problem { get; private set; }

    public int RegionCount { get; private set; }
    public int Reached { get; private set; }
    public int EmptySeeds { get; private set; }
    public double FloodMilliseconds { get; private set; }


    /// <summary>
    /// Makes the size class of a radius and floods from <paramref name="seedCount"/> seeds placed where the shapes leave
    /// room. Call again after the map is marked again, since the class is made for one unit and cap.
    /// </summary>
    public void Flood(float radius, int seedCount, int seedKey) {
        Array.Clear(_region);
        Seeds = [];
        (RegionCount, Reached, EmptySeeds, FloodMilliseconds) = (0, 0, 0, 0);
        Size = default;
        var clock = Stopwatch.StartNew();
        var shape = Shape;
        var map = new WallMap(shape, Unit, Cap, Counts);
        if (!SizeClass.TryMake(radius, map, out var size)) {
            Problem = radius > 0.5f * CellSize
                ? $"a body of radius {radius:0.##} m needs more than a cap of {Cap} counts of {Unit:0.#####} m can say: raise the cap, or the unit"
                : $"a radius of half a cell ({0.5f * CellSize:0.##} m) or less has no guarantee";
            return;
        }
        Size = size;

        // Seeds sit where an exact test says there is room, so that each one starts a flood: a small box in the open.
        var random = new Random(seedKey);
        var seeds = new List<Solid>();
        for (var tries = 0; tries < 400 && seeds.Count < seedCount; tries++) {
            // One in each room in turn, so the seeds show which rooms a body of this size can pass between.
            var which = seeds.Count % 3;
            float lowX = which == 0 ? 0 : which == 1 ? PartitionA + 0.5f : PartitionB + 0.5f, highX = which == 0 ? PartitionA : which == 1 ? PartitionB : Width;
            float x = Range(random, lowX + 1.5f, highX - 1.5f), y = Range(random, 2, Height - 2), z = Range(random, 1.5f, Depth - 1.5f);
            if (Exact(x, y, z, out var room) && room >= radius + 0.6f) { seeds.Add(Solid.Box(new Box(x - 0.3f, y - 0.3f, z - 0.3f, x + 0.3f, y + 0.3f, z + 0.3f))); }
        }
        Seeds = seeds.ToArray();

        if (!Regions.FromSeeds(map, size, Cost, Seeds, _region, _queue, out var reached, out var empty)) {
            Problem = "the flood was refused: more regions than a byte holds, or a seed that may not be stored";
            return;
        }
        Problem = null;
        (Reached, EmptySeeds) = (reached, empty);
        foreach (var number in _region) { RegionCount = Math.Max(RegionCount, number); }
        FloodMilliseconds = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>The number of the region a cell is in, 0 if it is in none.</summary>
    public byte RegionAt(int cellX, int cellY, int cellZ) => _region[Shape.IndexOf(cellX, cellY, cellZ)];

    /// <summary>
    /// What a cell shows in region colour: its region (1 to 254), 255 for a cell open for the class that no seed reached,
    /// 256 for a cell too near a shape for it.
    /// </summary>
    public int RegionKey(int cellX, int cellY, int cellZ) {
        var region = RegionAt(cellX, cellY, cellZ);
        if (region != 0) { return region; }
        return Problem is null && Size.Cell > 0 && CountAt(cellX, cellY, cellZ) >= Size.Cell ? 255 : 256;
    }

    public bool IsOpen(float x, float y, float z) => Regions.IsOpen(Shape, _region, x, y, z);

    /// <summary>The centre of the nearest open cell within a reach, in any region, or in the region of the cell holding a second point.</summary>
    public bool NearestOpen(float x, float y, float z, float reach, (float x, float y, float z)? like, out float openX, out float openY, out float openZ) =>
        like is { } l
            ? Regions.NearestOpen(Shape, _region, x, y, z, reach, l.x, l.y, l.z, out openX, out openY, out openZ)
            : Regions.NearestOpen(Shape, _region, x, y, z, reach, out openX, out openY, out openZ);

    /// <summary>Where the plane cuts each seed, as <see cref="Sections"/> does for the solids.</summary>
    public List<(double U, double V)[]> SeedSections(SlicePlane plane, float fixedAt) {
        var outlines = new List<(double U, double V)[]>();
        foreach (var seed in Seeds) {
            if (SolidSection.Cut(seed, plane, fixedAt) is { } outline) { outlines.Add(outline); }
        }
        return outlines;
    }

    // ---- Travel: how far each open cell is from a goal, going round what is in the way. ----

    /// <summary>Metres per count of the travel field: fine, so that two neighbours never share a count.</summary>
    public const float TravelUnit = 0.01f;

    /// <summary>The goals a field can start from: a point (where the view puts it), or the plane through a door.</summary>
    public const int GoalPoint = 0, GoalFirstDoor = 1, GoalSecondDoor = 2;

    private readonly ushort[] _travel = new ushort[CountX * CountY * CountZ];
    private readonly float[] _times = new float[CountX * CountY * CountZ];
    private readonly int[] _heap = new int[2 * CountX * CountY * CountZ];

    /// <summary>One byte per cell of what it costs to cross it: 0 normal, 255 closed. Cost boxes are written into it.</summary>
    public byte[] Cost { get; } = new byte[CountX * CountY * CountZ];

    /// <summary>The boxes written into <see cref="Cost"/>, which the view draws.</summary>
    public List<Solid> CostBoxes { get; } = [];

    /// <summary>The solid the field starts in, or null if there is no field.</summary>
    public Solid? GoalSolid { get; private set; }

    /// <summary>Why there is no field, or null if there is one.</summary>
    public string? TravelProblem { get; private set; }

    public int TravelReached { get; private set; }
    public int TravelCut { get; private set; }
    public double TravelMilliseconds { get; private set; }

    /// <summary>One count per cell of travel distance, or Travel.Unreached.</summary>
    public ushort[] TravelCounts => _travel;

    /// <summary>The farthest travel distance the field reaches, in metres.</summary>
    public float TravelMax { get; private set; }

    /// <summary>Writes a cost into every cell whose centre is inside a cube about a point, and returns false if the library refuses.</summary>
    public bool DropCost(float x, float y, float z, float half, byte value) {
        var box = Solid.Box(new Box(x - half, y - half, z - half, x + half, y + half, z + half));
        var shape = Shape;
        if (!CellCost.Fill(shape, box, value, Cost)) { return false; }
        CostBoxes.Add(box);
        return true;
    }

    public void ClearCost() {
        Array.Clear(Cost);
        CostBoxes.Clear();
    }

    /// <summary>
    /// Builds the field for a goal, through the cells of the regions the flood reached and the costs written so far. A point goal
    /// starts from the cells within 0.8 m of the point, each at its distance from it; a door goal starts from a box round the door,
    /// the cells in front of the door plane at their distance from it and those behind at 0, so the field leads out through the door.
    /// </summary>
    public void BuildTravel(int goal, float x, float y, float z) {
        GoalSolid = null;
        (TravelReached, TravelCut, TravelMax, TravelMilliseconds) = (0, 0, 0f, 0.0);
        Array.Fill(_travel, Travel.Unreached);
        TravelProblem = null;
        if (Problem is not null) {
            TravelProblem = "there are no regions without a size class, and a field spreads through regions";
            return;
        }
        var clock = Stopwatch.StartNew();
        TravelSeed seed;
        if (goal == GoalPoint) {
            GoalSolid = Solid.Sphere(x, y, z, 0.8f);
            seed = TravelSeed.FromPoint(GoalSolid.Value, x, y, z);
        } else {
            // The door in the partition: the box round it reaches a little way to either side, and the plane through its middle faces +x.
            float wall = goal == GoalFirstDoor ? PartitionA : PartitionB, from = goal == GoalFirstDoor ? 8f : 4.5f, to = goal == GoalFirstDoor ? 10.5f : 6f;
            GoalSolid = Solid.Box(new Box(wall - 0.75f, from, 0, wall + 1.25f, to, DoorHeight));
            seed = TravelSeed.FromPlane(GoalSolid.Value, wall + 0.25f, (from + to) / 2, DoorHeight / 2, 1, 0, 0);
        }
        var shape = Shape;
        TravelSeed[] seeds = [seed];
        if (!Travel.Build(shape, _region, Cost, seeds, TravelUnit, _travel, _times, _heap, out var reached, out var cut)) {
            TravelProblem = "the library refused to build the field";
            return;
        }
        (TravelReached, TravelCut) = (reached, cut);
        foreach (var count in _travel) {
            if (count != Travel.Unreached && count * TravelUnit > TravelMax) { TravelMax = count * TravelUnit; }
        }
        TravelMilliseconds = clock.Elapsed.TotalMilliseconds;
        if (reached == 0) { TravelProblem = goal == GoalPoint ? "the goal point holds no open cell of a region the seeds reached" : "the door holds no open cell for this body, which does not fit through it"; }
    }

    /// <summary>The travel distance at a point, in metres, and the direction that leads down the field from there. False if none of the eight cells round it is reached.</summary>
    public bool SampleField(float x, float y, float z, out float dx, out float dy, out float dz, out float distance) {
        var shape = Shape;
        var field = new TravelField(shape, TravelUnit, _travel);
        return Travel.Sample(field, x, y, z, out dx, out dy, out dz, out distance);
    }

    /// <summary>What a cell shows in travel colour: a band 0 to 47 by its distance, 48 if it is open but the field does not reach it, 49 if it is not open.</summary>
    public int TravelKey(int cellX, int cellY, int cellZ) {
        var count = _travel[Shape.IndexOf(cellX, cellY, cellZ)];
        if (count != Travel.Unreached && TravelMax > 0f) { return Math.Min(47, (int)(count * TravelUnit / TravelMax * 48)); }
        if (count != Travel.Unreached) { return 0; }
        return RegionAt(cellX, cellY, cellZ) != 0 ? 48 : 49;
    }

    /// <summary>Follows the field down from a point in steps of a quarter of a metre, as something with no plan of its own would.</summary>
    public (List<(float x, float y, float z)> Path, string End) FollowField(float x, float y, float z) {
        var path = new List<(float x, float y, float z)> { (x, y, z) };
        if (TravelProblem is not null) { return (path, "there is no field"); }
        const float step = 0.25f;
        for (var i = 0; i < 800; i++) {
            if (!SampleField(x, y, z, out var dx, out var dy, out var dz, out var distance)) { return (path, "left the field"); }
            if (distance < 0.5f) { return (path, "reached the goal"); }
            if (dx == 0f && dy == 0f && dz == 0f) { return (path, $"found no way down at {distance:0.00} m"); }
            (x, y, z) = (x + dx * step, y + dy * step, z + dz * step);
            path.Add((x, y, z));
        }
        return (path, "gave up after 800 steps");
    }

    /// <summary>Where the plane cuts each cost box, as <see cref="Sections"/> does for the solids.</summary>
    public List<(double U, double V)[]> CostSections(SlicePlane plane, float fixedAt) {
        var outlines = new List<(double U, double V)[]>();
        foreach (var box in CostBoxes) {
            if (SolidSection.Cut(box, plane, fixedAt) is { } outline) { outlines.Add(outline); }
        }
        return outlines;
    }

    /// <summary>Where the plane cuts the solid the field starts in.</summary>
    public List<(double U, double V)[]> GoalSections(SlicePlane plane, float fixedAt) {
        var outlines = new List<(double U, double V)[]>();
        if (GoalSolid is { } goal && SolidSection.Cut(goal, plane, fixedAt) is { } outline) { outlines.Add(outline); }
        return outlines;
    }

    // ---- Routes: a way from one point to another for a body of one radius. ----

    private float[]? _sums;
    private int[]? _links, _routeHeap;
    private float[] _routePoints = new float[3 * 2048];

    /// <summary>What a search for a route found: how it ended, the points it leads through (the start is not one), and what it took.</summary>
    public sealed record RouteFound(RouteStatus Status, RouteEnds Ends, int Count, int Expanded, float Cost, List<(float x, float y, float z)> Points, double Milliseconds);

    /// <summary>
    /// Looks for the way from the start to the goal through the regions and costs as they stand, for the size class of the last flood.
    /// <paramref name="straighten"/> false gives the cells' way, the centres of the cells it steps through; <paramref name="useShapes"/> false
    /// gives the library no shapes, so that a leg is proven by the map alone.
    /// </summary>
    public RouteFound FindRoute(float fromX, float fromY, float fromZ, float toX, float toY, float toZ, bool straighten, bool useShapes) {
        var wall = new WallMap(Shape, Unit, Cap, Counts);
        // The links must be zero the first time and a search leaves them so.
        _sums ??= new float[Routes.SumsLength(Shape)];
        _links ??= new int[Routes.LinksLength(Shape)];
        _routeHeap ??= new int[Routes.HeapLength(Shape)];
        var clock = Stopwatch.StartNew();
        var size = Size;
        var shapes = useShapes ? Set() : default;
        // No way through the regions enters a cell that costs less than this, which lets the search take a clear straight shot over evenly costed cells at once.
        var least = Regions.LeastCost(Shape, _region, Cost);
        var result = Routes.Find(wall, size, shapes, _region, Cost, fromX, fromY, fromZ, toX, toY, toZ, straighten, _sums, _links, _routeHeap, _routePoints, least);
        if (result.Status == RouteStatus.BufferTooShort) {
            _routePoints = new float[3 * result.Count];
            result = Routes.Find(wall, size, shapes, _region, Cost, fromX, fromY, fromZ, toX, toY, toZ, straighten, _sums, _links, _routeHeap, _routePoints, least);
        }
        var milliseconds = clock.Elapsed.TotalMilliseconds;
        var points = new List<(float x, float y, float z)>();
        if (result.Status == RouteStatus.Found) {
            for (var i = 0; i < result.Count; i++) { points.Add((_routePoints[3 * i], _routePoints[3 * i + 1], _routePoints[3 * i + 2])); }
        }
        return new RouteFound(result.Status, result.Ends, result.Count, result.Expanded, result.Cost, points, milliseconds);
    }

    private static float Range(Random random, float low, float high) => low + (float)random.NextDouble() * (high - low);

    // The x range, in a room chosen at random, that a shape reaching this far from its centre may centre in without touching a partition.
    private static (float lo, float hi) RoomRange(Random random, float reach) {
        var room = random.Next(3);
        float lo = room == 0 ? 0 : room == 1 ? PartitionA + 0.5f : PartitionB + 0.5f, hi = room == 0 ? PartitionA : room == 1 ? PartitionB : Width;
        return (lo + reach, hi - reach);
    }

    private static void AddBox(Random random, bool turned, List<Solid> solids) {
        float hx = Range(random, 0.8f, 1.8f), hy = Range(random, 0.8f, 1.8f), hz = Range(random, 0.8f, 2.4f);
        var (low, high) = RoomRange(random, hx + hy);
        float cx = Range(random, low, high), cy = Range(random, 3, Height - 3), cz = Range(random, 2.5f, Depth - 2.5f);
        // Axes from two turns, worked in double so they are of length 1 and square to each other to within a float's rounding.
        double yaw = turned ? random.NextDouble() * Math.PI : 0, tilt = turned ? (random.NextDouble() - 0.5) * 0.9 : 0;
        double cy1 = Math.Cos(yaw), sy1 = Math.Sin(yaw), ct = Math.Cos(tilt), st = Math.Sin(tilt);
        // U = Rz(yaw) * (1,0,0); V = Rz(yaw) * Rx(tilt) * (0,1,0); W = Rz(yaw) * Rx(tilt) * (0,0,1).
        double ux = cy1, uy = sy1, uz = 0;
        double vx = -sy1 * ct, vy = cy1 * ct, vz = st;
        double wx = sy1 * st, wy = -cy1 * st, wz = ct;
        solids.Add(Solid.Box(cx, cy, cz, (float)ux, (float)uy, (float)uz, (float)vx, (float)vy, (float)vz, (float)wx, (float)wy, (float)wz, hx, hy, hz));
    }

    private static void AddSphere(Random random, List<Solid> solids) {
        var radius = Range(random, 0.8f, 2.2f);
        var (low, high) = RoomRange(random, radius + 0.3f);
        float cx = Range(random, low, high), cy = Range(random, 3, Height - 3), cz = Range(random, 2.5f, Depth - 2.5f);
        solids.Add(Solid.Sphere(cx, cy, cz, radius));
    }

    private static void AddCapsule(Random random, List<Solid> solids) {
        var radius = Range(random, 0.4f, 0.9f);
        var (low, high) = RoomRange(random, 1.5f);
        float ax = Range(random, low, high), ay = Range(random, 3, Height - 3), az = Range(random, 2.5f, Depth - 2.5f);
        // The far end a few metres off in a random direction, kept inside the grid.
        var angle = random.NextDouble() * Math.PI * 2;
        var lift = (random.NextDouble() - 0.5) * 1.2;
        var length = Range(random, 2f, 5f);
        var bx = Math.Clamp(ax + (float)(Math.Cos(angle) * Math.Cos(lift)) * length, low - 0.6f, high + 0.6f);
        var by = Math.Clamp(ay + (float)(Math.Sin(angle) * Math.Cos(lift)) * length, 1f, Height - 1);
        var bz = Math.Clamp(az + (float)Math.Sin(lift) * length, 1f, Depth - 1);
        solids.Add(Solid.Capsule(ax, ay, az, bx, by, bz, radius));
    }
}
