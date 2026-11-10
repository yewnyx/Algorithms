using System;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// A diff that can be kept and used later, without the revised sequence: the hunks plus copies of the
/// items they delete and insert. Buffers only grow, so a reused patch stops allocating.
/// </summary>
public sealed class Patch<T> where T : IEquatable<T> {
    private Hunk[] _hunks = [];
    private T[] _deleted = [];
    private T[] _inserted = [];
    private int _hunkCount;
    private int _deletedCount;
    private int _insertedCount;

    public int OriginalLength { get; private set; }
    public int RevisedLength { get; private set; }

    public ReadOnlySpan<Hunk> Hunks => _hunks.AsSpan(0, _hunkCount);

    /// <summary>The items the hunks delete, hunk by hunk.</summary>
    public ReadOnlySpan<T> Deleted => _deleted.AsSpan(0, _deletedCount);

    /// <summary>The items the hunks insert, hunk by hunk.</summary>
    public ReadOnlySpan<T> Inserted => _inserted.AsSpan(0, _insertedCount);

    /// <summary>Replaces the patch with <paramref name="hunks"/> and copies of the items they touch.</summary>
    public void Set(ReadOnlySpan<T> original, ReadOnlySpan<T> revised, ReadOnlySpan<Hunk> hunks) {
        var deleted = 0;
        var inserted = 0;
        foreach (var (o, r) in hunks) {
            deleted += o.Size;
            inserted += r.Size;
        }

        _grow(ref _hunks, hunks.Length);
        _grow(ref _deleted, deleted);
        _grow(ref _inserted, inserted);
        hunks.CopyTo(_hunks);
        deleted = 0;
        inserted = 0;
        foreach (var (o, r) in hunks) {
            original.Slice(o.Offset, o.Size).CopyTo(_deleted.AsSpan(deleted));
            revised.Slice(r.Offset, r.Size).CopyTo(_inserted.AsSpan(inserted));
            deleted += o.Size;
            inserted += r.Size;
        }

        (_hunkCount, _deletedCount, _insertedCount) = (hunks.Length, deleted, inserted);
        (OriginalLength, RevisedLength) = (original.Length, revised.Length);
    }

    /// <summary>
    /// Writes the revised sequence to <paramref name="result"/>. Returns false, writing nothing, if
    /// <paramref name="original"/> has the wrong length or differs in an item the patch deletes.
    /// </summary>
    /// <remarks>
    /// <paramref name="result"/> needs <see cref="RevisedLength"/> elements and must not share memory
    /// with <paramref name="original"/>.
    /// </remarks>
    public bool TryApply(ReadOnlySpan<T> original, Span<T> result) =>
        _tryRebuild(original, result, OriginalLength, RevisedLength, Deleted, Inserted, reverse: false);

    /// <summary>
    /// Writes the original sequence to <paramref name="result"/>, undoing the patch. Returns false,
    /// writing nothing, if <paramref name="revised"/> has the wrong length or differs in an item the
    /// patch inserts.
    /// </summary>
    /// <remarks>
    /// <paramref name="result"/> needs <see cref="OriginalLength"/> elements and must not share memory
    /// with <paramref name="revised"/>.
    /// </remarks>
    public bool TryRevert(ReadOnlySpan<T> revised, Span<T> result) =>
        _tryRebuild(revised, result, RevisedLength, OriginalLength, Inserted, Deleted, reverse: true);

    private bool _tryRebuild(ReadOnlySpan<T> from, Span<T> result, int fromLength, int toLength,
        ReadOnlySpan<T> removed, ReadOnlySpan<T> added, bool reverse) {
        if (result.Length < toLength) { throw new ArgumentException($"Needs at least {toLength} elements.", nameof(result)); }
        if (from.Overlaps(result)) { throw new ArgumentException("Shares memory with the input.", nameof(result)); }
        if (from.Length != fromLength) { return false; }

        var hunks = Hunks;
        var removedAt = 0;
        foreach (var hunk in hunks) {
            var source = reverse ? hunk.Revised : hunk.Original;
            if (!_sameItems(from.Slice(source.Offset, source.Size), removed.Slice(removedAt, source.Size))) { return false; }
            removedAt += source.Size;
        }

        var copied = 0;
        var written = 0;
        var addedAt = 0;
        foreach (var hunk in hunks) {
            var (source, target) = reverse ? (hunk.Revised, hunk.Original) : (hunk.Original, hunk.Revised);
            from[copied..source.Offset].CopyTo(result[written..]);
            written += source.Offset - copied;
            added.Slice(addedAt, target.Size).CopyTo(result[written..]);
            written += target.Size;
            addedAt += target.Size;
            copied = source.Offset + source.Size;
        }
        from[copied..].CopyTo(result[written..]);
        return true;
    }

    private static bool _sameItems(ReadOnlySpan<T> a, ReadOnlySpan<T> b) {
        for (var i = 0; i < a.Length; i++) {
            if (!Diffing.Hunks.Equal(a[i], b[i])) { return false; }
        }
        return true;
    }

    private static void _grow<TItem>(ref TItem[] array, int length) {
        if (array.Length < length) { array = new TItem[length]; }
    }
}
