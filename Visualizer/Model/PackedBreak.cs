using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Algorithms.Visualizer.Model;

public enum BreakKind {
    RemoveTypeEntry, RenameType, NameOtherType, VersionPlusOne, ContentType, DangleRef, DeleteObject,
    WrongKind, CorruptValue, DropTypes, RefIdNotText, BadAssemblyName,
}

public sealed record BreakPreset(BreakKind Kind, string Label, string Hint);

/// <summary>What one change to the generated document did to unpacking it.</summary>
public sealed record BreakResult {
    /// <summary>The changed document, read as data.</summary>
    public required PackedDocument Document { get; init; }
    /// <summary>What was changed, as a sentence, or why the change could not be made.</summary>
    public required string Change { get; init; }
    public bool Applied { get; init; }
    /// <summary>Id of the object to point out, or null when the change is to the envelope or the types.</summary>
    public string? ChangedId { get; init; }
    /// <summary>What TryUnpack returned, or null if it threw.</summary>
    public bool? Returned { get; init; }
    public string? Exception { get; init; }
    /// <summary>Whether the library handed to TryUnpack was written to.</summary>
    public bool Touched { get; init; }
    /// <summary>Objects reachable from that library afterwards that had a name or title read into them.</summary>
    public int Filled { get; init; }
    /// <summary>Objects reachable from it that were created but had none.</summary>
    public int Blank { get; init; }
    /// <summary>Objects in the document before the change.</summary>
    public int Original { get; init; }
    /// <summary>Assemblies the runtime loaded while the unpack ran.</summary>
    public IReadOnlyList<string> Loaded { get; init; } = [];
}

/// <summary>
/// Preset changes to the generated document, each followed by an unpack into the demo classes, to
/// show what the library does with a document that is not what it wrote. Like the round trip, this
/// starts from a spec: a document from a file or an address cannot be passed in.
/// </summary>
public static class PackedBreak {
    private const string Untouched = "(not unpacked)";
    private const string OtherType = "System.Net.Mail.MailAddress, System.Net.Mail";
    private const string OtherAssembly = "System.Net.Mail";
    private const string MovedType = "Yewnyx.Algorithms.Visualizer.Moved.Book, NoSuchAssembly";
    private const string BadName = "Yewnyx.Algorithms.Visualizer.Model.Book, =,=";

    public static readonly IReadOnlyList<BreakPreset> Presets = [
        new(BreakKind.RemoveTypeEntry, "Remove a type entry", "Takes the entry for Book out of the types table."),
        new(BreakKind.RenameType, "Rename a type", "Gives Book a name no assembly has, as if the class had moved."),
        new(BreakKind.NameOtherType, "Name another type", $"Points Book's entry at {OtherType}."),
        new(BreakKind.VersionPlusOne, "Version + 1", "Raises the document's version by one."),
        new(BreakKind.ContentType, "Content type", "Changes the Content-Type to application/json."),
        new(BreakKind.DangleRef, "Dangle a ref", "Points a shelf's first book at an id no object has."),
        new(BreakKind.DeleteObject, "Delete an object", "Removes a book from the objects table and leaves the references to it."),
        new(BreakKind.WrongKind, "Wrong kind of ref", "Points a shelf's first book at an author."),
        new(BreakKind.CorruptValue, "Corrupt a value", "Replaces a book's page count with text."),
        new(BreakKind.DropTypes, "Drop the types table", "Removes the types table."),
        new(BreakKind.RefIdNotText, "Ref id not text", "Makes the id inside a reference an array."),
        new(BreakKind.BadAssemblyName, "Bad assembly name", "Gives Book's entry an assembly name that cannot be parsed."),
    ];

    /// <summary>
    /// Generates the library for <paramref name="spec"/>, packs it, makes one change to the text's
    /// document and unpacks the result into a new library.
    /// </summary>
    public static BreakResult Run(DemoSpec spec, BreakKind kind, CancellationToken ct = default) {
        var packed = PackedDemo.Pack(PackedDemo.Generate(spec));
        var json = PackedDocument.Parse(packed.ToString(Formatting.None));
        var original = (json["objects"] as JObject)?.Count ?? 0;
        var (change, changedId) = Apply(json, kind);
        var text = json.ToString(Formatting.None);
        var document = PackedDocument.Read(PackedDocument.Parse(text), ct: ct);
        var result = new BreakResult {
            Document = document, Change = change ?? "", Applied = change != null, ChangedId = changedId, Original = original,
        };
        if (change == null) { return result with { Change = "This document has no shelf with a book on it, so there is nothing to change." }; }
        ct.ThrowIfCancellationRequested();

        var target = new Library { Name = Untouched };
        target.Shelves[Untouched] = new Shelf();
        var loaded = new List<string>();
        void OnLoad(object? sender, AssemblyLoadEventArgs e) {
            lock (loaded) { loaded.Add(e.LoadedAssembly.GetName().Name ?? "(unnamed)"); }
        }

        bool? returned = null;
        string? exception = null;
        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
        try {
            // The unpacker gets its own copy, so nothing it does can alter the document shown.
            returned = PackedDemo.Unpack(PackedDocument.Parse(text), ref target);
        } catch (Exception ex) {
            exception = $"{ex.GetType().Name}: {ex.Message}";
        } finally {
            AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
        }

        var touched = target.Name != Untouched || !target.Shelves.ContainsKey(Untouched) || target.Featured != null;
        var (filled, blank) = touched ? Census(target) : (0, 0);
        string[] names;
        lock (loaded) { names = loaded.ToArray(); }
        return result with {
            Returned = returned, Exception = exception, Touched = touched, Filled = filled, Blank = blank, Loaded = names,
        };
    }

    // Counts what the unpack left behind: every object reachable from the library, split into those
    // that had a name, label or title read into them and those that were created and left empty.
    private static (int Filled, int Blank) Census(Library library) {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { library };
        var stack = new Stack<object>();
        stack.Push(library);
        int filled = 0, blank = 0;
        void Visit(object? next) {
            if (next != null && seen.Add(next)) { stack.Push(next); }
        }
        while (stack.Count > 0) {
            string name;
            switch (stack.Pop()) {
                case Library l:
                    name = l.Name == Untouched ? "" : l.Name;
                    foreach (var shelf in l.Shelves.Values) { Visit(shelf); }
                    Visit(l.Featured);
                    break;
                case Shelf s:
                    name = s.Label;
                    foreach (var book in s.Books) { Visit(book); }
                    break;
                case Book b:
                    name = b.Title;
                    Visit(b.Author);
                    Visit(b.Sequel);
                    break;
                case Author a:
                    name = a.Name;
                    foreach (var book in a.Books) { Visit(book); }
                    break;
                default: continue;
            }
            if (name.Length > 0) { filled++; } else { blank++; }
        }
        return (filled, blank);
    }

    // Makes the change in place. Every preset works on the same few things, found by walking from
    // the root: the first shelf that has a book, that shelf's first book, and the book's type entry.
    // Returns no sentence when the document lacks them.
    private static (string? Change, string? ChangedId) Apply(JObject json, BreakKind kind) {
        switch (kind) {
            case BreakKind.VersionPlusOne: {
                var was = json["version"]?.ToObject<int>() ?? 0;
                json["version"] = was + 1;
                return ($"Raised the version from {was} to {was + 1}.", null);
            }
            case BreakKind.ContentType: {
                var was = (string?)json["Content-Type"];
                json["Content-Type"] = "application/json";
                return ($"Changed the Content-Type from {was} to application/json.", null);
            }
            case BreakKind.DropTypes: {
                var count = (json["types"] as JObject)?.Count ?? 0;
                json.Remove("types");
                return (count == 1 ? "Removed the types table, which had 1 entry." : $"Removed the types table, which had {count} entries.", null);
            }
        }

        if (json["objects"] is not JObject objects || json["root"] is not JObject root ||
            objects[(string?)root["ref"] ?? ""] is not JObject library || library["shelves"] is not JObject shelves) {
            return (null, null);
        }
        foreach (var entry in shelves.Properties()) {
            if (entry.Value is not JObject shelfRef || (string?)shelfRef["ref"] is not { } shelfId ||
                objects[shelfId] is not JObject shelf || shelf["books"] is not JArray { Count: > 0 } books ||
                books[0] is not JObject bookRef || (string?)bookRef["ref"] is not { } bookId ||
                (string?)bookRef["type"] is not { } typeId || objects[bookId] is not JObject book) {
                continue;
            }
            var where = $"the first book on shelf {shelfId}";
            switch (kind) {
                case BreakKind.DangleRef: {
                    var missing = objects.Count + 9000;
                    while (objects.ContainsKey($"o:{missing}")) { missing++; }
                    bookRef["ref"] = $"o:{missing}";
                    return ($"Pointed {where} at o:{missing}, an id no object has. It was {bookId}.", shelfId);
                }
                case BreakKind.DeleteObject:
                    objects.Remove(bookId);
                    return ($"Removed {bookId}, a book, from the objects table. Shelf {shelfId} and everything else that referred to it still does.", shelfId);
                case BreakKind.WrongKind: {
                    if (book["author"] is not JObject authorRef || (string?)authorRef["ref"] is not { } authorId) { continue; }
                    bookRef["type"] = authorRef["type"]?.DeepClone();
                    bookRef["ref"] = authorId;
                    return ($"Pointed {where} at {authorId}, which is an author. It was {bookId}.", shelfId);
                }
                case BreakKind.CorruptValue: {
                    var was = book["pages"]?.ToString(Formatting.None);
                    book["pages"] = "abc";
                    return ($"Replaced the page count of {bookId}, which was {was}, with the text \"abc\".", bookId);
                }
                case BreakKind.RefIdNotText:
                    bookRef["ref"] = new JArray(bookId);
                    return ($"Made the id in the reference to {where} an array holding {bookId}, where a string belongs.", shelfId);
            }

            if (json["types"] is not JObject types || types[typeId] is not JObject type) { return (null, null); }
            var about = $"{bookId} is one of the books that name it.";
            switch (kind) {
                case BreakKind.RemoveTypeEntry:
                    types.Remove(typeId);
                    return ($"Removed the entry for {typeId}, which is Book, from the types table. {about}", bookId);
                case BreakKind.RenameType:
                    type["fullname"] = MovedType;
                    return ($"Renamed {typeId}, which is Book, to {MovedType}. {about}", bookId);
                case BreakKind.NameOtherType: {
                    var already = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == OtherAssembly);
                    type["fullname"] = OtherType;
                    var note = already ? $" {OtherAssembly} was already loaded in this run, so it cannot be seen loading again." : "";
                    return ($"Pointed {typeId}, which is Book, at {OtherType}. {about}{note}", bookId);
                }
                case BreakKind.BadAssemblyName:
                    type["fullname"] = BadName;
                    return ($"Renamed {typeId}, which is Book, to {BadName}, whose assembly name cannot be parsed. {about}", bookId);
            }
            return (null, null);
        }
        return (null, null);
    }
}
