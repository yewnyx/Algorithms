using System.Buffers.Binary;
using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class HeightFieldBlockTest {
    // A sphere, and ground of 2 by 2 squares of a metre that is flat but for a peak of a metre in
    // the middle, with its first corner at (1, 0, 2) and solid down to -1.
    private static readonly Solid[] Solids = [Solid.Sphere(5, 5, 5, 0.5f)];
    private static readonly float[] Given = [0, 0, 0, 0, 1, 0, 0, 0, 0];

    // Where each part of that block starts: 1 solid, 1 heightfield, 1 block, 9 kept heights (and two bytes to fill), 2 boxes, 2 order entries, 1 node.
    private const int SolidsAt = 40, FieldsAt = 104, BlocksAt = 196, HeightsAt = 200, BoxesAt = 220, OrderAt = 268, NodesAt = 276, Length = 308;

    // Where a heightfield keeps each of these, from its own start.
    private const int X = 0, SizeU = 48, CountU = 56, Lowest = 64, Step = 68, Floor = 72, Flags = 80, FirstBlock = 84, FirstHeight = 88;

    // The block those shapes make, byte for byte. If this changes, the layout, the packing or the
    // tree changed: raise Shapes.Version, so that stored blocks are made again and not misread.
    private const string Fixed =
        "59534850030000000100000000000000000000000100000001000000010000000900000000000000" +
        "010000000000A0400000A0400000A0400000000000000000000000000000000000000000000000000000000000000000000000000000003F0000000000000000" +
        "0000803F00000000000000400000803F0000000000000000000000000000803F0000000000000000000000000000803F0000803F0000803F020000000200000000000000AA00A037000080BF00EF7F35000000000000000000000000" +
        "00000000" +
        "0000000000000000CCCC00000000000000000000" +
        "00F88F4000F88F4000F88F400008B0400008B0400008B04000407F3F006080BF00A0FF3F003040400A60A03F00188040" +
        "0100000000000000" +
        "00407F3F006080BF00A0FF3F0008B0400008B0400008B0400000000002000000";

    private static byte[] Written() {
        var blocks = new uint[HeightFields.BlocksLength(2, 2)];
        var kept = new ushort[HeightFields.HeightsLength(2, 2)];
        HeightFields.Pack(Given, 2, 2, 1, 1, 0.25f, blocks, kept, out var packed, out var keptCount).Should().BeTrue();
        keptCount.Should().Be(9, "the peak is more than the bound above the flat between the corners, so every height is kept");
        HeightField[] fields = [packed.Placed(1, 0, 2, 1, 0, 0, 0, 1, 0, 0, 0, 1, -1)];
        var block = new byte[Shapes.BlockLength(1, 0, 0, 1, 1, 9)];
        block.AsSpan().Fill(0xFF);
        Shapes.Write(Solids, [], [], fields, blocks, kept.AsSpan(0, keptCount), new int[Shapes.WorkspaceLength(2)], block, out var length).Should().BeTrue();
        return block[..length];
    }

    private static float Distance(byte[] block, float x, float y, float z, float limit = 100f) {
        Shapes.TryRead(block, out var shapes).Should().BeTrue();
        Shapes.Distance(shapes, x, y, z, limit, out var distance).Should().BeTrue();
        return distance;
    }

    private static void Put(byte[] block, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(at), value);

    private static void Put(byte[] block, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(at), value);

    [Fact]
    public void TheSameShapesGiveTheSameBytes() {
        // The buffer was filled with ones first: nothing of that shows, in the header's spare bytes or after the odd kept height.
        Convert.ToHexString(Written()).Should().Be(Fixed);
    }

    [Fact]
    public void TheFixedBlockGivesItsKnownAnswers() {
        var block = Convert.FromHexString(Fixed);
        block.Length.Should().Be(Length);
        Distance(block, 2, 3, 3).Should().BeApproximately(2f, 1e-4f, "above the peak");
        Distance(block, 2, 0.2f, 3).Should().Be(0f, "under the peak");
        Distance(block, 0, -0.5f, 3).Should().BeApproximately(1f, 1e-6f, "beside the ground, below its rim");
        Distance(block, 2, -3, 3).Should().BeApproximately(2f, 1e-6f, "under the floor");
        Distance(block, 5, 5, 3).Should().Be(1.5f, "below the sphere");
    }

    [Fact]
    public void ABlockReadBackHoldsWhatWasWritten() {
        Shapes.TryRead(Written(), out var shapes).Should().BeTrue();
        shapes.Count.Should().Be(2);
        shapes.Solids.ToArray().Should().Equal(Solids);
        shapes.HeightFields.Length.Should().Be(1);
        shapes.HeightBlocks.ToArray().Should().Equal([0u], "every height, from the first");
        shapes.Heights.Length.Should().Be(9);
        var field = shapes.HeightFields[0];
        (field.X, field.Y, field.Z, field.Floor, field.CountU, field.CountW, field.Lowest).Should().Be((1f, 0f, 2f, -1f, 2, 2, 0f));
        // Each kept height stands at or just above the one given.
        for (var i = 0; i < 9; i++) {
            (field.Lowest + (double)field.Step * shapes.Heights[i] - Given[i]).Should().BeInRange(0, field.Step);
        }
    }

    [Fact]
    public void ABlockWithHeightsThatDoNotAddUpIsRefused() {
        var good = Written();
        Shapes.TryRead(good, out _).Should().BeTrue();
        byte[] Changed(int at, int value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }
        Shapes.TryRead(Changed(24, 2), out _).Should().BeFalse("one heightfield more than there is");
        Shapes.TryRead(Changed(24, -1), out _).Should().BeFalse();
        Shapes.TryRead(Changed(28, 0), out _).Should().BeFalse("no blocks");
        Shapes.TryRead(Changed(32, 11), out _).Should().BeFalse("two kept heights more");
        Shapes.TryRead(Changed(32, -9), out _).Should().BeFalse();
        // An odd count and the even one above it take the same bytes: the heightfield then finds no more heights than it had.
        Shapes.TryRead(Changed(32, 10), out var longer).Should().BeTrue();
        longer.Heights.Length.Should().Be(10);
    }

    [Fact]
    public void AHeightfieldReadBackWithAFlagSetMayNotBeBuiltAgain() {
        // No flag means anything yet. One to come, such as one for ground with holes, must not be taken for ground without.
        bool Builds(in ShapeSet set) => Shapes.Build(set.Solids, set.Corners, set.Triangles, set.HeightFields, set.HeightBlocks, set.Heights,
            new Box[2], new BoxNode[Shapes.NodesLength(2)], new int[2], new int[Shapes.WorkspaceLength(2)], out _);
        foreach (var flags in new[] { 1, 2, int.MinValue }) {
            var block = Written();
            Put(block, FieldsAt + Flags, flags);
            Shapes.TryRead(block, out var shapes).Should().BeTrue();
            Builds(shapes).Should().BeFalse($"flags of {flags}");
        }
        Shapes.TryRead(Written(), out var whole).Should().BeTrue();
        Builds(whole).Should().BeTrue();
    }

    [Fact]
    public void AHeightfieldWithANumberThatIsNotFiniteTouches() {
        // Above the peak, which is 2 away while it is whole.
        Distance(Written(), 2, 3, 3).Should().BeApproximately(2f, 1e-4f);
        foreach (var (at, value, why) in new[] {
                     (Step, float.NaN, "a step that is not a number"), (Step, -1f, "a step below zero"), (SizeU, -1f, "a size below zero"), (SizeU, 0f, "a size of nothing"),
                     (Lowest, float.PositiveInfinity, "a lowest height that is not finite"), (X, float.NaN, "a corner that is not a number"),
                     (Floor, float.NaN, "a floor that is not a number"),
                 }) {
            var block = Written();
            Put(block, FieldsAt + at, value);
            Distance(block, 2, 3, 3).Should().Be(0f, why);
        }
    }

    [Fact]
    public void ANumberThatPointsOutsideIsCaughtWhereItIsUsed() {
        static bool Answers(byte[] block) {
            Shapes.TryRead(block, out var shapes).Should().BeTrue("the header and the lengths are whole");
            var answered = Shapes.Distance(shapes, 2, 3, 3, 100f, out var distance);
            if (!answered) { distance.Should().Be(0f, "a refused query must not read as room"); }
            return answered;
        }

        Answers(Written()).Should().BeTrue();
        foreach (var (at, value, why) in new[] {
                     (FieldsAt + FirstBlock, 1, "blocks that begin past the blocks"), (FieldsAt + FirstBlock, -1, "blocks that begin before the start"),
                     (FieldsAt + FirstHeight, 1, "kept heights that run past the end"), (FieldsAt + FirstHeight, -1, "kept heights that begin before the start"),
                     (FieldsAt + CountU, 0, "no squares"), (FieldsAt + CountU, 40, "more squares than the block keeps heights for"),
                     (FieldsAt + CountU, int.MaxValue, "more squares than a heightfield may have"),
                     (BlocksAt, 3, "a spacing that does not divide the block's sides"), (BlocksAt, 4 << 2, "a block whose heights begin too late"),
                 }) {
            var block = Written();
            Put(block, at, value);
            Answers(block).Should().BeFalse(why);
        }

        // The marker reads the same numbers.
        var damaged = Written();
        Put(damaged, BlocksAt, 3);
        Shapes.TryRead(damaged, out var set).Should().BeTrue();
        var grid = new GridShape(0, -2, 1, 0.5f, 8, 10, 8);
        var layer = new byte[grid.LayerLength];
        WallDistance.MarkLayer(grid, set, 1f / 128f, 128, 4, layer).Should().BeFalse();
        layer.Should().OnlyContain(held => held == 0, "a refused layer claims no room");
    }

    [Fact]
    public void NoDamageToABlockMakesAQueryReadOutsideItOrRunOn() {
        // Every four bytes of the block in turn, set to each of these. Whatever comes back, nothing
        // throws and every call returns.
        int[] values = [-1, 0, 1, 3, 5, 1000, int.MaxValue, int.MinValue, BitConverter.SingleToInt32Bits(float.NaN),
            BitConverter.SingleToInt32Bits(float.PositiveInfinity), BitConverter.SingleToInt32Bits(-1e30f), BitConverter.SingleToInt32Bits(1e-30f)];
        var good = Written();
        var grid = new GridShape(0, -2, 1, 0.5f, 8, 10, 8);
        var layer = new byte[grid.LayerLength];
        var calls = 0;
        for (var at = 0; at < good.Length; at += 4) {
            foreach (var value in values) {
                var block = (byte[])good.Clone();
                Put(block, at, value);
                if (!Shapes.TryRead(block, out var shapes)) { continue; }
                foreach (var (x, y, z) in new[] { (2f, 3f, 3f), (2f, 0.2f, 3f), (0f, -0.5f, 3f), (2f, -3f, 3f), (5f, 5f, 3f), (50f, 50f, 50f), (-900f, 2f, 900f) }) {
                    Shapes.Distance(shapes, x, y, z, 100f, out _);
                    Shapes.Distance(shapes, x, y, z, float.MaxValue, out _);
                    calls++;
                }
                WallDistance.MarkLayer(grid, shapes, 1f / 128f, 128, 4, layer);
            }
        }
        calls.Should().BeGreaterThan(5000);
    }
}
