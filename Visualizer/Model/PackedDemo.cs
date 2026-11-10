using System.Diagnostics;
using Yewnyx.Packables;

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

    public void PackInto(Packer packer) {
        packer.WriteString("name", Name);
        var shelves = packer.Map("shelves");
        foreach (var (subject, shelf) in Shelves) { shelves.WriteRef(subject, shelf); }
        packer.WriteRef("featured", Featured);
    }

    public void UnpackFrom(Unpacker unpacker) {
        Name = unpacker.TryReadString("name", out var name) ? name ?? "" : "";
        Shelves.Clear();
        if (unpacker.TryReadMap("shelves", out var shelves)) {
            foreach (var entry in shelves) {
                if (entry.Value.TryReadRef(static () => new Shelf(), out Shelf? shelf) && shelf is not null) { Shelves[entry.Key] = shelf; }
            }
        }
        unpacker.TryReadRef("featured", static () => new Book(), out Featured);
    }
}

public sealed class Shelf : IPackable {
    public string Label = "";
    public List<Book> Books = new();

    public void PackInto(Packer packer) {
        packer.WriteString("label", Label);
        var books = packer.Array("books");
        foreach (var book in Books) { books.WriteRef(book); }
    }

    public void UnpackFrom(Unpacker unpacker) {
        Label = unpacker.TryReadString("label", out var label) ? label ?? "" : "";
        Books.Clear();
        if (unpacker.TryReadArray("books", out var books)) {
            foreach (var element in books) {
                if (element.TryReadRef(static () => new Book(), out Book? book) && book is not null) { Books.Add(book); }
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

    public void PackInto(Packer packer) {
        packer.WriteString("title", Title);
        packer.WriteInt32("pages", Pages);
        packer.WriteDouble("rating", Rating);
        packer.WriteRef("author", Author);
        packer.WriteRef("sequel", Sequel);
    }

    public void UnpackFrom(Unpacker unpacker) {
        Title = unpacker.TryReadString("title", out var title) ? title ?? "" : "";
        unpacker.TryReadInt32("pages", out Pages);
        Rating = unpacker.TryReadDouble("rating", out var rating) ? rating : double.NaN;
        unpacker.TryReadRef("author", static () => new Author(), out Author);
        unpacker.TryReadRef("sequel", static () => new Book(), out Sequel);
    }
}

public sealed class Author : IPackable {
    public string Name = "";
    public List<Book> Books = new();

    public void PackInto(Packer packer) {
        packer.WriteString("name", Name);
        var books = packer.Array("books");
        foreach (var book in Books) { books.WriteRef(book); }
    }

    public void UnpackFrom(Unpacker unpacker) {
        Name = unpacker.TryReadString("name", out var name) ? name ?? "" : "";
        Books.Clear();
        if (unpacker.TryReadArray("books", out var books)) {
            foreach (var element in books) {
                if (element.TryReadRef(static () => new Book(), out Book? book) && book is not null) { Books.Add(book); }
            }
        }
    }
}

/// <summary>What packing a generated library, and taking it through text and back, gave.</summary>
public sealed record RoundTripResult {
    /// <summary>The document as read back from the text, which is what a saved file would hold.</summary>
    public required ViewedDocument Document { get; init; }
    public TimeSpan PackTime { get; init; }
    /// <summary>Bytes the measured pack allocated.</summary>
    public long PackBytes { get; init; }
    /// <summary>Length of the document written as JSON without white space.</summary>
    public int Characters { get; init; }
    /// <summary>Whether the document read back from the text writes the same text again.</summary>
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
    // Unpacking fills objects from a queue, so even long chains of sequels would load; short series
    // keep the graph readable.
    private const int MaxSeries = 6;

    private const string ContentType = "application/packable+json";

    private static readonly PackableRegistry Registry = new PackableRegistry.Builder()
        .Add(typeof(Library), "Demo.Library", 0)
        .Add(typeof(Shelf), "Demo.Shelf", 0)
        .Add(typeof(Book), "Demo.Book", 0)
        .Add(typeof(Author), "Demo.Author", 0)
        .Build();

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

    /// <summary>Packs into <paramref name="document"/>; reusing it lets packing stop allocating.</summary>
    public static void Pack(Library library, PackedDocument document) {
        if (!Packer.TryPack(library, Registry, document, out var unregistered)) {
            throw new InvalidOperationException($"{unregistered} isn't registered.");
        }
    }

    // The only place the Visualizer unpacks anything, and the only caller hands it text this class
    // packed itself.
    private static UnpackOutcome Unpack(string text, Library into) {
        var document = new PackedDocument();
        if (!PackedJson.TryRead(text, document, ContentType, out var error)) { throw new InvalidOperationException(error.ToString()); }
        return Unpacker.Unpack(document, into, Registry);
    }

    /// <summary>
    /// Packs a generated library and takes it the way a saved file goes: written out as text, read
    /// back, unpacked into a new library and packed again. The two texts are then compared, because
    /// comparing documents in memory would miss what writing them out changes.
    /// </summary>
    public static RoundTripResult RoundTrip(DemoSpec spec, CancellationToken ct = default) {
        var library = Generate(spec);
        var packed = new PackedDocument();
        Pack(library, packed); // Once unmeasured: the measured pack then times neither compilation nor buffer growth.
        ct.ThrowIfCancellationRequested();

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        Pack(library, packed);
        var packTime = Stopwatch.GetElapsedTime(start);
        var packBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

        var text = PackedJson.Write(packed, ContentType, indented: false);
        var readBack = new PackedDocument();
        var same = PackedJson.TryRead(text, readBack, ContentType, out _) &&
                   PackedJson.Write(readBack, ContentType, indented: false) == text;
        var result = new RoundTripResult {
            Document = ViewedDocument.Read(ViewedDocument.Parse(text), ct: ct),
            PackTime = packTime,
            PackBytes = packBytes,
            Characters = text.Length,
            SameInMemory = same,
            Unrated = CountUnrated(library),
        };
        ct.ThrowIfCancellationRequested();

        var copy = new Library();
        UnpackOutcome outcome;
        try {
            Unpack(text, new Library()); // Unmeasured, as for packing.
            start = Stopwatch.GetTimestamp();
            outcome = Unpack(text, copy);
        } catch (Exception ex) {
            return result with { Failure = $"unpacking threw {ex.GetType().Name}: {ex.Message}" };
        }
        var unpackTime = Stopwatch.GetElapsedTime(start);
        if (outcome != UnpackOutcome.Clean) { return result with { Failure = $"unpacking gave {outcome}" }; }

        var repacked = new PackedDocument();
        Pack(copy, repacked);
        var again = PackedJson.Write(repacked, ContentType, indented: false);
        var common = Math.Min(text.Length, again.Length);
        var difference = 0;
        while (difference < common && text[difference] == again[difference]) { difference++; }
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
