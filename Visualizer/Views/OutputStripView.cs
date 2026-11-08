using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>One output array: the vertex at each position, where segments start, and how to colour a cell.</summary>
public sealed class StripRow {
    public int[] Order = [];
    public int Length;
    /// <summary>Positions where a new segment starts, ascending. Position 0 is implied.</summary>
    public int[] SegmentStarts = [];
    public Func<int, int, uint> Color = (_, _) => 0xFF808080; // (vertex, position) -> colour
    internal int[] PositionOf = [];
}

/// <summary>
/// Output arrays drawn as wrapped strips, one cell per position. The rows are interleaved line by line,
/// so position p of every row sits in the same column: outputs that should line up visibly do.
/// A bright tick marks each segment start.
/// </summary>
public sealed class OutputStripView : Control {
    private readonly PixelSurface _surface = new();
    private IReadOnlyList<StripRow> _rows = [];
    private int _cell = 1;
    private int _cols = 1;
    private int _lineHeight = 1;
    private int _rowGap;
    private int _hover = -1;

    public event Action<int>? HoverChanged;
    public event Action<int>? VertexClicked;

    public int LinkedHighlight { get; set; } = -1;

    /// <summary>Space left free at the top, in DIPs, for a caption.</summary>
    public double TopInset { get; set; } = 32;

    private int _inset;

    public OutputStripView() {
        ClipToBounds = true;
    }

    public void SetRows(IReadOnlyList<StripRow> rows, int vertexCount) {
        _rows = rows;
        foreach (var row in rows) {
            row.PositionOf = new int[vertexCount];
            Array.Fill(row.PositionOf, -1);
            for (var p = 0; p < row.Length; p++) { row.PositionOf[row.Order[p]] = p; }
        }
        InvalidateVisual();
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private int Longest {
        get {
            var n = 1;
            foreach (var row in _rows) { n = Math.Max(n, row.Length); }
            return n;
        }
    }

    private void ComputeGrid(int width, int height) {
        var rows = Math.Max(1, _rows.Count);
        var n = Longest;
        var cell = 24;
        while (cell > 1) {
            var gap = cell >= 4 ? Math.Max(2, cell / 2) : 1;
            var cols = Math.Max(1, width / cell);
            var lines = (n + cols - 1) / cols;
            if (lines * (rows * cell + gap) <= height) { break; }
            cell--;
        }
        _cell = cell;
        _cols = Math.Max(1, width / cell);
        _rowGap = cell >= 4 ? Math.Max(2, cell / 2) : 1;
        _lineHeight = rows * cell + _rowGap;
    }

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        if (_rows.Count == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        _surface.Resize(width, height);
        _surface.Clear(Palette.Background);
        _inset = (int)(TopInset * scaling);
        ComputeGrid(width, Math.Max(1, height - _inset));

        var size = _cell >= 5 ? _cell - 1 : _cell;
        for (var r = 0; r < _rows.Count; r++) {
            var row = _rows[r];
            for (var p = 0; p < row.Length; p++) {
                var x = p % _cols * _cell;
                var y = _inset + p / _cols * _lineHeight + r * _cell;
                if (y >= height) { break; }
                var v = row.Order[p];
                var color = v == _hover || v == LinkedHighlight ? 0xFFFFD54Fu : row.Color(v, p);
                _surface.FillRect(x, y, size, _cell >= 3 ? _cell - 1 : _cell, color);
            }
            // Segment starts: a white tick on the left edge of the cell.
            foreach (var start in row.SegmentStarts) {
                if (start <= 0 || start >= row.Length) { continue; }
                var x = start % _cols * _cell;
                var y = _inset + start / _cols * _lineHeight + r * _cell;
                if (y >= height) { break; }
                _surface.FillRect(x - 1, y, _cell >= 6 ? 2 : 1, _cell, 0xFFFFFFFF);
            }
        }

        _surface.Upload();
        if (_surface.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        // Ring the hovered vertex in every row so the eye can follow it between outputs.
        var highlight = _hover >= 0 ? _hover : LinkedHighlight;
        if (highlight < 0) { return; }
        var pen = new Pen(Brushes.Gold, 2);
        var c = _cell / scaling;
        var pad = Math.Max(2, c * 0.4);
        for (var r = 0; r < _rows.Count; r++) {
            var positions = _rows[r].PositionOf;
            if (highlight >= positions.Length) { continue; }
            var p = positions[highlight];
            if (p < 0) { continue; }
            var x = p % _cols * c;
            var y = (_inset + p / _cols * _lineHeight + r * _cell) / scaling;
            context.DrawRectangle(null, pen, new Rect(x - pad, y - pad, c + 2 * pad, c + 2 * pad));
        }
    }

    private int VertexAt(Point point) {
        if (_rows.Count == 0) { return -1; }
        var scaling = Scaling;
        var x = (int)(point.X * scaling) / _cell;
        var py = (int)(point.Y * scaling) - _inset;
        if (py < 0) { return -1; }
        var line = py / _lineHeight;
        var r = py % _lineHeight / _cell;
        if (x < 0 || x >= _cols || r >= _rows.Count) { return -1; }
        var p = line * _cols + x;
        var row = _rows[r];
        return p < row.Length ? row.Order[p] : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var v = VertexAt(e.GetPosition(this));
        if (v == _hover) { return; }
        _hover = v;
        HoverChanged?.Invoke(v);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        if (_hover == -1) { return; }
        _hover = -1;
        HoverChanged?.Invoke(-1);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) { return; }
        var v = VertexAt(e.GetPosition(this));
        if (v >= 0) { VertexClicked?.Invoke(v); }
    }
}
