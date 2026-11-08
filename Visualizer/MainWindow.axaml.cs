using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yewnyx.Algorithms.Visualizer.Model;
using Yewnyx.Algorithms.Visualizer.Views;

namespace Yewnyx.Algorithms.Visualizer;

public partial class MainWindow : Window {
    private readonly DispatcherTimer _frame;
    private GraphModel? _model;
    private Scanner? _scanner;
    private CancellationTokenSource? _generation;
    private int _hover = -1;
    private string _buildStats = "";
    private string _verification = "";
    private long _lastStatusTick;

    public MainWindow() {
        InitializeComponent();

        BeforeView.Layout = LayoutKind.IndexGrid;
        AfterView.Layout = LayoutKind.WaveLayered;

        BeforeView.HoverChanged += OnHover;
        AfterView.HoverChanged += OnHover;
        GridView.HoverChanged += OnHover;

        GenerateButton.Click += async (_, _) => await GenerateAsync();
        PlayButton.Click += (_, _) => TogglePlay();
        ResetButton.Click += (_, _) => ResetScan();
        ModeBox.SelectionChanged += (_, _) => { ResetScan(); ApplyColorMode(); };
        ColorBox.SelectionChanged += (_, _) => ApplyColorMode();
        ShapeBox.SelectionChanged += async (_, _) => {
            Layers.IsEnabled = Shape == GraphShape.Custom;
            if (IsLoaded) { await GenerateAsync(); }
        };
        EdgesCheck.IsCheckedChanged += (_, _) => {
            BeforeView.DrawEdges = AfterView.DrawEdges = EdgesCheck.IsChecked == true;
            BeforeView.InvalidateLayout();
            AfterView.InvalidateLayout();
        };
        CostSlider.PropertyChanged += (_, e) => {
            if (e.Property != Slider.ValueProperty) { return; }
            CostLabel.Text = FormatCost(NodeCost);
            if (_scanner is not null) { _scanner.NodeCostMicroseconds = NodeCost; }
        };
        CostLabel.Text = FormatCost(NodeCost);
        Workers.Value = Math.Clamp(Environment.ProcessorCount / 2, 2, 16);

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += (_, _) => OnFrame();

        Opened += async (_, _) => await GenerateAsync();
    }

    private double NodeCost => Math.Pow(10, CostSlider.Value);

    private static string FormatCost(double microseconds) =>
        microseconds >= 1000 ? $"{microseconds / 1000:0.#} ms" :
        microseconds >= 10 ? $"{microseconds:0} µs" : $"{microseconds:0.0} µs";

    private ScanMode Mode => ModeBox.SelectedIndex == 1 ? ScanMode.Parallel : ScanMode.Serial;

    private GraphShape Shape => ShapeBox.SelectedIndex switch { 1 => GraphShape.Deep, 2 => GraphShape.Custom, _ => GraphShape.Wide };

    private void ApplyColorMode() {
        var mode = ColorBox.SelectedIndex switch { 1 => ColorMode.Worker, 2 => ColorMode.Wave, _ => ColorMode.Island };
        BeforeView.ColorMode = AfterView.ColorMode = GridView.ColorMode = mode;
        Redraw();
    }

    private async Task GenerateAsync() {
        _generation?.Cancel();
        var cts = _generation = new CancellationTokenSource();
        var ct = cts.Token;

        ResetScan();
        var spec = new GraphSpec(
            (int)(NodeCount.Value ?? 1),
            (double)(IslandSize.Value ?? 1),
            (double)(SoloPercent.Value ?? 0) / 100.0,
            (int)(Layers.Value ?? 1),
            (double)(Links.Value ?? 0),
            (double)(Chords.Value ?? 0),
            (double)(LocalityPercent.Value ?? 0) / 100.0,
            (int)(Seed.Value ?? 0));
        spec = spec with { Layers = GraphSpec.LayersFor(Shape, spec.EstimatedIslands, spec.Layers) };
        Layers.Value = spec.Layers;

        GenerateButton.IsEnabled = PlayButton.IsEnabled = false;
        StatsText.Text = $"Generating {spec.NodeCount:N0} vertices…";
        ScanText.Text = "";

        try {
            var sw = Stopwatch.StartNew();
            var model = await Task.Run(() => {
                var graph = GraphGenerator.Generate(spec, ct);
                var generated = sw.Elapsed;
                var built = GraphModel.Build(graph, spec.NodeCount, ct);
                return (built, generated);
            });
            if (ct.IsCancellationRequested) { return; }

            _model = model.built;
            var m = _model;
            var solo = m.IslandCount - m.NonTrivialIslands;
            var truth = m.MatchesTruth switch { true => "  matches generated islands", false => "  DIFFERS FROM GENERATED ISLANDS", _ => "" };
            _buildStats =
                $"{m.N:N0} vertices  {m.E:N0} edges  →  {m.IslandCount:N0} islands ({solo:N0} solo, largest {m.MaxIslandSize:N0}){truth}  " +
                $"{m.WaveCount:N0} waves (~{m.IslandCount / Math.Max(1, m.WaveCount):N0} islands each)  |  generate {model.generated.TotalMilliseconds:N0} ms  Tarjan {m.TarjanTime.TotalMilliseconds:N1} ms  total {sw.Elapsed.TotalMilliseconds:N0} ms";
            StatsText.Text = _buildStats;
            BeforeView.SetModel(m, null);
            AfterView.SetModel(m, null);
            GridView.SetModel(m, null);
            UpdateScanText();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            StatsText.Text = "Failed: " + ex.Message;
        } finally {
            if (_generation == cts) {
                GenerateButton.IsEnabled = true;
                PlayButton.IsEnabled = _model is not null;
            }
        }
    }

    private void TogglePlay() {
        if (_model is null) { return; }
        if (_scanner is null || !_scanner.HasStarted) {
            _scanner?.Dispose();
            _scanner = new Scanner(_model, Mode, (int)(Workers.Value ?? 1), NodeCost);
            _verification = "";
            BeforeView.SetState(_scanner.State);
            AfterView.SetState(_scanner.State);
            GridView.SetState(_scanner.State);
            if (ColorBox.SelectedIndex == 0 && Mode == ScanMode.Parallel) { ColorBox.SelectedIndex = 1; }
            _scanner.Start();
            _frame.Start();
            PlayButton.Content = "⏸ Pause";
            return;
        }
        if (_scanner.State.Finished) { return; }
        if (_scanner.IsPaused) {
            _scanner.Resume();
            PlayButton.Content = "⏸ Pause";
        } else {
            _scanner.Pause();
            PlayButton.Content = "▶ Resume";
        }
    }

    private void ResetScan() {
        _scanner?.Dispose();
        _scanner = null;
        _verification = "";
        _frame.Stop();
        PlayButton.Content = "▶ Play";
        BeforeView.SetState(null);
        AfterView.SetState(null);
        GridView.SetState(null);
        UpdateScanText();
    }

    private void OnFrame() {
        Redraw();
        var now = Stopwatch.GetTimestamp();
        if (now - _lastStatusTick > Stopwatch.Frequency / 10) {
            _lastStatusTick = now;
            UpdateScanText();
        }
        if (_scanner is { } scanner && scanner.State.Finished && !scanner.IsRunning) {
            _frame.Stop();
            PlayButton.Content = "▶ Play again";
            var violations = scanner.CountOrderViolations();
            _verification = violations == 0
                ? $"  |  verified: every cross-island edge finished before its dependent started"
                : $"  |  ORDER VIOLATIONS: {violations:N0}";
            UpdateScanText();
            Redraw();
        }
    }

    private void UpdateScanText() {
        if (_scanner is null || _model is null) {
            ScanText.Text = _model is null ? "" : "Press Play to scan the execution order. Serial walks it linearly; Parallel runs islands as their dependencies complete.";
            return;
        }
        var state = _scanner.State;
        var done = state.Done;
        var elapsed = state.Elapsed;
        var rate = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
        var workers = "";
        if (state.WorkerCount > 1) {
            var parts = new string[state.WorkerCount];
            for (var w = 0; w < state.WorkerCount; w++) { parts[w] = $"w{w}:{Volatile.Read(ref state.WorkerDone[w].Value):N0}"; }
            workers = "  [" + string.Join(" ", parts) + "]";
        }
        var status = state.Finished ? "done" : _scanner.IsPaused ? "paused" : "running";
        ScanText.Text = $"{_scanner.Mode} ×{state.WorkerCount}  {status}  {done:N0}/{_model.N:N0}  {elapsed.TotalSeconds:0.00}s  {rate:N0} nodes/s{workers}{_verification}";
    }

    private void Redraw() {
        BeforeView.InvalidateVisual();
        AfterView.InvalidateVisual();
        GridView.InvalidateVisual();
    }

    private void OnHover(int vertex) {
        if (vertex == _hover) { return; }
        _hover = vertex;
        BeforeView.LinkedHighlight = AfterView.LinkedHighlight = GridView.LinkedHighlight = vertex;
        if (_model is null || vertex < 0) {
            HoverText.Text = "";
        } else {
            var island = _model.IslandOf[vertex];
            var worker = _scanner is null ? ScanState.NotProcessed : Volatile.Read(ref _scanner.State.ProcessedBy[vertex]);
            HoverText.Text =
                $"vertex {vertex:N0}  out {_model.OutNeighbours(vertex).Length} (orange)  in {_model.InNeighbours(vertex).Length} (blue)  |  island {island:N0} (size {_model.IslandSize[island]:N0}, wave {_model.WaveOf[island]:N0})  " +
                $"execution slot {_model.ExecutionIndex[vertex]:N0}" + (worker == ScanState.NotProcessed ? "" : $"  |  processed by worker {worker}");
        }
        if (!_frame.IsEnabled) { Redraw(); }
    }

    protected override void OnClosed(EventArgs e) {
        _generation?.Cancel();
        _scanner?.Dispose();
        _frame.Stop();
        base.OnClosed(e);
    }
}
