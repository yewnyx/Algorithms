using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// Myers' diff (1986), linear-space version: a shortest edit script turning one sequence into another,
/// as hunks between matching runs. O((n + m) * d) time for d inserted and deleted items, O(n + m)
/// workspace, and no allocation. Results depend only on the inputs. The overloads with a budget trade
/// the shortest script for a bounded cost.
/// </summary>
public static class MyersDiff {
    /// <summary>Hunks needed for any diff of these lengths: there is a match between any two hunks.</summary>
    public static int MaxHunks(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength) + 1;

    /// <summary>Workspace needed for a diff of these lengths.</summary>
    public static int WorkspaceLength(int originalLength, int revisedLength) => 2 * (originalLength + revisedLength + 3);

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
        where T : IEquatable<T> => Diff(original, revised, hunks, workspace, int.MaxValue, out _);

    /// <inheritdoc cref="Diff{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, Span{Hunk}, Span{int})"/>
    /// <remarks>Null or undersized arrays are replaced.</remarks>
    public static int Diff<T>(
        ReadOnlySpan<T> original, ReadOnlySpan<T> revised, [NotNull] ref Hunk[]? hunks, [NotNull] ref int[]? workspace)
        where T : IEquatable<T> => Diff(original, revised, ref hunks, ref workspace, int.MaxValue, out _);

    /// <summary>
    /// As <see cref="Diff{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, Span{Hunk}, Span{int})"/>, but a search that
    /// would have to look for more than <paramref name="budget"/> edits stops looking for the shortest script
    /// and splits at the best point it reached. The script is still complete, and <see cref="Patch{T}"/>
    /// takes it, but it may be longer than the shortest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A script of at most <paramref name="budget"/> edits comes out exactly as it does without a budget.
    /// A budget of 0 matches only the common start and end; <see cref="int.MaxValue"/> is no limit. An
    /// odd budget searches as far as the next even one.
    /// </para>
    /// <para>
    /// Time is about <c>(n + m) * (budget + 7)</c> item comparisons, where the 7 only matters for budgets
    /// under 10, so for a total allowance pass <c>allowance / (n + m) - 7</c>, but not below 0. That leaves
    /// out the passes that are linear in the input. The same buffer sizes as without a budget are needed.
    /// </para>
    /// </remarks>
    /// <param name="budget">
    /// How many edits (items inserted or deleted) one search looks for. It limits neither the script nor
    /// the total work.
    /// </param>
    /// <param name="cutShort">Whether any search was cut short, so the script may be longer than the shortest.</param>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, Span<Hunk> hunks, Span<int> workspace,
        int budget, out bool cutShort) where T : IEquatable<T> {
        var steps = Steps(budget);
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks.Length < maxHunks) { throw new ArgumentException($"Needs at least {maxHunks} elements.", nameof(hunks)); }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace.Length < workspaceLength) {
            throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace));
        }
        Hunks.RequireApart(original, revised, hunks, workspace);

        var count = 0;
        cutShort = false;
        Compare(original, revised, 0, original.Length, 0, revised.Length, hunks, ref count, steps, ref cutShort, workspace);
        return Hunks.SlideLate(original, revised, hunks, count);
    }

    /// <inheritdoc cref="Diff{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, Span{Hunk}, Span{int}, int, out bool)"/>
    /// <remarks>Null or undersized arrays are replaced.</remarks>
    public static int Diff<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, [NotNull] ref Hunk[]? hunks,
        [NotNull] ref int[]? workspace, int budget, out bool cutShort) where T : IEquatable<T> {
        Steps(budget);
        var maxHunks = MaxHunks(original.Length, revised.Length);
        if (hunks is null || hunks.Length < maxHunks) { hunks = new Hunk[maxHunks]; }
        var workspaceLength = WorkspaceLength(original.Length, revised.Length);
        if (workspace is null || workspace.Length < workspaceLength) { workspace = new int[workspaceLength]; }
        return Diff(original, revised, hunks.AsSpan(), workspace.AsSpan(), budget, out cutShort);
    }

    // How many rounds of the search a budget allows: it meets in the middle after half the script's edits,
    // rounded up, plus the round that starts it. Checks the budget, since every budgeted overload comes here.
    internal static int Steps(int budget) {
        if (budget < 0) { throw new ArgumentOutOfRangeException(nameof(budget), "The budget can't be negative."); }
        return (budget >> 1) + (budget & 1) + 1;
    }

    // Diffs a[aLo..aHi) against b[bLo..bHi), allowing each search `steps` rounds (see Steps; int.MaxValue
    // is as many as it takes) and setting `cut` when one ran out.
    //
    // The left half of each split recurses and the right half loops, so the depth stays near log d. A search
    // that runs out of rounds splits at the furthest point either direction reached. A point reached by the
    // forward search splits as above. One reached by the backward search leaves a left part that still needs
    // searching, and a right part that is known to take at most the budget's edits. The right part is set
    // aside, as the point where it starts, at the end of the workspace, and solved exactly (so it can't run
    // out) once the left part is done. Each piece is solved once, which keeps the cost near the budget times
    // the length.
    internal static void Compare<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int aLo, int aHi, int bLo, int bHi,
        Span<Hunk> hunks, ref int count, int steps, ref bool cut, Span<int> workspace) where T : IEquatable<T> {
        // Set-aside pieces: piece i runs from its point to the one set aside before it (or to the end of
        // this region). Point i is stored at workspace[^(2 * i + 2)..].
        int endA = aHi, endB = bHi, pieces = 0;
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
                if (pieces == 0) { return; }

                // Solve the leftmost set-aside piece: from the last point set aside to the one before it.
                var last = workspace.Length - 2 * pieces;
                var (pieceA, pieceB) = (workspace[last], workspace[last + 1]);
                pieces--;
                var (nextA, nextB) = pieces > 0 ? (workspace[last + 2], workspace[last + 3]) : (endA, endB);
                var pieceCut = false;
                Compare(a, b, pieceA, nextA, pieceB, nextB, hunks, ref count, steps, ref pieceCut, workspace[..(workspace.Length - 2 * pieces)]);
                Debug.Assert(!pieceCut, "A set-aside piece takes at most the budget's edits, so it is solved without a cut.");
                (aLo, bLo, aHi, bHi) = (nextA, nextB, nextA, nextB);
                continue;
            }

            var free = workspace[..(workspace.Length - 2 * pieces)];
            var result = _middleSnake(a[aLo..aHi], b[bLo..bHi], steps, free, out var x, out var y, out var fromEnd);
            if (result is Search.NothingInCommon or Search.NoPoint) {
                // Nothing to split on (after a cut short, with no point that gets anywhere, as for a budget
                // of 0): it is all one replacement. Then on to the pieces set aside.
                if (result == Search.NoPoint) { cut = true; }
                Hunks.Emit(hunks, ref count, aLo, aHi - aLo, bLo, bHi - bLo);
                aLo = aHi;
                bLo = bHi;
                continue;
            }

            // A point from a cut is strictly inside the region, in the sense that matters: forward, it has
            // x + y > 0 and is not the end; backward, it is not the start of the region and has progressed
            // from the end. So both parts shrink, and a cut always advances.
            x += aLo;
            y += bLo;
            Debug.Assert((x, y) != (aLo, bLo) && (x, y) != (aHi, bHi), "The split must shrink the problem.");
            if (result == Search.Cut) { cut = true; }
            if (fromEnd) {
                // Set aside the piece after the point, and carry on with what's before it.
                var slot = workspace.Length - 2 * pieces - 2;
                (workspace[slot], workspace[slot + 1]) = (x, y);
                pieces++;
                aHi = x;
                bHi = y;
                continue;
            }
            Compare(a, b, aLo, x, bLo, y, hunks, ref count, steps, ref cut, free);
            aLo = x;
            bLo = y;
        }
    }

    private enum Search { Met, NothingInCommon, Cut, NoPoint }

    // Searches forward from the start and backward from the end, d edits at a time, until the paths
    // overlap. Writes the point where they meet, which lies on a shortest edit script. Both inputs are
    // non-empty and differ in their first and last items.
    //
    // With fewer rounds than the inputs could need, the search may end before they meet. Then it writes the
    // furthest point either direction reached (fromEnd says which), counting how far from the diagonal
    // between the start and the end it lies as lost progress, so a path that wanders off doesn't win.
    // Backward points are only used when the workspace has room beyond the search's own arrays to set the
    // piece after them aside.
    private static Search _middleSnake<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int steps, Span<int> workspace,
        out int x, out int y, out bool fromEnd) where T : IEquatable<T> {
        fromEnd = false;
        var n = a.Length;
        var m = b.Length;
        var maxD = (n + m + 1) / 2;
        var rounds = Math.Min(maxD, steps);
        var offset = rounds;
        // Diagonals -rounds..rounds, plus the starting entry at offset + 1 (beyond rounds when it is 1).
        var length = 2 * rounds + 2;

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
        for (var d = 0; d < rounds; d++) {
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
                        return Search.Met;
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
                        return Search.Met;
                    }
                }
            }
        }

        x = y = 0;
        // Every round the inputs could need was run, and the paths never met: nothing in common.
        if (rounds == maxD) { return Search.NothingInCommon; }

        // With buffers of the stated sizes there is always room to set one more piece aside; this only
        // keeps the pieces from ever overlapping the search's arrays.
        var canSetAside = workspace.Length - 2 * length >= 2;
        int low = Math.Min(0, delta), high = Math.Max(0, delta);
        var best = int.MinValue;
        for (var k = 1 - rounds; k < rounds; k++) {
            // The forward search's diagonal k, and the backward search's, which is the forward diagonal
            // delta - k: the same distance from the span, since low + high is delta.
            var distance = _distance(k, low, high);
            if (forward[offset + k] >= 0) {
                int fx = forward[offset + k], fy = fx - k;
                if (fx <= n && fy >= 0 && fy <= m && fx + fy > 0 && (fx, fy) != (n, m)) {
                    var score = fx + fy - distance;
                    if (score > best) {
                        (best, x, y, fromEnd) = (score, fx, fy, false);
                    }
                }
            }
            if (canSetAside && backward[offset + k] >= 0) {
                int gx = backward[offset + k], gy = gx - k;
                if (gx <= n && gy >= 0 && gy <= m && gx + gy > 0 && (gx, gy) != (n, m)) {
                    var score = gx + gy - distance;
                    if (score > best) {
                        (best, x, y, fromEnd) = (score, n - gx, m - gy, true);
                    }
                }
            }
        }
        return best == int.MinValue ? Search.NoPoint : Search.Cut;
    }

    // How far diagonal k lies outside the span of diagonals between low and high.
    private static int _distance(int k, int low, int high) => k < low ? low - k : k > high ? k - high : 0;
}
