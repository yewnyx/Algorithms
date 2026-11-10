using FluentAssertions;
using Yewnyx.Algorithms.Diffing;

namespace Yewnyx.Algorithms.Test;

public class PatchTest {
    private static Patch<char> Make(string original, string revised) {
        Hunk[]? hunks = null;
        int[]? workspace = null;
        var count = MyersDiff.Diff(original.AsSpan(), revised.AsSpan(), ref hunks, ref workspace);
        var patch = new Patch<char>();
        patch.Set(original.AsSpan(), revised.AsSpan(), hunks.AsSpan(0, count));
        return patch;
    }

    [Fact]
    public void KeepsOnlyTheTouchedItems() {
        var patch = Make("the quick brown fox", "the quack brown box");
        patch.Deleted.ToString().Should().Be("if");
        patch.Inserted.ToString().Should().Be("ab");
        (patch.OriginalLength, patch.RevisedLength).Should().Be((19, 19));
    }

    [Fact]
    public void ApplyAndRevertRoundTrip() {
        var patch = Make("the quick brown fox", "the quack brown box");

        var revised = new char[patch.RevisedLength];
        patch.TryApply("the quick brown fox".AsSpan(), revised).Should().BeTrue();
        new string(revised).Should().Be("the quack brown box");

        var original = new char[patch.OriginalLength];
        patch.TryRevert(revised, original).Should().BeTrue();
        new string(original).Should().Be("the quick brown fox");
    }

    [Fact]
    public void AppliesOnlyWhereTheDeletedItemsMatch() {
        var patch = Make("the quick brown fox", "the quack brown box");
        var result = new char[patch.RevisedLength];

        // Matching items elsewhere may differ: only what the patch deletes is checked.
        patch.TryApply("THE quick BROWN fox".AsSpan(), result).Should().BeTrue();
        new string(result).Should().Be("THE quack BROWN box");

        // The deleted "i" is now "e", or the length is wrong: refused, nothing written.
        result.AsSpan().Fill('-');
        patch.TryApply("the queck brown fox".AsSpan(), result).Should().BeFalse();
        patch.TryApply("the quick brown fox!".AsSpan(), result).Should().BeFalse();
        new string(result).Should().Be(new string('-', 19));
    }

    [Fact]
    public void FindsMoves() {
        // "b" moves from the middle to the end.
        Make("abcde", "acdeb").FindMoves().ToArray().Should().Equal(new Move(1, 4));
        // "c" moves to the front.
        Make("abc", "cab").FindMoves().ToArray().Should().Equal(new Move(2, 0));
        // Nothing deleted is inserted again.
        Make("abc", "bcd").FindMoves().ToArray().Should().BeEmpty();
    }

    [Fact]
    public void MovesPairInOrder() {
        // Two "x" leave and two arrive: the first deleted pairs with the first inserted.
        Make("xaxb", "abxx").FindMoves().ToArray().Should().Equal(new Move(0, 2), new Move(2, 3));
    }

    [Fact]
    public void SetReplacesTheOldPatch() {
        var patch = Make("the quick brown fox", "the quack brown box");
        patch.Set("ab".AsSpan(), "b".AsSpan(), [new Hunk((0, 1), (0, 0))]);
        patch.Deleted.ToString().Should().Be("a");
        patch.Inserted.ToString().Should().BeEmpty();
        patch.Hunks.Length.Should().Be(1);
    }
}
