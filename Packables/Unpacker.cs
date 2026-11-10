using System;
using System.Collections.Generic;

namespace Yewnyx.Packables;

public enum UnpackOutcome { Clean, WithProblems, Failed }

public enum UnpackProblemKind {
    /// <summary>A value is there but of another kind.</summary>
    WrongKind,
    /// <summary>A number doesn't fit the type it's read as.</summary>
    OutOfRange,
    /// <summary>A ref names an object the document doesn't have.</summary>
    MissingObject,
    /// <summary>A ref's stated type differs from its target's own type.</summary>
    TypeMismatch,
    /// <summary>An object's type isn't registered, or isn't a valid "Name#Version".</summary>
    UnknownType,
    /// <summary>An object was written by a newer version of its type than the one registered.</summary>
    NewerVersion,
    /// <summary>The instance bound to an object isn't of the object's registered type.</summary>
    WrongInstance,
    /// <summary>The root is null or not an object.</summary>
    NoRoot,
    /// <summary>A create function gave null instead of an instance for an object.</summary>
    NotCreated,
}

/// <summary>One problem met while unpacking, and where: the object (-1 for the document), its type and the key.</summary>
public readonly struct UnpackProblem(UnpackProblemKind kind, int @object, string? type, string? key) {
    public readonly UnpackProblemKind Kind = kind;
    public readonly int Object = @object;
    public readonly string? Type = type;
    public readonly string? Key = key;

    public override string ToString() => $"{Kind} in object {Object} ({Type}){(Key is null ? "" : $" at \"{Key}\"")}";
}

[Flags]
public enum UnpackOptions {
    None = 0,
    /// <summary>The first problem ends the load, so nothing more is read only to be thrown away.</summary>
    FailFast = 1,
    /// <summary>Unknown or newer types are recorded and their objects skipped, instead of refusing the document.</summary>
    Permissive = 2,
}

/// <summary>
/// What a ref read found: null, or a ref to an object, its type, and the instance already bound to it
/// if any. The library never creates objects: the caller binds one it made or holds with
/// <see cref="Unpacker.Bind"/>, or passes a create function to a ref read.
/// </summary>
public readonly struct PackedRef {
    internal readonly int Target;

    /// <summary>The target's type, as "Name#Version".</summary>
    public readonly string? TypeName;

    /// <summary>The registered type for <see cref="TypeName"/>, if the registry has it.</summary>
    public readonly Type? Type;

    /// <summary>The instance bound to the target already, so shared refs give the same object.</summary>
    public readonly IPackable? Instance;

    public bool IsNull => TypeName is null;

    internal PackedRef(int target, string typeName, Type? type, IPackable? instance) {
        (Target, TypeName, Type, Instance) = (target, typeName, type, instance);
    }
}

/// <summary>
/// Reads one object's fields while unpacking. Reads return false for an absent key without recording
/// anything; a value of the wrong kind or out of range returns false and is recorded. Objects bound
/// to refs are filled from a queue, so each is complete by the time <see cref="Unpack{T}"/> returns.
/// </summary>
public readonly ref struct Unpacker {
    private readonly UnpackSession _session;
    private readonly int _object;

    internal Unpacker(UnpackSession session, int @object) {
        _session = session;
        _object = @object;
    }

    /// <summary>The version of this object's type that wrote it.</summary>
    public int Version => _session.Versions[_object];

    public KeyedReader Fields => new(_session, _object, _session.Document.ObjectFields(_object));

    public bool TryReadBoolean(string key, out bool value) => Fields.TryReadBoolean(key, out value);
    public bool TryReadInt32(string key, out int value) => Fields.TryReadInt32(key, out value);
    public bool TryReadInt64(string key, out long value) => Fields.TryReadInt64(key, out value);
    public bool TryReadSingle(string key, out float value) => Fields.TryReadSingle(key, out value);
    public bool TryReadDouble(string key, out double value) => Fields.TryReadDouble(key, out value);
    public bool TryReadString(string key, out string? value) => Fields.TryReadString(key, out value);
    public bool TryReadRef(string key, out PackedRef value) => Fields.TryReadRef(key, out value);
    /// <inheritdoc cref="ValueReader.TryReadRef{T}(Func{T}, out T)"/>
    public bool TryReadRef<T>(string key, Func<T> create, out T? value) where T : class, IPackable =>
        Fields.TryReadRef(key, create, out value);
    /// <inheritdoc cref="ValueReader.TryReadRef{T, TState}(TState, Func{TState, T}, out T)"/>
    public bool TryReadRef<T, TState>(string key, TState state, Func<TState, T> create, out T? value) where T : class, IPackable =>
        Fields.TryReadRef(key, state, create, out value);
    public bool TryReadMap(string key, out KeyedReader map) => Fields.TryReadMap(key, out map);
    public bool TryReadArray(string key, out ArrayReader array) => Fields.TryReadArray(key, out array);

    /// <summary>
    /// Binds <paramref name="instance"/> to the ref's object, queues it to be filled, and returns it. If
    /// the object is bound already, returns that instance instead. Returns null, and records it, if
    /// the instance isn't the object's registered type, so <c>Child = unpacker.Bind(found, new Leaf())</c>
    /// never keeps an object that wasn't bound.
    /// </summary>
    public T? Bind<T>(in PackedRef found, T instance) where T : class, IPackable {
        var bound = _session.Bind(found, instance, _object, null);
        if (bound is null || bound is T) { return bound as T; }
        _session.Record(UnpackProblemKind.WrongInstance, _object, null); // Bound already, to another type.
        return null;
    }

    /// <summary>
    /// Fills <paramref name="root"/> from <paramref name="document"/>, then every object bound along the
    /// way. <paramref name="problems"/>, if given, receives each problem met. Strict by default: an
    /// unknown or newer type anywhere the root reaches fails the load before anything is read.
    /// </summary>
    public static UnpackOutcome Unpack<T>(PackedDocument document, T root, PackableRegistry registry,
        List<UnpackProblem>? problems = null, UnpackOptions options = UnpackOptions.None) where T : class, IPackable {
        if (!document.IsSealed) { throw new ArgumentException("Seal the document first.", nameof(document)); }
        var session = new UnpackSession(document, registry, problems, options);
        return session.Run(root ?? throw new ArgumentNullException(nameof(root)));
    }
}

/// <summary>Reads named entries: an object's fields, or a map. Keys are found by binary search.</summary>
public readonly ref struct KeyedReader {
    private readonly UnpackSession _session;
    private readonly int _object;
    private readonly int _container;

    internal KeyedReader(UnpackSession session, int @object, int container) {
        (_session, _object, _container) = (session, @object, container);
    }

    public int Count => _session.Document.Entries(_container).Length;

    public bool TryReadBoolean(string key, out bool value) => Value(key).TryReadBoolean(out value);
    public bool TryReadInt32(string key, out int value) => Value(key).TryReadInt32(out value);
    public bool TryReadInt64(string key, out long value) => Value(key).TryReadInt64(out value);
    public bool TryReadSingle(string key, out float value) => Value(key).TryReadSingle(out value);
    public bool TryReadDouble(string key, out double value) => Value(key).TryReadDouble(out value);
    public bool TryReadString(string key, out string? value) => Value(key).TryReadString(out value);
    public bool TryReadRef(string key, out PackedRef value) => Value(key).TryReadRef(out value);
    /// <inheritdoc cref="ValueReader.TryReadRef{T}(Func{T}, out T)"/>
    public bool TryReadRef<T>(string key, Func<T> create, out T? value) where T : class, IPackable =>
        Value(key).TryReadRef(create, out value);
    /// <inheritdoc cref="ValueReader.TryReadRef{T, TState}(TState, Func{TState, T}, out T)"/>
    public bool TryReadRef<T, TState>(string key, TState state, Func<TState, T> create, out T? value) where T : class, IPackable =>
        Value(key).TryReadRef(state, create, out value);
    public bool TryReadMap(string key, out KeyedReader map) => Value(key).TryReadMap(out map);
    public bool TryReadArray(string key, out ArrayReader array) => Value(key).TryReadArray(out array);

    /// <summary>The value under <paramref name="key"/>; an absent key gives a reader whose reads all return false.</summary>
    public ValueReader Value(string key) {
        var entries = _session.Document.Entries(_container);
        int low = 0, high = entries.Length - 1;
        while (low <= high) {
            var middle = (low + high) >> 1;
            var order = string.CompareOrdinal(entries[middle].Key, key);
            if (order == 0) { return new ValueReader(_session, _object, key, entries[middle].Value, true); }
            if (order < 0) { low = middle + 1; } else { high = middle - 1; }
        }
        return new ValueReader(_session, _object, key, PackedValue.Null, false);
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>Walks the entries in key order. It holds its position; the entries it yields are read-only.</summary>
    public ref struct Enumerator {
        private readonly KeyedReader _reader;
        private int _index;

        internal Enumerator(KeyedReader reader) {
            _reader = reader;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _reader.Count;

        public KeyedEntry Current {
            get {
                var entry = _reader._session.Document.Entries(_reader._container)[_index];
                return new KeyedEntry(entry.Key!, new ValueReader(_reader._session, _reader._object, entry.Key, entry.Value, true));
            }
        }
    }
}

public readonly ref struct KeyedEntry {
    public readonly string Key;
    public readonly ValueReader Value;

    internal KeyedEntry(string key, ValueReader value) {
        Key = key;
        Value = value;
    }
}

/// <summary>Reads an array's elements by index or with foreach.</summary>
public readonly ref struct ArrayReader {
    private readonly UnpackSession _session;
    private readonly int _object;
    private readonly int _container;
    private readonly string? _key;

    internal ArrayReader(UnpackSession session, int @object, int container, string? key) {
        (_session, _object, _container, _key) = (session, @object, container, key);
    }

    public int Count => _session.Document.Entries(_container).Length;

    public ValueReader this[int index] =>
        new(_session, _object, _key, _session.Document.Entries(_container)[index].Value, true);

    /// <summary>
    /// Copies the elements into <paramref name="destination"/>, each read as by
    /// <see cref="ValueReader.TryReadInt32"/>. False, and recorded, if there are more than fit or one
    /// can't be read; <paramref name="count"/> is how many were read before that.
    /// </summary>
    public bool TryReadInt32s(Span<int> destination, out int count) {
        count = 0;
        if (!_fits(destination.Length)) { return false; }
        for (; count < Count; count++) {
            if (!this[count].TryReadInt32(out destination[count])) { return false; }
        }
        return true;
    }

    /// <inheritdoc cref="TryReadInt32s"/>
    public bool TryReadInt64s(Span<long> destination, out int count) {
        count = 0;
        if (!_fits(destination.Length)) { return false; }
        for (; count < Count; count++) {
            if (!this[count].TryReadInt64(out destination[count])) { return false; }
        }
        return true;
    }

    /// <inheritdoc cref="TryReadInt32s"/>
    public bool TryReadSingles(Span<float> destination, out int count) {
        count = 0;
        if (!_fits(destination.Length)) { return false; }
        for (; count < Count; count++) {
            if (!this[count].TryReadSingle(out destination[count])) { return false; }
        }
        return true;
    }

    /// <inheritdoc cref="TryReadInt32s"/>
    public bool TryReadDoubles(Span<double> destination, out int count) {
        count = 0;
        if (!_fits(destination.Length)) { return false; }
        for (; count < Count; count++) {
            if (!this[count].TryReadDouble(out destination[count])) { return false; }
        }
        return true;
    }

    private bool _fits(int length) => Count <= length || _session.Record(UnpackProblemKind.OutOfRange, _object, _key);

    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator {
        private readonly ArrayReader _reader;
        private int _index;

        internal Enumerator(ArrayReader reader) {
            _reader = reader;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _reader.Count;
        public ValueReader Current => _reader[_index];
    }
}

/// <summary>Reads one value as a given kind.</summary>
public readonly ref struct ValueReader {
    private readonly UnpackSession _session;
    private readonly int _object;
    private readonly string? _key;
    private readonly PackedValue _value;
    private readonly bool _present;

    internal ValueReader(UnpackSession session, int @object, string? key, PackedValue value, bool present) {
        (_session, _object, _key, _value, _present) = (session, @object, key, value, present);
    }

    public bool IsPresent => _present;
    public PackedKind Kind => _value.Kind;

    public bool TryReadBoolean(out bool value) {
        value = false;
        if (!_accept(PackedKind.Bool)) { return false; }
        value = _value.AsBool;
        return true;
    }

    public bool TryReadInt64(out long value) {
        value = 0;
        if (!_accept(PackedKind.Integer)) { return false; }
        value = _value.AsInteger;
        return true;
    }

    public bool TryReadInt32(out int value) {
        value = 0;
        if (!TryReadInt64(out var wide)) { return false; }
        if (wide is < int.MinValue or > int.MaxValue) { return _session.Record(UnpackProblemKind.OutOfRange, _object, _key); }
        value = (int)wide;
        return true;
    }

    /// <summary>Accepts an integer too, as JSON doesn't tell 1 from 1.0.</summary>
    public bool TryReadDouble(out double value) {
        value = 0;
        if (!_present || _session.Failed) { return false; }
        switch (_value.Kind) {
            case PackedKind.Double:
                value = _value.AsDouble;
                return true;
            case PackedKind.Integer:
                value = _value.AsInteger;
                return true;
            default:
                return _session.Record(UnpackProblemKind.WrongKind, _object, _key);
        }
    }

    public bool TryReadSingle(out float value) {
        value = 0;
        if (!TryReadDouble(out var wide)) { return false; }
        if (!double.IsNaN(wide) && !double.IsInfinity(wide) && Math.Abs(wide) > float.MaxValue) {
            return _session.Record(UnpackProblemKind.OutOfRange, _object, _key);
        }
        value = (float)wide;
        return true;
    }

    /// <summary>A stored null reads as a null string.</summary>
    public bool TryReadString(out string? value) {
        value = null;
        if (!_present || _session.Failed) { return false; }
        if (_value.Kind == PackedKind.Null) { return true; }
        if (!_accept(PackedKind.String)) { return false; }
        value = _value.AsString;
        return true;
    }

    /// <summary>
    /// A stored null gives a null ref. A ref to a missing object, or one whose stated type isn't its
    /// target's, returns false and is recorded.
    /// </summary>
    public bool TryReadRef(out PackedRef value) {
        value = default;
        if (!_present || _session.Failed) { return false; }
        if (_value.Kind == PackedKind.Null) { return true; }
        if (_value.Kind != PackedKind.Ref) { return _session.Record(UnpackProblemKind.WrongKind, _object, _key); }
        return _session.Resolve(_value, _object, _key, out value);
    }

    /// <summary>
    /// Reads a ref and gives the instance bound to its object, binding one <paramref name="create"/>
    /// makes if none is bound yet, so a shared object is made once. <paramref name="create"/> is called
    /// only then, and only if the object's registered type is a <typeparamref name="T"/>: never for a
    /// null ref, a ref that doesn't resolve, an object already bound, or an object of another type,
    /// which is recorded as <see cref="UnpackProblemKind.WrongInstance"/>. A null from it is recorded
    /// as <see cref="UnpackProblemKind.NotCreated"/>.
    /// </summary>
    public bool TryReadRef<T>(Func<T> create, out T? value) where T : class, IPackable =>
        TryReadRef<T, Func<T>>(create ?? throw new ArgumentNullException(nameof(create)), static make => make(), out value);

    /// <summary>
    /// As <see cref="TryReadRef{T}(Func{T}, out T)"/>, handing <paramref name="state"/> to
    /// <paramref name="create"/>, so making an object from context needn't capture it.
    /// </summary>
    public bool TryReadRef<T, TState>(TState state, Func<TState, T> create, out T? value) where T : class, IPackable {
        if (create is null) { throw new ArgumentNullException(nameof(create)); }
        value = null;
        if (!TryReadRef(out PackedRef found)) { return false; }
        if (found.IsNull) { return true; }

        IPackable? bound = found.Instance;
        if (bound is null) {
            if (!typeof(T).IsAssignableFrom(found.Type!)) { return _session.Record(UnpackProblemKind.WrongInstance, _object, _key); }
            if (create(state) is not { } made) { return _session.Record(UnpackProblemKind.NotCreated, _object, _key); }
            bound = _session.Bind(found, made, _object, _key);
            if (bound is null) { return false; } // Refused, and recorded.
        }
        value = bound as T;
        return value is not null || _session.Record(UnpackProblemKind.WrongInstance, _object, _key);
    }

    public bool TryReadMap(out KeyedReader map) {
        map = default;
        if (!_accept(PackedKind.Map)) { return false; }
        map = new KeyedReader(_session, _object, _value.Container);
        return true;
    }

    public bool TryReadArray(out ArrayReader array) {
        array = default;
        if (!_accept(PackedKind.Array)) { return false; }
        array = new ArrayReader(_session, _object, _value.Container, _key);
        return true;
    }

    // Absent, or after a fail-fast failure: false, recording nothing. Another kind: false, recorded.
    private bool _accept(PackedKind kind) {
        if (!_present || _session.Failed) { return false; }
        return _value.Kind == kind || _session.Record(UnpackProblemKind.WrongKind, _object, _key);
    }
}

internal sealed class UnpackSession {
    public readonly PackedDocument Document;
    public readonly int[] Versions;
    private readonly PackableRegistry _registry;
    private readonly List<UnpackProblem>? _problems;
    private readonly UnpackOptions _options;
    private readonly IPackable?[] _bound;
    private readonly Type?[] _types; // Registered type per object, null if unknown.
    private readonly List<int> _queue = new();

    public bool Failed;
    private bool _anyProblem;

    public UnpackSession(PackedDocument document, PackableRegistry registry, List<UnpackProblem>? problems, UnpackOptions options) {
        (Document, _registry, _problems, _options) = (document, registry, problems, options);
        Versions = new int[document.ObjectCount];
        _bound = new IPackable?[document.ObjectCount];
        _types = new Type?[document.ObjectCount];
    }

    public UnpackOutcome Run(IPackable root) {
        var rootRef = Document.Root;
        if (rootRef.Kind != PackedKind.Ref || (uint)rootRef.Target >= (uint)Document.ObjectCount) {
            Record(UnpackProblemKind.NoRoot, -1, null);
            return UnpackOutcome.Failed;
        }
        // The root is a ref like any other, so its stated type must be its object's.
        if (!string.Equals(rootRef.RefType, Document.ObjectType(rootRef.Target))) {
            Record(UnpackProblemKind.TypeMismatch, -1, null);
            return UnpackOutcome.Failed;
        }

        // Up front: check every type the root reaches, so a strict load fails before anything is read.
        foreach (var index in _reachable(rootRef.Target)) {
            var type = Document.ObjectType(index);
            if (!PackableNames.TryParse(type, out var name, out var version) || !_registry.TryGet(name, out var registration)) {
                Record(UnpackProblemKind.UnknownType, index, null);
            } else if (version > registration.Version) {
                Record(UnpackProblemKind.NewerVersion, index, null);
            } else {
                (_types[index], Versions[index]) = (registration.Type, version);
            }
        }
        if (_anyProblem && (_options & UnpackOptions.Permissive) == 0) { Failed = true; }
        if (Failed) { return UnpackOutcome.Failed; }

        if (_types[rootRef.Target] is null) { return UnpackOutcome.Failed; } // Permissive, but the root itself is unknown.
        var found = new PackedRef(rootRef.Target, Document.ObjectType(rootRef.Target), _types[rootRef.Target], null);
        if (Bind(found, root, -1, null) is null) { return UnpackOutcome.Failed; }

        for (var i = 0; i < _queue.Count && !Failed; i++) {
            var index = _queue[i];
            try {
                _bound[index]!.UnpackFrom(new Unpacker(this, index));
            } catch (Exception e) {
                e.Data["Packables.Object"] = index;
                e.Data["Packables.Type"] = Document.ObjectType(index);
                throw;
            }
        }

        return Failed ? UnpackOutcome.Failed : _anyProblem ? UnpackOutcome.WithProblems : UnpackOutcome.Clean;
    }

    public bool Resolve(PackedValue value, int from, string? key, out PackedRef found) {
        found = default;
        var target = value.Target;
        if (target < 0 || target >= Document.ObjectCount) { return Record(UnpackProblemKind.MissingObject, from, key); }
        if (!string.Equals(value.RefType, Document.ObjectType(target))) { return Record(UnpackProblemKind.TypeMismatch, from, key); }
        if (_types[target] is null) { return false; } // Unknown or newer: recorded up front, skipped in permissive mode.
        found = new PackedRef(target, Document.ObjectType(target), _types[target], _bound[target]);
        return true;
    }

    // The instance bound to the ref's object afterwards, or null if this one is refused (recorded
    // against the reading object and key).
    public IPackable? Bind(in PackedRef found, IPackable instance, int from, string? key) {
        if (Failed || found.IsNull) { return null; }
        if (_bound[found.Target] is { } existing) { return existing; }
        if (instance.GetType() != _types[found.Target]) {
            Record(UnpackProblemKind.WrongInstance, from, key);
            return null;
        }
        _bound[found.Target] = instance;
        _queue.Add(found.Target);
        return instance;
    }

    // Records a problem and returns false, so callers can return its result.
    public bool Record(UnpackProblemKind kind, int @object, string? key) {
        _anyProblem = true;
        _problems?.Add(new UnpackProblem(kind, @object, @object >= 0 ? Document.ObjectType(@object) : null, key));
        if ((_options & UnpackOptions.FailFast) != 0) { Failed = true; }
        return false;
    }

    private List<int> _reachable(int root) {
        var seen = new bool[Document.ObjectCount];
        var order = new List<int> { root };
        seen[root] = true;
        for (var i = 0; i < order.Count; i++) { _collect(Document.ObjectFields(order[i]), seen, order); }
        return order;
    }

    private void _collect(int container, bool[] seen, List<int> order) {
        foreach (var entry in Document.Entries(container)) {
            if (entry.Value.Kind == PackedKind.Ref && entry.Value.Target >= 0 && entry.Value.Target < seen.Length &&
                !seen[entry.Value.Target]) {
                seen[entry.Value.Target] = true;
                order.Add(entry.Value.Target);
            } else if (entry.Value.Kind is PackedKind.Map or PackedKind.Array) {
                _collect(entry.Value.Container, seen, order);
            }
        }
    }
}
