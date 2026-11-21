using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Boxes in a volume with a query sphere, seen from an orbiting camera. Plain 2D drawing of projected edges (no GPU
/// API), so it runs in the browser too. Drag to orbit, Ctrl and the wheel to zoom, the wheel alone to push the sphere
/// nearer or farther along the line of sight, and moving the pointer slides the sphere across the plane facing the
/// camera. Colors follow <see cref="BoxView"/>: resting grey, drifting orange, found boxes blue, the tree's visited
/// nodes yellow, the nearest boxes numbered.
/// </summary>
public sealed class BoxView3D : Control {
    private static readonly IBrush Background = new SolidColorBrush(Palette.ToColor(Palette.Background));
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)), 1);
    private static readonly IPen[] Resting = Fade(Color.FromRgb(0x78, 0x89, 0xA6), 0xD0, 0x88, 0x4A);
    private static readonly IPen[] Drifting = Fade(Color.FromRgb(0xFF, 0x8A, 0x65), 0xF0, 0xA8, 0x66);
    private static readonly IBrush FoundFill = new SolidColorBrush(Color.FromArgb(0x70, 0x4F, 0xC3, 0xF7));
    private static readonly IPen Found = new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0xF5, 0xFE)), 1.5);
    private static readonly IPen Skipped = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x90, 0xA4, 0xAE)), 1);
    private static readonly IPen LeafVisited = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xD5, 0x4F)), 1);
    private static readonly IPen SphereEdge = new Pen(new SolidColorBrush(Colors.White), 1.5);
    private static readonly IPen SphereRing = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush SphereFill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly IPen Plumb = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1, DashStyle.Dash);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x17, 0x1D));
    private static readonly IBrush CenterBrush = new SolidColorBrush(Colors.White);

    // Corner i of a box has x high if bit 0 is set, y high if bit 1, z high if bit 2.
    private static readonly int[] Edges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];
    private static readonly int[] Faces = [0, 1, 3, 2, 4, 5, 7, 6, 0, 2, 6, 4, 1, 3, 7, 5, 0, 1, 5, 4, 2, 3, 7, 6];

    private const double MaxPitch = 1.45;

    private readonly Point[] _corners = new Point[8];
    private bool _dragging;
    private Point _last;

    public BoxWorld? World { get; set; }
    public BoxQuery? Query { get; set; }

    /// <summary>Whether the sphere's boxes come from the tree rather than the scan (they are the same boxes).</summary>
    public bool UseTree { get; set; }
    public bool ShowTree { get; set; }

    /// <summary>The camera's turn about the vertical axis and its tilt, in radians; a tilt of 0 looks along z, as the top-down view does.</summary>
    public double Yaw { get; set; } = 0.6;
    public double Pitch { get; set; } = 0.42;

    /// <summary>Scales the picture; 1 fits the whole world.</summary>
    public double Zoom { get; set; } = 1;

    public event Action<float, float, float>? QueryMoved;

    public BoxView3D() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void ResetCamera() {
        (Yaw, Pitch, Zoom) = (0.6, 0.42, 1);
        InvalidateVisual();
    }

    /// <summary>Turns the camera about the world, as dragging does.</summary>
    public void Orbit(double yaw, double pitch) {
        Yaw += yaw;
        Pitch = Math.Clamp(Pitch + pitch, -MaxPitch, MaxPitch);
        InvalidateVisual();
    }

    private static IPen[] Fade(Color color, byte near, byte middle, byte far) => [
        new Pen(new SolidColorBrush(Color.FromArgb(near, color.R, color.G, color.B)), 1),
        new Pen(new SolidColorBrush(Color.FromArgb(middle, color.R, color.G, color.B)), 1),
        new Pen(new SolidColorBrush(Color.FromArgb(far, color.R, color.G, color.B)), 1),
    ];

    /// <summary>The camera: a turn and a tilt about the middle of the world, then a perspective from far enough out to see all of it.</summary>
    private readonly struct Camera {
        private readonly double _cx, _cy, _wx, _wy, _wz, _cosYaw, _sinYaw, _cosPitch, _sinPitch;
        public readonly double Focal, Distance, Reach;

        public Camera(Size size, double yaw, double pitch, double zoom) {
            (_wx, _wy, _wz) = (BoxWorld.Width / 2, BoxWorld.Height / 2, BoxWorld.Depth3D / 2);
            Reach = Math.Sqrt(_wx * _wx + _wy * _wy + _wz * _wz);
            Distance = Reach * 2.6;
            // At zoom 1 the sphere around the whole world fills about half of the smaller side.
            Focal = Math.Min(size.Width, size.Height) * 0.5 * Distance / Reach * Math.Clamp(zoom, 0.3, 6);
            (_cx, _cy) = (size.Width / 2, size.Height / 2);
            (_cosYaw, _sinYaw, _cosPitch, _sinPitch) = (Math.Cos(yaw), Math.Sin(yaw), Math.Cos(pitch), Math.Sin(pitch));
        }

        /// <summary>A world point in camera space: x right, y down, z away from the camera, the world's middle at 0.</summary>
        public (double x, double y, double z) ToCamera(double x, double y, double z) {
            x -= _wx; y -= _wy; z -= _wz;
            var x1 = x * _cosYaw + z * _sinYaw;
            var z1 = -x * _sinYaw + z * _cosYaw;
            return (x1, y * _cosPitch - z1 * _sinPitch, y * _sinPitch + z1 * _cosPitch);
        }

        public (double x, double y, double z) ToWorld(double x, double y, double z) {
            var y1 = y * _cosPitch + z * _sinPitch;
            var z1 = -y * _sinPitch + z * _cosPitch;
            return (x * _cosYaw - z1 * _sinYaw + _wx, y1 + _wy, x * _sinYaw + z1 * _cosYaw + _wz);
        }

        /// <summary>Where a world point lands on the screen, and its distance from the camera along the view.</summary>
        public Point Project(double x, double y, double z, out double depth) {
            var (a, b, c) = ToCamera(x, y, z);
            depth = Distance + c;
            var k = Focal / depth;
            return new Point(_cx + a * k, _cy + b * k);
        }

        /// <summary>
        /// The world point under a screen position at the given distance from the camera, kept within a ball about
        /// the world's middle that is a little wider than the world. The ball only trims the sideways reach, so
        /// the same pointer position always gives the same point whatever path led to it.
        /// </summary>
        public (double x, double y, double z) Unproject(Point p, double depth) {
            var limit = Reach * 1.25;
            var c = Math.Clamp(depth - Distance, -limit, limit);
            var k = Focal / (Distance + c);
            double a = (p.X - _cx) / k, b = (p.Y - _cy) / k;
            var side = Math.Sqrt(a * a + b * b);
            var room = Math.Sqrt(Math.Max(0, limit * limit - c * c));
            if (side > room) { (a, b) = (a * room / side, b * room / side); }
            return ToWorld(a, b, c);
        }
    }

    private Camera MakeCamera() => new(Bounds.Size, Yaw, Pitch, Zoom);

    private static (float x, float y, float z) ToFloats(double x, double y, double z) => ((float)x, (float)y, (float)z);

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return; }
        _dragging = true;
        _last = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!_dragging) { return; }
        _dragging = false;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging) {
            Orbit((p.X - _last.X) * 0.01, (p.Y - _last.Y) * 0.01);
            _last = p;
            return;
        }
        SlideTo(p);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0) {
            Zoom = Math.Clamp(Zoom * Math.Pow(1.1, e.Delta.Y), 0.3, 6);
            InvalidateVisual();
        } else {
            PushAlongSight(e.GetPosition(this), e.Delta.Y * 30);
        }
        e.Handled = true;
    }

    /// <summary>Puts the sphere under the pointer, at the distance from the camera it already has.</summary>
    private void SlideTo(Point screen) {
        if (Query is null || Bounds.Width < 1) { return; }
        var cam = MakeCamera();
        cam.Project(Query.X, Query.Y, Query.Z, out var depth);
        var (x, y, z) = cam.Unproject(screen, depth);
        var (cx, cy, cz) = ToFloats(x, y, z);
        QueryMoved?.Invoke(cx, cy, cz);
    }

    /// <summary>Moves the sphere along the line of sight through the pointer, nearer for positive steps.</summary>
    private void PushAlongSight(Point screen, double step) {
        if (Query is null || Bounds.Width < 1) { return; }
        var cam = MakeCamera();
        cam.Project(Query.X, Query.Y, Query.Z, out var depth);
        var (x, y, z) = cam.Unproject(screen, depth - step);
        var (cx, cy, cz) = ToFloats(x, y, z);
        QueryMoved?.Invoke(cx, cy, cz);
    }

    private void AddBoxEdges(StreamGeometryContext context, in Camera cam, float minX, float minY, float minZ, float maxX, float maxY, float maxZ) {
        for (var i = 0; i < 8; i++) {
            _corners[i] = cam.Project((i & 1) == 0 ? minX : maxX, (i & 2) == 0 ? minY : maxY, (i & 4) == 0 ? minZ : maxZ, out _);
        }
        for (var e = 0; e < Edges.Length; e += 2) {
            context.BeginFigure(_corners[Edges[e]], false);
            context.LineTo(_corners[Edges[e + 1]]);
            context.EndFigure(false);
        }
    }

    private static Geometry Wire(Action<StreamGeometryContext> draw) {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) { draw(context); }
        return geometry;
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var world = World;
        var query = Query;
        if (world is null || query is null || query.NodeVisited.Length != world.Nodes.Length || Bounds.Width < 1) { return; }
        var cam = MakeCamera();

        // The world's own box, so the volume reads as one.
        context.DrawGeometry(null, Frame, Wire(c => AddBoxEdges(c, cam, 0, 0, 0, BoxWorld.Width, BoxWorld.Height, BoxWorld.Depth3D)));

        // The tree: the leaves the walk reached, whose boxes were tested, and the subtrees it skipped without opening (a
        // parent was visited and it was not). The inner nodes between are left out, as they only crowd the picture.
        if (ShowTree) {
            var skipped = new StreamGeometry();
            var leaves = new StreamGeometry();
            using (var skippedContext = skipped.Open())
            using (var leafContext = leaves.Open()) {
                for (var i = 0; i < world.NodeCount; i++) {
                    if (!query.NodeVisited[i]) { continue; }
                    var node = world.Nodes[i];
                    if (node.Count > 0) {
                        AddBoxEdges(leafContext, cam, node.MinX, node.MinY, node.MinZ, node.MaxX, node.MaxY, node.MaxZ);
                        continue;
                    }
                    for (var side = 0; side < 2; side++) {
                        var child = side == 0 ? i + 1 : node.First;
                        if (query.NodeVisited[child]) { continue; }
                        var c = world.Nodes[child];
                        AddBoxEdges(skippedContext, cam, c.MinX, c.MinY, c.MinZ, c.MaxX, c.MaxY, c.MaxZ);
                    }
                }
            }
            context.DrawGeometry(null, Skipped, skipped);
            context.DrawGeometry(null, LeafVisited, leaves);
        }

        // Who the sphere found, by the method on show.
        var hits = UseTree ? query.TreeHits : query.ScanHits;
        var hitCount = Math.Max(0, UseTree ? query.TreeTotal : query.ScanTotal);
        var found = new bool[world.Count];
        for (var i = 0; i < hitCount; i++) { found[hits[i]] = true; }

        // The rest as wireframes, batched by pen: three steps of fading from near to far so depth reads.
        var resting = new StreamGeometry[3];
        var drifting = new StreamGeometry[3];
        var restingContexts = new StreamGeometryContext[3];
        var driftingContexts = new StreamGeometryContext[3];
        for (var s = 0; s < 3; s++) {
            resting[s] = new StreamGeometry(); restingContexts[s] = resting[s].Open();
            drifting[s] = new StreamGeometry(); driftingContexts[s] = drifting[s].Open();
        }
        var foundOrder = new List<(double depth, int index)>();
        for (var i = 0; i < world.Count; i++) {
            var box = world.Boxes[i];
            cam.Project((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, (box.MinZ + box.MaxZ) / 2, out var depth);
            if (found[i]) { foundOrder.Add((depth, i)); continue; }
            var step = Math.Clamp((int)((depth - (cam.Distance - cam.Reach)) / (2 * cam.Reach) * 3), 0, 2);
            AddBoxEdges((world.Moving[i] ? driftingContexts : restingContexts)[step], cam, box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
        }
        for (var s = 0; s < 3; s++) { restingContexts[s].Dispose(); driftingContexts[s].Dispose(); }
        // Far to near, so the nearer ones overpaint.
        for (var s = 2; s >= 0; s--) {
            context.DrawGeometry(null, Resting[s], resting[s]);
            context.DrawGeometry(null, Drifting[s], drifting[s]);
        }

        // Found boxes: faint faces and bright edges, farthest first.
        foundOrder.Sort((a, b) => b.depth.CompareTo(a.depth));
        foreach (var (_, i) in foundOrder) {
            var box = world.Boxes[i];
            for (var c = 0; c < 8; c++) {
                _corners[c] = cam.Project((c & 1) == 0 ? box.MinX : box.MaxX, (c & 2) == 0 ? box.MinY : box.MaxY, (c & 4) == 0 ? box.MinZ : box.MaxZ, out _);
            }
            var faces = new StreamGeometry();
            using (var faceContext = faces.Open()) {
                for (var f = 0; f < Faces.Length; f += 4) {
                    faceContext.BeginFigure(_corners[Faces[f]], true);
                    for (var v = 1; v < 4; v++) { faceContext.LineTo(_corners[Faces[f + v]]); }
                    faceContext.EndFigure(true);
                }
            }
            context.DrawGeometry(FoundFill, null, faces);
            context.DrawGeometry(null, Found, Wire(c => AddBoxEdges(c, cam, box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ)));
        }

        DrawSphere(context, cam, query);

        var nearest = UseTree ? query.TreeNearest : query.ScanNearest;
        var nearestCount = Math.Max(0, UseTree ? query.TreeNearestCount : query.ScanNearestCount);
        for (var rank = 0; rank < nearestCount; rank++) {
            var box = world.Boxes[nearest[rank]];
            var middle = cam.Project((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, (box.MinZ + box.MaxZ) / 2, out _);
            var text = new FormattedText((rank + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 13, LabelBrush);
            // On a dark tab, so the number reads over the wireframes behind it.
            var at = new Point(middle.X - text.Width / 2, middle.Y - text.Height / 2);
            context.DrawRectangle(LabelBack, null, new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
            context.DrawText(text, at);
        }
    }

    // The query sphere: its outline as the camera sees it, three rings round the axes to give it a shape, its center,
    // and a line down to the floor of the world so its height shows.
    private static void DrawSphere(DrawingContext context, in Camera cam, BoxQuery query) {
        double x = query.X, y = query.Y, z = query.Z, r = query.Radius;
        var middle = cam.Project(x, y, z, out var depth);
        if (depth > r * 1.05) {
            var radius = cam.Focal * r / Math.Sqrt(depth * depth - r * r);
            context.DrawEllipse(SphereFill, SphereEdge, middle, radius, radius);
        }
        for (var axis = 0; axis < 3; axis++) {
            var ring = new StreamGeometry();
            using (var ringContext = ring.Open()) {
                var open = false;
                for (var step = 0; step <= 48; step++) {
                    var angle = step * Math.PI * 2 / 48;
                    var (a, b) = (Math.Cos(angle) * r, Math.Sin(angle) * r);
                    double ringDepth;
                    var point = axis switch {
                        0 => cam.Project(x + a, y + b, z, out ringDepth),
                        1 => cam.Project(x + a, y, z + b, out ringDepth),
                        _ => cam.Project(x, y + a, z + b, out ringDepth),
                    };
                    // A huge sphere's ring can pass behind the camera; break the line there.
                    if (ringDepth < 1) { if (open) { ringContext.EndFigure(false); open = false; } continue; }
                    if (!open) { ringContext.BeginFigure(point, false); open = true; } else { ringContext.LineTo(point); }
                }
                if (open) { ringContext.EndFigure(false); }
            }
            context.DrawGeometry(null, SphereRing, ring);
        }
        var floor = cam.Project(x, BoxWorld.Height, z, out _);
        context.DrawLine(Plumb, middle, floor);
        context.DrawEllipse(null, Plumb, floor, 4, 2);
        context.DrawEllipse(CenterBrush, null, middle, 3, 3);
    }
}
