using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables.Test;

public class PackedJsonTest {
    private sealed class Note : IPackable {
        public string? Text;
        public double Weight;
        public Note? Next;
        public List<long> Tags = new();
        public Dictionary<string, double> Scores = new();

        public void PackInto(Packer packer) {
            packer.WriteString("text", Text);
            packer.WriteDouble("weight", Weight);
            packer.WriteRef("next", Next);
            packer.WriteInt64s("tags", Tags.ToArray());
            var scores = packer.Map("scores");
            foreach (var (key, score) in Scores) { scores.WriteDouble(key, score); }
        }

        public void UnpackFrom(Unpacker unpacker) {
            unpacker.TryReadString("text", out Text);
            unpacker.TryReadDouble("weight", out Weight);
            unpacker.TryReadRef("next", static () => new Note(), out Next);
            if (unpacker.TryReadArray("tags", out var tags)) {
                foreach (var tag in tags) { tag.TryReadInt64(out var value); Tags.Add(value); }
            }
            if (unpacker.TryReadMap("scores", out var scores)) {
                foreach (var entry in scores) { entry.Value.TryReadDouble(out var value); Scores[entry.Key] = value; }
            }
        }
    }

    private const string ContentType = "application/test+json";
    private static readonly PackableRegistry Registry = new PackableRegistry.Builder().Add(typeof(Note), "Test.Note", 1).Build();

    private static string Write(IPackable root) {
        var document = new PackedDocument();
        Packer.TryPack(root, Registry, document, out _).Should().BeTrue();
        return PackedJson.Write(document, ContentType);
    }

    private static PackedJsonError ReadError(string json, string contentType = ContentType, int maxDepth = PackedJson.DefaultMaxDepth) {
        PackedJson.TryRead(json, new PackedDocument(), contentType, out var error, maxDepth).Should().BeFalse();
        return error;
    }

    [Fact]
    public void WritesTheWrappedGrammar() {
        var second = new Note { Text = "b", Weight = double.NaN };
        var json = Write(new Note { Text = "a", Weight = 1.5, Next = second, Tags = [7], Scores = new() { ["x"] = 2 } });

        json.Should().Be("""
            {
              "Content-Type": "application/test+json",
              "version": 3,
              "root": {
                "$ref": "o:0",
                "$type": "Test.Note#1"
              },
              "objects": {
                "o:0": {
                  "$type": "Test.Note#1",
                  "$fields": {
                    "next": {
                      "$ref": "o:1",
                      "$type": "Test.Note#1"
                    },
                    "scores": {
                      "$map": {
                        "x": 2.0
                      }
                    },
                    "tags": {
                      "$array": [
                        7
                      ]
                    },
                    "text": "a",
                    "weight": 1.5
                  }
                },
                "o:1": {
                  "$type": "Test.Note#1",
                  "$fields": {
                    "next": null,
                    "scores": {
                      "$map": {}
                    },
                    "tags": {
                      "$array": []
                    },
                    "text": "b",
                    "weight": {
                      "$double": "NaN"
                    }
                  }
                }
              }
            }
            """.Replace("\r\n", "\n"));
    }

    [Fact]
    public void RoundTrips() {
        var json = Write(new Note {
            Text = "2026-11-10T12:00:00Z", // Looks like a date, stays a string.
            Weight = double.NegativeInfinity, Tags = [1, -2, long.MaxValue], Scores = new() { ["b"] = 0.1, ["a"] = -3 },
            Next = new Note { Text = "next" },
        });

        var document = new PackedDocument();
        PackedJson.TryRead(json, document, ContentType, out _).Should().BeTrue();
        PackedJson.Write(document, ContentType).Should().Be(json);

        var note = new Note();
        Unpacker.Unpack(document, note, Registry).Should().Be(UnpackOutcome.Clean);
        (note.Text, note.Weight, note.Next!.Text).Should().Be(("2026-11-10T12:00:00Z", double.NegativeInfinity, "next"));
        note.Tags.Should().Equal(1, -2, long.MaxValue);
        note.Scores.Should().Equal(new Dictionary<string, double> { ["a"] = -3, ["b"] = 0.1 });
    }

    [Theory]
    [InlineData("application/test+json")]
    [InlineData("application/test")]
    public void ContentTypesMatchWithOrWithoutTheSuffix(string expected) {
        PackedJson.TryRead(Write(new Note()), new PackedDocument(), expected, out _).Should().BeTrue();
        ReadError(Write(new Note()), "application/other").Kind.Should().Be(PackedJsonErrorKind.ContentType);
    }

    private static string Minimal(string fields, string root = """{ "$ref": "o:0", "$type": "Test.Note#1" }""") => $$"""
        { "Content-Type": "application/test+json", "version": 3, "root": {{root}},
          "objects": { "o:0": { "$type": "Test.Note#1", "$fields": {{fields}} } } }
        """;

    [Fact]
    public void MinimalDocumentsRead() {
        PackedJson.TryRead(Minimal("{}"), new PackedDocument(), ContentType, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "x": { "$set": [] } }""", PackedJsonErrorKind.UnknownTag, "objects.o:0.$fields.x.$set")]
    [InlineData("""{ "x": { "y": 1 } }""", PackedJsonErrorKind.BareContainer, "objects.o:0.$fields.x.y")]
    [InlineData("""{ "x": [ 1 ] }""", PackedJsonErrorKind.BareContainer, "objects.o:0.$fields.x")]
    [InlineData("""{ "x": {} }""", PackedJsonErrorKind.BareContainer, "objects.o:0.$fields.x")]
    [InlineData("""{ "x": { "$map": {}, "extra": 1 } }""", PackedJsonErrorKind.Malformed, "objects.o:0.$fields.x.extra")]
    [InlineData("""{ "x": { "$map": {}, "$meta": 1 } }""", PackedJsonErrorKind.UnknownTag, "objects.o:0.$fields.x.$meta")]
    [InlineData("""{ "x": { "$ref": "o:0", "$ref": "o:0", "$type": "Test.Note#1" } }""", PackedJsonErrorKind.Malformed, "objects.o:0.$fields.x")]
    [InlineData("""{ "x": { "$ref": "o:0" } }""", PackedJsonErrorKind.Malformed, "objects.o:0.$fields.x")]
    [InlineData("""{ "x": { "$double": "nan" } }""", PackedJsonErrorKind.Malformed, "objects.o:0.$fields.x.$double")]
    [InlineData("""{ "x": 1, "x": 2 }""", PackedJsonErrorKind.DuplicateKey, "x")]
    [InlineData("""{ "x": 99999999999999999999 }""", PackedJsonErrorKind.Malformed, "objects.o:0.$fields.x")]
    public void BadValuesAreRefused(string fields, PackedJsonErrorKind kind, string path) {
        var error = ReadError(Minimal(fields));
        error.Kind.Should().Be(kind);
        error.Path.Should().StartWith(path);
    }

    [Fact]
    public void BadDocumentsAreRefused() {
        ReadError(Minimal("{}", "null")).Kind.Should().Be(PackedJsonErrorKind.NoRoot);
        ReadError(Minimal("{}").Replace("\"version\": 3", "\"version\": 1")).Kind.Should().Be(PackedJsonErrorKind.Format1);
        ReadError(Minimal("{}").Replace("\"version\": 3", "\"version\": 4")).Kind.Should().Be(PackedJsonErrorKind.Version);
        ReadError(Minimal("{}").Replace("\"version\": 3,", "")).Kind.Should().Be(PackedJsonErrorKind.Version);
        ReadError("{ \"Content-Type\": ").Kind.Should().Be(PackedJsonErrorKind.Malformed);
        ReadError("[]").Kind.Should().Be(PackedJsonErrorKind.Malformed);
        ReadError(Minimal("{}") + " {}").Kind.Should().Be(PackedJsonErrorKind.Malformed);
        ReadError(Minimal("{}").Replace("\"objects\":", "\"objects\": {}, \"objects\":")).Kind.Should().Be(PackedJsonErrorKind.Malformed);
    }

    [Fact]
    public void NestingIsLimited() {
        var deep = "1";
        for (var i = 0; i < 10; i++) { deep = $$"""{ "$array": [ {{deep}} ] }"""; }
        PackedJson.TryRead(Minimal($$"""{ "x": {{deep}} }"""), new PackedDocument(), ContentType, out _, maxDepth: 12).Should().BeTrue();
        ReadError(Minimal($$"""{ "x": {{deep}} }"""), maxDepth: 11).Kind.Should().Be(PackedJsonErrorKind.TooDeep);
    }

    [Fact]
    public void ATreeParsedElsewhereIsReadButDatesAreRefused() {
        var json = Write(new Note { Text = "plain" });
        PackedJson.TryRead(JToken.Parse(json), new PackedDocument(), ContentType, out _).Should().BeTrue();

        // Newtonsoft's default parser turns this string into a date, losing the original text.
        var dated = JToken.Parse(json.Replace("\"plain\"", "\"2026-11-10T12:00:00Z\""));
        PackedJson.TryRead(dated, new PackedDocument(), ContentType, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.DateValue);
    }

    [Fact]
    public void RefsToIdsNoObjectHasReadAsMissingAndAreRecordedWhenUnpacked() {
        var json = Minimal("""{ "next": { "$ref": "o:5", "$type": "Test.Note#1" }, "text": "kept" }""");
        var document = new PackedDocument();
        PackedJson.TryRead(json, document, ContentType, out _).Should().BeTrue();
        document.ObjectCount.Should().Be(1); // Nothing is made up for "o:5".

        var note = new Note();
        var problems = new List<UnpackProblem>();
        Unpacker.Unpack(document, note, Registry, problems).Should().Be(UnpackOutcome.WithProblems);
        (note.Text, note.Next).Should().Be(("kept", null));
        problems.Single().Should().Be(new UnpackProblem(UnpackProblemKind.MissingObject, 0, "Test.Note#1", "next", "o:0"));

        // The missing id isn't kept, so the ref is written with a null id, which reads back the same way.
        var written = PackedJson.Write(document, ContentType);
        written.Should().Contain("\"$ref\": null");
        var reread = new PackedDocument();
        PackedJson.TryRead(written, reread, ContentType, out _).Should().BeTrue();
        PackedJson.Write(reread, ContentType).Should().Be(written);
    }

    [Theory]
    [InlineData("""{ "$ref": "nobody", "$type": "Test.Note#1" }""")]
    [InlineData("""{ "$ref": null, "$type": "Test.Note#1" }""")]
    public void ARootNamingNoObjectReadsButDoesntUnpack(string root) {
        var document = new PackedDocument();
        PackedJson.TryRead(Minimal("{}", root), document, ContentType, out _).Should().BeTrue();
        (document.Root.Target, document.ObjectCount, document.ObjectId(0)).Should().Be((-1, 1, "o:0"));

        var problems = new List<UnpackProblem>();
        Unpacker.Unpack(document, new Note(), Registry, problems).Should().Be(UnpackOutcome.Failed);
        problems.Single().Kind.Should().Be(UnpackProblemKind.NoRoot);
    }

    private sealed class Tree : IPackable {
        public List<Tree> Children = new();
        public Tree? Link;

        public void PackInto(Packer packer) {
            var children = packer.Array("children");
            foreach (var child in Children) { children.WriteRef(child); }
            packer.WriteRef("link", Link);
        }

        public void UnpackFrom(Unpacker unpacker) { }
    }

    [Fact]
    public void WrittenTextReadsBackWithEachIdAtItsIndex() {
        var registry = new PackableRegistry.Builder().Add<Tree>("Test.Tree", 0).Build();
        Tree grandchild = new(), other = new();
        var root = new Tree { Children = [new Tree { Children = [other] }, new Tree { Children = [grandchild] }], Link = grandchild };
        var packed = new PackedDocument();
        Packer.TryPack(root, registry, packed, out _).Should().BeTrue();
        var json = PackedJson.Write(packed, ContentType);

        // Ids are given out as they're first met, which in written text is breadth-first, as written.
        var document = new PackedDocument();
        PackedJson.TryRead(json, document, ContentType, out _).Should().BeTrue();
        Enumerable.Range(0, document.ObjectCount).Select(document.ObjectId).Should().Equal("o:0", "o:1", "o:2", "o:3", "o:4");
        PackedJson.Write(document, ContentType).Should().Be(json);
    }

    [Fact]
    public void IdsAreAnyTextInAnyOrder() {
        // Objects before the root, ids that aren't "o:N", one object nothing reaches, and a ref to an id
        // no object has: all read, as a joined or hand-edited text would need.
        var json = """
            { "objects": {
                "spare": { "$type": "Test.Note#1", "$fields": { "text": "unreached" } },
                "second": { "$type": "Test.Note#1", "$fields": { "text": "b", "next": { "$ref": "gone", "$type": "Test.Note#1" } } },
                "first": { "$type": "Test.Note#1", "$fields": { "text": "a", "next": { "$ref": "second", "$type": "Test.Note#1" } } } },
              "root": { "$ref": "first", "$type": "Test.Note#1" },
              "Content-Type": "application/test+json", "version": 3 }
            """;
        var document = new PackedDocument();
        PackedJson.TryRead(json, document, ContentType, out _).Should().BeTrue();
        Enumerable.Range(0, document.ObjectCount).Select(document.ObjectId).Should().Equal("spare", "second", "first");

        var note = new Note();
        var problems = new List<UnpackProblem>();
        Unpacker.Unpack(document, note, Registry, problems).Should().Be(UnpackOutcome.WithProblems);
        (note.Text, note.Next!.Text, note.Next.Next).Should().Be(("a", "b", null));
        problems.Single().Should().Be(new UnpackProblem(UnpackProblemKind.MissingObject, 1, "Test.Note#1", "next", "second"));
        problems.Single().ToString().Should().Be("MissingObject in object second (Test.Note#1) at \"next\"");

        // Writing keeps the ids, so what's read back writes the same text again.
        var written = PackedJson.Write(document, ContentType);
        written.Should().Contain("\"first\"").And.NotContain("\"o:");
        var reread = new PackedDocument();
        PackedJson.TryRead(written, reread, ContentType, out _).Should().BeTrue();
        PackedJson.Write(reread, ContentType).Should().Be(written);
    }

    // The root's ref comes first, but "objects" lists the root second and its children in reverse.
    private const string Reordered = """
        { "Content-Type": "application/test+json", "version": 3, "root": { "$ref": "x", "$type": "Test.Tree#0" },
          "objects": {
            "a": { "$type": "Test.Tree#0", "$fields": {} },
            "x": { "$type": "Test.Tree#0", "$fields": { "children": { "$array": [
              { "$ref": "b", "$type": "Test.Tree#0" }, { "$ref": "a", "$type": "Test.Tree#0" } ] } } },
            "b": { "$type": "Test.Tree#0", "$fields": {} } } }
        """;

    [Fact]
    public void ObjectsKeepTheOrderTheTextListsThem() {
        var document = new PackedDocument();
        PackedJson.TryRead(Reordered, document, ContentType, out _).Should().BeTrue();
        Enumerable.Range(0, document.ObjectCount).Select(document.ObjectId).Should().Equal("a", "x", "b");
        document.Root.Target.Should().Be(1);

        var once = PackedJson.Write(document, ContentType);
        var reread = new PackedDocument();
        PackedJson.TryRead(once, reread, ContentType, out _).Should().BeTrue();
        PackedJson.Write(reread, ContentType).Should().Be(once);
    }

    [Fact]
    public void RenumberingDropsTheIdsATextGave() {
        var document = new PackedDocument();
        PackedJson.TryRead(Reordered, document, ContentType, out _).Should().BeTrue();
        document.Canonicalize();

        Enumerable.Range(0, document.ObjectCount).Select(document.ObjectId).Should().OnlyContain(id => id == null);
        PackedJson.Write(document, ContentType).Should().Contain("\"o:2\"").And.NotContain("\"x\"");
    }

    private sealed class Link : IPackable {
        public int Value;
        public Link? Next;

        public void PackInto(Packer packer) {
            packer.WriteInt32("value", Value);
            packer.WriteRef("next", Next);
        }

        public void UnpackFrom(Unpacker unpacker) { }
    }

    [Fact]
    public void WritingAPackedDocumentMakesNoStringPerObject() {
        var registry = new PackableRegistry.Builder().Add<Link>("Test.Link", 0).Build();
        Link? head = null;
        for (var i = 0; i < 2000; i++) { head = new Link { Value = i, Next = head }; }
        var document = new PackedDocument();
        Packer.TryPack(head!, registry, document, out _).Should().BeTrue();

        PackedJson.Write(document, TextWriter.Null, ContentType); // Makes the shared ids.
        var before = GC.GetAllocatedBytesForCurrentThread();
        PackedJson.Write(document, TextWriter.Null, ContentType);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(4000, "two ids an object used to make about 300 KB here");
    }

    [Fact]
    public void IdsFromIndicesReadBackAsWritten() {
        // Past each change in digit count, and past the ids shared between writes.
        var registry = new PackableRegistry.Builder().Add<Link>("Test.Link", 0).Build();
        Link? head = null;
        for (var i = 0; i < 16386; i++) { head = new Link { Value = i, Next = head }; }
        var packed = new PackedDocument();
        Packer.TryPack(head!, registry, packed, out _).Should().BeTrue();

        var document = new PackedDocument();
        PackedJson.TryRead(PackedJson.Write(packed, ContentType, indented: false), document, ContentType, out var error).Should().BeTrue(error.ToString());
        foreach (var index in new[] { 0, 9, 10, 11, 99, 100, 16383, 16384, 16385 }) {
            document.ObjectId(index).Should().Be($"o:{index}");
        }
    }

    private static string WithObjects(string objects) => $$"""
        { "Content-Type": "application/test+json", "version": 3, "root": { "$ref": "o:0", "$type": "Test.Note#1" }, "objects": {{objects}} }
        """;

    [Fact]
    public void BadIdsAreRefusedAndLeaveNothingBehind() {
        const string body = """{ "$type": "Test.Note#1", "$fields": {} }""";
        var cases = new (string Objects, PackedJsonErrorKind Kind)[] {
            ($$"""{ "o:0": {{body}}, "o:0": {{body}} }""", PackedJsonErrorKind.DuplicateKey),
            ($$"""{ "": {{body}} }""", PackedJsonErrorKind.Malformed),
            ($$"""[ {{body}} ]""", PackedJsonErrorKind.Malformed),
            ("""{ "o:0": { "$type": "Test.Note#1", "$fields": { "x": { "$ref": 0, "$type": "Test.Note#1" } } } }""", PackedJsonErrorKind.Malformed),
            ("""{ "o:0": { "$type": "Test.Note#1", "$fields": { "x": { "$ref": "", "$type": "Test.Note#1" } } } }""", PackedJsonErrorKind.Malformed),
        };
        var document = new PackedDocument();
        foreach (var (objects, kind) in cases) {
            PackedJson.TryRead(WithObjects(objects), document, ContentType, out var error).Should().BeFalse(objects);
            error.Kind.Should().Be(kind, objects);
            (document.ObjectCount, document.IsSealed).Should().Be((0, false));
        }
    }
}