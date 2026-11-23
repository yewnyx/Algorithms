using Avalonia.Controls;
using Yewnyx.Algorithms.Visualizer.Model;
using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// The Spatial library's wall distance on a grid: a few boxes, spheres and capsules, a grid marked from them, and two
/// slices through it (looking down, and from the side) coloured by what the grid stored. A probe point, put anywhere
/// by pressing on either slice, shows what <see cref="WallDistance.RoomAt"/> proves from the grid alone beside the
/// exact distance from the shapes.
/// </summary>
public partial class GridPanel : UserControl {
    private WallWorld _world = null!;
    private bool _ready, _syncing;

    public GridPanel() {
        InitializeComponent();

        SideView.Plane = SlicePlane.Xz;
        TopView.ShowLegend = true;
        // Each slice's frame and title wear the colour of its plane in the 3D view.
        TopView.Accent = CubeView.TopAccent;
        SideView.Accent = CubeView.SideAccent;
        ResetViewButton.Click += (_, _) => CubeView.ResetCamera();

        NewButton.Click += (_, _) => Generate();
        SeedBox.ValueChanged += (_, _) => { if (_ready) { Generate(); } };
        UnitBox.ValueChanged += (_, _) => Marked();
        CapBox.ValueChanged += (_, _) => Marked();
        SolidsBox.IsCheckedChanged += (_, _) => Refresh();
        XBox.ValueChanged += (_, _) => Refresh();
        YBox.ValueChanged += (_, _) => Refresh();
        ZBox.ValueChanged += (_, _) => Refresh();
        TopView.ProbeMoved += (x, y) => MoveTo(x, y, null);
        SideView.ProbeMoved += (x, z) => MoveTo(x, null, z);
        TopView.WheelStepped += step => MoveTo(null, null, Z + step * 0.5f);
        SideView.WheelStepped += step => MoveTo(null, Y + step * 0.5f, null);
        ColourBox.SelectionChanged += (_, _) => Refresh();
        RadiusBox.ValueChanged += (_, _) => Flooded();
        SeedCountBox.ValueChanged += (_, _) => Flooded();
        ReachBox.ValueChanged += (_, _) => Refresh();
        LikeBox.IsCheckedChanged += (_, _) => Refresh();
        TopView.AnchorMoved += (x, y) => MoveAnchor(x, y, null);
        SideView.AnchorMoved += (x, z) => MoveAnchor(x, null, z);

        Generate();
        _ready = true;
    }

    private bool ByRegions => ColourBox.SelectedIndex == 1;
    private float BodyRadius => (float)(RadiusBox.Value ?? 0.9m);

    // The anchor: a place whose region the nearest-open query can be held to. Until it is put somewhere it sits in the first seed.
    private (float x, float y, float z)? _anchor;
    private bool _anchorPut;

    private float X => (float)(XBox.Value ?? 16);
    private float Y => (float)(YBox.Value ?? 10);
    private float Z => (float)(ZBox.Value ?? 2);

    private float Unit => (float)(UnitBox.Value ?? 0.125m);
    private byte Cap => (byte)(CapBox.Value ?? 8);

    private void Generate() {
        _world = new WallWorld((int)(SeedBox.Value ?? 1), Unit, Cap);
        _anchorPut = false;
        Flooded();
    }

    private void Marked() {
        if (!_ready) { return; }
        _world.Mark(Unit, Cap);
        Flooded();
    }

    // The size class is made for one unit and cap, so every new map needs a new flood.
    private void Flooded() {
        if (_world is null) { return; }
        _world.Flood(BodyRadius, (int)(SeedCountBox.Value ?? 3), (int)(SeedBox.Value ?? 1) + 1000);
        if (!_anchorPut) { _anchor = _world.Seeds.Length > 0 ? (_world.Seeds[0].X, _world.Seeds[0].Y, _world.Seeds[0].Z) : null; }
        Refresh();
    }

    /// <summary>Puts the anchor at a point of a slice, the third coordinate being the probe's.</summary>
    public void MoveAnchor(float? x, float? y, float? z) {
        _anchor = (WallWorld.Clamp(x ?? X, WallWorld.Width), WallWorld.Clamp(y ?? Y, WallWorld.Height), WallWorld.Clamp(z ?? Z, WallWorld.Depth));
        _anchorPut = true;
        Refresh();
    }

    /// <summary>Puts the probe at a point, giving only the coordinates that change.</summary>
    public void MoveTo(float? x, float? y, float? z) {
        _syncing = true;
        if (x is { } px) { XBox.Value = (decimal)Math.Round(WallWorld.Clamp(px, WallWorld.Width), 2); }
        if (y is { } py) { YBox.Value = (decimal)Math.Round(WallWorld.Clamp(py, WallWorld.Height), 2); }
        if (z is { } pz) { ZBox.Value = (decimal)Math.Round(WallWorld.Clamp(pz, WallWorld.Depth), 2); }
        _syncing = false;
        Refresh();
    }

    private void Refresh() {
        if (_world is null || _syncing) { return; }
        float x = WallWorld.Clamp(X, WallWorld.Width), y = WallWorld.Clamp(Y, WallWorld.Height), z = WallWorld.Clamp(Z, WallWorld.Depth);
        var hasRoom = _world.Room(x, y, z, out var room);
        var hasExact = _world.Exact(x, y, z, out var exact);
        var reach = _world.Cap * _world.Unit;

        // The regions queries at the probe: is its cell open and reached, and where is the nearest open cell (held to the anchor's region if asked).
        var like = LikeBox.IsChecked == true ? _anchor : null;
        var look = (float)(ReachBox.Value ?? 3m);
        var isOpen = _world.IsOpen(x, y, z);
        var hasNearest = _world.NearestOpen(x, y, z, look, like, out var nx, out var ny, out var nz);

        foreach (var view in new[] { TopView, SideView }) {
            view.World = _world;
            view.Colour = ByRegions ? SliceColour.Regions : SliceColour.WallDistance;
            (view.ProbeX, view.ProbeY, view.ProbeZ) = (x, y, z);
            view.Room = hasRoom ? room : float.NaN;
            view.Exact = hasExact ? exact : float.NaN;
            view.ShowSolids = SolidsBox.IsChecked == true;
            view.Reach = look;
            (view.HasNearest, view.NearestX, view.NearestY, view.NearestZ) = (hasNearest, nx, ny, nz);
            (view.HasAnchor, view.AnchorX, view.AnchorY, view.AnchorZ) = (_anchor is not null, _anchor?.x ?? 0, _anchor?.y ?? 0, _anchor?.z ?? 0);
            view.InvalidateVisual();
        }
        CubeView.World = _world;
        CubeView.Colour = ByRegions ? SliceColour.Regions : SliceColour.WallDistance;
        (CubeView.ProbeX, CubeView.ProbeY, CubeView.ProbeZ) = (x, y, z);
        CubeView.Room = hasRoom ? room : float.NaN;
        CubeView.Exact = hasExact ? exact : float.NaN;
        CubeView.ShowSolids = SolidsBox.IsChecked == true;
        var world = _world;
        var anchor = _anchor;
        CubeView.Overlay = ByRegions ? (context, cam) => Overlay3D.Regions(context, cam, world, (x, y, z), look, anchor, hasNearest ? (nx, ny, nz) : null) : null;
        CubeView.InvalidateVisual();

        if (ByRegions) {
            RegionTexts(x, y, z, isOpen, hasNearest, nx, ny, nz, look, like is not null);
            return;
        }
        HintText.Text = "Press on either slice to put the probe there; the wheel over a slice moves the other slice (z over the top-down one, y over the side one). Green circle: what the grid alone proves is free. Yellow dashed circle: the exact distance. Pale shapes: the solids. The 3D view shows where the two slices cut (sky blue: top-down, lilac: side), the solids, and the probe with both spheres: drag it to orbit, the wheel zooms, Reset 3D view goes back to the isometric angle.";

        var count = _world.CountAt(WallWorld.CellOf(x, WallWorld.CountX), WallWorld.CellOf(y, WallWorld.CountY), WallWorld.CellOf(z, WallWorld.CountZ));
        var kinds = _world.Solids.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        int Of(SolidKind kind) => kinds.GetValueOrDefault(kind);

        if (!hasRoom || !hasExact) {
            HeadlineText.Text = "The library refused the probe.";
        } else if (room > exact + 1e-4f) {
            HeadlineText.Text = $"The grid claims {room:0.000} m free but the shapes say {exact:0.000} m: that is a bug in the library, not a feature.";
        } else {
            HeadlineText.Text = $"At ({x:0.00}, {y:0.00}, {z:0.00}) m the grid alone proves {room:0.000} m free; the shapes say {exact:0.000} m. " +
                (exact <= 0f ? "The probe is inside a solid."
                    : exact >= reach ? $"The grid cannot say more than its cap of {reach:0.###} m, so it stops short of the truth here, and never goes over."
                    : $"The grid is {(exact - room) * 1000:0} mm short, {room * 100 / exact:0}% of the truth, and never over.");
        }
        CountsText.Text = $"The cell holding the probe stores count {count}" +
            (count >= _world.Cap ? $", the cap: {reach:0.###} m or more from every shape." : $": its centre is at least {count * _world.Unit:0.###} m from every shape (rounded down to a unit of {_world.Unit:0.#####} m).");
        NoteText.Text =
            $"{_world.Solids.Length} shapes ({Of(SolidKind.Box):N0} boxes, {Of(SolidKind.Sphere):N0} spheres, {Of(SolidKind.Capsule):N0} capsules) in a grid of {WallWorld.CountX}×{WallWorld.CountY}×{WallWorld.CountZ} = " +
            $"{WallWorld.CountX * WallWorld.CountY * WallWorld.CountZ:N0} cells of {WallWorld.CellSize} m; marking every layer took {_world.MarkMilliseconds:0.0} ms. " +
            "Room is worked out from the eight cells around the probe alone; it never reads the shapes.";
    }

    private void RegionTexts(float x, float y, float z, bool isOpen, bool hasNearest, float nx, float ny, float nz, float look, bool held) {
        HintText.Text = "Press on either slice to put the probe there; Shift or the right button puts the anchor (pink diamond); the wheel over a slice moves the other slice. " +
            "Each colour is a region; yellow boxes are the seeds; the white circle is the reach, and the line goes to the nearest open cell (dotted and hollow if it is off the slice). The 3D view draws the same colours on the two planes, with the seeds, the reach, the anchor and the nearest cell in space.";
        if (_world.Problem is { } problem) {
            HeadlineText.Text = $"No regions: {problem}.";
            CountsText.Text = "A cell is open for a size class when its centre is at least the radius from every shape; the class can only be made if the map's cap reaches that far plus a diagonal step.";
            NoteText.Text = $"Cap {_world.Cap} × unit {_world.Unit:0.#####} m = {_world.Cap * _world.Unit:0.###} m.";
            return;
        }
        var size = _world.Size;
        var cells = WallWorld.CountX * WallWorld.CountY * WallWorld.CountZ;
        var region = _world.RegionAt(WallWorld.CellOf(x, WallWorld.CountX), WallWorld.CellOf(y, WallWorld.CountY), WallWorld.CellOf(z, WallWorld.CountZ));
        HeadlineText.Text = $"A body of radius {size.Radius:0.##} m: the seeds' flood reached {_world.RegionCount} region{(_world.RegionCount == 1 ? "" : "s")} and {_world.Reached:N0} cells ({_world.Reached * 100.0 / cells:0.#}% of the grid). " +
            (isOpen ? $"The probe is in an open cell of region {region}." : "The probe's cell is not open and reached: IsOpen says no.");
        var nearest = hasNearest
            ? $"NearestOpen within {look:0.#} m{(held ? " and in the anchor's region" : "")}: the cell centred ({nx:0.00}, {ny:0.00}, {nz:0.00}) m, {Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y) + (nz - z) * (nz - z)):0.00} m away."
            : $"NearestOpen finds no open cell within {look:0.#} m{(held ? " in the anchor's region" : "")}.";
        CountsText.Text = nearest;
        NoteText.Text = $"Open means a count of at least {size.Cell} ({size.Cell * size.Unit:0.###} m). Along the straight line between the centres of two open cells that share a face, every point keeps {size.Guarantee:0.000} m from every shape (the guarantee). " +
            $"{_world.Seeds.Length} seed{(_world.Seeds.Length == 1 ? "" : "s")}{(_world.EmptySeeds > 0 ? $", {_world.EmptySeeds} holding no open cell" : "")}; the flood took {_world.FloodMilliseconds:0.0} ms. Regions join across faces only, never a diagonal.";
    }
}
