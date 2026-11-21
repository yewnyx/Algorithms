using System.Diagnostics;
using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>What one query found, by the scan and by the tree, and what each had to test to find it.</summary>
public sealed class BoxQuery {
    public float X, Y, Z, Radius;
    public int K;

    /// <summary>The boxes within the radius, by BoxScan (ascending) and by BoxTree (sorted here to compare).</summary>
    public int[] ScanHits = [];
    public int[] TreeHits = [];
    public int ScanTotal, TreeTotal;

    /// <summary>The nearest boxes, nearest first, by each method.</summary>
    public int[] ScanNearest = [];
    public int[] TreeNearest = [];
    public float[] NearestDistances = [];
    public int ScanNearestCount, TreeNearestCount;

    public bool SameHits, SameNearest;

    /// <summary>What the tree's walk touched: nodes visited, boxes tested in the leaves it reached, and which nodes.</summary>
    public int TreeNodesVisited, TreeBoxesTested;
    public bool[] NodeVisited = [];

    /// <summary>Best time of a few runs of each query (InSphere then Nearest), in microseconds.</summary>
    public double ScanMicroseconds, TreeMicroseconds;
}

/// <summary>
/// Boxes, most at rest and a share drifting, with a BoxTree rebuilt whole after each step. They lie in a plane
/// (every z within -1 to 1) or, in the 3D world, fill a volume and drift in z too. Queries run both ways, BoxScan
/// over six columns and BoxTree, so a view can show they agree and what the tree saves.
/// </summary>
public sealed class BoxWorld {
    public const float Width = 1000f, Height = 640f, Depth3D = 400f;
    private const float Speed = 60f;

    private readonly Random _random;
    private readonly float[] _centerX, _centerY, _centerZ, _halfWidth, _halfHeight, _halfDepth, _vx, _vy, _vz;
    private readonly float[] _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
    private readonly int[] _workspace;
    private readonly int[] _stack = new int[64];

    // Where the timing runs write, so they leave the answers in the query alone.
    private int[] _scratchHits = [], _scratchNearest = [];
    private float[] _scratchDistances = [];

    public int Count { get; }

    /// <summary>Whether the boxes fill a volume (true) or lie in a plane.</summary>
    public bool ThreeD { get; }

    /// <summary>The z extent of the world: 0 to <see cref="Depth3D"/> in 3D, -1 to 1 for the plane.</summary>
    public float MinZ => ThreeD ? 0f : -1f;
    public float MaxZ => ThreeD ? Depth3D : 1f;
    public Box[] Boxes { get; }
    public bool[] Moving { get; }
    public BoxNode[] Nodes { get; }
    public int[] Order { get; }
    public int NodeCount { get; private set; }
    public double RebuildMicroseconds { get; private set; }

    public BoxWorld(int count, int movingPercent, int seed, bool threeD = false) {
        _random = new Random(seed);
        Count = Math.Max(0, count);
        ThreeD = threeD;
        Boxes = new Box[Count];
        Moving = new bool[Count];
        _centerX = new float[Count]; _centerY = new float[Count]; _centerZ = new float[Count];
        _halfWidth = new float[Count]; _halfHeight = new float[Count]; _halfDepth = new float[Count];
        _vx = new float[Count]; _vy = new float[Count]; _vz = new float[Count];
        _minX = new float[Count]; _minY = new float[Count]; _minZ = new float[Count];
        _maxX = new float[Count]; _maxY = new float[Count]; _maxZ = new float[Count];
        Nodes = new BoxNode[BoxTree.NodesLength(Count)];
        Order = new int[Count];
        _workspace = new int[BoxTree.WorkspaceLength(Count)];

        for (var i = 0; i < Count; i++) {
            _halfWidth[i] = 4 + (float)_random.NextDouble() * 10;
            _halfHeight[i] = 4 + (float)_random.NextDouble() * 10;
            _centerX[i] = _halfWidth[i] + (float)_random.NextDouble() * (Width - 2 * _halfWidth[i]);
            _centerY[i] = _halfHeight[i] + (float)_random.NextDouble() * (Height - 2 * _halfHeight[i]);
            // The plane's boxes draw nothing more from the generator, so a seed still gives the same boxes there.
            if (ThreeD) {
                _halfDepth[i] = 4 + (float)_random.NextDouble() * 10;
                _centerZ[i] = _halfDepth[i] + (float)_random.NextDouble() * (Depth3D - 2 * _halfDepth[i]);
            }
            if (_random.NextDouble() * 100 < movingPercent) {
                Moving[i] = true;
                var angle = _random.NextDouble() * Math.PI * 2;
                var speed = Speed * (0.4f + (float)_random.NextDouble());
                _vx[i] = (float)Math.Cos(angle) * speed;
                _vy[i] = (float)Math.Sin(angle) * speed;
                if (ThreeD) {
                    // Drift in a direction over the whole sphere, not just in the plane.
                    var up = _random.NextDouble() * 2 - 1;
                    var flat = Math.Sqrt(1 - up * up);
                    _vx[i] = (float)(Math.Cos(angle) * flat) * speed;
                    _vy[i] = (float)(Math.Sin(angle) * flat) * speed;
                    _vz[i] = (float)up * speed;
                }
            }
        }
        Place();
        Rebuild();
    }

    /// <summary>Moves the drifting boxes by <paramref name="seconds"/>, bouncing off the walls, and rebuilds the tree.</summary>
    public void Step(float seconds) {
        for (var i = 0; i < Count; i++) {
            if (!Moving[i]) { continue; }
            _centerX[i] += _vx[i] * seconds;
            _centerY[i] += _vy[i] * seconds;
            if (_centerX[i] < _halfWidth[i] || _centerX[i] > Width - _halfWidth[i]) {
                _vx[i] = -_vx[i];
                _centerX[i] = Math.Clamp(_centerX[i], _halfWidth[i], Width - _halfWidth[i]);
            }
            if (_centerY[i] < _halfHeight[i] || _centerY[i] > Height - _halfHeight[i]) {
                _vy[i] = -_vy[i];
                _centerY[i] = Math.Clamp(_centerY[i], _halfHeight[i], Height - _halfHeight[i]);
            }
            if (ThreeD) {
                _centerZ[i] += _vz[i] * seconds;
                if (_centerZ[i] < _halfDepth[i] || _centerZ[i] > Depth3D - _halfDepth[i]) {
                    _vz[i] = -_vz[i];
                    _centerZ[i] = Math.Clamp(_centerZ[i], _halfDepth[i], Depth3D - _halfDepth[i]);
                }
            }
        }
        Place();
        Rebuild();
    }

    private void Place() {
        for (var i = 0; i < Count; i++) {
            // The plane's boxes are two thick, centered on z = 0.
            var (low, high) = ThreeD ? (_centerZ[i] - _halfDepth[i], _centerZ[i] + _halfDepth[i]) : (-1f, 1f);
            Boxes[i] = new Box(_centerX[i] - _halfWidth[i], _centerY[i] - _halfHeight[i], low,
                _centerX[i] + _halfWidth[i], _centerY[i] + _halfHeight[i], high);
        }
    }

    private void Rebuild() {
        var extent = new Box(0f, 0f, MinZ, Width, Height, MaxZ);
        BoxScan.Pack(Boxes, _minX, _minY, _minZ, _maxX, _maxY, _maxZ);
        // Every box lies inside the extent, so a failure means the world and the library disagree.
        if (!BoxTree.Build(Boxes, extent, Nodes, Order, _workspace, out var nodeCount)) {
            throw new InvalidOperationException("BoxTree.Build rejected the boxes.");
        }
        NodeCount = nodeCount;
    }

    // Runs the work over and over until the clock has ticked enough to trust, and returns the time of one run.
    // Browsers round their clocks to 0.1 ms or more, so one run of a query that takes microseconds reads as 0.
    private static double Microseconds(Action work) {
        for (var repeats = 1; ; repeats *= 2) {
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < repeats; i++) { work(); }
            var elapsed = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
            if (elapsed >= 1000 || repeats >= 1 << 16) { return elapsed / repeats; }
        }
    }

    /// <summary>
    /// Runs both queries at a point (in the plane world z is 0, inside every box's thickness), writes what they
    /// found into <paramref name="q"/>, and checks that they agree. With <paramref name="measure"/> it also
    /// times the queries and a rebuild; without it the times stay what they were.
    /// </summary>
    public void Run(BoxQuery q, float x, float y, float z, float radius, int k, bool measure = true) {
        (q.X, q.Y, q.Z, q.Radius, q.K) = (x, y, z, radius, k);
        if (q.ScanHits.Length != Count) { (q.ScanHits, q.TreeHits) = (new int[Count], new int[Count]); }
        if (q.ScanNearest.Length != k) { (q.ScanNearest, q.TreeNearest, q.NearestDistances) = (new int[k], new int[k], new float[k]); }
        if (q.NodeVisited.Length != Nodes.Length) { q.NodeVisited = new bool[Nodes.Length]; }
        if (_scratchHits.Length != Count) { _scratchHits = new int[Count]; }
        if (_scratchNearest.Length != k) { (_scratchNearest, _scratchDistances) = (new int[k], new float[k]); }
        var columns = new BoxColumns(_minX, _minY, _minZ, _maxX, _maxY, _maxZ);
        var nodes = Nodes.AsSpan(0, NodeCount);
        var maxDistanceSquared = float.PositiveInfinity;

        // Once for the answers, which are what the view shows...
        q.ScanTotal = BoxScan.InSphere(columns, x, y, z, radius, q.ScanHits, out _);
        q.ScanNearestCount = BoxScan.Nearest(columns, x, y, z, maxDistanceSquared, q.ScanNearest, q.NearestDistances);
        q.TreeTotal = BoxTree.InSphere(nodes, Order, Boxes, x, y, z, radius, q.TreeHits, out _);
        q.TreeNearestCount = BoxTree.Nearest(nodes, Order, Boxes, x, y, z, maxDistanceSquared, q.TreeNearest, _scratchDistances);

        // ...and, when asked, again and again into scratch buffers for the times.
        if (measure) {
            q.ScanMicroseconds = Microseconds(() => {
                var c = new BoxColumns(_minX, _minY, _minZ, _maxX, _maxY, _maxZ);
                BoxScan.InSphere(c, x, y, z, radius, _scratchHits, out _);
                BoxScan.Nearest(c, x, y, z, maxDistanceSquared, _scratchNearest, _scratchDistances);
            });
            q.TreeMicroseconds = Microseconds(() => {
                BoxTree.InSphere(Nodes.AsSpan(0, NodeCount), Order, Boxes, x, y, z, radius, _scratchHits, out _);
                BoxTree.Nearest(Nodes.AsSpan(0, NodeCount), Order, Boxes, x, y, z, maxDistanceSquared, _scratchNearest, _scratchDistances);
            });
            RebuildMicroseconds = Microseconds(Rebuild);
        }

        // The tree returns its hits in tree order; the scan's are ascending.
        Array.Sort(q.TreeHits, 0, Math.Max(0, q.TreeTotal));
        q.SameHits = q.ScanTotal == q.TreeTotal && q.ScanHits.AsSpan(0, Math.Max(0, q.ScanTotal)).SequenceEqual(q.TreeHits.AsSpan(0, Math.Max(0, q.TreeTotal)));
        q.SameNearest = q.ScanNearestCount == q.TreeNearestCount &&
                        q.ScanNearest.AsSpan(0, Math.Max(0, q.ScanNearestCount)).SequenceEqual(q.TreeNearest.AsSpan(0, Math.Max(0, q.TreeNearestCount)));

        WalkTree(q, x, y, z, radius * radius);
    }

    // The same walk BoxTree.InSphere makes, to count what it touches: a node is visited when it is popped,
    // and its boxes are tested only in a leaf the walk reaches. BoxTree doesn't report this itself.
    private void WalkTree(BoxQuery q, float x, float y, float z, float limit) {
        Array.Clear(q.NodeVisited);
        (q.TreeNodesVisited, q.TreeBoxesTested) = (0, 0);
        if (NodeCount == 0) { return; }
        var top = 0;
        _stack[top++] = 0;
        while (top > 0) {
            var index = _stack[--top];
            var node = Nodes[index];
            if (DistanceSquared(node, x, y, z) > limit) { continue; }
            q.NodeVisited[index] = true;
            q.TreeNodesVisited++;
            if (node.Count > 0) {
                q.TreeBoxesTested += node.Count;
                continue;
            }
            _stack[top++] = node.First;
            _stack[top++] = index + 1;
        }
    }

    private static float DistanceSquared(in BoxNode node, float x, float y, float z) {
        var dx = Math.Max(Math.Max(node.MinX - x, x - node.MaxX), 0f);
        var dy = Math.Max(Math.Max(node.MinY - y, y - node.MaxY), 0f);
        var dz = Math.Max(Math.Max(node.MinZ - z, z - node.MaxZ), 0f);
        return dx * dx + dy * dy + dz * dz;
    }
}
