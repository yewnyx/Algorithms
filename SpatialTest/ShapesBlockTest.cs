using System.Buffers.Binary;
using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class ShapesBlockTest {
    // A box, a sphere, a capsule and two triangles that share an edge.
    private static readonly Solid[] Solids = [Solid.Box(new Box(0, 0, 0, 2, 1, 1)), Solid.Sphere(5, 5, 5, 0.5f), Solid.Capsule(-3, 0, 0, -3, 0, 4, 0.25f)];
    private static readonly float[] Corners = [0, 4, 0, 4, 4, 0, 0, 4, 3, 8, 0, 8];
    private static readonly int[] Triangles = [0, 1, 2, 1, 3, 2];

    // Where each part of that block starts: 3 solids, 4 corners, 2 triangles, 5 boxes, 5 order entries, 3 nodes.
    private const int SolidsAt = 32, CornersAt = 224, TrianglesAt = 272, BoxesAt = 296, OrderAt = 416, NodesAt = 436, Length = 532;

    // The block those shapes make, byte for byte. If this changes, the layout or the tree changed:
    // raise Shapes.Version, so that stored blocks are made again and not misread.
    private const string Fixed =
        "5953485001000000030000000400000002000000030000000000000000000000" +
        "000000000000803F0000003F0000003F0000803F0000000000000000000000000000803F0000000000000000000000000000803F0000803F0000003F0000003F" +
        "010000000000A0400000A0400000A0400000000000000000000000000000000000000000000000000000000000000000000000000000003F0000000000000000" +
        "02000000000040C00000000000000000000040C000000000000080400000000000000000000000000000000000000000000000000000803E0000000000000000" +
        "000000000000804000000000000080400000804000000000000000000000804000004040000000410000000000000041" +
        "000000000100000002000000010000000300000002000000" +
        "000080BA000080BA000080BA001000400020803F0020803F00F88F4000F88F4000F88F400008B0400008B0400008B040001050C0008080BE008080BE00F02FC0" +
        "0080803E00088840000080BA00F07F40000080BA000880400008804000104040000080BA000080BA000080BA000400410008804000040041" +
        "0000000002000000030000000400000001000000" +
        "001050C0008080BE008080BE000400410008B040000400410200000000000000" +
        "001050C0008080BE008080BE001000400020803F000888400000000002000000" +
        "000080BA000080BA000080BA000400410008B040000400410200000003000000";

    private static byte[] Written() {
        var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
        Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(5)], block, out var length).Should().BeTrue();
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
        Convert.ToHexString(Written()).Should().Be(Fixed);
    }

    [Fact]
    public void WhatTheBufferHeldBeforeDoesNotShowInTheBlock() {
        // The header has spare bytes, and a store that compares blocks byte for byte must find them the same.
        var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
        block.AsSpan().Fill(0xFF);
        Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(5)], block, out var length).Should().BeTrue();
        Convert.ToHexString(block.AsSpan(0, length)).Should().Be(Fixed);
    }

    [Fact]
    public void TheFixedBlockGivesItsKnownAnswers() {
        var block = Convert.FromHexString(Fixed);
        block.Length.Should().Be(Length);
        Distance(block, 1, 0.5f, 3).Should().Be(2f, "above the box");
        Distance(block, 5, 5, 3).Should().Be(1.5f, "below the sphere");
        Distance(block, -3, 1, 2).Should().Be(0.75f, "beside the capsule");
        Distance(block, 2, 6, 1).Should().Be(2f, "in front of the first triangle");
        Distance(block, 6, 2, 5).Should().Be(0.33104235f, "off the second triangle");
    }

    [Fact]
    public void ABlockReadBackHoldsWhatWasWritten() {
        var block = Written();
        Shapes.TryRead(block, out var shapes).Should().BeTrue();
        shapes.Count.Should().Be(5);
        shapes.Solids.ToArray().Should().Equal(Solids);
        shapes.Corners.ToArray().Should().Equal(Corners);
        shapes.Triangles.ToArray().Should().Equal(Triangles);

        // The same shapes built without going through bytes answer the same, to the last bit.
        var scene = new ShapeScene(Solids, Corners, Triangles);
        shapes.Boxes.ToArray().Should().Equal(scene.Boxes);
        shapes.Order.ToArray().Should().Equal(scene.Order);
        shapes.Nodes.Length.Should().Be(scene.NodeCount);
        for (var i = 0; i < 6; i++) {
            for (var j = 0; j < 6; j++) {
                for (var k = 0; k < 6; k++) {
                    float x = -4 + i * 2.3f, y = -1 + j * 1.4f, z = -1 + k * 1.9f;
                    Distance(block, x, y, z).Should().Be(scene.Distance(x, y, z));
                }
            }
        }
    }

    [Fact]
    public void ABlockOfNoShapesIsOnlyAHeader() {
        Shapes.BlockLength(0, 0, 0).Should().Be(32);
        var block = new byte[32];
        Shapes.Write([], [], [], new int[Shapes.WorkspaceLength(0)], block, out var length).Should().BeTrue();
        length.Should().Be(32);
        Shapes.TryRead(block, out var shapes).Should().BeTrue();
        shapes.Count.Should().Be(0);
        Distance(block, 1, 2, 3, limit: 9f).Should().Be(9f);
    }

    [Fact]
    public void BlockLengthRefusesWhatAnIntCannotHold() {
        Shapes.BlockLength(3, 4, 2).Should().Be(32 + 3 * 64 + 4 * 12 + 2 * 12 + 5 * 60);
        Shapes.BlockLength(-1, 0, 0).Should().Be(-1);
        Shapes.BlockLength(0, -1, 0).Should().Be(-1);
        Shapes.BlockLength(0, 0, -1).Should().Be(-1);
        Shapes.BlockLength(int.MaxValue, 0, 0).Should().Be(-1);
        Shapes.BlockLength(0, int.MaxValue, 0).Should().Be(-1);
        Shapes.BlockLength(20_000_000, 0, 20_000_000).Should().Be(-1);
    }

    [Fact]
    public void WriteRefusesWhatBuildRefuses() {
        var workspace = new int[Shapes.WorkspaceLength(5)];
        var block = new byte[Shapes.BlockLength(3, 4, 2)];
        Shapes.Write(Solids, Corners, Triangles, workspace, block.AsSpan(0, block.Length - 1), out _).Should().BeFalse("a block one byte short of the most it may need");
        Shapes.Write(Solids, Corners, Triangles, workspace.AsSpan(0, workspace.Length - 1), block, out _).Should().BeFalse("a short workspace");
        Shapes.Write([Solid.Sphere(float.NaN, 0, 0, 1)], [], [], workspace, block, out _).Should().BeFalse("a coordinate that is not a number");
        Shapes.Write([Solid.Sphere(2000, 0, 0, 1)], [], [], workspace, block, out _).Should().BeFalse("beyond the reach");
        Shapes.Write([], [0, 0, 0, 1, 0, 0, 0, 1, float.PositiveInfinity], [0, 1, 2], workspace, block, out _).Should().BeFalse();
        Shapes.Write([], [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 5], workspace, block, out _).Should().BeFalse("a corner that is not there");
        Shapes.Write([], [0, 0, 0, 1], [], workspace, block, out _).Should().BeFalse("corners not in threes");
    }

    [Fact]
    public void ABlockWithAWrongHeaderOrLengthIsRefused() {
        var good = Written();
        Shapes.TryRead(good, out _).Should().BeTrue();
        Shapes.TryRead([], out _).Should().BeFalse();
        Shapes.TryRead(good.AsSpan(0, 31), out _).Should().BeFalse("shorter than a header");
        Shapes.TryRead(good.AsSpan(0, good.Length - 1), out _).Should().BeFalse("one byte short");
        Shapes.TryRead(good.Append((byte)0).ToArray(), out _).Should().BeFalse("one byte long");

        byte[] Changed(int at, int value) { var block = (byte[])good.Clone(); Put(block, at, value); return block; }
        Shapes.TryRead(Changed(0, 0x59534850), out _).Should().BeFalse("written the other way round");
        Shapes.TryRead(Changed(0, 0), out _).Should().BeFalse();
        Shapes.TryRead(Changed(4, Shapes.Version + 1), out _).Should().BeFalse("a newer version");
        Shapes.TryRead(Changed(4, Shapes.Version - 1), out _).Should().BeFalse("an older version");
        Shapes.TryRead(Changed(8, -3), out _).Should().BeFalse("a negative count");
        Shapes.TryRead(Changed(8, 4), out _).Should().BeFalse("lengths that do not add up");
        Shapes.TryRead(Changed(16, 1), out _).Should().BeFalse();
        Shapes.TryRead(Changed(20, 2), out _).Should().BeFalse();
        Shapes.TryRead(Changed(20, 0), out _).Should().BeFalse("shapes but no nodes");
        // 2^30 more corners is 3 * 2^32 more bytes: added in 32 bits the length would come out the same.
        Shapes.TryRead(Changed(12, 4 + (1 << 30)), out _).Should().BeFalse("lengths that only add up if they overflow");
    }

    [Fact]
    public void ASolidOfAnUnknownKindOrWithANumberThatIsNotFiniteTouches() {
        // Beside the sphere, which is 1.5 away while it is whole.
        Distance(Written(), 5, 5, 3).Should().Be(1.5f);

        var block = Written();
        Put(block, SolidsAt + 64, 9);
        Distance(block, 5, 5, 3).Should().Be(0f, "an unknown kind");

        block = Written();
        Put(block, SolidsAt + 64 + 4, float.NaN);
        Distance(block, 5, 5, 3).Should().Be(0f, "a centre that is not a number");

        block = Written();
        Put(block, SolidsAt + 64 + 52, float.PositiveInfinity);
        Distance(block, 5, 5, 3).Should().Be(0f, "a radius that is not finite");

        block = Written();
        Put(block, SolidsAt + 64 + 4, float.PositiveInfinity);
        Distance(block, 5, 5, 3).Should().Be(0f, "a centre that is not finite");

        // In front of the first triangle, which is 2 away while it is whole.
        block = Written();
        Put(block, CornersAt + 4, float.NaN);
        Distance(block, 2, 6, 1).Should().Be(0f, "a corner that is not a number");
    }

    [Fact]
    public void ANumberThatPointsOutsideIsCaughtWhereItIsUsed() {
        static bool Answers(byte[] block, float x, float y, float z) {
            Shapes.TryRead(block, out var shapes).Should().BeTrue("the header and the lengths are whole");
            var answered = Shapes.Distance(shapes, x, y, z, 100f, out var distance);
            if (!answered) { distance.Should().Be(0f, "a refused query must not read as room"); }
            return answered;
        }

        var block = Written();
        Put(block, TrianglesAt + 4, 4);
        Answers(block, 2, 6, 1).Should().BeFalse("a corner number past the corners");
        block = Written();
        Put(block, TrianglesAt + 4, -1);
        Answers(block, 2, 6, 1).Should().BeFalse("a negative corner number");
        block = Written();
        Put(block, OrderAt, 5);
        // The first leaf holds the box and the capsule, so the query is one that walks it.
        Answers(block, 1, 0.5f, 3).Should().BeFalse("an order entry past the shapes");
        block = Written();
        Put(block, NodesAt + 24, 0);
        Answers(block, 2, 6, 1).Should().BeFalse("a node whose child is itself");
        block = Written();
        Put(block, NodesAt + 24, 7);
        Answers(block, 2, 6, 1).Should().BeFalse("a node whose child is past the nodes");
        block = Written();
        Put(block, NodesAt + 32 + 28, 6);
        Answers(block, 1, 0.5f, 3).Should().BeFalse("a leaf that holds more than there is");
        block = Written();
        Put(block, NodesAt + 32 + 24, -1);
        Answers(block, 1, 0.5f, 3).Should().BeFalse("a leaf that starts before the start");

        // The marker reads the same numbers.
        block = Written();
        Put(block, TrianglesAt + 4, 4);
        Shapes.TryRead(block, out var damaged).Should().BeTrue();
        var grid = new GridShape(-4, -1, -1, 0.5f, 26, 14, 20);
        WallDistance.MarkLayer(grid, damaged, 1f / 128f, 128, 4, new byte[grid.LayerLength]).Should().BeFalse();
    }

    [Fact]
    public void NoDamageToABlockMakesAQueryReadOutsideItOrRunOn() {
        // Every four bytes of the block in turn, set to each of these. Whatever comes back, nothing
        // throws and every call returns.
        int[] values = [-1, 0, 1, 3, 5, 1000, int.MaxValue, int.MinValue, BitConverter.SingleToInt32Bits(float.NaN),
            BitConverter.SingleToInt32Bits(float.PositiveInfinity), BitConverter.SingleToInt32Bits(-1e30f)];
        var good = Written();
        var grid = new GridShape(-4, -1, -1, 0.5f, 26, 14, 20);
        var layer = new byte[grid.LayerLength];
        var calls = 0;
        for (var at = 0; at < good.Length; at += 4) {
            foreach (var value in values) {
                var block = (byte[])good.Clone();
                Put(block, at, value);
                if (!Shapes.TryRead(block, out var shapes)) { continue; }
                foreach (var (x, y, z) in new[] { (1f, 0.5f, 3f), (5f, 5f, 3f), (-3f, 1f, 2f), (2f, 6f, 1f), (6f, 2f, 5f), (50f, 50f, 50f) }) {
                    Shapes.Distance(shapes, x, y, z, 100f, out _);
                    calls++;
                }
                WallDistance.MarkLayer(grid, shapes, 1f / 128f, 128, 4, layer);
            }
        }
        calls.Should().BeGreaterThan(5000);
    }
}
