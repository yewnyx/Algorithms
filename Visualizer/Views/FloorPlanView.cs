using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Rooms drawn as a floor plan: walls between rooms, gaps for doors, a small triangle on one-way doors and
/// a cyan bar on fast doors. Each room is coloured by its distance to the goal and shows an arrow toward
/// the door to take. Clicking a room or a door raises an event; the panel decides what it means.
/// </summary>
public sealed class FloorPlanView : Control {
    private const double TopInset = 30;
    private const double DoorStart = 0.3;
    private const double DoorEnd = 0.7;
    private const double EdgeZone = 0.22;

    private int _hoverRoom = -1;
    private (int a, int b) _hoverDoor = (-1, -1);
    private Point _pressedAt;
    private bool _pressed;

    public ShortestPathsModel? Model { get; set; }
    public int Goal { get; set; } = -1;
    /// <summary>Distance from each room to the goal, PositiveInfinity if it cannot get there.</summary>
    public float[] Distance { get; set; } = [];
    /// <summary>The room each room steps into next on its way to the goal, or -1.</summary>
    public int[] NextRoom { get; set; } = [];
    /// <summary>A room to outline, or -1. The animation marks the latest stopover with it.</summary>
    public int Pivot { get; set; } = -1;
    /// <summary>Rooms below this number get a small dot: the animation's stopovers so far.</summary>
    public int StopoversBelow { get; set; }
    public IReadOnlySet<int> Glow { get; set; } = new HashSet<int>();
    public IReadOnlyList<int>? Path { get; set; }

    public event Action<int>? RoomClicked;
    public event Action<int, int>? DoorClicked;
    public event Action<int, (int a, int b)>? HoverChanged;

    public FloorPlanView() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private (double size, double ox, double oy) Geometry() {
        var m = Model!;
        var width = Bounds.Width - 16;
        var height = Bounds.Height - TopInset - 8;
        var size = Math.Max(1, Math.Min(width / m.Cols, height / m.Rows));
        var ox = (Bounds.Width - size * m.Cols) / 2;
        var oy = TopInset + (Bounds.Height - TopInset - size * m.Rows) / 2;
        return (size, ox, oy);
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(Bounds.Size));
        var m = Model;
        if (m is null || Distance.Length != m.N) { return; }
        var (s, ox, oy) = Geometry();
        var cols = m.Cols;
        Point Center(int v) => new(ox + (v % cols + 0.5) * s, oy + (v / cols + 0.5) * s);
        Rect Tile(int v) => new(ox + v % cols * s, oy + v / cols * s, s, s);

        var max = 1e-6f;
        foreach (var d in Distance) { if (!float.IsPositiveInfinity(d) && d > max) { max = d; } }

        // Floors.
        var blocked = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2C));
        for (var v = 0; v < m.N; v++) {
            var d = Distance[v];
            var brush = float.IsPositiveInfinity(d) ? blocked : new SolidColorBrush(Palette.ToColor(MatrixView.Heat(d / max)));
            context.FillRectangle(brush, Tile(v));
        }
        if (Glow.Count > 0) {
            var glow = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
            foreach (var v in Glow) { context.FillRectangle(glow, Tile(v).Deflate(s * 0.08)); }
        }
        if (StopoversBelow > 0 && s >= 10) {
            var dot = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
            var r = Math.Max(1.5, s * 0.07);
            for (var v = 0; v < Math.Min(StopoversBelow, m.N); v++) {
                var t = Tile(v);
                context.DrawEllipse(dot, null, new Point(t.X + s * 0.16, t.Y + s * 0.16), r, r);
            }
        }

        // Arrows toward the next room on the way to the goal.
        if (s >= 9 && NextRoom.Length == m.N) {
            var dark = new StreamGeometry();
            var light = new StreamGeometry();
            using (var darkCtx = dark.Open())
            using (var lightCtx = light.Open()) {
                for (var v = 0; v < m.N; v++) {
                    var next = NextRoom[v];
                    if (v == Goal || next < 0 || next == v) { continue; }
                    var c = Center(v);
                    var dir = Center(next) - c;
                    var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                    if (len < 1e-6) { continue; }
                    dir /= len;
                    var normal = new Vector(-dir.Y, dir.X);
                    var tail = c - dir * (s * 0.22);
                    var tip = c + dir * (s * 0.28);
                    var head = s * 0.16;
                    var ctx = Distance[v] / max < 0.55 ? darkCtx : lightCtx;
                    ctx.BeginFigure(tail, false);
                    ctx.LineTo(tip);
                    ctx.EndFigure(false);
                    ctx.BeginFigure(tip - dir * head + normal * head * 0.75, false);
                    ctx.LineTo(tip);
                    ctx.LineTo(tip - dir * head - normal * head * 0.75);
                    ctx.EndFigure(false);
                }
            }
            var thickness = Math.Max(1.2, s * 0.07);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x12, 0x18)), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), dark);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xE8, 0xED, 0xF4)), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), light);
        }

        // Walls with gaps for doors.
        var walls = new StreamGeometry();
        var oneWay = new StreamGeometry();
        var fast = new StreamGeometry();
        using (var w = walls.Open())
        using (var o = oneWay.Open())
        using (var f = fast.Open()) {
            void Segment(Point a, Point b) {
                w.BeginFigure(a, false);
                w.LineTo(b);
                w.EndFigure(false);
            }
            // Wall between room v and neighbour u, running from p to q.
            void Wall(int v, int u, Point p, Point q) {
                var forward = m.HasDoor(v, u);
                var backward = m.HasDoor(u, v);
                if (!forward && !backward) {
                    Segment(p, q);
                    return;
                }
                var along = q - p;
                var g0 = p + along * DoorStart;
                var g1 = p + along * DoorEnd;
                Segment(p, g0);
                Segment(g1, q);
                if (m.FastDoors.Contains((Math.Min(v, u), Math.Max(v, u)))) {
                    f.BeginFigure(g0, false);
                    f.LineTo(g1);
                    f.EndFigure(false);
                }
                if (forward != backward) {
                    // A small triangle in the gap pointing the way the door opens.
                    var mid = p + along * 0.5;
                    var dir = Center(forward ? u : v) - Center(forward ? v : u);
                    dir /= Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                    var normal = new Vector(-dir.Y, dir.X);
                    var a = s * 0.13;
                    o.BeginFigure(mid + dir * a, true);
                    o.LineTo(mid - dir * a + normal * a);
                    o.LineTo(mid - dir * a - normal * a);
                    o.EndFigure(true);
                }
            }
            for (var v = 0; v < m.N; v++) {
                var t = Tile(v);
                if (v % cols + 1 < cols) { Wall(v, v + 1, t.TopRight, t.BottomRight); }
                if (v + cols < m.N) { Wall(v, v + cols, t.BottomLeft, t.BottomRight); }
            }
        }
        var wallPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8)), Math.Max(1, s * 0.07), lineCap: PenLineCap.Square);
        context.DrawGeometry(null, wallPen, walls);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)), Math.Max(4, s * 0.26), lineCap: PenLineCap.Round), fast);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0xE0, 0xFF)), Math.Max(2.5, s * 0.17), lineCap: PenLineCap.Round), fast);
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA)), new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)), 1), oneWay);
        context.DrawRectangle(null, new Pen(wallPen.Brush, Math.Max(2, s * 0.1)), new Rect(ox, oy, s * cols, s * m.Rows));

        // The route from the hovered room.
        if (Path is { Count: > 1 } path) {
            var line = new StreamGeometry();
            using (var ctx = line.Open()) {
                ctx.BeginFigure(Center(path[0]), false);
                for (var i = 1; i < path.Count; i++) { ctx.LineTo(Center(path[i])); }
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xD5, 0x4F)), Math.Max(2.5, s * 0.16), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
            context.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 1), Center(path[0]), Math.Max(3, s * 0.15), Math.Max(3, s * 0.15));
        }

        if (Pivot >= 0 && Pivot < m.N) {
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), Math.Max(2, s * 0.12)), Tile(Pivot).Deflate(s * 0.06));
        }

        // The goal: a target in the middle of its room.
        if (Goal >= 0 && Goal < m.N) {
            var c = Center(Goal);
            var r = Math.Max(4, s * 0.3);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)), new Pen(Brushes.White, Math.Max(1.5, s * 0.06)), c, r, r);
            context.DrawEllipse(Brushes.White, null, c, r * 0.35, r * 0.35);
        }

        if (_hoverDoor.a >= 0) {
            var (a, b) = _hoverDoor;
            var ta = Tile(a);
            var (p, q) = cols > 1 && b == a + 1 ? (ta.TopRight, ta.BottomRight) : (ta.BottomLeft, ta.BottomRight);
            var along = q - p;
            context.DrawLine(new Pen(Brushes.Gold, Math.Max(3, s * 0.14), lineCap: PenLineCap.Round), p + along * DoorStart, p + along * DoorEnd);
        } else if (_hoverRoom >= 0) {
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), Tile(_hoverRoom));
        }
    }

    /// <summary>The room under a point and, if the point is in a doorway, the door as (lower, higher) room.</summary>
    private (int room, (int a, int b) door) HitTest(Point point) {
        var m = Model;
        if (m is null || Distance.Length != m.N) { return (-1, (-1, -1)); }
        var (s, ox, oy) = Geometry();
        var fx = (point.X - ox) / s;
        var fy = (point.Y - oy) / s;
        if (fx < 0 || fy < 0 || fx >= m.Cols || fy >= m.Rows) { return (-1, (-1, -1)); }
        var c = (int)fx;
        var r = (int)fy;
        var v = r * m.Cols + c;
        var u = fx - c;
        var w = fy - r;
        bool Door(int a, int b) => m.HasDoor(a, b) || m.HasDoor(b, a);
        (int, int)? door = null;
        var alongVertical = w > DoorStart - 0.1 && w < DoorEnd + 0.1;
        var alongHorizontal = u > DoorStart - 0.1 && u < DoorEnd + 0.1;
        if (alongVertical && u < EdgeZone && c > 0 && Door(v - 1, v)) { door = (v - 1, v); }
        else if (alongVertical && u > 1 - EdgeZone && c + 1 < m.Cols && Door(v, v + 1)) { door = (v, v + 1); }
        else if (alongHorizontal && w < EdgeZone && r > 0 && Door(v - m.Cols, v)) { door = (v - m.Cols, v); }
        else if (alongHorizontal && w > 1 - EdgeZone && r + 1 < m.Rows && Door(v, v + m.Cols)) { door = (v, v + m.Cols); }
        return (v, door ?? (-1, -1));
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var (room, door) = HitTest(e.GetPosition(this));
        if (room == _hoverRoom && door == _hoverDoor) { return; }
        _hoverRoom = room;
        _hoverDoor = door;
        HoverChanged?.Invoke(room, door);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hoverRoom < 0 && _hoverDoor.a < 0) { return; }
        _hoverRoom = -1;
        _hoverDoor = (-1, -1);
        HoverChanged?.Invoke(-1, (-1, -1));
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        _pressedAt = e.GetPosition(this);
        _pressed = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!_pressed) { return; }
        _pressed = false;
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _pressedAt.X) > 4 || Math.Abs(at.Y - _pressedAt.Y) > 4) { return; }
        var (room, door) = HitTest(at);
        if (door.a >= 0) { DoorClicked?.Invoke(door.a, door.b); }
        else if (room >= 0) { RoomClicked?.Invoke(room); }
    }
}
