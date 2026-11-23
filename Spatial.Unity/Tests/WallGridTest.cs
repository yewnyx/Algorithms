using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class WallGridTest {
        // 24 by 20 by 16 cells of half a metre from the origin.
        private static readonly GridShape Grid = new GridShape(0, 0, 0, 0.5f, 24, 20, 16);

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

        [Test]
        public void BurstIsOn() => Assert.That(BurstCompiler.IsEnabled, Is.True);

        // The same grid filled by the core on this thread, with no job and no Burst.
        private static byte[] MarkedByTheCore(float unit, byte cap) {
            var count = Solids.Length + Triangles.Length / 3;
            var boxes = new Box[count];
            var nodes = new BoxNode[Shapes.NodesLength(count)];
            var order = new int[count];
            Assert.That(Shapes.Build(Solids, Corners, Triangles, boxes, nodes, order, new int[Shapes.WorkspaceLength(count)], out var nodeCount), Is.True);
            var map = new byte[Grid.CellCount];
            for (var z = 0; z < Grid.CountZ; z++) {
                var set = new ShapeSet(Solids, Corners, Triangles, boxes, nodes.AsSpan(0, nodeCount), order);
                Assert.That(WallDistance.MarkLayer(Grid, set, unit, cap, z, map.AsSpan(z * Grid.LayerLength, Grid.LayerLength)), Is.True);
            }
            return map;
        }

        private static WallGrid Marked() {
            var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            grid.ScheduleMark();
            return grid;
        }

        [Test]
        public void TheJobsFillWhatTheCoreFillsByteForByte() {
            using var grid = Marked();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.ShapeCount, Is.EqualTo(7));
            Assert.That(grid.Wall.ToArray(), Is.EqualTo(MarkedByTheCore(1f / 128f, 128)));
        }

        [Test]
        public void AnotherUnitAndCapAreFilledTheSame() {
            using var grid = new WallGrid(Grid, 0.1f, 7);
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            grid.ScheduleMark().Complete();
            Assert.That(grid.Wall.ToArray(), Is.EqualTo(MarkedByTheCore(0.1f, 7)));
        }

        [Test]
        public void ABlockGivesTheSameGridAsTheShapesItWasMadeFrom() {
            var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
            Assert.That(Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(7)], block, out var length), Is.True);

            using var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(block.AsSpan(0, length)), Is.True);
            // The block is not needed once it has been taken.
            Array.Clear(block, 0, block.Length);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.Wall.ToArray(), Is.EqualTo(MarkedByTheCore(1f / 128f, 128)));
            Assert.That(grid.Solids.ToArray(), Is.EqualTo(Solids));
            Assert.That(grid.Corners.ToArray(), Is.EqualTo(Corners));
            Assert.That(grid.Triangles.ToArray(), Is.EqualTo(Triangles));

            // The marker does not use the tree, so the exact distance is what shows the tree came over whole.
            using var direct = new WallGrid(Grid);
            Assert.That(direct.SetShapes(Solids, Corners, Triangles), Is.True);
            for (var i = 0; i < 9; i++) {
                for (var j = 0; j < 7; j++) {
                    for (var k = 0; k < 5; k++) {
                        var point = new float3(12f * (i + 0.31f) / 9, 10f * (j + 0.57f) / 7, 8f * (k + 0.83f) / 5);
                        Assert.That(grid.TryDistance(point, 20f, out var fromBlock), Is.True);
                        Assert.That(direct.TryDistance(point, 20f, out var fromShapes), Is.True);
                        Assert.That(fromBlock, Is.EqualTo(fromShapes), $"at {point}");
                    }
                }
            }
        }

        // A job of a caller's that reads the wall distance, slowly enough to still be running when the grid is next used.
        private struct ReadWallJob : IJob {
            [ReadOnly] public NativeArray<byte>.ReadOnly Wall;
            public NativeArray<long> Sum;

            public void Execute() {
                long sum = 0;
                for (var round = 0; round < 40; round++) {
                    for (var i = 0; i < Wall.Length; i++) { sum += Wall[i]; }
                }
                Sum[0] = sum / 40;
            }
        }

        // The same, but it first sleeps for a set time, so that a test knows it is still running. Not for Burst.
        private struct SlowReadWallJob : IJob {
            [ReadOnly] public NativeArray<byte>.ReadOnly Wall;
            public NativeArray<long> Sum;
            public int Milliseconds;

            public void Execute() {
                System.Threading.Thread.Sleep(Milliseconds);
                long sum = 0;
                for (var i = 0; i < Wall.Length; i++) { sum += Wall[i]; }
                Sum[0] = sum;
            }
        }

        // Whichever of the two runs longer, what writes the grid's arrays waits for both.
        [TestCase(400, 40)]
        [TestCase(40, 400)]
        public void TwoReadersAtOnceAreBothWaitedFor(int first, int second) {
            using var grid = Marked();
            long expected = 0;
            foreach (var count in grid.Wall) { expected += count; }
            var firstSum = new NativeArray<long>(1, Allocator.TempJob);
            var secondSum = new NativeArray<long>(1, Allocator.TempJob);
            var firstReader = new SlowReadWallJob { Wall = grid.Wall, Sum = firstSum, Milliseconds = first }.Schedule();
            grid.AddReader(firstReader);
            var secondReader = new SlowReadWallJob { Wall = grid.Wall, Sum = secondSum, Milliseconds = second }.Schedule();
            Assert.That(firstReader.IsCompleted, Is.False, "the first must still be running for this to test anything");
            grid.AddReader(secondReader);

            Solid[] one = { Solid.Sphere(6, 5, 4, 1) };
            Assert.That(grid.SetShapes(one, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(firstReader.IsCompleted, Is.True);
            Assert.That(secondReader.IsCompleted, Is.True);
            Assert.That(firstSum[0], Is.EqualTo(expected));
            Assert.That(secondSum[0], Is.EqualTo(expected));
            firstSum.Dispose(); secondSum.Dispose();
        }

        [Test]
        public void WhatOnlyReadsTheGridDoesNotWaitForACallersReader() {
            using var grid = Marked();
            var sum = new NativeArray<long>(1, Allocator.TempJob);
            var reader = new SlowReadWallJob { Wall = grid.Wall, Sum = sum, Milliseconds = 500 }.Schedule();
            grid.AddReader(reader);

            var point = new float3(2, 2, 1.3f);
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.TryRoom(point, out _), Is.True);
            Assert.That(grid.TryDistance(point, 5f, out _), Is.True);
            Assert.That(grid.TryCost(point, out _), Is.True);
            Assert.That(grid.TryCentre(point, out _), Is.True);
            Assert.That(grid.Wall.Length, Is.EqualTo(Grid.CellCount));
            Assert.That(grid.Cost.Length, Is.EqualTo(0), "no cost has been written");
            Assert.That(grid.Solids.Length, Is.EqualTo(Solids.Length));
            Assert.That(grid.ShapeCount, Is.EqualTo(7));
            Assert.That(grid.ByteCount, Is.GreaterThan(0));
            Assert.That(reader.IsCompleted, Is.False, "none of those waited half a second for the reader");

            // What writes the costs does wait.
            Assert.That(grid.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 8)), 32), Is.True);
            Assert.That(reader.IsCompleted, Is.True);
            sum.Dispose();
        }

        [Test]
        public void ClearingTheCostsWaitsForACallersReader() {
            using var grid = Marked();
            Assert.That(grid.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 8)), 32), Is.True);
            var sum = new NativeArray<long>(1, Allocator.TempJob);
            // A job of the caller's that reads the costs, and will not be done for a while.
            var reader = new SlowReadWallJob { Wall = grid.Cost, Sum = sum, Milliseconds = 300 }.Schedule();
            grid.AddReader(reader);
            Assert.That(reader.IsCompleted, Is.False);
            // Clearing lets the bytes of cost go, so it waits for the reader first.
            Assert.DoesNotThrow(() => grid.ClearCost());
            Assert.That(reader.IsCompleted, Is.True);
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
            sum.Dispose();
        }

        [Test]
        public void AJobOfTheCallersThatReadsTheGridIsWaitedFor() {
            var grid = Marked();
            long expected = 0;
            foreach (var count in grid.Wall) { expected += count; }
            var sum = new NativeArray<long>(1, Allocator.TempJob);

            // New shapes clear the wall distance: the reader must have finished with it first.
            var reader = new ReadWallJob { Wall = grid.Wall, Sum = sum }.Schedule();
            grid.AddReader(reader);
            Solid[] one = { Solid.Sphere(6, 5, 4, 1) };
            Assert.That(grid.SetShapes(one, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(reader.IsCompleted, Is.True);
            Assert.That(sum[0], Is.EqualTo(expected));

            // A marking runs after a reader, and Dispose waits for both.
            grid.ScheduleMark().Complete();
            expected = 0;
            foreach (var count in grid.Wall) { expected += count; }
            reader = new ReadWallJob { Wall = grid.Wall, Sum = sum }.Schedule();
            grid.AddReader(reader);
            grid.ScheduleMark();
            Assert.DoesNotThrow(() => grid.Dispose());
            Assert.That(reader.IsCompleted, Is.True);
            Assert.That(sum[0], Is.EqualTo(expected));
            sum.Dispose();
        }

        [Test]
        public void BeforeItIsMarkedAGridProvesNothing() {
            using var grid = new WallGrid(Grid);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TryRoom(new float3(6, 5, 6), out var room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
        }

        [Test]
        public void AGridWithNoShapesIsOpenToTheCap() {
            using var grid = new WallGrid(Grid);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.ShapeCount, Is.EqualTo(0));
            Assert.That(grid.TryRoom(new float3(6.25f, 5.25f, 4.25f), out var room), Is.True);
            Assert.That(room, Is.EqualTo(1f - Shapes.Slack));
            Assert.That(grid.TryDistance(new float3(6, 5, 4), 3f, out var distance), Is.True);
            Assert.That(distance, Is.EqualTo(3f));
        }

        [Test]
        public void TheRoomAtAPointIsNeverMoreThanTheExactDistance() {
            using var grid = Marked();
            var proved = 0;
            for (var i = 0; i < 13; i++) {
                for (var j = 0; j < 11; j++) {
                    for (var k = 0; k < 9; k++) {
                        var point = new float3(12f * (i + 0.31f) / 13, 10f * (j + 0.57f) / 11, 8f * (k + 0.83f) / 9);
                        Assert.That(grid.TryRoom(point, out var room), Is.True);
                        Assert.That(grid.TryDistance(point, 2f, out var exact), Is.True);
                        Assert.That(room, Is.LessThanOrEqualTo(exact + Shapes.Slack), $"at {point}");
                        if (room > 0f) { proved++; }
                    }
                }
            }
            Assert.That(proved, Is.GreaterThan(400));
        }

        [Test]
        public void PointQueriesAnswerInTheGridsFrame() {
            using var grid = Marked();
            Assert.That(grid.TryDistance(new float3(2, 2, 1.3f), 5f, out var distance), Is.True);
            Assert.That(distance, Is.EqualTo(1f).Within(1e-5f), "above the floor");
            Assert.That(grid.TryDistance(new float3(5.85f, 5, 0.9f), 5f, out distance), Is.True);
            Assert.That(distance, Is.EqualTo(0.5f).Within(1e-5f), "beside the pillar");
            Assert.That(grid.TryDistance(new float3(5.1f, 5, 0.9f), 5f, out distance), Is.True);
            Assert.That(distance, Is.EqualTo(0f), "inside the pillar");
            Assert.That(grid.TryDistance(new float3(30, 5, 4), 5f, out distance), Is.True, "exact distance does not need the grid");
            Assert.That(distance, Is.EqualTo(5f));

            Assert.That(grid.TryCentre(new float3(1.1f, 2.6f, 3.9f), out var centre), Is.True);
            Assert.That(centre, Is.EqualTo(new float3(1.25f, 2.75f, 3.75f)));

            Assert.That(grid.TryRoom(new float3(-0.1f, 5, 4), out _), Is.False, "outside the grid");
            Assert.That(grid.TryRoom(new float3(float.NaN, 5, 4), out _), Is.False);
            Assert.That(grid.TryCentre(new float3(12, 5, 4), out _), Is.False, "on an upper face");
            Assert.That(grid.TryDistance(new float3(float.NaN, 5, 4), 5f, out _), Is.False);
            Assert.That(grid.TryDistance(new float3(1, 5, 4), -1f, out _), Is.False);
        }

        [Test]
        public void CostsAreWrittenAndReadAtPoints() {
            using var grid = new WallGrid(Grid);
            Assert.That(grid.TryCost(new float3(1, 1, 1), out var cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 8)), 32), Is.True);
            Assert.That(grid.FillCost(Solid.Box(new Box(1, 1, 0, 3, 3, 8)), CellCost.Closed), Is.True);
            Assert.That(grid.TryCost(new float3(0.3f, 0.3f, 1), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(32));
            Assert.That(grid.TryCost(new float3(1.3f, 1.3f, 1), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(255), "the later fill wins where they overlap");
            Assert.That(grid.TryCost(new float3(4, 4, 1), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.TryCost(new float3(-1, 1, 1), out _), Is.False);
            Assert.That(grid.FillCost(Solid.Sphere(float.NaN, 1, 1, 1), 32), Is.False);
            Assert.That(grid.Cost.Length, Is.EqualTo(Grid.CellCount));

            grid.ClearCost();
            Assert.That(grid.TryCost(new float3(1.3f, 1.3f, 1), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
        }

        [Test]
        public void AByteOfCostACellIsHeldOnlyOnceACostIsWritten() {
            using var grid = new WallGrid(Grid);
            var bare = grid.ByteCount;
            Assert.That(bare, Is.EqualTo((long)Grid.CellCount + Grid.CountZ), "the wall distance, and a flag for each layer");
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
            Assert.That(grid.TryCost(new float3(1, 1, 1), out var cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.TryCost(new float3(-1, 1, 1), out _), Is.False);
            // A solid that is refused, and a cost of 0 where all is 0 already, keep nothing.
            Assert.That(grid.FillCost(Solid.Sphere(float.NaN, 1, 1, 1), 32), Is.False);
            Assert.That(grid.FillCost(Solid.Sphere(2, 2, 2, 1), 0), Is.True);
            Assert.That(grid.ByteCount, Is.EqualTo(bare));
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
            // So does a cost on a solid that holds no cell's centre: a sheet between two rows of them.
            Assert.That(grid.FillCost(Solid.Box(new Box(0, 0.95f, 0, 4, 1.05f, 2)), 32), Is.True);
            Assert.That(grid.ByteCount, Is.EqualTo(bare));
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
            // The first cost that is not normal.
            Assert.That(grid.FillCost(Solid.Sphere(2, 2, 2, 1), 32), Is.True);
            Assert.That(grid.ByteCount, Is.EqualTo(bare + Grid.CellCount));
            Assert.That(grid.Cost.Length, Is.EqualTo(Grid.CellCount));
            Assert.That(grid.TryCost(new float3(2, 2, 2), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(32));
            Assert.That(grid.TryCost(new float3(6, 6, 6), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            // Once held, a 0 is written like any other, and a refused solid changes nothing.
            Assert.That(grid.FillCost(Solid.Sphere(2, 2, 2, 0.5f), 0), Is.True);
            Assert.That(grid.TryCost(new float3(2.2f, 2.2f, 2.2f), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.FillCost(Solid.Sphere(float.NaN, 1, 1, 1), 32), Is.False);
            Assert.That(grid.TryCost(new float3(2.6f, 2, 2), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(32));
            Assert.That(grid.ByteCount, Is.EqualTo(bare + Grid.CellCount));
            // Cleared, the bytes are let go, and a cost can be written again.
            grid.ClearCost();
            Assert.That(grid.ByteCount, Is.EqualTo(bare));
            Assert.That(grid.TryCost(new float3(2.6f, 2, 2), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(grid.FillCost(Solid.Sphere(6, 6, 6, 1), 200), Is.True);
            Assert.That(grid.TryCost(new float3(6, 6, 6), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(200));
            Assert.That(grid.TryCost(new float3(2, 2, 2), out cost), Is.True);
            Assert.That(cost, Is.EqualTo(0), "not what was there before the clearing");
        }

        [Test]
        public void ShapesThatAreRefusedLeaveTheOldOnesInPlace() {
            using var grid = Marked();
            var before = grid.Wall.ToArray();
            Solid[] bad = { Solid.Sphere(float.NaN, 0, 0, 1) };
            Assert.That(grid.SetShapes(bad, Array.Empty<float>(), Array.Empty<int>()), Is.False);
            Assert.That(grid.SetShapes(Solids, Corners, new[] { 0, 1, 99 }), Is.False, "a corner that is not there");
            Assert.That(grid.SetShapes(new byte[40]), Is.False, "not a block");
            Assert.That(grid.ShapeCount, Is.EqualTo(7));
            grid.ScheduleMark();
            Assert.That(grid.Wall.ToArray(), Is.EqualTo(before));
        }

        [Test]
        public void NewShapesLeaveNothingProvenUntilTheyAreMarked() {
            // Marked with no shapes: room everywhere.
            using var grid = new WallGrid(Grid);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            var inside = new float3(6.25f, 5.25f, 4.25f);
            Assert.That(grid.TryRoom(inside, out var room), Is.True);
            Assert.That(room, Is.EqualTo(1f - Shapes.Slack));

            // A box now fills that place. Until it is marked, the old room must not be claimed.
            Solid[] box = { Solid.Box(new Box(4, 3, 2, 8, 7, 6)) };
            Assert.That(grid.SetShapes(box, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TryRoom(inside, out room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
            Assert.That(grid.TryRoom(new float3(1.25f, 1.25f, 1.25f), out room), Is.True);
            Assert.That(room, Is.EqualTo(0f), "nor anywhere else");
            Assert.That(grid.TryDistance(inside, 5f, out var exact), Is.True, "the exact distance reads the shapes, so it is right at once");
            Assert.That(exact, Is.EqualTo(0f));

            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.TryRoom(inside, out room), Is.True);
            Assert.That(room, Is.EqualTo(0f), "inside the box");
            Assert.That(grid.TryRoom(new float3(1.25f, 1.25f, 1.25f), out room), Is.True);
            Assert.That(room, Is.EqualTo(1f - Shapes.Slack), "far from it");

            // A block does the same.
            var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
            Assert.That(Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(7)], block, out var length), Is.True);
            Assert.That(grid.SetShapes(block.AsSpan(0, length)), Is.True);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.TryRoom(new float3(1.25f, 1.25f, 1.25f), out room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
        }

        [Test]
        public void ALayerThatIsRefusedReadsAsNoRoom() {
            // A block whose first triangle names a corner that is not there. Its header and lengths
            // are whole, so it is taken; the damage is met when the triangle is first measured.
            var block = new byte[Shapes.BlockLength(Solids.Length, Corners.Length / 3, Triangles.Length / 3)];
            Assert.That(Shapes.Write(Solids, Corners, Triangles, new int[Shapes.WorkspaceLength(7)], block, out var length), Is.True);
            var triangles = 40 + Solids.Length * 64 + Corners.Length * 4;
            block[triangles + 4] = 99;

            using var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(block.AsSpan(0, length)), Is.True);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.False);
            // On the damaged triangle (the wall at x = 9.6), and all through the layers it reaches.
            Assert.That(grid.TryRoom(new float3(9.6f, 5, 4), out var room), Is.True);
            Assert.That(room, Is.EqualTo(0f));
            var wall = grid.Wall;
            for (var z = 0; z < Grid.CountZ; z++) {
                for (var i = 0; i < Grid.LayerLength; i++) {
                    Assert.That(wall[z * Grid.LayerLength + i], Is.EqualTo(0), $"layer {z}: the damaged wall runs the height of the grid");
                }
            }
        }

        [Test]
        public void TheGridSaysHowManyBytesItHolds() {
            using var grid = new WallGrid(Grid);
            var empty = grid.ByteCount;
            Assert.That(empty, Is.EqualTo((long)Grid.CellCount + Grid.CountZ), "the wall distance, and a flag for each layer");
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(grid.ByteCount, Is.GreaterThan(empty + 4 * 64 + 21 * 4 + 9 * 4));
        }

        [Test]
        public void AShapeUnitOrCapThatCannotBeUsedIsRefused() {
            Assert.That(() => new WallGrid(default), Throws.ArgumentException);
            Assert.That(() => new WallGrid(new GridShape(0, 0, 0, 0f, 4, 4, 4)), Throws.ArgumentException);
            Assert.That(() => new WallGrid(Grid, 0f), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new WallGrid(Grid, float.NaN), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new WallGrid(Grid, 1f / 128f, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void AfterDisposeTheGridRefusesToBeUsed() {
            var grid = Marked();
            grid.Dispose();
            grid.Dispose();
            Assert.That(grid.IsDisposed, Is.True);
            Assert.That(grid.Shape.CellCount, Is.EqualTo(Grid.CellCount));
            Assert.DoesNotThrow(() => grid.Complete());
            Assert.That(() => grid.TryRoom(float3.zero, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.TryDistance(float3.zero, 1f, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.ScheduleMark(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.SetShapes(Solids, Corners, Triangles), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.IsMarked, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.ByteCount, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.Wall, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.FillCost(Solid.Sphere(1, 1, 1, 1), 32), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void DisposeWaitsForAMarkingThatIsStillRunning() {
            var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(Solids, Corners, Triangles), Is.True);
            grid.ScheduleMark();
            grid.ScheduleMark();
            Assert.DoesNotThrow(() => grid.Dispose());
        }
    }
}
