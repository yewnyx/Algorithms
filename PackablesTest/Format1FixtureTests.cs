using System.Runtime.CompilerServices;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables.Test.Format1;

// Inside the namespace, so these names win over format 3's in Yewnyx.Packables.
using Yewnyx.Packables.Format1;

// Format 1 documents, kept so the format 1 to 3 converter has real input once format 1 is no longer
// written. Fixtures/Format1/README.md describes each one. Run with UPDATE_FIXTURES=1 to regenerate.
public class Format1FixtureTests {
    private static string FixtureDirectory([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Format1");

    // Indented, "\n" newlines and a final newline, so the bytes are the same on every platform.
    private static string Serialize(JObject document) {
        var text = new StringWriter { NewLine = "\n" };
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented }) { document.WriteTo(writer); }
        return text + "\n";
    }

    // The documents, as the format 1 Packer writes them (or derived from one, where noted).
    private static readonly Dictionary<string, Func<JObject>> Documents = new() {
        ["scalars"] = () => new Packer().Pack(new Scalars {
            i = -7, l = 1L << 40, d = 0.1, nan = double.NaN, positiveInfinity = double.PositiveInfinity,
            negativeInfinity = double.NegativeInfinity, negativeZero = -0.0, flag = true, text = "héllo \"quoted\"",
            empty = "", none = null,
        }),
        ["nested"] = () => {
            var counts = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
            counts.Remove("a");
            counts["d"] = 4; // Reuses a's slot, so the order is d, b, c: not insertion or sorted order.
            return new Packer().Pack(new Vectors {
                points = new() { ["b"] = [1, 2, 3], ["a"] = [4.5, 5, 6, 7] },
                counts = counts,
            });
        },
        ["shared"] = () => {
            var node = new Node { value = 7 };
            return new Packer().Pack(new Pair { first = node, second = node });
        },
        ["loop"] = () => {
            var first = new Node { value = 1, next = new Node { value = 2 } };
            first.next.next = first;
            return new Packer().Pack(first);
        },
        ["chain12"] = () => new Packer().Pack(Chain(12)),
        ["map-of-refs"] = () => {
            var one = new Node { value = 1 };
            return new Packer().Pack(new Table {
                entries = new() { ["id-2"] = new Node { value = 2 }, ["id-1"] = one, ["id-3"] = one },
            });
        },
        // Derived: a chain of 9 with its last object removed, as the old Packer left refs past 8 levels.
        ["dangling"] = () => {
            var document = new Packer().Pack(Chain(9));
            ((JObject)document["objects"]!).Remove("o:9");
            return document;
        },
        // Derived: "shared" with Node's type named in an assembly that no longer exists.
        ["unresolved-type"] = () => {
            var document = Documents["shared"]();
            document["types"]!["t:2"]!["fullname"] =
                "Yewnyx.Packables.Test.Format1.Node, Gone, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
            return document;
        },
    };

    private static readonly string[] Derived = ["dangling", "unresolved-type"];

    private static Node Chain(int length) {
        var head = new Node { value = 1 };
        var tail = head;
        for (var value = 2; value <= length; value++) { tail = tail.next = new Node { value = value }; }
        return head;
    }

    public static IEnumerable<object[]> Names() => Documents.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Names))]
    public void FixtureMatchesWriter(string name) {
        var path = Path.Combine(FixtureDirectory(), name + ".json");
        var expected = Serialize(Documents[name]());
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1") {
            Directory.CreateDirectory(FixtureDirectory());
            File.WriteAllText(path, expected);
        }

        // Once format 1 is no longer written, only the files remain; this test goes with the Packer.
        File.ReadAllText(path).Should().Be(expected, Derived.Contains(name) ? "it is derived from the Packer's output" : "");
    }

    private static T Read<T>(string name) where T : class, IPackable, new() {
        var document = JObject.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), name + ".json")));
        var root = new T();
        new Unpacker(document).TryUnpack(ref root).Should().BeTrue();
        return root;
    }

    [Fact]
    public void Scalars() {
        var s = Read<Scalars>("scalars");
        (s.i, s.l, s.d, s.flag).Should().Be((-7, 1L << 40, 0.1, true));
        double.IsNaN(s.nan).Should().BeTrue();
        s.positiveInfinity.Should().Be(double.PositiveInfinity);
        s.negativeInfinity.Should().Be(double.NegativeInfinity);
        double.IsNegative(s.negativeZero).Should().BeTrue();
        s.text.Should().Be("héllo \"quoted\"");
        s.empty.Should().BeEmpty();
        s.none.Should().BeNull();
    }

    [Fact]
    public void Nested() {
        var v = Read<Vectors>("nested");
        v.points.Keys.Should().Equal("b", "a");
        v.points["b"].Should().Equal(1, 2, 3);
        v.points["a"].Should().Equal(4.5, 5, 6, 7);
        v.counts.Keys.Should().Equal("d", "b", "c");
        v.counts.Values.Should().Equal(4, 2, 3);
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
    public void Chain12() {
        var values = new List<int>();
        for (Node? node = Read<Node>("chain12"); node != null; node = node.next) { values.Add(node.value); }
        values.Should().Equal(Enumerable.Range(1, 12));
    }

    [Fact]
    public void MapOfRefs() {
        var table = Read<Table>("map-of-refs");
        table.entries.Keys.Should().Equal("id-2", "id-1", "id-3");
        table.entries["id-2"].value.Should().Be(2);
        table.entries["id-1"].value.Should().Be(1);
        table.entries["id-3"].Should().BeSameAs(table.entries["id-1"]);
    }

    [Fact]
    public void Dangling() {
        // Format 1 readers make an empty object for a ref to a missing id.
        var values = new List<int>();
        for (Node? node = Read<Node>("dangling"); node != null; node = node.next) { values.Add(node.value); }
        values.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 0);
    }

    [Fact]
    public void UnresolvedType() {
        // The root still loads; refs whose type can't be found come back null.
        var pair = Read<Pair>("unresolved-type");
        pair.first.Should().BeNull();
        pair.second.Should().BeNull();
    }
}

public sealed class Scalars : IPackable {
    public int i;
    public long l;
    public double d, nan, positiveInfinity, negativeInfinity, negativeZero;
    public bool flag;
    public string? text, empty, none;

    public void PackInto(ref Packer packer, JObject storage) {
        storage["i"] = i;
        storage["l"] = l;
        storage["d"] = d;
        storage["nan"] = nan;
        storage["positiveInfinity"] = positiveInfinity;
        storage["negativeInfinity"] = negativeInfinity;
        storage["negativeZero"] = negativeZero;
        storage["flag"] = flag;
        storage["text"] = text;
        storage["empty"] = empty;
        storage["none"] = none;
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        i = Get<int>(ref unpacker, objectId, "i");
        l = Get<long>(ref unpacker, objectId, "l");
        d = Get<double>(ref unpacker, objectId, "d");
        nan = Get<double>(ref unpacker, objectId, "nan");
        positiveInfinity = Get<double>(ref unpacker, objectId, "positiveInfinity");
        negativeInfinity = Get<double>(ref unpacker, objectId, "negativeInfinity");
        negativeZero = Get<double>(ref unpacker, objectId, "negativeZero");
        flag = Get<bool>(ref unpacker, objectId, "flag");
        text = Get<string?>(ref unpacker, objectId, "text");
        empty = Get<string?>(ref unpacker, objectId, "empty");
        none = Get<string?>(ref unpacker, objectId, "none");
    }

    private static T Get<T>(ref Unpacker unpacker, ObjectId objectId, string key) =>
        unpacker.TryGetJson(key, objectId, out var token) ? token.ToObject<T>()! : default!;
}

public sealed class Vectors : IPackable {
    public Dictionary<string, double[]> points = new();
    public Dictionary<string, int> counts = new();

    public void PackInto(ref Packer packer, JObject storage) {
        var p = new JObject();
        foreach (var (key, value) in points) { p[key] = new JArray(value.Cast<object>().ToArray()); }
        storage["points"] = p;
        var c = new JObject();
        foreach (var (key, value) in counts) { c[key] = value; }
        storage["counts"] = c;
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("points", objectId, out var p)) {
            foreach (var property in ((JObject)p).Properties()) { points[property.Name] = property.Value.ToObject<double[]>()!; }
        }
        if (unpacker.TryGetJson("counts", objectId, out var c)) {
            foreach (var property in ((JObject)c).Properties()) { counts[property.Name] = property.Value.ToObject<int>(); }
        }
    }
}

public sealed class Node : IPackable {
    public int value;
    public Node? next;

    public void PackInto(ref Packer packer, JObject storage) {
        storage["value"] = value;
        if (next != null) { storage["next"] = packer.PackReference(next); }
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        value = unpacker.TryGetJson("value", objectId, out var v) ? v.ToObject<int>() : 0;
        if (unpacker.TryGetJson("next", objectId, out var n)) { unpacker.TryCreateFromReference(n, out next, _ => new Node()); }
    }
}

public sealed class Pair : IPackable {
    public Node? first, second;

    public void PackInto(ref Packer packer, JObject storage) {
        storage["first"] = packer.PackReference(first);
        storage["second"] = packer.PackReference(second);
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (unpacker.TryGetJson("first", objectId, out var f)) { unpacker.TryCreateFromReference(f, out first, _ => new Node()); }
        if (unpacker.TryGetJson("second", objectId, out var s)) { unpacker.TryCreateFromReference(s, out second, _ => new Node()); }
    }
}

public sealed class Table : IPackable {
    public Dictionary<string, Node> entries = new();

    public void PackInto(ref Packer packer, JObject storage) {
        var table = new JObject();
        foreach (var (key, node) in entries) { table[key] = packer.PackReference(node); }
        storage["entries"] = table;
    }

    public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
        if (!unpacker.TryGetJson("entries", objectId, out var table)) { return; }
        foreach (var property in ((JObject)table).Properties()) {
            if (unpacker.TryCreateFromReference(property.Value, out Node? node, _ => new Node())) { entries[property.Name] = node!; }
        }
    }
}
