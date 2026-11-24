using FluentAssertions;
using P = (float X, float Y, float Z);

namespace Yewnyx.Spatial.Test;

public class TreeFieldsTest {
    private const float Radius = 0.51f;
    private const float Unit = 0.02f;

    // A scene's tree and its regions for one size, with a field over the tree and the dense field from the same seeds.
    private sealed class Fielded {
        public readonly TreeScene Scene;
        public readonly SizeClass Size;
        public readonly byte[] Region, DenseRegion, Bytes;
        public readonly TravelSeed[] Seeds;
        public readonly ushort[] Dense;
        public readonly int Reached;

        public Fielded(GridShape shape, ShapeScene shapes, float radius, params TravelSeed[] seeds) {
            Scene = new TreeScene(shape, shapes.Set);
            var tree = Scene.Tree;
            SizeClass.TryMake(radius, tree, out Size).Should().BeTrue();
            Solid[] everywhere = [Solid.Box(new Box(-1000, -1000, -1000, 1000, 1000, 1000))];
            var region = new byte[TreeRegions.MostLength(tree)];
            TreeRegions.FromSeeds(tree, Size, everywhere, region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out var used, out _, out _).Should().BeTrue();
            Region = region[..used];
            DenseRegion = new byte[shape.CellCount];
            Regions.FromSeeds(Scene.Map, Size, default, everywhere, DenseRegion, new int[Regions.QueueLength(shape)], out _, out _).Should().BeTrue();
            Seeds = seeds;
            TreeFields.Measure(tree, Region, seeds, out var length, out var places, out var work).Should().BeTrue();
            var bytes = new byte[length];
            // The scratch need not be clear.
            var sums = Enumerable.Repeat(7f, places).ToArray();
            var ints = Enumerable.Repeat(-5, work).ToArray();
            TreeFields.Build(tree, Region, seeds, Unit, bytes, sums, ints, out var written, out Reached, out var cut).Should().BeTrue();
            (written, cut).Should().Be((length, 0));
            Bytes = bytes;
            Dense = new ushort[shape.CellCount];
            Travel.Build(shape, DenseRegion, default, seeds, Unit, Dense, new float[Travel.TimesLength(shape)], new int[Travel.HeapLength(shape)], out _, out _).Should().BeTrue();
        }

        public TreeField Field => new(Scene.Tree, Region, Bytes, Seeds, Unit);

        public bool TravelAt(P at, out float travel) => TreeFields.TravelAt(Field, at.X, at.Y, at.Z, out travel, out _);

        public bool DenseAt(P at, out float travel) => Travel.Sample(new TravelField(Scene.Grid, Unit, Dense), at.X, at.Y, at.Z, out _, out _, out _, out travel);

        public bool IsOpen(P at) => TreeRegions.IsOpen(Scene.Tree, Region, at.X, at.Y, at.Z);

        // A follower that flies to the point Next gives and asks again on entering another place or on reaching it.
        // How it ended, how far it flew, and how many of its steps ended in a cell that is not open.
        public (bool Arrived, double Length, int Out) Follow(P from) {
            float x = from.X, y = from.Y, z = from.Z, tx = 0, ty = 0, tz = 0;
            var place = -1;
            double length = 0;
            var out_ = 0;
            for (var i = 0; i < 4000; i++) {
                if (!TreeFields.TravelAt(Field, x, y, z, out var travel, out var here)) { return (false, length, out_); }
                if (travel < Scene.Grid.CellSize) { return (true, length, out_); }
                var left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
                if (here != place || left < 0.01f) {
                    if (!TreeFields.Next(Field, x, y, z, out tx, out ty, out tz, out _, out _)) { return (travel < Scene.Grid.CellSize, length, out_); }
                    place = here;
                    left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
                    if (left < 1e-6f) { return (false, length, out_); }
                }
                var step = MathF.Min(0.125f, left);
                x += (tx - x) / left * step; y += (ty - y) / left * step; z += (tz - z) / left * step;
                length += step;
                if (!IsOpen((x, y, z))) { out_++; }
            }
            return (false, length, out_);
        }

        // Whether a follower comes to rest: within a cell of the goal, or where Next says there is nowhere to go, before 4,000 steps.
        public bool ComesToRest(P from) {
            float x = from.X, y = from.Y, z = from.Z, tx = 0, ty = 0, tz = 0;
            var place = -1;
            for (var i = 0; i < 4000; i++) {
                if (!TreeFields.TravelAt(Field, x, y, z, out var travel, out var here)) { return false; }
                if (travel < Scene.Grid.CellSize) { return true; }
                var left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
                if (here != place || left < 0.01f) {
                    if (!TreeFields.Next(Field, x, y, z, out tx, out ty, out tz, out _, out _)) { return true; }
                    place = here;
                    left = MathF.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y) + (tz - z) * (tz - z));
                }
                var step = MathF.Min(0.125f, left);
                x += (tx - x) / left * step; y += (ty - y) / left * step; z += (tz - z) / left * step;
            }
            return false;
        }

        // A follower of the dense field, an eighth of a metre a step.
        public (bool Arrived, double Length) FollowDense(P from) {
            var field = new TravelField(Scene.Grid, Unit, Dense);
            float x = from.X, y = from.Y, z = from.Z;
            double length = 0;
            for (var i = 0; i < 8000; i++) {
                if (!Travel.Sample(field, x, y, z, out var dx, out var dy, out var dz, out var travel)) { return (false, length); }
                if (travel < Scene.Grid.CellSize) { return (true, length); }
                if (dx == 0 && dy == 0 && dz == 0) { return (false, length); }
                x += dx * 0.125f; y += dy * 0.125f; z += dz * 0.125f;
                length += 0.125;
            }
            return (false, length);
        }

        // The centres of open cells, every so many.
        public IEnumerable<P> OpenCentres(int every) {
            var shape = Scene.Grid;
            var k = 0;
            for (var z = 0; z < shape.CountZ; z++) {
                for (var y = 0; y < shape.CountY; y++) {
                    for (var x = 0; x < shape.CountX; x++) {
                        shape.CentreOf(x, y, z, out var cx, out var cy, out var cz);
                        if (!IsOpen((cx, cy, cz)) || k++ % every != 0) { continue; }
                        yield return (cx, cy, cz);
                    }
                }
            }
        }
    }

    private static readonly Solid[] Mixed = [
        Solid.Box(new Box(-1, -1, -1, 20, 0.3f, 20)),
        ShapeScene.TurnedBox(6, 3, 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(11, 4.5f, 8, 1.6f),
        Solid.Capsule(3, 1, 9, 14, 7, 2, 0.35f),
        Solid.Box(new Box(15.5f, 0, 0, 30, 30, 30)),
    ];

    private static readonly GridShape Odd = new(-0.3f, -0.7f, 0.2f, 0.5f, 37, 22, 29);

    // The same scene and grid moved out along x and z, where a float's step is tens of micrometres.
    private static Solid[] MixedAt(float ox, float oz) => [
        Solid.Box(new Box(ox - 1, -1, oz - 1, ox + 20, 0.3f, oz + 20)),
        ShapeScene.TurnedBox(ox + 6, 3, oz + 5, 2.5, 0.4, 1.2, 37, 21),
        Solid.Sphere(ox + 11, 4.5f, oz + 8, 1.6f),
        Solid.Capsule(ox + 3, 1, oz + 9, ox + 14, 7, oz + 2, 0.35f),
        Solid.Box(new Box(ox + 15.5f, 0, oz + 0, ox + 30, 30, oz + 30)),
    ];

    private static GridShape OddAt(float ox, float oz) => new(ox - 0.3f, -0.7f, oz + 0.2f, 0.5f, 37, 22, 29);

    // Followers from open cells all over a scene: every one the dense field brings in arrives, and
    // stays in open cells on the way; the travel and the way flown are near the dense field's.
    private static void FollowersArrive(Fielded fielded, int every, double travelWithin, double lengthWithin) {
        int tried = 0, arrived = 0;
        double length = 0, denseLength = 0;
        foreach (var start in fielded.OpenCentres(every)) {
            var dense = fielded.FollowDense(start);
            if (!dense.Arrived) { continue; }
            tried++;
            fielded.TravelAt(start, out var travel).Should().BeTrue($"at {start}");
            fielded.DenseAt(start, out var denseTravel).Should().BeTrue();
            if (denseTravel > 4f) { (travel / denseTravel).Should().BeInRange((float)(1d / travelWithin), (float)travelWithin, $"the travel at {start}"); }
            var (did, flown, out_) = fielded.Follow(start);
            did.Should().BeTrue($"a follower from {start} arrives");
            out_.Should().Be(0, $"a follower from {start} stays in open cells");
            arrived++;
            length += flown;
            denseLength += dense.Length;
        }
        tried.Should().BeGreaterThan(30);
        arrived.Should().Be(tried);
        (length / denseLength).Should().BeLessThan(lengthWithin);
    }

    [Fact]
    public void FollowersOfAPointArriveThroughOpenCellsAsTheDenseFieldsDo() {
        var goal = Solid.Sphere(2, 2.5f, 3, 1f);
        var fielded = new Fielded(Odd, new ShapeScene(Mixed), Radius, TravelSeed.FromPoint(goal, 2, 2.5f, 3));
        FollowersArrive(fielded, 13, 1.25, 1.15);
    }

    // Two rooms in rock joined by a passage, for a body of 0.9 m, seeded in the second room.
    [Fact]
    public void FollowersFindThePassageOutOfARoom() {
        var shape = new GridShape(0, 0, 0, 0.5f, 48, 48, 48);
        var fielded = new Fielded(shape, new ShapeScene(TreeScene.CubeRoom()), 0.9f, TravelSeed.FromPoint(Solid.Sphere(21.75f, 12, 12, 1f), 21.75f, 12, 12));
        FollowersArrive(fielded, 7, 1.3, 1.2);
    }

    // A wall across a room with a doorway in it, and a field that leads out through the doorway.
    private static Fielded Doorway(float radius = Radius) {
        var shape = new GridShape(0, 0, 0, 0.5f, 32, 16, 16);
        var shapes = new ShapeScene(TreeScene.Carved(new Box(-2, -2, -2, 18, 10, 10), new Box(0.2f, 0.2f, 0.2f, 15.8f, 7.8f, 7.8f)) .Concat(
            TreeScene.Carved(new Box(7.8f, 0.2f, 0.2f, 8.2f, 7.8f, 7.8f), new Box(7.8f, 2.5f, 0.2f, 8.2f, 5.5f, 4.5f))).ToArray());
        // The seed: a box round the doorway, the plane through it facing back into the first half.
        var door = Solid.Box(new Box(7, 2.5f, 0.2f, 9, 5.5f, 4.5f));
        return new Fielded(shape, shapes, radius, TravelSeed.FromPlane(door, 8, 4, 2.35f, -1, 0, 0));
    }

    [Fact]
    public void ADoorwaysFieldLeadsThroughItAndIsZeroBehindItsPlane() {
        var fielded = Doorway();
        FollowersArrive(fielded, 5, 1.3, 1.2);
        // Behind the plane, inside the seed: nothing to go.
        fielded.TravelAt((8.75f, 4.25f, 2.25f), out var behind).Should().BeTrue();
        behind.Should().Be(0f);
        // In front of it, inside the seed: how far in front.
        fielded.TravelAt((7.25f, 4.25f, 2.25f), out var ahead).Should().BeTrue();
        ahead.Should().BeApproximately(0.75f, 1e-4f);
        // Behind the plane there is nowhere to go: a follower there has arrived.
        TreeFields.Next(fielded.Field, 8.75f, 4.25f, 2.25f, out _, out _, out _, out _, out _).Should().BeFalse();
        // From there, square to the plane and just behind it.
        TreeFields.Next(fielded.Field, 7.25f, 4.25f, 2.25f, out var toX, out var toY, out var toZ, out _, out _).Should().BeTrue();
        (toX, toY, toZ).Should().Match<(float X, float Y, float Z)>(to => to.X > 8f && to.X < 8.1f && to.Y == 4.25f && to.Z == 2.25f);
    }

    [Fact]
    public void ASeedStartsNoCellBeyondAWall() {
        // A wall with a gap at one end; a ball of 3 m round a goal beside the wall reaches through it.
        var shape = new GridShape(0, 0, 0, 0.5f, 32, 16, 16);
        var shapes = new ShapeScene([Solid.Box(new Box(-1, -1, -1, 17, 0.2f, 9)), Solid.Box(new Box(7.8f, -1, -1, 8.2f, 6, 9))]);
        var fielded = new Fielded(shape, shapes, Radius, TravelSeed.FromPoint(Solid.Sphere(7, 4, 4, 3f), 7, 4, 4));
        // Just beyond the wall, inside the ball: reached round the end of the wall, not through it.
        fielded.TravelAt((9.25f, 4.25f, 4.25f), out var beyond).Should().BeTrue();
        beyond.Should().BeGreaterThan(5f, "round the end of the wall, not the 2.3 m through it");
        fielded.DenseAt((9.25f, 4.25f, 4.25f), out var dense).Should().BeTrue();
        (beyond / dense).Should().BeInRange(0.8f, 1.25f);
        fielded.Follow((9.25f, 4.25f, 4.25f)).Arrived.Should().BeTrue();
    }

    // The second seed is in a brick whose open cells are not convex: from some of them none of the cells it starts is in sight.
    // From the third's field, some lines to the point given pass through a corner where three cells meet. The fourth's
    // reaches bricks whose open cells are in two parts, and so two places. The fifth is 700 and 900 m out, where a point to fly to,
    // checked in doubles, can lie a float's rounding inside a closed cell.
    [Theory]
    [InlineData(2f, 2.5f, 3f, Radius)]
    [InlineData(14.2f, 6.2f, 2.2f, Radius)]
    [InlineData(1f, 1f, 1f, 0.33f)]
    [InlineData(1f, 1f, 3.4f, 0.33f)]
    [InlineData(705.6f, 3.6f, 903.4f, 0.33f, 700f, 900f)]
    public void EveryReachedPointButTheSeedsOwnHasAPointToFlyTo(float x, float y, float z, float radius, float ox = 0f, float oz = 0f) {
        var fielded = new Fielded(OddAt(ox, oz), new ShapeScene(MixedAt(ox, oz)), radius, TravelSeed.FromPoint(Solid.Sphere(x, y, z, 1f), x, y, z));
        var asked = 0;
        foreach (var at in fielded.OpenCentres(1)) {
            if (!fielded.TravelAt(at, out var travel)) { continue; }
            asked++;
            // No way is shorter than the straight one, and a count is rounded to the nearest unit.
            var straight = MathF.Sqrt((at.X - x) * (at.X - x) + (at.Y - y) * (at.Y - y) + (at.Z - z) * (at.Z - z));
            travel.Should().BeGreaterThanOrEqualTo(straight - Unit / 2f - 1e-3f, $"the travel at {at}");
            var next = TreeFields.Next(fielded.Field, at.X, at.Y, at.Z, out var toX, out var toY, out var toZ, out var said, out _);
            if (!next) {
                travel.Should().BeLessThan(0.5f, $"only at the seed's point is there nowhere to go, not at {at}");
                continue;
            }
            said.Should().BeGreaterThanOrEqualTo(0f);
            // The whole way to it, a centimetre at a time.
            var length = MathF.Sqrt((toX - at.X) * (toX - at.X) + (toY - at.Y) * (toY - at.Y) + (toZ - at.Z) * (toZ - at.Z));
            length.Should().BeGreaterThan(0f, $"the point to fly to from {at} is not the point itself");
            var steps = (int)(length / 0.01f) + 1;
            for (var i = 1; i <= steps; i++) {
                var f = (float)i / steps;
                fielded.IsOpen((at.X + (toX - at.X) * f, at.Y + (toY - at.Y) * f, at.Z + (toZ - at.Z) * f)).Should().BeTrue($"the way from {at} to the point to fly to, ({toX}, {toY}, {toZ}), stays in open cells");
            }
        }
        asked.Should().BeGreaterThan(1000);
        TreeFields.Next(fielded.Field, x, y, z, out _, out _, out _, out _, out _).Should().BeFalse("at the seed's own point there is nowhere to go");
        // Followers that start near the seed come to rest, even where its point is in a closed cell and can only be come near.
        foreach (var at in fielded.OpenCentres(1).Where(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) + (p.Z - z) * (p.Z - z) < 4f)) {
            if (!fielded.TravelAt(at, out _)) { continue; }
            fielded.ComesToRest(at).Should().BeTrue($"a follower from {at} comes to rest by the seed");
        }
    }

    [Fact]
    public void AFieldReachesNoCellTheRegionsDoNot() {
        // A sheet across the whole grid: the regions are two, and a field seeded in one never reaches the other.
        var shape = new GridShape(0, 0, 0, 0.5f, 40, 10, 20);
        var shapes = new ShapeScene([], [9.1f, -5, -5, 9.1f, 30, -5, 9.1f, 30, 30, 9.1f, -5, 30], [0, 1, 2, 0, 2, 3]);
        var fielded = new Fielded(shape, shapes, Radius, TravelSeed.FromPoint(Solid.Sphere(4, 2.5f, 5, 1f), 4, 2.5f, 5));
        foreach (var at in fielded.OpenCentres(3)) {
            var reached = fielded.TravelAt(at, out _);
            reached.Should().Be(at.X < 9.1f, $"at {at}");
        }
        // Not in the grid, not a number, in a closed cell: refused.
        fielded.TravelAt((-1f, 2f, 2f), out _).Should().BeFalse();
        fielded.TravelAt((float.NaN, 2f, 2f), out _).Should().BeFalse();
        fielded.TravelAt((9.0f, 2.25f, 2.25f), out _).Should().BeFalse("within the radius of the sheet");
    }

    [Fact]
    public void APointOnAFaceIsInTheHigherCellAsOverADenseMap() {
        // The cells at x 18 to 18.5 m are open and those below 18 are not, or the other way: a point at exactly 18 is in the higher.
        var shape = new GridShape(0, 0, 0, 0.5f, 40, 10, 20);
        var shapes = new ShapeScene([Solid.Box(new Box(-1, -1, -1, 17.6f, 11, 21))]);
        var fielded = new Fielded(shape, shapes, Radius, TravelSeed.FromPoint(Solid.Sphere(19.25f, 2.5f, 5, 0.7f), 19.25f, 2.5f, 5));
        shape.TryCentre(18f, 2.25f, 5.25f, out var centreX, out _, out _).Should().BeTrue();
        centreX.Should().Be(18.25f, "the higher cell");
        fielded.IsOpen((18.25f, 2.25f, 5.25f)).Should().BeTrue();
        fielded.IsOpen((17.75f, 2.25f, 5.25f)).Should().BeFalse();
        fielded.TravelAt((18f, 2.25f, 5.25f), out _).Should().BeTrue("on the face, in the cell above");
        fielded.TravelAt((MathF.BitDecrement(18f), 2.25f, 5.25f), out _).Should().BeFalse("a hair below the face, in the closed cell");
        Regions.IsOpen(shape, fielded.DenseRegion, 18f, 2.25f, 5.25f).Should().BeTrue("as over the dense map");
    }

    [Fact]
    public void ThePlacesRoundAPointGiveEveryFieldItsTravelAsTravelAtDoes() {
        // Two fields over the same tree and regions, from two points; the places found once serve both.
        var one = new Fielded(Odd, new ShapeScene(Mixed), Radius, TravelSeed.FromPoint(Solid.Sphere(2, 2.5f, 3, 1f), 2, 2.5f, 3));
        TravelSeed[] otherSeeds = [TravelSeed.FromPoint(Solid.Sphere(13, 6, 12, 1f), 13, 6, 12)];
        TreeFields.Measure(one.Scene.Tree, one.Region, otherSeeds, out var length, out var places, out var work).Should().BeTrue();
        var otherBytes = new byte[length];
        TreeFields.Build(one.Scene.Tree, one.Region, otherSeeds, Unit, otherBytes, new float[places], new int[work], out _, out _, out _).Should().BeTrue();
        var other = new TreeField(one.Scene.Tree, one.Region, otherBytes, otherSeeds, Unit);
        var near = new TreeNear[TreeFields.MostNear];
        var asked = 0;
        foreach (var at in one.OpenCentres(1).Concat(new P[] { (2.1f, 2.4f, 3.2f), (13.1f, 6, 11.9f) })) {
            var found = TreeFields.Around(one.Field, at.X, at.Y, at.Z, near, out var count);
            TreeFields.PlaceAt(one.Field, at.X, at.Y, at.Z, out var place).Should().Be(found);
            if (!found) { continue; }
            near[0].Place.Should().Be(place);
            foreach (var field in new[] { 0, 1 }) {
                var f = field == 0 ? one.Field : other;
                var at1 = TreeFields.TravelAt(f, at.X, at.Y, at.Z, out var travel, out var inPlace);
                TreeFields.TravelNear(f, at.X, at.Y, at.Z, near.AsSpan(0, count), out var fromNear).Should().Be(at1, $"at {at}");
                if (!at1) { continue; }
                asked++;
                inPlace.Should().Be(place);
                fromNear.Should().BeApproximately(travel, 1e-4f, $"at {at}");
            }
        }
        asked.Should().BeGreaterThan(2000);
        // Too little room for the places, and a place that is not the field's, are refused.
        // Too little room: false, with how many places there are; with just that room, true.
        TreeFields.Around(one.Field, 2.1f, 2.4f, 3.2f, near, out var all).Should().BeTrue();
        TreeFields.Around(one.Field, 2.1f, 2.4f, 3.2f, near.AsSpan(0, all - 1), out var needed).Should().BeFalse();
        needed.Should().Be(all);
        TreeFields.Around(one.Field, 2.1f, 2.4f, 3.2f, near.AsSpan(0, all), out _).Should().BeTrue();
        TreeFields.TravelNear(one.Field, 2.1f, 2.4f, 3.2f, [new TreeNear { Place = int.MaxValue }], out _).Should().BeFalse();
        TreeFields.TravelNear(one.Field, 2.1f, 2.4f, 3.2f, [], out _).Should().BeFalse();
        var round = all;
        near[round - 1].Place = int.MaxValue;
        TreeFields.TravelNear(other, 2.1f, 2.4f, 3.2f, near.AsSpan(0, round), out _).Should().BeFalse();
    }

    [Fact]
    public void APlaceTooFarForACountIsLeftUnreachedAndCut() {
        // 100 m of open air, and a unit of a millimetre: no more than about 65 m can be counted.
        var shape = new GridShape(0, 0, 0, 0.5f, 200, 8, 8);
        var tree = new TreeScene(shape, new ShapeScene([]).Set).Tree;
        SizeClass.TryMake(Radius, tree, out var size).Should().BeTrue();
        var region = new byte[TreeRegions.MostLength(tree)];
        TreeRegions.FromSeeds(tree, size, [Solid.Sphere(1, 2, 2, 0.5f)], region, new ulong[TreeRegions.MarksLength(tree)], new int[TreeRegions.QueueLength(tree)], out var used, out _, out _).Should().BeTrue();
        region = region[..used];
        TravelSeed[] seeds = [TravelSeed.FromPoint(Solid.Sphere(1, 2, 2, 0.5f), 1, 2, 2)];
        TreeFields.Measure(tree, region, seeds, out var length, out var places, out var work).Should().BeTrue();
        var bytes = new byte[length];
        TreeFields.Build(tree, region, seeds, 0.001f, bytes, new float[places], new int[work], out _, out var reached, out var cut).Should().BeTrue();
        (reached, cut).Should().Match<(int Reached, int Cut)>(r => r.Reached > 0 && r.Cut > 0);
        var field = new TreeField(tree, region, bytes, seeds, 0.001f);
        TreeFields.TravelAt(field, 30, 2, 2, out var near, out _).Should().BeTrue();
        near.Should().BeInRange(29f - 0.001f, 29f * 1.05f);
        TreeFields.TravelAt(field, 95, 2, 2, out _, out _).Should().BeFalse("94 m is too far for a count");
        TreeFields.Next(field, 95, 2, 2, out _, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ABuildIsRefusedForWhatItCannotUse() {
        var fielded = new Fielded(Odd, new ShapeScene(Mixed), Radius, TravelSeed.FromPoint(Solid.Sphere(2, 2.5f, 3, 1f), 2, 2.5f, 3));
        var tree = fielded.Scene.Tree;
        TreeFields.Measure(tree, fielded.Region, fielded.Seeds, out var length, out var places, out var work).Should().BeTrue();
        bool Builds(float unit, int lengthLess = 0, int placesLess = 0, int workLess = 0, byte[]? region = null) =>
            TreeFields.Build(fielded.Scene.Tree, region ?? fielded.Region, fielded.Seeds, unit, new byte[length - lengthLess], new float[places - placesLess], new int[work - workLess], out _, out _, out _);
        Builds(Unit).Should().BeTrue();
        Builds(0f).Should().BeFalse();
        Builds(0.2f).Should().BeFalse("more than a quarter of a cell");
        Builds(Unit, lengthLess: 1).Should().BeFalse();
        Builds(Unit, placesLess: 1).Should().BeFalse();
        Builds(Unit, workLess: 1).Should().BeFalse();
        Builds(Unit, region: fielded.Region[..^1]).Should().BeFalse("regions that are not a flood's of this tree");
        TreeFields.Measure(tree, fielded.Region, [TravelSeed.FromPlane(Solid.Sphere(2, 2, 2, 1), 2, 2, 2, 0, 0, 0)], out _, out _, out _).Should().BeFalse("a plane with no normal");
        TreeFields.Measure(tree, fielded.Region, [TravelSeed.FromPoint(Solid.Sphere(2, 2, 2, 1), float.NaN, 2, 2)], out _, out _, out _).Should().BeFalse("a point that is not a number");
        var many = Enumerable.Range(0, TreeFields.MostSeeds + 1).Select(i => TravelSeed.FromPoint(Solid.Sphere(2, 2.5f, 3, 0.2f), 2, 2.5f, 3)).ToArray();
        TreeFields.Measure(tree, fielded.Region, many.AsSpan(0, TreeFields.MostSeeds), out _, out _, out _).Should().BeTrue();
        TreeFields.Measure(tree, fielded.Region, many, out _, out _, out _).Should().BeFalse("a started cell keeps its seed in a byte");
        // A field asked with the wrong bytes, seeds or unit answers nothing.
        var at = fielded.OpenCentres(97).First(p => fielded.TravelAt(p, out _));
        TreeFields.TravelAt(new TreeField(tree, fielded.Region, fielded.Bytes[..^1], fielded.Seeds, Unit), at.X, at.Y, at.Z, out _, out _).Should().BeFalse();
        TreeFields.TravelAt(new TreeField(tree, fielded.Region, fielded.Bytes, fielded.Seeds, 0f), at.X, at.Y, at.Z, out _, out _).Should().BeFalse();
        // Bytes of the right length that say one brick more and one cube fewer than the tree has.
        var other = fielded.Bytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(0), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(other) + 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(4), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(other.AsSpan(4)) - 1);
        TreeFields.TravelAt(new TreeField(tree, fielded.Region, other, fielded.Seeds, Unit), at.X, at.Y, at.Z, out _, out _).Should().BeFalse();
        TreeFields.TravelAt(new TreeField(tree, fielded.Region, fielded.Bytes, fielded.Seeds, Unit), at.X, at.Y, at.Z, out _, out _).Should().BeTrue();
    }
}
