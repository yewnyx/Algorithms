using System.Runtime.CompilerServices;
using FluentAssertions;

namespace Yewnyx.Packables.Test.Format1;

// The format 1 documents in Fixtures/Format1, converted to format 3 and unpacked into the same types
// ported to format 3, as a host does after porting. The types keep their names, so their
// assembly-qualified names match the ones the documents hold.
public class Format1FixtureTests {
    private const string ContentType = "application/packable+json";

    private static readonly PackableRegistry Registry = new PackableRegistry.Builder()
        .Add(typeof(Scalars), "Test.Scalars", 0)
        .Add(typeof(Vectors), "Test.Vectors", 0)
        .Add(typeof(Node), "Test.Node", 0)
        .Add(typeof(Pair), "Test.Pair", 0)
        .Add(typeof(Table), "Test.Table", 0)
        .Build();

    private static string Fixture(string name, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Format1", name + ".json"));

    private static PackedDocument Convert(string name) {
        var document = new PackedDocument();
        Format1Converter.TryConvert(Fixture(name), document, ContentType, Registry, out var error).Should().BeTrue(error.ToString());
        return document;
    }

    private static T Read<T>(string name, UnpackOutcome outcome = UnpackOutcome.Clean, List<UnpackProblem>? problems = null,
        UnpackOptions options = UnpackOptions.None) where T : class, IPackable, new() {
        var root = new T();
        Unpacker.Unpack(Convert(name), root, Registry, problems, options).Should().Be(outcome);
        return root;
    }

    private static List<int> Values(Node? node) {
        var values = new List<int>();
        for (; node != null; node = node.next) { values.Add(node.value); }
        return values;
    }

    [Fact]
    public void Scalars() {
        var s = Read<Scalars>("scalars");
        (s.i, s.l, s.d, s.flag).Should().Be((-7, 1L << 40, 0.1, true));
        double.IsNaN(s.nan).Should().BeTrue(); // Format 1 wrote "NaN" as a string; typed reads accept it.
        (s.positiveInfinity, s.negativeInfinity).Should().Be((double.PositiveInfinity, double.NegativeInfinity));
        (s.negativeZero, double.IsNegative(s.negativeZero)).Should().Be((0.0, false)); // Normalised when converted.
        s.text.Should().Be("héllo \"quoted\"");
        s.empty.Should().BeEmpty();
        s.none.Should().BeNull();
    }

    [Fact]
    public void Nested() {
        // Format 3 sorts maps by key: the Dictionary order format 1 kept (d, b, c) is gone.
        var v = Read<Vectors>("nested");
        v.points.Keys.Should().Equal("a", "b");
        v.points["a"].Should().Equal(4.5, 5, 6, 7);
        v.points["b"].Should().Equal(1, 2, 3);
        v.counts.Keys.Should().Equal("b", "c", "d");
        v.counts.Values.Should().Equal(2, 3, 4);
    }

    [Fact]
    public void Shared() {
        var pair = Read<Pair>("shared");
        pair.first!.value.Should().Be(7);
        pair.second.Should().BeSameAs(pair.first);
    }

    [Fact]
    public void Loop() {
        var first = Read<Node>("loop");
        first.next!.value.Should().Be(2);
        first.next.next.Should().BeSameAs(first);
    }

    [Fact]
    public void Chain12() => Values(Read<Node>("chain12")).Should().Equal(Enumerable.Range(1, 12));

    [Fact]
    public void MapOfRefs() {
        var table = Read<Table>("map-of-refs");
        table.entries.Keys.Should().Equal("id-1", "id-2", "id-3");
        (table.entries["id-1"].value, table.entries["id-2"].value).Should().Be((1, 2));
        table.entries["id-3"].Should().BeSameAs(table.entries["id-1"]);
    }

    [Fact]
    public void Dangling() {
        // Format 1 readers made an empty object for the missing id. Format 3 never makes one: the ref
        // reads as missing, which is recorded, and the rest loads.
        var problems = new List<UnpackProblem>();
        var head = Read<Node>("dangling", UnpackOutcome.WithProblems, problems);
        Values(head).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        problems.Single().Should().Be(new UnpackProblem(UnpackProblemKind.MissingObject, 7, "Test.Node#0", "next"));
    }

    [Fact]
    public void UnresolvedType() {
        // "Gone" matches no registration, so the name stays as it was and is an unknown type.
        const string gone = "Yewnyx.Packables.Test.Format1.Node, Gone, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        var document = Convert("unresolved-type");
        document.ObjectType(1).Should().Be(gone);

        var problems = new List<UnpackProblem>();
        Unpacker.Unpack(document, new Pair(), Registry, problems).Should().Be(UnpackOutcome.Failed);
        problems.Single().Should().Be(new UnpackProblem(UnpackProblemKind.UnknownType, 1, gone, null));

        var pair = Read<Pair>("unresolved-type", UnpackOutcome.WithProblems, options: UnpackOptions.Permissive);
        pair.first.Should().BeNull();
        pair.second.Should().BeNull();
    }

    // Converting a format 1 document, or unpacking it and packing the result afresh in format 3,
    // gives the same text. NaN is the expected exception: format 1 stored it as a string.
    [Theory]
    [InlineData("nested")]
    [InlineData("shared")]
    [InlineData("loop")]
    [InlineData("chain12")]
    [InlineData("map-of-refs")]
    public void ConvertingEqualsPackingAfresh(string name) {
        var converted = Convert(name);
        var rootType = Registry.Registrations.Single(r => PackableNames.Format(r.Name, 0) == converted.ObjectType(0)).Type;
        var root = (IPackable)Activator.CreateInstance(rootType)!;
        Unpacker.Unpack(converted, root, Registry).Should().Be(UnpackOutcome.Clean);

        var fresh = new PackedDocument();
        Packer.TryPack(root, Registry, fresh, out _).Should().BeTrue();
        PackedJson.Write(fresh, ContentType).Should().Be(PackedJson.Write(converted, ContentType));
    }

    [Fact]
    public void AliasesMatchOldNames() {
        // The types moved out of the "Gone" assembly, as the host's did: the old name as an alias maps them back.
        const string gone = "Yewnyx.Packables.Test.Format1.Node, Gone, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        var registry = new PackableRegistry.Builder().Add(typeof(Pair), "Test.Pair", 0).Add(typeof(Node), "Test.Node", 0, gone).Build();
        var document = new PackedDocument();
        Format1Converter.TryConvert(Fixture("unresolved-type"), document, ContentType, registry, out _).Should().BeTrue();
        document.ObjectType(1).Should().Be("Test.Node#0");

        var pair = new Pair();
        Unpacker.Unpack(document, pair, registry).Should().Be(UnpackOutcome.Clean);
        pair.first!.value.Should().Be(7);
        pair.second.Should().BeSameAs(pair.first);
    }

    // A one-object format 1 document: a Node with value 1 and the given extra fields.
    private static string Doc(string fields, string? typeId = null, bool typesTable = true) {
        var id = typeId ?? "t:1";
        var types = typesTable ? $$""", "types": { "{{id}}": { "type": "{{id}}", "fullname": "{{typeof(Node).AssemblyQualifiedName}}" } }""" : "";
        return $$"""
            { "Content-Type": "application/packable+json", "version": 1, "root": { "type": "{{id}}", "ref": "o:1" },
              "objects": { "o:1": { "value": 1, {{fields}} "isa": { "type": "{{id}}" } } }{{types}} }
            """;
    }

    private static PackedEntry[] Fields(string fields) {
        var document = new PackedDocument();
        Format1Converter.TryConvert(Doc(fields), document, ContentType, Registry, out var error).Should().BeTrue(error.ToString());
        return document.Entries(document.ObjectFields(0)).ToArray();
    }

    [Fact]
    public void RefsAreRecognisedOnlyByTheirExactShape() {
        Fields("""  "next": { "type": "t:null", "ref": "o:null" },  """).Single(e => e.Key == "next").Value.Kind.Should().Be(PackedKind.Null);
        var entries = Fields("""  "extra": { "type": "t:1", "ref": "o:1", "more": 1 }, "notAnId": { "type": "x", "ref": "y" },  """);
        entries.Single(e => e.Key == "extra").Value.Kind.Should().Be(PackedKind.Map);
        entries.Single(e => e.Key == "notAnId").Value.Kind.Should().Be(PackedKind.Map); // Format 1 ids all start "o:".
    }

    [Fact]
    public void DatesStayStringsInTextAndAreRefusedInParsedTrees() {
        Fields("""  "when": "2026-11-10T12:00:00Z",  """).Single(e => e.Key == "when").Value.AsString.Should().Be("2026-11-10T12:00:00Z");

        var parsed = Newtonsoft.Json.Linq.JObject.Parse(Doc("""  "when": "2026-11-10T12:00:00Z",  """));
        Format1Converter.TryConvert(parsed, new PackedDocument(), ContentType, Registry, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.DateValue);

        var plain = Newtonsoft.Json.Linq.JObject.Parse(Doc(""));
        Format1Converter.TryConvert(plain, new PackedDocument(), ContentType, Registry, out _).Should().BeTrue();
    }

    [Fact]
    public void RepeatedKeysAreRefused() {
        Format1Converter.TryConvert(Doc("""  "value": 2,  """), new PackedDocument(), ContentType, Registry, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.Malformed);
    }

    [Fact]
    public void BuiltInIdsMatchAliasesToo() {
        var registry = new PackableRegistry.Builder().Add(typeof(Node), "Test.Node", 0, "node").Build();
        var document = new PackedDocument();
        Format1Converter.TryConvert(Doc("", "node", typesTable: false), document, ContentType, registry, out _).Should().BeTrue();
        document.ObjectType(0).Should().Be("Test.Node#0");
    }

    [Fact]
    public void AFailedConversionLeavesTheDocumentEmpty() {
        var document = Convert("chain12");
        Format1Converter.TryConvert("not json", document, ContentType, Registry, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.Malformed);
        (document.ObjectCount, document.IsSealed).Should().Be((0, false));
    }

    // A Node whose "deep" field holds a number inside this many arrays.
    private static string NestedArrays(int arrays) => Doc($"  \"deep\": {new string('[', arrays)}1{new string(']', arrays)},  ");

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void NestingIsLimitedAsFormat3ReadsIt(int arrays, bool fits) {
        // Whatever converts at a depth limit must read back as format 3 at the same limit.
        const int maxDepth = 8;
        var text = NestedArrays(arrays);
        var parsed = Newtonsoft.Json.Linq.JObject.Parse(text);
        Format1Converter.TryConvert(text, new PackedDocument(), ContentType, Registry, out var fromText, maxDepth).Should().Be(fits);
        Format1Converter.TryConvert(parsed, new PackedDocument(), ContentType, Registry, out var fromTree, maxDepth).Should().Be(fits);
        var expected = fits ? PackedJsonErrorKind.None : PackedJsonErrorKind.TooDeep;
        (fromText.Kind, fromTree.Kind).Should().Be((expected, expected));

        var document = new PackedDocument();
        Format1Converter.TryConvert(text, document, ContentType, Registry, out _, maxDepth: 100).Should().BeTrue();
        PackedJson.TryRead(PackedJson.Write(document, ContentType), new PackedDocument(), ContentType, out var reread, maxDepth)
            .Should().Be(fits);
        if (!fits) { reread.Kind.Should().Be(PackedJsonErrorKind.TooDeep); }
    }

    [Fact]
    public void VeryDeepTextIsRefusedWithoutACrash() {
        Format1Converter.TryConvert(NestedArrays(100_000), new PackedDocument(), ContentType, Registry, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.TooDeep);
    }

    [Fact]
    public void DepthLimitsStopWhereTheStackStillHolds() {
        // Reading recurses once per level, so the highest limit is one both readers survive.
        var text = NestedArrays(PackedJson.MaxDepthLimit - 2);
        var document = new PackedDocument();
        Format1Converter.TryConvert(text, document, ContentType, Registry, out _, PackedJson.MaxDepthLimit).Should().BeTrue();
        var written = PackedJson.Write(document, ContentType);
        PackedJson.TryRead(written, new PackedDocument(), ContentType, out _, PackedJson.MaxDepthLimit).Should().BeTrue();

        var act = () => Format1Converter.TryConvert(text, new PackedDocument(), ContentType, Registry, out _, PackedJson.MaxDepthLimit + 1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxDepth");
        act = () => PackedJson.TryRead(written, new PackedDocument(), ContentType, out _, 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxDepth");
    }

    [Fact]
    public void ConversionChecksTheEnvelope() {
        Format1Converter.TryConvert(Fixture("shared"), new PackedDocument(), "application/other", Registry, out var error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.ContentType);

        var newer = Fixture("shared").Replace("\"version\": 1", "\"version\": 3");
        Format1Converter.TryConvert(newer, new PackedDocument(), ContentType, Registry, out error).Should().BeFalse();
        error.Kind.Should().Be(PackedJsonErrorKind.Version);
    }
}

public sealed class Scalars : IPackable {
    public int i;
    public long l;
    public double d, nan, positiveInfinity, negativeInfinity, negativeZero;
    public bool flag;
    public string? text, empty, none;

    public void PackInto(Packer packer) {
        packer.WriteInt32("i", i);
        packer.WriteInt64("l", l);
        packer.WriteDouble("d", d);
        packer.WriteDouble("nan", nan);
        packer.WriteDouble("positiveInfinity", positiveInfinity);
        packer.WriteDouble("negativeInfinity", negativeInfinity);
        packer.WriteDouble("negativeZero", negativeZero);
        packer.WriteBoolean("flag", flag);
        packer.WriteString("text", text);
        packer.WriteString("empty", empty);
        packer.WriteString("none", none);
    }

    public void UnpackFrom(Unpacker unpacker) {
        unpacker.TryReadInt32("i", out i);
        unpacker.TryReadInt64("l", out l);
        unpacker.TryReadDouble("d", out d);
        unpacker.TryReadDouble("nan", out nan);
        unpacker.TryReadDouble("positiveInfinity", out positiveInfinity);
        unpacker.TryReadDouble("negativeInfinity", out negativeInfinity);
        unpacker.TryReadDouble("negativeZero", out negativeZero);
        unpacker.TryReadBoolean("flag", out flag);
        unpacker.TryReadString("text", out text);
        unpacker.TryReadString("empty", out empty);
        unpacker.TryReadString("none", out none);
    }
}

public sealed class Vectors : IPackable {
    public Dictionary<string, double[]> points = new();
    public Dictionary<string, int> counts = new();

    public void PackInto(Packer packer) {
        var p = packer.Map("points");
        foreach (var (key, value) in points) { p.WriteDoubles(key, value); }
        var c = packer.Map("counts");
        foreach (var (key, value) in counts) { c.WriteInt32(key, value); }
    }

    public void UnpackFrom(Unpacker unpacker) {
        if (unpacker.TryReadMap("points", out var p)) {
            foreach (var entry in p) {
                if (!entry.Value.TryReadArray(out var array)) { continue; }
                var values = new double[array.Count];
                array.TryReadDoubles(values, out _);
                points[entry.Key] = values;
            }
        }
        if (unpacker.TryReadMap("counts", out var c)) {
            foreach (var entry in c) {
                if (entry.Value.TryReadInt32(out var value)) { counts[entry.Key] = value; }
            }
        }
    }
}

public sealed class Node : IPackable {
    public int value;
    public Node? next;

    public void PackInto(Packer packer) {
        packer.WriteInt32("value", value);
        if (next != null) { packer.WriteRef("next", next); }
    }

    public void UnpackFrom(Unpacker unpacker) {
        unpacker.TryReadInt32("value", out value);
        unpacker.TryReadRef("next", static () => new Node(), out next);
    }
}

public sealed class Pair : IPackable {
    public Node? first, second;

    public void PackInto(Packer packer) {
        packer.WriteRef("first", first);
        packer.WriteRef("second", second);
    }

    public void UnpackFrom(Unpacker unpacker) {
        unpacker.TryReadRef("first", static () => new Node(), out first);
        unpacker.TryReadRef("second", static () => new Node(), out second);
    }
}

public sealed class Table : IPackable {
    public Dictionary<string, Node> entries = new();

    public void PackInto(Packer packer) {
        var table = packer.Map("entries");
        foreach (var (key, node) in entries) { table.WriteRef(key, node); }
    }

    public void UnpackFrom(Unpacker unpacker) {
        if (!unpacker.TryReadMap("entries", out var table)) { return; }
        foreach (var entry in table) {
            if (entry.Value.TryReadRef(static () => new Node(), out Node? node) && node != null) { entries[entry.Key] = node; }
        }
    }
}
