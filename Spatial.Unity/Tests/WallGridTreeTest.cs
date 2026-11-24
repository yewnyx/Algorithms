using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class WallGridTreeTest {
        // 26 by 22 by 18 cells of half a metre: no side a multiple of four, and small enough to mark both ways.
        private static readonly GridShape Grid = new GridShape(-0.5f, -0.5f, -0.5f, 0.5f, 26, 22, 18);

        // A floor, a box at an angle (its axes from a 3-4-5 triangle), a ball, a pillar, and a wall of
        // two triangles with no thickness.
        private static readonly Solid[] Solids = {
            Solid.Box(new Box(0, 0, 0, 12, 10, 0.3f)),
            Solid.Box(3, 6, 3, 0.6f, 0.8f, 0, -0.8f, 0.6f, 0, 0, 0, 1, 1.5f, 0.4f, 0.9f),
            Solid.Sphere(8.5f, 2.5f, 4f, 1.2f),
            Solid.Capsule(5, 5, 0, 5, 5, 8, 0.35f),
        };
        private static readonly float[] Corners = { 9.6f, 0, 0, 9.6f, 10, 0, 9.6f, 10, 8, 9.6f, 0, 8, 0, 9, 1, 12, 8, 2, 4, 1, 7.5f };
        private static readonly int[] Triangles = { 0, 1, 2, 0, 2, 3, 4, 5, 6 };

        [OneTimeSetUp]
        public void CompileFirstAndLookForLeaks() {
            // Without this a job runs as managed code until Burst has finished compiling it.
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
        }

        private static WallGrid Marked(bool tree) {
            var grid = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, tree);
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            return grid;
        }

        // Points all through the grid and a little beyond it, at steps that line up with nothing.
        private static System.Collections.Generic.IEnumerable<float3> Points() {
            for (var i = 0; i < 31; i++) {
                for (var j = 0; j < 27; j++) {
                    for (var k = 0; k < 23; k++) {
                        yield return new float3(-0.8f + i * 0.449f, -0.8f + j * 0.437f, -0.8f + k * 0.421f);
                    }
                }
            }
        }

        [Test]
        public void AGridOfMoreThanFourMillionCellsKeepsATree() {
            using var small = new WallGrid(new GridShape(0, 0, 0, 0.5f, 160, 160, 160));
            Assert.That(small.IsTree, Is.False, "4,096,000 cells");
            Assert.That(small.ByteCount, Is.GreaterThan(4_000_000));
            using var large = new WallGrid(new GridShape(0, 0, 0, 0.5f, 400, 100, 400));
            Assert.That(large.IsTree, Is.True, "16 million cells");
            Assert.That(large.ByteCount, Is.LessThan(1000), "no count is kept for a cell");
            Assert.That(large.Wall.Length, Is.EqualTo(0));

            // Marked from a floor and a ball: the tree is a few thousandths of a count for every cell.
            Assert.That(large.SetShapes(new[] { Solid.Box(new Box(-5, -5, -5, 205, 0.4f, 205)), Solid.Sphere(100, 20, 100, 3) }, default, default), Is.True);
            large.ScheduleMark();
            Assert.That(large.IsMarked, Is.True);
            Assert.That(large.ByteCount, Is.InRange(100_000, 4_000_000));
            Assert.That(large.TryRoom(new float3(100, 30, 100), out var room), Is.True);
            Assert.That(room, Is.EqualTo(1f), "the cap, in open air");
            Assert.That(large.TryRoom(new float3(100, 23.5f, 100), out room), Is.True);
            Assert.That(room, Is.InRange(0.3f, 0.5f), "half a metre over the ball: what the cells round the point prove, never more than there is");
            Assert.That(large.HasRoom(new float3(100, 23.5f, 100), 0.3f), Is.True);
            Assert.That(large.HasRoom(new float3(100, 23.5f, 100), 0.55f), Is.False);
            Assert.That(large.HasRoom(new float3(100, 0.2f, 100), 0.01f), Is.False, "in the floor");
        }

        // A grid's counts put down as a tree keeps them: to which fifteenth of the cap each is in,
        // and back to the least count in that fifteenth.
        private static byte[] PutDown(WallGrid dense) {
            var counts = dense.Wall.ToArray();
            for (var i = 0; i < counts.Length; i++) { counts[i] = (byte)((counts[i] * 15 / dense.Cap * dense.Cap + 14) / 15); }
            return counts;
        }

        [Test]
        public void ATreeAnswersAsCountsPutDownToAFifteenthOfTheCapDo() {
            using var dense = Marked(false);
            using var tree = Marked(true);
            var kept = PutDown(dense);
            Assert.That(tree.IsTree, Is.True);
            Assert.That(tree.Wall.Length, Is.EqualTo(0));
            Assert.That(tree.TreeNodes.Length, Is.GreaterThan(1));
            Assert.That(tree.TreeBricks.Length % WallTrees.BrickBytes, Is.EqualTo(0));
            // Bricks along the grid's far faces are kept whole, with zeros for the cells that are not in the grid.
            Assert.That(tree.TreeBricks.Length, Is.InRange(WallTrees.BrickBytes * 20, Grid.CellCount));
            Assert.That(dense.TreeNodes.Length, Is.EqualTo(0));
            float[] radii = { 0f, 0.3f, 0.51f, 0.9f, 0.93f, 0.94f, 1f };
            int inside = 0, open = 0;
            foreach (var point in Points()) {
                var inDense = WallDistance.RoomAt(new WallMap(Grid, dense.Unit, dense.Cap, kept), point.x, point.y, point.z, out var denseRoom);
                Assert.That(tree.TryRoom(point, out var treeRoom), Is.EqualTo(inDense), $"at {point}");
                Assert.That(BitConverter.SingleToInt32Bits(treeRoom), Is.EqualTo(BitConverter.SingleToInt32Bits(denseRoom)), $"at {point}: {treeRoom} for {denseRoom}");
                dense.TryRoom(point, out var whole);
                Assert.That(treeRoom, Is.LessThanOrEqualTo(whole), $"at {point}: never more room than the counts prove");
                foreach (var radius in radii) {
                    Assert.That(tree.HasRoom(point, radius), Is.EqualTo(inDense && denseRoom >= radius), $"at {point} for {radius}");
                    Assert.That(dense.HasRoom(point, radius), Is.EqualTo(dense.TryRoom(point, out var room) && room >= radius));
                }
                if (inDense) { inside++; }
                if (denseRoom >= 0.9f) { open++; }
                // The exact distance is from the shapes, and is the same whatever the grid keeps.
                Assert.That(tree.TryDistance(point, 1f, out var exact), Is.True);
                Assert.That(treeRoom, Is.LessThanOrEqualTo(exact + (float)Shapes.Slack));
            }
            Assert.That(inside, Is.GreaterThan(10000));
            Assert.That(open, Is.GreaterThan(2000));
        }

        [Test]
        public void UntilATreeIsBuiltItClaimsNoRoom() {
            using var grid = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TryRoom(new float3(6, 5, 6), out var room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
            Assert.That(grid.TryRoom(new float3(60, 5, 6), out _), Is.False, "outside the grid");
            Assert.That(grid.HasRoom(new float3(6, 5, 6), 0.1f), Is.False);
            Assert.That(grid.TryWriteTree(out var none), Is.False);
            Assert.That(none, Is.Empty);

            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.TryRoom(new float3(6, 5, 6), out room), Is.True);
            Assert.That(room, Is.GreaterThan(0.5f));

            // New shapes: what was built says nothing of them.
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TreeNodes.Length, Is.EqualTo(0));
            Assert.That(grid.TryRoom(new float3(6, 5, 6), out room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
        }

        [Test]
        public void ATreeOfOtherShapesIsNotTaken() {
            using var built = Marked(true);
            Assert.That(built.TryWriteTree(out var block), Is.True);

            // The same grid holding the same shapes but for one, which is somewhere else. Taken, the
            // first grid's tree would prove a metre of room in the middle of this grid's own box.
            var others = (Solid[])Solids.Clone();
            others[0] = Solid.Box(new Box(8, 6, 4, 11, 9, 7));
            using var other = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(other.SetShapes(others, Corners, Triangles), Is.True);
            Assert.That(other.SetTree(block), Is.False);
            Assert.That(other.IsMarked, Is.False);
            Assert.That(other.HasRoom(new float3(9.5f, 7.5f, 5.5f), 0.1f), Is.False);
            // With no shapes yet, and with the shapes taken after the tree, there is no tree either.
            using var bare = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(bare.SetTree(block), Is.False, "shapes first");
            Assert.That(bare.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(bare.SetTree(block), Is.True);
            Assert.That(bare.SetShapes(others, Corners, Triangles), Is.True);
            Assert.That(bare.IsMarked, Is.False, "new shapes: the tree says nothing of them");
            Assert.That(bare.SetTree(block), Is.False);
            // The other grid's own tree is its own.
            other.ScheduleMark().Complete();
            Assert.That(other.TryWriteTree(out var own), Is.True);
            Assert.That(own, Is.Not.EqualTo(block));
            Assert.That(built.SetTree(own), Is.False);
            Assert.That(built.IsMarked, Is.True, "what it had is kept");
        }

        [Test]
        public void ATreeIsWrittenAndTakenBackInPlaceOfAMarking() {
            using var built = Marked(true);
            Assert.That(built.TryWriteTree(out var block), Is.True);
            Assert.That(block.Length, Is.EqualTo(WallTrees.BlockLength(built.TreeNodes.Length, built.TreeBricks.Length / WallTrees.BrickBytes)));

            using var loaded = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(loaded.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(loaded.SetTree(block), Is.True);
            Assert.That(loaded.IsMarked, Is.True, "with no marking scheduled");
            Assert.That(loaded.ByteCount, Is.EqualTo(built.ByteCount));
            foreach (var point in Points()) {
                var found = built.TryRoom(point, out var room);
                Assert.That(loaded.TryRoom(point, out var same), Is.EqualTo(found));
                Assert.That(same, Is.EqualTo(room));
            }
            Assert.That(loaded.TryWriteTree(out var again), Is.True);
            Assert.That(again, Is.EqualTo(block));

            // Refused, and what was there is kept: a block cut short, one of another grid, unit or cap, and a grid that keeps counts.
            Assert.That(loaded.SetTree(block.AsSpan(0, block.Length - 1)), Is.False);
            Assert.That(loaded.IsMarked, Is.True);
            using var moved = new WallGrid(new GridShape(0, -0.5f, -0.5f, 0.5f, 26, 22, 18), 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(moved.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(moved.SetTree(block), Is.False, "a grid somewhere else");
            using var coarser = new WallGrid(Grid, 1f / 64f, 128, Allocator.Persistent, true);
            Assert.That(coarser.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(coarser.SetTree(block), Is.False, "another unit");
            using var lower = new WallGrid(Grid, 1f / 128f, 100, Allocator.Persistent, true);
            Assert.That(lower.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(lower.SetTree(block), Is.False, "another cap");
            Assert.That(lower.IsMarked, Is.False);
            using var dense = Marked(false);
            Assert.That(dense.SetTree(block), Is.False);
            Assert.That(dense.TryWriteTree(out _), Is.False);
        }

        [Test]
        public void ATreeKeepsNoCostsAndNoSizeClasses() {
            using var tree = Marked(true);
            Assert.That(tree.AddSizeClass(0.51f), Is.EqualTo(-1));
            Assert.That(tree.SizeClassCount, Is.EqualTo(0));
            Assert.That(tree.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 2)), 32), Is.False);
            Assert.That(tree.TryCost(new float3(1, 1, 1), out var cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            using var dense = Marked(false);
            Assert.That(dense.AddSizeClass(0.51f), Is.EqualTo(0));
            Assert.That(dense.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 2)), 32), Is.True);
        }

        [Test]
        public void ABuildThatIsRefusedLeavesNoTree() {
            // A block whose first triangle names a corner that is not there. Its header and lengths
            // are whole, so it is taken; the damage is met when the triangle is first measured.
            var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
            Assert.That(Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(7)], block, out var length), Is.True);
            block[40 + Solids.Length * 64 + Corners.Length * 4 + 4] = 99;

            using var grid = new WallGrid(Grid, 1f / 128f, 128, Allocator.Persistent, true);
            Assert.That(grid.SetShapes(block.AsSpan(0, length)), Is.True);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TreeNodes.Length, Is.EqualTo(0));
            Assert.That(grid.TryRoom(new float3(2, 2, 6), out var room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
            Assert.That(grid.HasRoom(new float3(2, 2, 6), 0.1f), Is.False);
        }

        [Test]
        public void AJobCanReadTheTree() {
            using var tree = Marked(true);
            using var dense = Marked(false);
            var points = new NativeArray<float3>(512, Allocator.TempJob);
            var rooms = new NativeArray<float>(512, Allocator.TempJob);
            var i = 0;
            foreach (var point in Points()) {
                if (i % 37 == 0 && i / 37 < 512) { points[i / 37] = point; }
                i++;
            }
            var job = new RoomJob { Shape = Grid, Unit = tree.Unit, Cap = tree.Cap, Nodes = tree.TreeNodes, Bricks = tree.TreeBricks, Points = points, Rooms = rooms };
            tree.AddReader(Unity.Jobs.IJobParallelForExtensions.Schedule(job, 512, 32));
            // Disposing the grid waits for the reader.
            tree.Complete();
            var kept = PutDown(dense);
            for (var k = 0; k < 512; k++) {
                WallDistance.RoomAt(new WallMap(Grid, dense.Unit, dense.Cap, kept), points[k].x, points[k].y, points[k].z, out var room);
                Assert.That(rooms[k], Is.EqualTo(room), $"at {points[k]}");
            }
            points.Dispose();
            rooms.Dispose();
        }

        [BurstCompile]
        private struct RoomJob : Unity.Jobs.IJobParallelFor {
            public GridShape Shape;
            public float Unit;
            public byte Cap;
            [ReadOnly] public NativeArray<WallNode>.ReadOnly Nodes;
            [ReadOnly] public NativeArray<byte>.ReadOnly Bricks;
            [ReadOnly] public NativeArray<float3> Points;
            public NativeArray<float> Rooms;

            public void Execute(int index) {
                var point = Points[index];
                WallTrees.RoomAt(new WallTree(Shape, Unit, Cap, Nodes.AsReadOnlySpan(), Bricks.AsReadOnlySpan()), point.x, point.y, point.z, out var room);
                Rooms[index] = room;
            }
        }
    }
}
