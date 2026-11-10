using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>
/// Where a packed document keeps things: its key names and how a reference is told from plain data.
/// The reader knows a layout only through this, so another layout is another instance.
/// </summary>
public sealed class DocumentLayout {
    public required string Name { get; init; }
    public required string ContentTypeKey { get; init; }
    public required string VersionKey { get; init; }
    public required string RootKey { get; init; }
    public required string ObjectsKey { get; init; }
    public required string TypesKey { get; init; }

    /// <summary>Key inside an object that holds its type entry.</summary>
    public required string IsaKey { get; init; }
    public required string TypeKey { get; init; }
    public required string RefKey { get; init; }
    public required string TypeNameKey { get; init; }
    public required string NullObjectId { get; init; }

    public static readonly DocumentLayout V1 = new() {
        Name = "v1",
        ContentTypeKey = "Content-Type",
        VersionKey = "version",
        RootKey = "root",
        ObjectsKey = "objects",
        TypesKey = "types",
        IsaKey = "isa",
        TypeKey = "type",
        RefKey = "ref",
        TypeNameKey = "fullname",
        NullObjectId = "o:null",
    };

    /// <summary>
    /// A reference is known by shape alone: an object with exactly the type and ref keys, both text.
    /// Plain data can have that shape too.
    /// </summary>
    public bool IsReference(JToken? token, out string objectId) {
        objectId = "";
        if (token is not JObject { Count: 2 } o) { return false; }
        if (o[TypeKey] is not JValue { Type: JTokenType.String }) { return false; }
        if (o[RefKey] is not JValue { Type: JTokenType.String } id) { return false; }
        objectId = (string)id!;
        return true;
    }

    public string? TypeIdOf(JObject body) =>
        body[IsaKey] is JObject isa && isa[TypeKey] is JValue { Type: JTokenType.String } type ? (string?)type : null;
}

/// <param name="Listed">False when objects name this type but the types table has no entry for it.</param>
public sealed record PackedType(string Id, string? FullName, string Name, bool Listed) {
    public int Objects { get; set; }
}

/// <param name="From">The object holding the reference, or -1 for the root key of the document.</param>
public readonly record struct DanglingRef(int From, string Path, string TargetId);

/// <summary>
/// A packed document read as data only: objects, the references between them, the types table, the
/// root, and what the root reaches. It never creates the objects the document describes, so it is
/// safe on a document from anywhere. Vertices are objects and edges are references.
/// </summary>
public sealed class PackedDocument {
    // Parsing stops here, so the walks below can recurse.
    private const int MaxDepth = 64;

    private readonly Dictionary<string, int> _index = new();

    public required JObject Json { get; init; }
    public required DocumentLayout Layout { get; init; }
    public string? ContentType { get; private set; }
    public string? Version { get; private set; }

    /// <summary>What a packed document needs and this one lacks, in words. Empty when nothing is.</summary>
    public IReadOnlyList<string> Missing { get; private set; } = [];

    /// <summary>False when there is no objects table, so there is nothing to show.</summary>
    public bool HasObjects { get; private set; }

    // Objects, in document order.
    public int N { get; private set; }
    public string[] ObjectIds { get; private set; } = [];
    public JObject?[] Bodies { get; private set; } = [];
    public int[] TypeOf { get; private set; } = []; // index into Types, or -1
    public int NotObjects { get; private set; }     // entries of the objects table that are not objects
    public int Untyped { get; private set; }        // objects that do not say what type they are

    // The types table in document order, then types that objects name but the table lacks.
    public IReadOnlyList<PackedType> Types { get; private set; } = [];

    // References between objects, in document order.
    public int[] RefFrom { get; private set; } = [];
    public int[] RefStart { get; private set; } = [0]; // refs of object v are RefStart[v]..RefStart[v + 1]
    public int[] RefTo { get; private set; } = [];
    public string[] RefPath { get; private set; } = [];
    public IReadOnlyList<DanglingRef> Dangling { get; private set; } = [];
    public int NullRefs { get; private set; }

    public int Root { get; private set; } = -1;
    public string? RootId { get; private set; }

    // The trace from the root.
    public bool[] Reached { get; private set; } = [];
    public int ReachedCount { get; private set; }

    /// <summary>Whether Graph.Tarjan rooted at the root wrote exactly the reached objects; null without a root.</summary>
    public bool? TarjanAgrees { get; private set; }
    public int TarjanWritten { get; private set; }

    public int SharedCount { get; private set; } // objects referenced more than once
    public int LoopCount { get; private set; }   // groups of objects that reach themselves, self-references included
    public int LoopObjects { get; private set; }
    public bool[] InLoop { get; private set; } = [];

    public GraphModel? Graph { get; private set; }
    public TimeSpan ReadTime { get; private set; }

    /// <summary>
    /// Parses text strictly: date-looking strings stay strings, and a repeated key, text after the
    /// document or very deep nesting is an error rather than something silently resolved.
    /// </summary>
    public static JObject Parse(string text) {
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = MaxDepth };
        var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        while (reader.Read()) {
            if (reader.TokenType != JsonToken.Comment) { throw new JsonReaderException("There is more text after the document."); }
        }
        return token as JObject ?? throw new JsonReaderException($"The top level is {Kind(token)}, not an object.");
    }

    private static string Kind(JToken token) => token.Type switch {
        JTokenType.Object => "an object",
        JTokenType.Array => "an array",
        JTokenType.String => "text",
        JTokenType.Integer or JTokenType.Float => "a number",
        JTokenType.Boolean => "a boolean",
        JTokenType.Null => "null",
        _ => token.Type.ToString(),
    };

    public static PackedDocument Read(JObject json, DocumentLayout? layout = null, CancellationToken ct = default) {
        layout ??= DocumentLayout.V1;
        var sw = Stopwatch.StartNew();
        var doc = new PackedDocument {
            Json = json,
            Layout = layout,
            ContentType = Scalar(json[layout.ContentTypeKey]),
            Version = Scalar(json[layout.VersionKey]),
        };

        var missing = new List<string>();
        doc.Missing = missing;
        var objects = json[layout.ObjectsKey] as JObject;
        if (objects is null) {
            missing.Add(json.ContainsKey(layout.ObjectsKey) ? $"`{layout.ObjectsKey}` is not a table of objects" : $"no `{layout.ObjectsKey}`");
        }
        var hasRoot = layout.IsReference(json[layout.RootKey], out var rootId);
        if (!hasRoot) {
            missing.Add(json.ContainsKey(layout.RootKey) ? $"`{layout.RootKey}` is not a reference" : $"no `{layout.RootKey}`");
        }
        if (objects is null) { return doc; }
        doc.HasObjects = true;

        var n = doc.N = objects.Count;
        var ids = doc.ObjectIds = new string[n];
        var bodies = doc.Bodies = new JObject?[n];
        var index = doc._index;
        var next = 0;
        foreach (var p in objects.Properties()) {
            ids[next] = p.Name;
            bodies[next] = p.Value as JObject;
            index[p.Name] = next++;
        }

        var types = new List<PackedType>();
        var typeIndex = new Dictionary<string, int>();
        if (json[layout.TypesKey] is JObject table) {
            foreach (var p in table.Properties()) {
                var full = p.Value is JObject entry && entry[layout.TypeNameKey] is JValue { Type: JTokenType.String } name ? (string?)name : null;
                typeIndex[p.Name] = types.Count;
                types.Add(new PackedType(p.Name, full, full is null ? p.Name : ShortName(full), true));
            }
        }
        var typeOf = doc.TypeOf = new int[n];
        for (var v = 0; v < n; v++) {
            typeOf[v] = -1;
            if (bodies[v] is not { } body) { doc.NotObjects++; continue; }
            if (layout.TypeIdOf(body) is not { } typeId) { doc.Untyped++; continue; }
            if (!typeIndex.TryGetValue(typeId, out var t)) {
                t = typeIndex[typeId] = types.Count;
                types.Add(new PackedType(typeId, null, typeId, false));
            }
            typeOf[v] = t;
            types[t].Objects++;
        }
        doc.Types = types;
        ct.ThrowIfCancellationRequested();

        var from = new List<int>();
        var to = new List<int>();
        var paths = new List<string>();
        var dangling = new List<DanglingRef>();
        var nullRefs = 0;
        var refStart = doc.RefStart = new int[n + 1];
        for (var v = 0; v < n; v++) {
            refStart[v] = from.Count;
            if (bodies[v] is not { } body) { continue; }
            foreach (var p in body.Properties()) {
                if (p.Name != layout.IsaKey) { Walk(p.Value, p.Name, v); }
            }
        }
        refStart[n] = from.Count;
        doc.RefFrom = from.ToArray();
        doc.RefTo = to.ToArray();
        doc.RefPath = paths.ToArray();
        doc.NullRefs = nullRefs;
        doc.Dangling = dangling;

        if (hasRoot) {
            doc.RootId = rootId;
            if (rootId == layout.NullObjectId) {
                missing.Add($"`{layout.RootKey}` is null");
            } else if (index.TryGetValue(rootId, out var root)) {
                doc.Root = root;
            } else {
                missing.Add($"`{layout.RootKey}` points at {rootId}, which is not in `{layout.ObjectsKey}`");
                dangling.Add(new DanglingRef(-1, layout.RootKey, rootId));
            }
        }

        doc.Reached = new bool[n];
        doc.InLoop = new bool[n];
        if (n > 0) {
            ct.ThrowIfCancellationRequested();
            doc.Graph = GraphModel.Build(n, doc.RefFrom, doc.RefTo, ct: ct);
            doc.Trace();
        }
        doc.ReadTime = sw.Elapsed;
        return doc;

        void Walk(JToken token, string path, int owner) {
            if (layout.IsReference(token, out var id)) {
                if (id == layout.NullObjectId) {
                    nullRefs++;
                } else if (index.TryGetValue(id, out var target)) {
                    from.Add(owner);
                    to.Add(target);
                    paths.Add(path);
                } else {
                    dangling.Add(new DanglingRef(owner, path, id));
                }
                return;
            }
            switch (token) {
                case JObject o:
                    foreach (var p in o.Properties()) { Walk(p.Value, path + "/" + p.Name, owner); }
                    break;
                case JArray a:
                    for (var k = 0; k < a.Count; k++) { Walk(a[k], $"{path}[{k}]", owner); }
                    break;
            }
        }
    }

    private void Trace() {
        var g = Graph!;
        var n = N;

        // The walk the reader does itself, from the root.
        if (Root >= 0) {
            var stack = new Stack<int>();
            Reached[Root] = true;
            stack.Push(Root);
            while (stack.Count > 0) {
                var v = stack.Pop();
                ReachedCount++;
                foreach (var w in g.OutNeighbours(v)) {
                    if (Reached[w]) { continue; }
                    Reached[w] = true;
                    stack.Push(w);
                }
            }

            // The independent check: Tarjan rooted at the root writes exactly what the root reaches.
            var graph = new Graph().EnsureIndex(n - 1);
            for (var i = 0; i < RefFrom.Length; i++) { graph.AddEdge(RefFrom[i], RefTo[i]); }
            var order = new int[n];
            var segments = new Segment[n];
            var components = graph.Tarjan([Root], order, segments);
            var written = 0;
            for (var s = 0; s < components; s++) { written += segments[s].Size; }
            var agrees = written == ReachedCount;
            for (var i = 0; i < written && agrees; i++) { agrees = Reached[order[i]]; }
            TarjanWritten = written;
            TarjanAgrees = agrees;
        }

        for (var v = 0; v < n; v++) {
            if (g.InNeighbours(v).Length > 1) { SharedCount++; }
            if (g.IslandSize[g.IslandOf[v]] > 1) { InLoop[v] = true; }
        }
        LoopCount = g.NonTrivialIslands;
        for (var i = 0; i < RefFrom.Length; i++) {
            var v = RefFrom[i];
            if (v != RefTo[i] || InLoop[v]) { continue; }
            InLoop[v] = true;
            LoopCount++;
        }
        foreach (var inLoop in InLoop) { if (inLoop) { LoopObjects++; } }
    }

    public string TypeName(int v) => TypeOf[v] < 0 ? "(no type)" : Types[TypeOf[v]].Name;

    /// <summary>The envelope as found. Nothing here is required: documents without a content type are read too.</summary>
    public string Envelope =>
        (ContentType is null ? $"no {Layout.ContentTypeKey}" : $"{Layout.ContentTypeKey} {ContentType}") +
        (Version is null ? $", no {Layout.VersionKey}" : $", {Layout.VersionKey} {Version}");

    public bool TryGetIndex(string id, out int v) => _index.TryGetValue(id, out v);

    /// <summary>The entries of one object as indented lines, references written as arrows.</summary>
    public string BodyText(int v, int maxLines = 120) {
        if (Bodies[v] is not { } body) { return "  (not an object)"; }
        var sb = new StringBuilder();
        var lines = 0;
        foreach (var p in body.Properties()) {
            if (p.Name != Layout.IsaKey) { Write(sb, p.Name, p.Value, 1, ref lines, maxLines); }
        }
        if (lines == 0) { return "  (no entries)"; }
        if (lines > maxLines) { sb.Append("  … ").Append(lines - maxLines).Append(" more lines\n"); }
        return sb.ToString(0, sb.Length - 1);
    }

    private void Write(StringBuilder sb, string key, JToken value, int depth, ref int lines, int maxLines) {
        if (lines++ >= maxLines) { return; }
        sb.Append(' ', depth * 2).Append(key);
        if (Inline(value) is { } text) {
            sb.Append("  ").Append(text).Append('\n');
            return;
        }
        sb.Append('\n');
        switch (value) {
            case JObject o:
                foreach (var p in o.Properties()) { Write(sb, p.Name, p.Value, depth + 1, ref lines, maxLines); }
                break;
            case JArray a:
                for (var k = 0; k < a.Count; k++) { Write(sb, $"[{k}]", a[k], depth + 1, ref lines, maxLines); }
                break;
        }
    }

    // One line for a value, or null when it needs lines of its own.
    private string? Inline(JToken value) {
        if (Layout.IsReference(value, out var id)) { return RefText(id); }
        switch (value) {
            case JValue scalar:
                var text = scalar.ToString(Formatting.None);
                return text.Length <= 100 ? text : text[..100] + "…";
            case JObject { Count: 0 }:
                return "{}";
            case JArray { Count: 0 }:
                return "[]";
            case JArray { Count: <= 8 } a:
                var parts = new List<string>(a.Count);
                foreach (var item in a) {
                    if (item is JArray || (item is JObject && !Layout.IsReference(item, out _))) { return null; }
                    parts.Add(Inline(item)!);
                }
                var joined = "[" + string.Join(", ", parts) + "]";
                return joined.Length <= 72 ? joined : null;
            default:
                return null;
        }
    }

    public string RefText(string id) =>
        id == Layout.NullObjectId ? "-> null" : _index.ContainsKey(id) ? "-> " + id : "-> " + id + "  (no such object)";

    private static string? Scalar(JToken? token) => token is JValue value ? value.ToString() : null;

    // "Space.Outer+Name, Assembly, Version=..." -> "Name"
    private static string ShortName(string full) {
        var end = full.IndexOfAny([',', '[', '`']);
        var name = (end < 0 ? full : full[..end]).Trim();
        name = name[(name.LastIndexOfAny(['.', '+']) + 1)..];
        return name.Length > 0 ? name : full;
    }
}
