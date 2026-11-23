using System.Collections.Generic;

namespace Yewnyx.Collections;

/// <summary>
/// A read-only dictionary of one-to-one pairs: each key has one value and each value one key, so either can be looked up.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IReadOnlyTwoWayDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey,TValue>>
    where TKey: notnull
    where TValue: notnull {
    /// <summary>Whether the key is in a pair.</summary>
    bool Contains(TKey key);
    /// <summary>
    /// Looks up the value paired with the key. Returns false, with a default <paramref name="value"/>, if there is
    /// none.
    /// </summary>
    bool TryGet(TKey key, out TValue value);
    /// <summary>The value paired with the key. Throws <see cref="KeyNotFoundException"/> if there is none.</summary>
    TValue this[TKey key] { get; }

    /// <summary>Whether the value is in a pair.</summary>
    bool Contains(TValue value);
    /// <summary>
    /// Looks up the key paired with the value. Returns false, with a default <paramref name="key"/>, if there is
    /// none.
    /// </summary>
    bool TryGet(TValue value, out TKey key);
    /// <summary>The key paired with the value. Throws <see cref="KeyNotFoundException"/> if there is none.</summary>
    TKey this[TValue value] { get; }
}