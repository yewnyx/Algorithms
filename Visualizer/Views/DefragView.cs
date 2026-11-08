using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// The "defrag" grid: one cell per vertex, laid out in execution order (dependencies first),
/// row-major. Cells light up as the scan processes them.
/// </summary>
public sealed class DefragView : Control {
    private readonly PixelSurface _surface = new();
    private GraphModel? _model;
    private ScanState? _state;
    private int _cell = 1;
    private int _gap;
    private int _cols = 1;
    private int _hover = -1;

    public event Action<int>? HoverChanged;

    public ColorMode ColorMode { get; set; } = ColorMode.Island;

    /// <summary>Vertex highlighted from elsewhere (the hover in a graph pane), or -1.</summary>
    public int LinkedHighlight { get; set; } = -1;

    public DefragView() {
        ClipToBounds = true;
    }

    public void SetModel(GraphModel? model, ScanState? state) {
        _model = model;
        _state = state;
        _hover = -1;
        InvalidateVisual();
    }

    public void SetState(ScanState? state) {
        _state = state;
        InvalidateVisual();
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private void ComputeGrid(int width, int height) {
        var n = Math.Max(1, _model?.N ?? 1);
        var cell = Math.Max(1, (int)Math.Floor(Math.Sqrt((double)width * height / n)));
        cell = Math.Min(cell, 28);
        while (cell > 1) {
            var cols = Math.Max(1, width / cell);
            var rows = (n + cols - 1) / cols;
            if (rows * cell <= height) { break; }
            cell--;
        }
        _cell = cell;
        _gap = cell >= 5 ? 1 : 0;
        _cols = Math.Max(1, width / cell);
    }

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        if (_model is null || _model.N == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        _surface.Resize(width, height);
        ComputeGrid(width, height);

        var model = _model;
        var state = _state;
        var mode = ColorMode;
        var n = model.N;
        var cell = _cell;
        var size = cell - _gap;
        var cols = _cols;

        _surface.Clear(Palette.Background);
        var pixels = _surface.Pixels;
        var order = model.ExecutionOrder;

        if (size == 1) {
            // Fast path: one pixel per vertex.
            for (var k = 0; k < n; k++) {
                var x = k % cols;
                var y = k / cols;
                if (y >= height) { break; }
                pixels[y * width + x] = Palette.Vertex(model, state, mode, order[k]);
            }
        } else {
            for (var k = 0; k < n; k++) {
                var x = k % cols * cell;
                var y = k / cols * cell;
                if (y >= height) { break; }
                _surface.FillRect(x, y, size, size, Palette.Vertex(model, state, mode, order[k]));
            }
        }

        // Scan cursors: the vertex each worker is on right now.
        if (state is not null) {
            for (var w = 0; w < state.WorkerCount; w++) {
                var v = Volatile.Read(ref state.WorkerCurrent[w].Value);
                if (v < 0) { continue; }
                var k = model.ExecutionIndex[v];
                var x = k % cols * cell;
                var y = k / cols * cell;
                var pad = Math.Max(0, (cell - size) / 2);
                _surface.FillRect(x - pad - 1, y - pad - 1, size + 2 * pad + 2, size + 2 * pad + 2, Palette.Cursor);
                if (size >= 3) { _surface.FillRect(x + 1, y + 1, size - 2, size - 2, Palette.Worker(w)); }
            }
        }

        _surface.Upload();
        if (_surface.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        DrawRing(context, LinkedHighlight, Colors.White, scaling);
        DrawRing(context, _hover, Colors.Gold, scaling);
    }

    private void DrawRing(DrawingContext context, int vertex, Color color, double scaling) {
        if (_model is null || vertex < 0 || vertex >= _model.N) { return; }
        var k = _model.ExecutionIndex[vertex];
        var cell = _cell / scaling;
        var x = k % _cols * cell;
        var y = k / _cols * cell;
        var pad = Math.Max(2, cell * 0.5);
        var rect = new Rect(x - pad, y - pad, cell + 2 * pad, cell + 2 * pad);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(color), 2), rect);
    }

    private int VertexAt(Point p) {
        if (_model is null || _model.N == 0) { return -1; }
        var scaling = Scaling;
        var x = (int)(p.X * scaling) / _cell;
        var y = (int)(p.Y * scaling) / _cell;
        if (x < 0 || x >= _cols || y < 0) { return -1; }
        var k = y * _cols + x;
        return k < _model.N ? _model.ExecutionOrder[k] : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        SetHover(VertexAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int vertex) {
        if (vertex == _hover) { return; }
        _hover = vertex;
        HoverChanged?.Invoke(vertex);
        InvalidateVisual();
    }
}
