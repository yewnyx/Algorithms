using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Replays a frame-budgeted round-robin scheduler over many tasks, so the trade it makes is visible:
/// frame time stays near the budget, and the cost of more work shows up as rotation length, which is
/// the latency every task sees.
/// </summary>
public partial class TimeSlicingPanel : UserControl {
    private const int MaxStepsPerTick = 4000;

    private readonly DispatcherTimer _timer;
    private TaskInfo[] _tasks = [];
    private FrameScheduler? _scheduler;
    private string _splitText = "";
    private CancellationTokenSource? _generation;
    private long _lastTick;
    private double _pending;
    private int _hover = -1;

    public TimeSlicingPanel() {
        InitializeComponent();

        GenerateButton.Click += async (_, _) => await GenerateAsync();
        PlayButton.Click += (_, _) => TogglePlay();
        StepButton.Click += (_, _) => { EnsureScheduler(); _scheduler!.Step(); Refresh(); };
        ResetButton.Click += (_, _) => { RebuildScheduler(); Refresh(); };
        SpeedSlider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty) { SpeedLabel.Text = $"{Speed:0} fps"; }
        };
        SpeedLabel.Text = $"{Speed:0} fps";

        // Scheduler settings rebuild the scheduler; the tasks stay.
        foreach (var box in new[] { BudgetMs, PinCap, Fps, InputUs, ComputeUs, OutputUs, VisitUs, EditUs, JitterPercent }) {
            box.ValueChanged += (_, _) => { RebuildScheduler(); Refresh(); };
        }

        Timeline.HoverChanged += OnHover;
        TaskGrid.HoverChanged += OnHover;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => OnTick();

        AttachedToVisualTree += async (_, _) => {
            if (_scheduler is null) { await GenerateAsync(); }
        };
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Simulated frames per real second.</summary>
    private double Speed => Math.Round(Math.Pow(10, SpeedSlider.Value));

    private SchedulerSettings Settings => new(
        (double)(BudgetMs.Value ?? 1),
        (int)(PinCap.Value ?? 0),
        (double)(InputUs.Value ?? 0),
        (double)(ComputeUs.Value ?? 0),
        (double)(OutputUs.Value ?? 0),
        (double)(VisitUs.Value ?? 0),
        (double)(EditUs.Value ?? 0),
        (double)(JitterPercent.Value ?? 0) / 100.0,
        (double)(Fps.Value ?? 60),
        (int)(Seed.Value ?? 0));

    private async Task GenerateAsync() {
        _generation?.Cancel();
        var cts = _generation = new CancellationTokenSource();
        var spec = new TaskGraphSpec(
            (int)(Count.Value ?? 0),
            (double)(LonePercent.Value ?? 0) / 100.0,
            (double)(MeanSize.Value ?? 2),
            (int)(MaxSize.Value ?? 2),
            (double)(PinnedPercent.Value ?? 0) / 100.0,
            (double)(ActivePercent.Value ?? 0) / 100.0,
            (int)(Seed.Value ?? 0));

        var wasRunning = _timer.IsEnabled;
        Stop();
        GenerateButton.IsEnabled = false;
        HeadlineText.Text = $"Generating {spec.Count:N0} task graphs…";
        SubheadText.Text = "";
        try {
            var result = await Task.Run(() => {
                var generated = TaskGraphGenerator.Generate(spec, cts.Token);
                if (generated.NodeCount == 0) { return (generated, Array.Empty<TaskInfo>(), true, TimeSpan.Zero); }
                var (tasks, matches, time) = TaskSplit.Split(generated);
                return (generated, tasks, matches, time);
            }, cts.Token);
            if (cts.IsCancellationRequested) { return; }

            var (g, tasks, ok, splitTime) = result;
            _tasks = tasks;
            _splitText =
                $"{g.NodeCount:N0} nodes and {g.From.Length:N0} edges split into {tasks.Length:N0} tasks " +
                $"(ConnectedComponents, {splitTime.TotalMilliseconds:N1} ms{(ok ? ", matches the generated graphs" : ", DIFFERS FROM THE GENERATED GRAPHS")}).";
            RebuildScheduler();
            Refresh();
            if (wasRunning) { TogglePlay(); }
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            HeadlineText.Text = "Failed: " + ex.Message;
        } finally {
            if (_generation == cts) { GenerateButton.IsEnabled = true; }
        }
    }

    private void EnsureScheduler() {
        if (_scheduler is null) { RebuildScheduler(); }
    }

    private void RebuildScheduler() {
        if (!IsInitialized) { return; }
        _scheduler = new FrameScheduler(_tasks, Settings);
        _pending = 0;
        Timeline.SetScheduler(_scheduler);
        TaskGrid.SetScheduler(_scheduler);
    }

    private void TogglePlay() {
        EnsureScheduler();
        if (_timer.IsEnabled) {
            Stop();
            return;
        }
        _lastTick = Stopwatch.GetTimestamp();
        _pending = 0;
        _timer.Start();
        PlayButton.Content = "⏸ Pause";
    }

    private void Stop() {
        _timer.Stop();
        PlayButton.Content = "▶ Play";
    }

    private void OnTick() {
        if (_scheduler is null) { return; }
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        _pending += Speed * Math.Min(dt, 0.25);
        var steps = Math.Min(MaxStepsPerTick, (int)_pending);
        _pending -= steps;
        for (var i = 0; i < steps; i++) { _scheduler.Step(); }
        if (steps > 0) { Refresh(); }
    }

    private void Refresh() {
        var scheduler = _scheduler;
        Timeline.InvalidateVisual();
        TaskGrid.InvalidateVisual();
        if (scheduler is null) { return; }
        var s = scheduler.Settings;

        var unbudgetedMs = scheduler.UnbudgetedUs / 1000;
        var aloneFps = unbudgetedMs > 0 ? 1000 / unbudgetedMs : double.PositiveInfinity;
        var rotation = scheduler.AverageRotationFrames > 0 ? scheduler.AverageRotationFrames : 0;
        var pinnedUs = 0.0;
        foreach (var p in scheduler.PinnedTasks) { pinnedUs += scheduler.ExpectedCost(scheduler.Tasks[p]); }

        HeadlineText.Text =
            $"Running every task every frame would take {unbudgetedMs:0.00} ms per frame" +
            (double.IsInfinity(aloneFps) ? "." : $", which alone limits the frame rate to {aloneFps:0} fps.") +
            (rotation > 0
                ? $" Budgeted at {s.BudgetMs:0.###} ms, a full rotation takes {rotation:0.0} frames, {rotation * s.FrameMs:0} ms at {s.Fps:0} fps." +
                  " That rotation is the latency: a change that arrives just after its task ran waits a whole rotation."
                : $" Budgeted at {s.BudgetMs:0.###} ms, press Play or Step to measure how many frames a full rotation takes.");
        SubheadText.Text =
            $"Worst gap between visits so far: {scheduler.WorstGap:N0} frames ({scheduler.WorstGap * s.FrameMs:0} ms). " +
            $"Worst frame {scheduler.WorstFrameUs / 1000:0.000} ms; the rotation stops only after crossing the budget, overshooting by {scheduler.MeanOvershootUs:0.0} µs on average and {scheduler.MaxOvershootUs:0.0} µs at most. " +
            $"Pinned: {scheduler.PinnedTasks.Length} task(s) every frame, about {pinnedUs:0.0} µs" +
            (scheduler.OverCap > 0 ? $", {scheduler.OverCap} more over the cap run in the rotation." : ".") +
            $"\n{_splitText}";

        StatusText.Text =
            $"frame {scheduler.FrameCount:N0}  rotation {scheduler.RotationsCompleted:N0}  cursor {scheduler.Cursor:N0}/{scheduler.RotationOrder.Length:N0}  " +
            $"current rotation {scheduler.CurrentRotationFrames:N0} frames  last rotation {scheduler.LastRotationFrames:N0}";
        UpdateHoverText();
    }

    private void OnHover(int task) {
        if (task == _hover) { return; }
        _hover = task;
        Timeline.LinkedHighlight = TaskGrid.LinkedHighlight = task;
        Timeline.InvalidateVisual();
        TaskGrid.InvalidateVisual();
        UpdateHoverText();
    }

    private void UpdateHoverText() {
        var scheduler = _scheduler;
        if (scheduler is null || _hover < 0 || _hover >= scheduler.Tasks.Count) {
            HoverText.Text = "";
            return;
        }
        var info = scheduler.Tasks[_hover];
        var lane = scheduler.PinnedTasks.Contains(_hover) ? "pinned" : "rotation";
        var last = scheduler.LastVisitFrame[_hover];
        HoverText.Text =
            $"task {_hover:N0}: {info.Inputs} input, {info.Computes} compute, {info.Outputs} output nodes  |  {lane}  " +
            $"expected {scheduler.ExpectedCost(info):0.0} µs per visit  |  visits {scheduler.Visits[_hover]:N0}  " +
            (last < 0 ? "not run yet" : $"last ran {scheduler.FrameCount - 1 - last:N0} frames ago{(scheduler.LastChanged[_hover] ? " (changed)" : "")}") +
            $"  worst gap {scheduler.MaxGap[_hover]:N0} frames ({scheduler.MaxGap[_hover] * scheduler.Settings.FrameMs:0} ms)";
    }
}
