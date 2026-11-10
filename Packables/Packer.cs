using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Yewnyx.Packables;

/// <summary>A type that writes itself to a packed document and reads itself back.</summary>
public interface IPackable {
    void PackInto(Packer packer);
    void UnpackFrom(Unpacker unpacker);
}

/// <summary>
/// Writes one object's fields. <see cref="Pack"/> makes one for each object it reaches from the root.
/// Fields, maps and arrays are handles: each names its own container, so nothing is opened or closed.
/// Keys are sorted when the document is sealed, so the order of writes doesn't matter.
/// </summary>
public readonly ref struct Packer {
    private readonly PackSession _session;
    private readonly int _fields;

    internal Packer(PackSession session, int fields) {
        _session = session;
        _fields = fields;
    }

    /// <summary>The object's own fields.</summary>
    public KeyedWriter Fields => new(_session, _fields);

    public void WriteNull(string key) => Fields.WriteNull(key);
    public void WriteBoolean(string key, bool value) => Fields.WriteBoolean(key, value);
    public void WriteInt32(string key, int value) => Fields.WriteInt32(key, value);
    public void WriteInt64(string key, long value) => Fields.WriteInt64(key, value);
    public void WriteSingle(string key, float value) => Fields.WriteSingle(key, value);
    public void WriteDouble(string key, double value) => Fields.WriteDouble(key, value);
    public void WriteString(string key, string? value) => Fields.WriteString(key, value);
    public void WriteRef(string key, IPackable? value) => Fields.WriteRef(key, value);

    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteInt32(string key, char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteInt64(string key, char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteSingle(string key, char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteDouble(string key, char value) => throw new NotSupportedException();

    public void WriteInt32s(string key, ReadOnlySpan<int> values) => Fields.WriteInt32s(key, values);
    public void WriteInt64s(string key, ReadOnlySpan<long> values) => Fields.WriteInt64s(key, values);
    public void WriteSingles(string key, ReadOnlySpan<float> values) => Fields.WriteSingles(key, values);
    public void WriteDoubles(string key, ReadOnlySpan<double> values) => Fields.WriteDoubles(key, values);
    public KeyedWriter Map(string key) => Fields.Map(key);
    public ArrayWriter Array(string key) => Fields.Array(key);

    /// <summary>
    /// Packs <paramref name="root"/> and every object it reaches into <paramref name="document"/>,
    /// which is cleared first. Objects are told apart by reference and numbered breadth-first from the
    /// root. Returns false if an object's type isn't in <paramref name="registry"/>, naming it in
    /// <paramref name="unregistered"/>. Throws if a type writes the same key twice.
    /// </summary>
    public static bool TryPack(IPackable root, PackableRegistry registry, PackedDocument document, out Type? unregistered) {
        if (root is null) { throw new ArgumentNullException(nameof(root)); }

        var session = PackSession.Rent(registry, document);
        try {
            document.Clear();
            var rootRef = session.RefTo(root);
            for (var i = 0; i < session.Queue.Count && session.Unregistered is null; i++) {
                session.Queue[i].PackInto(new Packer(session, document.ObjectFields(i)));
            }

            unregistered = session.Unregistered;
            if (unregistered is not null) { return false; }

            document.SetRoot(rootRef);
            if (!document.TrySeal(out _, out var key)) { throw new InvalidOperationException($"\"{key}\" was written twice."); }
            document.Canonicalize();
            return true;
        } finally {
            session.Return();
        }
    }
}

/// <summary>
/// Writes named entries: an object's fields, or a map. Numbers are stored as one of two kinds, a 64-bit
/// integer or a double, so WriteInt32 and WriteInt64 store the same kind, as do WriteSingle and WriteDouble.
/// </summary>
public readonly ref struct KeyedWriter {
    // A char converts to every number type without a cast, so each number write refuses one. A ulong
    // isn't refused this way: a ulong overload would also capture uint, byte and constant arguments,
    // and it can't reach WriteInt64 anyway, having no implicit conversion to long.
    internal const string CharIsNotANumber = "A char isn't a number: write it as a string, or cast it.";

    private readonly PackSession _session;
    private readonly int _container;

    internal KeyedWriter(PackSession session, int container) {
        _session = session;
        _container = container;
    }

    private PackedDocument Document => _session.Document;

    public void WriteNull(string key) => Document.Add(_container, key, PackedValue.Null);
    public void WriteBoolean(string key, bool value) => Document.Add(_container, key, PackedValue.Bool(value));
    public void WriteInt32(string key, int value) => Document.Add(_container, key, PackedValue.Integer(value));
    public void WriteInt64(string key, long value) => Document.Add(_container, key, PackedValue.Integer(value));
    public void WriteSingle(string key, float value) => Document.Add(_container, key, PackedValue.Double(value));
    public void WriteDouble(string key, double value) => Document.Add(_container, key, PackedValue.Double(value));
    public void WriteString(string key, string? value) => Document.Add(_container, key, PackedValue.String(value));
    public void WriteRef(string key, IPackable? value) => Document.Add(_container, key, _session.RefTo(value));

    [Obsolete(CharIsNotANumber, true)] public void WriteInt32(string key, char value) => throw new NotSupportedException();
    [Obsolete(CharIsNotANumber, true)] public void WriteInt64(string key, char value) => throw new NotSupportedException();
    [Obsolete(CharIsNotANumber, true)] public void WriteSingle(string key, char value) => throw new NotSupportedException();
    [Obsolete(CharIsNotANumber, true)] public void WriteDouble(string key, char value) => throw new NotSupportedException();

    /// <summary>Adds an array of numbers under <paramref name="key"/>, straight from the span.</summary>
    public void WriteInt32s(string key, ReadOnlySpan<int> values) {
        var array = Array(key);
        foreach (var value in values) { array.WriteInt32(value); }
    }

    /// <inheritdoc cref="WriteInt32s"/>
    public void WriteInt64s(string key, ReadOnlySpan<long> values) {
        var array = Array(key);
        foreach (var value in values) { array.WriteInt64(value); }
    }

    /// <inheritdoc cref="WriteInt32s"/>
    public void WriteSingles(string key, ReadOnlySpan<float> values) {
        var array = Array(key);
        foreach (var value in values) { array.WriteSingle(value); }
    }

    /// <inheritdoc cref="WriteInt32s"/>
    public void WriteDoubles(string key, ReadOnlySpan<double> values) {
        var array = Array(key);
        foreach (var value in values) { array.WriteDouble(value); }
    }

    /// <summary>Adds a map under <paramref name="key"/> and returns a writer for it.</summary>
    public KeyedWriter Map(string key) {
        var map = Document.AddMap();
        Document.Add(_container, key, PackedValue.Map(map));
        return new KeyedWriter(_session, map);
    }

    /// <summary>Adds an array under <paramref name="key"/> and returns a writer for it.</summary>
    public ArrayWriter Array(string key) {
        var array = Document.AddArray();
        Document.Add(_container, key, PackedValue.Array(array));
        return new ArrayWriter(_session, array);
    }
}

/// <summary>Appends elements to an array.</summary>
public readonly ref struct ArrayWriter {
    private readonly PackSession _session;
    private readonly int _container;

    internal ArrayWriter(PackSession session, int container) {
        _session = session;
        _container = container;
    }

    private PackedDocument Document => _session.Document;

    public void WriteNull() => Document.Add(_container, PackedValue.Null);
    public void WriteBoolean(bool value) => Document.Add(_container, PackedValue.Bool(value));
    public void WriteInt32(int value) => Document.Add(_container, PackedValue.Integer(value));
    public void WriteInt64(long value) => Document.Add(_container, PackedValue.Integer(value));
    public void WriteSingle(float value) => Document.Add(_container, PackedValue.Double(value));
    public void WriteDouble(double value) => Document.Add(_container, PackedValue.Double(value));
    public void WriteString(string? value) => Document.Add(_container, PackedValue.String(value));
    public void WriteRef(IPackable? value) => Document.Add(_container, _session.RefTo(value));

    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteInt32(char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteInt64(char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteSingle(char value) => throw new NotSupportedException();
    [Obsolete(KeyedWriter.CharIsNotANumber, true)] public void WriteDouble(char value) => throw new NotSupportedException();

    public KeyedWriter Map() {
        var map = Document.AddMap();
        Document.Add(_container, PackedValue.Map(map));
        return new KeyedWriter(_session, map);
    }

    public ArrayWriter Array() {
        var array = Document.AddArray();
        Document.Add(_container, PackedValue.Array(array));
        return new ArrayWriter(_session, array);
    }
}

// One pack's state: objects by reference, in the order they were reached. Reused per thread, so
// packing stops allocating once warm; a pack started inside another gets its own.
internal sealed class PackSession {
    [ThreadStatic] private static PackSession? t_spare;

    private readonly Dictionary<IPackable, int> _ids = new(ReferenceComparer.Instance);
    public readonly List<IPackable> Queue = new();
    public PackableRegistry Registry = PackableRegistry.Empty;
    public PackedDocument Document = null!;
    public Type? Unregistered;

    public static PackSession Rent(PackableRegistry registry, PackedDocument document) {
        var session = t_spare ?? new PackSession();
        t_spare = null;
        (session.Registry, session.Document, session.Unregistered) = (registry, document, null);
        return session;
    }

    public void Return() {
        _ids.Clear();
        Queue.Clear();
        (Registry, Document, Unregistered) = (PackableRegistry.Empty, null!, null);
        t_spare = this;
    }

    // A ref to value, adding it as an object the first time it's seen.
    public PackedValue RefTo(IPackable? value) {
        if (value is null) { return PackedValue.Null; }
        if (!Registry.TryGet(value.GetType(), out var registration)) {
            Unregistered ??= value.GetType();
            return PackedValue.Null;
        }

        if (!_ids.TryGetValue(value, out var id)) {
            id = Document.AddObject(registration.VersionedName);
            _ids.Add(value, id);
            Queue.Add(value);
        }
        return PackedValue.Ref(id, registration.VersionedName);
    }

    private sealed class ReferenceComparer : IEqualityComparer<IPackable> {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(IPackable? x, IPackable? y) => ReferenceEquals(x, y);
        public int GetHashCode(IPackable obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
