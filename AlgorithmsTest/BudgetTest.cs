using FluentAssertions;
using Yewnyx.Algorithms.Diffing;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

// The edit budget: scripts stay valid whatever the budget, a budget that covers the shortest script changes
// nothing, and a search costs about (n + m) * budget item comparisons.
public class BudgetTest {
    private static readonly int[] Budgets = [0, 1, 2, 3, 4, 5, 6, 7, 10, 100, int.MaxValue];

    private static readonly (string Original, string Revised)[] Pairs = [
        ("ABCABBA", "CBABAC"),
        ("1ABCD9", "1BCDA9"),
        ("bccddb", "bddbcb"),
        ("cbaaaa", "aadbbb"),
        ("abcdefghij", "xaybzcwdvu"),
        ("abcdefghijklmnop", "bacdfehgjikllmpo"),
        ("aXbXcXdXe", "aYbYcYdYe"),
        ("abc", "xyz"),
        ("", "abc"),
        ("abc", ""),
        ("same", "same"),
    ];

    // Each hunk reads "original -> revised", each side as "a[offset] items" (no items when empty).
    private static string[] Describe(string a, string b, ReadOnlySpan<Hunk> hunks) {
        var lines = new List<string>();
        foreach (var (original, revised) in hunks) {
            lines.Add($"{Side("a", a, original)} -> {Side("b", b, revised)}");
        }
        return lines.ToArray();
    }

    private static string Side(string name, string text, Segment segment) =>
        segment.Size == 0 ? $"{name}[{segment.Offset}]" : $"{name}[{segment.Offset}] {text.Substring(segment.Offset, segment.Size)}";

    private static int Edits(ReadOnlySpan<Hunk> hunks) {
        var edits = 0;
        foreach (var (original, revised) in hunks) { edits += original.Size + revised.Size; }
        return edits;
    }

    // The script turns a into b and back.
    private static void RequireValid<T>(T[] a, T[] b, ReadOnlySpan<Hunk> hunks) where T : IEquatable<T> {
        var patch = new Patch<T>();
        patch.Set(a, b, hunks);
        var applied = new T[b.Length];
        patch.TryApply(a, applied).Should().BeTrue();
        applied.Should().Equal(b);
        var reverted = new T[a.Length];
        patch.TryRevert(b, reverted).Should().BeTrue();
        reverted.Should().Equal(a);
    }

    // Each entry point, run with buffers of exactly the stated sizes. Gives the hunks and whether it was cut short.
    private delegate (Hunk[] Hunks, bool CutShort) Entry(string a, string b, int budget);

    private static (Hunk[], bool) Myers(string a, string b, int budget) {
        var hunks = new Hunk[MyersDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[MyersDiff.WorkspaceLength(a.Length, b.Length)];
        var count = MyersDiff.Diff<char>(a, b, hunks, workspace, budget, out var cutShort);
        return (hunks[..count], cutShort);
    }


    private static (Hunk[], bool) Heckel(string a, string b, int budget) {
        var hunks = new Hunk[HeckelDiff.MaxHunks(a.Length, b.Length)];
        var moves = new BlockMove[HeckelDiff.MaxMoves(a.Length, b.Length)];
        var workspace = new int[HeckelDiff.WorkspaceLength(a.Length, b.Length)];
        var count = HeckelDiff.Diff<char>(a, b, hunks, moves, workspace, budget, out _, out var cutShort);
        return (hunks[..count], cutShort);
    }

    // The text as one group on each side, so Refine diffs the two whole.
    private static (Hunk[], bool) Refine(string a, string b, int budget) {
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)];
        Segment[] groupsA = a.Length == 0 ? [] : [new Segment(0, a.Length)];
        Segment[] groupsB = b.Length == 0 ? [] : [new Segment(0, b.Length)];
        Hunk[] groupHunks = a == b ? [] : [new Hunk(new Segment(0, groupsA.Length), new Segment(0, groupsB.Length))];
        var count = GroupedDiff.Refine<char>(a, groupsA, b, groupsB, groupHunks, hunks, workspace, budget, out var cutShort);
        return (hunks[..count], cutShort);
    }

    private static readonly (string Name, Entry Run)[] Entries = [
        ("MyersDiff", Myers),
        ("HeckelDiff", Heckel),
        ("GroupedDiff.Refine", Refine),
    ];

    [Fact]
    public void EveryBudgetGivesAScriptThatPatchesBothWays() {
        foreach (var (name, run) in Entries) {
            foreach (var (a, b) in Pairs) {
                foreach (var budget in Budgets) {
                    var (hunks, _) = run(a, b, budget);
                    try { RequireValid(a.ToCharArray(), b.ToCharArray(), hunks); }
                    catch (Exception) { throw new Exception($"{name}, \"{a}\" -> \"{b}\", budget {budget}"); }
                }
            }
        }
    }

    [Fact]
    public void ABudgetThatCoversTheShortestScriptChangesNothing() {
        // Checked against each entry point's own script, whatever its length: with a budget as large as the
        // script it gives the same hunks, and it never reports a cut.
        foreach (var (name, run) in Entries) {
            foreach (var (a, b) in Pairs) {
                var (plain, plainCut) = run(a, b, int.MaxValue);
                plainCut.Should().BeFalse();
                foreach (var budget in Budgets.Where(budget => budget >= Edits(plain))) {
                    var (hunks, cutShort) = run(a, b, budget);
                    hunks.Should().Equal(plain, $"{name}, \"{a}\" -> \"{b}\", budget {budget}");
                    cutShort.Should().BeFalse();
                }
            }
        }
    }

    [Fact]
    public void AScriptLongerThanTheShortestIsReportedAsCutShort() {
        foreach (var (name, run) in Entries) {
            foreach (var (a, b) in Pairs) {
                var (plain, _) = run(a, b, int.MaxValue);
                foreach (var budget in Budgets) {
                    var (hunks, cutShort) = run(a, b, budget);
                    if (Edits(hunks) > Edits(plain)) { cutShort.Should().BeTrue($"{name}, \"{a}\" -> \"{b}\", budget {budget}"); }
                }
            }
        }
    }

    [Fact]
    public void BudgetZeroMatchesOnlyTheCommonStartAndEnd() {
        // The shortest script moves the "A" with two edits, but nothing is searched for.
        var (hunks, cutShort) = Myers("1ABCD9", "1BCDA9", 0);
        Describe("1ABCD9", "1BCDA9", hunks).Should().Equal("a[1] ABCD -> b[1] BCDA");
        cutShort.Should().BeTrue();
        Describe("1ABCD9", "1BCDA9", Myers("1ABCD9", "1BCDA9", int.MaxValue).Item1).Should().Equal("a[1] A -> b[1]", "a[5] -> b[4] A");
    }

    [Fact]
    public void BudgetZeroIsNotCutShortWhenTheStartAndEndAreAllThereIs() {
        var (hunks, cutShort) = Myers("abc", "abXc", 0);
        Describe("abc", "abXc", hunks).Should().Equal("a[2] -> b[2] X");
        cutShort.Should().BeFalse();
    }

    [Fact]
    public void ASearchThatRunsOutSplitsAtTheBestPointItReached() {
        // "cc" and "dd" against "dd" and "bc" needs four edits: delete "cc" and insert "bc". With a budget
        // of two the search stops short, and the script pairs the first "d" with the "cc" instead.
        const string a = "bccddb", b = "bddbcb";
        var (cut, cutShort) = Myers(a, b, 2);
        Describe(a, b, cut).Should().Equal("a[1] cc -> b[1] d", "a[4] d -> b[3] bc");
        cutShort.Should().BeTrue();
        Edits(cut).Should().Be(6);
        RequireValid(a.ToCharArray(), b.ToCharArray(), cut);

        // A budget of four covers the shortest script.
        var (exact, exactCut) = Myers(a, b, 4);
        Edits(exact).Should().Be(4);
        exactCut.Should().BeFalse();
    }

    [Fact]
    public void HistogramDiffRunsItsMyersFallbackToTheEnd() {
        // Three values, each far more common than HistogramDiff anchors on, so the whole input goes to its
        // Myers fallback, which has no budget: the hunks are exactly MyersDiff's, however long the script.
        var a = Enumerable.Range(0, 300).Select(i => (i * i + i / 3) % 3).ToArray();
        var b = Enumerable.Range(0, 300).Select(i => (i * 2 + i / 4) % 3).ToArray();
        var myers = new Hunk[MyersDiff.MaxHunks(a.Length, b.Length)];
        var myersCount = MyersDiff.Diff<int>(a, b, myers, new int[MyersDiff.WorkspaceLength(a.Length, b.Length)]);
        var histogram = new Hunk[HistogramDiff.MaxHunks(a.Length, b.Length)];
        var histogramCount = HistogramDiff.Diff<int>(a, b, histogram, new int[HistogramDiff.WorkspaceLength(a.Length, b.Length)]);
        Edits(myers.AsSpan(0, myersCount)).Should().BeGreaterThan(100);
        histogram[..histogramCount].Should().Equal(myers[..myersCount]);
    }

    [Fact]
    public void ANegativeBudgetThrows() {
        var hunks = new Hunk[4];
        var workspace = new int[64];
        var moves = new BlockMove[4];
        Hunk[]? hunkArray = null;
        int[]? workspaceArray = null;
        BlockMove[]? moveArray = null;
        Assert.Throws<ArgumentOutOfRangeException>(() => MyersDiff.Diff<char>("abc", "abd", hunks, workspace, -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MyersDiff.Diff<char>("abc", "abd", ref hunkArray, ref workspaceArray, -1, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => HeckelDiff.Diff<char>("abc", "abd", hunks, moves, workspace, -1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeckelDiff.Diff<char>("abc", "abd", ref hunkArray, ref moveArray, ref workspaceArray, -1, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupedDiff.Refine<char>("abc", [new Segment(0, 3)], "abd", [new Segment(0, 3)],
            [new Hunk(new Segment(0, 1), new Segment(0, 1))], hunks, workspace, -1, out _));
    }

    [Fact]
    public void TheArrayOverloadsReplaceShortArraysAndAgreeWithTheSpanOnes() {
        const string a = "bccddb", b = "bddbcb";
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff<char>(a, b, ref hunks, ref workspace, 2, out var cutShort);
        Describe(a, b, hunks.AsSpan(0, count)).Should().Equal("a[1] cc -> b[1] d", "a[4] d -> b[3] bc");
        cutShort.Should().BeTrue();

        BlockMove[]? moves = null;
        Hunk[]? heckelHunks = null;
        int[]? heckelWorkspace = null;
        count = HeckelDiff.Diff<char>(a, b, ref heckelHunks, ref moves, ref heckelWorkspace, 2, out _, out cutShort);
        heckelHunks.AsSpan(0, count).ToArray().Should().Equal(Heckel(a, b, 2).Item1);
        cutShort.Should().BeTrue();

    }

    [Fact]
    public void HeckelSearchesItsGapsWithTheBudget() {
        // No item occurs once on each side, so the whole input is one gap, searched as Myers' diff would.
        const string a = "bccddb", b = "bddbcb";
        var (cut, cutShort) = Heckel(a, b, 2);
        Describe(a, b, cut).Should().Equal("a[1] cc -> b[1] d", "a[4] d -> b[3] bc");
        cutShort.Should().BeTrue();

        // A budget of four covers the shortest script.
        var (exact, exactCut) = Heckel(a, b, 4);
        exact.Should().Equal(Heckel(a, b, int.MaxValue).Item1);
        Edits(exact).Should().Be(4);
        exactCut.Should().BeFalse();
    }

    [Fact]
    public void HeckelReportsACutInAnyGapNotOnlyTheLast() {
        // "#" and "a" occur once on each side and anchor the diff: the gap before them is cut, the "bc"
        // against "bXc" after them needs no search.
        const string a = "bccddb#abc", b = "bddbcb#abXc";
        var (hunks, cutShort) = Heckel(a, b, 2);
        Describe(a, b, hunks).Should().Equal("a[1] cc -> b[1] d", "a[4] d -> b[3] bc", "a[9] -> b[9] X");
        cutShort.Should().BeTrue();
        Edits(hunks).Should().Be(7);
        Edits(Heckel(a, b, int.MaxValue).Item1).Should().Be(5);
    }

    [Fact]
    public void RefineReportsACutInAnyPairNotOnlyTheLast() {
        // Two lines each: the first pair is cut, the second ("abc" against "abXc") needs no search.
        const string a = "bccddbabc", b = "bddbcbabXc";
        Segment[] groupsA = [new Segment(0, 6), new Segment(6, 3)];
        Segment[] groupsB = [new Segment(0, 6), new Segment(6, 4)];
        Hunk[] groupHunks = [new Hunk(new Segment(0, 2), new Segment(0, 2))];
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)];
        var count = GroupedDiff.Refine<char>(a, groupsA, b, groupsB, groupHunks, hunks, workspace, 2, out var cutShort);
        Describe(a, b, hunks.AsSpan(0, count)).Should().Equal("a[1] cc -> b[1] d", "a[4] d -> b[3] bc", "a[8] -> b[8] X");
        cutShort.Should().BeTrue();

        count = GroupedDiff.Refine<char>(a, groupsA, b, groupsB, groupHunks, hunks, workspace, int.MaxValue, out cutShort);
        Describe(a, b, hunks.AsSpan(0, count)).Should().Equal("a[1] cc -> b[1]", "a[6] -> b[4] cb", "a[8] -> b[8] X");
        cutShort.Should().BeFalse();
    }

    // The cost tests count item comparisons, so they don't depend on the machine. A search costs about
    // (n + m) * budget; the limit here is twice that, for the passes around the searches.
    private readonly struct Counted(int value) : IEquatable<Counted> {
        private readonly int _value = value;
        public static long Comparisons;
        public bool Equals(Counted other) {
            Comparisons++;
            return _value == other._value;
        }
        public override bool Equals(object? obj) => obj is Counted other && Equals(other);
        public override int GetHashCode() => _value;
    }

    private static Counted[] Items(IEnumerable<int> values) => values.Select(v => new Counted(v)).ToArray();

    // Every second item replaced, so the script is as long as half the items and every search is cut.
    private static (Counted[], Counted[]) EverySecondChanged(int length) =>
        (Items(Enumerable.Range(0, length)), Items(Enumerable.Range(0, length).Select(i => i % 2 == 1 ? -i - 1 : i)));

    // An unrelated front, then the same items, then a differing last item: a rewritten first half in a
    // file whose end differs. The search from the end finds the shared items after one edit each time.
    private static (Counted[], Counted[]) UnrelatedFrontSharedBack(int front, int shared) {
        IEnumerable<int> Side(int frontBase, int last) =>
            Enumerable.Range(frontBase, front).Concat(Enumerable.Range(1_000_000, shared)).Append(last);
        return (Items(Side(0, -1)), Items(Side(100_000, -2)));
    }

    // Short differing stretches, too long to cross two at once, between shared runs that grow towards the
    // end. Each search wins nearly everywhere from the end, and a design that searches the same items
    // again each time costs several times more here.
    private static (Counted[], Counted[]) GrowingRuns(int blocks, int gap, int runStart) {
        List<int> a = [], b = [];
        var next = 1;
        for (var block = 0; block < blocks; block++) {
            for (var i = 0; i < gap; i++) {
                a.Add(next++);
                b.Add(-next++);
            }
            for (var i = 0; i < runStart + block; i++) {
                a.Add(next);
                b.Add(next++);
            }
        }
        return (Items(a), Items(b));
    }

    // Two sequences over a few values with no relation, which is as bad as a real script gets.
    private static (Counted[], Counted[]) Unrelated(int length, int values) =>
        (Items(Enumerable.Range(0, length).Select(i => (i * 7 + i / 3) % values)),
            Items(Enumerable.Range(0, length).Select(i => (i * 11 + i / 5 + 3) % values)));

    private static void RequireCost(Counted[] a, Counted[] b, int budget, bool cutShort) {
        Counted.Comparisons.Should().BeLessThanOrEqualTo(2L * (a.Length + b.Length) * budget);
        cutShort.Should().BeTrue();
    }

    private static Hunk[] RunMyers(Counted[] a, Counted[] b, int budget, out bool cutShort) {
        var hunks = new Hunk[MyersDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[MyersDiff.WorkspaceLength(a.Length, b.Length)];
        Counted.Comparisons = 0;
        var count = MyersDiff.Diff<Counted>(a, b, hunks, workspace, budget, out cutShort);
        return hunks[..count];
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void MyersCostStaysNearTheBudgetTimesTheLength(int budget) {
        // The first four are cut; the last two shapes are the ones that tell designs apart (see their comments).
        var (a, b) = EverySecondChanged(20_000);
        var hunks = RunMyers(a, b, budget, out var cutShort);
        RequireCost(a, b, budget, cutShort);
        Edits(hunks).Should().Be(20_000); // as short as it gets: 10,000 items replaced

        (a, b) = UnrelatedFrontSharedBack(5_000, 5_000);
        hunks = RunMyers(a, b, budget, out cutShort);
        RequireCost(a, b, budget, cutShort);
        Edits(hunks).Should().Be(10_002); // the shared items are found from the end

        // Each differing stretch is as long as a search crosses once, in items of each side.
        var gap = (budget + 1) / 2 / 2;
        (a, b) = GrowingRuns(400, gap, 50);
        hunks = RunMyers(a, b, budget, out cutShort);
        RequireCost(a, b, budget, cutShort);
        Edits(hunks).Should().Be(2 * gap * 400); // each stretch is replaced, nothing else

        (a, b) = Unrelated(5_000, 26);
        RunMyers(a, b, budget, out cutShort);
        RequireCost(a, b, budget, cutShort);
    }

    [Fact]
    public void MyersScriptsStayValidAtTheCostTestSizes() {
        var (a, b) = GrowingRuns(400, 2, 50);
        var hunks = RunMyers(a, b, 10, out _);
        RequireValid(a.Select(c => c.GetHashCode()).ToArray(), b.Select(c => c.GetHashCode()).ToArray(), hunks);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void RefineCostStaysNearTheBudgetTimesTheLengthOfALongChangedLine(int budget) {
        var (a, b) = Unrelated(5_000, 26);
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)];
        Counted.Comparisons = 0;
        var count = GroupedDiff.Refine<Counted>(a, [new Segment(0, a.Length)], b, [new Segment(0, b.Length)],
            [new Hunk(new Segment(0, 1), new Segment(0, 1))], hunks, workspace, budget, out var cutShort);
        RequireCost(a, b, budget, cutShort);
        RequireValid(a.Select(c => c.GetHashCode()).ToArray(), b.Select(c => c.GetHashCode()).ToArray(), hunks.AsSpan(0, count));
    }
}
