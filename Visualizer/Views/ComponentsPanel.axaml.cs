using System.Diagnostics;
using Avalonia.Controls;
using Yewnyx.Algorithms.Graphs;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Shows the two passes side by side: ConnectedComponents ignores direction, Tarjan follows it.
/// Tarjan rooted at the component vertices writes each component as one contiguous run, aligned
/// with the component segments. Picking roots shows what each root reaches, the order it is
/// written in, and what is skipped.
/// </summary>
public partial class ComponentsPanel : UserControl {
    private const uint Skipped = 0xFF262A32;
    private const uint SingleDim = 0xFF4A505C;

    private GeneratedClusters? _generated;
    private PassesModel? _model;
    private PassesModel.RootQuery? _query;
    private readonly List<int> _roots = new();
    private CancellationTokenSource? _generation;
    private int _hover = -1;
    private int _shuffleSeed = 1;
    private string _buildText = "";

    public ComponentsPanel() {
        InitializeComponent();

        CcView.Layout = SccView.Layout = LayoutKind.Custom;
        CcView.ShowDirection = false;
        CcView.HighlightCycleEdges = false;
        SccView.ShowDirection = true;
        SccView.HighlightCycleEdges = true;
        CcView.ColorOverride = CcColor;
        SccView.ColorOverride = SccColor;

        GenerateButton.Click += async (_, _) => await GenerateAsync();
        ClearRootsButton.Click += (_, _) => { _roots.Clear(); RunQuery(); };
        RandomRootsButton.Click += (_, _) => {
            if (_model is null || _model.N == 0) { return; }
            var rng = new Random(Environment.TickCount);
            _roots.Clear();
            for (var i = 0; i < 5; i++) { _roots.Add(rng.Next(_model.N)); }
            RunQuery();
        };
        ShuffleButton.Click += (_, _) => RunDeterminismCheck();

        CcView.VertexClicked += ToggleRoot;
        SccView.VertexClicked += ToggleRoot;
        Strips.VertexClicked += ToggleRoot;
        CcView.HoverChanged += OnHover;
        SccView.HoverChanged += OnHover;
        Strips.HoverChanged += OnHover;

        AttachedToVisualTree += async (_, _) => {
            if (_model is null) { await GenerateAsync(); }
        };
    }

    private uint CcColor(int v) {
        var m = _model!;
        var c = m.CcOf[v];
        return m.CcSegments[c].Size > 1 ? Palette.Island(c, true) : SingleDim;
    }

    private uint SccColor(int v) {
        var m = _model!;
        if (_query is { } q && q.Roots.Length > 0) {
            var r = q.RootOf[v];
            return r < 0 ? Skipped : Palette.Worker(r);
        }
        var s = m.RootedSccOf[v];
        return m.RootedSegments[s].Size > 1 ? Palette.Island(s, true) : SingleDim;
    }

    private async Task GenerateAsync() {
        _generation?.Cancel();
        var cts = _generation = new CancellationTokenSource();
        var spec = new ClusterSpec(
            (int)(Clusters.Value ?? 1),
            (double)(LonePercent.Value ?? 0) / 100.0,
            (double)(IslandsPerCluster.Value ?? 1),
            (double)(IslandSize.Value ?? 1),
            (double)(ExtraLinks.Value ?? 0),
            (int)(Seed.Value ?? 0));
        GenerateButton.IsEnabled = false;
        HeadlineText.Text = $"Generating {spec.Clusters:N0} clusters…";
        try {
            var sw = Stopwatch.StartNew();
            var (generated, model) = await Task.Run(() => {
                var g = ClusterGenerator.Generate(spec, cts.Token);
                return (g, g.VertexCount == 0 ? null : PassesModel.Build(g, cts.Token));
            }, cts.Token);
            if (cts.IsCancellationRequested) { return; }
            _generated = generated;
            _model = model;
            _roots.Clear();
            _query = null;
            if (model is null) {
                HeadlineText.Text = "The graph is empty.";
                return;
            }

            string Badge(bool? ok) => ok == true ? "matches the generated graph" : "DIFFERS FROM THE GENERATED GRAPH";
            _buildText =
                $"{model.N:N0} vertices, {model.Scc.E:N0} edges.  ConnectedComponents: {model.CcCount:N0} components in {model.CcTime.TotalMilliseconds:N1} ms, {Badge(model.CcMatchesTruth)}.  " +
                $"Tarjan rooted at those vertices: {model.RootedCount:N0} strongly connected components in {model.RootedTime.TotalMilliseconds:N1} ms, {Badge(model.Scc.MatchesTruth)}, " +
                (model.RootedAlignsWithCc ? "and its output splits exactly on the component segments." : "but its output does NOT split on the component segments.") +
                $"  Graph.ContractVersion {Graph.ContractVersion}.  Built in {sw.ElapsedMilliseconds:N0} ms.";
            HeadlineText.Text = _buildText;
            CheckText.Text = "";

            CcView.CustomX = SccView.CustomX = model.X;
            CcView.CustomY = SccView.CustomY = model.Y;
            CcView.SetModel(model.Scc, null);
            SccView.SetModel(model.Scc, null);
            RunQuery();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            HeadlineText.Text = "Failed: " + ex.Message;
        } finally {
            if (_generation == cts) { GenerateButton.IsEnabled = true; }
        }
    }

    private void ToggleRoot(int v) {
        if (_model is null || v < 0) { return; }
        if (!_roots.Remove(v)) { _roots.Add(v); }
        RunQuery();
    }

    private void RunQuery() {
        var m = _model;
        if (m is null) { return; }
        _query = _roots.Count > 0 ? m.QueryRoots(_roots) : null;

        var rows = new List<StripRow> {
            new() {
                Order = m.CcOrder,
                Length = m.N,
                SegmentStarts = m.CcSegments.Take(m.CcCount).Select(s => s.Offset).ToArray(),
                Color = (v, _) => CcColor(v),
            },
            new() {
                Order = m.RootedOrder,
                Length = m.N,
                SegmentStarts = m.RootedSegments.Take(m.RootedCount).Select(s => s.Offset).ToArray(),
                Color = (v, _) => {
                    var s = m.RootedSccOf[v];
                    return m.RootedSegments[s].Size > 1 ? Palette.Island(s, true) : SingleDim;
                },
            },
        };

        var labels = new List<(int, string, uint)>();
        if (_query is { } q) {
            rows.Add(new StripRow {
                Order = q.Order,
                Length = q.Written,
                SegmentStarts = q.Segments.Take(q.ComponentCount).Select(s => s.Offset).ToArray(),
                Color = (v, _) => Palette.Worker(q.RootOf[v]),
            });
            for (var i = 0; i < q.Roots.Length; i++) { labels.Add((q.Roots[i], $"R{i + 1}", Palette.Worker(i))); }

            var parts = new List<string>();
            for (var i = 0; i < q.Roots.Length; i++) {
                parts.Add(q.BandSize[i] > 0
                    ? $"R{i + 1} reached {q.BandSize[i]:N0}"
                    : $"R{i + 1} was already reached by R{q.RootOf[q.Roots[i]] + 1}, so it writes nothing");
            }
            RootsText.Text =
                $"Tarjan rooted at {q.Roots.Length} vertices wrote {q.Written:N0} of {m.N:N0} vertices in {q.ComponentCount:N0} components and skipped {m.N - q.Written:N0} that no root reaches ({q.Time.TotalMilliseconds:N2} ms). " +
                string.Join("; ", parts) + ". " +
                (q.BandsMatchOutput
                    ? "Each root's first-reached vertices are one contiguous band, in root order, as the contract says."
                    : "The bands are NOT contiguous in root order.");
            SccCaption.Text = "Tarjan from your roots: colour = the root that reached it first, dark = skipped. Arrows follow edge direction.";
            StripCaption.Text = "Rows per line: ConnectedComponents, Tarjan rooted at those vertices, then Tarjan from your roots. White ticks start segments.";
        } else {
            RootsText.Text = "No roots picked. Click vertices to see what Tarjan writes when it starts from them.";
            SccCaption.Text = "Tarjan: directed, left to right along edges; colour per strongly connected component, orange edges lie on a cycle";
            StripCaption.Text = "Rows per line: ConnectedComponents, then Tarjan rooted at those vertices. White ticks start segments.";
        }
        SccView.Labels = CcView.Labels = labels;
        Strips.SetRows(rows, m.N);
        CcView.InvalidateVisual();
        SccView.InvalidateVisual();
        UpdateHoverText();
    }

    private void RunDeterminismCheck() {
        if (_model is null || _generated is null) { return; }
        var (cc, rooted, query) = _model.CheckDeterminism(_generated, _roots, _shuffleSeed++);
        var same = cc == 0 && rooted == 0 && query == 0;
        CheckText.Text =
            $"Rebuilt from the same {_generated.From.Length:N0} edges in shuffled order with about 30% added twice. Positions that differ: " +
            $"ConnectedComponents {cc:N0}, rooted Tarjan {rooted:N0}" + (_roots.Count > 0 ? $", your roots {query:N0}" : "") +
            (same ? ". Identical, as the contract promises." : ". THE OUTPUT CHANGED.");
    }

    private void OnHover(int v) {
        if (v == _hover) { return; }
        _hover = v;
        CcView.LinkedHighlight = SccView.LinkedHighlight = Strips.LinkedHighlight = v;
        CcView.InvalidateVisual();
        SccView.InvalidateVisual();
        Strips.InvalidateVisual();
        UpdateHoverText();
    }

    private void UpdateHoverText() {
        var m = _model;
        if (m is null || _hover < 0 || _hover >= m.N) {
            HoverText.Text = "";
            return;
        }
        var v = _hover;
        var cc = m.CcOf[v];
        var s = m.RootedSccOf[v];
        var text =
            $"vertex {v:N0}  out {m.Scc.OutNeighbours(v).Length} in {m.Scc.InNeighbours(v).Length}  |  component {cc:N0} of size {m.CcSegments[cc].Size:N0}  " +
            $"|  strongly connected component {s:N0} of size {m.RootedSegments[s].Size:N0}, written at {Array.IndexOf(m.RootedOrder, v):N0}";
        if (_query is { } q) {
            text += q.RootOf[v] < 0 ? "  |  skipped by your roots" : $"  |  reached first by R{q.RootOf[v] + 1}, written at {q.Position[v]:N0}";
        }
        HoverText.Text = text;
    }
}
