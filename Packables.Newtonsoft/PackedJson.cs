using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables;

public enum PackedJsonErrorKind {
    /// <summary>Not JSON, or JSON of the wrong shape.</summary>
    Malformed,
    ContentType,
    /// <summary>The version is missing, or not one this build reads.</summary>
    Version,
    /// <summary>A format 1 document, which needs converting first.</summary>
    Format1,
    /// <summary>A "$" key the format doesn't define.</summary>
    UnknownTag,
    /// <summary>A JSON object or array used as a value without a "$" wrapper.</summary>
    BareContainer,
    DuplicateKey,
    NoRoot,
    TooDeep,
    /// <summary>A date value in a tree parsed elsewhere: the parser has already rewritten it.</summary>
    DateValue,
}

public readonly struct PackedJsonError(PackedJsonErrorKind kind, string path) {
    public readonly PackedJsonErrorKind Kind = kind;

    /// <summary>Where in the JSON, as a JSON path.</summary>
    public readonly string Path = path;

    public override string ToString() => $"{Kind} at '{Path}'";
}

/// <summary>
/// Format 3 as JSON. Plain values are plain JSON. Everything else is wrapped, and "$" keys belong to
/// the format:
/// <code>
/// ref    { "$ref": "id", "$type": "Name#Version" }       (also the root)
/// a ref to a missing object  { "$ref": null, "$type": "Name#Version" }
/// map    { "$map": { key: value, ... } }
/// array  { "$array": [ value, ... ] }
/// object { "$type": "Name#Version", "$fields": { key: value, ... } }
/// NaN and the infinities  { "$double": "NaN" | "Infinity" | "-Infinity" }
/// </code>
/// The top level holds "Content-Type", "version" (3), "root" and "objects", a map from each object's
/// id to the object.
/// <list type="bullet">
/// <item>Reading takes any non-empty ids, so joined or hand-edited text needn't be renumbered first.
/// Objects are indexed in the order "objects" lists them, and a ref to an id no object has reads as a
/// ref to a missing object.</item>
/// <item>Writing keeps the ids a document was read with, in its order, so reading and writing again
/// gives the same text. A packed or converted document has none, and its ids are written "o:0",
/// "o:1" and on from its indices, which number objects breadth-first from the root.</item>
/// </list>
/// </summary>
public static class PackedJson {
    public const int FormatVersion = 3;
    public const int DefaultMaxDepth = 64;

    public static string Write(PackedDocument document, string contentType, bool indented = true) {
        var text = new StringWriter { NewLine = "\n" };
        Write(document, text, contentType, indented);
        return text.ToString();
    }

    public static void Write(PackedDocument document, TextWriter text, string contentType, bool indented = true) {
        if (!document.IsSealed) { throw new ArgumentException("Seal the document first.", nameof(document)); }
        using var writer = new JsonTextWriter(text) { Formatting = indented ? Formatting.Indented : Formatting.None, CloseOutput = false };
        writer.WriteStartObject();
        writer.WritePropertyName("Content-Type");
        writer.WriteValue(contentType);
        writer.WritePropertyName("version");
        writer.WriteValue(FormatVersion);
        writer.WritePropertyName("root");
        _writeValue(writer, document, document.Root);
        writer.WritePropertyName("objects");
        writer.WriteStartObject();
        for (var i = 0; i < document.ObjectCount; i++) {
            writer.WritePropertyName(_id(document, i));
            writer.WriteStartObject();
            writer.WritePropertyName("$type");
            writer.WriteValue(document.ObjectType(i));
            writer.WritePropertyName("$fields");
            _writeEntries(writer, document, document.ObjectFields(i));
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string _id(PackedDocument document, int index) => document.ObjectId(index) ?? _indexId(index);

    // "o:N" ids are the same on every write, so the first few thousand are made once and shared;
    // past that, each is made in one allocation. Threads that grow the table at once each build a
    // whole one, and either may win.
    private const int SharedIds = 16384;
    private static string[] s_ids = [];

    private static string _indexId(int index) {
        var ids = Volatile.Read(ref s_ids);
        if (index < ids.Length) { return ids[index]; }
        if (index >= SharedIds) { return _makeId(index); }

        var grown = new string[Math.Min(SharedIds, Math.Max(Math.Max(index + 1, ids.Length * 2), 64))];
        System.Array.Copy(ids, grown, ids.Length);
        for (var i = ids.Length; i < grown.Length; i++) { grown[i] = _makeId(i); }
        Volatile.Write(ref s_ids, grown);
        return grown[index];
    }

    private static string _makeId(int index) {
        var digits = 1;
        for (var rest = index; rest >= 10; rest /= 10) { digits++; }
        return string.Create(2 + digits, index, static (chars, value) => {
            chars[0] = 'o';
            chars[1] = ':';
            for (var at = chars.Length - 1; at >= 2; at--, value /= 10) { chars[at] = (char)('0' + value % 10); }
        });
    }

    private static void _writeEntries(JsonWriter writer, PackedDocument document, int container) {
        var keyed = document.IsMap(container);
        if (keyed) { writer.WriteStartObject(); } else { writer.WriteStartArray(); }
        foreach (var entry in document.Entries(container)) {
            if (keyed) { writer.WritePropertyName(entry.Key!); }
            _writeValue(writer, document, entry.Value);
        }
        if (keyed) { writer.WriteEndObject(); } else { writer.WriteEndArray(); }
    }

    private static void _writeValue(JsonWriter writer, PackedDocument document, PackedValue value) {
        switch (value.Kind) {
            case PackedKind.Null: writer.WriteNull(); break;
            case PackedKind.Bool: writer.WriteValue(value.AsBool); break;
            case PackedKind.Integer: writer.WriteValue(value.AsInteger); break;
            case PackedKind.String: writer.WriteValue(value.AsString); break;
            case PackedKind.Double:
                var number = value.AsDouble;
                if (double.IsNaN(number) || double.IsInfinity(number)) {
                    writer.WriteStartObject();
                    writer.WritePropertyName("$double");
                    writer.WriteValue(double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity");
                    writer.WriteEndObject();
                } else {
                    writer.WriteValue(number);
                }
                break;
            case PackedKind.Ref:
                writer.WriteStartObject();
                writer.WritePropertyName("$ref");
                if ((uint)value.Target < (uint)document.ObjectCount) { writer.WriteValue(_id(document, value.Target)); } else { writer.WriteNull(); }
                writer.WritePropertyName("$type");
                writer.WriteValue(value.RefType);
                writer.WriteEndObject();
                break;
            case PackedKind.Map or PackedKind.Array:
                writer.WriteStartObject();
                writer.WritePropertyName(value.Kind == PackedKind.Map ? "$map" : "$array");
                _writeEntries(writer, document, value.Container);
                writer.WriteEndObject();
                break;
        }
    }

    /// <summary>
    /// Reads a format 3 document into <paramref name="document"/>, which is cleared first and sealed on
    /// success. The content type matches with or without a "+json" suffix. Nothing is rewritten on the
    /// way in: dates stay strings.
    /// </summary>
    public static bool TryRead(string json, PackedDocument document, string contentType, out PackedJsonError error,
        int maxDepth = DefaultMaxDepth) => TryRead(new StringReader(json), document, contentType, out error, maxDepth);

    public static bool TryRead(Stream utf8, PackedDocument document, string contentType, out PackedJsonError error,
        int maxDepth = DefaultMaxDepth) => TryRead(new StreamReader(utf8, Encoding.UTF8, true, 4096, true), document, contentType, out error, maxDepth);

    public static bool TryRead(TextReader text, PackedDocument document, string contentType, out PackedJsonError error,
        int maxDepth = DefaultMaxDepth) {
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = null, CloseInput = false };
        return new Decoder(reader, document, contentType, maxDepth).Run(out error);
    }

    /// <summary>
    /// Reads a document from a JSON tree parsed elsewhere. A date value in it is an error: the parser
    /// has already changed it, so the original text is lost.
    /// </summary>
    public static bool TryRead(JToken parsed, PackedDocument document, string contentType, out PackedJsonError error,
        int maxDepth = DefaultMaxDepth) {
        using var reader = new JTokenReader(parsed) { MaxDepth = null };
        return new Decoder(reader, document, contentType, maxDepth).Run(out error);
    }

    private static string _baseType(string contentType) =>
        contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ? contentType.Substring(0, contentType.Length - 5) : contentType;

    private sealed class Decoder(JsonReader reader, PackedDocument document, string contentType, int maxDepth) {
        private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal); // Ids met so far.
        private PackedJsonError? _error;

        public bool Run(out PackedJsonError error) {
            document.Clear();
            try {
                _readDocument();
                // Nothing may follow the document. (Newtonsoft throws on extra text, landing below.)
                if (_error is null && reader.Read()) { _fail(PackedJsonErrorKind.Malformed, reader.Path); }
            } catch (JsonReaderException e) {
                _fail(PackedJsonErrorKind.Malformed, e.Path ?? "");
            }
            if (_error is null && !document.TrySeal(out _, out var key)) { _fail(PackedJsonErrorKind.DuplicateKey, key ?? ""); }
            // Objects are indexed as their ids are first met, refs included; this puts them in the
            // order the text lists them. Written text lists them in the order they're first met.
            if (_error is null) { document.OrderAsDefined(); }
            if (_error is not null) { document.Clear(); } // Nothing half-read is left, reserved objects included.
            error = _error ?? default;
            return _error is null;
        }

        // The index for an id, reserving one the first time the id is met.
        private int _index(string id) {
            if (!_indices.TryGetValue(id, out var index)) { _indices.Add(id, index = document.ReserveObject(id)); }
            return index;
        }

        private void _readDocument() {
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }

            string? foundType = null;
            long? version = null;
            PackedValue? root = null;
            var seen = 0; // One bit per known key, so a repeat is caught.
            while (_error is null && reader.Read() && reader.TokenType == JsonToken.PropertyName) {
                var bit = (string)reader.Value! switch { "Content-Type" => 1, "version" => 2, "root" => 4, "objects" => 8, _ => 0 };
                if ((seen & bit) != 0) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
                seen |= bit;
                switch ((string)reader.Value!) {
                    case "Content-Type":
                        foundType = reader.ReadAsString();
                        break;
                    case "version":
                        reader.Read();
                        if (reader.TokenType != JsonToken.Integer || reader.Value is not (long or int)) { _fail(PackedJsonErrorKind.Version, reader.Path); return; }
                        version = Convert.ToInt64(reader.Value);
                        if (version == 1) { _fail(PackedJsonErrorKind.Format1, reader.Path); return; }
                        if (version != FormatVersion) { _fail(PackedJsonErrorKind.Version, reader.Path); return; }
                        break;
                    case "root":
                        reader.Read();
                        root = _readValue(1);
                        break;
                    case "objects":
                        _readObjects();
                        break;
                    default:
                        reader.Read();
                        reader.Skip(); // Unknown top-level keys are left for later formats.
                        break;
                }
            }
            if (_error is not null) { return; }
            if (reader.TokenType != JsonToken.EndObject) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }

            if (version is null) { _fail(PackedJsonErrorKind.Version, ""); return; }
            if (foundType is null || !string.Equals(_baseType(foundType), _baseType(contentType), StringComparison.Ordinal)) {
                _fail(PackedJsonErrorKind.ContentType, "Content-Type");
                return;
            }
            if (root is not { Kind: PackedKind.Ref }) { _fail(PackedJsonErrorKind.NoRoot, "root"); return; }
            document.SetRoot(root.Value);
        }

        private void _readObjects() {
            reader.Read();
            if (reader.TokenType != JsonToken.StartObject) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
            while (_error is null && reader.Read() && reader.TokenType == JsonToken.PropertyName) {
                var id = (string)reader.Value!;
                if (id.Length == 0) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
                var index = _index(id);
                if (document.IsDefined(index)) { _fail(PackedJsonErrorKind.DuplicateKey, reader.Path); return; }

                reader.Read();
                if (reader.TokenType != JsonToken.StartObject) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
                string? type = null;
                var hasFields = false;
                // "$fields" may come before "$type", so the fields go in a map that's attached afterwards.
                var fields = document.AddMap();
                while (_error is null && reader.Read() && reader.TokenType == JsonToken.PropertyName) {
                    var name = (string)reader.Value!;
                    if (name == "$type" && type is null) {
                        type = reader.ReadAsString();
                    } else if (name == "$fields" && !hasFields) {
                        reader.Read();
                        if (reader.TokenType != JsonToken.StartObject) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
                        _readEntries(fields, keyed: true, depth: 2);
                        hasFields = true;
                    } else {
                        _fail(_isTag(name) && name is not ("$type" or "$fields") ? PackedJsonErrorKind.UnknownTag : PackedJsonErrorKind.Malformed, reader.Path);
                        return;
                    }
                }
                if (_error is not null) { return; }
                if (type is null) { _fail(PackedJsonErrorKind.Malformed, reader.Path); return; }
                document.DefineObject(index, type, fields);
            }
        }

        // Reads entries up to the closing token of the container just opened.
        private void _readEntries(int container, bool keyed, int depth) {
            while (_error is null && reader.Read()) {
                if (reader.TokenType is JsonToken.EndObject or JsonToken.EndArray) { return; }
                if (keyed) {
                    var key = (string)reader.Value!;
                    reader.Read();
                    var value = _readValue(depth);
                    if (_error is null) { document.Add(container, key, value); }
                } else {
                    var value = _readValue(depth);
                    if (_error is null) { document.Add(container, value); }
                }
            }
        }

        private PackedValue _readValue(int depth) {
            if (depth > maxDepth) {
                _fail(PackedJsonErrorKind.TooDeep, reader.Path);
                return PackedValue.Null;
            }

            switch (reader.TokenType) {
                case JsonToken.Null: return PackedValue.Null;
                case JsonToken.Boolean: return PackedValue.Bool((bool)reader.Value!);
                case JsonToken.Integer:
                    if (reader.Value is long or int) { return PackedValue.Integer(Convert.ToInt64(reader.Value)); }
                    _fail(PackedJsonErrorKind.Malformed, reader.Path); // Too big for 64 bits.
                    return PackedValue.Null;
                case JsonToken.Float: return PackedValue.Double(Convert.ToDouble(reader.Value));
                case JsonToken.String: return PackedValue.String((string)reader.Value!);
                case JsonToken.Date:
                    _fail(PackedJsonErrorKind.DateValue, reader.Path);
                    return PackedValue.Null;
                case JsonToken.StartArray:
                    _fail(PackedJsonErrorKind.BareContainer, reader.Path);
                    return PackedValue.Null;
                case JsonToken.StartObject:
                    return _readWrapped(depth);
                default:
                    _fail(PackedJsonErrorKind.Malformed, reader.Path);
                    return PackedValue.Null;
            }
        }

        private PackedValue _readWrapped(int depth) {
            if (!reader.Read() || reader.TokenType != JsonToken.PropertyName) {
                _fail(PackedJsonErrorKind.BareContainer, reader.Path);
                return PackedValue.Null;
            }

            var tag = (string)reader.Value!;
            PackedValue result;
            switch (tag) {
                case "$ref" or "$type":
                    return _readRef(tag);
                case "$map" or "$array":
                    reader.Read();
                    var keyed = tag == "$map";
                    if (reader.TokenType != (keyed ? JsonToken.StartObject : JsonToken.StartArray)) {
                        _fail(PackedJsonErrorKind.Malformed, reader.Path);
                        return PackedValue.Null;
                    }
                    var container = keyed ? document.AddMap() : document.AddArray();
                    _readEntries(container, keyed, depth + 1);
                    result = keyed ? PackedValue.Map(container) : PackedValue.Array(container);
                    break;
                case "$double":
                    result = reader.ReadAsString() switch {
                        "NaN" => PackedValue.Double(double.NaN),
                        "Infinity" => PackedValue.Double(double.PositiveInfinity),
                        "-Infinity" => PackedValue.Double(double.NegativeInfinity),
                        _ => _failValue(PackedJsonErrorKind.Malformed),
                    };
                    break;
                default:
                    return _failValue(_isTag(tag) ? PackedJsonErrorKind.UnknownTag : PackedJsonErrorKind.BareContainer);
            }

            // A wrapper holds its one tag and nothing else. A second "$" key may be metadata a later build
            // added, so it's reported as such.
            if (_error is null && (!reader.Read() || reader.TokenType != JsonToken.EndObject)) {
                return _failValue(reader.TokenType == JsonToken.PropertyName && _isTag((string)reader.Value!)
                    ? PackedJsonErrorKind.UnknownTag : PackedJsonErrorKind.Malformed);
            }
            return result;
        }

        private PackedValue _readRef(string first) {
            int? target = null;
            string? type = null;
            var name = first;
            while (true) {
                if (name == "$ref" && target is null) {
                    reader.Read();
                    if (reader.TokenType == JsonToken.Null) {
                        target = -1; // Written for a ref whose object was missing already.
                    } else if (reader.TokenType == JsonToken.String && reader.Value is string { Length: > 0 } id) {
                        target = _index(id);
                    } else {
                        return _failValue(PackedJsonErrorKind.Malformed);
                    }
                } else if (name == "$type" && type is null) {
                    type = reader.ReadAsString();
                } else {
                    return _failValue(_isTag(name) && name is not ("$ref" or "$type") ? PackedJsonErrorKind.UnknownTag : PackedJsonErrorKind.Malformed);
                }

                if (!reader.Read()) { return _failValue(PackedJsonErrorKind.Malformed); }
                if (reader.TokenType == JsonToken.EndObject) { break; }
                name = (string)reader.Value!;
            }

            if (target is null || type is null) { return _failValue(PackedJsonErrorKind.Malformed); }
            // A ref to an id no object has ends up a ref to a missing object (-1) once reading is done;
            // the Unpacker records it when a type reads it, and the rest of the document still loads.
            return PackedValue.Ref(target.Value, type);
        }

        private static bool _isTag(string name) => name.StartsWith("$", StringComparison.Ordinal);

        private PackedValue _failValue(PackedJsonErrorKind kind) {
            _fail(kind, reader.Path);
            return PackedValue.Null;
        }

        private void _fail(PackedJsonErrorKind kind, string path) => _error ??= new PackedJsonError(kind, path);
    }
}
