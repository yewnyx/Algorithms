using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// Myers' diff (1986), linear-space version: a shortest edit script turning one sequence into another,
/// as hunks between matching runs. O((n + m) * d) time for d inserted and deleted items, O(n + m)
/// workspace, and no allocation. Results depend only on the inputs.
/// </summary>
public static class MyersDiff {
    /// <summary>Hunks needed for any diff of these lengths: there is a match between any two hunks.</summary>
    public static int MaxHunks(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength) + 1;

    /// <summary>Workspace needed for a diff of these lengths.</summary>
    public static int WorkspaceLength(int originalLength, int revisedLength) => 2 * (originalLength + revisedLength + 3);

    /// <summary>
    /// Writes the hunks that turn <paramref name="original"/> into <paramref name="revised"/>, in order,
    /// and returns how many. Everything outside the hunks matches, in order.
    /// </summary>
    /// <remarks>
    /// <paramref name="hunks"/> needs <see cref="MaxHunks"/> elements and <paramref name="workspace"/>
    /// needs <see cref="WorkspaceLength"/>.
    /// </remarks>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, Span<Hunk> hunks, Span<int> workspace)
        where T : IEquatable<T> {
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks.Length < maxHunks) { throw new ArgumentException($"Needs at least {maxHunks} elements.", nameof(hunks)); }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace.Length < workspaceLength) {
            throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace));
        }

        var count = 0;
        _compare(original, revised, 0, original.Length, 0, revised.Length, hunks, ref count, workspace);
        return count;
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

    // Diffs a[aLo..aHi) against b[bLo..bHi). The left half of each split recurses and the right half
    // loops, so the depth stays near log d.
    private static void _compare<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int aLo, int aHi, int bLo, int bHi,
        Span<Hunk> hunks, ref int count, Span<int> workspace) where T : IEquatable<T> {
        while (true) {
            while (aLo < aHi && bLo < bHi && Hunks.Equal(a[aLo], b[bLo])) {
                aLo++;
                bLo++;
            }
            while (aLo < aHi && bLo < bHi && Hunks.Equal(a[aHi - 1], b[bHi - 1])) {
                aHi--;
                bHi--;
            }

            if (aLo == aHi || bLo == bHi) {
                if (aLo < aHi || bLo < bHi) { Hunks.Emit(hunks, ref count, aLo, aHi - aLo, bLo, bHi - bLo); }
                return;
            }

            if (!_middleSnake(a[aLo..aHi], b[bLo..bHi], workspace, out var x, out var y)) {
                // No path met in the middle: nothing in common, so it is all one replacement.
                Hunks.Emit(hunks, ref count, aLo, aHi - aLo, bLo, bHi - bLo);
                return;
            }

            x += aLo;
            y += bLo;
            Debug.Assert((x, y) != (aLo, bLo) && (x, y) != (aHi, bHi), "The split must shrink the problem.");
            _compare(a, b, aLo, x, bLo, y, hunks, ref count, workspace);
            aLo = x;
            bLo = y;
        }
    }

    // Searches forward from the start and backward from the end, d edits at a time, until the paths
    // overlap. Writes the point where they meet, which lies on a shortest edit script. Both inputs are
    // non-empty and differ in their first and last items.
    private static bool _middleSnake<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<int> workspace, out int x, out int y)
        where T : IEquatable<T> {
        var n = a.Length;
        var m = b.Length;
        var maxD = (n + m + 1) / 2;
        var offset = maxD;
        // Diagonals -maxD..maxD, plus the starting entry at offset + 1 (beyond maxD when maxD is 1).
        var length = 2 * maxD + 2;

        // forward[offset + k] is the furthest x reached on diagonal k = x - y; backward is the same,
        // measured from the end.
        var forward = workspace[..length];
        var backward = workspace.Slice(length, length);
        forward.Fill(-1);
        backward.Fill(-1);
        forward[offset + 1] = 0;
        backward[offset + 1] = 0;

        var delta = n - m;
        // With an odd delta the paths first meet during a forward step, otherwise during a backward one.
        var oddDelta = (delta & 1) != 0;

        // Diagonals that ran off an edge are dropped from either end of the range.
        int forwardStart = 0, forwardEnd = 0, backwardStart = 0, backwardEnd = 0;
        for (var d = 0; d < maxD; d++) {
            for (var k = -d + forwardStart; k <= d - forwardEnd; k += 2) {
                var i = offset + k;
                var x1 = k == -d || (k != d && forward[i - 1] < forward[i + 1]) ? forward[i + 1] : forward[i - 1] + 1;
                var y1 = x1 - k;
                while (x1 < n && y1 < m && Hunks.Equal(a[x1], b[y1])) {
                    x1++;
                    y1++;
                }
                forward[i] = x1;

                if (x1 > n) {
                    forwardEnd += 2;
                } else if (y1 > m) {
                    forwardStart += 2;
                } else if (oddDelta) {
                    var j = offset + delta - k;
                    if (j >= 0 && j < length && backward[j] != -1 && x1 >= n - backward[j]) {
                        (x, y) = (x1, y1);
                        return true;
                    }
                }
            }

            for (var k = -d + backwardStart; k <= d - backwardEnd; k += 2) {
                var i = offset + k;
                var x2 = k == -d || (k != d && backward[i - 1] < backward[i + 1]) ? backward[i + 1] : backward[i - 1] + 1;
                var y2 = x2 - k;
                while (x2 < n && y2 < m && Hunks.Equal(a[n - x2 - 1], b[m - y2 - 1])) {
                    x2++;
                    y2++;
                }
                backward[i] = x2;

                if (x2 > n) {
                    backwardEnd += 2;
                } else if (y2 > m) {
                    backwardStart += 2;
                } else if (!oddDelta) {
                    var j = offset + delta - k;
                    if (j >= 0 && j < length && forward[j] != -1 && forward[j] >= n - x2) {
                        x = forward[j];
                        y = x - (j - offset);
                        return true;
                    }
                }
            }
        }

        x = y = 0;
        return false;
    }
}
