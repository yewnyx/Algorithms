using System.Diagnostics;
using FluentAssertions;
using Yewnyx.Algorithms.Diffing;

namespace Yewnyx.Algorithms.Test;

public class HeckelDiffTest {
    // Hunks read "original -> revised", each side as "a[offset] items"; moves read "a[offset] items -> b[offset]".
    private static string[] Diff(string a, string b, out string[] moves) {
        Hunk[]? hunks = null;
        BlockMove[]? found = null;
        int[]? workspace = null;
        var count = HeckelDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref found, ref workspace, out var moveCount);
        Check(a.AsSpan(), b.AsSpan(), hunks.AsSpan(0, count), found.AsSpan(0, moveCount));
        moves = found[..moveCount].Select(m => $"a[{m.Original}] {a.Substring(m.Original, m.Length)} -> b[{m.Revised}]").ToArray();
        return hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}").ToArray();
    }

    private static string Side(string name, string text, Graphs.Segment segment) =>
        segment.Size == 0 ? $"{name}[{segment.Offset}]" : $"{name}[{segment.Offset}] {text.Substring(segment.Offset, segment.Size)}";

    // The hunks rebuild the revised sequence through a Patch. Each move pairs equal runs that lie inside
    // one hunk on each side, and moves come in original order, apart, and maximal.
    private static void Check<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, ReadOnlySpan<Hunk> hunks, ReadOnlySpan<BlockMove> moves)
        where T : IEquatable<T> {
        var patch = new Patch<T>();
        patch.Set(a, b, hunks);
        var rebuilt = new T[b.Length];
        patch.TryApply(a, rebuilt).Should().BeTrue();
        rebuilt.Should().Equal(b.ToArray());

        for (var i = 0; i < moves.Length; i++) {
            var (from, to, length) = moves[i];
            length.Should().BePositive();
            for (var k = 0; k < length; k++) { Same(a[from + k], b[to + k]).Should().BeTrue(); }
            Inside(hunks, from, length, original: true).Should().BeTrue($"move {i} is deleted");
            Inside(hunks, to, length, original: false).Should().BeTrue($"move {i} is inserted");
            for (var j = 0; j < i; j++) {
                (moves[j].Original + moves[j].Length).Should().BeLessThanOrEqualTo(from, "moves are in original order and apart");
                (moves[j].Revised + moves[j].Length <= to || to + length <= moves[j].Revised).Should().BeTrue("moves don't overlap");
                (moves[j].Original + moves[j].Length == from && moves[j].Revised + moves[j].Length == to).Should().BeFalse("moves are maximal");
            }
        }
    }

    private static bool Same<T>(T a, T b) where T : IEquatable<T> => a is null ? b is null : a.Equals(b);

    private static bool Inside(ReadOnlySpan<Hunk> hunks, int start, int length, bool original) {
        foreach (var hunk in hunks) {
            var side = original ? hunk.Original : hunk.Revised;
            if (side.Offset <= start && start + length <= side.Offset + side.Size) { return true; }
        }
        return false;
    }

    [Fact]
    public void AMovedBlockIsOneMove() {
        Diff("abcde", "adebc", out var moves).Should().Equal("a[1] -> b[1] de", "a[3] de -> b[5]");
        moves.Should().Equal("a[3] de -> b[1]");
    }

    [Fact]
    public void OfTwoEqualBlocksThatSwapTheSecondMoves() {
        Diff("ab", "ba", out var moves).Should().Equal("a[0] -> b[0] b", "a[1] b -> b[2]");
        moves.Should().Equal("a[1] b -> b[0]");

        Diff("abcd", "cdab", out moves).Should().Equal("a[0] -> b[0] cd", "a[2] cd -> b[4]");
        moves.Should().Equal("a[2] cd -> b[0]");

        // "a" and "b" both lead to "c" equally; the earlier, "a", stays.
        Diff("abc", "bac", out moves).Should().Equal("a[0] -> b[0] b", "a[1] b -> b[2]");
        moves.Should().Equal("a[1] b -> b[0]");
    }

    [Fact]
    public void AsManyItemsStayAsTheLongestRunInOrderAllows() {
        // 1 2 then 4 or 5 can stay: three items, and the blocks holding the most of them stay.
        Diff("31254", "12345", out var moves).Should().Equal("a[0] 3 -> b[0]", "a[3] -> b[2] 34", "a[4] 4 -> b[5]");
        moves.Should().Equal("a[0] 3 -> b[2]", "a[4] 4 -> b[3]");
    }

    [Fact]
    public void BlocksGrowFromUniqueItemsOverRepeatedOnes() {
        Diff("xaay", "yxaa", out var moves).Should().Equal("a[0] -> b[0] y", "a[3] y -> b[4]");
        moves.Should().Equal("a[3] y -> b[0]");
    }

    [Fact]
    public void BlocksGrowBothWays() {
        // "a" occurs twice in the revised side, so only "u" anchors; "a" joins its block forwards, then
        // backwards. In both, the block moves.
        Diff("pquaxyz", "pqaxyzua", out var moves).Should().Equal("a[2] ua -> b[2] a", "a[7] -> b[6] ua");
        moves.Should().Equal("a[2] ua -> b[6]");
        Diff("pqauxyz", "pqxyzaua", out moves).Should().Equal("a[2] au -> b[2]", "a[7] -> b[5] aua");
        moves.Should().Equal("a[2] au -> b[5]");
    }

    [Fact]
    public void AMovedItemNeverMatches() {
        // The moved "ua" sits in the same gap as the revised side's spare "a"; matching those two would
        // leave the move half outside its hunk.
        Diff("pquaxyz", "pqaxyzua", out var moves).Should().Contain("a[2] ua -> b[2] a");
        moves.Should().Equal("a[2] ua -> b[6]");
        // The same the other way round: the moved "ua" is on the revised side of the gap.
        Diff("pqaxyzua", "pquaxyz", out moves).Should().Equal("a[2] a -> b[2] ua", "a[6] ua -> b[7]");
        moves.Should().Equal("a[6] ua -> b[2]");
    }

    [Fact]
    public void AHunkHoldingAMoveStaysWhereTheMoveIs() {
        // The moved "au" is followed by an equal "a" that stays, so sliding the hunk would split the move.
        Diff("auaxy", "axyau", out var moves).Should().Equal("a[0] au -> b[0]", "a[5] -> b[3] au");
        moves.Should().Equal("a[0] au -> b[3]");
        Diff("axyau", "auaxy", out moves).Should().Equal("a[0] -> b[0] au", "a[3] au -> b[5]");
        moves.Should().Equal("a[3] au -> b[0]");
    }

    [Fact]
    public void OnlyItemsOnceOnBothSidesAnchor() {
        // "a" is twice in the original, so only "x" anchors, and the block it grows keeps the second "a".
        Diff("axa", "xa", out var moves).Should().Equal("a[0] a -> b[0]");
        moves.Should().BeEmpty();
        Diff("xa", "axa", out moves).Should().Equal("a[0] -> b[0] a");
        moves.Should().BeEmpty();
    }

    [Fact]
    public void PureEditsSitLate() {
        Diff("aab", "ab", out var moves).Should().Equal("a[1] a -> b[1]");
        moves.Should().BeEmpty();
    }

    [Fact]
    public void NothingInCommonIsOneReplacement() {
        Diff("xyz", "abc", out var moves).Should().Equal("a[0] xyz -> b[0] abc");
        moves.Should().BeEmpty();
    }

    [Fact]
    public void ABlockOfRepeatedItemsHasNoAnchor() {
        // "bb" moved, but nothing in it occurs once, so it shows as a deletion and an insertion.
        Diff("xbby", "bbxy", out var moves).Should().Equal("a[0] -> b[0] bb", "a[1] bb -> b[3]");
        moves.Should().BeEmpty();
    }

    // Where every value is on both sides and none once on each, nothing anchors and nothing is left out.
    [Theory]
    [InlineData("aabba", "bbaab")]
    [InlineData("aab", "abb")]
    [InlineData("abab", "baba")]
    [InlineData("aaaa", "aa")]
    public void WithoutUniqueItemsItIsMyers(string a, string b) {
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref workspace);
        var myers = hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}");

        Diff(a, b, out var moves).Should().Equal(myers);
        moves.Should().BeEmpty();
    }

    [Fact]
    public void EqualAndEmpty() {
        Diff("abc", "abc", out var moves).Should().BeEmpty();
        moves.Should().BeEmpty();
        Diff("", "", out _).Should().BeEmpty();
        Diff("", "ab", out _).Should().Equal("a[0] -> b[0] ab");
        Diff("ab", "", out _).Should().Equal("a[0] ab -> b[0]");
    }

    [Fact]
    public void NullItemsCompare() {
        string[] a = ["a", null!, "b"];
        string[] b = [null!, "b", "a"];
        Hunk[]? hunks = null;
        BlockMove[]? moves = null;
        int[]? workspace = null;
        var count = HeckelDiff.Diff<string>(a, b, ref hunks, ref moves, ref workspace, out var moveCount);

        Check<string>(a, b, hunks.AsSpan(0, count), moves.AsSpan(0, moveCount));
        moves[..moveCount].Should().Equal(new BlockMove(0, 2, 1));
    }

    // Diffs, timing it, and checks the hunks through a Patch.
    private static (TimeSpan Time, int Moves) Timed(int[] a, int[] b) {
        Hunk[]? hunks = null;
        BlockMove[]? moves = null;
        int[]? workspace = null;
        var watch = Stopwatch.StartNew();
        var count = HeckelDiff.Diff<int>(a, b, ref hunks, ref moves, ref workspace, out var moveCount);
        var time = watch.Elapsed;

        var patch = new Patch<int>();
        patch.Set(a, b, hunks.AsSpan(0, count));
        var rebuilt = new int[b.Length];
        patch.TryApply(a, rebuilt).Should().BeTrue();
        rebuilt.Should().Equal(b);
        return (time, moveCount);
    }

    [Fact]
    public void MovedItemsStayOutOfMyers() {
        // Each half reversed: one item of each half stays, and between them lie about n / 2 moved items on
        // each side with nothing in common, which Myers' diff would take quadratic time over.
        const int n = 100_000, half = n / 2;
        var a = Enumerable.Range(0, n).ToArray();
        var b = Enumerable.Range(0, half).Select(i => half - 1 - i).Concat(Enumerable.Range(0, half).Select(i => n - 1 - i)).ToArray();

        var (time, moves) = Timed(a, b);
        moves.Should().Be(n - 2);
        time.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ItemsTheOtherSideLacksStayOutOfMyers() {
        // A list replaced wholesale: no item can match, which Myers' diff would take quadratic time to find.
        const int n = 100_000;
        var (time, moves) = Timed(Enumerable.Range(0, n).ToArray(), Enumerable.Range(n, n).ToArray());
        moves.Should().Be(0);
        time.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    private readonly struct Clashing(char value) : IEquatable<Clashing> {
        private readonly char _value = value;
        public bool Equals(Clashing other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Clashing other && Equals(other);
        public override int GetHashCode() => 0;
    }

    [Fact]
    public void HashesThatClashGiveTheSameResult() {
        const string a = "the quick brown fox", b = "brown fox the quick";
        Hunk[]? hunks = null;
        BlockMove[]? moves = null;
        int[]? workspace = null;
        var count = HeckelDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref moves, ref workspace, out var moveCount);
        var expected = (hunks[..count], moves[..moveCount]);

        var count2 = HeckelDiff.Diff<Clashing>(a.Select(c => new Clashing(c)).ToArray(), b.Select(c => new Clashing(c)).ToArray(),
            ref hunks, ref moves, ref workspace, out var moveCount2);
        (hunks[..count2], moves[..moveCount2]).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
    }

    [Fact]
    public void ShortBuffersAreRejected() {
        var workspace = new int[HeckelDiff.WorkspaceLength(2, 2)];
        var act = () => HeckelDiff.Diff("ab".AsSpan(), "ba".AsSpan(), new Hunk[3], new BlockMove[2], new int[1], out _);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => HeckelDiff.Diff("ab".AsSpan(), "ba".AsSpan(), new Hunk[3], new BlockMove[1], workspace, out _);
        act.Should().Throw<ArgumentException>().WithParameterName("moves");
        act = () => HeckelDiff.Diff("ab".AsSpan(), "ba".AsSpan(), new Hunk[1], new BlockMove[2], workspace, out _);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void BuffersMayNotShareMemoryWithTheInputs() {
        // Moves are items too, so a diff of moves can be handed one of its inputs to write to.
        var memory = new BlockMove[4];
        var workspace = new int[HeckelDiff.WorkspaceLength(2, 2)];
        var act = () => HeckelDiff.Diff<BlockMove>(memory.AsSpan(0, 2), new BlockMove[2], new Hunk[3], memory.AsSpan(1, 2), workspace, out _);
        act.Should().Throw<ArgumentException>().WithParameterName("moves");

        var ints = new int[4 + HeckelDiff.WorkspaceLength(2, 2)];
        act = () => HeckelDiff.Diff<int>(ints.AsSpan(0, 2), ints.AsSpan(2, 2), new Hunk[3], new BlockMove[2], ints.AsSpan(3), out _);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
    }

    [Fact]
    public void AWarmDiffDoesntAllocate() {
        var a = "the quick brown fox jumps over the lazy dog".ToCharArray();
        var b = "over the lazy dog the quick fox jumps brown".ToCharArray();
        Hunk[]? hunks = null;
        BlockMove[]? moves = null;
        int[]? workspace = null;
        HeckelDiff.Diff<char>(a, b, ref hunks, ref moves, ref workspace, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        HeckelDiff.Diff<char>(a, b, ref hunks, ref moves, ref workspace, out var moveCount);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        moveCount.Should().BePositive();
    }
}
