using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// A run of items that moved: <c>original[Original..Original + Length)</c> was deleted there and
/// inserted as the equal run <c>revised[Revised..Revised + Length)</c>.
/// </summary>
public readonly struct BlockMove(int original, int revised, int length) : IEquatable<BlockMove> {
    public readonly int Original = original;
    public readonly int Revised = revised;
    public readonly int Length = length;

    public bool Equals(BlockMove other) => Original == other.Original && Revised == other.Revised && Length == other.Length;
    public override bool Equals(object? obj) => obj is BlockMove other && Equals(other);
    public static bool operator ==(BlockMove left, BlockMove right) => left.Equals(right);
    public static bool operator !=(BlockMove left, BlockMove right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(Original, Revised, Length);

    public void Deconstruct(out int original, out int revised, out int length) => (original, revised, length) = (Original, Revised, Length);
}

/// <summary>
/// Heckel's diff (1978), which finds moved blocks: the hunks that turn one sequence into another, as
/// <see cref="MyersDiff"/>'s do, and the runs among them that moved. Items that occur once on each side
/// anchor the matches, which then grow over equal neighbours into blocks. The heaviest set of blocks
/// still in order stays in place, and the other blocks moved. Between the blocks that stay, Myers' diff
/// matches what's left. No allocation, and results depend only on the inputs.
/// </summary>
/// <remarks>
/// <para>
/// A reordered list of distinct items costs O(n log n). Myers' diff is quadratic on one, which is why
/// this isn't <see cref="Patch{T}.FindMoves"/> over a Myers patch: that also pairs single items after
/// the fact, where this finds whole blocks as it diffs.
/// </para>
/// <para>
/// A block made only of items that occur more than once has no anchor, so it shows as a deletion and an
/// insertion; FindMoves can still pair its items one by one. The script is as short as Myers' only
/// between the blocks that stay, not overall: a unique item that moves across repeated ones stays put
/// and the repeats are rewritten, so [100, 1, 1, 1, 1, 1] against [1, 1, 1, 1, 1, 100] is ten edits and
/// no move, where Myers' is two.
/// </para>
/// <para>
/// Between the blocks that stay, the cost is Myers' on the items that can still match: moved items, and
/// items with no equal item on the other side, are left out. Speed depends on the items'
/// <c>GetHashCode</c> varying in its low bits, as for <see cref="HistogramDiff"/> (results don't change).
/// </para>
/// </remarks>
public static class HeckelDiff {
    // Links that aren't to an item: not linked yet (-1), in a moved block, or with no equal item on the
    // other side. Only the first can still match.
    private const int Unlinked = -1, Moved = -2, Unmatchable = -3;

    /// <inheritdoc cref="MyersDiff.MaxHunks"/>
    public static int MaxHunks(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength) + 1;

    /// <summary>Moves needed for any diff of these lengths: each moves at least one item.</summary>
    public static int MaxMoves(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength);

    /// <summary>Workspace needed for a diff of these lengths.</summary>
    public static int WorkspaceLength(int originalLength, int revisedLength) {
        int n = originalLength, m = revisedLength, t = n + m, k = Math.Min(n, m);
        var classes = _bucketCount(t) + 3 * t;
        var keeping = 2 * (m + 1);
        var hunks = 2 * t + MyersDiff.WorkspaceLength(n, m) + 4 * (k + 1);
        return 2 * t + 5 * k + Math.Max(classes, Math.Max(keeping, hunks));
    }

    /// <summary>
    /// Writes the hunks that turn <paramref name="original"/> into <paramref name="revised"/>, in order,
    /// and returns how many; then the blocks among them that moved, in original order, setting
    /// <paramref name="moveCount"/>. Everything outside the hunks matches, in order, and each pure
    /// insertion or deletion sits as late as it can, except that one holding a moved block stays where
    /// the block is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each move's original run lies inside one hunk's <see cref="Hunk.Original"/> and its revised run
    /// inside one hunk's <see cref="Hunk.Revised"/>. Moves are maximal: no two are next to each other on
    /// both sides. The blocks that stay are those holding the most items; of equally heavy choices, the
    /// blocks earlier in the original stay, so of two equal blocks that swap places, the second moves.
    /// </para>
    /// <para>
    /// <paramref name="hunks"/> needs <see cref="MaxHunks"/> elements, <paramref name="moves"/>
    /// <see cref="MaxMoves"/> and <paramref name="workspace"/> <see cref="WorkspaceLength"/>. The inputs
    /// may overlap each other; the buffers must not share memory with them.
    /// </para>
    /// </remarks>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, Span<Hunk> hunks, Span<BlockMove> moves,
        Span<int> workspace, out int moveCount) where T : IEquatable<T> {
        int n = original.Length, m = revised.Length, t = n + m, k = Math.Min(n, m);
        var maxHunks = MaxHunks(n, m);
        if (hunks.Length < maxHunks) { throw new ArgumentException($"Needs at least {maxHunks} elements.", nameof(hunks)); }
        if (moves.Length < k) { throw new ArgumentException($"Needs at least {k} elements.", nameof(moves)); }
        var workspaceLength = WorkspaceLength(n, m);
        if (workspace.Length < workspaceLength) {
            throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace));
        }
        Hunks.RequireApart(original, revised, hunks, workspace);
        Hunks.RequireApart(original, revised, moves);

        // Each item's class (the first item equal to it, counting the original first), each item's
        // link to its match (or -1, or Moved), and the blocks: original start, revised start, length,
        // chain weight (-1 once kept) and chain predecessor.
        var classA = workspace[..n];
        var classB = workspace.Slice(n, m);
        var linkA = workspace.Slice(t, n);
        var linkB = workspace.Slice(t + n, m);
        var blocks = workspace.Slice(2 * t, 5 * k);
        var scratch = workspace[(2 * t + 5 * k)..];

        _classify(original, revised, workspace[..t], scratch);
        _link(classA, classB, scratch.Slice(_bucketCount(t) + t, t), scratch.Slice(_bucketCount(t) + 2 * t, t), linkA, linkB);
        var blockCount = _findBlocks(linkA, blocks, k);
        _keep(blocks, k, blockCount, scratch, m);

        moveCount = 0;
        for (var b = 0; b < blockCount; b++) {
            if (blocks[3 * k + b] == -1) { continue; }
            int from = blocks[b], to = blocks[k + b], length = blocks[2 * k + b];
            moves[moveCount++] = new BlockMove(from, to, length);
            linkA.Slice(from, length).Fill(Moved);
            linkB.Slice(to, length).Fill(Moved);
        }

        var count = _hunks(classA, classB, linkA, linkB, blocks, k, blockCount, hunks, scratch);

        // A moved item now equals nothing, so sliding can't take one out of its hunk.
        for (var o = 0; o < n; o++) {
            if (linkA[o] == Moved) { classA[o] = t + o; }
        }
        for (var r = 0; r < m; r++) {
            if (linkB[r] == Moved) { classB[r] = t + n + r; }
        }
        return Hunks.SlideLate<int>(classA, classB, hunks, count);
    }

    /// <inheritdoc cref="Diff{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, Span{Hunk}, Span{BlockMove}, Span{int}, out int)"/>
    /// <remarks>Null or undersized arrays are replaced.</remarks>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, [NotNull] ref Hunk[]? hunks,
        [NotNull] ref BlockMove[]? moves, [NotNull] ref int[]? workspace, out int moveCount) where T : IEquatable<T> {
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks is null || hunks.Length < maxHunks) { hunks = new Hunk[maxHunks]; }
        var maxMoves = MaxMoves(original.Length, revised.Length);
        if (moves is null || moves.Length < maxMoves) { moves = new BlockMove[maxMoves]; }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace is null || workspace.Length < workspaceLength) { workspace = new int[workspaceLength]; }
        return Diff(original, revised, hunks.AsSpan(), moves.AsSpan(), workspace.AsSpan(), out moveCount);
    }

    private static int _bucketCount(int n) {
        var buckets = 1;
        while (buckets < 2 * n) { buckets <<= 1; }
        return buckets;
    }

    private static int _hash<T>(T item) => item is null ? 0 : item.GetHashCode();

    // Gives each item the index of the first item equal to it, the original's items counting first,
    // and counts each class on each side. Equal hashes are confirmed with Equals.
    private static void _classify<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<int> classes, Span<int> scratch)
        where T : IEquatable<T> {
        var t = classes.Length;
        var buckets = scratch[.._bucketCount(t)];
        var chain = scratch.Slice(buckets.Length, t);
        var countA = scratch.Slice(buckets.Length + t, t);
        var countB = scratch.Slice(buckets.Length + 2 * t, t);
        buckets.Fill(-1);
        countA.Clear();
        countB.Clear();
        var mask = buckets.Length - 1;
        for (var e = 0; e < t; e++) {
            var item = e < a.Length ? a[e] : b[e - a.Length];
            ref var bucket = ref buckets[_hash(item) & mask];
            var found = bucket;
            while (found != -1 && !Hunks.Equal(found < a.Length ? a[found] : b[found - a.Length], item)) { found = chain[found]; }
            if (found == -1) {
                found = e;
                chain[e] = bucket;
                bucket = e;
            }
            classes[e] = found;
            if (e < a.Length) { countA[found]++; } else { countB[found]++; }
        }
    }

    // Links items that occur once on each side, then grows each link forwards and backwards over equal
    // neighbours not linked yet. Items with no equal item on the other side are marked unmatchable.
    private static void _link(ReadOnlySpan<int> classA, ReadOnlySpan<int> classB, ReadOnlySpan<int> countA,
        ReadOnlySpan<int> countB, Span<int> linkA, Span<int> linkB) {
        int n = classA.Length, m = classB.Length;
        for (var o = 0; o < n; o++) { linkA[o] = countB[classA[o]] == 0 ? Unmatchable : Unlinked; }
        for (var r = 0; r < m; r++) { linkB[r] = countA[classB[r]] == 0 ? Unmatchable : Unlinked; }
        for (var r = 0; r < m; r++) {
            // A class seen in the original is named after its first original item, here its only one.
            var c = classB[r];
            if (c < n && countA[c] == 1 && countB[c] == 1) { (linkA[c], linkB[r]) = (r, c); }
        }
        for (var r = 0; r + 1 < m; r++) {
            var o = linkB[r];
            if (o >= 0 && o + 1 < n && linkB[r + 1] < 0 && linkA[o + 1] < 0 && classB[r + 1] == classA[o + 1]) {
                (linkA[o + 1], linkB[r + 1]) = (r + 1, o + 1);
            }
        }
        for (var r = m - 1; r > 0; r--) {
            var o = linkB[r];
            if (o > 0 && linkB[r - 1] < 0 && linkA[o - 1] < 0 && classB[r - 1] == classA[o - 1]) {
                (linkA[o - 1], linkB[r - 1]) = (r - 1, o - 1);
            }
        }
    }

    // Joins links that are consecutive on both sides into blocks, in original order.
    private static int _findBlocks(ReadOnlySpan<int> linkA, Span<int> blocks, int k) {
        var count = 0;
        for (var o = 0; o < linkA.Length; o++) {
            var r = linkA[o];
            if (r < 0) { continue; }
            if (count > 0 && blocks[count - 1] + blocks[2 * k + count - 1] == o && blocks[k + count - 1] + blocks[2 * k + count - 1] == r) {
                blocks[2 * k + count - 1]++;
                continue;
            }
            (blocks[count], blocks[k + count], blocks[2 * k + count]) = (o, r, 1);
            count++;
        }
        return count;
    }

    // Keeps the blocks in order on both sides that hold the most items: a longest increasing subsequence
    // weighted by length, with a Fenwick tree over revised starts. Kept blocks' weights become -1.
    private static void _keep(Span<int> blocks, int k, int count, Span<int> scratch, int m) {
        var weights = blocks.Slice(3 * k, k);
        var previous = blocks.Slice(4 * k, k);
        var treeWeight = scratch[..(m + 1)];
        var treeBlock = scratch.Slice(m + 1, m + 1);
        treeWeight.Clear();
        treeBlock.Fill(-1);
        for (var b = 0; b < count; b++) {
            int start = blocks[k + b], bestWeight = 0, best = -1;
            for (var i = start; i > 0; i -= i & -i) {
                if (_heavier(treeWeight[i], treeBlock[i], bestWeight, best)) { (bestWeight, best) = (treeWeight[i], treeBlock[i]); }
            }
            (weights[b], previous[b]) = (bestWeight + blocks[2 * k + b], best);
            for (var i = start + 1; i <= m; i += i & -i) {
                if (_heavier(weights[b], b, treeWeight[i], treeBlock[i])) { (treeWeight[i], treeBlock[i]) = (weights[b], b); }
            }
        }

        var last = -1;
        for (var b = 0; b < count; b++) {
            if (last < 0 || weights[b] > weights[last]) { last = b; }
        }
        while (last >= 0) {
            var before = previous[last];
            weights[last] = -1;
            last = before;
        }
    }

    // Heavier wins, and of equal weights the block earlier in the original.
    private static bool _heavier(int weight, int block, int otherWeight, int otherBlock) =>
        block >= 0 && (weight > otherWeight || (weight == otherWeight && (otherBlock < 0 || block < otherBlock)));

    // Writes the hunks: the runs between matches, the matches being the kept blocks and what Myers'
    // diff matches between them. Moved items, and items with no equal item on the other side, are left
    // out of that diff, since they can't match: so a reordered list, or one replaced wholesale, never
    // reaches it.
    private static int _hunks(ReadOnlySpan<int> classA, ReadOnlySpan<int> classB, ReadOnlySpan<int> linkA,
        ReadOnlySpan<int> linkB, ReadOnlySpan<int> blocks, int k, int blockCount, Span<Hunk> hunks, Span<int> scratch) {
        int n = classA.Length, m = classB.Length, t = n + m;
        var gap = new Gap {
            ClassA = scratch[..n], ClassB = scratch.Slice(n, m),
            AtA = scratch.Slice(t, n), AtB = scratch.Slice(t + n, m),
            Workspace = scratch.Slice(2 * t, MyersDiff.WorkspaceLength(n, m)),
            Found = MemoryMarshal.Cast<int, Hunk>(scratch.Slice(2 * t + MyersDiff.WorkspaceLength(n, m), 4 * (k + 1))),
        };

        var emitter = new Emitter();
        for (var b = 0; b < blockCount; b++) {
            if (blocks[3 * k + b] != -1) { continue; }
            int from = blocks[b], to = blocks[k + b], length = blocks[2 * k + b];
            gap.Match(classA, classB, linkA, linkB, emitter.LastA, from, emitter.LastB, to, hunks, ref emitter);
            emitter.Match(hunks, from, to);
            (emitter.LastA, emitter.LastB) = (from + length, to + length);
        }
        gap.Match(classA, classB, linkA, linkB, emitter.LastA, n, emitter.LastB, m, hunks, ref emitter);
        if (emitter.LastA < n || emitter.LastB < m) {
            Hunks.Emit(hunks, ref emitter.Count, emitter.LastA, n - emitter.LastA, emitter.LastB, m - emitter.LastB);
        }
        return emitter.Count;
    }

    // Writes a hunk for whatever lies between the last match and the next.
    private struct Emitter {
        public int Count, LastA, LastB;

        public void Match(Span<Hunk> hunks, int a, int b) {
            if (a > LastA || b > LastB) { Hunks.Emit(hunks, ref Count, LastA, a - LastA, LastB, b - LastB); }
            (LastA, LastB) = (a + 1, b + 1);
        }
    }

    private ref struct Gap {
        public Span<int> ClassA, ClassB, AtA, AtB, Workspace;
        public Span<Hunk> Found; // Myers' hunks over the items left in.

        // Matches a[aLo..aHi) against b[bLo..bHi) with Myers' diff, leaving out the items that can't match.
        public void Match(ReadOnlySpan<int> classA, ReadOnlySpan<int> classB, ReadOnlySpan<int> linkA, ReadOnlySpan<int> linkB,
            int aLo, int aHi, int bLo, int bHi, Span<Hunk> output, ref Emitter emitter) {
            int na = 0, nb = 0;
            for (var o = aLo; o < aHi; o++) {
                if (linkA[o] == Unlinked) { (ClassA[na], AtA[na++]) = (classA[o], o); }
            }
            for (var r = bLo; r < bHi; r++) {
                if (linkB[r] == Unlinked) { (ClassB[nb], AtB[nb++]) = (classB[r], r); }
            }
            if (na == 0 || nb == 0) { return; }

            var count = 0;
            MyersDiff.Compare<int>(ClassA[..na], ClassB[..nb], 0, na, 0, nb, Found, ref count, Workspace);
            int i = 0, j = 0;
            for (var h = 0; h < count; h++) {
                var (original, revised) = Found[h];
                while (i < original.Offset) { emitter.Match(output, AtA[i++], AtB[j++]); }
                (i, j) = (original.Offset + original.Size, revised.Offset + revised.Size);
            }
            while (i < na) { emitter.Match(output, AtA[i++], AtB[j++]); }
        }
    }
}
