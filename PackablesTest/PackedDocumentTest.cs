using FluentAssertions;

namespace Yewnyx.Packables.Test;

public class PackedDocumentTest {
    private static string[] Keys(PackedDocument document, int container) =>
        document.Entries(container).ToArray().Select(e => e.Key!).ToArray();

    [Fact]
    public void SealingSortsFieldsAndMapsByKey() {
        var document = new PackedDocument();
        var o = document.AddObject("Ship#0");
        var map = document.AddMap();
        var array = document.AddArray();
        // Entries for different containers can interleave, as independent handles write them.
        document.Add(document.ObjectFields(o), "b", PackedValue.Integer(2));
        document.Add(map, "z", PackedValue.Bool(true));
        document.Add(array, PackedValue.String("first"));
        document.Add(document.ObjectFields(o), "a", PackedValue.Map(map));
        document.Add(map, "Z", PackedValue.Null);
        document.Add(array, PackedValue.String("second"));
        document.Add(document.ObjectFields(o), "c", PackedValue.Array(array));

        document.TrySeal(out _, out _).Should().BeTrue();
        Keys(document, document.ObjectFields(o)).Should().Equal("a", "b", "c");
        Keys(document, map).Should().Equal("Z", "z"); // Ordinal: upper case first.
        document.Entries(array).ToArray().Select(e => e.Value.AsString).Should().Equal("first", "second");
        document.IsMap(map).Should().BeTrue();
        document.IsMap(array).Should().BeFalse();
    }

    [Fact]
    public void SealingReportsADuplicateKey() {
        var document = new PackedDocument();
        var o = document.AddObject("Ship#0");
        document.Add(document.ObjectFields(o), "x", PackedValue.Integer(1));
        document.Add(document.ObjectFields(o), "x", PackedValue.Integer(2));

        document.TrySeal(out var container, out var key).Should().BeFalse();
        (container, key).Should().Be((document.ObjectFields(o), "x"));
        document.IsSealed.Should().BeTrue();
    }

    [Fact]
    public void DoublesAreNormalisedWhenMade() {
        PackedValue.Double(-0.0).Should().Be(PackedValue.Double(0.0));
        double.IsNegative(PackedValue.Double(-0.0).AsDouble).Should().BeFalse();

        var otherNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001);
        PackedValue.Double(otherNaN).Should().Be(PackedValue.Double(double.NaN));
        PackedValue.Double(1.5).Should().NotBe(PackedValue.Double(-1.5));
        PackedValue.Double(1).Should().NotBe(PackedValue.Integer(1));
    }

    [Fact]
    public void NullStringsAreNull() {
        PackedValue.String(null).Kind.Should().Be(PackedKind.Null);
        PackedValue.String("").Kind.Should().Be(PackedKind.String);
    }

    [Fact]
    public void ReadingTheWrongKindThrows() {
        var act = () => PackedValue.Integer(1).AsString;
        act.Should().Throw<InvalidOperationException>().WithMessage("A Integer value, not String.");
    }

    [Fact]
    public void CanonicalizeNumbersBreadthFirstAndDropsTheUnreachable() {
        // Built out of order: 0 unreachable, 1 = c (a leaf), 2 = root, 3 = a, 4 = b.
        // root -> "b": b, "a": a (fields sorted, so a comes first); a -> c in an array; b -> root (a loop).
        var document = new PackedDocument();
        var lost = document.AddObject("Lost#0");
        var c = document.AddObject("Leaf#0");
        var root = document.AddObject("Root#0");
        var a = document.AddObject("Node#0");
        var b = document.AddObject("Node#0");
        document.Add(document.ObjectFields(root), "b", PackedValue.Ref(b, "Node#0"));
        document.Add(document.ObjectFields(root), "a", PackedValue.Ref(a, "Node#0"));
        var children = document.AddArray();
        document.Add(children, PackedValue.Ref(c, "Leaf#0"));
        document.Add(children, PackedValue.Ref(-1, "Leaf#0")); // Missing object: stays -1.
        document.Add(document.ObjectFields(a), "children", PackedValue.Array(children));
        document.Add(document.ObjectFields(b), "back", PackedValue.Ref(root, "Root#0"));
        document.Add(document.ObjectFields(lost), "x", PackedValue.Ref(c, "Leaf#0"));
        document.SetRoot(PackedValue.Ref(root, "Root#0"));
        document.TrySeal(out _, out _).Should().BeTrue();

        document.Canonicalize();

        // Breadth-first: root, then its fields a and b, then a's child c. "Lost" is gone.
        document.ObjectCount.Should().Be(4);
        Enumerable.Range(0, 4).Select(document.ObjectType).Should().Equal("Root#0", "Node#0", "Node#0", "Leaf#0");
        document.Root.Should().Be(PackedValue.Ref(0, "Root#0"));
        var rootFields = document.Entries(document.ObjectFields(0)).ToArray();
        rootFields.Select(e => e.Value.Target).Should().Equal(1, 2);
        var aChildren = document.Entries(document.Entries(document.ObjectFields(1))[0].Value.Container).ToArray();
        aChildren.Select(e => e.Value.Target).Should().Equal(3, -1);
        document.Entries(document.ObjectFields(2))[0].Value.Should().Be(PackedValue.Ref(0, "Root#0"));
    }

    [Fact]
    public void LargeMapsSortToo() {
        var document = new PackedDocument();
        var map = document.AddMap();
        var keys = Enumerable.Range(0, 100).Select(i => $"k{(i * 37) % 100:D3}").ToArray(); // A shuffled order.
        foreach (var key in keys) { document.Add(map, key, PackedValue.Null); }

        document.TrySeal(out _, out _).Should().BeTrue();
        Keys(document, map).Should().Equal(keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void RefsPastTheLastObjectBecomeMissing() {
        var document = new PackedDocument();
        var o = document.AddObject("Ship#0");
        document.Add(document.ObjectFields(o), "far", PackedValue.Ref(5, "Ship#0"));
        document.Add(document.ObjectFields(o), "negative", PackedValue.Ref(-7, "Ship#0"));
        document.SetRoot(PackedValue.Ref(o, "Ship#0"));
        document.TrySeal(out _, out _);

        document.Canonicalize();

        document.Entries(document.ObjectFields(0)).ToArray().Select(e => e.Value.Target).Should().Equal(-1, -1);
    }

    [Fact]
    public void AReusedDocumentStopsAllocating() {
        var keys = new[] { "c", "b", "a", "e", "d" };
        var document = new PackedDocument();
        void Fill() {
            document.Clear();
            for (var i = 0; i < 200; i++) {
                var o = document.AddObject("Ship#0");
                foreach (var key in keys) { document.Add(document.ObjectFields(o), key, PackedValue.Integer(i)); }
            }
            document.SetRoot(PackedValue.Ref(0, "Ship#0"));
            document.TrySeal(out _, out _);
            document.Canonicalize();
        }

        Fill();
        Fill();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Fill();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void ClearKeepsTheDocumentUsable() {
        var document = new PackedDocument();
        document.AddObject("Ship#0");
        document.SetRoot(PackedValue.Ref(0, "Ship#0"));
        document.TrySeal(out _, out _);

        document.Clear();

        (document.ObjectCount, document.IsSealed, document.Root).Should().Be((0, false, PackedValue.Null));
        var o = document.AddObject("Crew#1");
        document.Add(document.ObjectFields(o), "n", PackedValue.Integer(3));
        document.TrySeal(out _, out _).Should().BeTrue();
        document.Entries(document.ObjectFields(o))[0].Value.AsInteger.Should().Be(3);
    }

    [Fact]
    public void BuildingRulesAreEnforced() {
        var document = new PackedDocument();
        var map = document.AddMap();
        var array = document.AddArray();

        var act = () => document.Add(array, "k", PackedValue.Null);
        act.Should().Throw<InvalidOperationException>();
        act = () => document.Add(map, PackedValue.Null);
        act.Should().Throw<InvalidOperationException>();
        act = () => document.SetRoot(PackedValue.Integer(1));
        act.Should().Throw<ArgumentException>();
        var read = () => document.Entries(map).Length;
        read.Should().Throw<InvalidOperationException>().WithMessage("Seal the document first.");

        document.TrySeal(out _, out _);
        act = () => document.Add(map, "k", PackedValue.Null);
        act.Should().Throw<InvalidOperationException>().WithMessage("The document is sealed.");
    }
}
