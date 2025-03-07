using System.Collections.Generic;

namespace Yewnyx.Collections;

public interface IReadOnlyTwoWayDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey,TValue>>
    where TKey: notnull
    where TValue: notnull {
    bool Contains(TKey key);
    bool TryGet(TKey key, out TValue value);
    TValue this[TKey key] { get; }

    bool Contains(TValue value);
    bool TryGet(TValue value, out TKey key);
    TKey this[TValue value] { get; }
}