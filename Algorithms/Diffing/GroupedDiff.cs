using System;
using System.Runtime.InteropServices;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// Gives groups of items, such as lines of characters, class ids: groups holding equal items share an id,
/// and ids are numbered from 0 in the order first seen, the original's groups first. Any diff over the
/// ids is then a diff over the groups. See <see cref="GroupedDiff"/> for the whole recipe. No allocation,
/// and results depend only on the inputs.
/// </summary>
/// <remarks>
/// Each group is hashed from its items' <c>GetHashCode</c>, and equal hashes are confirmed item by item,
/// so ids are right whatever the hashes; only speed depends on them. Empty groups share one id.
/// </remarks>
public static class GroupClasses {
    /// <summary>Workspace needed for these numbers of groups (or items, when items are the groups).</summary>
    public static int WorkspaceLength(int originalGroups, int revisedGroups) {
        var groups = originalGroups + revisedGroups;
        return _bucketCount(groups) + 3 * groups;
    }

    /// <summary>
    /// Writes each group's class id to <paramref name="originalClasses"/> and
    /// <paramref name="revisedClasses"/>, and returns how many classes there are.
    /// </summary>
    /// <remarks>
    /// Groups are slices of their side's items, in any order, and may overlap or leave gaps. The class
    /// spans need an element per group and <paramref name="workspace"/> needs <see cref="WorkspaceLength"/>;
    /// none of these may share memory with the items or each other.
    /// </remarks>
    public static int Assign<T>(ReadOnlySpan<T> original, ReadOnlySpan<Segment> originalGroups, ReadOnlySpan<T> revised,
        ReadOnlySpan<Segment> revisedGroups, Span<int> originalClasses, Span<int> revisedClasses, Span<int> workspace)
        where T : IEquatable<T> {
        _requireInside(originalGroups, original.Length, nameof(originalGroups));
        _requireInside(revisedGroups, revised.Length, nameof(revisedGroups));
        return _assign(original, originalGroups, revised, revisedGroups, false, originalClasses, revisedClasses, workspace);
    }

    /// <summary>Gives each item a class id, as if each were a group of its own.</summary>
    public static int Assign<T>(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, Span<int> originalClasses,
        Span<int> revisedClasses, Span<int> workspace) where T : IEquatable<T> =>
        _assign(original, default, revised, default, true, originalClasses, revisedClasses, workspace);

    private static int _bucketCount(int n) {
        var buckets = 1;
        while (buckets < 2 * n) { buckets <<= 1; }
        return buckets;
    }

    private static void _requireInside(ReadOnlySpan<Segment> groups, int length, string name) {
        foreach (var (offset, size) in groups) {
            if ((uint)offset > (uint)length || (uint)size > (uint)(length - offset)) {
                throw new ArgumentException("A group lies outside the items.", name);
            }
        }
    }

    private static int _assign<T>(ReadOnlySpan<T> a, ReadOnlySpan<Segment> groupsA, ReadOnlySpan<T> b,
        ReadOnlySpan<Segment> groupsB, bool itemsAreGroups, Span<int> classesA, Span<int> classesB, Span<int> workspace)
        where T : IEquatable<T> {
        var countA = itemsAreGroups ? a.Length : groupsA.Length;
        var countB = itemsAreGroups ? b.Length : groupsB.Length;
        if (classesA.Length < countA) { throw new ArgumentException($"Needs at least {countA} elements.", "originalClasses"); }
        if (classesB.Length < countB) { throw new ArgumentException($"Needs at least {countB} elements.", "revisedClasses"); }
        var workspaceLength = WorkspaceLength(countA, countB);
        if (workspace.Length < workspaceLength) { throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace)); }
        Hunks.RequireApart(a, b, classesA, "originalClasses");
        Hunks.RequireApart(a, b, classesB, "revisedClasses");
        Hunks.RequireApart(a, b, workspace, nameof(workspace));
        if (classesA.Overlaps(classesB) || classesA.Overlaps(workspace) || classesB.Overlaps(workspace)) {
            throw new ArgumentException("The class spans and workspace must not share memory.", nameof(workspace));
        }

        // Buckets hold a class (or -1); each class links to the next in its bucket, and keeps its first
        // group (numbered across both sides) and its hash.
        var total = countA + countB;
        var buckets = workspace[.._bucketCount(total)];
        var next = workspace.Slice(buckets.Length, total);
        var first = workspace.Slice(buckets.Length + total, total);
        var hashes = workspace.Slice(buckets.Length + 2 * total, total);
        buckets.Fill(-1);
        var mask = buckets.Length - 1;
        var classes = 0;
        for (var g = 0; g < total; g++) {
            var items = _items(a, groupsA, b, groupsB, itemsAreGroups, countA, g);
            var hash = _hash(items);
            ref var bucket = ref buckets[hash & mask];
            var found = bucket;
            while (found != -1 && (hashes[found] != hash ||
                                   !_same(_items(a, groupsA, b, groupsB, itemsAreGroups, countA, first[found]), items))) {
                found = next[found];
            }
            if (found == -1) {
                found = classes++;
                (next[found], first[found], hashes[found]) = (bucket, g, hash);
                bucket = found;
            }
            if (g < countA) { classesA[g] = found; } else { classesB[g - countA] = found; }
        }
        return classes;
    }

    private static ReadOnlySpan<T> _items<T>(ReadOnlySpan<T> a, ReadOnlySpan<Segment> groupsA, ReadOnlySpan<T> b,
        ReadOnlySpan<Segment> groupsB, bool itemsAreGroups, int countA, int group) {
        if (group < countA) { return itemsAreGroups ? a.Slice(group, 1) : a.Slice(groupsA[group].Offset, groupsA[group].Size); }
        group -= countA;
        return itemsAreGroups ? b.Slice(group, 1) : b.Slice(groupsB[group].Offset, groupsB[group].Size);
    }

    // Mixed so its low bits, which pick the bucket, depend on every item.
    private static int _hash<T>(ReadOnlySpan<T> items) {
        var hash = (uint)items.Length;
        foreach (var item in items) {
            hash = (hash ^ (uint)(item is null ? 0 : item.GetHashCode())) * 0x9E3779B1u;
            hash ^= hash >> 15;
        }
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        return (int)(hash & int.MaxValue);
    }

    private static bool _same<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T> {
        if (a.Length != b.Length) { return false; }
        for (var i = 0; i < a.Length; i++) {
            if (!Hunks.Equal(a[i], b[i])) { return false; }
        }
        return true;
    }
}

/// <summary>
/// Diffs items split into groups, such as lines of characters, in three steps:
/// <list type="number">
/// <item><see cref="GroupClasses.Assign{T}(ReadOnlySpan{T}, ReadOnlySpan{Segment}, ReadOnlySpan{T}, ReadOnlySpan{Segment}, Span{int}, Span{int}, Span{int})"/>
/// gives the groups class ids.</item>
/// <item>Any diff over the ids (<see cref="MyersDiff"/>, <see cref="HistogramDiff"/>, ...) gives the
/// group hunks: which lines changed.</item>
/// <item><see cref="Refine{T}"/> diffs the items inside the changed groups and writes item hunks: a
/// complete script over the items, which <see cref="Patch{T}"/> takes, and which a view draws as the
/// characters that changed inside changed lines.</item>
/// </list>
/// Nothing is about text: the items and groups are the caller's (characters in lines, words in lines,
/// cells in rows). Deeper levels compose: lines' ids are items too, so paragraphs are groups over them,
/// and refining paragraph hunks over line ids gives line hunks, which refine again into characters.
/// </summary>
public static class GroupedDiff {
    /// <inheritdoc cref="MyersDiff.MaxHunks"/>
    public static int MaxHunks(int originalLength, int revisedLength) => Math.Min(originalLength, revisedLength) + 1;

    /// <summary>Workspace needed to refine a diff of these numbers of items.</summary>
    public static int WorkspaceLength(int originalLength, int revisedLength) =>
        MyersDiff.WorkspaceLength(originalLength, revisedLength) + 4 * MaxHunks(originalLength, revisedLength);

    /// <summary>
    /// Writes item hunks for a diff over groups, given its group hunks, and returns how many. Where a run
    /// of groups is replaced by as many groups, they pair in order and each pair's items are diffed with
    /// <see cref="MyersDiff"/>, each pure insertion or deletion as late as it goes inside its pair. A run
    /// replaced by a different number of groups is one item hunk. Groups outside the group hunks match,
    /// and so do their items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The groups must tile their items in order: the first starts at 0, each starts where the last ended,
    /// and the last ends at the end (text: lines with their line breaks). So the group an item hunk lies
    /// in is found by its offset, and each item hunk lies inside one group hunk's items, unless only empty
    /// groups lie between two group hunks: then no item separates them, and their item hunks join.
    /// </para>
    /// <para>
    /// A run whose group counts differ gets no refinement, such as one line edited next to one line
    /// added; and a pair of unrelated groups gives scattered small matches. A view can tell both apart by
    /// how little matched. Each pair costs what Myers' diff costs on it, so two long, unrelated groups
    /// (one long changed line) are quadratic in their length.
    /// </para>
    /// <para>
    /// The group hunks must come from a diff of the groups' classes: they're checked to lie in order, and
    /// each pair of groups outside them to be the same size, but not compared item by item.
    /// </para>
    /// <para>
    /// <paramref name="hunks"/> needs <see cref="MaxHunks"/> elements and <paramref name="workspace"/>
    /// <see cref="WorkspaceLength"/>; neither may share memory with the items or the group hunks.
    /// </para>
    /// </remarks>
    public static int Refine<T>(ReadOnlySpan<T> original, ReadOnlySpan<Segment> originalGroups, ReadOnlySpan<T> revised,
        ReadOnlySpan<Segment> revisedGroups, ReadOnlySpan<Hunk> groupHunks, Span<Hunk> hunks, Span<int> workspace)
        where T : IEquatable<T> {
        int n = original.Length, m = revised.Length;
        _requireTiling(originalGroups, n, nameof(originalGroups));
        _requireTiling(revisedGroups, m, nameof(revisedGroups));
        _requireInOrder(groupHunks, originalGroups, revisedGroups);
        var maxHunks = MaxHunks(n, m);
        if (hunks.Length < maxHunks) { throw new ArgumentException($"Needs at least {maxHunks} elements.", nameof(hunks)); }
        var workspaceLength = WorkspaceLength(n, m);
        if (workspace.Length < workspaceLength) { throw new ArgumentException($"Needs at least {workspaceLength} elements.", nameof(workspace)); }
        Hunks.RequireApart(original, revised, hunks, workspace);
        if (groupHunks.Overlaps(hunks)) { throw new ArgumentException("Shares memory with the group hunks.", nameof(hunks)); }

        // A pair's own hunks, before they're shifted into place.
        var myers = workspace[..MyersDiff.WorkspaceLength(n, m)];
        var pairHunks = MemoryMarshal.Cast<int, Hunk>(workspace.Slice(myers.Length, 4 * maxHunks));
        var count = 0;
        foreach (var (groupsA, groupsB) in groupHunks) {
            if (groupsA.Size != groupsB.Size) {
                int aStart = _start(originalGroups, groupsA.Offset, n), aEnd = _start(originalGroups, groupsA.Offset + groupsA.Size, n);
                int bStart = _start(revisedGroups, groupsB.Offset, m), bEnd = _start(revisedGroups, groupsB.Offset + groupsB.Size, m);
                // Empty groups added or removed change no items.
                if (aStart < aEnd || bStart < bEnd) { Hunks.Emit(hunks, ref count, aStart, aEnd - aStart, bStart, bEnd - bStart); }
                continue;
            }
            for (var i = 0; i < groupsA.Size; i++) {
                var (aOffset, aSize) = originalGroups[groupsA.Offset + i];
                var (bOffset, bSize) = revisedGroups[groupsB.Offset + i];
                var a = original.Slice(aOffset, aSize);
                var b = revised.Slice(bOffset, bSize);
                var pairCount = 0;
                MyersDiff.Compare(a, b, 0, aSize, 0, bSize, pairHunks, ref pairCount, myers);
                pairCount = Hunks.SlideLate(a, b, pairHunks, pairCount);
                for (var h = 0; h < pairCount; h++) {
                    var (o, r) = pairHunks[h];
                    Hunks.Emit(hunks, ref count, aOffset + o.Offset, o.Size, bOffset + r.Offset, r.Size);
                }
            }
        }
        return count;
    }

    // Where a group starts, or the end for the group past the last.
    private static int _start(ReadOnlySpan<Segment> groups, int group, int length) => group < groups.Length ? groups[group].Offset : length;

    private static void _requireTiling(ReadOnlySpan<Segment> groups, int length, string name) {
        var end = 0;
        foreach (var (offset, size) in groups) {
            if (offset != end || size < 0) { throw new ArgumentException("Groups must tile the items in order.", name); }
            end = offset + size;
        }
        if (end != length) { throw new ArgumentException("Groups must tile the items in order.", name); }
    }

    // The group hunks lie in order inside the groups, and the groups between them pair up with the same sizes.
    private static void _requireInOrder(ReadOnlySpan<Hunk> groupHunks, ReadOnlySpan<Segment> originalGroups, ReadOnlySpan<Segment> revisedGroups) {
        int a = 0, b = 0;
        foreach (var (original, revised) in groupHunks) {
            if (original.Offset < a || original.Size < 0 || original.Offset + original.Size > originalGroups.Length ||
                revised.Offset < b || revised.Size < 0 || revised.Offset + revised.Size > revisedGroups.Length ||
                original.Offset - a != revised.Offset - b) {
                throw new ArgumentException("Group hunks must lie in order inside the groups.", nameof(groupHunks));
            }
            _requireSameSizes(originalGroups[a..original.Offset], revisedGroups[b..revised.Offset]);
            (a, b) = (original.Offset + original.Size, revised.Offset + revised.Size);
        }
        if (originalGroups.Length - a != revisedGroups.Length - b) {
            throw new ArgumentException("Group hunks must lie in order inside the groups.", nameof(groupHunks));
        }
        _requireSameSizes(originalGroups[a..], revisedGroups[b..]);
    }

    private static void _requireSameSizes(ReadOnlySpan<Segment> original, ReadOnlySpan<Segment> revised) {
        for (var i = 0; i < original.Length; i++) {
            if (original[i].Size != revised[i].Size) {
                throw new ArgumentException("Groups outside the group hunks must match, so be the same size.", "groupHunks");
            }
        }
    }
}
