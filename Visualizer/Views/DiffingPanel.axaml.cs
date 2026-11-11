using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Yewnyx.Algorithms.Diffing;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// Diffs two editable texts two ways side by side: Myers against Histogram, by lines or by characters;
/// Myers against Heckel, which finds moved lines; and lines alone against lines refined into the
/// characters that changed inside them.
/// </summary>
public partial class DiffingPanel : UserControl {
    // Character diffs of long, very different texts take a while; lines stay quick.
    private const int MaxCharacters = 20_000;

    private static readonly IBrush DeletedBackground = Brush("#4A2026"), DeletedForeground = Brush("#F2B8BE");
    private static readonly IBrush InsertedBackground = Brush("#1E4430"), InsertedForeground = Brush("#B8F2C8");
    private static readonly IBrush ChangedDeleted = Brush("#8A2E3A"), ChangedInserted = Brush("#2E7A4E");
    private static readonly IBrush MovedBackground = Brush("#1E3352"), MovedForeground = Brush("#B8D4F2");
    private static readonly IBrush NoteForeground = Brush("#7F8896");

    private static readonly (string Original, string Revised, string Note)[] Samples = [
        ("""
         int Add(int a, int b)
         {
             Log("add");
             return a + b;
         }

         int Subtract(int a, int b)
         {
             if (a == b)
             {
                 return 0;
             }
             Log("subtract");
             return a - b;
         }
         """, """
         int Subtract(int a, int b)
         {
             if (a < b)
             {
                 return -(b - a);
             }
             Log("subtract");
             return a - b;
         }

         int Multiply(int a, int b)
         {
             if (a == 0 || b == 0)
             {
                 return 0;
             }
             Log("multiply");
             return a * b;
         }
         """,
         "Add is removed, Subtract changes and Multiply is new. Myers keeps the most lines, so it pairs the functions up by their braces; " +
         "Histogram anchors on lines that occur once, so Subtract stays in one piece."),
        ("""
         Milk
         Eggs
         Bread
         Apples
         Coffee
         Rice
         """, """
         Coffee
         Milk
         Eggs
         Apples
         Rice
         Bread
         """,
         "Two items moved. Myers and Histogram show a move as a deletion and an insertion; Heckel's diff finds the moved lines (view Moves)."),
        ("""
         The quick brown fox jumps over the lazy dog.
         A diff finds the fewest changes between two texts.
         Sphinx of black quartz, judge my vow.
         """, """
         The quick red fox jumps over the sleeping dog.
         A diff finds the smallest set of changes between two texts.
         Sphinx of black quartz, judge my vow.
         """,
         "A few words changed. By lines, each changed line is replaced whole; view Lines, then the characters, to see which words changed."),
    ];

    // What a diff wrote, for the captions and the headline.
    private readonly record struct Result(Hunk[] Script, int Removed, int Added, int Moves, double Microseconds);

    private Hunk[]? _hunks;
    private int[]? _workspace;
    private BlockMove[]? _moves;
    private int _moveCount;
    private bool _loading;

    public DiffingPanel() {
        InitializeComponent();
        SampleBox.SelectionChanged += (_, _) => LoadSample();
        ViewBox.SelectionChanged += (_, _) => Update();
        OriginalBox.TextChanged += (_, _) => Update();
        RevisedBox.TextChanged += (_, _) => Update();
        LoadSample();
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private void LoadSample() {
        var sample = Samples[Math.Max(0, SampleBox.SelectedIndex)];
        _loading = true;
        OriginalBox.Text = sample.Original;
        RevisedBox.Text = sample.Revised;
        _loading = false;
        ActionText.Text = sample.Note;
        Update();
    }

    private void Update() {
        if (_loading) { return; }
        var original = Normalize(OriginalBox.Text);
        var revised = Normalize(RevisedBox.Text);
        var view = Math.Max(0, ViewBox.SelectedIndex);
        if (view is 1 or 3 && original.Length + revised.Length > MaxCharacters) {
            HeadlineText.Text = $"Too long to compare by characters here (over {MaxCharacters:N0}); compare by lines instead.";
            LeftText.Inlines?.Clear();
            RightText.Inlines?.Clear();
            return;
        }

        var (a, b) = (original.Split('\n'), revised.Split('\n'));
        switch (view) {
            case 0: {
                var myers = LineDiff(a, b, histogram: false, LeftText);
                var histogram = LineDiff(a, b, histogram: true, RightText);
                LeftCaption.Text = Caption("MyersDiff", myers, "lines");
                RightCaption.Text = Caption("HistogramDiff", histogram, "lines");
                HeadlineText.Text = Compare(myers, histogram, "lines");
                break;
            }
            case 1: {
                var myers = CharacterDiff(original, revised, histogram: false, LeftText);
                var histogram = CharacterDiff(original, revised, histogram: true, RightText);
                LeftCaption.Text = Caption("MyersDiff", myers, "characters");
                RightCaption.Text = Caption("HistogramDiff", histogram, "characters");
                HeadlineText.Text = Compare(myers, histogram, "characters");
                break;
            }
            case 2: {
                var myers = LineDiff(a, b, histogram: false, LeftText);
                var heckel = MoveDiff(a, b, RightText);
                LeftCaption.Text = Caption("MyersDiff", myers, "lines");
                RightCaption.Text = Caption("HeckelDiff", heckel, "lines") + $", {heckel.Moves:N0} {Plural(heckel.Moves, "move")}";
                var moved = 0;
                for (var i = 0; i < _moveCount; i++) { moved += _moves![i].Length; }
                HeadlineText.Text = heckel.Moves == 0
                    ? "Heckel finds no moved lines here: a block moves only if it holds a line that occurs once on each side."
                    : $"Heckel finds {heckel.Moves:N0} moved {Plural(heckel.Moves, "block")} ({moved:N0} {Plural(moved, "line")}, in blue), " +
                      "which Myers shows as lines deleted in one place and inserted in another.";
                break;
            }
            default: {
                var lines = LineDiff(a, b, histogram: true, LeftText);
                var refined = RefinedDiff(original, revised, RightText);
                LeftCaption.Text = Caption("HistogramDiff", lines, "lines");
                RightCaption.Text = Caption("GroupedDiff.Refine", refined, "characters");
                HeadlineText.Text = lines.Script.Length == 0
                    ? "No line changed."
                    : $"By lines, {lines.Removed:N0} {Plural(lines.Removed, "line")} removed and {lines.Added:N0} added; " +
                      $"refined into characters, {refined.Removed:N0} removed and {refined.Added:N0} added. Lines replaced by as many lines " +
                      "are compared pair by pair; the rest change whole.";
                break;
            }
        }
    }

    private static string Plural(int count, string word) => count == 1 ? word : word + "s";

    private static string Caption(string name, Result result, string unit) =>
        $"{name}: {result.Script.Length:N0} {Plural(result.Script.Length, "hunk")}, " +
        $"-{result.Removed:N0} +{result.Added:N0} {unit}, {result.Microseconds:N1} µs";

    private static string Compare(Result myers, Result histogram, string unit) {
        var (myersChanged, histogramChanged) = (myers.Removed + myers.Added, histogram.Removed + histogram.Added);
        if (myers.Script.AsSpan().SequenceEqual(histogram.Script)) {
            return $"Both find the same edits: {myers.Removed:N0} {unit} removed and {myers.Added:N0} added.";
        }
        return myersChanged == histogramChanged
            ? $"Both change {myersChanged:N0} {unit}, the fewest possible, but match up different ones."
            : $"They differ. Myers changes {myersChanged:N0} {unit}, the fewest possible; Histogram changes " +
              $"{histogramChanged:N0}, lining up {unit} that occur once instead of common ones.";
    }

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    // The best of a few runs, after one to warm up, so the time isn't mostly the first call's.
    private static double Time(Func<int> diff, out int count) {
        count = diff();
        var best = long.MaxValue;
        for (var i = 0; i < 5; i++) {
            var start = Stopwatch.GetTimestamp();
            count = diff();
            best = Math.Min(best, Stopwatch.GetTimestamp() - start);
        }
        return best * 1e6 / Stopwatch.Frequency;
    }

    private static Result Summarize(Hunk[] script, int moves, double microseconds) =>
        new(script, script.Sum(h => h.Original.Size), script.Sum(h => h.Revised.Size), moves, microseconds);

    private Result LineDiff(string[] a, string[] b, bool histogram, SelectableTextBlock target) {
        var time = Time(() => histogram
            ? HistogramDiff.Diff<string>(a, b, ref _hunks, ref _workspace)
            : MyersDiff.Diff<string>(a, b, ref _hunks, ref _workspace), out var count);
        var script = _hunks![..count];
        RenderLines(a, b, script, null, null, Clear(target));
        return Summarize(script, 0, time);
    }

    private Result CharacterDiff(string a, string b, bool histogram, SelectableTextBlock target) {
        var time = Time(() => histogram
            ? HistogramDiff.Diff(a.AsSpan(), b.AsSpan(), ref _hunks, ref _workspace)
            : MyersDiff.Diff(a.AsSpan(), b.AsSpan(), ref _hunks, ref _workspace), out var count);
        var script = _hunks![..count];
        RenderCharacters(a, b, script, Clear(target));
        return Summarize(script, 0, time);
    }

    private Result MoveDiff(string[] a, string[] b, SelectableTextBlock target) {
        var time = Time(() => HeckelDiff.Diff<string>(a, b, ref _hunks, ref _moves, ref _workspace, out _moveCount), out var count);
        var script = _hunks![..count];

        // Where each moved line went, and where each line moved here came from.
        var movedTo = Enumerable.Repeat(-1, a.Length).ToArray();
        var movedFrom = Enumerable.Repeat(-1, b.Length).ToArray();
        for (var i = 0; i < _moveCount; i++) {
            var (from, to, length) = _moves![i];
            for (var k = 0; k < length; k++) {
                movedTo[from + k] = to + k;
                movedFrom[to + k] = from + k;
            }
        }
        RenderLines(a, b, script, movedTo, movedFrom, Clear(target));
        return Summarize(script, _moveCount, time);
    }

    private Result RefinedDiff(string a, string b, SelectableTextBlock target) {
        var (linesA, linesB) = (Lines(a), Lines(b));
        var (classesA, classesB) = (new int[linesA.Length], new int[linesB.Length]);
        var classWorkspace = new int[GroupClasses.WorkspaceLength(linesA.Length, linesB.Length)];
        Hunk[]? lineHunks = null;
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)];
        var lineCount = 0;
        var time = Time(() => {
            GroupClasses.Assign(a.AsSpan(), linesA, b.AsSpan(), linesB, classesA, classesB, classWorkspace);
            lineCount = HistogramDiff.Diff<int>(classesA, classesB, ref lineHunks, ref _workspace);
            return GroupedDiff.Refine(a.AsSpan(), linesA, b.AsSpan(), linesB, lineHunks.AsSpan(0, lineCount), hunks, workspace);
        }, out var count);
        var script = hunks[..count];
        RenderRefined(a, b, linesA, linesB, lineHunks![..lineCount], script, Clear(target));
        return Summarize(script, 0, time);
    }

    private static InlineCollection Clear(SelectableTextBlock target) {
        var inlines = target.Inlines ??= new InlineCollection();
        inlines.Clear();
        return inlines;
    }

    // Lines with their line breaks, which tile the text, as GroupedDiff.Refine needs.
    private static Segment[] Lines(string text) {
        var lines = new List<Segment>();
        var start = 0;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] != '\n') { continue; }
            lines.Add(new Segment(start, i + 1 - start));
            start = i + 1;
        }
        if (start < text.Length) { lines.Add(new Segment(start, text.Length - start)); }
        return lines.ToArray();
    }

    // A unified diff: kept lines, then each hunk's removed lines and its added lines. Lines that moved
    // are drawn in blue with where they went or came from.
    private static void RenderLines(string[] a, string[] b, Hunk[] hunks, int[]? movedTo, int[]? movedFrom, InlineCollection inlines) {
        var width = 2 + Math.Max(a.Max(l => l.Length), b.Max(l => l.Length));
        void Line(string text, IBrush? background, IBrush? foreground, string note = "") {
            inlines.Add(Styled(new Run(text.PadRight(width)), background, foreground));
            if (note.Length > 0) { inlines.Add(new Run(note) { Foreground = NoteForeground }); }
            inlines.Add(new LineBreak());
        }

        var kept = 0;
        foreach (var (original, revised) in hunks) {
            for (; kept < original.Offset; kept++) { Line("  " + a[kept], null, null); }
            for (var i = original.Offset; i < original.Offset + original.Size; i++) {
                if (movedTo is not null && movedTo[i] >= 0) {
                    Line("→ " + a[i], MovedBackground, MovedForeground, $"  moved to line {movedTo[i] + 1}");
                } else {
                    Line("- " + a[i], DeletedBackground, DeletedForeground);
                }
            }
            for (var i = revised.Offset; i < revised.Offset + revised.Size; i++) {
                if (movedFrom is not null && movedFrom[i] >= 0) {
                    Line("← " + b[i], MovedBackground, MovedForeground, $"  moved from line {movedFrom[i] + 1}");
                } else {
                    Line("+ " + b[i], InsertedBackground, InsertedForeground);
                }
            }
            kept = original.Offset + original.Size;
        }
        for (; kept < a.Length; kept++) { Line("  " + a[kept], null, null); }
    }

    // The original text with removed characters struck through and added ones after them.
    private static void RenderCharacters(string a, string b, Hunk[] hunks, InlineCollection inlines) {
        var kept = 0;
        foreach (var (original, revised) in hunks) {
            Text(inlines, a[kept..original.Offset], null, null, false);
            Text(inlines, a.Substring(original.Offset, original.Size), DeletedBackground, DeletedForeground, true);
            Text(inlines, b.Substring(revised.Offset, revised.Size), InsertedBackground, InsertedForeground, false);
            kept = original.Offset + original.Size;
        }
        Text(inlines, a[kept..], null, null, false);
    }

    // Changed lines as a unified diff, each pair of lines that replace each other one above the other,
    // with the characters that changed inside them picked out.
    private static void RenderRefined(string a, string b, Segment[] linesA, Segment[] linesB, Hunk[] lineHunks, Hunk[] hunks,
        InlineCollection inlines) {
        var removed = new bool[a.Length];
        var added = new bool[b.Length];
        foreach (var (original, revised) in hunks) {
            removed.AsSpan(original.Offset, original.Size).Fill(true);
            added.AsSpan(revised.Offset, revised.Size).Fill(true);
        }

        void Line(string text, Segment line, string prefix, bool[]? changed, IBrush background, IBrush strong, IBrush foreground) {
            var end = line.Offset + line.Size - (line.Size > 0 && text[line.Offset + line.Size - 1] == '\n' ? 1 : 0);
            inlines.Add(Styled(new Run(prefix), changed is null ? null : background, changed is null ? null : foreground));
            for (var i = line.Offset; i < end;) {
                var start = i;
                var on = changed is not null && changed[i];
                while (i < end && (changed is not null && changed[i]) == on) { i++; }
                var run = new Run(text[start..i]);
                if (changed is not null) { Styled(run, on ? strong : background, foreground); }
                inlines.Add(run);
            }
            inlines.Add(new LineBreak());
        }

        var kept = 0;
        foreach (var (original, revised) in lineHunks) {
            for (; kept < original.Offset; kept++) { Line(a, linesA[kept], "  ", null, DeletedBackground, ChangedDeleted, DeletedForeground); }
            if (original.Size == revised.Size) {
                for (var i = 0; i < original.Size; i++) {
                    Line(a, linesA[original.Offset + i], "- ", removed, DeletedBackground, ChangedDeleted, DeletedForeground);
                    Line(b, linesB[revised.Offset + i], "+ ", added, InsertedBackground, ChangedInserted, InsertedForeground);
                }
            } else {
                for (var i = 0; i < original.Size; i++) { Line(a, linesA[original.Offset + i], "- ", removed, DeletedBackground, ChangedDeleted, DeletedForeground); }
                for (var i = 0; i < revised.Size; i++) { Line(b, linesB[revised.Offset + i], "+ ", added, InsertedBackground, ChangedInserted, InsertedForeground); }
            }
            kept = original.Offset + original.Size;
        }
        for (; kept < linesA.Length; kept++) { Line(a, linesA[kept], "  ", null, DeletedBackground, ChangedDeleted, DeletedForeground); }
    }

    // Adds text with its line breaks, marking a changed line break so it shows.
    private static void Text(InlineCollection inlines, string text, IBrush? background, IBrush? foreground, bool struck) {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            var part = lines[i] + (background is not null && i < lines.Length - 1 ? "↵" : "");
            if (part.Length > 0) {
                var run = Styled(new Run(part), background, foreground);
                if (struck) { run.TextDecorations = TextDecorations.Strikethrough; }
                inlines.Add(run);
            }
            if (i < lines.Length - 1) { inlines.Add(new LineBreak()); }
        }
    }

    // Kept text leaves its brushes unset, so it takes the block's colour: a null brush would hide it.
    private static Run Styled(Run run, IBrush? background, IBrush? foreground) {
        if (background is not null) { run.Background = background; }
        if (foreground is not null) { run.Foreground = foreground; }
        return run;
    }
}
