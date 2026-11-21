using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Boxes in a plane with a query circle: resting boxes grey, drifting ones orange, the boxes the circle finds
/// filled blue, the nearest ones numbered, and optionally the tree's nodes as fainter outlines, lit where the
/// tree's walk visited them. Moving the pointer raises <see cref="QueryMoved"/> with a point in the world.
/// </summary>
public sealed class BoxView : Control {
    private const double TopInset = 30;

    private static readonly IBrush Background = new SolidColorBrush(Palette.ToColor(Palette.Background));
    private static readonly IPen Resting = new Pen(new SolidColorBrush(Color.FromRgb(0x5E, 0x6B, 0x82)), 1);
    private static readonly IPen Drifting = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)), 1);
    private static readonly IBrush FoundFill = new SolidColorBrush(Color.FromArgb(0xA0, 0x4F, 0xC3, 0xF7));
    private static readonly IPen Found = new Pen(new SolidColorBrush(Color.FromRgb(0xE1, 0xF5, 0xFE)), 1.5);
    // Inner nodes stay quiet; the leaves the walk reached, whose boxes were tested, are the ones that stand out.
    private static readonly IPen NodeFaint = new Pen(new SolidColorBrush(Color.FromArgb(0x2A, 0x90, 0xA4, 0xAE)), 1);
    private static readonly IPen InnerVisited = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD5, 0x4F)), 1);
    private static readonly IPen LeafVisited = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xD5, 0x4F)), 1.5);
    private static readonly IBrush LeafVisitedFill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xD5, 0x4F));
    private static readonly IPen Circle = new Pen(new SolidColorBrush(Colors.White), 1.5);
    private static readonly IBrush CircleFill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76));

    public BoxWorld? World { get; set; }
    public BoxQuery? Query { get; set; }

    /// <summary>Whether the circle's boxes come from the tree rather than the scan (they are the same boxes).</summary>
    public bool UseTree { get; set; }
    public bool ShowTree { get; set; }

    public event Action<float, float>? QueryMoved;

    public BoxView() {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private (double scale, double ox, double oy) Geometry() {
        var width = Bounds.Width - 16;
        var height = Bounds.Height - TopInset - 8;
        var scale = Math.Max(0.01, Math.Min(width / BoxWorld.Width, height / BoxWorld.Height));
        var ox = (Bounds.Width - scale * BoxWorld.Width) / 2;
        var oy = TopInset + (Bounds.Height - TopInset - scale * BoxWorld.Height) / 2;
        return (scale, ox, oy);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var (scale, ox, oy) = Geometry();
        var p = e.GetPosition(this);
        QueryMoved?.Invoke((float)((p.X - ox) / scale), (float)((p.Y - oy) / scale));
    }

    public override void Render(DrawingContext context) {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var world = World;
        var query = Query;
        if (world is null || query is null || query.NodeVisited.Length != world.Nodes.Length) { return; }
        var (scale, ox, oy) = Geometry();
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x33)), 1),
            new Rect(ox, oy, BoxWorld.Width * scale, BoxWorld.Height * scale));

        Rect Rectangle(float minX, float minY, float maxX, float maxY) =>
            new(ox + minX * scale, oy + minY * scale, (maxX - minX) * scale, (maxY - minY) * scale);

        if (ShowTree) {
            for (var i = 0; i < world.NodeCount; i++) {
                var node = world.Nodes[i];
                var visited = query.NodeVisited[i];
                var rect = Rectangle(node.MinX, node.MinY, node.MaxX, node.MaxY);
                if (visited && node.Count > 0) { context.FillRectangle(LeafVisitedFill, rect); }
                context.DrawRectangle(null, !visited ? NodeFaint : node.Count > 0 ? LeafVisited : InnerVisited, rect);
            }
        }

        // Who the circle found, by the method on show, and each nearest box's place in line.
        var hits = UseTree ? query.TreeHits : query.ScanHits;
        var hitCount = Math.Max(0, UseTree ? query.TreeTotal : query.ScanTotal);
        var found = new bool[world.Count];
        for (var i = 0; i < hitCount; i++) { found[hits[i]] = true; }

        for (var i = 0; i < world.Count; i++) {
            var box = world.Boxes[i];
            var rect = Rectangle(box.MinX, box.MinY, box.MaxX, box.MaxY);
            if (found[i]) { context.FillRectangle(FoundFill, rect); }
            context.DrawRectangle(null, found[i] ? Found : world.Moving[i] ? Drifting : Resting, rect);
        }

        var radius = query.Radius * scale;
        var center = new Point(ox + query.X * scale, oy + query.Y * scale);
        context.DrawEllipse(CircleFill, Circle, center, radius, radius);

        var nearest = UseTree ? query.TreeNearest : query.ScanNearest;
        var nearestCount = Math.Max(0, UseTree ? query.TreeNearestCount : query.ScanNearestCount);
        for (var rank = 0; rank < nearestCount; rank++) {
            var box = world.Boxes[nearest[rank]];
            var rect = Rectangle(box.MinX, box.MinY, box.MaxX, box.MaxY);
            var text = new FormattedText((rank + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 13, LabelBrush);
            context.DrawText(text, new Point(rect.Center.X - text.Width / 2, rect.Center.Y - text.Height / 2));
        }
    }
}
