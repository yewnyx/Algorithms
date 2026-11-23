using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Yewnyx.Collections;

/// <summary>
/// A dictionary of one-to-one pairs, kept as two <see cref="Dictionary{TKey,TValue}"/>: each lookup, by key or by value,
/// costs one hash lookup. Enumerates in the key dictionary's order, as <see cref="Dictionary{TKey,TValue}"/> does, and
/// is not thread-safe.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[Serializable]
public class TwoWayDictionary<TKey, TValue> : ITwoWayDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull {
    private readonly Dictionary<TKey, TValue> _dictionary;
    private readonly Dictionary<TValue, TKey> _reverseDictionary;

    /// <summary>An empty dictionary.</summary>
    public TwoWayDictionary() {
        _dictionary = new Dictionary<TKey, TValue>();
        _reverseDictionary = new Dictionary<TValue, TKey>();
    }

    /// <summary>
    /// A copy of the pairs. Throws <see cref="ArgumentException"/> if a key or a value comes twice. Copying a
    /// <see cref="TwoWayDictionary{TKey,TValue}"/> copies both of its dictionaries without checking.
    /// </summary>
    /// <param name="collection">The pairs to copy.</param>
    public TwoWayDictionary(IReadOnlyCollection<KeyValuePair<TKey, TValue>> collection) {
        if (collection is TwoWayDictionary<TKey, TValue> twd) {
            _dictionary = new Dictionary<TKey, TValue>(twd._dictionary);
            _reverseDictionary = new Dictionary<TValue, TKey>(twd._reverseDictionary);
        } else {
            _dictionary = new Dictionary<TKey, TValue>();
            _reverseDictionary = new Dictionary<TValue, TKey>();
            foreach (var pair in collection) {
                _dictionary.Add(pair.Key, pair.Value);
                _reverseDictionary.Add(pair.Value, pair.Key);
            }
        }
    }

    /// <summary>The number of pairs.</summary>
    public int Count => _dictionary.Count;

    /// <inheritdoc/>
    public bool Contains(TKey key) => _dictionary.ContainsKey(key);
    /// <inheritdoc/>
    public bool Contains(TValue value) => _reverseDictionary.ContainsKey(value);

    // A miss leaves default in the out parameter, as Dictionary's TryGetValue does; the signature says it
    // isn't null, as the interface does.
    /// <inheritdoc/>
    public bool TryGet(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value!);
    /// <inheritdoc/>
    public bool TryGet(TValue value, out TKey key) => _reverseDictionary.TryGetValue(value, out key!);

    /// <inheritdoc/>
    public TValue this[TKey key] {
        get => _dictionary[key];
        set => _set(key, value);
    }

    /// <inheritdoc/>
    public TKey this[TValue val] {
        get => _reverseDictionary[val];
        set => _set(value, val);
    }

    // Keeps the pairs one-to-one: the key's old value and the value's old key are dropped first.
    private void _set(TKey key, TValue value) {
        if (_dictionary.TryGetValue(key, out var oldValue)) { _reverseDictionary.Remove(oldValue); }
        if (_reverseDictionary.TryGetValue(value, out var oldKey)) { _dictionary.Remove(oldKey); }
        _dictionary[key] = value;
        _reverseDictionary[value] = key;
    }

    /// <summary>
    /// Adds the pair. Does nothing if the key or the value is already present (unlike Dictionary.Add,
    /// which throws).
    /// </summary>
    public void Add(TKey key, TValue value) => _ = TryAdd(key, value);

    /// <inheritdoc/>
    public bool TryAdd(TKey key, TValue value) {
        if (!_dictionary.TryAdd(key, value)) { return false; }
        if (_reverseDictionary.TryAdd(value, key)) { return true; }

        // The value already belongs to another key.
        _dictionary.Remove(key);
        return false;
    }

    /// <inheritdoc cref="Add(TKey, TValue)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(TValue value, TKey key) => _ = TryAdd(key, value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TValue value, TKey key) => TryAdd(key, value);

    /// <inheritdoc/>
    public bool TryRemove(TKey key) {
        return _dictionary.Remove(key, out var value) &&
               _reverseDictionary.Remove(value);
    }

    /// <inheritdoc/>
    public bool TryRemove(TValue value) {
        return _reverseDictionary.Remove(value, out var key) &&
               _dictionary.Remove(key);
    }

    /// <inheritdoc/>
    public void Clear() {
        _dictionary.Clear();
        _reverseDictionary.Clear();
    }

    /// <summary>
    /// The enumerator is the dictionary's own struct, so a <c>foreach</c> over this doesn't allocate. Going
    /// through <see cref="IEnumerable{T}"/> boxes it, as it does for any collection.
    /// </summary>
    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}