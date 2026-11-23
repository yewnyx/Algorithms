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
        HintText.Text = "Press on either slice to put the probe there; the wheel over a slice moves the other slice (z over the top-down one, y over the side one). Green circle: what the grid alone proves is free. Yellow dashed circle: the exact distance. Pale shapes: the solids. The 3D view shows where the two slices cut (sky blue: top-down, lilac: side), the solids, and the probe with both spheres: drag it to orbit, the wheel zooms, Reset 3D view goes back to the isometric angle.";

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

        Generate();
        _ready = true;
    }

    private float X => (float)(XBox.Value ?? 16);
    private float Y => (float)(YBox.Value ?? 10);
    private float Z => (float)(ZBox.Value ?? 6);

    private float Unit => (float)(UnitBox.Value ?? 0.125m);
    private byte Cap => (byte)(CapBox.Value ?? 8);

    private void Generate() {
        _world = new WallWorld((int)(SeedBox.Value ?? 1), Unit, Cap);
        Refresh();
    }

    private void Marked() {
        if (!_ready) { return; }
        _world.Mark(Unit, Cap);
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

        foreach (var view in new[] { TopView, SideView }) {
            view.World = _world;
            (view.ProbeX, view.ProbeY, view.ProbeZ) = (x, y, z);
            view.Room = hasRoom ? room : float.NaN;
            view.Exact = hasExact ? exact : float.NaN;
            view.ShowSolids = SolidsBox.IsChecked == true;
            view.InvalidateVisual();
        }
        CubeView.World = _world;
        (CubeView.ProbeX, CubeView.ProbeY, CubeView.ProbeZ) = (x, y, z);
        CubeView.Room = hasRoom ? room : float.NaN;
        CubeView.Exact = hasExact ? exact : float.NaN;
        CubeView.ShowSolids = SolidsBox.IsChecked == true;
        CubeView.InvalidateVisual();

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
}
