using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// All-pairs shortest paths with FloydWarshall on a floor plan. One solve answers every route, so picking
/// any room as the goal immediately shows which door every other room should take. The animation replays
/// the algorithm as rooms becoming allowed stopovers, clicking a door makes it fast with Relax, and the
/// distance table the library actually stores can be shown alongside.
/// </summary>
public partial class ShortestPathsPanel : UserControl {
    private readonly DispatcherTimer _timer;
    private ShortestPathsModel? _model;
    private CancellationTokenSource? _generation;
    private int _goal = -1;
    private int _hoverRoom = -1;
    private (int a, int b) _hoverDoor = (-1, -1);
    private (int from, int to) _hoverCell = (-1, -1);
    private HashSet<int> _glow = [];
    private float[] _distance = [];
    private int[] _nextRoom = [];
    private long _lastTick;
    private double _pending;

    public ShortestPathsPanel() {
        InitializeComponent();

        GenerateButton.Click += async (_, _) => await GenerateAsync();
        SweepButton.Click += (_, _) => ToggleSweep();
        StepButton.Click += (_, _) => {
            StopSweep();
            EnsureSweep();
            StepAndShow(1);
        };
        TableBox.IsCheckedChanged += (_, _) => ShowTable(TableBox.IsChecked == true);
        SpeedSlider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty) { SpeedLabel.Text = $"{Speed:0} /s"; }
        };
        SpeedLabel.Text = $"{Speed:0} /s";

        Plan.RoomClicked += SetGoal;
        Plan.DoorClicked += MakeDoorFast;
        Plan.HoverChanged += (room, door) => {
            _hoverRoom = room;
            _hoverDoor = door;
            _hoverCell = (-1, -1);
            Refresh();
        };
        Matrix.HoverChanged += (from, to) => { _hoverCell = (from, to); Refresh(); };

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => OnTick();

        AttachedToVisualTree += async (_, _) => {
            if (_model is null) { await GenerateAsync(); }
        };
        DetachedFromVisualTree += (_, _) => StopSweep();
    }

    /// <summary>Animation steps per second.</summary>
    private double Speed => Math.Round(Math.Pow(10, SpeedSlider.Value));

    private const string Explain =
        "Click any room to make it the goal: every room's arrow points through the door to take, and the colour shows how far it is " +
        "(bright = near, dark = far, grey = no way there). Hover a room to see its route. Click a doorway to make that door fast.";

    private async Task GenerateAsync() {
        _generation?.Cancel();
        var cts = _generation = new CancellationTokenSource();
        StopSweep();
        // Cell indices from the old table would be out of range for a smaller one.
        Matrix.Flash = [];
        Matrix.Pivot = -1;
        _glow = [];
        var spec = new PathsSpec(
            (int)(Rooms.Value ?? 2),
            (double)(DoorPercent.Value ?? 0) / 100.0,
            (double)(OneWayPercent.Value ?? 0) / 100.0,
            (int)(Seed.Value ?? 0));
        GenerateButton.IsEnabled = false;
        ActionText.Text = $"Building about {spec.Rooms:N0} rooms and solving every route…";
        try {
            var model = await Task.Run(() => ShortestPathsModel.Build(spec, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) { return; }
            _model = model;
            _goal = model.Rows / 2 * model.Cols + model.Cols / 2;
            _hoverRoom = -1;
            _hoverDoor = (-1, -1);
            _hoverCell = (-1, -1);
            var n = model.N;
            var pairs = (long)n * n;
            HeadlineText.Text =
                $"{n:N0} rooms. FloydWarshall.Solve found the shortest route between all {pairs:N0} pairs of rooms in {model.SolveTime.TotalMilliseconds:N1} ms, " +
                $"so changing the goal needs no more searching.";
            ActionText.Text = Explain;
            var checks = new[] { model.ComponentsMatchMutualReach, model.ConnectedMatchesSymmetricReach, model.TarjanOrderIsTriangular };
            ChecksText.Text =
                $"Cross-checks with Tarjan and ConnectedComponents: {checks.Count(c => c)} of {checks.Length} pass. " +
                $"{model.ReachablePairs * 100.0 / pairs:0.#}% of pairs have a route; the tables take {pairs * 8 / 1024.0:N0} KB.";
            ToolTip.SetTip(ChecksText,
                "Rooms in the same strongly connected component (Tarjan) are exactly the rooms that can reach each other: " + Check(model.ComponentsMatchMutualReach) + "\n" +
                "Rooms in the same connected component are exactly the rooms joined when one-way doors open both ways: " + Check(model.ConnectedMatchesSymmetricReach) + "\n" +
                "In Tarjan's order every route points backwards or stays within a component, so the table is block triangular: " + Check(model.TarjanOrderIsTriangular));
            Plan.Model = model;
            Matrix.Blocks = model.TarjanBlocks();
            ShowLibraryTable();
            UpdateField();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            ActionText.Text = "Failed: " + ex.Message;
        } finally {
            if (_generation == cts) { GenerateButton.IsEnabled = true; }
        }

        static string Check(bool ok) => ok ? "yes" : "NO";
    }

    private void ShowTable(bool show) {
        Panes.ColumnDefinitions[0].Width = new GridLength(show ? 3 : 1, GridUnitType.Star);
        Panes.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        Panes.ColumnDefinitions[2].Width = show ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        Splitter.IsVisible = show;
        TablePane.IsVisible = show;
    }

    private void ShowLibraryTable() {
        var m = _model!;
        Matrix.SetMatrix(m.Sweeping ? m.Sweep : m.Distances, m.N, m.TarjanOrder);
    }

    private void SetGoal(int room) {
        if (_model is null) { return; }
        _goal = room;
        _glow = [];
        if (!_model.Sweeping) { ActionText.Text = Explain; }
        UpdateField();
    }

    /// <summary>Reads the distance and next room toward the goal for every room, from the animation while it runs.</summary>
    private void UpdateField() {
        var m = _model;
        if (m is null) { return; }
        var n = m.N;
        var distances = m.Sweeping ? m.Sweep : m.Distances;
        var next = m.Sweeping ? m.SweepNext : m.Next;
        if (_distance.Length != n) {
            _distance = new float[n];
            _nextRoom = new int[n];
        }
        for (var v = 0; v < n; v++) {
            _distance[v] = distances[v * n + _goal];
            _nextRoom[v] = next[v * n + _goal];
        }
        Plan.Goal = _goal;
        Plan.Distance = _distance;
        Plan.NextRoom = _nextRoom;
        Plan.Glow = _glow;
        Plan.Pivot = m.Sweeping ? m.SweepPivot - 1 : -1;
        Plan.StopoversBelow = m.Sweeping ? m.SweepPivot : 0;
        Matrix.Pivot = m.Sweeping ? m.SweepPivot - 1 : -1;
        Matrix.RefreshScale();
        Refresh();
    }

    private void EnsureSweep() {
        var m = _model;
        if (m is null || m.Sweeping) { return; }
        m.StartSweep();
        Matrix.Flash = [];
        ShowLibraryTable();
    }

    private void ToggleSweep() {
        if (_model is null) { return; }
        if (_timer.IsEnabled) {
            StopSweep();
            return;
        }
        EnsureSweep();
        _lastTick = Stopwatch.GetTimestamp();
        _pending = 0;
        _timer.Start();
        SweepButton.Content = "⏸ Pause";
        StepAndShow(0);
    }

    private void StopSweep() {
        _timer.Stop();
        SweepButton.Content = _model?.Sweeping == true ? "▶ Resume" : "▶ Watch it learn";
    }

    private void OnTick() {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        _pending += Speed * Math.Min(dt, 0.25);
        var steps = (int)_pending;
        _pending -= steps;
        if (steps > 0) { StepAndShow(steps); }
    }

    private void StepAndShow(int steps) {
        var m = _model;
        if (m is null || !m.Sweeping) { return; }
        var n = m.N;
        var before = new float[n];
        for (var v = 0; v < n; v++) { before[v] = m.Sweep[v * n + _goal]; }
        for (var i = 0; i < steps && m.Sweeping; i++) { m.StepSweep(); }

        if (m.SweepDone) {
            StopSweep();
            var total = (long)n * n;
            _glow = [];
            ActionText.Text =
                $"Done. Once every room was allowed as a stopover, every route was shortest. The animation's {total:N0} distances are " +
                (m.SweepCellsMatchingLibrary == total
                    ? "bit-identical to FloydWarshall.Solve."
                    : $"NOT all identical to FloydWarshall.Solve: {m.SweepCellsMatchingLibrary:N0} match.");
            ShowLibraryTable();
            UpdateField();
            return;
        }

        _glow = [];
        var known = 0;
        for (var v = 0; v < n; v++) {
            var d = m.Sweep[v * n + _goal];
            if (d < before[v]) { _glow.Add(v); }
            if (v != _goal && !float.IsPositiveInfinity(d)) { known++; }
        }
        var k = m.SweepPivot - 1;
        ActionText.Text =
            $"Step {m.SweepPivot:N0} of {n:N0}: routes may now stop over in room {k:N0} (outlined in blue); rooms with a blue dot are allowed stopovers so far. " +
            $"{known:N0} of {n - 1:N0} rooms know a way to the goal; {_glow.Count:N0} found a shorter one this step (glowing). " +
            "It learns the routes to every goal at once, so try changing the goal mid-way.";
        UpdateField();
    }

    private void MakeDoorFast(int a, int b) {
        var m = _model;
        if (m is null) { return; }
        StopSweep();
        var n = m.N;
        var nextBefore = (int[])_nextRoom.Clone();
        if (m.MakeDoorFast(a, b) is not { } r) {
            ActionText.Text = $"The door between rooms {a:N0} and {b:N0} is already fast.";
            return;
        }
        SweepButton.Content = "▶ Watch it learn";
        _glow = [];
        foreach (var cell in r.Changed) {
            if (cell % n == _goal) { _glow.Add(cell / n); }
        }
        ShowLibraryTable();
        Matrix.Flash = r.Changed;
        UpdateField();
        var rerouted = 0;
        for (var v = 0; v < n; v++) { if (_nextRoom[v] != nextBefore[v]) { rerouted++; } }
        ActionText.Text =
            $"Made the door between rooms {a:N0} and {b:N0} {ShortestPathsModel.FastDoorFactor:0} times faster (cyan). FloydWarshall.Relax updated " +
            $"{r.Changed.Length:N0} of {(long)n * n:N0} routes in {r.Time.TotalMilliseconds:N2} ms without re-solving. For this goal, {_glow.Count:N0} rooms got closer (glowing) " +
            $"and {rerouted:N0} now take a different door. " +
            (r.MatchesResolve
                ? $"A full re-solve agrees, up to float rounding (worst relative difference {r.WorstDifference:0.##E+0})."
                : "A full re-solve DISAGREES.");
    }

    private void Refresh() {
        var m = _model;
        if (m is null) { return; }
        var n = m.N;
        var sweeping = m.Sweeping;
        int[] Route(int from, int to) => sweeping ? m.SweepPathBetween(from, to) : m.PathBetween(from, to);
        float Dist(int from, int to) => (sweeping ? m.Sweep : m.Distances)[from * n + to];

        int[] path = [];
        var hover = "";
        if (_hoverCell.from >= 0) {
            path = Route(_hoverCell.from, _hoverCell.to);
            hover = Describe(_hoverCell.from, _hoverCell.to, Dist(_hoverCell.from, _hoverCell.to), path, "room " + _hoverCell.to.ToString("N0"), sweeping);
        } else if (_hoverDoor.a >= 0) {
            var (a, b) = _hoverDoor;
            var ab = m.HasDoor(a, b);
            var ba = m.HasDoor(b, a);
            var time = ab ? m.Weights[a * n + b] : m.Weights[b * n + a];
            var way = ab && ba ? "opens both ways" : ab ? $"only opens from {a:N0} into {b:N0}" : $"only opens from {b:N0} into {a:N0}";
            var fast = m.FastDoors.Contains((a, b));
            hover = $"Door between rooms {a:N0} and {b:N0}: {way}, takes {time:0.##} to walk through. " +
                    (fast ? "Already fast." : "Click to make it fast.");
        } else if (_hoverRoom >= 0) {
            if (_hoverRoom == _goal) {
                hover = $"Room {_goal:N0} is the goal. Click another room to move it.";
            } else {
                path = Route(_hoverRoom, _goal);
                hover = Describe(_hoverRoom, _goal, Dist(_hoverRoom, _goal), path, "the goal", sweeping) + " Click to make this room the goal.";
            }
        }
        Plan.Path = path.Length > 1 ? path : null;
        Matrix.LinkedVertex = _goal;
        PlanCaption.Text = sweeping
            ? "What the algorithm knows so far: arrows and colours use only the stopovers allowed yet"
            : $"Goal: room {_goal:N0}. Arrows point through the door to take.";
        HoverText.Text = hover;
        Plan.InvalidateVisual();
        Matrix.InvalidateVisual();
    }

    private static string Describe(int from, int to, float distance, int[] path, string toName, bool sweeping) {
        if (float.IsPositiveInfinity(distance)) {
            return sweeping
                ? $"Room {from:N0} knows no way to {toName} yet."
                : $"Room {from:N0} has no way to {toName}: walls and one-way doors block every route.";
        }
        return $"Room {from:N0} to {toName}: through {path.Length - 1:N0} doors, taking {distance:0.##}" + (sweeping ? " (best so far)." : ".");
    }
}
