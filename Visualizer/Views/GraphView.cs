using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

public enum LayoutKind {
    /// <summary>Vertices on a grid in input-id order. Edges go every which way.</summary>
    IndexGrid,
    /// <summary>Columns are parallel waves; within a wave each island's vertices are contiguous.</summary>
    WaveLayered,
    /// <summary>Positions come from <see cref="GraphView.CustomX"/> and <see cref="GraphView.CustomY"/>, in 0..1.</summary>
    Custom,
}

/// <summary>
/// Draws a whole graph: edges into a cached bitmap, vertices as pixels, highlights as vectors.
/// Pan with the left button, zoom with the wheel, double-click to reset.
/// </summary>
public sealed class GraphView : Control {
    private const int EdgeBudget = 250_000;
    private const int BucketSize = 12;
    private const int MaxHighlightEdges = 4000;

    private readonly PixelSurface _nodes = new();
    private readonly DispatcherTimer _settle;
    private GraphModel? _model;
    private ScanState? _state;

    // Base layout in device pixels at zoom 1, for the current surface size.
    private float[] _baseX = [];
    private float[] _baseY = [];
    private int _layoutWidth, _layoutHeight;
    private bool _layoutDirty = true;

    private double _zoom = 1;
    private Vector _pan;
    private bool _interacting;

    private RenderTargetBitmap? _edges;
    private double _edgesZoom = 1;
    private Vector _edgesPan;
    private bool _edgesDirty = true;

    // Spatial hash of screen positions, rebuilt with the edge cache.
    private int[] _bucketStart = [];
    private int[] _bucketItems = [];
    private int _bucketCols, _bucketRows;
    private bool _bucketsDirty = true;

    private int _hover = -1;
    private Point _pressPoint;
    private Vector _pressPan;
    private bool _dragging;
    private bool _leftPressed;

    public event Action<int>? HoverChanged;

    /// <summary>Raised on a left click that did not drag, with the nearest vertex.</summary>
    public event Action<int>? VertexClicked;

    public LayoutKind Layout { get; set; } = LayoutKind.IndexGrid;
    public ColorMode ColorMode { get; set; } = ColorMode.Island;
    public bool DrawEdges { get; set; } = true;
    public int LinkedHighlight { get; set; } = -1;

    /// <summary>Vertex colour, replacing the palette rules when set.</summary>
    public Func<int, uint>? ColorOverride { get; set; }

    /// <summary>Layout positions in 0..1 for <see cref="LayoutKind.Custom"/>.</summary>
    public float[]? CustomX { get; set; }
    public float[]? CustomY { get; set; }

    /// <summary>
    /// Draw edge direction: arrowheads when few edges are drawn, otherwise each edge dim at its source
    /// half and bright at its target half, which still reads at hundreds of thousands of edges.
    /// </summary>
    public bool ShowDirection { get; set; }

    /// <summary>Draw edges inside a strongly connected island in orange. Defaults to the wave layout only.</summary>
    public bool? HighlightCycleEdges { get; set; }

    /// <summary>Text drawn next to vertices, such as root numbers.</summary>
    public IReadOnlyList<(int vertex, string text, uint color)>? Labels { get; set; }

    /// <summary>Share of edges actually drawn, for the status bar.</summary>
    public double EdgeCoverage { get; private set; } = 1;

    public GraphView() {
        ClipToBounds = true;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => {
            _settle.Stop();
            _interacting = false;
            InvalidateVisual();
        };
    }

    public void SetModel(GraphModel? model, ScanState? state) {
        _model = model;
        _state = state;
        _hover = -1;
        _zoom = 1;
        _pan = default;
        _layoutDirty = _edgesDirty = _bucketsDirty = true;
        InvalidateVisual();
    }

    public void SetState(ScanState? state) {
        _state = state;
        InvalidateVisual();
    }

    public void InvalidateLayout() {
        _layoutDirty = _edgesDirty = _bucketsDirty = true;
        InvalidateVisual();
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private void EnsureLayout(int width, int height) {
        if (_model is null) { return; }
        if (!_layoutDirty && width == _layoutWidth && height == _layoutHeight) { return; }
        _layoutWidth = width;
        _layoutHeight = height;
        _layoutDirty = false;
        _edgesDirty = _bucketsDirty = true;

        var n = _model.N;
        if (_baseX.Length < n) { _baseX = new float[n]; _baseY = new float[n]; }
        var margin = 10f;
        var w = Math.Max(1f, width - 2 * margin);
        var h = Math.Max(1f, height - 2 * margin);

        if (Layout == LayoutKind.Custom && CustomX is { } cx && CustomY is { } cy) {
            // The caller packs for the pane's rough shape, so stretch to fill it.
            for (var v = 0; v < n; v++) {
                _baseX[v] = margin + cx[v] * w;
                _baseY[v] = margin + cy[v] * h;
            }
        } else if (Layout == LayoutKind.IndexGrid) {
            var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * (double)w / h)));
            var rows = Math.Max(1, (n + cols - 1) / cols);
            var cell = Math.Min(w / cols, h / rows);
            var ox = margin + (w - cols * cell) / 2;
            var oy = margin + (h - rows * cell) / 2;
            for (var v = 0; v < n; v++) {
                _baseX[v] = ox + (v % cols + 0.5f) * cell;
                _baseY[v] = oy + (v / cols + 0.5f) * cell;
            }
        } else {
            var waves = Math.Max(1, _model.WaveCount);
            var islandOf = _model.IslandOf;
            var waveOf = _model.WaveOf;
            var waveSize = _model.WaveSize;
            var slot = _model.WaveSlot;

            // Deep graphs have more waves than columns fit, so wrap waves into stacked bands. Pick the band
            // count that makes column spacing (w * bands / waves) match row spacing (h / (bands * maxWidth)),
            // and never let a column get narrower than a few pixels.
            const float minColumn = 5f;
            var maxWidth = 1;
            for (var i = 0; i < waves; i++) { maxWidth = Math.Max(maxWidth, waveSize[i]); }
            var bands = (int)Math.Round(Math.Sqrt(h * (double)waves / (w * (double)maxWidth)));
            bands = Math.Clamp(Math.Max(bands, (int)Math.Ceiling(waves * minColumn / w)), 1, waves);
            var perBand = (int)Math.Ceiling(waves / (double)bands);
            var bandHeight = h / bands;
            var gap = bands > 1 ? Math.Min(8f, bandHeight * 0.15f) : 0f;
            var columnWidth = w / perBand;
            var inner = columnWidth > 3 * minColumn ? columnWidth * 0.85f : columnWidth;

            // Wide waves fill their column as a small grid, islands contiguous in reading order.
            var subColumns = new int[waves];
            var subRows = new int[waves];
            for (var i = 0; i < waves; i++) {
                var size = Math.Max(1, waveSize[i]);
                var c = (int)Math.Round(Math.Sqrt(size * inner / (double)(bandHeight - gap)));
                subColumns[i] = Math.Clamp(c, 1, size);
                subRows[i] = (size + subColumns[i] - 1) / subColumns[i];
            }

            for (var v = 0; v < n; v++) {
                var wave = waveOf[islandOf[v]];
                var band = wave / perBand;
                var columns = subColumns[wave];
                var s = slot[v];
                _baseX[v] = margin + (wave % perBand) * columnWidth + (columnWidth - inner) / 2 + (s % columns + 0.5f) / columns * inner;
                _baseY[v] = margin + band * bandHeight + (s / columns + 0.5f) / subRows[wave] * (bandHeight - gap);
            }
        }
    }

    private Point ToScreen(int v) => new(_baseX[v] * _zoom + _pan.X, _baseY[v] * _zoom + _pan.Y);

    private void RebuildEdges(int width, int height) {
        _edgesDirty = false;
        _edgesZoom = _zoom;
        _edgesPan = _pan;
        if (_edges is null || _edges.PixelSize.Width != width || _edges.PixelSize.Height != height) {
            _edges?.Dispose();
            _edges = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        }
        using var ctx = _edges.CreateDrawingContext();
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, width, height));
        if (_model is null || !DrawEdges || _model.E == 0) { EdgeCoverage = 1; return; }

        var from = _model.EdgeFrom;
        var to = _model.EdgeTo;
        var islandOf = _model.IslandOf;
        var e = from.Length;
        var stride = Math.Max(1, (int)Math.Ceiling(e / (double)EdgeBudget));
        EdgeCoverage = 1.0 / stride;

        var drawn = e / stride;
        var arrows = ShowDirection && drawn <= 3000;
        var halves = ShowDirection && !arrows;
        var arrowBack = DotSize(width, height) / 2.0 + 1;
        // The before pane must not give away the islands, so by default only the wave layout marks cycles.
        var markCycles = HighlightCycleEdges ?? Layout == LayoutKind.WaveLayered;

        var crossHead = new StreamGeometry();
        var crossTail = new StreamGeometry();
        var innerHead = new StreamGeometry();
        var innerTail = new StreamGeometry();
        var crossArrows = new StreamGeometry();
        var innerArrows = new StreamGeometry();
        var visible = new Rect(-2, -2, width + 4, height + 4);
        using (var ch = crossHead.Open())
        using (var ct = crossTail.Open())
        using (var ih = innerHead.Open())
        using (var it = innerTail.Open())
        using (var ca = crossArrows.Open())
        using (var ia = innerArrows.Open()) {
            for (var i = 0; i < e; i += stride) {
                var a = ToScreen(from[i]);
                var b = ToScreen(to[i]);
                if (!visible.Contains(a) && !visible.Contains(b)) { continue; }
                var inner = markCycles && islandOf[from[i]] == islandOf[to[i]];
                var head = inner ? ih : ch;
                if (halves) {
                    var mid = (a + b) / 2;
                    var tail = inner ? it : ct;
                    tail.BeginFigure(a, false);
                    tail.LineTo(mid);
                    tail.EndFigure(false);
                    head.BeginFigure(mid, false);
                    head.LineTo(b);
                    head.EndFigure(false);
                    continue;
                }
                head.BeginFigure(a, false);
                head.LineTo(b);
                head.EndFigure(false);
                if (!arrows) { continue; }
                var d = b - a;
                var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (length < arrowBack + 2) { continue; }
                var u = d / length;
                var tip = b - u * arrowBack;
                var normal = new Vector(-u.Y, u.X);
                var arrow = inner ? ia : ca;
                arrow.BeginFigure(tip, true);
                arrow.LineTo(tip - u * 7 + normal * 3.2);
                arrow.LineTo(tip - u * 7 - normal * 3.2);
                arrow.EndFigure(true);
            }
        }

        var alpha = (byte)Math.Clamp(2600 / Math.Sqrt(Math.Max(1, drawn)), 10, 120);
        var innerAlpha = (byte)Math.Min(255, alpha * 2 + 20);
        if (halves) {
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(alpha / 3 + 4), 180, 190, 210)), 1), crossTail);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(innerAlpha / 3 + 4), 255, 140, 70)), 1), innerTail);
            alpha = (byte)Math.Min(255, alpha * 2);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, 180, 190, 210)), 1), crossHead);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(innerAlpha, 255, 140, 70)), 1), innerHead);
        if (arrows) {
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha * 2 + 40), 200, 210, 225)), null, crossArrows);
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(innerAlpha, 255, 140, 70)), null, innerArrows);
        }
    }

    private int DotSize(int width, int height) =>
        Math.Clamp((int)Math.Round(Math.Sqrt((double)width * height / Math.Max(1, _model?.N ?? 1)) * 0.55 * Math.Sqrt(_zoom)), 2, 9);

    private void RebuildBuckets(int width, int height) {
        _bucketsDirty = false;
        if (_model is null) { return; }
        var n = _model.N;
        _bucketCols = Math.Max(1, (width + BucketSize - 1) / BucketSize);
        _bucketRows = Math.Max(1, (height + BucketSize - 1) / BucketSize);
        var buckets = _bucketCols * _bucketRows;
        if (_bucketStart.Length < buckets + 1) { _bucketStart = new int[buckets + 1]; } else { Array.Clear(_bucketStart, 0, buckets + 1); }
        if (_bucketItems.Length < n) { _bucketItems = new int[n]; }

        for (var v = 0; v < n; v++) {
            var b = BucketOf(v, out _);
            if (b >= 0) { _bucketStart[b + 1]++; }
        }
        for (var b = 0; b < buckets; b++) { _bucketStart[b + 1] += _bucketStart[b]; }
        var fill = new int[buckets];
        for (var v = 0; v < n; v++) {
            var b = BucketOf(v, out _);
            if (b >= 0) { _bucketItems[_bucketStart[b] + fill[b]++] = v; }
        }
    }

    private int BucketOf(int v, out Point p) {
        p = ToScreen(v);
        var bx = (int)(p.X / BucketSize);
        var by = (int)(p.Y / BucketSize);
        if (bx < 0 || by < 0 || bx >= _bucketCols || by >= _bucketRows) { return -1; }
        return by * _bucketCols + bx;
    }

    private int Nearest(Point devicePoint, double radius) {
        if (_model is null || _bucketsDirty) { return -1; }
        var best = -1;
        var bestDistance = radius * radius;
        var bx0 = Math.Max(0, (int)((devicePoint.X - radius) / BucketSize));
        var bx1 = Math.Min(_bucketCols - 1, (int)((devicePoint.X + radius) / BucketSize));
        var by0 = Math.Max(0, (int)((devicePoint.Y - radius) / BucketSize));
        var by1 = Math.Min(_bucketRows - 1, (int)((devicePoint.Y + radius) / BucketSize));
        for (var by = by0; by <= by1; by++) {
            for (var bx = bx0; bx <= bx1; bx++) {
                var b = by * _bucketCols + bx;
                for (var i = _bucketStart[b]; i < _bucketStart[b + 1]; i++) {
                    var v = _bucketItems[i];
                    var p = ToScreen(v);
                    var dx = p.X - devicePoint.X;
                    var dy = p.Y - devicePoint.Y;
                    var d = dx * dx + dy * dy;
                    if (d < bestDistance) { bestDistance = d; best = v; }
                }
            }
        }
        return best;
    }

    public override void Render(DrawingContext context) {
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Palette.ToColor(Palette.Background)), new Rect(bounds.Size));
        if (_model is null || _model.N == 0) { return; }

        var scaling = Scaling;
        var width = Math.Max(1, (int)(bounds.Width * scaling));
        var height = Math.Max(1, (int)(bounds.Height * scaling));
        EnsureLayout(width, height);
        _nodes.Resize(width, height);

        // Edges: rebuild when settled, otherwise reproject the stale cache so dragging stays smooth.
        if ((_edgesDirty && !_interacting) || _edges is null) { RebuildEdges(width, height); }
        if (_edges is not null && DrawEdges) {
            var ratio = _zoom / _edgesZoom;
            var origin = new Point((-_edgesPan.X * ratio + _pan.X) / scaling, (-_edgesPan.Y * ratio + _pan.Y) / scaling);
            var size = new Size(width * ratio / scaling, height * ratio / scaling);
            context.DrawImage(_edges, new Rect(0, 0, width, height), new Rect(origin, size));
        }
        if (_bucketsDirty && !_interacting) { RebuildBuckets(width, height); }

        // Vertices.
        var model = _model;
        var state = _state;
        var mode = ColorMode;
        var n = model.N;
        var dot = DotSize(width, height);
        var half = dot / 2;
        var colorOf = ColorOverride ?? (v => Palette.Vertex(model, state, mode, v));
        _nodes.Clear(0);
        var pixels = _nodes.Pixels;
        for (var v = 0; v < n; v++) {
            var x = (int)(_baseX[v] * _zoom + _pan.X);
            var y = (int)(_baseY[v] * _zoom + _pan.Y);
            if (dot == 1) {
                if ((uint)x < (uint)width && (uint)y < (uint)height) { pixels[y * width + x] = colorOf(v); }
            } else {
                if (x + half < 0 || y + half < 0 || x - half >= width || y - half >= height) { continue; }
                _nodes.FillRect(x - half, y - half, dot, dot, colorOf(v));
            }
        }
        _nodes.Upload();
        if (_nodes.Bitmap is { } bitmap) {
            context.DrawImage(bitmap, new Rect(0, 0, width, height), new Rect(bounds.Size));
        }

        // Worker cursors.
        if (state is not null) {
            for (var w = 0; w < state.WorkerCount; w++) {
                var v = Volatile.Read(ref state.WorkerCurrent[w].Value);
                if (v < 0) { continue; }
                var p = ToScreen(v) / scaling;
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.ToColor(Palette.Worker(w))), 2), p, 6, 6);
            }
        }

        if (Labels is { } labels) {
            foreach (var (v, text, color) in labels) {
                if (v < 0 || v >= n) { continue; }
                var p = ToScreen(v) / scaling;
                if (p.X < -20 || p.Y < -20 || p.X > bounds.Width + 20 || p.Y > bounds.Height + 20) { continue; }
                var brush = new SolidColorBrush(Palette.ToColor(color));
                var r = Math.Max(5, dot / scaling + 3);
                context.DrawEllipse(null, new Pen(brush, 2), p, r, r);
                var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
                var at = new Point(p.X + 7, p.Y - formatted.Height - 2);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x13, 0x18)), null, new Rect(at.X - 2, at.Y, formatted.Width + 4, formatted.Height));
                context.DrawText(formatted, at);
            }
        }

        DrawHighlight(context, LinkedHighlight, Colors.White, scaling, dot);
        DrawHighlight(context, _hover, Colors.Gold, scaling, dot);
    }

    private void DrawHighlight(DrawingContext context, int v, Color ring, double scaling, int dot) {
        if (_model is null || v < 0 || v >= _model.N) { return; }
        var p = ToScreen(v) / scaling;
        var outgoing = new StreamGeometry();
        var incoming = new StreamGeometry();
        var budget = MaxHighlightEdges;
        using (var o = outgoing.Open()) {
            foreach (var t in _model.OutNeighbours(v)) {
                if (budget-- <= 0) { break; }
                o.BeginFigure(p, false);
                o.LineTo(ToScreen(t) / scaling);
                o.EndFigure(false);
            }
        }
        using (var i = incoming.Open()) {
            foreach (var s in _model.InNeighbours(v)) {
                if (budget-- <= 0) { break; }
                i.BeginFigure(ToScreen(s) / scaling, false);
                i.LineTo(p);
                i.EndFigure(false);
            }
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 167, 38)), 1.5), outgoing);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(220, 79, 195, 247)), 1.5), incoming);
        var r = Math.Max(5, dot / scaling + 3);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(ring), 2), p, r, r);
    }

    private void BeginInteraction() {
        _interacting = true;
        _edgesDirty = _bucketsDirty = true;
        _settle.Stop();
        _settle.Start();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        if (_model is null) { return; }
        var scaling = Scaling;
        var p = e.GetPosition(this) * scaling;
        var factor = Math.Pow(1.25, e.Delta.Y);
        var newZoom = Math.Clamp(_zoom * factor, 1, 400);
        factor = newZoom / _zoom;
        _pan = new Vector(p.X - (p.X - _pan.X) * factor, p.Y - (p.Y - _pan.Y) * factor);
        _zoom = newZoom;
        if (_zoom == 1) { _pan = default; }
        BeginInteraction();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (e.ClickCount == 2) {
            _zoom = 1;
            _pan = default;
            BeginInteraction();
            return;
        }
        if (point.Properties.IsLeftButtonPressed) {
            _pressPoint = point.Position;
            _pressPan = _pan;
            _dragging = false;
            _leftPressed = true;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed && e.Pointer.Captured == this) {
            var delta = point.Position - _pressPoint;
            if (_dragging || Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) {
                _dragging = true;
                _pan = _pressPan + delta * Scaling;
                BeginInteraction();
                return;
            }
        }
        var v = Nearest(point.Position * Scaling, 10 * Scaling);
        SetHover(v);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_leftPressed && !_dragging && VertexClicked is { } clicked) {
            var v = Nearest(e.GetPosition(this) * Scaling, 10 * Scaling);
            if (v >= 0) { clicked(v); }
        }
        _leftPressed = false;
        _dragging = false;
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int v) {
        if (v == _hover) { return; }
        _hover = v;
        HoverChanged?.Invoke(v);
        InvalidateVisual();
    }
}
