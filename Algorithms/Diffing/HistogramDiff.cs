using System;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// Histogram diff, as in git and JGit: splits both sequences around the longest matching run built on
/// the rarest items, then repeats on each side. Scripts are not always shortest, but they line up
/// unique lines (such as declarations) instead of common ones (such as braces and blank lines), which
/// reads better. A region whose shared items are all too common falls back to <see cref="MyersDiff"/>.
/// No allocation, and results depend only on the inputs. Only the Myers fallback recurses, about
/// log d deep.
/// Speed depends on the items' <c>GetHashCode</c> varying in its low bits: hashes that share them, such
/// as ints that are all multiples of 256, slow it down sharply (results don't change).
/// </summary>
public static class HistogramDiff {
    /// <summary>Items seen more often than this in the original side of a region are not used as anchors.</summary>
    public const int MaxOccurrences = 64;

    /// <inheritdoc cref="MyersDiff.MaxHunks"/>
    public static int MaxHunks(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength) + 1;

    /// <summary>Workspace needed for a diff of these lengths.</summary>
    public static int WorkspaceLength(int originalLength, int revisedLength) =>
        _stackLength(originalLength, revisedLength) +
        Math.Max(_indexLength(originalLength), MyersDiff.WorkspaceLength(originalLength, revisedLength));

    /// <summary>
    /// Writes the hunks that turn <paramref name="original"/> into <paramref name="revised"/>, in order,
    /// and returns how many. Everything outside the hunks matches, in order. Each pure insertion or
    /// deletion sits as late as it can.
    /// </summary>
    /// <remarks>
    /// <paramref name="hunks"/> needs <see cref="MaxHunks"/> elements and <paramref name="workspace"/>
    /// needs <see cref="WorkspaceLength"/>. The inputs may overlap each other; hunks and workspace
    /// must not share memory with them.
    /// </remarks>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, Span<Hunk> hunks, Span<int> workspace)
        where T : IEquatable<T> {
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks.Length < maxHunks) { throw new ArgumentException($"Needs at least {maxHunks} elements.", nameof(hunks)); }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace.Length < workspaceLength) {
            throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace));
        }
        Hunks.RequireApart(original, revised, hunks, workspace);

        // Regions still to diff, four ints each. The left side of a split is pushed last so it comes
        // off first, which keeps the hunks in order.
        var stack = workspace[.._stackLength(original.Length, revised.Length)];
        var scratch = workspace[stack.Length..];
        var depth = 0;
        _push(stack, ref depth, 0, original.Length, 0, revised.Length);

        var count = 0;
        while (depth > 0) {
            depth -= 4;
            int aLo = stack[depth], aHi = stack[depth + 1], bLo = stack[depth + 2], bHi = stack[depth + 3];
            while (aLo < aHi && bLo < bHi && Hunks.Equal(original[aLo], revised[bLo])) {
                aLo++;
                bLo++;
            }
            while (aLo < aHi && bLo < bHi && Hunks.Equal(original[aHi - 1], revised[bHi - 1])) {
                aHi--;
                bHi--;
            }

            if (aLo == aHi || bLo == bHi) {
                if (aLo < aHi || bLo < bHi) { Hunks.Emit(hunks, ref count, aLo, aHi - aLo, bLo, bHi - bLo); }
                continue;
            }

            switch (_findAnchor(original, revised, aLo, aHi, bLo, bHi, scratch, out var aStart, out var bStart, out var length)) {
                case Anchor.NothingShared:
                    Hunks.Emit(hunks, ref count, aLo, aHi - aLo, bLo, bHi - bLo);
                    break;
                case Anchor.OnlyCommonItems:
                    MyersDiff.Compare(original, revised, aLo, aHi, bLo, bHi, hunks, ref count, scratch);
                    break;
                default:
                    _push(stack, ref depth, aStart + length, aHi, bStart + length, bHi);
                    _push(stack, ref depth, aLo, aStart, bLo, bStart);
                    break;
            }
        }

        return Hunks.SlideLate(original, revised, hunks, count);
    }

    /// <inheritdoc cref="Diff{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, Span{Hunk}, Span{int})"/>
    /// <remarks>Null or undersized arrays are replaced.</remarks>
    public static int Diff<T>(
        ReadOnlySpan<T> original, ReadOnlySpan<T> revised, [NotNull] ref Hunk[]? hunks, [NotNull] ref int[]? workspace)
        where T : IEquatable<T> {
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks is null || hunks.Length < maxHunks) { hunks = new Hunk[maxHunks]; }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace is null || workspace.Length < workspaceLength) { workspace = new int[workspaceLength]; }
        return Diff(original, revised, hunks.AsSpan(), workspace.AsSpan());
    }

    private enum Anchor { Found, NothingShared, OnlyCommonItems }

    // Every split keeps at least one matched item, so at most min(n, m) + 1 regions wait at once.
    private static int _stackLength(int n, int m) => 4 * (Math.Min(n, m) + 2);

    private static int _bucketCount(int n) {
        var buckets = 1;
        while (buckets < 2 * n) { buckets <<= 1; }
        return buckets;
    }

    // Buckets, then per distinct value (first position, count, next in bucket), then per position
    // (next position with the same value, distinct value).
    private static int _indexLength(int n) => _bucketCount(n) + 5 * n;

    private static void _push(Span<int> stack, ref int depth, int aLo, int aHi, int bLo, int bHi) {
        stack[depth] = aLo;
        stack[depth + 1] = aHi;
        stack[depth + 2] = bLo;
        stack[depth + 3] = bHi;
        depth += 4;
    }

    private static int _hash<T>(T item) => item is null ? 0 : item.GetHashCode();

    // Indexes a[aLo..aHi) by value, then scans b[bLo..bHi) for the matching run whose rarest item is
    // rarest in a, the longest such run winning ties. Both regions are non-empty.
    private static Anchor _findAnchor<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int aLo, int aHi, int bLo, int bHi,
        Span<int> scratch, out int aStart, out int bStart, out int length) where T : IEquatable<T> {
        var n = aHi - aLo;
        var bucketCount = _bucketCount(n);
        var mask = bucketCount - 1;
        var buckets = scratch[..bucketCount];
        var first = scratch.Slice(bucketCount, n);
        var counts = scratch.Slice(bucketCount + n, n);
        var nextInBucket = scratch.Slice(bucketCount + 2 * n, n);
        var nextSame = scratch.Slice(bucketCount + 3 * n, n);
        var valueOf = scratch.Slice(bucketCount + 4 * n, n);
        buckets.Fill(-1);

        // Backwards, so each value's positions chain in ascending order.
        var distinct = 0;
        for (var i = aHi - 1; i >= aLo; i--) {
            ref var bucket = ref buckets[_hash(a[i]) & mask];
            var value = bucket;
            while (value != -1 && !Hunks.Equal(a[first[value]], a[i])) { value = nextInBucket[value]; }
            if (value == -1) {
                value = distinct++;
                nextInBucket[value] = bucket;
                bucket = value;
                counts[value] = 0;
                nextSame[i - aLo] = -1;
            } else {
                nextSame[i - aLo] = first[value];
            }
            first[value] = i;
            counts[value]++;
            valueOf[i - aLo] = value;
        }

        var shared = false;
        var bestRarity = MaxOccurrences + 1;
        (aStart, bStart, length) = (0, 0, 0);
        for (var bi = bLo; bi < bHi;) {
            var next = bi + 1;
            var value = buckets[_hash(b[bi]) & mask];
            while (value != -1 && !Hunks.Equal(a[first[value]], b[bi])) { value = nextInBucket[value]; }

            if (value != -1) {
                shared = true;
                if (counts[value] <= MaxOccurrences) {
                    // Occurrences inside a run already grown from this item would only find it again.
                    var grownTo = aLo;
                    for (var ai = first[value]; ai != -1; ai = nextSame[ai - aLo]) {
                        if (ai < grownTo) { continue; }

                        int aFrom = ai, bFrom = bi, aTo = ai + 1, bTo = bi + 1, rarity = counts[value];
                        while (aFrom > aLo && bFrom > bLo && Hunks.Equal(a[aFrom - 1], b[bFrom - 1])) {
                            aFrom--;
                            bFrom--;
                            rarity = Math.Min(rarity, counts[valueOf[aFrom - aLo]]);
                        }
                        while (aTo < aHi && bTo < bHi && Hunks.Equal(a[aTo], b[bTo])) {
                            rarity = Math.Min(rarity, counts[valueOf[aTo - aLo]]);
                            aTo++;
                            bTo++;
                        }
                        grownTo = aTo;

                        if (rarity < bestRarity || (rarity == bestRarity && aTo - aFrom > length)) {
                            (aStart, bStart, length, bestRarity) = (aFrom, bFrom, aTo - aFrom, rarity);
                        }
                        if (next < bTo) { next = bTo; }
                    }
                }
            }
            bi = next;
        }

        if (length > 0) { return Anchor.Found; }
        return shared ? Anchor.OnlyCommonItems : Anchor.NothingShared;
    }
}
