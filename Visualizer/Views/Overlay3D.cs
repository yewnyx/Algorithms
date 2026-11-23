using Avalonia;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;
using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>What each colouring of the grid draws in the 3D view besides its planes, through <see cref="WallView3D.Overlay"/>.</summary>
internal static class Overlay3D {
    private static readonly IPen SeedEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76)), 1.5);
    private static readonly IPen ReachEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.5, DashStyle.Dash);
    private static readonly IPen ReachRing = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1, DashStyle.Dash);
    private static readonly IPen NearestLine = new Pen(new SolidColorBrush(Colors.White), 1.5);
    private static readonly IPen AnchorEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0xAB)), 2);
    private static readonly IBrush AnchorFill = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x80, 0xAB));
    private static readonly IBrush White = new SolidColorBrush(Colors.White);

    /// <summary>The solids as wireframes in one pen.</summary>
    public static void Wire(DrawingContext context, in Projection cam, IEnumerable<Solid> solids, IPen pen) {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) {
            foreach (var solid in solids) { WallView3D.AddSolid(g, cam, solid); }
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>A diamond about a point of the picture, the anchor's mark.</summary>
    public static void Anchor(DrawingContext context, Point at) {
        const double r = 8;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) {
            g.BeginFigure(new Point(at.X, at.Y - r), true);
            g.LineTo(new Point(at.X + r, at.Y));
            g.LineTo(new Point(at.X, at.Y + r));
            g.LineTo(new Point(at.X - r, at.Y));
            g.EndFigure(true);
        }
        context.DrawGeometry(AnchorFill, AnchorEdge, geometry);
    }

    /// <summary>Colouring by region: the seeds as yellow boxes, the reach of the nearest-open query about the probe, the anchor, and the line to the nearest open cell.</summary>
    public static void Regions(DrawingContext context, in Projection cam, WallWorld world, (float x, float y, float z) probe, float reach,
        (float x, float y, float z)? anchor, (float x, float y, float z)? nearest) {
        Wire(context, cam, world.Seeds, SeedEdge);
        var at = cam.Project(probe.x, probe.y, probe.z);
        WallView3D.DrawBall(context, cam, reach, at, null, ReachEdge, ReachRing);
        if (nearest is { } n) {
            var to = cam.Project(n.x, n.y, n.z);
            context.DrawLine(NearestLine, at, to);
            context.DrawRectangle(White, null, new Rect(to.X - 3.5, to.Y - 3.5, 7, 7));
        }
        if (anchor is { } a) { Anchor(context, cam.Project(a.x, a.y, a.z)); }
    }
}
