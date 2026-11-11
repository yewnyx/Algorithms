using FluentAssertions;
using Yewnyx.Algorithms.Diffing;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Test;

public class GroupedDiffTest {
    // Lines with their line breaks, which tile the text.
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

    private static int[] Classes(string a, Segment[] groupsA, string b, Segment[] groupsB, out int[] classesB) {
        var classesA = new int[groupsA.Length];
        classesB = new int[groupsB.Length];
        GroupClasses.Assign(a.AsSpan(), groupsA, b.AsSpan(), groupsB, classesA, classesB,
            new int[GroupClasses.WorkspaceLength(groupsA.Length, groupsB.Length)]);
        return classesA;
    }

    // The three steps, with Myers over the groups' classes. Item hunks read "a[offset] items -> b[offset] items";
    // group hunks read "offset+size -> offset+size".
    private static string[] Diff(string a, Segment[] groupsA, string b, Segment[] groupsB, out string[] groupHunks) {
        var classesA = Classes(a, groupsA, b, groupsB, out var classesB);
        Hunk[]? found = null;
        int[]? workspace = null;
        var groupCount = MyersDiff.Diff<int>(classesA, classesB, ref found, ref workspace);
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var count = GroupedDiff.Refine(a.AsSpan(), groupsA, b.AsSpan(), groupsB, found.AsSpan(0, groupCount), hunks,
            new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)]);

        Check(a.ToCharArray(), groupsA, b.ToCharArray(), groupsB, found.AsSpan(0, groupCount), hunks.AsSpan(0, count));
        groupHunks = found[..groupCount].Select(h => $"{h.Original.Offset}+{h.Original.Size} -> {h.Revised.Offset}+{h.Revised.Size}").ToArray();
        return hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}").ToArray();
    }

    private static string[] Diff(string a, string b, out string[] groupHunks) => Diff(a, Lines(a), b, Lines(b), out groupHunks);

    private static string Side(string name, string text, Segment segment) =>
        segment.Size == 0 ? $"{name}[{segment.Offset}]" : $"{name}[{segment.Offset}] {text.Substring(segment.Offset, segment.Size).Replace("\n", "\\n")}";

    // The item hunks rebuild the revised items through a Patch, and each lies inside one group hunk's items.
    private static void Check<T>(T[] a, Segment[] groupsA, T[] b, Segment[] groupsB, ReadOnlySpan<Hunk> groupHunks, ReadOnlySpan<Hunk> hunks)
        where T : IEquatable<T> {
        var patch = new Patch<T>();
        patch.Set(a, b, hunks);
        var rebuilt = new T[b.Length];
        patch.TryApply(a, rebuilt).Should().BeTrue();
        rebuilt.Should().Equal(b);

        foreach (var (original, revised) in hunks) {
            var inside = false;
            foreach (var (groupsOriginal, groupsRevised) in groupHunks) {
                int aStart = Start(groupsA, groupsOriginal.Offset, a.Length), aEnd = Start(groupsA, groupsOriginal.Offset + groupsOriginal.Size, a.Length);
                int bStart = Start(groupsB, groupsRevised.Offset, b.Length), bEnd = Start(groupsB, groupsRevised.Offset + groupsRevised.Size, b.Length);
                inside |= aStart <= original.Offset && original.Offset + original.Size <= aEnd &&
                          bStart <= revised.Offset && revised.Offset + revised.Size <= bEnd;
            }
            inside.Should().BeTrue("unchanged groups give no item hunks");
        }
    }

    private static int Start(Segment[] groups, int group, int length) => group < groups.Length ? groups[group].Offset : length;

    [Fact]
    public void EqualGroupsShareAClass() {
        const string a = "a\nb\na\n", b = "b\nc\n\n\n";
        Classes(a, Lines(a), b, Lines(b), out var classesB).Should().Equal(0, 1, 0);
        classesB.Should().Equal(1, 2, 3, 3);

        // Empty groups are all equal, wherever they sit.
        Classes("xy", [new Segment(0, 0), new Segment(2, 0)], "z", [new Segment(1, 0)], out classesB).Should().Equal(0, 0);
        classesB.Should().Equal(0);
    }

    [Fact]
    public void EachItemCanBeItsOwnGroup() {
        var (classesA, classesB) = (new int[4], new int[3]);
        var count = GroupClasses.Assign("abca".AsSpan(), "cab".AsSpan(), classesA, classesB, new int[GroupClasses.WorkspaceLength(4, 3)]);

        count.Should().Be(3);
        classesA.Should().Equal(0, 1, 2, 0);
        classesB.Should().Equal(2, 0, 1);
    }

    private readonly struct Clashing(char value) : IEquatable<Clashing> {
        private readonly char _value = value;
        public bool Equals(Clashing other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Clashing other && Equals(other);
        public override int GetHashCode() => 0;
    }

    [Fact]
    public void HashesThatClashGiveTheSameClasses() {
        const string a = "ab\ncd\nab\n\n", b = "cd\nab\ncdx\n";
        var expected = Classes(a, Lines(a), b, Lines(b), out var expectedB);

        var (classesA, classesB) = (new int[expected.Length], new int[expectedB.Length]);
        GroupClasses.Assign<Clashing>(a.Select(c => new Clashing(c)).ToArray(), Lines(a), b.Select(c => new Clashing(c)).ToArray(), Lines(b),
            classesA, classesB, new int[GroupClasses.WorkspaceLength(classesA.Length, classesB.Length)]);
        classesA.Should().Equal(expected);
        classesB.Should().Equal(expectedB);
    }

    [Fact]
    public void ALineEditedInPlaceShowsWhichCharactersChanged() {
        Diff("one\ntwo\nthree\n", "one\ntwo!\nthree\n", out var lines).Should().Equal("a[7] -> b[7] !");
        lines.Should().Equal("1+1 -> 1+1");
    }

    [Fact]
    public void ARunOfAnotherLengthIsOneHunk() {
        Diff("a\nb\nc\n", "a\nx\ny\nc\n", out var lines).Should().Equal("a[2] b\\n -> b[2] x\\ny\\n");
        lines.Should().Equal("1+1 -> 1+2");
    }

    [Fact]
    public void HunksFromNeighbouringPairsJoin() {
        // Words without separators: the last item of one pair and the first of the next both change.
        Segment[] words = [new(0, 2), new(2, 2)];
        Diff("abcd", words, "aXYd", words, out var groups).Should().Equal("a[1] bc -> b[1] XY");
        groups.Should().Equal("0+2 -> 0+2");
    }

    [Fact]
    public void LevelsCompose() {
        // Paragraphs of lines of characters: paragraphs are groups over the lines' classes.
        const string a = "x\ny\n\nz\n", b = "x\ny!\n\nz\n";
        var (linesA, linesB) = (Lines(a), Lines(b));
        var lineClassesA = Classes(a, linesA, b, linesB, out var lineClassesB);
        Segment[] paragraphs = [new(0, 3), new(3, 1)];

        var (paragraphClassesA, paragraphClassesB) = (new int[2], new int[2]);
        GroupClasses.Assign<int>(lineClassesA, paragraphs, lineClassesB, paragraphs, paragraphClassesA, paragraphClassesB,
            new int[GroupClasses.WorkspaceLength(2, 2)]);
        Hunk[]? paragraphHunks = null;
        int[]? workspace = null;
        var paragraphCount = MyersDiff.Diff<int>(paragraphClassesA, paragraphClassesB, ref paragraphHunks, ref workspace);

        var lineHunks = new Hunk[GroupedDiff.MaxHunks(linesA.Length, linesB.Length)];
        var lineCount = GroupedDiff.Refine<int>(lineClassesA, paragraphs, lineClassesB, paragraphs, paragraphHunks.AsSpan(0, paragraphCount),
            lineHunks, new int[GroupedDiff.WorkspaceLength(linesA.Length, linesB.Length)]);
        lineHunks[..lineCount].Should().Equal(new Hunk((1, 1), (1, 1)));

        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var count = GroupedDiff.Refine(a.AsSpan(), linesA, b.AsSpan(), linesB, lineHunks.AsSpan(0, lineCount), hunks,
            new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)]);
        hunks[..count].Should().Equal(new Hunk((3, 0), (3, 1)));
        Check(a.ToCharArray(), linesA, b.ToCharArray(), linesB, lineHunks.AsSpan(0, lineCount), hunks.AsSpan(0, count));
    }

    [Fact]
    public void GroupsMustTileTheirItems() {
        var hunks = new Hunk[3];
        var workspace = new int[GroupedDiff.WorkspaceLength(2, 2)];
        Segment[] whole = [new(0, 2)];
        foreach (var groups in new[] { new Segment[] { new(0, 1) }, [new(1, 1)], [new(0, 1), new(0, 1)], [new(0, 2), new(1, 1)], [new(0, 3)] }) {
            var act = () => GroupedDiff.Refine("ab".AsSpan(), groups, "ab".AsSpan(), whole, default, hunks, workspace);
            act.Should().Throw<ArgumentException>().WithParameterName("originalGroups");
            act = () => GroupedDiff.Refine("ab".AsSpan(), whole, "ab".AsSpan(), groups, default, hunks, workspace);
            act.Should().Throw<ArgumentException>().WithParameterName("revisedGroups");
        }
    }

    [Fact]
    public void GroupHunksMustFitTheGroups() {
        var hunks = new Hunk[3];
        var workspace = new int[GroupedDiff.WorkspaceLength(2, 2)];
        Segment[] two = [new(0, 1), new(1, 1)];
        foreach (var groupHunks in new[] { new Hunk[] { new((0, 3), (0, 3)) }, [new((1, 1), (0, 1))], [new((1, 1), (1, 1)), new((0, 1), (0, 1))] }) {
            var act = () => GroupedDiff.Refine("ab".AsSpan(), two, "ab".AsSpan(), two, groupHunks, hunks, workspace);
            act.Should().Throw<ArgumentException>().WithParameterName("groupHunks");
        }

        // Out of step, though the groups after it pair up.
        Segment[] three = [new(0, 1), new(1, 1), new(2, 1)];
        var outOfStep = () => GroupedDiff.Refine("abc".AsSpan(), three, "abc".AsSpan(), three, [new Hunk((1, 1), (0, 2))], hunks, workspace);
        outOfStep.Should().Throw<ArgumentException>().WithParameterName("groupHunks");
        // Groups left over on one side after the last hunk.
        var leftOver = () => GroupedDiff.Refine("ab".AsSpan(), two, "abc".AsSpan(), three, default, new Hunk[3], new int[GroupedDiff.WorkspaceLength(2, 3)]);
        leftOver.Should().Throw<ArgumentException>().WithParameterName("groupHunks");
        // Groups that would match, but can't, being different sizes.
        Segment[] twoTwo = [new(0, 2), new(2, 2)], threeTwo = [new(0, 3), new(3, 2)];
        var sizes = () => GroupedDiff.Refine("abcd".AsSpan(), twoTwo, "abcde".AsSpan(), threeTwo, default, new Hunk[5], new int[GroupedDiff.WorkspaceLength(4, 5)]);
        sizes.Should().Throw<ArgumentException>().WithParameterName("groupHunks");
    }

    [Fact]
    public void EmptyGroupsChangeNoItems() {
        // Empty groups added, or removed between kept ones: the groups differ, the items don't.
        Diff("x", [new(0, 1)], "x", [new(0, 1), new(1, 0), new(1, 0)], out var groups).Should().BeEmpty();
        groups.Should().Equal("1+0 -> 1+2");
        Diff("xy", [new(0, 1), new(1, 0), new(1, 1)], "xy", [new(0, 1), new(1, 1)], out groups).Should().BeEmpty();
        groups.Should().Equal("1+1 -> 1+0");
    }

    [Theory]
    [InlineData("aba", "baa")]
    [InlineData("baa", "aaba")]
    public void APairIsDiffedAsMyersDiffWould(string a, string b) {
        // One group each, so the one pair is the whole diff; its edits sit late, as Myers' do.
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff(a.AsSpan(), b.AsSpan(), ref hunks, ref workspace);
        var myers = hunks[..count].Select(h => $"{Side("a", a, h.Original)} -> {Side("b", b, h.Revised)}");

        Diff(a, [new(0, a.Length)], b, [new(0, b.Length)], out _).Should().Equal(myers);
    }

    [Fact]
    public void AssignChecksItsInputs() {
        Segment[] one = [new(0, 2)], outside = [new(1, 2)];
        var workspace = new int[GroupClasses.WorkspaceLength(1, 1)];
        var act = () => GroupClasses.Assign("ab".AsSpan(), outside, "cd".AsSpan(), one, new int[1], new int[1], workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("originalGroups");
        act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), outside, new int[1], new int[1], workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("revisedGroups");

        // Int items beside the revised classes and the workspace, and the outputs over each other.
        var memory = new int[4 + GroupClasses.WorkspaceLength(1, 1)];
        act = () => GroupClasses.Assign<int>(memory.AsSpan(0, 2), one, memory.AsSpan(2, 2), one, new int[1], memory.AsSpan(3, 1), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("revisedClasses");
        act = () => GroupClasses.Assign<int>(memory.AsSpan(0, 2), one, memory.AsSpan(2, 2), one, new int[1], new int[1], memory.AsSpan(3));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        var outputs = new int[2 + GroupClasses.WorkspaceLength(1, 1)];
        act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), one, outputs.AsSpan(0, 1), outputs.AsSpan(0, 1), workspace);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), one, outputs.AsSpan(0, 1), new int[1], outputs.AsSpan(0));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), one, new int[1], outputs.AsSpan(1, 1), outputs.AsSpan(1));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
    }

    [Fact]
    public void BuffersMayNotShareMemoryWithTheInputs() {
        // At a second level the items are ints, like the workspace and the classes.
        Segment[] one = [new(0, 2)];
        var memory = new int[4 + Math.Max(GroupedDiff.WorkspaceLength(2, 2), GroupClasses.WorkspaceLength(1, 1))];
        var act = () => GroupedDiff.Refine<int>(memory.AsSpan(0, 2), one, memory.AsSpan(2, 2), one, default, new Hunk[3], memory.AsSpan(3));
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => GroupClasses.Assign<int>(memory.AsSpan(0, 2), one, memory.AsSpan(2, 2), one, memory.AsSpan(1, 1), new int[1],
            new int[GroupClasses.WorkspaceLength(1, 1)]);
        act.Should().Throw<ArgumentException>().WithParameterName("originalClasses");

        var hunkMemory = new Hunk[4];
        hunkMemory[0] = new Hunk((0, 1), (0, 1));
        act = () => GroupedDiff.Refine("ab".AsSpan(), one, "cd".AsSpan(), one, hunkMemory.AsSpan(0, 1), hunkMemory.AsSpan(0, 3),
            new int[GroupedDiff.WorkspaceLength(2, 2)]);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void ShortBuffersAreRejected() {
        Segment[] one = [new(0, 2)];
        var act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), one, new int[0], new int[1], new int[8]);
        act.Should().Throw<ArgumentException>().WithParameterName("originalClasses");
        act = () => GroupClasses.Assign("ab".AsSpan(), one, "cd".AsSpan(), one, new int[1], new int[1], new int[1]);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => GroupedDiff.Refine("ab".AsSpan(), one, "cd".AsSpan(), one, default, new Hunk[3], new int[1]);
        act.Should().Throw<ArgumentException>().WithParameterName("workspace");
        act = () => GroupedDiff.Refine("ab".AsSpan(), one, "cd".AsSpan(), one, default, new Hunk[1], new int[GroupedDiff.WorkspaceLength(2, 2)]);
        act.Should().Throw<ArgumentException>().WithParameterName("hunks");
    }

    [Fact]
    public void AWarmDiffDoesntAllocate() {
        const string a = "one\ntwo\nthree\nfour\n", b = "one\ntoo\nthree\nfive\nsix\n";
        var (linesA, linesB) = (Lines(a), Lines(b));
        var (classesA, classesB) = (new int[linesA.Length], new int[linesB.Length]);
        var classWorkspace = new int[GroupClasses.WorkspaceLength(linesA.Length, linesB.Length)];
        Hunk[]? groupHunks = null;
        int[]? diffWorkspace = null;
        var hunks = new Hunk[GroupedDiff.MaxHunks(a.Length, b.Length)];
        var workspace = new int[GroupedDiff.WorkspaceLength(a.Length, b.Length)];
        int Run() {
            GroupClasses.Assign(a.AsSpan(), linesA, b.AsSpan(), linesB, classesA, classesB, classWorkspace);
            var groupCount = MyersDiff.Diff<int>(classesA, classesB, ref groupHunks, ref diffWorkspace);
            return GroupedDiff.Refine(a.AsSpan(), linesA, b.AsSpan(), linesB, groupHunks.AsSpan(0, groupCount), hunks, workspace);
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = Run();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        count.Should().BePositive();
    }
}
