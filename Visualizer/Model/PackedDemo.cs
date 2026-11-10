using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Yewnyx.Packables.Format1;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <param name="Shelves">Shelves in the map the library keeps, keyed by subject.</param>
/// <param name="BooksPerShelf">Mean books on a shelf.</param>
/// <param name="Authors">Authors the books are shared between.</param>
/// <param name="SharedFraction">Share of shelf slots that hold a book already on another shelf.</param>
public sealed record DemoSpec(int Shelves, double BooksPerShelf, int Authors, double SharedFraction, int Seed);

// The object graph of the demo: a library maps subjects to shelves, a shelf lists books, and books
// and authors point at each other. Made up for this tab; it stands for any object graph an
// application packs.

public sealed class Library : IPackable {
    public string Name = "";
    public Dictionary<string, Shelf> Shelves = new();
    public Book? Featured;

    public void PackInto(ref Packer packer, JObject storage) {
        storage["name"] = Name;
        var shelves = new JObject();
        foreach (var (subject, shelf) in Shelves) { shelves[subject] = packer.PackReference(shelf); }
        storage["shelves"] = shelves;
        storage["featured"] = packer.PackReference(Featured);
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("name", objectId, out var token)) { Name = token.ToObject<string>() ?? ""; }
        Shelves.Clear();
        if (unpacker.TryGetJson("shelves", objectId, out token) && token is JObject shelves) {
            foreach (var p in shelves.Properties()) {
                if (unpacker.TryCreateFromReference<Shelf>(p.Value, out var shelf, _ => new Shelf())) { Shelves[p.Name] = shelf!; }
            }
        }
        if (unpacker.TryGetJson("featured", objectId, out token)) { unpacker.TryCreateFromReference(token, out Featured, _ => new Book()); }
    }
}

public sealed class Shelf : IPackable {
    public string Label = "";
    public List<Book> Books = new();

    public void PackInto(ref Packer packer, JObject storage) {
        storage["label"] = Label;
        var books = new JArray();
        foreach (var book in Books) { books.Add((JToken)packer.PackReference(book)); }
        storage["books"] = books;
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("label", objectId, out var token)) { Label = token.ToObject<string>() ?? ""; }
        Books.Clear();
        if (unpacker.TryGetJson("books", objectId, out token) && token is JArray books) {
            foreach (var item in books) {
                if (unpacker.TryCreateFromReference<Book>(item, out var book, _ => new Book())) { Books.Add(book!); }
            }
        }
    }
}

public sealed class Book : IPackable {
    public string Title = "";
    public int Pages;
    /// <summary>Stars out of five; NaN while nobody has rated the book.</summary>
    public double Rating = double.NaN;
    public Author? Author;
    public Book? Sequel;

    public void PackInto(ref Packer packer, JObject storage) {
        storage["title"] = Title;
        storage["pages"] = Pages;
        storage["rating"] = Rating;
        storage["author"] = packer.PackReference(Author);
        storage["sequel"] = packer.PackReference(Sequel);
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("title", objectId, out var token)) { Title = token.ToObject<string>() ?? ""; }
        if (unpacker.TryGetJson("pages", objectId, out token)) { Pages = token.ToObject<int>(); }
        if (unpacker.TryGetJson("rating", objectId, out token)) { Rating = token.ToObject<double>(); }
        if (unpacker.TryGetJson("author", objectId, out token)) { unpacker.TryCreateFromReference(token, out Author, _ => new Author()); }
        if (unpacker.TryGetJson("sequel", objectId, out token)) { unpacker.TryCreateFromReference(token, out Sequel, _ => new Book()); }
    }
}

public sealed class Author : IPackable {
    public string Name = "";
    public List<Book> Books = new();

    public void PackInto(ref Packer packer, JObject storage) {
        storage["name"] = Name;
        var books = new JArray();
        foreach (var book in Books) { books.Add((JToken)packer.PackReference(book)); }
        storage["books"] = books;
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("name", objectId, out var token)) { Name = token.ToObject<string>() ?? ""; }
        Books.Clear();
        if (unpacker.TryGetJson("books", objectId, out token) && token is JArray books) {
            foreach (var item in books) {
                if (unpacker.TryCreateFromReference<Book>(item, out var book, _ => new Book())) { Books.Add(book!); }
            }
        }
    }
}

/// <summary>What packing a generated library, and taking it through text and back, gave.</summary>
public sealed record RoundTripResult {
    /// <summary>The document as read back from the text, which is what a saved file would hold.</summary>
    public required PackedDocument Document { get; init; }
    public TimeSpan PackTime { get; init; }
    /// <summary>Bytes the measured pack allocated.</summary>
    public long PackBytes { get; init; }
    /// <summary>Length of the document written as JSON without white space.</summary>
    public int Characters { get; init; }
    /// <summary>Whether the document read from the text equals the packed one, token for token.</summary>
    public bool SameInMemory { get; init; }
    /// <summary>Why the trip stopped before the comparison, or null.</summary>
    public string? Failure { get; init; }
    public TimeSpan UnpackTime { get; init; }
    public int RepackedCharacters { get; init; }
    /// <summary>Where the repacked text first differs from the first text, or -1 if they are equal.</summary>
    public int FirstDifference { get; init; } = -1;
    /// <summary>Books whose rating is NaN in the generated library, and in the unpacked one.</summary>
    public int Unrated { get; init; }
    public int UnratedBack { get; init; }
}

public static class PackedDemo {
    // Unpacking recurses once per reference, so a long chain of sequels would overflow the stack.
    private const int MaxSeries = 6;

    private static readonly string[] Subjects = [
        "fiction", "history", "maps", "poetry", "science", "travel", "music", "cooking", "gardens", "letters", "plays", "essays",
    ];
    private static readonly string[] Adjectives = ["Quiet", "Last", "Blue", "Hidden", "Long", "Small", "Northern", "Paper", "Second", "Bright"];
    private static readonly string[] Nouns = ["River", "Garden", "Winter", "Atlas", "Harbour", "Orchard", "Lantern", "Road", "Tide", "Clock"];
    private static readonly string[] Names = ["Ann", "Bob", "Cy", "Dee", "Eli", "Fay", "Gus", "Hal", "Ida", "Jo", "Kit", "Lou"];

    /// <summary>
    /// Builds a library. Books on two shelves and authors of several books are shared objects; a book
    /// and its author point at each other, which is a loop; a sequel links one book to the next.
    /// </summary>
    public static Library Generate(DemoSpec spec) {
        var rng = new Random(spec.Seed);
        var authors = new Author[Math.Max(1, spec.Authors)];
        for (var i = 0; i < authors.Length; i++) {
            authors[i] = new Author { Name = i < Names.Length ? Names[i] : $"Author {i + 1}" };
        }

        var library = new Library { Name = $"Library {spec.Seed}" };
        var all = new List<Book>();
        var lastBy = new Book?[authors.Length];
        var seriesLength = new int[authors.Length];
        for (var s = 0; s < spec.Shelves; s++) {
            var subject = s < Subjects.Length ? Subjects[s] : $"shelf-{s + 1}";
            var shelf = new Shelf { Label = char.ToUpperInvariant(subject[0]) + subject[1..] };
            var count = (int)Math.Round(spec.BooksPerShelf * (0.5 + rng.NextDouble()));
            for (var b = 0; b < count; b++) {
                if (all.Count > 0 && rng.NextDouble() < spec.SharedFraction) {
                    var other = all[rng.Next(all.Count)];
                    if (!shelf.Books.Contains(other)) { shelf.Books.Add(other); }
                    continue;
                }
                var a = rng.Next(authors.Length);
                var book = new Book {
                    Title = $"The {Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]}",
                    Pages = 80 + rng.Next(520),
                    Author = authors[a],
                };
                // Taken from the page count, so adding ratings left every generated library as it was.
                book.Rating = book.Pages % 9 == 0 ? double.NaN : 1 + book.Pages % 41 / 10.0;
                authors[a].Books.Add(book);
                if (lastBy[a] is { } previous && seriesLength[a] < MaxSeries && rng.NextDouble() < 0.4) {
                    previous.Sequel = book;
                    seriesLength[a]++;
                } else {
                    seriesLength[a] = 1;
                }
                lastBy[a] = book;
                shelf.Books.Add(book);
                all.Add(book);
            }
            library.Shelves[subject] = shelf;
        }
        if (all.Count > 0) { library.Featured = all[rng.Next(all.Count)]; }
        return library;
    }

    // Packer is a ref struct, so it lives only as a local inside this call.
    public static JObject Pack(Library library) {
        var packer = new Packer();
        return packer.Pack(library);
    }

    // Unpacker is a ref struct too. This is the only place the Visualizer unpacks anything, and the
    // only caller hands it text this class packed itself.
    private static bool Unpack(JObject document, ref Library into) {
        var unpacker = new Unpacker(document);
        return unpacker.TryUnpack(ref into);
    }

    /// <summary>
    /// Packs a generated library and takes it the way a saved file goes: written out as text, read
    /// back, unpacked into a new library and packed again. The two texts are then compared, because
    /// comparing documents in memory would miss what writing them out changes.
    /// </summary>
    public static RoundTripResult RoundTrip(DemoSpec spec, CancellationToken ct = default) {
        var library = Generate(spec);
        Pack(library); // Once unmeasured, so the measured pack does not time first-run compilation.
        ct.ThrowIfCancellationRequested();

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var packed = Pack(library);
        var packTime = Stopwatch.GetElapsedTime(start);
        var packBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

        var text = packed.ToString(Formatting.None);
        var document = PackedDocument.Read(PackedDocument.Parse(text), ct: ct);
        var result = new RoundTripResult {
            Document = document,
            PackTime = packTime,
            PackBytes = packBytes,
            Characters = text.Length,
            SameInMemory = JToken.DeepEquals(packed, document.Json),
            Unrated = CountUnrated(library),
        };
        ct.ThrowIfCancellationRequested();

        // The unpacker gets its own copy of the document, so nothing it does can alter the one shown.
        var read = PackedDocument.Parse(text);
        var copy = new Library();
        bool unpacked;
        try {
            var unmeasured = new Library();
            Unpack(PackedDocument.Parse(text), ref unmeasured);
            start = Stopwatch.GetTimestamp();
            unpacked = Unpack(read, ref copy);
        } catch (Exception ex) {
            return result with { Failure = $"unpacking threw {ex.GetType().Name}: {ex.Message}" };
        }
        var unpackTime = Stopwatch.GetElapsedTime(start);
        if (!unpacked) { return result with { Failure = "TryUnpack returned false" }; }

        var again = Pack(copy).ToString(Formatting.None);
        var same = Math.Min(text.Length, again.Length);
        var difference = 0;
        while (difference < same && text[difference] == again[difference]) { difference++; }
        return result with {
            UnpackTime = unpackTime,
            RepackedCharacters = again.Length,
            FirstDifference = difference == text.Length && difference == again.Length ? -1 : difference,
            UnratedBack = CountUnrated(copy),
        };
    }

    private static int CountUnrated(Library library) {
        var seen = new HashSet<Book>(ReferenceEqualityComparer.Instance);
        var count = 0;
        foreach (var shelf in library.Shelves.Values) {
            foreach (var book in shelf.Books) {
                if (seen.Add(book) && double.IsNaN(book.Rating)) { count++; }
            }
        }
        return count;
    }
}
