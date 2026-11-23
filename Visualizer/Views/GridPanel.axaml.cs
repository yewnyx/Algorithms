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
        GoalBox.SelectionChanged += (_, _) => { _travelDirty = true; Refresh(); };
        StrokesBox.IsCheckedChanged += (_, _) => Refresh();
        CellsBox.IsCheckedChanged += (_, _) => Refresh();
        StraightBox.IsCheckedChanged += (_, _) => Refresh();
        ShapesBox.IsCheckedChanged += (_, _) => Refresh();
        DropButton.Click += (_, _) => DropCost();
        ClearButton.Click += (_, _) => { _world.ClearCost(); Flooded(); };
        TopView.AnchorMoved += (x, y) => MoveAnchor(x, y, null);
        SideView.AnchorMoved += (x, z) => MoveAnchor(x, null, z);

        Generate();
        _ready = true;
    }

    private bool ByRegions => ColourBox.SelectedIndex == 1;
    private bool ByTravel => ColourBox.SelectedIndex == 2;
    private bool ByRoutes => ColourBox.SelectedIndex == 3;

    // The field is built when it is shown, and again after anything it depends on changes.
    private bool _travelDirty = true;
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
    private void DropCost() {
        float x = WallWorld.Clamp(X, WallWorld.Width), y = WallWorld.Clamp(Y, WallWorld.Height), z = WallWorld.Clamp(Z, WallWorld.Depth);
        _world.DropCost(x, y, z, 1.5f, (byte)(CostBox.Value ?? 64));
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
        WallWorld.RouteFound? cellsRoute = null, straightRoute = null, otherRoute = null;
        if (ByRoutes && _world.Problem is null && _anchor is { } goal) {
            var useShapes = ShapesBox.IsChecked == true;
            cellsRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, false, useShapes);
            straightRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, true, useShapes);
            otherRoute = _world.FindRoute(x, y, z, goal.x, goal.y, goal.z, true, !useShapes);
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
            RouteTexts(x, y, z, cellsRoute, straightRoute, otherRoute);
            return;
        }
        if (ByTravel) {
            TravelTexts(x, y, z, path!, pathEnd);
            return;
        }
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

    private void RouteTexts(float x, float y, float z, WallWorld.RouteFound? cells, WallWorld.RouteFound? straight, WallWorld.RouteFound? other) {
        HintText.Text = "Press on either slice to put the start (the white dot); Shift or the right button puts the goal (the pink diamond); the wheel over a slice moves the other slice. " +
            "Blue: the cells' way, a dot at every cell centre; yellow: the same pulled straight; thin and dashed: off this slice, seen flat; dotted: a leg the library does not prove clear; orange boxes are costs you dropped. The 3D view draws the same colours on the two planes, with both routes in space.";
        if (_world.Problem is { } problem) {
            HeadlineText.Text = $"No route: {problem}.";
            CountsText.Text = "A route is for a body of one size and runs through the open cells of its regions, so the size class has to exist: try a smaller body radius.";
            NoteText.Text = $"Cap {_world.Cap} × unit {_world.Unit:0.#####} m = {_world.Cap * _world.Unit:0.###} m.";
            return;
        }
        if (_anchor is not { } goal || cells is null || straight is null || other is null) {
            HeadlineText.Text = "There is no goal: the seeds found no room for the anchor, and nothing has put it anywhere.";
            CountsText.Text = "Press Shift or the right button on a slice to put the goal.";
            NoteText.Text = "";
            return;
        }
        var radius = _world.Size.Radius;
        var direct = Math.Sqrt((double)(goal.x - x) * (goal.x - x) + (double)(goal.y - y) * (goal.y - y) + (double)(goal.z - z) * (goal.z - z));
        if (straight.Status != RouteStatus.Found) {
            HeadlineText.Text = straight.Status switch {
                RouteStatus.Unreachable => $"No route for a body of radius {radius:0.##} m: the start and the goal are in different regions, or one of them has no open cell within reach that it can see, or the open cells in between are shut by cost.",
                RouteStatus.Refused => "The library refused the search: a point is not a number, the size class does not fit the map, or an array is the wrong length.",
                _ => $"The search ended as {straight.Status}.",
            };
            CountsText.Text = $"Status {straight.Status}, {straight.Count} points, {straight.Expanded:N0} cells searched. The straight line from the start to the goal is {direct:0.00} m.";
            NoteText.Text = "Move the start or the goal into an open cell of the same region (coloured, in region view), shrink the body, or clear the costs.";
            return;
        }
        var shapes = ShapesBox.IsChecked == true;
        var lengthCells = cells.Status == RouteStatus.Found ? Length(x, y, z, cells.Points) : double.NaN;
        var lengthStraight = Length(x, y, z, straight.Points);
        var ends = straight.Ends == RouteEnds.None
            ? ""
            : " " + (straight.Ends.HasFlag(RouteEnds.FirstLegUnproven) ? "The first leg is not proven clear: the start is nearer a shape than the radius, or beside a closed cell, so the route starts at the centre of the nearest open cell it can see. " : "") +
              (straight.Ends.HasFlag(RouteEnds.StopsShortOfGoal) ? "The route stops short of the goal, at an open cell near it, since the way from there to the goal is not proven clear." : "");
        HeadlineText.Text = $"Found: from the probe to the goal for a body of radius {radius:0.##} m, {straight.Count} point{(straight.Count == 1 ? "" : "s")} and {lengthStraight:0.0} m once pulled straight (the straight line is {direct:0.0} m).{ends}";
        CountsText.Text = cells.Status == RouteStatus.Found
            ? $"The cells' way has {cells.Count} points and is {lengthCells:0.0} m long; the search cost it {cells.Cost:0.0} over {cells.Expanded:N0} cells taken up. Pulling it straight drops it to {straight.Count} point{(straight.Count == 1 ? "" : "s")}, {(lengthCells > 0 ? (lengthCells - lengthStraight) * 100 / lengthCells : 0):0}% shorter" +
              (straight.Expanded == 0 ? ", and the straight line from the start to the goal was clear, so nothing was searched." : ".")
            : $"The cells' way: {cells.Status}.";
        var otherLength = other.Status == RouteStatus.Found ? Length(x, y, z, other.Points) : double.NaN;
        var comparison = other.Status == RouteStatus.Found
            ? $"{(shapes ? "Without the shapes" : "With the shapes")} the same search gives {other.Count} point{(other.Count == 1 ? "" : "s")} and {otherLength:0.0} m."
            : $"{(shapes ? "Without the shapes" : "With the shapes")} the same search finds no route ({other.Status}).";
        NoteText.Text = (shapes
                ? "The library may prove a leg clear against the shapes themselves. "
                : "The library is given no shapes, so a leg is proven by the map alone, which needs more room than the radius. ") + comparison +
            $" The searches took {straight.Milliseconds:0.00} ms for the route and {cells.Milliseconds:0.00} ms for the cells' way. {(_world.CostBoxes.Count > 0 ? " Costs bend the cells' way. A pulled leg crosses no cell that costs more than the cells of the stretch it replaces and the cells its ends are in, so the route goes round what the way went round and crosses a dear area only where the way did. It also costs no more as flown (its length in each cell times 1 + cost / 16) than that stretch, so a start or goal inside a dear area is searched for and not cut straight across; the leg from an end to the centre of its own cell is exempt." : "")} A step between touching open cells keeps the guarantee of {_world.Size.Guarantee:0.000} m from every shape, a straight leg keeps the full radius of {_world.Size.Radius:0.###} m, and no leg comes nearer than half a cell to a closed cell. The view searches with no leeway; the Unity package defaults to a tenth, so its ways and counts differ.";
    }

    private void TravelTexts(float x, float y, float z, List<(float x, float y, float z)> path, string pathEnd) {
        HintText.Text = "Press on either slice to put the probe there; Shift or the right button puts the anchor, which is the goal when the goal is a point; the wheel over a slice moves the other slice. " +
            "Cell colour is how far the cell is from the goal, going round what is in the way; strokes lead downhill; the yellow line is the path the field leads the probe along; orange boxes are costs you dropped. The 3D view draws the same colours on the two planes, with the path, the cost boxes and the goal in space.";
        var goal = GoalBox.SelectedIndex switch { WallWorld.GoalPoint => "the anchor", WallWorld.GoalFirstDoor => "the first door", _ => "the second door" };
        if (_world.TravelProblem is { } problem) {
            HeadlineText.Text = $"No field from {goal}: {problem}.";
            CountsText.Text = "A field spreads from its seed through the cells of the regions the flood reached, so the body has to fit: try a smaller body radius, or put the anchor in an open cell.";
            NoteText.Text = $"Body radius {BodyRadius:0.##} m; the first door is 2.5 m wide and the second 1.5 m.";
            return;
        }
        var cells = WallWorld.CountX * WallWorld.CountY * WallWorld.CountZ;
        var has = _world.SampleField(x, y, z, out var dx, out var dy, out var dz, out var travel);
        var headline = $"The field from {goal} reaches {_world.TravelReached:N0} cells ({_world.TravelReached * 100.0 / cells:0.#}% of the grid), the farthest {_world.TravelMax:0.#} m of travel away. ";
        if (!has) {
            headline += "The probe is out of the field: none of the eight cells round it is reached.";
        } else {
            headline += $"At the probe it says {travel:0.00} m of travel";
            if (GoalBox.SelectedIndex == WallWorld.GoalPoint && _anchor is { } a) {
                var straight = Math.Sqrt((x - a.x) * (x - a.x) + (y - a.y) * (y - a.y) + (z - a.z) * (z - a.z));
                headline += $", the straight line to the goal being {straight:0.00} m";
            }
            headline += ".";
        }
        HeadlineText.Text = headline;
        CountsText.Text = has
            ? $"Downhill from the probe leads ({dx:0.00}, {dy:0.00}, {dz:0.00}). Following it in quarter-metre steps: {path.Count - 1} step{(path.Count == 2 ? "" : "s")}, which {pathEnd}."
            : $"Following the field from the probe: {pathEnd}.";
        NoteText.Text = $"The field took {_world.TravelMilliseconds:0.0} ms to build (a count is {WallWorld.TravelUnit} m{(_world.TravelCut > 0 ? $"; {_world.TravelCut:N0} cells were too far to count and are left unreached" : "")}). " +
            (GoalBox.SelectedIndex != WallWorld.GoalPoint ? "A door seed starts only the open cells with a straight open way to its plane, so a body too wide to pass starts just the cells behind the plane, and the room in front is reached some other way or not at all. " : "") +
            (_world.CostBoxes.Count > 0 ? $"{_world.CostBoxes.Count} cost box{(_world.CostBoxes.Count == 1 ? "" : "es")} dropped: crossing a cell costs 1 + cost / 16 times as much. " : "No cost boxes yet: drop one at the probe to see the field bend round it. ") +
            "The library promises nothing about what lies between cells; whatever follows the field still needs its own way of not hitting things.";
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
