#pragma warning disable CS0108, CS0114 // Indexer is...fine I think
namespace Yewnyx.Collections;

/// <summary>
/// A dictionary of one-to-one pairs that can be changed. A key is never in two pairs, nor a value.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ITwoWayDictionary<TKey, TValue> : IReadOnlyTwoWayDictionary<TKey, TValue>
    where TKey: notnull
    where TValue: notnull {
    /// <summary>Removes every pair.</summary>
    void Clear();

    /// <summary>
    /// The value paired with the key. Getting one that is not there throws
    /// <see cref="System.Collections.Generic.KeyNotFoundException"/>. Setting adds the pair, first removing the key's
    /// old pair and the old pair of the value, if there are any.
    /// </summary>
    TValue this[TKey key] { get; set; }
    /// <summary>
    /// Adds the pair and returns true, or returns false and changes nothing if the key or the value is already in a
    /// pair.
    /// </summary>
    bool TryAdd(TKey key, TValue val);
    /// <summary>Removes the key's pair and returns true, or returns false if there is none.</summary>
    bool TryRemove(TKey key);

    /// <summary>
    /// The key paired with the value. Getting one that is not there throws
    /// <see cref="System.Collections.Generic.KeyNotFoundException"/>. Setting adds the pair, first removing the value's
    /// old pair and the old pair of the key, if there are any.
    /// </summary>
    TKey this[TValue val] { get; set; }
    /// <summary>
    /// Adds the pair and returns true, or returns false and changes nothing if the key or the value is already in a
    /// pair.
    /// </summary>
    bool TryAdd(TValue value, TKey key);
    /// <summary>Removes the value's pair and returns true, or returns false if there is none.</summary>
    bool TryRemove(TValue value);
}