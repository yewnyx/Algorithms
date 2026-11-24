using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// One slice through a <see cref="WallWorld"/>. Coloured by wall distance, each cell shows the count the grid holds for it
/// (hot near a shape, cool far, flat at the cap); coloured by regions, each cell shows which region of a size class the
/// seeds' flood reached, with the cells open but unreached dim and the cells too near a shape dark. The solids are pale
/// shapes cut from their own numbers. The probe has two circles in wall-distance colour (what the grid alone proves is
/// free, and what the shapes say is) and, in region colour, its reach and the nearest open cell. Pressing and dragging
/// puts the probe there; Shift or the right button puts the anchor, whose region the nearest-open query can be held to, or
/// where a travel field starts. Coloured by travel, each cell shows how far it is from the goal going round what is in the way,
/// with short strokes downhill on a coarse lattice and the path the field leads the probe along. With the wall distance
/// kept as a tree, the tree's open cubes and bricks are outlined where the slice passes through them.
/// The x-by-y slice (looking down) also draws its legend; the x-by-z slice (from the side, z up) draws a line where the
/// other slice cuts.
/// </summary>
public sealed class WallSliceView : Control {
    private const double TopInset = 24, Inset = 10;

    private static readonly IBrush Background = new SolidColorBrush(Palette.ToColor(Palette.Background));
    private static readonly IBrush Silver = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly IBrush SolidFill = new SolidColorBrush(Color.FromArgb(0x78, 0xF2, 0xF5, 0xFA));
    private static readonly IPen SolidEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush SeedFill = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD5, 0x4F));
    private static readonly IPen SeedEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76)), 2);
    private static readonly IPen CutLine = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1, DashStyle.Dash);
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)), 1);
    private static readonly IBrush RoomFill = new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0xFC, 0x9A));
    private static readonly IPen RoomEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)), 2);
    private static readonly IPen ExactEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)), 1.5, DashStyle.Dash);
    private static readonly IPen ReachEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.5, DashStyle.Dash);
    private static readonly IPen NearestLine = new Pen(new SolidColorBrush(Colors.White), 1.5);
    private static readonly IPen NearestOff = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1.5, DashStyle.Dot);
    private static readonly IPen AnchorEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0xAB)), 2);
    private static readonly IBrush ProbeDot = new SolidColorBrush(Colors.White);
    private static readonly IPen CostEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)), 2);
    private static readonly IBrush CostFill = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0x98, 0x00));
    private static readonly IPen Stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.4);
    private static readonly IPen PathNear = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x3B)), 3);
    private static readonly IPen PathFar = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xEB, 0x3B)), 2, DashStyle.Dot);
    // The route before it is pulled straight (blue, a dot at every cell centre it steps through) and after (yellow). Thinner and dashed where it is above or below the slice (it is drawn flat, as seen from the slice), dotted where the library does not prove the leg.
    private static readonly IPen CellsNear = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 2);
    private static readonly IPen CellsFar = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x4F, 0xC3, 0xF7)), 1.5, DashStyle.Dash);
    private static readonly IPen CellsDash = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 2, DashStyle.Dot);
    private static readonly IBrush CellsDot = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly IPen RouteNear = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x3B)), 3.5);
    private static readonly IPen RouteFar = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xEB, 0x3B)), 2.5, DashStyle.Dash);
    private static readonly IPen RouteDash = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x3B)), 3.5, DashStyle.Dot);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC));
    // The tree's leaves: an open cube, all at the cap, and a brick that keeps a count for each of its cells.
    private static readonly IPen OpenEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1.2);
    private static readonly IPen BrickEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x13)), 1.6);

    private List<(double U, double V)[]>? _costOutlines, _goalOutlines;
    private List<(double U, double V)[]>? _outlines, _seedOutlines;
    private (WallWorld? world, float at) _outlinesFor;
    private bool _pressed;

    public WallWorld? World { get; set; }
    public SlicePlane Plane { get; set; }
    public SliceColour Colour { get; set; }

    /// <summary>The probe, in metres, and what the grid and the shapes say about it (room, exact); either is NaN if unknown.</summary>
    public float ProbeX { get; set; }
    public float ProbeY { get; set; }
    public float ProbeZ { get; set; }
    public float Room { get; set; } = float.NaN;
    public float Exact { get; set; } = float.NaN;

    /// <summary>For region colour: how far the nearest-open query looks, the cell it found if it found one, and the anchor if one is held to.</summary>
    public float Reach { get; set; }
    public bool HasNearest { get; set; }
    public float NearestX { get; set; }
    public float NearestY { get; set; }
    public float NearestZ { get; set; }
    public bool HasAnchor { get; set; }
    public float AnchorX { get; set; }
    public float AnchorY { get; set; }
    public float AnchorZ { get; set; }

    /// <summary>For travel colour: the path the field leads down from the probe, and whether to draw the strokes.</summary>
    public List<(float x, float y, float z)>? Path { get; set; }
    public bool ShowStrokes { get; set; } = true;

    /// <summary>
    /// For route colour: a route as the points it leads through, the start first. <see cref="DashFirst"/> and <see cref="DashLast"/> say that
    /// the first or last leg is one the library does not prove clear; <see cref="Dots"/> marks each point.
    /// </summary>
    public sealed record RouteLine(List<(float x, float y, float z)> Points, bool DashFirst, bool DashLast, bool Dots);

    public RouteLine? CellsWay { get; set; }
    public RouteLine? StraightWay { get; set; }

    /// <summary>The colour of this slice's frame and title, to match its plane in the 3D view; none for the plain look.</summary>
    public Color? Accent { get; set; }

    public bool ShowSolids { get; set; } = true;
    public bool ShowLegend { get; set; }

    /// <summary>The probe put somewhere on this slice: (x, y) on the top-down slice, (x, z) on the side slice.</summary>
    public event Action<float, float>? ProbeMoved;

    /// <summary>The anchor put somewhere on this slice, the same two coordinates.</summary>
    public event Action<float, float>? AnchorMoved;

    /// <summary>The wheel turned: +1 for a step up, -1 for down.</summary>
    public event Action<int>? WheelStepped;

    public WallSliceView() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private double PlaneHeight => Plane == SlicePlane.Xy ? WallWorld.Height : WallWorld.Depth;
    private float Fixed => Plane == SlicePlane.Xy ? ProbeZ : ProbeY;

    private (double scale, double ox, double oy) Geometry() {
        var bottom = ShowLegend ? (Colour == SliceColour.Routes ? 46.0 : 30.0) : Inset;
        var scale = Math.Max(0.01, Math.Min((Bounds.Width - 2 * Inset) / WallWorld.Width, (Bounds.Height - TopInset - bottom) / PlaneHeight));
        var ox = (Bounds.Width - scale * WallWorld.Width) / 2;
        var oy = TopInset + (Bounds.Height - TopInset - bottom - scale * PlaneHeight) / 2;
        return (scale, ox, oy);
    }

    // The slice's own two coordinates to pixels and back; the side slice has z up.
    private Point ToPixel(double u, double v, double scale, double ox, double oy) =>
        new(ox + u * scale, Plane == SlicePlane.Xy ? oy + v * scale : oy + (PlaneHeight - v) * scale);

    private (float u, float v) FromPixel(Point p, double scale, double ox, double oy) {
        var u = (p.X - ox) / scale;
        var v = Plane == SlicePlane.Xy ? (p.Y - oy) / scale : PlaneHeight - (p.Y - oy) / scale;
        return ((float)Math.Clamp(u, 0, WallWorld.Width), (float)Math.Clamp(v, 0, PlaneHeight));
    }

    // A point's place on this slice and how far off the slice's plane it is.
    private (double u, double v, double off) OnSlice(float x, float y, float z) =>
        Plane == SlicePlane.Xy ? (x, y, z - ProbeZ) : (x, z, y - ProbeY);

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this).Properties;
        if (!point.IsLeftButtonPressed && !point.IsRightButtonPressed) { return; }
        _pressed = true;
        e.Pointer.Capture(this);
        Move(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (_pressed) { Move(e); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        _pressed = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0) { WheelStepped?.Invoke(e.Delta.Y > 0 ? 1 : -1); }
        e.Handled = true;
    }

    private void Move(PointerEventArgs e) {
        var (scale, ox, oy) = Geometry();
        var (u, v) = FromPixel(e.GetPosition(this), scale, ox, oy);
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed || (e.KeyModifiers & KeyModifiers.Shift) != 0) { AnchorMoved?.Invoke(u, v); } else { ProbeMoved?.Invoke(u, v); }
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var world = World;
        if (world is null) { return; }
        var (scale, ox, oy) = Geometry();
        var cell = WallWorld.CellSize * scale;
        var routes = Colour == SliceColour.Routes;
        var regions = Colour == SliceColour.Regions || routes;
        var travel = Colour == SliceColour.Travel;
        var brushes = SliceColours.Brushes(world, Colour);

        // The layer the slice cuts, or for the side slice the row of cells holding the probe's y.
        var rows = Plane == SlicePlane.Xy ? WallWorld.CountY : WallWorld.CountZ;
        var fixedCell = WallWorld.CellOf(Fixed, Plane == SlicePlane.Xy ? WallWorld.CountZ : WallWorld.CountY);
        if (fixedCell < 0) { return; }

        var title = Plane == SlicePlane.Xy
            ? $"Looking down at z = {ProbeZ:0.00} m (layer {fixedCell} of {WallWorld.CountZ})"
            : $"From the side at y = {ProbeY:0.00} m; dashed: the top-down slice";
        DrawText(context, title, new Point(Inset, 5), 12, Accent is { } accent ? new SolidColorBrush(accent) : TextBrush);

        // Cells with the same key share one geometry, so a slice is a few dozen draws, not thousands.
        var shapes = new StreamGeometry?[brushes.Length];
        var contexts = new StreamGeometryContext?[brushes.Length];
        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < WallWorld.CountX; column++) {
                var (cx, cy, cz) = Plane == SlicePlane.Xy ? (column, row, fixedCell) : (column, fixedCell, row);
                var key = SliceColours.KeyAt(world, Colour, cx, cy, cz);
                var g = contexts[key];
                if (g is null) {
                    shapes[key] = new StreamGeometry();
                    g = contexts[key] = shapes[key]!.Open();
                }
                var top = Plane == SlicePlane.Xy ? oy + row * cell : oy + (rows - 1 - row) * cell;
                AddRect(g, ox + column * cell, top, cell, cell);
            }
        }
        for (var key = 0; key < brushes.Length; key++) {
            contexts[key]?.Dispose();
            if (shapes[key] is { } shape) { context.DrawGeometry(brushes[key], null, shape); }
        }
        context.DrawRectangle(null, Accent is { } edge ? new Pen(new SolidColorBrush(edge), 1.8) : Frame, new Rect(ox, oy, WallWorld.Width * scale, PlaneHeight * scale));

        if (world.AsTree && Colour == SliceColour.WallDistance) { DrawLeaves(context, world, fixedCell, scale, ox, oy); }
        DrawSolids(context, world, regions, travel, routes, scale, ox, oy);
        if (travel && ShowStrokes) { DrawStrokes(context, world, scale, ox, oy); }
        if (travel && Path is { Count: > 1 } path) { DrawPath(context, path, scale, ox, oy); }
        if (routes) {
            if (CellsWay is { } cells) { DrawRoute(context, cells, CellsNear, CellsFar, CellsDash, scale, ox, oy); }
            if (StraightWay is { } straight) { DrawRoute(context, straight, RouteNear, RouteFar, RouteDash, scale, ox, oy); }
        }

        if (Plane == SlicePlane.Xz) {
            var cut = ToPixel(0, ProbeZ, scale, ox, oy);
            context.DrawLine(CutLine, cut, new Point(ox + WallWorld.Width * scale, cut.Y));
        }

        // The probe is on this slice, at its own height or row, so its circles are true circles here.
        var at = ToPixel(ProbeX, Plane == SlicePlane.Xy ? ProbeY : ProbeZ, scale, ox, oy);
        if (travel || routes) {
            // The anchor is where a point goal sits, and where a route ends.
            if (HasAnchor) { DrawAnchor(context, scale, ox, oy); }
        } else if (regions) {
            context.DrawEllipse(null, ReachEdge, at, Reach * scale, Reach * scale);
            if (HasAnchor) { DrawAnchor(context, scale, ox, oy); }
            if (HasNearest) { DrawNearest(context, at, scale, ox, oy); }
        } else {
            if (!float.IsNaN(Room)) { context.DrawEllipse(RoomFill, RoomEdge, at, Room * scale, Room * scale); }
            if (!float.IsNaN(Exact)) { context.DrawEllipse(null, ExactEdge, at, Exact * scale, Exact * scale); }
        }
        context.DrawEllipse(ProbeDot, null, at, 3, 3);

        if (ShowLegend) { DrawLegend(context, world, regions, travel, routes, ox, oy + PlaneHeight * scale + 6, WallWorld.Width * scale); }
    }

    // The tree's open cubes (white) and bricks (grey) that this slice's layer or row of cells passes through, cut at the grid's edge.
    private void DrawLeaves(DrawingContext context, WallWorld world, int fixedCell, double scale, double ox, double oy) {
        var cell = WallWorld.CellSize * scale;
        var rows = Plane == SlicePlane.Xy ? WallWorld.CountY : WallWorld.CountZ;
        var open = new StreamGeometry();
        var bricks = new StreamGeometry();
        using (var openContext = open.Open())
        using (var brickContext = bricks.Open()) {
            foreach (var leaf in world.Leaves) {
                var (across, start) = Plane == SlicePlane.Xy ? (leaf.Z, leaf.Y) : (leaf.Y, leaf.Z);
                if (fixedCell < across || fixedCell >= across + leaf.Side) { continue; }
                int right = Math.Min(leaf.X + leaf.Side, WallWorld.CountX), end = Math.Min(start + leaf.Side, rows);
                double left = ox + leaf.X * cell, width = (right - leaf.X) * cell, height = (end - start) * cell;
                var top = Plane == SlicePlane.Xy ? oy + start * cell : oy + (rows - end) * cell;
                AddRect(leaf.Open ? openContext : brickContext, left, top, width, height);
            }
        }
        context.DrawGeometry(null, BrickEdge, bricks);
        context.DrawGeometry(null, OpenEdge, open);
    }

    // Short strokes downhill on a coarse lattice: from the middle of every other cell, a line along the field's direction as far as it lies in this plane.
    private void DrawStrokes(DrawingContext context, WallWorld world, double scale, double ox, double oy) {
        var rows = Plane == SlicePlane.Xy ? WallWorld.CountY : WallWorld.CountZ;
        var geometry = new StreamGeometry();
        var any = false;
        using (var g = geometry.Open()) {
            for (var row = 1; row < rows; row += 2) {
                for (var column = 1; column < WallWorld.CountX; column += 2) {
                    double u = (column + 0.5) * WallWorld.CellSize, v = (row + 0.5) * WallWorld.CellSize;
                    var (x, y, z) = Plane == SlicePlane.Xy ? ((float)u, (float)v, ProbeZ) : ((float)u, ProbeY, (float)v);
                    if (!world.SampleField(x, y, z, out var dx, out var dy, out var dz, out _)) { continue; }
                    var (du, dv) = Plane == SlicePlane.Xy ? (dx, dy) : (dx, dz);
                    var along = Math.Sqrt(du * du + dv * dv);
                    // A direction mostly across this plane has no stroke to show on it.
                    if (along < 0.2) { continue; }
                    var length = WallWorld.CellSize * 1.4 * along;
                    double tipU = u + du / along * length, tipV = v + dv / along * length;
                    var tail = ToPixel(u, v, scale, ox, oy);
                    var tip = ToPixel(tipU, tipV, scale, ox, oy);
                    g.BeginFigure(tail, false);
                    g.LineTo(tip);
                    // The head: two short strokes back from the tip.
                    double bx = tail.X - tip.X, by = tail.Y - tip.Y, back = Math.Sqrt(bx * bx + by * by);
                    if (back > 0.5) {
                        double ex = bx / back * 4, ey = by / back * 4;
                        g.LineTo(new Point(tip.X + ex * 0.8 - ey * 0.6, tip.Y + ey * 0.8 + ex * 0.6));
                        g.BeginFigure(tip, false);
                        g.LineTo(new Point(tip.X + ex * 0.8 + ey * 0.6, tip.Y + ey * 0.8 - ex * 0.6));
                    }
                    g.EndFigure(false);
                    any = true;
                }
            }
        }
        if (any) { context.DrawGeometry(null, Stroke, geometry); }
    }

    // A route as a line on this slice: bold where it runs near the slice, thin and dashed where it is above or below it (flattened onto the slice), dotted where the library does not prove the leg.
    private void DrawRoute(DrawingContext context, RouteLine line, IPen near, IPen far, IPen dashed, double scale, double ox, double oy) {
        var points = line.Points;
        for (var i = 1; i < points.Count; i++) {
            var (au, av, aoff) = OnSlice(points[i - 1].x, points[i - 1].y, points[i - 1].z);
            var (bu, bv, boff) = OnSlice(points[i].x, points[i].y, points[i].z);
            var onSlice = Math.Abs(aoff) < 1.0 && Math.Abs(boff) < 1.0;
            var unproven = (i == 1 && line.DashFirst) || (i == points.Count - 1 && line.DashLast);
            context.DrawLine(!onSlice ? far : unproven ? dashed : near, ToPixel(au, av, scale, ox, oy), ToPixel(bu, bv, scale, ox, oy));
        }
        if (!line.Dots) { return; }
        for (var i = 1; i < points.Count; i++) {
            var (u, v, off) = OnSlice(points[i].x, points[i].y, points[i].z);
            if (Math.Abs(off) < 1.0) { context.DrawEllipse(CellsDot, null, ToPixel(u, v, scale, ox, oy), 2.5, 2.5); }
        }
    }

    // The way the field leads the probe, as a line on this slice: bright where it runs near the slice, faint and dotted where it is above or below it.
    private void DrawPath(DrawingContext context, List<(float x, float y, float z)> path, double scale, double ox, double oy) {
        for (var i = 1; i < path.Count; i++) {
            var (au, av, aoff) = OnSlice(path[i - 1].x, path[i - 1].y, path[i - 1].z);
            var (bu, bv, boff) = OnSlice(path[i].x, path[i].y, path[i].z);
            var near = Math.Abs(aoff) < 1.0 && Math.Abs(boff) < 1.0;
            context.DrawLine(near ? PathNear : PathFar, ToPixel(au, av, scale, ox, oy), ToPixel(bu, bv, scale, ox, oy));
        }
    }

    // The cell the nearest-open query found, joined to the probe by a line; hollow and dotted if it is off this slice's plane.
    private void DrawNearest(DrawingContext context, Point probe, double scale, double ox, double oy) {
        var (u, v, off) = OnSlice(NearestX, NearestY, NearestZ);
        var at = ToPixel(u, v, scale, ox, oy);
        var onPlane = Math.Abs(off) < WallWorld.CellSize / 2;
        context.DrawLine(onPlane ? NearestLine : NearestOff, probe, at);
        var half = Math.Max(3.0, WallWorld.CellSize * scale / 2);
        context.DrawRectangle(onPlane ? ProbeDot : null, onPlane ? null : NearestLine, new Rect(at.X - half, at.Y - half, 2 * half, 2 * half));
    }

    // A diamond where the anchor is: the place whose region the nearest-open query can be held to.
    private void DrawAnchor(DrawingContext context, double scale, double ox, double oy) {
        var (u, v, off) = OnSlice(AnchorX, AnchorY, AnchorZ);
        var at = ToPixel(u, v, scale, ox, oy);
        var r = 7.0;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) {
            g.BeginFigure(new Point(at.X, at.Y - r), false);
            g.LineTo(new Point(at.X + r, at.Y));
            g.LineTo(new Point(at.X, at.Y + r));
            g.LineTo(new Point(at.X - r, at.Y));
            g.EndFigure(true);
        }
        context.DrawGeometry(Math.Abs(off) < WallWorld.CellSize / 2 ? new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x80, 0xAB)) : null, AnchorEdge, geometry);
    }

    // Pale shapes: where the plane cuts each solid, from the solid's own numbers; in region colour the seeds too, as yellow boxes.
    private void DrawSolids(DrawingContext context, WallWorld world, bool regions, bool travel, bool routes, double scale, double ox, double oy) {
        var at = Fixed;
        if (_outlines is null || _outlinesFor != (world, at) || travel || routes) {
            _outlines = world.Sections(Plane, at);
            _seedOutlines = world.SeedSections(Plane, at);
            _costOutlines = world.CostSections(Plane, at);
            _goalOutlines = world.GoalSections(Plane, at);
            _outlinesFor = (world, at);
        }
        if (ShowSolids) { Fill(context, _outlines, SolidFill, SolidEdge, scale, ox, oy); }
        // Dropped cost boxes in orange.
        if (travel || routes) { Fill(context, _costOutlines!, CostFill, CostEdge, scale, ox, oy); }
        if (travel) {
            // The solid the field starts in, in yellow.
            Fill(context, _goalOutlines!, SeedFill, SeedEdge, scale, ox, oy);
            return;
        }
        if (!regions) { return; }
        if (_seedOutlines is not null) { Fill(context, _seedOutlines, SeedFill, SeedEdge, scale, ox, oy); }
        // A seed the plane misses still shows, as a hollow mark where it lies over or under the slice.
        foreach (var seed in world.Seeds) {
            var (u, v, off) = OnSlice(seed.X, seed.Y, seed.Z);
            if (Math.Abs(off) <= 0.3) { continue; }
            var mark = ToPixel(u, v, scale, ox, oy);
            context.DrawRectangle(null, SeedEdge, new Rect(mark.X - 4, mark.Y - 4, 8, 8));
        }
    }

    private void Fill(DrawingContext context, List<(double U, double V)[]> outlines, IBrush fill, IPen edge, double scale, double ox, double oy) {
        foreach (var outline in outlines) {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                g.BeginFigure(ToPixel(outline[0].U, outline[0].V, scale, ox, oy), true);
                for (var i = 1; i < outline.Length; i++) { g.LineTo(ToPixel(outline[i].U, outline[i].V, scale, ox, oy)); }
                g.EndFigure(true);
            }
            context.DrawGeometry(fill, edge, geometry);
        }
    }

    private static void AddRect(StreamGeometryContext g, double x, double y, double width, double height) {
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x + width, y));
        g.LineTo(new Point(x + width, y + height));
        g.LineTo(new Point(x, y + height));
        g.EndFigure(true);
    }

    private void DrawLegend(DrawingContext context, WallWorld world, bool regions, bool travel, bool routes, double x, double y, double width) {
        if (travel) {
            var brushes = SliceColours.Brushes(world, SliceColour.Travel);
            var travelBar = Math.Min(width * 0.4, 260.0);
            var travelStep = travelBar / SliceColours.Bands;
            for (var band = 0; band < SliceColours.Bands; band++) { context.FillRectangle(brushes[band], new Rect(x + band * travelStep, y, Math.Ceiling(travelStep) + 0.5, 10)); }
            DrawText(context, "at the goal", new Point(x, y + 11), 11, Silver);
            var farText = $"{world.TravelMax:0.#} m of travel";
            var farLabel = new FormattedText(farText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
            context.DrawText(farLabel, new Point(x + travelBar - farLabel.Width, y + 11));
            var at = x + travelBar + 16;
            foreach (var (label, brush) in new[] { ("open, not reached", brushes[SliceColours.FieldUnreached]), ("not open", brushes[SliceColours.FieldClosed]) }) {
                context.FillRectangle(brush, new Rect(at, y, 20, 10));
                var formatted = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
                context.DrawText(formatted, new Point(at, y + 11));
                at += Math.Max(formatted.Width, 20) + 20;
            }
            return;
        }
        if (regions) {
            var brushes = SliceColours.Brushes(world, SliceColour.Regions);
            var shown = Math.Min(world.RegionCount, 6);
            for (var i = 0; i < shown; i++) { context.FillRectangle(brushes[i + 1], new Rect(x + i * 22, y, 20, 10)); }
            // Each label sits after the one before it, whatever the font makes of its width.
            var at = x;
            foreach (var (label, brush) in new[] { (shown > 0 ? "seeded regions" : "no region reached", (IBrush?)null), ("open, unreached", brushes[SliceColours.Unreached]), ("too near a shape", brushes[SliceColours.Closed]) }) {
                if (brush is not null) { context.FillRectangle(brush, new Rect(at, y, 20, 10)); }
                var formatted = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
                context.DrawText(formatted, new Point(at, y + 11));
                at += Math.Max(formatted.Width, 20) + 24;
            }
            if (routes) {
                // The routes' own key, on a second row: a short sample of each line, then its label.
                var row = y + 28;
                var key = x;
                foreach (var (label, pen, dots) in new[] { ("cells' way", CellsNear, true), ("straightened", RouteNear, false), ("dotted: unproven", RouteDash, false) }) {
                    context.DrawLine(pen, new Point(key, row + 5), new Point(key + 26, row + 5));
                    if (dots) { context.DrawEllipse(CellsDot, null, new Point(key + 13, row + 5), 2.5, 2.5); }
                    var formatted = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
                    context.DrawText(formatted, new Point(key + 32, row - 1));
                    key += 32 + formatted.Width + 12;
                }
            }
            return;
        }
        var bar = Math.Min(width, 260.0);
        var segments = world.Cap + 1;
        var step = bar / segments;
        for (var count = 0; count < segments; count++) {
            context.FillRectangle(SliceColours.Brushes(world, SliceColour.WallDistance)[count], new Rect(x + count * step, y, Math.Ceiling(step) + 0.5, 10));
        }
        DrawText(context, "0 m", new Point(x, y + 11), 11, Silver);
        var far = $"{world.Cap * world.Unit:0.###} m or more (the cap)";
        var text = new FormattedText(far, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
        context.DrawText(text, new Point(x + bar - text.Width, y + 11));
        DrawText(context, world.AsTree ? "kept by the tree" : "stored wall distance", new Point(x + bar + 12, y - 1), 11, Silver);
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush) {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawText(formatted, at);
    }
}
