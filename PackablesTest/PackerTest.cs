using FluentAssertions;

namespace Yewnyx.Packables.Test;

public class PackerTest {
    private sealed class Crew : IPackable {
        public string? Name;
        public int Age;
        public Crew? Friend;

        public void PackInto(Packer packer) {
            packer.WriteString("name", Name);
            packer.WriteInt32("age", Age);
            packer.WriteRef("friend", Friend);
        }

        public void UnpackFrom(Unpacker unpacker) {
            unpacker.TryReadString("name", out Name);
            unpacker.TryReadInt32("age", out Age);
            unpacker.TryReadRef("friend", static () => new Crew(), out Friend);
        }
    }

    private sealed class Ship : IPackable {
        public string? Name;
        public double Speed;
        public Dictionary<string, Crew> Roster = new();
        public List<Crew?> Watch = new();
        public double[] Position = [];
        public int ReadVersion = -1;

        public void PackInto(Packer packer) {
            // Written out of key order on purpose: keys are sorted when the document is sealed.
            packer.WriteDouble("speed", Speed);
            packer.WriteString("name", Name);
            var roster = packer.Map("roster");
            foreach (var (role, crew) in Roster) { roster.WriteRef(role, crew); }
            var watch = packer.Array("watch");
            foreach (var crew in Watch) { watch.WriteRef(crew); }
            packer.WriteDoubles("position", Position);
        }

        public void UnpackFrom(Unpacker unpacker) {
            ReadVersion = unpacker.Version;
            unpacker.TryReadString("name", out Name);
            unpacker.TryReadDouble("speed", out Speed);
            if (unpacker.TryReadMap("roster", out var roster)) {
                foreach (var entry in roster) {
                    if (entry.Value.TryReadRef(static () => new Crew(), out Crew? crew)) { Roster[entry.Key] = crew!; }
                }
            }
            if (unpacker.TryReadArray("watch", out var watch)) {
                foreach (var element in watch) {
                    if (element.TryReadRef(static () => new Crew(), out Crew? crew)) { Watch.Add(crew); }
                }
            }
            if (unpacker.TryReadArray("position", out var position)) {
                Position = new double[position.Count];
                position.TryReadDoubles(Position, out _);
            }
        }
    }

    private static readonly PackableRegistry Registry = new PackableRegistry.Builder()
        .Add(typeof(Ship), "Test.Ship", 2)
        .Add(typeof(Crew), "Test.Crew", 0)
        .Build();

    private static Ship MakeShip() {
        var ada = new Crew { Name = "Ada", Age = 36 };
        var bo = new Crew { Name = "Bo", Age = 29, Friend = ada };
        ada.Friend = bo; // A loop.
        return new Ship {
            Name = "Kestrel", Speed = 12.5,
            Roster = new() { ["pilot"] = ada, ["cook"] = bo },
            Watch = [bo, null, ada], // Shared with the roster.
            Position = [1, -0.0, 3.5],
        };
    }

    private static PackedDocument Pack(IPackable root) {
        var document = new PackedDocument();
        Packer.TryPack(root, Registry, document, out var unregistered).Should().BeTrue();
        unregistered.Should().BeNull();
        return document;
    }

    [Fact]
    public void RoundTrips() {
        var document = Pack(MakeShip());
        var ship = new Ship();
        var problems = new List<UnpackProblem>();

        Packer.TryPack(MakeShip(), Registry, document, out _);
        Unpacker.Unpack(document, ship, Registry, problems).Should().Be(UnpackOutcome.Clean);

        problems.Should().BeEmpty();
        (ship.Name, ship.Speed, ship.ReadVersion).Should().Be(("Kestrel", 12.5, 2));
        ship.Position.Should().Equal(1, 0, 3.5);
        double.IsNegative(ship.Position[1]).Should().BeFalse(); // -0.0 was normalised.
        var (ada, bo) = (ship.Roster["pilot"], ship.Roster["cook"]);
        (ada.Name, ada.Age, bo.Name, bo.Age).Should().Be(("Ada", 36, "Bo", 29));
        ada.Friend.Should().BeSameAs(bo);
        bo.Friend.Should().BeSameAs(ada);
        ship.Watch.Should().Equal(bo, null, ada);
    }

    [Fact]
    public void FieldsAreSortedAndObjectsNumberedBreadthFirst() {
        var document = Pack(MakeShip());

        document.Entries(document.ObjectFields(0)).ToArray().Select(e => e.Key)
            .Should().Equal("name", "position", "roster", "speed", "watch");
        // Breadth-first from the ship, keys in order: roster's "cook" (Bo) comes before "pilot" (Ada).
        Enumerable.Range(0, document.ObjectCount).Select(document.ObjectType)
            .Should().Equal("Test.Ship#2", "Test.Crew#0", "Test.Crew#0");
        var roster = document.Entries(document.ObjectFields(0))[2].Value.Container;
        document.Entries(roster).ToArray().Select(e => (e.Key, e.Value.Target)).Should().Equal(("cook", 1), ("pilot", 2));
    }

    [Fact]
    public void PackingNeedsEveryTypeRegistered() {
        var registry = new PackableRegistry.Builder().Add(typeof(Ship), "Test.Ship", 2).Build();
        Packer.TryPack(MakeShip(), registry, new PackedDocument(), out var unregistered).Should().BeFalse();
        unregistered.Should().Be(typeof(Crew));
    }

    private sealed class Twice : IPackable {
        public void PackInto(Packer packer) {
            packer.WriteInt32("x", 1);
            packer.WriteInt32("x", 2);
        }

        public void UnpackFrom(Unpacker unpacker) { }
    }

    [Fact]
    public void WritingAKeyTwiceThrows() {
        var registry = new PackableRegistry.Builder().Add(typeof(Twice), "Test.Twice", 0).Build();
        var act = () => Packer.TryPack(new Twice(), registry, new PackedDocument(), out _);
        act.Should().Throw<InvalidOperationException>().WithMessage("\"x\" was written twice.");
    }

    private sealed class Samples : IPackable {
        public int[] Counts = [];
        public long[] Ticks = [];
        public float[] Levels = [];

        public void PackInto(Packer packer) {
            packer.WriteInt32s("counts", Counts);
            packer.WriteInt64s("ticks", Ticks);
            packer.WriteSingles("levels", Levels);
        }

        public void UnpackFrom(Unpacker unpacker) {
            if (unpacker.TryReadArray("counts", out var counts)) { counts.TryReadInt32s(Counts = new int[counts.Count], out _); }
            if (unpacker.TryReadArray("ticks", out var ticks)) { ticks.TryReadInt64s(Ticks = new long[ticks.Count], out _); }
            if (unpacker.TryReadArray("levels", out var levels)) { levels.TryReadSingles(Levels = new float[levels.Count], out _); }
        }
    }

    [Fact]
    public void ArraysOfEachNumberTypeGoInAndComeBackWithoutWidening() {
        var registry = new PackableRegistry.Builder().Add(typeof(Samples), "Test.Samples", 0).Build();
        var document = new PackedDocument();
        var samples = new Samples { Counts = [int.MinValue, 0, 7], Ticks = [long.MaxValue], Levels = [0.1f, -2.5f, float.MaxValue] };
        Packer.TryPack(samples, registry, document, out _).Should().BeTrue();

        var read = new Samples();
        Unpacker.Unpack(document, read, registry).Should().Be(UnpackOutcome.Clean);
        read.Counts.Should().Equal(int.MinValue, 0, 7);
        read.Ticks.Should().Equal(long.MaxValue);
        read.Levels.Should().Equal(0.1f, -2.5f, float.MaxValue);
    }

    private sealed class Narrow : IPackable {
        public (bool Read, int Count) TooWide, TooMany;

        public void PackInto(Packer packer) {
            packer.WriteInt64s("wide", [1, long.MaxValue]);
            packer.WriteInt32s("three", [1, 2, 3]);
        }

        public void UnpackFrom(Unpacker unpacker) {
            Span<int> two = stackalloc int[2];
            if (unpacker.TryReadArray("wide", out var wide)) { TooWide = (wide.TryReadInt32s(two, out var count), count); }
            if (unpacker.TryReadArray("three", out var three)) { TooMany = (three.TryReadInt32s(two, out var count), count); }
        }
    }

    [Fact]
    public void ArrayReadsRecordValuesOutOfRangeAndTooLittleRoom() {
        var registry = new PackableRegistry.Builder().Add(typeof(Narrow), "Test.Narrow", 0).Build();
        var document = new PackedDocument();
        Packer.TryPack(new Narrow(), registry, document, out _).Should().BeTrue();
        var problems = new List<UnpackProblem>();

        var narrow = new Narrow();
        Unpacker.Unpack(document, narrow, registry, problems).Should().Be(UnpackOutcome.WithProblems);
        (narrow.TooWide, narrow.TooMany).Should().Be(((false, 1), (false, 0))); // How many were read before stopping.
        problems.Select(p => (p.Kind, p.Key)).Should().Equal(
            (UnpackProblemKind.OutOfRange, "wide"), (UnpackProblemKind.OutOfRange, "three"));
    }

    // A one-object document with a ship's type and the given fields.
    private static PackedDocument Hand(string shipType, Action<PackedDocument, int> fill) {
        var document = new PackedDocument();
        var ship = document.AddObject(shipType);
        fill(document, document.ObjectFields(ship));
        document.SetRoot(PackedValue.Ref(ship, shipType));
        document.TrySeal(out _, out _).Should().BeTrue();
        return document;
    }

    [Fact]
    public void AbsentKeysRecordNothingAndWrongKindsAreRecorded() {
        var document = Hand("Test.Ship#2", (d, fields) => {
            d.Add(fields, "name", PackedValue.Integer(7));
            d.Add(fields, "speed", PackedValue.String("fast"));
        });
        var problems = new List<UnpackProblem>();

        var ship = new Ship();
        Unpacker.Unpack(document, ship, Registry, problems).Should().Be(UnpackOutcome.WithProblems);

        problems.Select(p => (p.Kind, p.Key)).Should().Equal(
            (UnpackProblemKind.WrongKind, "name"), (UnpackProblemKind.WrongKind, "speed"));
        problems[0].Type.Should().Be("Test.Ship#2");
        (ship.Name, ship.Speed).Should().Be((null, 0));
    }

    [Fact]
    public void NumbersOutOfRangeAreRecorded() {
        var document = new PackedDocument();
        var crew = document.AddObject("Test.Crew#0");
        document.Add(document.ObjectFields(crew), "age", PackedValue.Integer(long.MaxValue));
        document.SetRoot(PackedValue.Ref(crew, "Test.Crew#0"));
        document.TrySeal(out _, out _);
        var problems = new List<UnpackProblem>();

        Unpacker.Unpack(document, new Crew(), Registry, problems).Should().Be(UnpackOutcome.WithProblems);
        problems.Single().Kind.Should().Be(UnpackProblemKind.OutOfRange);
    }

    [Fact]
    public void BadRefsAreRecordedAndNothingIsInvented() {
        var document = new PackedDocument();
        var crew = document.AddObject("Test.Crew#0");
        var friend = document.AddObject("Test.Crew#0");
        document.Add(document.ObjectFields(crew), "friend", PackedValue.Ref(friend, "Test.Ship#2")); // Says Ship.
        document.Add(document.ObjectFields(friend), "friend", PackedValue.Ref(-1, "Test.Crew#0")); // Missing.
        document.SetRoot(PackedValue.Ref(crew, "Test.Crew#0"));
        document.TrySeal(out _, out _);
        var problems = new List<UnpackProblem>();

        var root = new Crew();
        Unpacker.Unpack(document, root, Registry, problems).Should().Be(UnpackOutcome.WithProblems);

        problems.Select(p => p.Kind).Should().Equal(UnpackProblemKind.TypeMismatch);
        root.Friend.Should().BeNull();
    }

    [Fact]
    public void UnknownTypesFailAStrictLoadBeforeAnythingIsRead() {
        var document = Hand("Test.Ship#2", (d, fields) => {
            d.Add(fields, "name", PackedValue.String("Kestrel"));
            var stranger = d.AddObject("Someone.Else#0");
            d.Add(fields, "watch", PackedValue.Array(_watch(d, PackedValue.Ref(stranger, "Someone.Else#0"))));
        });
        var problems = new List<UnpackProblem>();

        var strict = new Ship();
        Unpacker.Unpack(document, strict, Registry, problems).Should().Be(UnpackOutcome.Failed);
        strict.Name.Should().BeNull();
        problems.Single().Should().Be(new UnpackProblem(UnpackProblemKind.UnknownType, 1, "Someone.Else#0", null));

        // Permissive: recorded, and the unknown object is skipped.
        problems.Clear();
        var lenient = new Ship();
        Unpacker.Unpack(document, lenient, Registry, problems, UnpackOptions.Permissive).Should().Be(UnpackOutcome.WithProblems);
        lenient.Name.Should().Be("Kestrel");
        lenient.Watch.Should().BeEmpty();
        problems.Select(p => p.Kind).Should().Equal(UnpackProblemKind.UnknownType);
    }

    private static int _watch(PackedDocument document, PackedValue element) {
        var array = document.AddArray();
        document.Add(array, element);
        return array;
    }

    [Fact]
    public void VersionsNewerThanTheRegistryAreRefusedAndOlderOnesAreShown() {
        var newer = Hand("Test.Ship#3", (_, _) => { });
        Unpacker.Unpack(newer, new Ship(), Registry).Should().Be(UnpackOutcome.Failed);

        var older = Hand("Test.Ship#1", (_, _) => { });
        var ship = new Ship();
        Unpacker.Unpack(older, ship, Registry).Should().Be(UnpackOutcome.Clean);
        ship.ReadVersion.Should().Be(1);
    }

    [Fact]
    public void ARootPastTheLastObjectOrOfAnotherTypeFails() {
        var problems = new List<UnpackProblem>();
        var past = new PackedDocument();
        past.AddObject("Test.Ship#2");
        past.SetRoot(PackedValue.Ref(5, "Test.Ship#2"));
        past.TrySeal(out _, out _);
        Unpacker.Unpack(past, new Ship(), Registry, problems).Should().Be(UnpackOutcome.Failed);

        var empty = new PackedDocument();
        empty.SetRoot(PackedValue.Ref(0, "Test.Ship#2"));
        empty.TrySeal(out _, out _);
        Unpacker.Unpack(empty, new Ship(), Registry, problems).Should().Be(UnpackOutcome.Failed);

        // The root is a ship, but its ref says crew.
        var wrong = new PackedDocument();
        var o = wrong.AddObject("Test.Ship#2");
        wrong.SetRoot(PackedValue.Ref(o, "Test.Crew#0"));
        wrong.TrySeal(out _, out _);
        Unpacker.Unpack(wrong, new Ship(), Registry, problems).Should().Be(UnpackOutcome.Failed);

        problems.Select(p => p.Kind).Should().Equal(
            UnpackProblemKind.NoRoot, UnpackProblemKind.NoRoot, UnpackProblemKind.TypeMismatch);
    }

    [Fact]
    public void FailFastEndsTheLoadAtTheFirstProblem() {
        var document = Hand("Test.Ship#2", (d, fields) => {
            d.Add(fields, "name", PackedValue.Integer(7));
            d.Add(fields, "speed", PackedValue.Double(3));
        });
        var problems = new List<UnpackProblem>();

        var ship = new Ship();
        Unpacker.Unpack(document, ship, Registry, problems, UnpackOptions.FailFast).Should().Be(UnpackOutcome.Failed);

        problems.Should().ContainSingle();
        ship.Speed.Should().Be(0); // Read after the failure, so not read at all.
    }

    private sealed class Captain : IPackable {
        public Crew? First, Second;

        public void PackInto(Packer packer) {
            packer.WriteRef("first", First);
            packer.WriteRef("second", Second);
        }

        public void UnpackFrom(Unpacker unpacker) {
            // The caller makes children, here with a name set before filling.
            if (unpacker.TryReadRef("first", out PackedRef found) && !found.IsNull) {
                First = unpacker.Bind(found, new Crew { Name = "made by the captain" });
            }
            if (unpacker.TryReadRef("second", out found) && !found.IsNull) {
                found.Type.Should().Be(typeof(Crew));
                Second = found.Instance as Crew; // Already bound through "first".
            }
        }
    }

    [Fact]
    public void CallersBindTheObjectsTheyMake() {
        var registry = new PackableRegistry.Builder()
            .Add(typeof(Captain), "Test.Captain", 0).Add(typeof(Crew), "Test.Crew", 0).Build();
        var shared = new Crew { Name = "Ada", Age = 36 };
        var document = new PackedDocument();
        Packer.TryPack(new Captain { First = shared, Second = shared }, registry, document, out _).Should().BeTrue();

        var captain = new Captain();
        Unpacker.Unpack(document, captain, registry).Should().Be(UnpackOutcome.Clean);

        captain.First!.Name.Should().Be("Ada"); // Filled after binding, overwriting the made-up name.
        captain.Second.Should().BeSameAs(captain.First);
    }

    // Reads each key as a crew with a create function that notes the key it was called for, passing
    // the probe and key as state, so the lambda captures nothing.
    private sealed class Probe : IPackable {
        public readonly List<string> Created = new();
        public readonly Dictionary<string, (bool Read, Crew? Crew)> Reads = new();

        public void PackInto(Packer packer) { }

        public void UnpackFrom(Unpacker unpacker) {
            foreach (var key in new[] { "null", "missing", "mismatch", "unknown", "first", "again", "ship" }) {
                var read = unpacker.TryReadRef(key, (Probe: this, Key: key), static s => {
                    s.Probe.Created.Add(s.Key);
                    return new Crew();
                }, out Crew? crew);
                Reads[key] = (read, crew);
            }
        }
    }

    [Fact]
    public void CreateIsCalledOnlyForAnObjectNotBoundYet() {
        var document = new PackedDocument();
        var probe = document.AddObject("Test.Probe#0");
        var crew = document.AddObject("Test.Crew#0");
        var stranger = document.AddObject("Someone.Else#0");
        var ship = document.AddObject("Test.Ship#2");
        var fields = document.ObjectFields(probe);
        document.Add(fields, "null", PackedValue.Null);
        document.Add(fields, "missing", PackedValue.Ref(-1, "Test.Crew#0"));
        document.Add(fields, "mismatch", PackedValue.Ref(crew, "Test.Ship#2")); // Says Ship; it's crew.
        document.Add(fields, "unknown", PackedValue.Ref(stranger, "Someone.Else#0"));
        document.Add(fields, "first", PackedValue.Ref(crew, "Test.Crew#0"));
        document.Add(fields, "again", PackedValue.Ref(crew, "Test.Crew#0")); // Bound by "first" already.
        document.Add(fields, "ship", PackedValue.Ref(ship, "Test.Ship#2")); // A ship isn't a crew.
        document.SetRoot(PackedValue.Ref(probe, "Test.Probe#0"));
        document.TrySeal(out _, out _).Should().BeTrue();
        var registry = new PackableRegistry.Builder()
            .Add<Probe>("Test.Probe", 0).Add<Crew>("Test.Crew", 0).Add<Ship>("Test.Ship", 2).Build();
        var problems = new List<UnpackProblem>();

        var root = new Probe();
        Unpacker.Unpack(document, root, registry, problems, UnpackOptions.Permissive).Should().Be(UnpackOutcome.WithProblems);

        root.Created.Should().Equal("first");
        root.Reads["null"].Should().Be((true, null));
        foreach (var key in new[] { "missing", "mismatch", "unknown", "ship" }) { root.Reads[key].Should().Be((false, null), key); }
        root.Reads["first"].Read.Should().BeTrue();
        root.Reads["again"].Crew.Should().BeSameAs(root.Reads["first"].Crew);
        problems.Select(p => (p.Kind, p.Key)).Should().Equal(
            (UnpackProblemKind.UnknownType, null), (UnpackProblemKind.MissingObject, "missing"),
            (UnpackProblemKind.TypeMismatch, "mismatch"), (UnpackProblemKind.WrongInstance, "ship"));
    }

    private sealed class Declines : IPackable {
        public (bool Read, Crew? Crew) Result;

        public void PackInto(Packer packer) => packer.WriteRef("crew", new Crew());
        public void UnpackFrom(Unpacker unpacker) => Result = (unpacker.TryReadRef<Crew>("crew", static () => null!, out var crew), crew);
    }

    [Fact]
    public void ACreateFunctionGivingNullIsRecorded() {
        var registry = new PackableRegistry.Builder().Add<Declines>("Test.Declines", 0).Add<Crew>("Test.Crew", 0).Build();
        var document = new PackedDocument();
        Packer.TryPack(new Declines(), registry, document, out _).Should().BeTrue();
        var problems = new List<UnpackProblem>();

        var declines = new Declines();
        Unpacker.Unpack(document, declines, registry, problems).Should().Be(UnpackOutcome.WithProblems);
        declines.Result.Should().Be((false, null));
        problems.Select(p => (p.Kind, p.Key)).Should().Equal((UnpackProblemKind.NotCreated, "crew"));
    }

    [Fact]
    public void BindingTheWrongTypeIsRecorded() {
        var document = Pack(new Crew { Name = "Ada" });
        var problems = new List<UnpackProblem>();
        Unpacker.Unpack(document, new Ship(), Registry, problems).Should().Be(UnpackOutcome.Failed);
        problems.Single().Kind.Should().Be(UnpackProblemKind.WrongInstance);
    }

    private sealed class Throws : IPackable {
        public void PackInto(Packer packer) { }
        public void UnpackFrom(Unpacker unpacker) => throw new FormatException("bad data");
    }

    [Fact]
    public void ExceptionsFromTypesCarryWhereTheyHappened() {
        var registry = new PackableRegistry.Builder().Add(typeof(Throws), "Test.Throws", 0).Build();
        var document = new PackedDocument();
        Packer.TryPack(new Throws(), registry, document, out _);

        var act = () => Unpacker.Unpack(document, new Throws(), registry);
        var thrown = act.Should().Throw<FormatException>().Which;
        (thrown.Data["Packables.Object"], thrown.Data["Packables.Type"]).Should().Be((0, "Test.Throws#0"));
    }
}
