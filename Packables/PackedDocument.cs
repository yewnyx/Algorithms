using System;

namespace Yewnyx.Packables;

public enum PackedKind : byte { Null, Bool, Integer, Double, String, Ref, Map, Array }

/// <summary>
/// One value in a packed document. Doubles are normalised when made (-0.0 becomes 0.0, and every NaN
/// the same NaN), so values compare bit for bit.
/// </summary>
public readonly struct PackedValue : IEquatable<PackedValue> {
    private readonly long _bits; // Bool, Integer, Double bits, a ref's target, or a container.
    private readonly string? _text; // A String's text, or the type a ref says its target has.

    public PackedKind Kind { get; }

    private PackedValue(PackedKind kind, long bits, string? text) {
        Kind = kind;
        _bits = bits;
        _text = text;
    }

    public static PackedValue Null => default;
    public static PackedValue Bool(bool value) => new(PackedKind.Bool, value ? 1 : 0, null);
    public static PackedValue Integer(long value) => new(PackedKind.Integer, value, null);

    public static PackedValue Double(double value) {
        if (double.IsNaN(value)) { value = double.NaN; } else if (value == 0) { value = 0.0; }
        return new PackedValue(PackedKind.Double, BitConverter.DoubleToInt64Bits(value), null);
    }

    /// <summary>A null string is <see cref="Null"/>.</summary>
    public static PackedValue String(string? value) => value is null ? Null : new PackedValue(PackedKind.String, 0, value);

    /// <summary>
    /// A ref to object <paramref name="target"/> (-1 if the document has no such object), saying its
    /// target's type is <paramref name="type"/>.
    /// </summary>
    public static PackedValue Ref(int target, string type) => new(PackedKind.Ref, target, type);

    public static PackedValue Map(int container) => new(PackedKind.Map, container, null);
    public static PackedValue Array(int container) => new(PackedKind.Array, container, null);

    public bool AsBool => _require(PackedKind.Bool)._bits != 0;
    public long AsInteger => _require(PackedKind.Integer)._bits;
    public double AsDouble => BitConverter.Int64BitsToDouble(_require(PackedKind.Double)._bits);
    public string AsString => _require(PackedKind.String)._text!;
    public int Target => (int)_require(PackedKind.Ref)._bits;
    public string RefType => _require(PackedKind.Ref)._text!;
    public int Container => Kind is PackedKind.Map or PackedKind.Array ? (int)_bits : throw _wrongKind("Map or Array");

    private PackedValue _require(PackedKind kind) => Kind == kind ? this : throw _wrongKind(kind.ToString());
    private InvalidOperationException _wrongKind(string expected) => new($"A {Kind} value, not {expected}.");

    // Containers compare by index, so this is identity for them, not content.
    public bool Equals(PackedValue other) => Kind == other.Kind && _bits == other._bits && string.Equals(_text, other._text);
    public override bool Equals(object? obj) => obj is PackedValue other && Equals(other);
    public static bool operator ==(PackedValue left, PackedValue right) => left.Equals(right);
    public static bool operator !=(PackedValue left, PackedValue right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(Kind, _bits, _text);

    public override string ToString() => Kind switch {
        PackedKind.Null => "null",
        PackedKind.Bool => AsBool ? "true" : "false",
        PackedKind.Integer => AsInteger.ToString(),
        PackedKind.Double => AsDouble.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        PackedKind.String => $"\"{_text}\"",
        PackedKind.Ref => $"ref {Target} ({_text})",
        _ => $"{Kind} {_bits}",
    };
}

/// <summary>An entry in a container. <see cref="Key"/> is null in arrays.</summary>
public readonly struct PackedEntry(string? key, PackedValue value) {
    public readonly string? Key = key;
    public readonly PackedValue Value = value;
}

/// <summary>
/// A document as a model, independent of how it's encoded: objects, each with a type name and its
/// fields, and containers (maps and arrays) of values. Built by adding objects, containers and entries
/// in any order, then sealed, which lays out each container and sorts maps and fields by key.
/// Buffers only grow, so a reused document stops allocating.
/// </summary>
public sealed class PackedDocument {
    private struct ObjectRecord {
        public string Type;
        public int Fields;
    }

    private struct ContainerRecord {
        public bool Keyed;
        public int Start, Count; // In _entries, once sealed.
        public int Head, Tail; // In _building, while building.
    }

    private struct Building {
        public PackedEntry Entry;
        public int Next;
    }

    private ObjectRecord[] _objects = [];
    private ContainerRecord[] _containers = [];
    private Building[] _building = [];
    private PackedEntry[] _entries = [];
    private int[] _remap = [];
    private ObjectRecord[] _reordered = [];
    private int _objectCount, _containerCount, _buildingCount;

    public bool IsSealed { get; private set; }

    /// <summary>A ref to the root object, or <see cref="PackedValue.Null"/>.</summary>
    public PackedValue Root { get; private set; }

    public int ObjectCount => _objectCount;

    /// <summary>The object's type, as "Name#Version" or whatever the document said.</summary>
    public string ObjectType(int index) => _object(index).Type;

    /// <summary>The map holding the object's fields.</summary>
    public int ObjectFields(int index) => _object(index).Fields;

    public bool IsMap(int container) => _container(container).Keyed;

    /// <summary>A sealed container's entries: maps and fields in key order, arrays in order.</summary>
    public ReadOnlySpan<PackedEntry> Entries(int container) {
        _requireSealed(true);
        var c = _container(container);
        return _entries.AsSpan(c.Start, c.Count);
    }

    /// <summary>Empties the document and keeps its buffers.</summary>
    public void Clear() {
        // Drop the strings held, so a cleared document doesn't keep them alive.
        System.Array.Clear(_objects, 0, _objectCount);
        System.Array.Clear(_building, 0, _buildingCount);
        System.Array.Clear(_entries, 0, Math.Min(_entries.Length, _buildingCount));
        _objectCount = _containerCount = _buildingCount = 0;
        Root = PackedValue.Null;
        IsSealed = false;
    }

    /// <summary>Adds an object with an empty map for its fields, and returns its index.</summary>
    internal int AddObject(string type) {
        _requireSealed(false);
        var fields = AddMap();
        _grow(ref _objects, _objectCount + 1);
        _objects[_objectCount] = new ObjectRecord { Type = type, Fields = fields };
        return _objectCount++;
    }

    internal int AddMap() => _addContainer(true);
    internal int AddArray() => _addContainer(false);

    /// <summary>Adds an entry to a map or an object's fields. Duplicate keys are found when sealing.</summary>
    internal void Add(int map, string key, PackedValue value) {
        if (!_container(map).Keyed) { throw new InvalidOperationException("Keyed entries go in maps."); }
        _append(map, new PackedEntry(key ?? throw new ArgumentNullException(nameof(key)), value));
    }

    /// <summary>Adds an element to an array.</summary>
    internal void Add(int array, PackedValue value) {
        if (_container(array).Keyed) { throw new InvalidOperationException("Arrays take values without keys."); }
        _append(array, new PackedEntry(null, value));
    }

    internal void SetRoot(PackedValue root) {
        if (root.Kind is not (PackedKind.Ref or PackedKind.Null)) { throw new ArgumentException("The root is a ref or null.", nameof(root)); }
        Root = root;
    }

    /// <summary>
    /// Lays out every container and sorts maps by key (ordinal, UTF-16 code units). False, with the
    /// first map holding a key twice, if any does; the document is sealed either way.
    /// </summary>
    internal bool TrySeal(out int duplicateIn, out string? duplicateKey) {
        _requireSealed(false);
        (duplicateIn, duplicateKey) = (-1, null);
        _grow(ref _entries, _buildingCount);
        var start = 0;
        for (var i = 0; i < _containerCount; i++) {
            ref var c = ref _containers[i];
            c.Start = start;
            for (var at = c.Head; at != -1; at = _building[at].Next) { _entries[start++] = _building[at].Entry; }
            c.Count = start - c.Start;
            if (!c.Keyed || c.Count < 2) { continue; }

            _sortByKey(_entries.AsSpan(c.Start, c.Count));
            for (var e = c.Start + 1; e < start && duplicateKey is null; e++) {
                if (string.Equals(_entries[e].Key, _entries[e - 1].Key)) { (duplicateIn, duplicateKey) = (i, _entries[e].Key); }
            }
        }
        IsSealed = true;
        return duplicateKey is null;
    }

    /// <summary>
    /// Renumbers objects breadth-first from the root, visiting fields and maps in key order and arrays
    /// in order, and drops objects the root can't reach. Refs to missing objects (-1) stay as they are.
    /// </summary>
    internal void Canonicalize() {
        _requireSealed(true);
        _grow(ref _remap, _objectCount);
        _grow(ref _reordered, _objectCount);
        var remap = _remap.AsSpan(0, _objectCount);
        remap.Fill(-1);

        var kept = 0;
        if (Root.Kind == PackedKind.Ref && (uint)Root.Target >= (uint)_objectCount) {
            Root = PackedValue.Ref(-1, Root.RefType);
        } else if (Root.Kind == PackedKind.Ref) {
            remap[Root.Target] = kept;
            _reordered[kept++] = _objects[Root.Target];
            Root = PackedValue.Ref(0, Root.RefType);
        }
        // The objects kept so far are also the queue: visiting one appends the objects it first reaches.
        for (var next = 0; next < kept; next++) { _visit(_reordered[next].Fields, remap, ref kept); }

        System.Array.Copy(_reordered, _objects, kept);
        _objectCount = kept;
    }

    private void _visit(int container, Span<int> remap, ref int kept) {
        var c = _containers[container];
        for (var e = c.Start; e < c.Start + c.Count; e++) {
            var entry = _entries[e];
            switch (entry.Value.Kind) {
                case PackedKind.Ref when (uint)entry.Value.Target >= (uint)_objectCount:
                    // A target past the end, or any negative one, is a missing object.
                    if (entry.Value.Target != -1) { _entries[e] = new PackedEntry(entry.Key, PackedValue.Ref(-1, entry.Value.RefType)); }
                    break;
                case PackedKind.Ref:
                    var old = entry.Value.Target;
                    if (remap[old] == -1) {
                        remap[old] = kept;
                        _reordered[kept++] = _objects[old];
                    }
                    _entries[e] = new PackedEntry(entry.Key, PackedValue.Ref(remap[old], entry.Value.RefType));
                    break;
                case PackedKind.Map or PackedKind.Array:
                    _visit(entry.Value.Container, remap, ref kept);
                    break;
            }
        }
    }

    private int _addContainer(bool keyed) {
        _requireSealed(false);
        _grow(ref _containers, _containerCount + 1);
        _containers[_containerCount] = new ContainerRecord { Keyed = keyed, Head = -1, Tail = -1 };
        return _containerCount++;
    }

    private void _append(int container, PackedEntry entry) {
        _requireSealed(false);
        _grow(ref _building, _buildingCount + 1);
        _building[_buildingCount] = new Building { Entry = entry, Next = -1 };
        ref var c = ref _containers[container];
        if (c.Tail == -1) { c.Head = _buildingCount; } else { _building[c.Tail].Next = _buildingCount; }
        c.Tail = _buildingCount++;
    }

    private ref ObjectRecord _object(int index) {
        if ((uint)index >= (uint)_objectCount) { throw new ArgumentOutOfRangeException(nameof(index), index, "Not an object."); }
        return ref _objects[index];
    }

    private ref ContainerRecord _container(int index) {
        if ((uint)index >= (uint)_containerCount) { throw new ArgumentOutOfRangeException(nameof(index), index, "Not a container."); }
        return ref _containers[index];
    }

    private void _requireSealed(bool sealed_) {
        if (IsSealed != sealed_) { throw new InvalidOperationException(sealed_ ? "Seal the document first." : "The document is sealed."); }
    }

    private static void _grow<T>(ref T[] array, int length) {
        if (array.Length < length) { System.Array.Resize(ref array, Math.Max(length, array.Length * 2)); }
    }

    // Insertion sort for the small sets fields usually are, heapsort beyond that. Neither allocates.
    private static void _sortByKey(Span<PackedEntry> entries) {
        if (entries.Length <= 16) {
            for (var i = 1; i < entries.Length; i++) {
                var entry = entries[i];
                var j = i - 1;
                for (; j >= 0 && string.CompareOrdinal(entries[j].Key, entry.Key) > 0; j--) { entries[j + 1] = entries[j]; }
                entries[j + 1] = entry;
            }
            return;
        }

        for (var root = entries.Length / 2 - 1; root >= 0; root--) { _siftDown(entries, root, entries.Length); }
        for (var end = entries.Length - 1; end > 0; end--) {
            (entries[0], entries[end]) = (entries[end], entries[0]);
            _siftDown(entries, 0, end);
        }
    }

    private static void _siftDown(Span<PackedEntry> entries, int root, int length) {
        while (true) {
            var child = 2 * root + 1;
            if (child >= length) { return; }
            if (child + 1 < length && string.CompareOrdinal(entries[child].Key, entries[child + 1].Key) < 0) { child++; }
            if (string.CompareOrdinal(entries[root].Key, entries[child].Key) >= 0) { return; }
            (entries[root], entries[child]) = (entries[child], entries[root]);
            root = child;
        }
    }
}
