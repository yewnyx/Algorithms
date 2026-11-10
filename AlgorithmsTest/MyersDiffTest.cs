using FluentAssertions;
using Yewnyx.Algorithms.Diffing;

namespace Yewnyx.Algorithms.Test;

public class MyersDiffTest {
    // Each hunk reads "original -> revised", each side as "a[offset] items" (no items when empty).
    private static string[] Diff(string a, string b) {
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref workspace);
        return hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}").ToArray();
    }

    private static string Side(string name, string text, Graphs.Segment segment) =>
        segment.Size == 0 ? $"{name}[{segment.Offset}]" : $"{name}[{segment.Offset}] {text.Substring(segment.Offset, segment.Size)}";

    // Rebuilds b from a, the hunks and b's inserted items, and counts the items inserted or deleted.
    private static int Edits(string a, string b) {
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref workspace);
        var rebuilt = "";
        var copied = 0;
        var edits = 0;
        foreach (var (original, revised) in hunks[..count]) {
            rebuilt += a[copied..original.Offset] + b.Substring(revised.Offset, revised.Size);
            copied = original.Offset + original.Size;
            edits += original.Size + revised.Size;
        }
        rebuilt += a[copied..];
        rebuilt.Should().Be(b);
        return edits;
    }

    [Fact]
    public void InsertDeleteAndReplace() {
        Diff("123456", "102476").Should().Equal(
            "a[1] -> b[1] 0",
            "a[2] 3 -> b[3]",
            "a[4] 5 -> b[4] 7");
    }

    [Fact]
    public void EqualSequencesHaveNoHunks() {
        Diff("abc", "abc").Should().BeEmpty();
        Diff("", "").Should().BeEmpty();
    }

    [Fact]
    public void EmptySides() {
        Diff("", "ab").Should().Equal("a[0] -> b[0] ab");
        Diff("ab", "").Should().Equal("a[0] ab -> b[0]");
    }

    [Fact]
    public void EditsAtTheEnds() {
        Diff("abc", "xabc").Should().Equal("a[0] -> b[0] x");
        Diff("abc", "abcx").Should().Equal("a[3] -> b[3] x");
        Diff("abcd", "bcd").Should().Equal("a[0] a -> b[0]");
        Diff("abcd", "abc").Should().Equal("a[3] d -> b[3]");
    }

    [Fact]
    public void NothingInCommonIsOneReplacement() {
        Diff("abc", "xyz").Should().Equal("a[0] abc -> b[0] xyz");
    }

    [Fact]
    public void AmbiguousEditsSitLate() {
        // Equally short scripts could put the inserted or deleted "a" an item earlier.
        Diff("aba", "baa").Should().Equal("a[0] a -> b[0]", "a[3] -> b[2] a");
        Diff("baa", "aaba").Should().Equal("a[0] -> b[0] aa", "a[2] a -> b[4]");
    }

    [Fact]
    public void ScriptsAreShortest() {
        // The example from Myers' paper: 5 edits, keeping "CABA" or "BABA" (both length 4).
        Edits("ABCABBA", "CBABAC").Should().Be(5);
        // Swapped neighbours: keep one, move the other.
        Edits("ab", "ba").Should().Be(2);
        // Repeats give many equally short scripts.
        Edits("aaabbb", "bbbaaa").Should().Be(6);
        Edits("abcabcabc", "cbacbacba").Should().Be(8);
        Edits("the quick brown fox", "the quack brown box").Should().Be(4);
    }

    [Fact]
    public void ShortBuffersAreRejected() {
        var act = () => MyersDiff.Diff("ab".AsSpan(), "cd".AsSpan(), new Hunk[3], new int[1]);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");

        act = () => MyersDiff.Diff("ab".AsSpan(), "cd".AsSpan(), new Hunk[1], new int[100]);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void InputsMayOverlapButBuffersMayNot() {
        // One array holds both sides, which overlap, and then room for the workspace.
        var memory = new int[6 + MyersDiff.WorkspaceLength(4, 4)];
        int[] items = [1, 2, 3, 4, 5, 6];
        items.CopyTo(memory, 0);
        var hunks = new Hunk[MyersDiff.MaxHunks(4, 4)];

        var count = MyersDiff.Diff<int>(memory.AsSpan(0, 4), memory.AsSpan(2, 4), hunks, memory.AsSpan(6));
        hunks[..count].Should().Equal(new Hunk((0, 2), (0, 0)), new Hunk((4, 0), (2, 2)));

        // A workspace over the revised side only, then over the original side only.
        var act = () => MyersDiff.Diff<int>(memory.AsSpan(0, 4), memory.AsSpan(2, 4), hunks, memory.AsSpan(5));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        var apart = new int[8 + MyersDiff.WorkspaceLength(4, 4)];
        act = () => MyersDiff.Diff<int>(apart.AsSpan(4, 4), apart.AsSpan(0, 4), hunks, apart.AsSpan(6));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");

        // Hunks are items too, so a diff of hunks can be handed one of its inputs to write to: here
        // hunks over the original side only, then over the revised side only.
        var hunkMemory = new Hunk[6];
        var workspace = new int[MyersDiff.WorkspaceLength(2, 2)];
        act = () => MyersDiff.Diff<Hunk>(hunkMemory.AsSpan(2, 2), hunkMemory.AsSpan(0, 2), hunkMemory.AsSpan(3, 3), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
        act = () => MyersDiff.Diff<Hunk>(hunkMemory.AsSpan(0, 2), hunkMemory.AsSpan(2, 2), hunkMemory.AsSpan(3, 3), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void NullItemsCompare() {
        string[] a = ["a", null!, "b"];
        string[] b = ["a", "b", null!];
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff<string>(a, b, ref hunks, ref workspace);
        hunks[..count].Should().Equal(new Hunk((1, 1), (1, 0)), new Hunk((3, 0), (2, 1)));
    }
}
