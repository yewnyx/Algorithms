using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// The Spatial library on boxes: a sphere (a circle, in the top-down plane view) follows the pointer and finds the
/// boxes near it, by a scan of every box and by a tree, which find the same boxes while the tree tests only those
/// near the sphere. Most boxes rest and some drift, the tree being rebuilt whole after each step, which is the case
/// the library is for.
/// </summary>
public partial class SpatialPanel : UserControl {
    private readonly DispatcherTimer _timer;
    private readonly BoxQuery _query = new();
    private BoxWorld _world = null!;
    private float _x = BoxWorld.Width / 2, _y = BoxWorld.Height / 2, _z = BoxWorld.Depth3D / 2;
    private long _lastTick;
    private int _ticks;
    private bool _ready;

    public SpatialPanel() {
        InitializeComponent();

        NewButton.Click += (_, _) => Generate();
        PlayButton.Click += (_, _) => TogglePlay();
        ResetViewButton.Click += (_, _) => Boxes3D.ResetCamera();
        ViewBox.SelectionChanged += (_, _) => { if (_ready) { ChangeView(); } };
        RadiusBox.ValueChanged += (_, _) => Refresh();
        NearestBox.ValueChanged += (_, _) => Refresh();
        MethodBox.SelectionChanged += (_, _) => Refresh();
        TreeBox.IsCheckedChanged += (_, _) => Refresh();
        Boxes.QueryMoved += (x, y) => {
            (_x, _y) = (x, y);
            Refresh();
        };
        Boxes3D.QueryMoved += (x, y, z) => {
            (_x, _y, _z) = (x, y, z);
            Refresh();
        };

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();

        Generate();
        _ready = true;
        AttachedToVisualTree += (_, _) => { if (PlayButton.Content as string != "▶ Play") { Start(); } };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private bool ThreeD => ViewBox.SelectedIndex == 0;

    // The two views suit different numbers: a sphere in a volume needs more boxes and a wider reach to find a few.
    private void ChangeView() {
        CountBox.Value = ThreeD ? 800 : 400;
        RadiusBox.Value = ThreeD ? 140 : 90;
        (_x, _y, _z) = (BoxWorld.Width / 2, BoxWorld.Height / 2, BoxWorld.Depth3D / 2);
        Generate();
    }

    private void Generate() {
        _world = new BoxWorld((int)(CountBox.Value ?? 400), (int)(MovingBox.Value ?? 15), (int)(SeedBox.Value ?? 1), ThreeD);
        Boxes.World = _world;
        Boxes3D.World = _world;
        Boxes.IsVisible = !ThreeD;
        Boxes3D.IsVisible = ThreeD;
        PaneText.Text = ThreeD
            ? "Drag to orbit. The pointer slides the sphere; the wheel pushes it nearer or farther; Ctrl and the wheel zoom. Grey rest, orange drift, blue are found, numbers are the nearest, yellow outlines are tree nodes the walk visited"
            : "Move the pointer over the boxes. Grey rest, orange drift, blue are found, numbers are the nearest, yellow outlines are tree nodes the walk visited";
        Refresh();
    }

    private void Start() {
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        PlayButton.Content = "⏸ Pause";
    }

    private void TogglePlay() {
        if (_timer.IsEnabled) {
            _timer.Stop();
            PlayButton.Content = "▶ Play";
        } else {
            Start();
        }
    }

    private void OnTick() {
        var now = Stopwatch.GetTimestamp();
        var seconds = (float)Math.Min((now - _lastTick) / (double)Stopwatch.Frequency, 0.1);
        _lastTick = now;
        // Timing takes a few milliseconds, so do it every eighth step, not every one.
        Advance(seconds, measure: _ticks++ % 8 == 0);
    }

    /// <summary>Moves the drifting boxes on and runs the query again.</summary>
    public void Advance(float seconds, bool measure = true) {
        _world.Step(seconds);
        Refresh(measure);
    }

    /// <summary>Puts the query at a point in the plane view's world and runs it.</summary>
    public void MoveTo(float x, float y) {
        (_x, _y) = (x, y);
        Refresh();
    }

    /// <summary>Puts the query at a point in the 3D world and runs it.</summary>
    public void MoveTo(float x, float y, float z) {
        (_x, _y, _z) = (x, y, z);
        Refresh();
    }

    /// <summary>Turns the 3D camera, as dragging does.</summary>
    public void Orbit(double yaw, double pitch) => Boxes3D.Orbit(yaw, pitch);

    private void Refresh(bool measure = true) {
        if (_world is null) { return; }
        var radius = (float)(RadiusBox.Value ?? 90);
        var k = (int)(NearestBox.Value ?? 5);
        var shape = _world.ThreeD ? "sphere" : "circle";
        // The plane's boxes are all within z = 0, where its query sits.
        _world.Run(_query, _x, _y, _world.ThreeD ? _z : 0f, radius, k, measure);

        var useTree = MethodBox.SelectedIndex != 0;
        var showTree = TreeBox.IsChecked == true;
        Boxes.Query = _query;
        (Boxes.UseTree, Boxes.ShowTree) = (useTree, showTree);
        Boxes3D.Query = _query;
        (Boxes3D.UseTree, Boxes3D.ShowTree) = (useTree, showTree);
        (_world.ThreeD ? (Control)Boxes3D : Boxes).InvalidateVisual();

        var n = _world.Count;
        var drifting = _world.Moving.Count(m => m);
        HeadlineText.Text = _query.SameHits && _query.SameNearest
            ? $"The {shape} finds {_query.ScanTotal:N0} of {n:N0} boxes, and the scan and the tree agree on which, and on the {_query.ScanNearestCount:N0} nearest."
            : "The scan and the tree DISAGREE: that is a bug in the library, not a feature.";
        CountsText.Text =
            $"To find the boxes in the {shape}, the scan tested all {n:N0}; the tree visited {_query.TreeNodesVisited:N0} of its " +
            $"{_world.NodeCount:N0} nodes and tested {_query.TreeBoxesTested:N0} boxes ({_query.TreeBoxesTested * 100.0 / Math.Max(1, n):0.#}%). " +
            $"The {shape} query and the nearest query together took {_query.ScanMicroseconds:N1} µs by scan and {_query.TreeMicroseconds:N1} µs by tree.";
        var saved = _query.ScanMicroseconds - _query.TreeMicroseconds;
        var payback = saved > 0.05
            ? $"The tree saves {saved:N1} µs per pair of queries, so a rebuild pays for itself after {_world.RebuildMicroseconds / saved:N0} of them."
            : $"Here the tree saves nothing, so a rebuild never pays for itself: with few boxes, or a {shape} covering most of them, a scan is as good.";
        NoteText.Text =
            $"{drifting:N0} of {n:N0} boxes drift; after each step the tree is rebuilt whole, taking {_world.RebuildMicroseconds:N1} µs with the six columns. " +
            payback + " A scan has nothing to build. Times are averages over many runs, on this machine.";
    }
}
