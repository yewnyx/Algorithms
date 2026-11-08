using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// One cell per task: pinned tasks first, then the rotation in cursor order. Colour shows how long ago
/// each task last ran, relative to the rotation length, so the cursor drags a bright wake that fades
/// until the next pass. Overdue cells turn orange.
/// </summary>
public sealed class TaskGridView : Control {
    private readonly PixelSurface _surface = new();
    private FrameScheduler? _scheduler;
    private int[] _order = [];
    private int[] _cellOf = [];
    private int _pinnedCount;
    private int _cell = 1;
    private int _gap;
    private int _cols = 1;
    private int _hover = -1;

    public event Action<int>? HoverChanged;

    public int LinkedHighlight { get; set; } = -1;

    public TaskGridView() {
        ClipToBounds = true;
    }

    public void SetScheduler(FrameScheduler? scheduler) {
        _scheduler = scheduler;
        _hover = -1;
        if (scheduler is null) {
            _order = [];
            _cellOf = [];
        } else {
            _order = [.. scheduler.PinnedTasks.ToArray(), .. scheduler.RotationOrder.ToArray()];
            _pinnedCount = scheduler.PinnedTasks.Length;
            _cellOf = new int[scheduler.Tasks.Count];
            for (var k = 0; k < _order.Length; k++) { _cellOf[_order[k]] = k; }
        }
        InvalidateVisual();
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private void ComputeGrid(int width, int height, int n) {
        n = Math.Max(1, n);
        var cell = Math.Clamp((int)Math.Floor(Math.Sqrt((double)width * height / n)), 1, 24);
        while (cell > 1) {
            var cols = Math.Max(1, width / cell);
            if ((n + cols - 1) / cols * cell <= height) { break; }
            cell--;
        }
        _cell = cell;
        _gap = cell >= 5 ? 1 : 0;
        _cols = Math.Max(1, width / cell);
    }

    /// <summary>Fresh is bright cyan, a full rotation old is dark blue, older than that is orange.</summary>
    private static uint Staleness(double ratio, bool changed) {
        if (ratio > 1.05) { return 0xFFFF8A3D; }
        var t = Math.Clamp(ratio, 0, 1);
        var v = 1.0 - 0.72 * Math.Pow(t, 0.6);
        var color = Palette.Hsv(0.53 + 0.08 * t, 0.55 + 0.35 * t, v);
        if (!changed) { return color; }
        // Visits with changed inputs ran every node: lift them towards white.
        var r = (byte)(((color >> 16) & 0xFF) + (255 - ((color >> 16) & 0xFF)) * 0.35);
        var g = (byte)(((color >> 8) & 0xFF) + (255 - ((color >> 8) & 0xFF)) * 0.35);
        var b = (byte)((color & 0xFF) + (255 - (color & 0xFF)) * 0.35);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        var scheduler = _scheduler;
        if (scheduler is null || _order.Length == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        _surface.Resize(width, height);
        _surface.Clear(Palette.Background);
        ComputeGrid(width, height, _order.Length);

        var now = scheduler.FrameCount - 1;
        var rotation = Math.Max(1.0, Math.Max(scheduler.AverageRotationFrames, scheduler.CurrentRotationFrames));
        var size = _cell - _gap;
        var cursorTask = scheduler.RotationOrder.Length > 0 ? scheduler.RotationOrder[scheduler.Cursor] : -1;

        for (var k = 0; k < _order.Length; k++) {
            var t = _order[k];
            var x = k % _cols * _cell;
            var y = k / _cols * _cell;
            if (y >= height) { break; }
            var last = scheduler.LastVisitFrame[t];
            uint color;
            if (last < 0) {
                color = Palette.SingletonDim;
            } else {
                var age = now - last;
                color = k < _pinnedCount ? 0xFFE57373 : Staleness(age / rotation, scheduler.LastChanged[t]);
            }
            if (t == cursorTask) { color = 0xFFFFFFFF; }
            if (t == LinkedHighlight || t == _hover) { color = 0xFFFFD54F; }
            _surface.FillRect(x, y, size, size, color);
        }

        _surface.Upload();
        if (_surface.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        // Ring the highlighted cell so it can be found at one pixel per cell.
        var highlight = _hover >= 0 ? _hover : LinkedHighlight;
        if (highlight >= 0 && highlight < _cellOf.Length) {
            var k = _cellOf[highlight];
            var cell = _cell / scaling;
            var pad = Math.Max(2, cell * 0.5);
            var rect = new Rect(k % _cols * cell - pad, k / _cols * cell - pad, cell + 2 * pad, cell + 2 * pad);
            context.DrawRectangle(null, new Pen(Brushes.Gold, 2), rect);
        }
    }

    private int TaskAt(Point p) {
        if (_order.Length == 0) { return -1; }
        var scaling = Scaling;
        var x = (int)(p.X * scaling) / _cell;
        var y = (int)(p.Y * scaling) / _cell;
        if (x < 0 || x >= _cols || y < 0) { return -1; }
        var k = y * _cols + x;
        return k < _order.Length ? _order[k] : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var t = TaskAt(e.GetPosition(this));
        if (t == _hover) { return; }
        _hover = t;
        HoverChanged?.Invoke(t);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hover == -1) { return; }
        _hover = -1;
        HoverChanged?.Invoke(-1);
        InvalidateVisual();
    }
}
