﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Yewnyx.Collections;

[Serializable]
public class TwoWayDictionary<TKey, TValue> : ITwoWayDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull {
    private readonly Dictionary<TKey, TValue> _dictionary;
    private readonly Dictionary<TValue, TKey> _reverseDictionary;

    public TwoWayDictionary() {
        _dictionary = new Dictionary<TKey, TValue>();
        _reverseDictionary = new Dictionary<TValue, TKey>();
    }

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

    public int Count => _dictionary.Count;

    public bool Contains(TKey key) => _dictionary.ContainsKey(key);
    public bool Contains(TValue value) => _reverseDictionary.ContainsKey(value);

    public bool TryGet(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value);
    public bool TryGet(TValue value, out TKey key) => _reverseDictionary.TryGetValue(value, out key);

    public TValue this[TKey key] {
        get => _dictionary[key];
        set => _set(key, value);
    }

    public TKey this[TValue val] {
        get => _reverseDictionary[val];
        set => _set(value, val);
    }

    private void _set(TKey key, TValue value) {
        _dictionary[key] = value;
        _reverseDictionary[value] = key;
    }

    public void Add(TKey key, TValue value) => _ = TryAdd(key, value);

    public bool TryAdd(TKey key, TValue value) {
        if (!_dictionary.TryAdd(key, value)) { return false; }
        if (_reverseDictionary.TryAdd(value, key)) { return true; }

        _dictionary.Remove(key);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(TValue value, TKey key) => _ = TryAdd(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TValue value, TKey key) => TryAdd(key, value);

    public bool TryRemove(TKey key) {
        return _dictionary.Remove(key, out var value) &&
               _reverseDictionary.Remove(value);
    }

    public bool TryRemove(TValue value) {
        return _reverseDictionary.Remove(value, out var key) &&
               _dictionary.Remove(key);
    }

    public void Clear() {
        _dictionary.Clear();
        _reverseDictionary.Clear();
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}