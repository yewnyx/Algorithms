#pragma warning disable CS0108, CS0114 // Indexer is...fine I think
namespace Yewnyx.Collections;

public interface ITwoWayDictionary<TKey, TValue> : IReadOnlyTwoWayDictionary<TKey, TValue>
    where TKey: notnull
    where TValue: notnull {
    void Clear();

    TValue this[TKey key] { get; set; }
    bool TryAdd(TKey key, TValue val);
    bool TryRemove(TKey key);

    TKey this[TValue val] { get; set; }
    bool TryAdd(TValue value, TKey key);
    bool TryRemove(TValue value);
}