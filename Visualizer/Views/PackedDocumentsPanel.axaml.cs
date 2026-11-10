using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>One block of the document pane: the envelope, an object, or the types table.</summary>
public sealed class DocBlock : INotifyPropertyChanged {
    private static readonly IBrush Plain = Brushes.Transparent;
    private static readonly IBrush Lit = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private readonly Func<string> _body;
    private string? _bodyText;
    private bool _marked;

    public DocBlock(int vertex, string title, IBrush titleBrush, Func<string> body) {
        Vertex = vertex;
        Title = title;
        TitleBrush = titleBrush;
        _body = body;
    }

    /// <summary>The object this block shows, or -1.</summary>
    public int Vertex { get; }
    public string Title { get; }
    public IBrush TitleBrush { get; }

    // Written out only when the list first shows the block, so a large document costs little to open.
    public string Body => _bodyText ??= _body();
    public IBrush Background => _marked ? Lit : Plain;

    public bool Marked {
        get => _marked;
        set {
            if (_marked == value) { return; }
            _marked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Shows a packed document as a graph of objects beside its text: one the library packed from a
/// generated object graph, or one opened from a file or an https address. A file or a download is
/// only ever read as data; this panel never hands it to an Unpacker, because unpacking creates the
/// types a document names.
/// </summary>
public partial class PackedDocumentsPanel : UserControl {
    private const uint NotReached = 0xFFFF1744;
    private const uint NoType = 0xFF9AA3B2;
    private const uint Neutral = 0xFFE8EDF4;
    private const int LabelAllUpTo = 16;
    private const int MaxFileCharacters = 64 << 20;

    /// <summary>Where a document came from. Only a generated one may ever be unpacked.</summary>
    private enum Origin { Generated, File, Url }

    private sealed record Shown(Origin Origin, string Title, PackedDocument? Document, string? Failure, string? Note = null, RoundTripResult? Trip = null);

    private readonly Dictionary<uint, IBrush> _brushes = new();
    private Shown? _generated;
    private Shown? _file;
    private Shown? _url;
    private PackedDocument? _doc;
    private List<DocBlock>? _blocks;
    private DocBlock? _marked;
    private CancellationTokenSource? _generation;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _fetching;
    private int _graphHover = -1;
    private int _listHover = -1;
    private int _selected = -1;

    public PackedDocumentsPanel() {
        InitializeComponent();

        Graph.Layout = LayoutKind.WaveLayered;
        Graph.ShowDirection = true;
        Graph.HighlightCycleEdges = true;
        Graph.ColorOverride = ColorOf;
        Graph.HoverChanged += OnGraphHover;
        Graph.VertexClicked += Select;

        Source.SelectionChanged += (_, _) => OnSourceChanged();
        GenerateButton.Click += async (_, _) => await GenerateAsync();
        OpenButton.Click += async (_, _) => await OpenFileAsync();
        LoadButton.Click += async (_, _) => await LoadAddressAsync(Address.Text);
        CancelButton.Click += (_, _) => _fetching?.Cancel();
        Address.KeyDown += async (_, e) => {
            if (e.Key == Key.Enter) { await LoadAddressAsync(Address.Text); }
        };

        Blocks.PointerMoved += (_, e) => SetListHover(((e.Source as StyledElement)?.DataContext as DocBlock)?.Vertex ?? -1);
        Blocks.PointerExited += (_, _) => SetListHover(-1);
        Blocks.SelectionChanged += (_, _) => {
            _selected = (Blocks.SelectedItem as DocBlock)?.Vertex ?? -1;
            UpdateLabels();
            UpdateHoverText();
        };

        AttachedToVisualTree += async (_, _) => {
            if (_generated is null) { await GenerateAsync(); }
        };
    }

    private uint ColorOf(int v) {
        var d = _doc!;
        if (d.Root >= 0 && !d.Reached[v]) { return NotReached; }
        return d.TypeOf[v] < 0 ? NoType : Palette.Worker(d.TypeOf[v]);
    }

    private IBrush Brush(uint color) {
        if (!_brushes.TryGetValue(color, out var brush)) { _brushes[color] = brush = new SolidColorBrush(Palette.ToColor(color)); }
        return brush;
    }

    private void OnSourceChanged() {
        var source = Source.SelectedIndex;
        GeneratedFields.IsVisible = source == 0;
        FileFields.IsVisible = source == 1;
        UrlFields.IsVisible = source == 2;
        Show(source switch { 0 => _generated, 1 => _file, _ => _url });
    }

    private async Task GenerateAsync() {
        _generation?.Cancel();
        var cts = _generation = new CancellationTokenSource();
        var spec = new DemoSpec(
            (int)(Shelves.Value ?? 0),
            (double)(BooksPerShelf.Value ?? 0),
            (int)(Authors.Value ?? 1),
            (double)(SharedPercent.Value ?? 0) / 100.0,
            (int)(Seed.Value ?? 0));
        var title = $"Generated library, seed {spec.Seed}, packed by Packer";
        GenerateButton.IsEnabled = false;
        if (Source.SelectedIndex == 0) { HeadlineText.Text = "Generating and packing…"; }
        Shown shown;
        try {
            // The round trip unpacks, so it takes a spec and nothing else: no document can be passed in.
            var trip = await Task.Run(() => PackedDemo.RoundTrip(spec, cts.Token), cts.Token);
            shown = new Shown(Origin.Generated, title, trip.Document, null, Trip: trip);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            shown = new Shown(Origin.Generated, title, null, "failed: " + OneLine(ex.Message));
        } finally {
            if (_generation == cts) { GenerateButton.IsEnabled = true; }
        }
        if (cts.IsCancellationRequested) { return; }
        _generated = shown;
        if (Source.SelectedIndex == 0) { Show(shown); }
    }

    private async Task OpenFileAsync() {
        if (TopLevel.GetTopLevel(this) is not { } top) { return; }
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open a packed document",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
        });
        if (picked.Count == 0) { return; }
        await using var stream = await picked[0].OpenReadAsync();
        await LoadAsync(stream, picked[0].Name);
    }

    /// <summary>Reads a document from a stream, as data only, and shows it under the File source.</summary>
    public async Task LoadAsync(Stream stream, string name) {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        OpenButton.IsEnabled = false;
        Source.SelectedIndex = 1;
        HeadlineText.Text = $"Reading {name}…";
        Shown shown;
        try {
            var text = await ReadTextAsync(stream, MaxFileCharacters, cts.Token);
            if (text is null) {
                shown = new Shown(Origin.File, name, null, $"is longer than {MaxFileCharacters:N0} characters, so it was not read");
            } else {
                var title = $"{name} ({text.Length:N0} characters)";
                shown = await Task.Run(() => ReadText(Origin.File, title, text, null, cts.Token), cts.Token);
            }
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            shown = new Shown(Origin.File, name, null, "could not be read: " + OneLine(ex.Message));
        } finally {
            if (_loading == cts) { OpenButton.IsEnabled = true; }
        }
        if (cts.IsCancellationRequested) { return; }
        _file = shown;
        if (Source.SelectedIndex == 1) { Show(shown); }
    }

    /// <summary>Downloads a document from an https address, reads it as data only, and shows it under the URL source.</summary>
    public async Task LoadAddressAsync(string? text) {
        _fetching?.Cancel();
        _fetching = null;
        SetFetching(false);
        Source.SelectedIndex = 2;
        if (!DocumentFetch.TryAddress(text, out var address, out var refusal)) {
            _url = null;
            Show(null);
            HeadlineText.Text = refusal;
            return;
        }

        var cts = _fetching = new CancellationTokenSource();
        var title = DocumentFetch.Describe(address);
        SetFetching(true);
        Show(null);
        HeadlineText.Text = $"Fetching from {address.Host}…";
        Shown shown;
        try {
            var fetched = await DocumentFetch.GetTextAsync(address, DocumentFetch.MaxBytes, DocumentFetch.Timeout, cts.Token);
            if (fetched.Text is not { } body) {
                shown = new Shown(Origin.Url, title, null, OneLine(fetched.Failure ?? "gave nothing"));
            } else {
                var note = $"Fetched over https in {fetched.Time.TotalMilliseconds:N0} ms, served as {fetched.MediaType ?? "no stated type"}; nothing was saved.";
                shown = await Task.Run(() => ReadText(Origin.Url, $"{title} ({fetched.Bytes:N0} bytes)", body, note, cts.Token), cts.Token);
            }
        } catch (OperationCanceledException) {
            // Cancelled by the button: back to whatever was loaded before. Replaced by a newer load: leave it alone.
            if (_fetching == cts) {
                _fetching = null;
                SetFetching(false);
                if (Source.SelectedIndex == 2) { Show(_url); }
            }
            return;
        } catch (Exception ex) {
            shown = new Shown(Origin.Url, title, null, "could not be read: " + OneLine(ex.Message));
        }
        if (_fetching != cts) { return; }
        _fetching = null;
        SetFetching(false);
        _url = shown;
        if (Source.SelectedIndex == 2) { Show(shown); }
    }

    private void SetFetching(bool fetching) {
        LoadButton.IsEnabled = !fetching;
        CancelButton.IsVisible = fetching;
    }

    // Text from a file or an address becomes a document here and nowhere else: parsed and traced as data.
    private static Shown ReadText(Origin origin, string title, string text, string? note, CancellationToken ct) {
        JObject json;
        try {
            json = PackedDocument.Parse(text);
        } catch (JsonException ex) {
            var why = text.AsSpan().TrimStart().StartsWith("<")
                ? "is not JSON: it starts like an HTML or XML page"
                : "is not a JSON document: " + OneLine(ex.Message);
            return new Shown(origin, title, null, why, note);
        }
        return new Shown(origin, title, PackedDocument.Read(json, ct: ct), null, note);
    }

    // Null when the text is longer than the cap. Reads with await so it also works where blocking is not allowed.
    private static async Task<string?> ReadTextAsync(Stream stream, int maxCharacters, CancellationToken ct) {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1 << 16, leaveOpen: true);
        var text = new StringBuilder();
        var buffer = new char[1 << 16];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0) {
            if (text.Length + read > maxCharacters) { return null; }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private static string OneLine(string message) {
        message = message.Replace('\r', ' ').Replace('\n', ' ').TrimEnd('.', ' ');
        return message.Length <= 200 ? message : message[..200] + "…";
    }

    private void Show(Shown? shown) {
        var d = _doc = shown?.Document;
        _blocks = null;
        _marked = null;
        _graphHover = _listHover = _selected = -1;
        Graph.LinkedHighlight = -1;
        Graph.Labels = null;
        HoverText.Text = TraceText.Text = NoteText.Text = RoundTripText.Text = "";
        RoundTripText.IsVisible = false;
        Blocks.ItemsSource = null;
        GraphCaption.IsVisible = d?.Graph is not null;

        if (shown is null) {
            HeadlineText.Text = Source.SelectedIndex switch {
                0 => "",
                1 => "No file is open. Open… reads a packed document from a file.",
                _ => "No address is loaded. Load fetches a packed document over https.",
            };
            Graph.SetModel(null, null);
            return;
        }
        if (d is null) {
            HeadlineText.Text = $"{shown.Title} {shown.Failure}.";
            NoteText.Text = shown.Note ?? "";
            Graph.SetModel(null, null);
            return;
        }

        var blocks = new List<DocBlock> { new(-1, "document", Brush(Neutral), () => EnvelopeText(d)) };
        if (!d.HasObjects) {
            HeadlineText.Text = $"{shown.Title} is not a packed document: {string.Join(", ", d.Missing)}.";
            TraceText.Text = "It is JSON, shown as found on the right.";
            NoteText.Text = shown.Note ?? "";
            Blocks.ItemsSource = blocks;
            Graph.SetModel(null, null);
            return;
        }

        HeadlineText.Text =
            $"{shown.Title}: {d.N:N0} objects, {d.RefFrom.Length:N0} refs, {d.Types.Count:N0} types.  " +
            $"{d.SharedCount:N0} shared (referenced more than once), {d.LoopCount:N0} loops through {d.LoopObjects:N0} objects.";

        var dangling = d.Dangling.Count == 0
            ? "0 dangling"
            : $"{d.Dangling.Count:N0} DANGLING (shaped like a reference, pointing at no object)";
        TraceText.Text = d.Root < 0
            ? $"Nothing is reached, because {string.Join(", ", d.Missing)}.  {dangling}, {d.NullRefs:N0} null."
            : $"From the root {d.RootId}: {d.ReachedCount:N0} reached, {d.N - d.ReachedCount:N0} not reached, {dangling}, {d.NullRefs:N0} null.  " +
              (d.TarjanAgrees == true
                  ? $"The trace matches rooted Tarjan, which wrote {d.TarjanWritten:N0}."
                  : $"THE TRACE DIFFERS FROM ROOTED TARJAN, WHICH WROTE {d.TarjanWritten:N0}.");

        var notes = new List<string> { $"{d.Envelope}.  Read as data and traced in {d.ReadTime.TotalMilliseconds:N1} ms; no object was created." };
        var unlisted = d.Types.Count(t => !t.Listed);
        if (unlisted > 0) { notes.Add(unlisted == 1 ? "1 type has no entry in the types table and is labelled by id." : $"{unlisted:N0} types have no entry in the types table and are labelled by id."); }
        if (d.Untyped > 0) { notes.Add(d.Untyped == 1 ? "1 object does not say what type it is." : $"{d.Untyped:N0} objects do not say what type they are."); }
        if (d.NotObjects > 0) { notes.Add(d.NotObjects == 1 ? "1 entry of the objects table is not an object." : $"{d.NotObjects:N0} entries of the objects table are not objects."); }
        if (shown.Note is not null) { notes.Add(shown.Note); }
        NoteText.Text = string.Join("  ", notes);
        if (shown.Origin == Origin.Generated && shown.Trip is { } trip) {
            RoundTripText.Text = TripText(trip, d.N);
            RoundTripText.IsVisible = true;
        }

        for (var v = 0; v < d.N; v++) {
            var vertex = v;
            var title = $"{d.ObjectIds[v]}  {d.TypeName(v)}";
            if (v == d.Root) { title += "   root"; } else if (d.Root >= 0 && !d.Reached[v]) { title += "   not reached"; }
            if (d.InLoop[v]) { title += "   in a loop"; }
            blocks.Add(new DocBlock(v, title, Brush(ColorOf(v)), () => d.BodyText(vertex)));
        }
        blocks.Add(new DocBlock(-1, "types", Brush(Neutral), () => TypesText(d)));
        _blocks = blocks;
        Blocks.ItemsSource = blocks;

        Graph.SetModel(d.Graph, null);
        UpdateLabels();
    }

    private static string TripText(RoundTripResult t, int objects) {
        var per = Math.Max(1, objects);
        var sb = new StringBuilder();
        sb.Append($"Packed in {t.PackTime.TotalMilliseconds:N2} ms: {t.PackBytes / per:N0} bytes allocated per object, ");
        sb.Append($"{t.Characters / per:N0} JSON characters per object ({t.Characters:N0} in all).  ");
        if (t.Failure is not null) {
            return sb.Append($"THE ROUND TRIP THROUGH TEXT FAILED: {OneLine(t.Failure)}.").ToString();
        }
        if (t.FirstDifference >= 0) {
            sb.Append("THE ROUND TRIP THROUGH TEXT CHANGED THE DOCUMENT: unpacked and packed again it is ");
            sb.Append($"{t.RepackedCharacters:N0} characters and first differs at character {t.FirstDifference:N0}.");
        } else {
            sb.Append("Round trip through text: written out, read back, unpacked into a new library in ");
            sb.Append($"{t.UnpackTime.TotalMilliseconds:N2} ms and packed again, it gives the same {t.Characters:N0} characters.");
        }
        if (t.Unrated > 0) {
            sb.Append(t.Unrated == 1 ? "  1 rating is NaN" : $"  {t.Unrated:N0} ratings are NaN");
            sb.Append(t.UnratedBack == t.Unrated ? " and came back as NaN" : $" AND {t.UnratedBack:N0} CAME BACK AS NaN");
            sb.Append(t.SameInMemory
                ? "."
                : "; the text holds NaN as a string, so token for token it is not the document that was in memory.");
        } else if (!t.SameInMemory) {
            sb.Append("  Token for token, the text is not the document that was in memory.");
        }
        return sb.ToString();
    }

    private static string EnvelopeText(PackedDocument d) {
        var layout = d.Layout;
        var sb = new StringBuilder();
        foreach (var p in d.Json.Properties()) {
            sb.Append("  ").Append(p.Name).Append("  ");
            if (p.Name == layout.RootKey && layout.IsReference(p.Value, out var id)) {
                sb.Append(d.RefText(id));
            } else if (p.Value is JValue value) {
                sb.Append(OneLine(value.ToString(Formatting.None)));
            } else if (p.Value is JObject o) {
                sb.Append(o.Count.ToString("N0")).Append(" entries");
            } else if (p.Value is JArray a) {
                sb.Append("array of ").Append(a.Count.ToString("N0"));
            }
            sb.Append('\n');
        }
        return sb.Length == 0 ? "  (empty)" : sb.ToString(0, sb.Length - 1);
    }

    private static string TypesText(PackedDocument d) {
        if (d.Types.Count == 0) { return "  (none)"; }
        var sb = new StringBuilder();
        foreach (var t in d.Types) {
            sb.Append("  ").Append(t.Id).Append("  ").Append(t.Objects.ToString("N0")).Append(t.Objects == 1 ? " object   " : " objects  ");
            sb.Append(!t.Listed ? "(no entry in the types table)" : t.FullName ?? "(entry without a name)").Append('\n');
        }
        return sb.ToString(0, sb.Length - 1);
    }

    private void OnGraphHover(int v) {
        _graphHover = v;
        Mark(v >= 0 ? v : _listHover);
        if (v >= 0 && _blocks is not null) { Blocks.ScrollIntoView(v + 1); }
        UpdateLabels();
        UpdateHoverText();
    }

    private void SetListHover(int v) {
        if (v == _listHover) { return; }
        _listHover = v;
        Graph.LinkedHighlight = v;
        Mark(v);
        UpdateLabels();
        UpdateHoverText();
    }

    private void Select(int v) {
        if (_blocks is null || v < 0) { return; }
        var block = _blocks[v + 1];
        Blocks.SelectedItem = ReferenceEquals(Blocks.SelectedItem, block) ? null : block;
        Blocks.ScrollIntoView(v + 1);
    }

    private void Mark(int v) {
        var block = v >= 0 && _blocks is not null ? _blocks[v + 1] : null;
        if (ReferenceEquals(block, _marked)) { return; }
        if (_marked is not null) { _marked.Marked = false; }
        _marked = block;
        if (block is not null) { block.Marked = true; }
    }

    // Every object is labelled on a small graph. On a larger one that is a mess, so only the root,
    // the selected object and the one under the pointer are.
    private void UpdateLabels() {
        var d = _doc;
        if (d?.Graph is null) { return; }
        var labels = new List<(int, string, uint)>();
        if (d.N <= LabelAllUpTo) {
            for (var v = 0; v < d.N; v++) { labels.Add((v, $"{d.ObjectIds[v]} {d.TypeName(v)}", ColorOf(v))); }
        } else {
            if (d.Root >= 0) { labels.Add((d.Root, $"root {d.ObjectIds[d.Root]}", Neutral)); }
            foreach (var v in new[] { _selected, _listHover, _graphHover }) {
                if (v >= 0 && v != d.Root) { labels.Add((v, $"{d.ObjectIds[v]} {d.TypeName(v)}", ColorOf(v))); }
            }
        }
        Graph.Labels = labels;
        Graph.InvalidateVisual();
    }

    private void UpdateHoverText() {
        var v = _graphHover >= 0 ? _graphHover : _listHover >= 0 ? _listHover : _selected;
        HoverText.Text = _doc is { } d && v >= 0 && v < d.N ? Describe(d, v) : "";
    }

    private static string Describe(PackedDocument d, int v) {
        const int show = 4;
        var sb = new StringBuilder();
        sb.Append(d.ObjectIds[v]).Append("  ").Append(d.TypeName(v));
        if (v == d.Root) { sb.Append("  root"); } else if (d.Root >= 0) { sb.Append(d.Reached[v] ? "  reached" : "  NOT REACHED"); }
        if (d.InLoop[v]) { sb.Append("  in a loop"); }

        int start = d.RefStart[v], end = d.RefStart[v + 1];
        sb.Append("   out ").Append(end - start);
        var perTarget = new Dictionary<int, int>();
        for (var i = start; i < end; i++) {
            if (i - start < show) { sb.Append(i == start ? ": " : ", ").Append(d.RefPath[i]).Append(" -> ").Append(d.ObjectIds[d.RefTo[i]]); }
            perTarget[d.RefTo[i]] = perTarget.GetValueOrDefault(d.RefTo[i]) + 1;
        }
        if (end - start > show) { sb.Append(", +").Append(end - start - show).Append(" more"); }
        foreach (var (target, count) in perTarget) {
            if (target == v) { sb.Append(count == 1 ? "  (refers to itself)" : $"  (refers to itself {count} times)"); }
            else if (count > 1) { sb.Append($"  ({count} refs to {d.ObjectIds[target]})"); }
        }

        var incoming = 0;
        var from = new StringBuilder();
        for (var i = 0; i < d.RefTo.Length; i++) {
            if (d.RefTo[i] != v) { continue; }
            if (incoming++ < show) { from.Append(incoming == 1 ? ": " : ", ").Append(d.ObjectIds[d.RefFrom[i]]).Append(' ').Append(d.RefPath[i]); }
        }
        sb.Append("   in ").Append(incoming).Append(from);
        if (incoming > show) { sb.Append(", +").Append(incoming - show).Append(" more"); }

        foreach (var dangling in d.Dangling) {
            if (dangling.From == v) { sb.Append("   DANGLING ").Append(dangling.Path).Append(" -> ").Append(dangling.TargetId); }
        }
        return sb.ToString();
    }
}
