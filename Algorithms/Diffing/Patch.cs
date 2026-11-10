using System;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>A moved item: deleted at <see cref="Original"/> and inserted at <see cref="Revised"/>.</summary>
public readonly struct Move(int original, int revised) : IEquatable<Move> {
    public readonly int Original = original;
    public readonly int Revised = revised;

    public bool Equals(Move other) => Original == other.Original && Revised == other.Revised;
    public override bool Equals(object? obj) => obj is Move other && Equals(other);
    public static bool operator ==(Move left, Move right) => left.Equals(right);
    public static bool operator !=(Move left, Move right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(Original, Revised);

    public void Deconstruct(out int original, out int revised) => (original, revised) = (Original, Revised);
}

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

    // For FindMoves: inserted items chained by hash, in order.
    private int[] _buckets = [];
    private int[] _chain = [];
    private int[] _revisedPositions = [];
    private Move[] _moves = [];

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

    /// <summary>
    /// Pairs deleted items with equal inserted items as moves. Each deleted item, in order, takes the
    /// earliest inserted item equal to it that is still free. The span is valid until the next call or
    /// <see cref="Set"/>. Speed depends on the items' <c>GetHashCode</c> varying in its low bits, as for
    /// <see cref="HistogramDiff"/>.
    /// </summary>
    public ReadOnlySpan<Move> FindMoves() {
        if (_deletedCount == 0 || _insertedCount == 0) { return default; }

        // Buckets hold the first inserted item of each hash (or -1); _chain links the rest, in order.
        var bucketCount = 1;
        while (bucketCount < 2 * _insertedCount) { bucketCount <<= 1; }
        _grow(ref _buckets, bucketCount);
        _grow(ref _chain, _insertedCount);
        _grow(ref _revisedPositions, _insertedCount);
        _grow(ref _moves, Math.Min(_deletedCount, _insertedCount));
        var buckets = _buckets.AsSpan(0, bucketCount);
        var chain = _chain.AsSpan(0, _insertedCount);
        buckets.Fill(-1);

        var item = _insertedCount;
        for (var h = _hunkCount - 1; h >= 0; h--) {
            var revised = _hunks[h].Revised;
            for (var i = revised.Size - 1; i >= 0; i--) {
                item--;
                _revisedPositions[item] = revised.Offset + i;
                ref var bucket = ref buckets[_hash(_inserted[item]) & (bucketCount - 1)];
                chain[item] = bucket;
                bucket = item;
            }
        }

        var count = 0;
        var deletedItem = 0;
        for (var h = 0; h < _hunkCount; h++) {
            var original = _hunks[h].Original;
            for (var i = 0; i < original.Size; i++, deletedItem++) {
                var value = _deleted[deletedItem];
                ref var link = ref buckets[_hash(value) & (bucketCount - 1)];
                while (link != -1 && !Diffing.Hunks.Equal(_inserted[link], value)) { link = ref chain[link]; }
                if (link == -1) { continue; }

                _moves[count++] = new Move(original.Offset + i, _revisedPositions[link]);
                link = chain[link]; // Unlink it so it pairs once.
            }
        }

        return _moves.AsSpan(0, count);
    }

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

    private static int _hash(T item) => item is null ? 0 : item.GetHashCode();

    private static void _grow<TItem>(ref TItem[] array, int length) {
        if (array.Length < length) { array = new TItem[length]; }
    }
}
