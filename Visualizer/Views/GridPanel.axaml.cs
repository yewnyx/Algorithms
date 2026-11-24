using Avalonia;
using Avalonia.Media;
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
        ModeDistance.Click += (_, _) => SetMode(0);
        ModeRegions.Click += (_, _) => SetMode(1);
        ModeTravel.Click += (_, _) => SetMode(2);
        ModeRoutes.Click += (_, _) => SetMode(3);
        RadiusSlider.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) { RadiusText.Text = $"{BodyRadius:0.00} m"; if (_ready) { Flooded(); } } };
        RadiusText.Text = $"{BodyRadius:0.00} m";
        SeedCountBox.ValueChanged += (_, _) => Flooded();
        ReachBox.ValueChanged += (_, _) => Refresh();
        LikeBox.IsCheckedChanged += (_, _) => Refresh();
        GoalBox.SelectionChanged += (_, _) => { _travelDirty = true; Refresh(); };
        StrokesBox.IsCheckedChanged += (_, _) => Refresh();
        CellsBox.IsCheckedChanged += (_, _) => Refresh();
        StraightBox.IsCheckedChanged += (_, _) => Refresh();
        ShapesBox.IsCheckedChanged += (_, _) => Refresh();
        WallButton.Click += (_, _) => DropCost(255);
        SlowButton.Click += (_, _) => DropCost((byte)(CostBox.Value ?? 64));
        ClearButton.Click += (_, _) => { _world.ClearCost(); Flooded(); };
        TopView.AnchorMoved += (x, y) => MoveAnchor(x, y, null);
        SideView.AnchorMoved += (x, z) => MoveAnchor(x, null, z);
        SceneRooms.Click += (_, _) => SetScene(WallScene.Rooms);
        SceneOutdoors.Click += (_, _) => SetScene(WallScene.Outdoors);
        TreeBox.IsCheckedChanged += (_, _) => {
            _world.AsTree = TreeBox.IsChecked == true;
            // A tree has no costs: those need a count for every cell.
            if (_world.AsTree && _world.CostBoxes.Count > 0) { _world.ClearCost(); Flooded(); }
            SetMode(_mode);
        };

        Generate();
        _ready = true;
        SetMode(0);
    }

    private static readonly string[] Captions = {
        "How far each cell is from the nearest shape, as the grid stores it. Click a slice to put the probe there.",
        "Where a body of this size can go: each colour is a region, and the dark cells are too near a shape. Drag the body radius and watch a door close.",
        "How far each cell is from the goal (the pink diamond), going round what is in the way. Right-click a slice to move the goal; wall off or slow a place to bend the field.",
        "The route from the probe to the goal (the pink diamond): the cells' way in blue, pulled straight in yellow. Right-click a slice to move the goal; wall off a place to bend it.",
    };

    private const string TreeCaption = "How far each cell is from the nearest shape, as the tree keeps it: counts only in the bricks near surfaces (dark squares), and open air as cubes of one bit (white squares). Click a slice to put the probe there.";

    private const string TreeTravelCaption = "How far each cell is from the goal (the pink diamond) over the tree, kept for each open cube and brick, not each cell. The yellow line flies straight from point to point as the field gives them. Right-click a slice to move the goal.";

    private const string TreeRouteCaption = "The route over the tree from the probe to the goal (the pink diamond): the way through open cubes and bricks in blue, pulled straight in yellow. The table sets it beside the same search over a count for every cell.";

    private WallScene _scene = WallScene.Rooms;

    // The scene is its own choice, so the tree and the counts can be compared on either.
    private void SetScene(WallScene scene) {
        _scene = scene;
        SceneRooms.IsChecked = scene == WallScene.Rooms;
        SceneOutdoors.IsChecked = scene == WallScene.Outdoors;
        // Outdoors there are no doors to lead out through.
        foreach (var (item, index) in GoalBox.Items.OfType<ComboBoxItem>().Select((item, index) => (item, index))) { item.IsEnabled = index == WallWorld.GoalPoint || scene == WallScene.Rooms; }
        if (scene == WallScene.Outdoors) { GoalBox.SelectedIndex = WallWorld.GoalPoint; }
        Generate();
    }

    // The four views are one choice. Each shows only the actions that mean something in it.
    private void SetMode(int mode) {
        _mode = mode;
        ModeDistance.IsChecked = mode == 0;
        ModeRegions.IsChecked = mode == 1;
        ModeTravel.IsChecked = mode == 2;
        ModeRoutes.IsChecked = mode == 3;
        var tree = TreeBox.IsChecked == true;
        LikeBox.IsEnabled = !tree;
        CaptionText.Text = mode == 0 && tree ? TreeCaption : mode == 2 && tree ? TreeTravelCaption : mode == 3 && tree ? TreeRouteCaption : Captions[mode];
        BodyPanel.IsVisible = mode > 0;
        CostPanel.IsVisible = mode > 0 && !tree;
        SlowButton.IsVisible = mode > 1;
        ShapesBox.IsVisible = mode == 3;
        _travelDirty = true;
        Refresh();
    }

    private int _mode;
    private bool ByRegions => _mode == 1;
    private bool ByTravel => _mode == 2;
    private bool ByRoutes => _mode == 3;

    // The field is built when it is shown, and again after anything it depends on changes.
    private bool _travelDirty = true;
    private float BodyRadius => (float)Math.Round(RadiusSlider.Value, 2);

    // The anchor: a place whose region the nearest-open query can be held to. Until it is put somewhere it sits in the first seed.
    private (float x, float y, float z)? _anchor;
    private bool _anchorPut;

    private float X => (float)(XBox.Value ?? 16);
    private float Y => (float)(YBox.Value ?? 10);
    private float Z => (float)(ZBox.Value ?? 2);

    private float Unit => (float)(UnitBox.Value ?? 0.125m);
    private byte Cap => (byte)(CapBox.Value ?? 8);

    private void Generate() {
        _world = new WallWorld((int)(SeedBox.Value ?? 1), Unit, Cap, _scene) { AsTree = TreeBox.IsChecked == true };
        _anchorPut = false;
        _travelDirty = true;
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
        _travelDirty = true;
        if (!_anchorPut) { _anchor = _world.Seeds.Length > 0 ? (_world.Seeds[0].X, _world.Seeds[0].Y, _world.Seeds[0].Z) : null; }
        Refresh();
    }

    /// <summary>Puts the anchor at a point of a slice, the third coordinate being the probe's.</summary>
    public void MoveAnchor(float? x, float? y, float? z) {
        _anchor = (WallWorld.Clamp(x ?? X, WallWorld.Width), WallWorld.Clamp(y ?? Y, WallWorld.Height), WallWorld.Clamp(z ?? Z, WallWorld.Depth));
        _anchorPut = true;
        if (GoalBox.SelectedIndex == WallWorld.GoalPoint) { _travelDirty = true; }
        Refresh();
    }

    // Writes the cost into a cube about the probe.
    private void DropCost(byte cost) {
        float x = WallWorld.Clamp(X, WallWorld.Width), y = WallWorld.Clamp(Y, WallWorld.Height), z = WallWorld.Clamp(Z, WallWorld.Depth);
        _world.DropCost(x, y, z, 1.5f, cost);
        // A cost of 255 closes cells, which the regions have to leave out, so the flood is made again.
        Flooded();
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

        // The travel field, and the path it leads down from the probe.
        List<(float x, float y, float z)>? path = null;
        var pathEnd = "";
        if (ByTravel) {
            if (_travelDirty) {
                var goalAt = _anchor ?? (x, y, z);
                _world.BuildTravel(GoalBox.SelectedIndex, goalAt.x, goalAt.y, goalAt.z);
                _travelDirty = false;
            }
            (path, pathEnd) = _world.FollowField(x, y, z);
        }

        // The routes from the probe to the anchor: the cells' way, the same pulled straight, and the straight one again with the shapes the other way about.
        WallWorld.RouteFound? cellsRoute = null, straightRoute = null, otherRoute = null, denseRoute = null;
        if (ByRoutes && _world.Problem is null && _anchor is { } goal) {
            var useShapes = ShapesBox.IsChecked == true;
            cellsRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, false, useShapes);
            straightRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, true, useShapes);
            otherRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, true, !useShapes);
            // Over the tree, the same route over the counts, to set beside it.
            if (_world.AsTree) { denseRoute = _world.FindDenseRoute(x, y, z, goal.x, goal.y, goal.z, true, useShapes); }
        }

        foreach (var view in new[] { TopView, SideView }) {
            view.World = _world;
            view.Colour = ByRoutes ? SliceColour.Routes : ByTravel ? SliceColour.Travel : ByRegions ? SliceColour.Regions : SliceColour.WallDistance;
            view.CellsWay = CellsBox.IsChecked == true ? Line(cellsRoute, x, y, z, true) : null;
            view.StraightWay = StraightBox.IsChecked == true ? Line(straightRoute, x, y, z, false) : null;
            view.Path = path;
            view.ShowStrokes = StrokesBox.IsChecked == true;
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
        CubeView.Colour = ByRoutes ? SliceColour.Routes : ByTravel ? SliceColour.Travel : ByRegions ? SliceColour.Regions : SliceColour.WallDistance;
        (CubeView.ProbeX, CubeView.ProbeY, CubeView.ProbeZ) = (x, y, z);
        CubeView.Room = hasRoom ? room : float.NaN;
        CubeView.Exact = hasExact ? exact : float.NaN;
        CubeView.ShowSolids = SolidsBox.IsChecked == true;
        var world = _world;
        var anchor = _anchor;
        var cellsWay = CellsBox.IsChecked == true ? Line(cellsRoute, x, y, z, true) : null;
        var straightWay = StraightBox.IsChecked == true ? Line(straightRoute, x, y, z, false) : null;
        CubeView.Overlay = ByRoutes ? (context, cam) => Overlay3D.Routes(context, cam, world, cellsWay, straightWay, anchor)
            : ByTravel ? (context, cam) => Overlay3D.Travel(context, cam, world, path, anchor)
            : ByRegions ? (context, cam) => Overlay3D.Regions(context, cam, world, (x, y, z), look, anchor, hasNearest ? (nx, ny, nz) : null) : null;
        CubeView.InvalidateVisual();

        if (ByRoutes) {
            RouteTable(x, y, z, cellsRoute, straightRoute, otherRoute, denseRoute);
            return;
        }
        if (ByTravel) {
            TravelTable(x, y, z, path!, pathEnd);
            return;
        }
        if (ByRegions) {
            RegionTable(x, y, z, isOpen, hasNearest, nx, ny, nz, look);
            return;
        }
        DistanceTable(x, y, z, hasRoom, room, hasExact, exact, reach);
    }

    private static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0x7F, 0x88, 0x96));
    private static readonly IBrush White = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF4));

    // The readout is a few label and value pairs, three to a line.
    private void Table(params (string label, string value)[] rows) {
        TableGrid.Children.Clear();
        foreach (var (label, value) in rows) {
            var cell = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 12, 2) };
            cell.Children.Add(new TextBlock { Text = label, Foreground = Grey, Width = 150, FontSize = 12 });
            cell.Children.Add(new TextBlock { Text = value, Foreground = White, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
            TableGrid.Children.Add(cell);
        }
    }

    private static string At(float x, float y, float z) => $"({x:0.00}, {y:0.00}, {z:0.00}) m";
    private static string Cells(long count) => $"{count:N0} cells ({count * 100.0 / (WallWorld.CountX * WallWorld.CountY * WallWorld.CountZ):0.#}%)";
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private void DistanceTable(float x, float y, float z, bool hasRoom, float room, bool hasExact, float exact, float reach) {
        HintText.Text = "Press on either slice to put the probe there; the wheel over a slice moves the other slice (z over the top-down one, y over the side one). Green circle: what the grid alone proves is free. Yellow dashed circle: the exact distance. Pale shapes: the solids. The 3D view shows where the two slices cut (sky blue: top-down, lilac: side), the solids, and the probe with both spheres: drag it to orbit, the wheel zooms, Reset 3D view goes back to the isometric angle.";
        var count = _world.CountAt(WallWorld.CellOf(x, WallWorld.CountX), WallWorld.CellOf(y, WallWorld.CountY), WallWorld.CellOf(z, WallWorld.CountZ));
        var kinds = _world.Solids.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        int Of(SolidKind kind) => kinds.GetValueOrDefault(kind);
        if (!hasRoom || !hasExact) {
            Table(("Probe", At(x, y, z)), ("Status", "The library refused the probe."));
            return;
        }
        var gap = room > exact + 1e-4f ? "over the truth: a bug in the library"
            : exact <= 0f ? "the probe is inside a solid"
            : exact >= reach ? $"stops at its cap of {reach:0.###} m, never over"
            : $"{(exact - room) * 1000:0} mm short ({room * 100 / exact:0}% of the truth), never over";
        if (_world.AsTree) {
            _world.DenseRoom(x, y, z, out var dense);
            Table(
                ("Probe", At(x, y, z)),
                ("Tree proves free", $"{room:0.000} m"),
                ("Counts prove free", dense == room ? $"{dense:0.000} m, the same" : $"{dense:0.000} m"),
                ("Shapes say", $"{exact:0.000} m"),
                ("Tree size", $"{_world.TreeBytes:N0} bytes, {_world.TreeBytes * 100.0 / _world.DenseBytes:0}% of a count a cell ({_world.DenseBytes:N0})"),
                ("Tree", $"{Count(_world.TreeNodeCount, "node")}, {Count(_world.TreeBrickCount, "brick")}, {Count(_world.OpenCubeCount, "open cube")}"));
            return;
        }
        Table(
            ("Probe", At(x, y, z)),
            ("Grid proves free", $"{room:0.000} m"),
            ("Shapes say", $"{exact:0.000} m"),
            ("Grid is", gap),
            ("Stored count", count >= _world.Cap ? $"{count} (the cap)" : count.ToString()),
            ("Shapes", $"{_world.Solids.Length} ({Of(SolidKind.Box)} boxes, {Of(SolidKind.Sphere)} spheres, {Of(SolidKind.Capsule)} capsules){(_scene == WallScene.Outdoors ? " and the ground" : "")}"));
    }

    private void RouteTable(float x, float y, float z, WallWorld.RouteFound? cells, WallWorld.RouteFound? straight, WallWorld.RouteFound? other, WallWorld.RouteFound? dense) {
        HintText.Text = "Press on either slice to put the start (the white dot); Shift or the right button puts the goal (the pink diamond); the wheel over a slice moves the other slice. " +
            "Blue: the cells' way, a dot at every cell centre; yellow: the same pulled straight; thin and dashed: off this slice, seen flat; dotted: a leg the library does not prove clear; orange boxes are costs you dropped. The 3D view draws the same colours on the two planes, with both routes in space.";
        if (_world.Problem is { } problem) {
            Table(("Body radius", $"{BodyRadius:0.00} m"), ("Status", $"No route: {problem}. Try a smaller body."));
            return;
        }
        if (_anchor is not { } goal || cells is null || straight is null || other is null) {
            Table(("Status", "No goal yet: right-click a slice to put it."));
            return;
        }
        var radius = _world.Size.Radius;
        var direct = Math.Sqrt((double)(goal.x - x) * (goal.x - x) + (double)(goal.y - y) * (goal.y - y) + (double)(goal.z - z) * (goal.z - z));
        if (straight.Status != RouteStatus.Found) {
            Table(
                ("Status", straight.Status == RouteStatus.Unreachable ? "No route: the start and the goal are in different regions, or the way is shut" : $"No route ({straight.Status})"),
                ("Body radius", $"{radius:0.00} m"),
                ("Straight line", $"{direct:0.0} m"),
                ("Cells searched", straight.Expanded.ToString("N0")));
            return;
        }
        var shapes = ShapesBox.IsChecked == true;
        var lengthStraight = Length(x, y, z, straight.Points);
        if (dense is not null) {
            var denseFound = dense.Status == RouteStatus.Found;
            var treeRows = new List<(string, string)> {
                ("Over the tree", $"{Count(straight.Count, "point")}, {lengthStraight:0.0} m"),
                ("Over the counts", denseFound ? $"{Count(dense.Count, "point")}, {Length(x, y, z, dense.Points):0.0} m" : $"no route ({dense.Status})"),
                ("Straight line", $"{direct:0.0} m"),
                ("Searched", $"{Count(straight.Expanded, "place")} over the tree, {Count(dense.Expanded, "cell")} over the counts"),
                ("Search arrays", $"{WallWorld.TreeSearchBytes / 1024:N0} KB over the tree whatever the grid, {_world.DenseSearchBytes / 1024:N0} KB over the counts"),
                ("Search time", $"{straight.Milliseconds:0.00} ms over the tree, {dense.Milliseconds:0.00} ms over the counts"),
            };
            if (straight.Ends.HasFlag(RouteEnds.FirstLegUnproven)) { treeRows.Add(("Note", "The first leg is not proven clear.")); }
            if (straight.Ends.HasFlag(RouteEnds.StopsShortOfGoal)) { treeRows.Add(("Note", "Stops short of the goal.")); }
            Table(treeRows.ToArray());
            return;
        }
        var rows = new List<(string, string)> {
            ("Status", "Found"),
            ("Pulled straight", $"{Count(straight.Count, "point")}, {lengthStraight:0.0} m"),
            ("Cells' way", cells.Status == RouteStatus.Found ? $"{Count(cells.Count, "point")}, {Length(x, y, z, cells.Points):0.0} m" : cells.Status.ToString()),
            ("Straight line", $"{direct:0.0} m"),
            (shapes ? "Without the shapes" : "With the shapes", other.Status == RouteStatus.Found ? $"{Count(other.Count, "point")}, {Length(x, y, z, other.Points):0.0} m" : $"no route ({other.Status})"),
            ("Search time", $"{straight.Milliseconds:0.00} ms"),
        };
        if (straight.Ends.HasFlag(RouteEnds.FirstLegUnproven)) { rows.Add(("Note", "The first leg is not proven clear.")); }
        if (straight.Ends.HasFlag(RouteEnds.StopsShortOfGoal)) { rows.Add(("Note", "Stops short of the goal.")); }
        Table(rows.ToArray());
    }

    private void TravelTable(float x, float y, float z, List<(float x, float y, float z)> path, string pathEnd) {
        HintText.Text = "Press on either slice to put the probe there; Shift or the right button puts the anchor, which is the goal when the goal is a point; the wheel over a slice moves the other slice. " +
            "Cell colour is how far the cell is from the goal, going round what is in the way; strokes lead downhill; the yellow line is the path the field leads the probe along; orange boxes are costs you dropped. The 3D view draws the same colours on the two planes, with the path, the cost boxes and the goal in space.";
        var goal = GoalBox.SelectedIndex switch { WallWorld.GoalPoint => "the pink diamond", WallWorld.GoalFirstDoor => "the first door", _ => "the second door" };
        if (_world.TravelProblem is { } problem) {
            Table(("Goal", goal), ("Body radius", $"{BodyRadius:0.00} m"), ("Status", $"No field: {problem}. Try a smaller body, or put the goal in the open."));
            return;
        }
        var has = _world.SampleField(x, y, z, out _, out _, out _, out var travel);
        var rows = new List<(string, string)> {
            ("Goal", goal),
            ("Travel at the probe", has ? $"{travel:0.00} m" : "out of the field"),
        };
        if (has && GoalBox.SelectedIndex == WallWorld.GoalPoint && _anchor is { } a) {
            rows.Add(("Straight line", $"{Math.Sqrt((x - a.x) * (x - a.x) + (y - a.y) * (y - a.y) + (z - a.z) * (z - a.z)):0.00} m"));
        }
        rows.Add(("Field reaches", Cells(_world.TravelReached)));
        if (_world.AsTree) { rows.Add(("Field kept", $"{_world.TreeFieldBytes:N0} bytes for {_world.TreeFieldPlaces:N0} places, against {_world.DenseFieldBytes:N0} for a count a cell")); }
        rows.Add(("Following it", $"{Count(path.Count - 1, "step")}, {pathEnd}"));
        rows.Add(("Walls and slow zones", _world.CostBoxes.Count == 0 ? "none" : _world.CostBoxes.Count.ToString()));
        Table(rows.ToArray());
    }

    private void RegionTable(float x, float y, float z, bool isOpen, bool hasNearest, float nx, float ny, float nz, float look) {
        HintText.Text = "Press on either slice to put the probe there; Shift or the right button puts the anchor (pink diamond); the wheel over a slice moves the other slice. " +
            "Each colour is a region; yellow boxes are the seeds; the white circle is the reach, and the line goes to the nearest open cell (dotted and hollow if it is off the slice). The 3D view draws the same colours on the two planes, with the seeds, the reach, the anchor and the nearest cell in space.";
        if (_world.Problem is { } problem) {
            Table(("Body radius", $"{BodyRadius:0.00} m"), ("Status", $"No regions: {problem}. Try a smaller body."));
            return;
        }
        var region = _world.RegionAt(WallWorld.CellOf(x, WallWorld.CountX), WallWorld.CellOf(y, WallWorld.CountY), WallWorld.CellOf(z, WallWorld.CountZ));
        var held = LikeBox.IsChecked == true && !_world.AsTree;
        if (_world.AsTree) {
            Table(
                ("Body radius", $"{_world.Size.Radius:0.00} m"),
                ("Reached", $"{Cells(_world.TreeReached)}, as over the counts: {(_world.TreeReached == _world.Reached ? "the same" : Cells(_world.Reached))}"),
                ("Probe's cell", isOpen ? $"open, region {region}" : "not open"),
                ("Regions kept in", $"{_world.TreeRegionBytes:N0} bytes, against {_world.DenseBytes:N0} for a byte a cell"),
                ("Nearest open cell", hasNearest ? $"{Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y) + (nz - z) * (nz - z)):0.00} m away" : $"none within {look:0.#} m"),
                ("Flood time", $"{_world.TreeFloodMilliseconds:0.0} ms"));
            return;
        }
        Table(
            ("Body radius", $"{_world.Size.Radius:0.00} m"),
            ("Regions", $"{_world.RegionCount} (from {Count(_world.Seeds.Length, "seed")})"),
            ("Reached", Cells(_world.Reached)),
            ("Probe's cell", isOpen ? $"open, region {region}" : "not open"),
            ("Nearest open cell", hasNearest ? $"{Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y) + (nz - z) * (nz - z)):0.00} m away" : $"none within {look:0.#} m{(held ? " in the anchor's region" : "")}"),
            ("Flood time", $"{_world.FloodMilliseconds:0.0} ms"));
    }

    // A route as a line for the slices: the start, the points, and the goal again when the route stops short of it.
    private WallSliceView.RouteLine? Line(WallWorld.RouteFound? found, float x, float y, float z, bool dots) {
        if (found is not { Status: RouteStatus.Found }) { return null; }
        var points = new List<(float x, float y, float z)> { (x, y, z) };
        points.AddRange(found.Points);
        var stops = found.Ends.HasFlag(RouteEnds.StopsShortOfGoal);
        if (stops && _anchor is { } goal) { points.Add(goal); }
        return new WallSliceView.RouteLine(points, found.Ends.HasFlag(RouteEnds.FirstLegUnproven), stops, dots);
    }

    private static double Length(float x, float y, float z, List<(float x, float y, float z)> points) {
        double total = 0;
        float px = x, py = y, pz = z;
        foreach (var (qx, qy, qz) in points) {
            total += Math.Sqrt((double)(qx - px) * (qx - px) + (double)(qy - py) * (qy - py) + (double)(qz - pz) * (qz - pz));
            (px, py, pz) = (qx, qy, qz);
        }
        return total;
    }
}
