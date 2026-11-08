using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// An n by n distance matrix as a heatmap: row = from, column = to. Near is bright, far is dark, and
/// unreachable cells are left blank. Rows and columns can be shown in any vertex order, and blocks along
/// the diagonal can be outlined. Cells are whole pixels when each gets at least two; otherwise each pixel
/// samples one cell, so the matrix always fills the pane.
/// </summary>
public sealed class MatrixView : Control {
    private readonly PixelSurface _surface = new();
    private float[] _matrix = [];
    private int _n;
    private int[] _order = [];
    private float _maxFinite = 1;
    private double _origin;
    private double _side;
    private (int from, int to) _hover = (-1, -1);

    public event Action<int, int>? HoverChanged;

    /// <summary>Diagonal blocks to outline, as (first position, size) in display order.</summary>
    public IReadOnlyList<(int start, int size)> Blocks { get; set; } = [];

    /// <summary>A pivot to mark with a row and a column line, or -1.</summary>
    public int Pivot { get; set; } = -1;

    /// <summary>Cells to flash, as from * n + to.</summary>
    public IReadOnlyCollection<int> Flash { get; set; } = [];

    /// <summary>A vertex whose row and column are marked, or -1.</summary>
    public int LinkedVertex { get; set; } = -1;

    public MatrixView() {
        ClipToBounds = true;
    }

    /// <param name="order">Display position to vertex.</param>
    public void SetMatrix(float[] matrix, int n, int[] order) {
        _matrix = matrix;
        _n = n;
        _order = order;
        RefreshScale();
        InvalidateVisual();
    }

    /// <summary>Call after the matrix changes in place.</summary>
    public void RefreshScale() {
        var max = 0f;
        for (var i = 0; i < _n * _n && i < _matrix.Length; i++) {
            var d = _matrix[i];
            if (!float.IsPositiveInfinity(d) && d > max) { max = d; }
        }
        _maxFinite = Math.Max(max, 1e-6f);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    public static uint Heat(double t) {
        // Bright yellow for near, through orange and red, to deep purple for far.
        t = Math.Clamp(t, 0, 1);
        var hue = 0.15 - 0.30 * t;
        if (hue < 0) { hue += 1; }
        return Palette.Hsv(hue, 0.55 + 0.4 * t, 1.0 - 0.62 * t);
    }

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        var n = _n;
        if (n == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        var top = (int)(30 * scaling);
        // Too small to draw anything, for example while the pane is collapsed.
        if (width < 16 || height - top < 16) { return; }
        var side = Math.Max(1, Math.Min(width - 8, height - top - 8));
        var cell = side / n;
        var whole = cell >= 2;
        var drawn = whole ? cell * n : side;
        var ox = (width - drawn) / 2;
        var oy = top + (height - top - drawn) / 2;
        _origin = ox;
        _side = drawn;
        _surface.Resize(width, height);
        _surface.Clear(Palette.Background);

        var pixels = _surface.Pixels;
        var order = _order;
        var matrix = _matrix;
        var scale = 1.0 / _maxFinite;
        if (whole) {
            for (var r = 0; r < n; r++) {
                var from = order[r] * n;
                for (var c = 0; c < n; c++) {
                    var d = matrix[from + order[c]];
                    if (float.IsPositiveInfinity(d)) { continue; }
                    _surface.FillRect(ox + c * cell, oy + r * cell, cell, cell, Heat(d * scale));
                }
            }
        } else {
            for (var py = 0; py < drawn; py++) {
                var from = order[(int)((long)py * n / drawn)] * n;
                var row = (oy + py) * width + ox;
                for (var px = 0; px < drawn; px++) {
                    var d = matrix[from + order[(int)((long)px * n / drawn)]];
                    if (!float.IsPositiveInfinity(d)) { pixels[row + px] = Heat(d * scale); }
                }
            }
        }

        // Cells changed by the last update.
        if (Flash.Count > 0) {
            var positionOf = PositionOf();
            foreach (var cellIndex in Flash) {
                var r = positionOf[cellIndex / n];
                var c = positionOf[cellIndex % n];
                var (x, y, s) = CellRect(r, c, ox, oy, cell, drawn, n);
                _surface.FillRect(x, y, Math.Max(1, s), Math.Max(1, s), 0xFFFFFFFF);
            }
        }

        _surface.Upload();
        if (_surface.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        var unit = drawn / (double)n / scaling;
        var left = ox / scaling;
        var topDip = oy / scaling;
        var sizeDip = drawn / scaling;

        // Diagonal blocks, only when they are big enough to see.
        if (unit * 2 >= 1.5) {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1);
            foreach (var (start, size) in Blocks) {
                if (size < 2) { continue; }
                context.DrawRectangle(null, pen, new Rect(left + start * unit, topDip + start * unit, size * unit, size * unit));
            }
        }

        // Pivot row and column during the sweep.
        if (Pivot >= 0 && Pivot < n) {
            var p = PositionOf()[Pivot];
            var brush = new SolidColorBrush(Color.FromArgb(0x90, 0x4F, 0xC3, 0xF7));
            context.FillRectangle(brush, new Rect(left, topDip + p * unit, sizeDip, Math.Max(1, unit)));
            context.FillRectangle(brush, new Rect(left + p * unit, topDip, Math.Max(1, unit), sizeDip));
        }

        if (LinkedVertex >= 0 && LinkedVertex < n) {
            var p = PositionOf()[LinkedVertex];
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1);
            context.DrawRectangle(null, pen, new Rect(left, topDip + p * unit, sizeDip, Math.Max(1, unit)));
            context.DrawRectangle(null, pen, new Rect(left + p * unit, topDip, Math.Max(1, unit), sizeDip));
        }

        if (_hover.from >= 0) {
            var positionOf = PositionOf();
            var r = positionOf[_hover.from];
            var c = positionOf[_hover.to];
            var pad = Math.Max(2, unit * 0.5);
            context.DrawRectangle(null, new Pen(Brushes.Gold, 2), new Rect(left + c * unit - pad, topDip + r * unit - pad, unit + 2 * pad, unit + 2 * pad));
        }
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)), 1), new Rect(left - 1, topDip - 1, sizeDip + 2, sizeDip + 2));
    }

    private static (int x, int y, int size) CellRect(int r, int c, int ox, int oy, int cell, int drawn, int n) {
        if (cell >= 2 && cell * n == drawn) { return (ox + c * cell, oy + r * cell, cell); }
        var x0 = (int)((long)c * drawn / n);
        var y0 = (int)((long)r * drawn / n);
        var size = Math.Max(1, (int)((long)(c + 1) * drawn / n) - x0);
        return (ox + x0, oy + y0, size);
    }

    private int[]? _positionOf;
    private int[]? _positionOfFor;

    private int[] PositionOf() {
        if (_positionOf is null || !ReferenceEquals(_positionOfFor, _order) || _positionOf.Length != _n) {
            _positionOf = new int[_n];
            for (var p = 0; p < _n; p++) { _positionOf[_order[p]] = p; }
            _positionOfFor = _order;
        }
        return _positionOf;
    }

    private (int, int) CellAt(Point point) {
        if (_n == 0 || _side <= 0) { return (-1, -1); }
        var scaling = Scaling;
        var top = (int)(30 * scaling);
        var height = (int)(Bounds.Height * scaling);
        var oy = top + (height - top - (int)_side) / 2;
        var x = point.X * scaling - _origin;
        var y = point.Y * scaling - oy;
        if (x < 0 || y < 0 || x >= _side || y >= _side) { return (-1, -1); }
        var c = (int)(x / _side * _n);
        var r = (int)(y / _side * _n);
        return (_order[r], _order[c]);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        if (cell == _hover) { return; }
        _hover = cell;
        HoverChanged?.Invoke(cell.Item1, cell.Item2);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hover.from < 0) { return; }
        _hover = (-1, -1);
        HoverChanged?.Invoke(-1, -1);
        InvalidateVisual();
    }
}
