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

    private static float Range(Random random, float low, float high) => low + (float)random.NextDouble() * (high - low);

    private static void AddBox(Random random, bool turned, List<Solid> solids) {
        float hx = Range(random, 0.8f, 2.6f), hy = Range(random, 0.8f, 2.6f), hz = Range(random, 0.8f, 2.4f);
        float cx = Range(random, 3, Width - 3), cy = Range(random, 3, Height - 3), cz = Range(random, 2.5f, Depth - 2.5f);
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
        float cx = Range(random, 3, Width - 3), cy = Range(random, 3, Height - 3), cz = Range(random, 2.5f, Depth - 2.5f);
        solids.Add(Solid.Sphere(cx, cy, cz, radius));
    }

    private static void AddCapsule(Random random, List<Solid> solids) {
        var radius = Range(random, 0.4f, 0.9f);
        float ax = Range(random, 3, Width - 3), ay = Range(random, 3, Height - 3), az = Range(random, 2.5f, Depth - 2.5f);
        // The far end a few metres off in a random direction, kept inside the grid.
        var angle = random.NextDouble() * Math.PI * 2;
        var lift = (random.NextDouble() - 0.5) * 1.2;
        var length = Range(random, 2f, 5f);
        var bx = Math.Clamp(ax + (float)(Math.Cos(angle) * Math.Cos(lift)) * length, 1f, Width - 1);
        var by = Math.Clamp(ay + (float)(Math.Sin(angle) * Math.Cos(lift)) * length, 1f, Height - 1);
        var bz = Math.Clamp(az + (float)Math.Sin(lift) * length, 1f, Depth - 1);
        solids.Add(Solid.Capsule(ax, ay, az, bx, by, bz, radius));
    }
}
