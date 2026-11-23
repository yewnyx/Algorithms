using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// One slice through a <see cref="WallWorld"/>: each cell coloured by the count the grid holds for it (the stored wall
/// distance, hot near a shape, cool far, flat at the cap), the solids themselves as pale shapes from the exact test,
/// and the probe with two circles: what the grid alone proves is free, and what the shapes say is. Pressing and
/// dragging puts the probe there. The x-by-y slice (looking down) can also draw its colour legend; the x-by-z slice
/// (from the side, z up) draws a line where the other slice cuts.
/// </summary>
public sealed class WallSliceView : Control {
    private const double TopInset = 24, Inset = 10;

    private static readonly IBrush Background = new SolidColorBrush(Palette.ToColor(Palette.Background));
    private static readonly IBrush Silver = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly IBrush SolidFill = new SolidColorBrush(Color.FromArgb(0x78, 0xF2, 0xF5, 0xFA));
    private static readonly IPen SolidEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen CutLine = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1, DashStyle.Dash);
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)), 1);
    private static readonly IBrush RoomFill = new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0xFC, 0x9A));
    private static readonly IPen RoomEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x9A)), 2);
    private static readonly IPen ExactEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)), 1.5, DashStyle.Dash);
    private static readonly IBrush ProbeDot = new SolidColorBrush(Colors.White);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC));

    private List<(double U, double V)[]>? _outlines;
    private (WallWorld? world, float at) _outlinesFor;
    private bool _pressed;

    public WallWorld? World { get; set; }
    public SlicePlane Plane { get; set; }

    /// <summary>The probe, in metres, and what the grid and the shapes say about it (room, exact); either is NaN if unknown.</summary>
    public float ProbeX { get; set; }
    public float ProbeY { get; set; }
    public float ProbeZ { get; set; }
    public float Room { get; set; } = float.NaN;
    public float Exact { get; set; } = float.NaN;

    /// <summary>The colour of this slice's frame and title, to match its plane in the 3D view; none for the plain look.</summary>
    public Color? Accent { get; set; }

    public bool ShowSolids { get; set; } = true;
    public bool ShowLegend { get; set; }

    /// <summary>The probe put somewhere on this slice: (x, y) on the top-down slice, (x, z) on the side slice.</summary>
    public event Action<float, float>? ProbeMoved;

    /// <summary>The wheel turned: +1 for a step up, -1 for down.</summary>
    public event Action<int>? WheelStepped;

    public WallSliceView() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private double PlaneHeight => Plane == SlicePlane.Xy ? WallWorld.Height : WallWorld.Depth;
    private float Fixed => Plane == SlicePlane.Xy ? ProbeZ : ProbeY;

    private (double scale, double ox, double oy) Geometry() {
        var bottom = ShowLegend ? 30.0 : Inset;
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

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { return; }
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
        ProbeMoved?.Invoke(u, v);
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var world = World;
        if (world is null) { return; }
        var (scale, ox, oy) = Geometry();
        var cell = WallWorld.CellSize * scale;
        var brushes = SliceColours.Brushes(world);

        // The layer the slice cuts, or for the side slice the row of cells holding the probe's y.
        var rows = Plane == SlicePlane.Xy ? WallWorld.CountY : WallWorld.CountZ;
        var fixedCell = WallWorld.CellOf(Fixed, Plane == SlicePlane.Xy ? WallWorld.CountZ : WallWorld.CountY);
        if (fixedCell < 0) { return; }

        var title = Plane == SlicePlane.Xy
            ? $"Looking down at z = {ProbeZ:0.00} m (layer {fixedCell} of {WallWorld.CountZ})"
            : $"From the side at y = {ProbeY:0.00} m; dashed: the top-down slice";
        DrawText(context, title, new Point(Inset, 5), 12, Accent is { } accent ? new SolidColorBrush(accent) : TextBrush);

        // Cells with the same count share one geometry, so a slice is a few dozen draws, not thousands.
        var shapes = new StreamGeometry?[brushes.Length];
        var contexts = new StreamGeometryContext?[brushes.Length];
        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < WallWorld.CountX; column++) {
                var count = Plane == SlicePlane.Xy ? SliceColours.KeyAt(world, column, row, fixedCell) : SliceColours.KeyAt(world, column, fixedCell, row);
                var g = contexts[count];
                if (g is null) {
                    shapes[count] = new StreamGeometry();
                    g = contexts[count] = shapes[count]!.Open();
                }
                var top = Plane == SlicePlane.Xy ? oy + row * cell : oy + (rows - 1 - row) * cell;
                AddRect(g, ox + column * cell, top, cell, cell);
            }
        }
        for (var count = 0; count < brushes.Length; count++) {
            contexts[count]?.Dispose();
            if (shapes[count] is { } shape) { context.DrawGeometry(brushes[count], null, shape); }
        }
        context.DrawRectangle(null, Accent is { } edge ? new Pen(new SolidColorBrush(edge), 1.8) : Frame, new Rect(ox, oy, WallWorld.Width * scale, PlaneHeight * scale));

        if (ShowSolids) { DrawSolids(context, world, scale, ox, oy); }

        if (Plane == SlicePlane.Xz) {
            var cut = ToPixel(0, ProbeZ, scale, ox, oy);
            context.DrawLine(CutLine, cut, new Point(ox + WallWorld.Width * scale, cut.Y));
        }

        // The probe is on this slice, at its own height or row, so both circles are true circles here.
        var at = ToPixel(ProbeX, Plane == SlicePlane.Xy ? ProbeY : ProbeZ, scale, ox, oy);
        if (!float.IsNaN(Room)) { context.DrawEllipse(RoomFill, RoomEdge, at, Room * scale, Room * scale); }
        if (!float.IsNaN(Exact)) { context.DrawEllipse(null, ExactEdge, at, Exact * scale, Exact * scale); }
        context.DrawEllipse(ProbeDot, null, at, 3, 3);

        if (ShowLegend) { DrawLegend(context, world, brushes, ox, oy + PlaneHeight * scale + 6, WallWorld.Width * scale); }
    }

    // Pale shapes: where the plane cuts each solid, from the solid's own numbers.
    private void DrawSolids(DrawingContext context, WallWorld world, double scale, double ox, double oy) {
        var at = Fixed;
        if (_outlines is null || _outlinesFor != (world, at)) {
            _outlines = world.Sections(Plane, at);
            _outlinesFor = (world, at);
        }
        foreach (var outline in _outlines) {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                g.BeginFigure(ToPixel(outline[0].U, outline[0].V, scale, ox, oy), true);
                for (var i = 1; i < outline.Length; i++) { g.LineTo(ToPixel(outline[i].U, outline[i].V, scale, ox, oy)); }
                g.EndFigure(true);
            }
            context.DrawGeometry(SolidFill, SolidEdge, geometry);
        }
    }

    private static void AddRect(StreamGeometryContext g, double x, double y, double width, double height) {
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x + width, y));
        g.LineTo(new Point(x + width, y + height));
        g.LineTo(new Point(x, y + height));
        g.EndFigure(true);
    }

    private void DrawLegend(DrawingContext context, WallWorld world, IBrush[] brushes, double x, double y, double width) {
        var bar = Math.Min(width, 260.0);
        var segments = world.Cap + 1;
        var step = bar / segments;
        for (var count = 0; count < segments; count++) {
            context.FillRectangle(brushes[count], new Rect(x + count * step, y, Math.Ceiling(step) + 0.5, 10));
        }
        DrawText(context, "0 m", new Point(x, y + 11), 11, Silver);
        var far = $"{world.Cap * world.Unit:0.###} m or more (the cap)";
        var text = new FormattedText(far, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Silver);
        context.DrawText(text, new Point(x + bar - text.Width, y + 11));
        DrawText(context, "stored wall distance", new Point(x + bar + 12, y - 1), 11, Silver);
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush) {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawText(formatted, at);
    }
}
