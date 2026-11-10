using FluentAssertions;
using Yewnyx.Algorithms.Diffing;

namespace Yewnyx.Algorithms.Test;

public class HistogramDiffTest {
    // Each hunk reads "original -> revised", each side as "a[offset] items" (no items when empty).
    private static string[] Diff(string[] a, string[] b) {
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = HistogramDiff.Diff<string>(a, b, ref hunks, ref workspace);
        return hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}").ToArray();
    }

    private static string Side(string name, string[] items, Graphs.Segment segment) =>
        segment.Size == 0
            ? $"{name}[{segment.Offset}]"
            : $"{name}[{segment.Offset}] {string.Join(" ", items.Skip(segment.Offset).Take(segment.Size).Select(i => $"'{i}'"))}";

    private static string[] Chars(string s) => s.Select(c => c.ToString()).ToArray();

    [Fact]
    public void InsertDeleteAndReplace() {
        Diff(Chars("123456"), Chars("102476")).Should().Equal(
            "a[1] -> b[1] '0'",
            "a[2] '3' -> b[3]",
            "a[4] '5' -> b[4] '7'");
    }

    [Fact]
    public void EqualAndEmpty() {
        Diff(Chars("abc"), Chars("abc")).Should().BeEmpty();
        Diff([], []).Should().BeEmpty();
        Diff([], Chars("ab")).Should().Equal("a[0] -> b[0] 'a' 'b'");
        Diff(Chars("ab"), []).Should().Equal("a[0] 'a' 'b' -> b[0]");
        Diff(Chars("abc"), Chars("xyz")).Should().Equal("a[0] 'a' 'b' 'c' -> b[0] 'x' 'y' 'z'");
    }

    [Fact]
    public void UniqueLinesStayAligned() {
        string[] a = ["", "", "", "return x;"];
        string[] b = ["", "", "}", "return x;", ""];

        // Both scripts are 3 edits. Myers keeps the third blank line and moves "return x;";
        // histogram anchors on the unique "return x;" and edits the blank lines around it.
        Diff(a, b).Should().Equal(
            "a[2] '' -> b[2] '}'",
            "a[4] -> b[4] ''");

        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff<string>(a, b, ref hunks, ref workspace);
        hunks[..count].Should().Equal(new Hunk((2, 0), (2, 2)), new Hunk((3, 1), (5, 0)));
    }

    [Fact]
    public void CommonItemsFallBackToMyers() {
        // "x" appears more than MaxOccurrences times, so it can't anchor a split.
        var x = Enumerable.Repeat("x", HistogramDiff.MaxOccurrences + 1);
        string[] a = [..x, "a"];
        string[] b = ["b", ..x];

        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff<string>(a, b, ref hunks, ref workspace);
        var myers = hunks[..count];
        count = HistogramDiff.Diff<string>(a, b, ref hunks, ref workspace);
        hunks[..count].Should().Equal(myers);
    }

    [Fact]
    public void ShortBuffersAreRejected() {
        var act = () => HistogramDiff.Diff("ab".AsSpan(), "cd".AsSpan(), new Hunk[3], new int[1]);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");

        act = () => HistogramDiff.Diff("ab".AsSpan(), "cd".AsSpan(), new Hunk[1], new int[100]);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void InputsMayOverlapButBuffersMayNot() {
        // One array holds both sides, which overlap, and then room for the workspace.
        var memory = new int[6 + HistogramDiff.WorkspaceLength(4, 4)];
        int[] items = [1, 2, 3, 4, 5, 6];
        items.CopyTo(memory, 0);
        var hunks = new Hunk[HistogramDiff.MaxHunks(4, 4)];

        var count = HistogramDiff.Diff<int>(memory.AsSpan(0, 4), memory.AsSpan(2, 4), hunks, memory.AsSpan(6));
        hunks[..count].Should().Equal(new Hunk((0, 2), (0, 0)), new Hunk((4, 0), (2, 2)));

        // A workspace over the revised side only, then over the original side only.
        var act = () => HistogramDiff.Diff<int>(memory.AsSpan(0, 4), memory.AsSpan(2, 4), hunks, memory.AsSpan(5));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        var apart = new int[8 + HistogramDiff.WorkspaceLength(4, 4)];
        act = () => HistogramDiff.Diff<int>(apart.AsSpan(4, 4), apart.AsSpan(0, 4), hunks, apart.AsSpan(6));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");

        // Hunks are items too, so a diff of hunks can be handed one of its inputs to write to: here
        // hunks over the original side only, then over the revised side only.
        var hunkMemory = new Hunk[6];
        var workspace = new int[HistogramDiff.WorkspaceLength(2, 2)];
        act = () => HistogramDiff.Diff<Hunk>(hunkMemory.AsSpan(2, 2), hunkMemory.AsSpan(0, 2), hunkMemory.AsSpan(3, 3), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
        act = () => HistogramDiff.Diff<Hunk>(hunkMemory.AsSpan(0, 2), hunkMemory.AsSpan(2, 2), hunkMemory.AsSpan(3, 3), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }
}
