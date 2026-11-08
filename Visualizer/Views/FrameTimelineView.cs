using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// One bar per frame, newest on the right. Each bar stacks queued edits (grey), pinned tasks (red) and
/// the rotation (blue, alternating shade per rotation). The white tick on each bar is where the budget
/// ran out for that frame. Scroll the wheel to look back through history.
/// </summary>
public sealed class FrameTimelineView : Control {
    private const int BarWidth = 3;
    private const int Pitch = BarWidth + 1;
    private const uint EditColor = 0xFF5F6675;
    private const uint PinnedColor = 0xFFE57373;
    private const uint RotationColorA = 0xFF4FC3F7;
    private const uint RotationColorB = 0xFF2F7FB0;
    private const uint SeparatorColor = 0xFF14161B;
    private const uint HighlightColor = 0xFFFFD54F;

    private readonly PixelSurface _surface = new();
    private FrameScheduler? _scheduler;
    private long _scrollBack;
    private int _hover = -1;
    private long _lastNewest = -1;
    private int _lastPitchCount;
    private double _yMaxUs;

    public event Action<int>? HoverChanged;

    /// <summary>Task to highlight in every visible frame, or -1.</summary>
    public int LinkedHighlight { get; set; } = -1;

    public FrameTimelineView() {
        ClipToBounds = true;
    }

    public void SetScheduler(FrameScheduler? scheduler) {
        _scheduler = scheduler;
        _scrollBack = 0;
        _hover = -1;
        InvalidateVisual();
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        var scheduler = _scheduler;
        if (scheduler is null || scheduler.FrameCount == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        _surface.Resize(width, height);
        _surface.Clear(Palette.Background);

        var top = (int)(30 * scaling);
        var bottom = (int)(20 * scaling);
        var plotHeight = Math.Max(10, height - top - bottom);
        var baseY = top + plotHeight;

        var count = Math.Max(1, width / Pitch);
        _scrollBack = Math.Clamp(_scrollBack, 0, Math.Max(0, Math.Min(scheduler.FrameCount, scheduler.HistoryCapacity) - count));
        var newest = scheduler.FrameCount - 1 - _scrollBack;
        _lastNewest = newest;
        _lastPitchCount = count;

        // Y scale: at least 2.5 budgets, and enough for the tallest visible frame.
        var budget = scheduler.Settings.BudgetUs;
        var yMax = budget * 2.5;
        for (var i = 0; i < count; i++) {
            var f = scheduler.GetFrame(newest - i);
            if (f is not null && f.TotalUs * 1.1 > yMax) { yMax = f.TotalUs * 1.1; }
        }
        _yMaxUs = yMax;
        double Px(double us) => us / yMax * plotHeight;

        for (var i = 0; i < count; i++) {
            var frame = scheduler.GetFrame(newest - i);
            if (frame is null) { break; }
            var x = width - (i + 1) * Pitch;
            if (x < 0) { break; }

            var y = (double)baseY;
            if (frame.EditUs > 0) {
                var y1 = y - Px(frame.EditUs);
                var iy = (int)Math.Round(y1);
                _surface.FillRect(x, iy, BarWidth, Math.Max(1, (int)Math.Round(y) - iy), EditColor);
                y = y1;
            }
            var rotationColor = frame.Rotation % 2 == 0 ? RotationColorA : RotationColorB;
            var budgetMarkY = baseY - Px(frame.EditUs + budget);

            // Segments drawn individually so the hovered task and separators line up with the data.
            foreach (var segment in frame.Segments) {
                var y1 = y - Px(segment.CostUs);
                var iy0 = (int)Math.Round(y);
                var iy1 = (int)Math.Round(y1);
                var color = segment.Task == LinkedHighlight || segment.Task == _hover
                    ? HighlightColor
                    : segment.Lane == Lane.Pinned ? PinnedColor : rotationColor;
                if (iy0 > iy1) {
                    _surface.FillRect(x, iy1, BarWidth, iy0 - iy1, color);
                    if (iy0 - iy1 >= 4) { _surface.FillRect(x, iy1, BarWidth, 1, SeparatorColor); }
                } else if (color == HighlightColor) {
                    _surface.FillRect(x - 1, iy1 - 1, BarWidth + 2, 2, color);
                }
                y = y1;
            }

            // Budget mark, and the rotation boundary after a frame that finished one.
            _surface.FillRect(x - 1, (int)Math.Round(budgetMarkY), BarWidth + 2, 1, 0xFFFFFFFF);
            if (frame.CompletedRotation) { _surface.FillRect(x + BarWidth, top, 1, plotHeight, 0xFF4A5160); }
        }

        _surface.Upload();
        if (_surface.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        // Reference lines and labels in DIPs.
        var s = 1 / scaling;
        var label = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDC));
        var dim = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
        void HLine(double us, IBrush brush, string text) {
            if (us > yMax) { return; }
            var yy = (baseY - Px(us)) * s;
            context.DrawLine(new Pen(brush, 1, new DashStyle([4, 4], 0)), new Point(0, yy), new Point(bounds.Width, yy));
            DrawText(context, text, new Point(4, yy - 16), label, 11);
        }
        HLine(budget, dim, $"budget {scheduler.Settings.BudgetMs:0.###} ms");
        var frameUs = scheduler.Settings.FrameMs * 1000;
        HLine(frameUs, new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8A, 0x65)), $"whole frame at {scheduler.Settings.Fps:0} fps = {scheduler.Settings.FrameMs:0.0} ms");
        var offScale = frameUs > yMax ? $"   whole frame {scheduler.Settings.FrameMs:0.0} ms is off the top" : "";
        var caption = $"y axis 0 to {yMax / 1000:0.00} ms   {count:N0} frames shown{(_scrollBack > 0 ? $", {_scrollBack:N0} back from live" : "")}{offScale}   wheel scrolls history";
        DrawText(context, caption, new Point(6, (baseY + 3) * s), label, 11);
    }

    private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size) {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x13, 0x18)), null, new Rect(at.X - 2, at.Y, formatted.Width + 4, formatted.Height));
        context.DrawText(formatted, at);
    }

    private int TaskAt(Point p) {
        var scheduler = _scheduler;
        if (scheduler is null || _lastNewest < 0) { return -1; }
        var scaling = Scaling;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);
        var x = (int)(p.X * scaling);
        var i = (width - x - 1) / Pitch;
        if (i < 0 || i >= _lastPitchCount) { return -1; }
        var frame = scheduler.GetFrame(_lastNewest - i);
        if (frame is null) { return -1; }

        var top = (int)(30 * scaling);
        var bottom = (int)(20 * scaling);
        var plotHeight = Math.Max(10, height - top - bottom);
        var us = (top + plotHeight - p.Y * scaling) / plotHeight * _yMaxUs;
        if (us < frame.EditUs) { return -1; }
        var acc = (double)frame.EditUs;
        foreach (var segment in frame.Segments) {
            acc += segment.CostUs;
            if (us <= acc) { return segment.Task; }
        }
        return -1;
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        _scrollBack += (long)Math.Round(-e.Delta.Y * Math.Max(1, _lastPitchCount / 8));
        if (_scrollBack < 0) { _scrollBack = 0; }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) {
            _scrollBack = 0;
            InvalidateVisual();
        }
    }
}
