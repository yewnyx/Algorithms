using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables;

/// <summary>
/// Turns a format 1 document into the format 3 model, which can then be unpacked or written as
/// format 3. The rules:
/// <list type="bullet">
/// <item>A type whose assembly-qualified name is a registered type's, or one of its aliases, becomes
/// "Name#0": version 0 is format 1's layout. Any other name is kept as it was, so unpacking reports it
/// as an unknown type.</item>
/// <item>An object holding exactly string "type" and "ref" keys is a ref, "o:null" a null ref, and a
/// ref to an id with no object a missing one. Other objects become maps and arrays stay arrays.</item>
/// <item>Each object's "isa" entry gives its type and isn't kept as a field.</item>
/// <item>Numbers, strings, booleans and nulls carry over; dates stay strings. Doubles are normalised,
/// so -0.0 becomes 0.0.</item>
/// <item>Objects are numbered breadth-first from the root, and ones it doesn't reach are dropped.</item>
/// <item>Nesting is limited as <see cref="PackedJson"/> limits it, so whatever converts reads back as
/// format 3 at the same maxDepth.</item>
/// </list>
/// </summary>
public static class Format1Converter {
    /// <inheritdoc cref="Format1Converter"/>
    public static bool TryConvert(string json, PackedDocument document, string contentType, PackableRegistry registry,
        out PackedJsonError error, int maxDepth = PackedJson.DefaultMaxDepth) {
        PackedJson.CheckMaxDepth(maxDepth);
        document.Clear(); // Even text that doesn't parse leaves no earlier document behind.
        JToken token;
        try {
            using var reader = new JsonTextReader(new StringReader(json)) {
                // Loading a tree doesn't recurse, so depth is left to the converter, which reports TooDeep.
                DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = null,
            };
            token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) { return _fail(PackedJsonErrorKind.Malformed, reader.Path, out error); }
        } catch (JsonReaderException e) {
            return _fail(PackedJsonErrorKind.Malformed, e.Path ?? "", out error);
        }
        if (token is not JObject format1) { return _fail(PackedJsonErrorKind.Malformed, "", out error); }
        return TryConvert(format1, document, contentType, registry, out error, maxDepth);
    }

    /// <summary>
    /// Converts a format 1 document parsed elsewhere. A date value in it is an error, so pass text when
    /// you can: a default parse turns date-like strings into dates.
    /// </summary>
    public static bool TryConvert(JObject format1, PackedDocument document, string contentType, PackableRegistry registry,
        out PackedJsonError error, int maxDepth = PackedJson.DefaultMaxDepth) {
        PackedJson.CheckMaxDepth(maxDepth);
        document.Clear();
        var converter = new Converter(format1, document, registry, maxDepth);
        return converter.Run(contentType, out error);
    }

    private static bool _fail(PackedJsonErrorKind kind, string path, out PackedJsonError error) {
        error = new PackedJsonError(kind, path);
        return false;
    }

    private static string _baseType(string contentType) =>
        contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ? contentType.Substring(0, contentType.Length - 5) : contentType;

    private sealed class Converter(JObject json, PackedDocument document, PackableRegistry registry, int maxDepth) {
        private readonly Dictionary<string, int> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typeNames = new(StringComparer.Ordinal); // "t:N" -> format 3 name
        private readonly Dictionary<string, PackableRegistration> _claimed = new(StringComparer.Ordinal);
        private PackedJsonError? _error;

        public bool Run(string contentType, out PackedJsonError error) {
            if (json["Content-Type"] is not JValue { Type: JTokenType.String } found ||
                !string.Equals(_baseType((string)found!), _baseType(contentType), StringComparison.Ordinal)) {
                return _fail(PackedJsonErrorKind.ContentType, "Content-Type", out error);
            }
            // Format 1 writers always wrote 1; a document without a version was read as 1 too.
            if (json["version"] is { } version && !(version is JValue { Type: JTokenType.Integer } v && v.Value is long number && number == 1)) {
                return _fail(PackedJsonErrorKind.Version, "version", out error);
            }
            if (json["objects"] is not JObject objects) { return _fail(PackedJsonErrorKind.Malformed, "objects", out error); }

            _readTypes(json["types"] as JObject);
            var bodies = new List<(int Index, JObject Body)>();
            foreach (var property in objects.Properties()) {
                var body = property.Value as JObject;
                var typeId = body?["isa"] is JObject isa && isa["type"] is JValue { Type: JTokenType.String } t ? (string)t! : null;
                var index = document.AddObject(_typeName(typeId));
                _objects[property.Name] = index;
                if (body is not null) { bodies.Add((index, body)); }
            }

            foreach (var (index, body) in bodies) {
                foreach (var property in body.Properties()) {
                    if (property.Name == "isa") { continue; }
                    var value = _convert(property.Value, FieldDepth);
                    if (_error is not null) { break; }
                    document.Add(document.ObjectFields(index), property.Name, value);
                }
            }

            var root = _convert(json["root"] ?? JValue.CreateNull(), RootDepth);
            if (_error is null && root.Kind != PackedKind.Ref) { _error = new PackedJsonError(PackedJsonErrorKind.NoRoot, "root"); }
            if (_error is not null) {
                error = _error.Value;
                return false;
            }

            document.SetRoot(root);
            document.TrySeal(out _, out _); // JSON objects can't repeat a key here: parsing refuses it.
            document.Canonicalize();
            error = default;
            return true;
        }

        private void _readTypes(JObject? types) {
            // Old names that registrations claim: their own type's name, and their aliases. The
            // registry refuses two registrations claiming one name.
            foreach (var registration in registry.Registrations) {
                if (registration.Type.AssemblyQualifiedName is { } name) { _claimed[name] = registration; }
                foreach (var alias in registration.Aliases) { _claimed[alias] = registration; }
            }
            if (types is null) { return; }

            foreach (var property in types.Properties()) {
                if (property.Value is not JObject entry || entry["fullname"] is not JValue { Type: JTokenType.String } fullName) { continue; }
                _typeNames[property.Name] = _claimedName((string)fullName!);
            }
        }

        private string _claimedName(string old) =>
            _claimed.TryGetValue(old, out var registration) ? PackableNames.Format(registration.Name, 0) : old;

        // Format 3's name for a format 1 type id. An id the types table doesn't list, such as a
        // built-in one, is matched against the claimed names itself, or kept as it was.
        private string _typeName(string? typeId) =>
            typeId is null ? "" : _typeNames.TryGetValue(typeId, out var name) ? name : _claimedName(typeId);

        // Depths count as PackedJson's reader counts them, so whatever converts also reads back as format 3.
        private const int RootDepth = 1, FieldDepth = 2;

        private PackedValue _convert(JToken token, int depth) {
            if (depth > maxDepth) {
                _error ??= new PackedJsonError(PackedJsonErrorKind.TooDeep, token.Path);
                return PackedValue.Null;
            }
            switch (token) {
                case JValue value:
                    switch (value.Type) {
                        case JTokenType.Null: return PackedValue.Null;
                        case JTokenType.Boolean: return PackedValue.Bool((bool)value);
                        case JTokenType.Integer when _asLong(value.Value, out var integer): return PackedValue.Integer(integer);
                        case JTokenType.Float: return PackedValue.Double((double)value);
                        case JTokenType.String: return PackedValue.String((string?)value);
                        case JTokenType.Date:
                            _error ??= new PackedJsonError(PackedJsonErrorKind.DateValue, value.Path);
                            return PackedValue.Null;
                        default:
                            _error ??= new PackedJsonError(PackedJsonErrorKind.Malformed, value.Path);
                            return PackedValue.Null;
                    }
                // Format 1's packer only ever wrote "o:N" ids, so a two-string map without one is data.
                case JObject o when o.Count == 2 && o["type"] is JValue { Type: JTokenType.String } type &&
                                    o["ref"] is JValue { Type: JTokenType.String } id && ((string)id!).StartsWith("o:", StringComparison.Ordinal):
                    if ((string)id! == "o:null") { return PackedValue.Null; }
                    return PackedValue.Ref(_objects.TryGetValue((string)id!, out var target) ? target : -1, _typeName((string)type!));
                case JObject o: {
                    var map = document.AddMap();
                    foreach (var property in o.Properties()) { document.Add(map, property.Name, _convert(property.Value, depth + 1)); }
                    return PackedValue.Map(map);
                }
                case JArray a: {
                    var array = document.AddArray();
                    foreach (var element in a) { document.Add(array, _convert(element, depth + 1)); }
                    return PackedValue.Array(array);
                }
                default:
                    _error ??= new PackedJsonError(PackedJsonErrorKind.Malformed, token.Path);
                    return PackedValue.Null;
            }
        }

        // Any integer a tree can hold, as long as it fits 64 bits. A BigInteger never does here.
        private static bool _asLong(object? value, out long result) {
            result = 0;
            if (value is not IConvertible) { return false; }
            try {
                result = Convert.ToInt64(value);
                return true;
            } catch (OverflowException) {
                return false;
            }
        }
    }
}
