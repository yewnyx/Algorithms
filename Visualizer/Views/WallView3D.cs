using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;
using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// A parallel projection of a <see cref="WallWorld"/>: world metres to screen points, the camera turning about the middle
/// of the world. It keeps the slices' own orientation: looking straight down (elevation 90 degrees) x runs right and y
/// runs down the screen, as in the top-down slice; looking level from the high-y side (elevation 0, azimuth 0) x runs
/// right and z runs up, as in the side slice. Azimuth turns the camera about the vertical axis, elevation tilts it up.
/// </summary>
public readonly struct Projection {
    private readonly double _cx, _cy, _scale, _rx, _ry, _ux, _uy, _uz, _dx, _dy, _dz;

    public Projection(Size size, double azimuth, double elevation, double zoom) {
        var reach = Math.Sqrt(WallWorld.Width * WallWorld.Width + WallWorld.Height * WallWorld.Height + WallWorld.Depth * WallWorld.Depth) / 2;
        Reach = reach;
        _scale = Math.Max(0.01, Math.Min(size.Width, size.Height) / (2 * reach) * 0.94 * Math.Clamp(zoom, 0.3, 8));
        (_cx, _cy) = (size.Width / 2, size.Height / 2 + 8);
        double sinA = Math.Sin(azimuth), cosA = Math.Cos(azimuth), sinE = Math.Sin(elevation), cosE = Math.Cos(elevation);
        // The camera sits out from the world's middle along d, at (sin a, cos a) round the vertical axis and tilted up by e.
        (_dx, _dy, _dz) = (cosE * sinA, cosE * cosA, sinE);
        // Screen right, and screen up: straight up at elevation 0, and toward -y when looking down from azimuth 0.
        (_rx, _ry) = (cosA, -sinA);
        (_ux, _uy, _uz) = (-sinE * sinA, -sinE * cosA, cosE);
    }

    /// <summary>Screen pixels per metre.</summary>
    public double Scale => _scale;

    /// <summary>The distance from the world's middle to a corner, which bounds every depth.</summary>
    public double Reach { get; }

    /// <summary>Where a world point lands on the screen, and how far toward the camera it is (more is nearer), in metres from the world's middle.</summary>
    public Point Project(double x, double y, double z, out double depth) {
        x -= WallWorld.Width / 2; y -= WallWorld.Height / 2; z -= WallWorld.Depth / 2;
        depth = x * _dx + y * _dy + z * _dz;
        return new Point(_cx + (x * _rx + y * _ry) * _scale, _cy - (x * _ux + y * _uy + z * _uz) * _scale);
    }

    public Point Project(double x, double y, double z) => Project(x, y, z, out _);

    /// <summary>The screen vector a step of one metre along a world direction makes, in pixels.</summary>
    public Vector Direction(double dx, double dy, double dz) => new((dx * _rx + dy * _ry) * _scale, -(dx * _ux + dy * _uy + dz * _uz) * _scale);
}

/// <summary>
/// A <see cref="WallWorld"/> seen from an orbiting camera, in plain 2D drawing so it runs in the browser too: the solids as
/// wireframes, the two slices as coloured planes where they cut (each cell in the colour the slice gives it), the probe as
/// a point with its two spheres (what the grid alone proves free, and the exact distance). It starts at the isometric angle.
/// Drag to orbit, the wheel to zoom. Picking stays with the slices.
/// </summary>
public sealed class WallView3D : Control {
    /// <summary>The camera the view starts at and goes back to: from above the high-y low-x corner, the three axes equally foreshortened.</summary>
    public const double IsometricAzimuth = -Math.PI / 4;
    public const double IsometricElevation = 0.6154797086703874; // atan(1 / sqrt 2)

    private const double PlaneOpacity = 0.74;

    private static readonly IBrush Background = new SolidColorBrush(Palette.ToColor(Palette.Background));
    private static readonly IBrush Silver = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x53, 0x62)), 1);
    private static readonly IPen[] Solids = Fade(Color.FromRgb(0xF2, 0xF5, 0xFA), 0xF0, 0xB0, 0x70);
    private static readonly IPen Section = new Pen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), 1.5);
    private static readonly IBrush RoomFill = new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0xFC, 0x9A));
    private static readonly IPen RoomEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)), 2);
    private static readonly IPen RoomRing = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x7C, 0xFC, 0x9A)), 1);
    private static readonly IPen ExactEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)), 1.5, DashStyle.Dash);
    private static readonly IPen ExactRing = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD5, 0x4F)), 1, DashStyle.Dash);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x17, 0x1D));
    private static readonly IBrush ProbeDot = new SolidColorBrush(Colors.White);

    // Corner i of a box has x high if bit 0 is set, y high if bit 1, z high if bit 2.
    private static readonly int[] Edges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];

    private const int RingSteps = 40;

    private List<(double U, double V)[]>? _topOutlines, _sideOutlines;
    private (WallWorld? world, float at) _topFor, _sideFor;
    private bool _dragging;
    private Point _last;

    public WallWorld? World { get; set; }

    /// <summary>The probe, in metres, and what the grid and the shapes say about it (room, exact); either is NaN if unknown.</summary>
    public float ProbeX { get; set; }
    public float ProbeY { get; set; }
    public float ProbeZ { get; set; }
    public float Room { get; set; } = float.NaN;
    public float Exact { get; set; } = float.NaN;

    public bool ShowSolids { get; set; } = true;

    /// <summary>The colours that mark each slice's plane and its title in the 2D view.</summary>
    public Color TopAccent { get; set; } = Color.FromRgb(0x80, 0xD8, 0xFF);
    public Color SideAccent { get; set; } = Color.FromRgb(0xCE, 0x93, 0xD8);

    public double Azimuth { get; set; } = IsometricAzimuth;
    public double Elevation { get; set; } = IsometricElevation;
    public double Zoom { get; set; } = 1;

    /// <summary>Called after everything else is drawn, with the projection, for whatever else is to be drawn in the world.</summary>
    public Action<DrawingContext, Projection>? Overlay { get; set; }

    public WallView3D() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The camera as it now stands.</summary>
    public Projection Camera() => new(Bounds.Size, Azimuth, Elevation, Zoom);

    public void ResetCamera() {
        (Azimuth, Elevation, Zoom) = (IsometricAzimuth, IsometricElevation, 1);
        InvalidateVisual();
    }

    /// <summary>Turns the camera about the world, as dragging does.</summary>
    public void Orbit(double azimuth, double elevation) {
        Azimuth += azimuth;
        Elevation = Math.Clamp(Elevation + elevation, -Math.PI / 2, Math.PI / 2);
        InvalidateVisual();
    }

    private static IPen[] Fade(Color color, byte near, byte middle, byte far) => [
        new Pen(new SolidColorBrush(Color.FromArgb(near, color.R, color.G, color.B)), 1.2),
        new Pen(new SolidColorBrush(Color.FromArgb(middle, color.R, color.G, color.B)), 1),
        new Pen(new SolidColorBrush(Color.FromArgb(far, color.R, color.G, color.B)), 1),
    ];

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
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (!_dragging) { return; }
        var p = e.GetPosition(this);
        // Dragging right brings the far side round to the right, as turning the world by hand does.
        Orbit(-(p.X - _last.X) * 0.01, (p.Y - _last.Y) * 0.01);
        _last = p;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        Zoom = Math.Clamp(Zoom * Math.Pow(1.1, e.Delta.Y), 0.3, 8);
        InvalidateVisual();
        e.Handled = true;
    }

    private static Geometry Lines(Action<StreamGeometryContext> draw) {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) { draw(context); }
        return geometry;
    }

    private static void Line(StreamGeometryContext g, Point a, Point b) {
        g.BeginFigure(a, false);
        g.LineTo(b);
        g.EndFigure(false);
    }

    private static void Polygon(StreamGeometryContext g, ReadOnlySpan<Point> points, bool closed) {
        if (points.Length < 2) { return; }
        g.BeginFigure(points[0], false);
        for (var i = 1; i < points.Length; i++) { g.LineTo(points[i]); }
        g.EndFigure(closed);
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var world = World;
        if (world is null || Bounds.Width < 1 || Bounds.Height < 1) { return; }
        var cam = Camera();
        DrawText(context, "3D: drag to orbit, wheel to zoom", new Point(10, 5), 12, Silver);

        // The world's own box.
        Span<Point> corners = stackalloc Point[8];
        for (var i = 0; i < 8; i++) {
            corners[i] = cam.Project((i & 1) == 0 ? 0 : WallWorld.Width, (i & 2) == 0 ? 0 : WallWorld.Height, (i & 4) == 0 ? 0 : WallWorld.Depth);
        }
        var box = new StreamGeometry();
        using (var g = box.Open()) {
            for (var e = 0; e < Edges.Length; e += 2) { Line(g, corners[Edges[e]], corners[Edges[e + 1]]); }
        }
        context.DrawGeometry(null, Frame, box);

        // The two slices where they cut, the one farther from the camera first.
        float z = WallWorld.Clamp(ProbeZ, WallWorld.Depth), y = WallWorld.Clamp(ProbeY, WallWorld.Height);
        cam.Project(WallWorld.Width / 2, WallWorld.Height / 2, z, out var topDepth);
        cam.Project(WallWorld.Width / 2, y, WallWorld.Depth / 2, out var sideDepth);
        var brushes = SliceColours.Brushes(world);
        var labels = new (string, Point, Color)[2];
        if (topDepth <= sideDepth) {
            labels[0] = DrawPlane(context, cam, world, true, z, brushes, TopAccent);
            labels[1] = DrawPlane(context, cam, world, false, y, brushes, SideAccent);
        } else {
            labels[1] = DrawPlane(context, cam, world, false, y, brushes, SideAccent);
            labels[0] = DrawPlane(context, cam, world, true, z, brushes, TopAccent);
        }

        if (ShowSolids) { DrawSolids(context, cam, world); }

        // The probe: a point, and the two spheres round it.
        var at = cam.Project(ProbeX, ProbeY, ProbeZ);
        if (!float.IsNaN(Exact) && Exact < 100) { DrawBall(context, cam, Exact, at, null, ExactEdge, ExactRing); }
        if (!float.IsNaN(Room)) { DrawBall(context, cam, Room, at, RoomFill, RoomEdge, RoomRing); }
        context.DrawEllipse(ProbeDot, null, at, 3.5, 3.5);

        // The planes are named last, on a dark ground so the name reads over whatever lies behind it.
        foreach (var (name, at0, color) in labels) {
            var text = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, new SolidColorBrush(color));
            context.DrawRectangle(LabelBack, null, new Rect(at0.X - 3, at0.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
            context.DrawText(text, at0);
        }

        Overlay?.Invoke(context, cam);
        DrawAxes(context, cam);
    }

    // A slice as a plane: each cell of the layer or row it cuts in the colour the slice gives it, a run of equal cells as one
    // quadrilateral, the whole drawn opaque and then laid on the picture at one opacity so no seam shows between cells.
    private (string, Point, Color) DrawPlane(DrawingContext context, in Projection cam, WallWorld world, bool top, float at, IBrush[] brushes, Color accent) {
        int columns = WallWorld.CountX, rows = top ? WallWorld.CountY : WallWorld.CountZ;
        var fixedCell = WallWorld.CellOf(at, top ? WallWorld.CountZ : WallWorld.CountY);
        // The grid's lattice points on the plane, row by row.
        var lattice = new Point[(columns + 1) * (rows + 1)];
        for (var r = 0; r <= rows; r++) {
            for (var c = 0; c <= columns; c++) {
                double u = c * WallWorld.CellSize, v = r * WallWorld.CellSize;
                lattice[r * (columns + 1) + c] = top ? cam.Project(u, v, at) : cam.Project(u, at, v);
            }
        }
        var geometries = new StreamGeometry?[brushes.Length];
        var contexts = new StreamGeometryContext?[brushes.Length];
        for (var r = 0; r < rows; r++) {
            var c = 0;
            while (c < columns) {
                var key = top ? SliceColours.KeyAt(world, c, r, fixedCell) : SliceColours.KeyAt(world, c, fixedCell, r);
                var end = c + 1;
                while (end < columns && (top ? SliceColours.KeyAt(world, end, r, fixedCell) : SliceColours.KeyAt(world, end, fixedCell, r)) == key) { end++; }
                var g = contexts[key];
                if (g is null) {
                    geometries[key] = new StreamGeometry();
                    g = contexts[key] = geometries[key]!.Open();
                }
                g.BeginFigure(lattice[r * (columns + 1) + c], true);
                g.LineTo(lattice[r * (columns + 1) + end]);
                g.LineTo(lattice[(r + 1) * (columns + 1) + end]);
                g.LineTo(lattice[(r + 1) * (columns + 1) + c]);
                g.EndFigure(true);
                c = end;
            }
        }
        using (context.PushOpacity(PlaneOpacity)) {
            for (var key = 0; key < brushes.Length; key++) {
                contexts[key]?.Dispose();
                if (geometries[key] is { } geometry) { context.DrawGeometry(brushes[key], null, geometry); }
            }
        }
        var edge = new Pen(new SolidColorBrush(accent), 1.8);
        Point[] ring = [lattice[0], lattice[columns], lattice[rows * (columns + 1) + columns], lattice[rows * (columns + 1)]];
        context.DrawGeometry(null, edge, Lines(g => Polygon(g, ring, true)));
        var label = top ? $"top-down slice, z = {at:0.00} m" : $"side slice, y = {at:0.00} m";
        var corner = lattice[rows * (columns + 1)];
        var named = (label, new Point(corner.X + 6, corner.Y + 4), accent);

        // Where the plane cuts each solid, as the slice draws it.
        List<(double U, double V)[]> outlines;
        if (top) {
            if (_topOutlines is null || _topFor != (world, at)) { (_topOutlines, _topFor) = (world.Sections(SlicePlane.Xy, at), (world, at)); }
            outlines = _topOutlines;
        } else {
            if (_sideOutlines is null || _sideFor != (world, at)) { (_sideOutlines, _sideFor) = (world.Sections(SlicePlane.Xz, at), (world, at)); }
            outlines = _sideOutlines;
        }
        if (!ShowSolids) { return named; }
        foreach (var outline in outlines) {
            var points = new Point[outline.Length];
            for (var i = 0; i < outline.Length; i++) { points[i] = top ? cam.Project(outline[i].U, outline[i].V, at) : cam.Project(outline[i].U, at, outline[i].V); }
            context.DrawGeometry(null, Section, Lines(g => Polygon(g, points, true)));
        }
        return named;
    }

    // The solids as wireframes, batched in three steps of fading from near to far so depth reads.
    private void DrawSolids(DrawingContext context, in Projection cam, WallWorld world) {
        var geometries = new StreamGeometry[3];
        var contexts = new StreamGeometryContext[3];
        for (var s = 0; s < 3; s++) { geometries[s] = new StreamGeometry(); contexts[s] = geometries[s].Open(); }
        Span<Point> corners = stackalloc Point[8];
        foreach (var solid in world.Solids) {
            cam.Project(solid.X, solid.Y, solid.Z, out var depth);
            var t = Math.Clamp((depth + cam.Reach) / (2 * cam.Reach), 0, 1);
            var g = contexts[Math.Clamp(2 - (int)(t * 3), 0, 2)];
            switch (solid.Kind) {
                case SolidKind.Box:
                    for (var i = 0; i < 8; i++) {
                        double a = (i & 1) == 0 ? -solid.HalfU : solid.HalfU, b = (i & 2) == 0 ? -solid.HalfV : solid.HalfV, c = (i & 4) == 0 ? -solid.HalfW : solid.HalfW;
                        corners[i] = cam.Project(solid.X + a * solid.UX + b * solid.VX + c * solid.WX, solid.Y + a * solid.UY + b * solid.VY + c * solid.WY, solid.Z + a * solid.UZ + b * solid.VZ + c * solid.WZ);
                    }
                    for (var e = 0; e < Edges.Length; e += 2) { Line(g, corners[Edges[e]], corners[Edges[e + 1]]); }
                    break;
                case SolidKind.Sphere:
                    AddBall(g, cam, solid.X, solid.Y, solid.Z, solid.HalfU);
                    break;
                case SolidKind.Capsule:
                    AddCapsule(g, cam, solid);
                    break;
            }
        }
        for (var s = 0; s < 3; s++) { contexts[s].Dispose(); }
        for (var s = 2; s >= 0; s--) { context.DrawGeometry(null, Solids[s], geometries[s]); }
    }

    // A sphere: its outline, a true circle under a parallel projection, and three rings round the axes to give it a shape.
    private static void AddBall(StreamGeometryContext g, in Projection cam, double x, double y, double z, double r) {
        var middle = cam.Project(x, y, z);
        Span<Point> ring = stackalloc Point[RingSteps + 1];
        var radius = r * cam.Scale;
        for (var i = 0; i <= RingSteps; i++) {
            var angle = i * Math.PI * 2 / RingSteps;
            ring[i] = new Point(middle.X + radius * Math.Cos(angle), middle.Y + radius * Math.Sin(angle));
        }
        Polygon(g, ring, true);
        for (var axis = 0; axis < 3; axis++) {
            for (var i = 0; i <= RingSteps; i++) {
                var angle = i * Math.PI * 2 / RingSteps;
                double a = Math.Cos(angle) * r, b = Math.Sin(angle) * r;
                ring[i] = axis switch {
                    0 => cam.Project(x + a, y + b, z),
                    1 => cam.Project(x + a, y, z + b),
                    _ => cam.Project(x, y + a, z + b),
                };
            }
            Polygon(g, ring, true);
        }
    }

    // A capsule: a circle at each end, the two lines that join them on the outline, and a ring about the axis at each end.
    private static void AddCapsule(StreamGeometryContext g, in Projection cam, in Solid s) {
        double ax = s.X, ay = s.Y, az = s.Z, bx = s.UX, by = s.UY, bz = s.UZ, r = s.HalfU;
        double wx = bx - ax, wy = by - ay, wz = bz - az, length = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        var a = cam.Project(ax, ay, az);
        var b = cam.Project(bx, by, bz);
        var radius = r * cam.Scale;
        Span<Point> ring = stackalloc Point[RingSteps + 1];
        foreach (var end in new[] { a, b }) {
            for (var i = 0; i <= RingSteps; i++) {
                var angle = i * Math.PI * 2 / RingSteps;
                ring[i] = new Point(end.X + radius * Math.Cos(angle), end.Y + radius * Math.Sin(angle));
            }
            Polygon(g, ring, true);
        }
        double sx = b.X - a.X, sy = b.Y - a.Y, screen = Math.Sqrt(sx * sx + sy * sy);
        if (screen > 0.5) {
            double nx = -sy / screen * radius, ny = sx / screen * radius;
            Line(g, new Point(a.X + nx, a.Y + ny), new Point(b.X + nx, b.Y + ny));
            Line(g, new Point(a.X - nx, a.Y - ny), new Point(b.X - nx, b.Y - ny));
        }
        if (length < 1e-9) { return; }
        // Two directions across the axis, to draw its rings with.
        double axX = wx / length, axY = wy / length, axZ = wz / length;
        var (hx, hy, hz) = Math.Abs(axZ) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        double ux = axY * hz - axZ * hy, uy = axZ * hx - axX * hz, uz = axX * hy - axY * hx, un = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        (ux, uy, uz) = (ux / un, uy / un, uz / un);
        double vx = axY * uz - axZ * uy, vy = axZ * ux - axX * uz, vz = axX * uy - axY * ux;
        foreach (var (px, py, pz) in new[] { (ax, ay, az), (bx, by, bz) }) {
            for (var i = 0; i <= RingSteps; i++) {
                var angle = i * Math.PI * 2 / RingSteps;
                double c = Math.Cos(angle) * r, d = Math.Sin(angle) * r;
                ring[i] = cam.Project(px + c * ux + d * vx, py + c * uy + d * vy, pz + c * uz + d * vz);
            }
            Polygon(g, ring, true);
        }
    }

    // A translucent ball about a point of the picture: its outline and three rings.
    private static void DrawBall(DrawingContext context, in Projection cam, double r, Point middle, IBrush? fill, IPen edge, IPen rings) {
        context.DrawEllipse(fill, edge, middle, r * cam.Scale, r * cam.Scale);
        var ball = new StreamGeometry();
        using (var g = ball.Open()) {
            Span<Point> ring = stackalloc Point[RingSteps + 1];
            // The rings are about the probe, which is where the middle was projected from: undo that by working from its own point.
            for (var axis = 0; axis < 3; axis++) {
                for (var i = 0; i <= RingSteps; i++) {
                    var angle = i * Math.PI * 2 / RingSteps;
                    double a = Math.Cos(angle) * r, b = Math.Sin(angle) * r;
                    var d = axis switch { 0 => cam.Direction(a, b, 0), 1 => cam.Direction(a, 0, b), _ => cam.Direction(0, a, b) };
                    ring[i] = new Point(middle.X + d.X, middle.Y + d.Y);
                }
                Polygon(g, ring, true);
            }
        }
        context.DrawGeometry(null, rings, ball);
    }

    // The three axes in a corner of the view, so the orientation reads whatever the angle.
    private void DrawAxes(DrawingContext context, in Projection cam) {
        var origin = new Point(44, Bounds.Height - 56);
        foreach (var (name, dx, dy, dz, color) in new[] { ("x", 1.0, 0.0, 0.0, 0xEF5350), ("y", 0.0, 1.0, 0.0, 0x66BB6A), ("z", 0.0, 0.0, 1.0, 0x42A5F5) }) {
            var v = cam.Direction(dx, dy, dz);
            var length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            // One metre on the screen is a few pixels, so give every axis the same reach and keep its foreshortening.
            var shown = new Vector(v.X / cam.Scale * 30, v.Y / cam.Scale * 30);
            _ = length;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
            var tip = new Point(origin.X + shown.X, origin.Y + shown.Y);
            context.DrawLine(new Pen(brush, 2), origin, tip);
            var out_ = Math.Sqrt(shown.X * shown.X + shown.Y * shown.Y);
            var (ox, oy) = out_ > 1 ? (shown.X / out_ * 8, shown.Y / out_ * 8) : (0.0, -8.0);
            DrawText(context, name, new Point(tip.X + ox - 3, tip.Y + oy - 8), 12, brush);
        }
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush) {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawText(formatted, at);
    }
}
