using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class WallGridRegionsTest {
        // 16 cells of half a metre each way from the origin.
        private static readonly GridShape Grid = new GridShape(0, 0, 0, 0.5f, 16, 16, 16);

        // A floor, and a wall 0.4 thick between two rooms with a doorway 2 m wide and 3 m high.
        private static readonly Solid[] TwoRooms = {
            Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f)),
            Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)),
            Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)),
            Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
        };

        private static readonly Solid[] Everywhere = { Solid.Box(new Box(-1, -1, -1, 9, 9, 9)) };
        private static readonly Solid[] LeftRoomOnly = { Solid.Sphere(2, 2, 2, 0.6f) };

        // The doorway's cells, to close it with.
        private static readonly Solid Doorway = Solid.Box(new Box(3.6f, 2.5f, 0, 4.4f, 5.5f, 4));

        [OneTimeSetUp]
        public void CompileFirstAndLookForLeaks() {
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
        }

        private static WallGrid Marked() {
            var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleMark();
            return grid;
        }

        // The same regions flooded by the core on this thread, with no job and no Burst.
        private static byte[] FloodedByTheCore(WallGrid grid, float radius, Solid[] seeds, out int reached, out int empty) {
            var wall = grid.Wall.ToArray();
            var cost = grid.Cost.ToArray();
            var map = new WallMap(Grid, grid.Unit, grid.Cap, wall);
            Assert.That(SizeClass.TryMake(radius, map, out var size), Is.True);
            var region = new byte[Grid.CellCount];
            Assert.That(Regions.FromSeeds(map, size, cost, seeds, region, new int[Regions.QueueLength(Grid)], out reached, out empty), Is.True);
            return region;
        }

        [Test]
        public void TheFloodJobGivesWhatTheCoreGivesByteForByte() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            Assert.That(body, Is.EqualTo(0));
            // Scheduled straight after the marking, without waiting for it: the flood runs after it.
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);
            var expected = FloodedByTheCore(grid, 0.51f, Everywhere, out var reached, out var empty);
            Assert.That(grid.RegionsOf(body).ToArray(), Is.EqualTo(expected));
            Assert.That(grid.Reached(body), Is.EqualTo(reached));
            Assert.That(grid.EmptySeeds(body), Is.EqualTo(empty));
            Assert.That(reached, Is.GreaterThan(1500));
            Assert.That(grid.SizeOf(body).Radius, Is.EqualTo(0.51f));
        }

        [Test]
        public void PointsAreOpenWhereABodyFitsAndASeedReaches() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, LeftRoomOnly);
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.True);
            Assert.That(grid.IsOpen(body, new float3(6, 6, 2)), Is.True, "through the doorway");
            Assert.That(grid.IsOpen(body, new float3(2, 2, 0.3f)), Is.False, "too near the floor");
            Assert.That(grid.IsOpen(body, new float3(4, 1, 5)), Is.False, "inside the wall");
            Assert.That(grid.IsOpen(body, new float3(-1, 2, 2)), Is.False, "outside the grid");
            Assert.That(grid.EmptySeeds(body), Is.EqualTo(0));

            Assert.That(grid.TryNearestOpen(body, new float3(2.3f, 2.3f, 0.2f), 3f, out var open), Is.True);
            Assert.That(open, Is.EqualTo(new float3(2.25f, 2.25f, 0.75f)), "up from the floor to the first open layer");
            Assert.That(grid.TryNearestOpen(body, new float3(2.3f, 2.3f, 0.2f), 0.3f, out _), Is.False, "out of reach");
            Assert.That(grid.TryNearestOpen(body, new float3(3.9f, 1.3f, 5.3f), 3f, new float3(2, 2, 2), out open), Is.True);
            Assert.That(open, Is.EqualTo(new float3(3.25f, 1.25f, 5.25f)));
            Assert.That(grid.TryNearestOpen(body, new float3(3.9f, 1.3f, 5.3f), 3f, new float3(4, 1, 5), out _), Is.False, "the other point is inside the wall");
        }

        [Test]
        public void ASeedWithNoOpenCellIsCounted() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            Solid[] seeds = { Solid.Sphere(2, 2, 2, 0.6f), Solid.Box(new Box(3.85f, 0, 4, 4.15f, 2, 6)) };
            grid.ScheduleRegions(body, seeds);
            Assert.That(grid.HasRegions(body), Is.True);
            Assert.That(grid.EmptySeeds(body), Is.EqualTo(1), "the one inside the wall");
            Assert.That(grid.Reached(body), Is.GreaterThan(1500));
        }

        [Test]
        public void AFloodThatIsRefusedLeavesNoRegions() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);
            // A seed that may not be stored, after one that reaches the whole room.
            Solid[] seeds = { Everywhere[0], Solid.Sphere(float.NaN, 2, 2, 1) };
            grid.ScheduleRegions(body, seeds);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.Reached(body), Is.EqualTo(0));
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.False);
            Assert.That(grid.RegionsOf(body).ToArray(), Is.All.EqualTo(0));
        }

        [Test]
        public void ASizeClassSaysNothingUntilItIsFlooded() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.False);
            Assert.That(grid.TryNearestOpen(body, new float3(2, 2, 2), 3f, out _), Is.False);
            Assert.That(grid.Reached(body), Is.EqualTo(0));
            // Nor does a grid that was never marked: a flood of it is refused.
            using var unmarked = new WallGrid(Grid);
            var other = unmarked.AddSizeClass(0.51f);
            unmarked.ScheduleRegions(other, Everywhere);
            Assert.That(unmarked.HasRegions(other), Is.False);
            Assert.That(unmarked.Reached(other), Is.EqualTo(0));
            Assert.That(unmarked.IsOpen(other, new float3(2, 2, 2)), Is.False);
        }

        [Test]
        public void AFloodBeforeTheMarkingOfNewShapesIsRefused() {
            // New shapes, then the flood, then the marking: the wrong order. The flood read a wall
            // distance that says nothing of the new shapes, so its regions are not theirs.
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);

            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.False, "flooded before the marking");
            Assert.That(grid.Reached(body), Is.EqualTo(0));
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.HasRegions(body), Is.False, "the marking does not make an earlier flood good");
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.False);

            // Flooded again after the marking, they are good.
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.True);
        }

        [Test]
        public void AFloodOfAWallDistanceThatWasNotWhollyMarkedIsRefused() {
            // A block whose one triangle names a corner that is not there: the marking of the layers
            // it reaches is refused. The layers it does not reach are marked, and must not be flooded
            // as if they were the whole of it.
            float[] corners = { 4.6f, 0, 2, 4.6f, 8, 2, 4.6f, 8, 5 };
            int[] triangles = { 0, 1, 2 };
            var block = new byte[Shapes.BlockLength(TwoRooms.Length, 3, 1)];
            Assert.That(Shapes.Write(TwoRooms, corners, triangles, new int[Shapes.WorkspaceLength(5)], block, out var length), Is.True);
            block[40 + TwoRooms.Length * 64 + corners.Length * 4 + 4] = 99;

            using var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(block.AsSpan(0, length)), Is.True);
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleMark();
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.IsMarked, Is.False);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.Reached(body), Is.EqualTo(0));
            Assert.That(grid.IsOpen(body, new float3(2, 2, 7)), Is.False, "a layer that was marked, above the damage");
            Assert.That(grid.RegionsOf(body).ToArray(), Is.All.EqualTo(0));
        }

        // A job that only waits, for a flood to be made to run after.
        private struct WaitJob : IJob {
            public int Milliseconds;
            public void Execute() => System.Threading.Thread.Sleep(Milliseconds);
        }

        [Test]
        public void CompleteAndDisposeWaitForAFloodThatHasNotRunYet() {
            var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            // The flood cannot start for 300 ms, so it is certainly still to run.
            var flood = grid.ScheduleRegions(body, Everywhere, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(flood.IsCompleted, Is.False);
            grid.Complete();
            Assert.That(flood.IsCompleted, Is.True);

            flood = grid.ScheduleRegions(body, Everywhere, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(flood.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => grid.Dispose());
            Assert.That(flood.IsCompleted, Is.True);
        }

        [Test]
        public void ClearingTheCostsWaitsForAFloodThatReadsThem() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            Assert.That(grid.FillCost(Doorway, 32), Is.True);
            // The flood reads the bytes of cost, and cannot start for 300 ms.
            var flood = grid.ScheduleRegions(body, Everywhere, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(flood.IsCompleted, Is.False);
            // Clearing frees those bytes, so it waits for the flood first.
            Assert.DoesNotThrow(() => grid.ClearCost());
            Assert.That(flood.IsCompleted, Is.True);
            Assert.That(grid.Cost.Length, Is.EqualTo(0));
            // The first cost written puts an array where the empty one was, and waits the same.
            flood = grid.ScheduleRegions(body, Everywhere, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(flood.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => grid.FillCost(Doorway, 32));
            Assert.That(flood.IsCompleted, Is.True);
        }

        [Test]
        public void NewShapesLeaveNoRegionsUntilTheyAreMarkedAndFloodedAgain() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            var inside = new float3(6.25f, 5.25f, 4.25f);
            Assert.That(grid.IsOpen(body, inside), Is.True);

            // A box now fills that place.
            Solid[] box = { Solid.Box(new Box(4.5f, 3, 2, 8, 7, 6)) };
            Assert.That(grid.SetShapes(box, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.IsOpen(body, inside), Is.False);
            Assert.That(grid.IsOpen(body, new float3(1.25f, 1.25f, 1.25f)), Is.False, "nor anywhere else");
            Assert.That(grid.RegionsOf(body).ToArray(), Is.All.EqualTo(0));

            // Marked but not flooded: still nothing.
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.IsOpen(body, new float3(1.25f, 1.25f, 1.25f)), Is.False);

            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);
            Assert.That(grid.IsOpen(body, new float3(1.25f, 1.25f, 1.25f)), Is.True);
            Assert.That(grid.IsOpen(body, inside), Is.False, "inside the new box");
        }

        [Test]
        public void AChangeOfCostLeavesNoRegionsUntilTheyAreFloodedAgain() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            float3 left = new float3(2, 2, 2), right = new float3(6, 6, 2);
            Assert.That(grid.TryNearestOpen(body, right, 0.5f, left, out _), Is.True, "the rooms are joined through the doorway");

            // The doorway is shut. Until they are flooded again the regions are not to be believed.
            Assert.That(grid.FillCost(Doorway, CellCost.Closed), Is.True);
            Assert.That(grid.HasRegions(body), Is.False);
            Assert.That(grid.IsOpen(body, left), Is.False);

            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.HasRegions(body), Is.True);
            Assert.That(grid.IsOpen(body, left), Is.True);
            Assert.That(grid.IsOpen(body, right), Is.True);
            Assert.That(grid.IsOpen(body, new float3(4.1f, 4, 1.5f)), Is.False, "a closed cell");
            Assert.That(grid.TryNearestOpen(body, right, 0.5f, left, out _), Is.False, "the rooms are no longer joined");
            Assert.That(grid.RegionsOf(body).ToArray(), Is.EqualTo(FloodedByTheCore(grid, 0.51f, Everywhere, out _, out _)));

            // Opened again.
            grid.ClearCost();
            Assert.That(grid.HasRegions(body), Is.False);
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(grid.TryNearestOpen(body, right, 0.5f, left, out _), Is.True);

            // A fill that is refused changes nothing, and the regions stand.
            Assert.That(grid.FillCost(Solid.Sphere(float.NaN, 1, 1, 1), CellCost.Closed), Is.False);
            Assert.That(grid.HasRegions(body), Is.True);
        }

        [Test]
        public void EachSizeOfBodyHasItsOwnRegions() {
            using var grid = Marked();
            var small = grid.AddSizeClass(0.3f);
            var large = grid.AddSizeClass(0.6f);
            Assert.That((small, large), Is.EqualTo((0, 1)));
            Assert.That(grid.SizeClassCount, Is.EqualTo(2));
            grid.ScheduleRegions(small, Everywhere);
            grid.ScheduleRegions(large, Everywhere);
            // 0.55 above the floor's top: room for the small body, not for the large.
            var low = new float3(2.25f, 2.25f, 0.75f);
            Assert.That(grid.IsOpen(small, low), Is.True);
            Assert.That(grid.IsOpen(large, low), Is.False);
            Assert.That(grid.Reached(small), Is.GreaterThan(grid.Reached(large)));
            Assert.That(grid.RegionsOf(small).ToArray(), Is.EqualTo(FloodedByTheCore(grid, 0.3f, Everywhere, out _, out _)));
            Assert.That(grid.RegionsOf(large).ToArray(), Is.EqualTo(FloodedByTheCore(grid, 0.6f, Everywhere, out _, out _)));
        }

        [Test]
        public void AMarkingRunsAfterAFloodThatReadsTheWallDistance() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            // Scheduled while the flood may still be reading: it must be made to run after it.
            Assert.DoesNotThrow(() => grid.ScheduleMark());
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.RegionsOf(body).ToArray(), Is.EqualTo(FloodedByTheCore(grid, 0.51f, Everywhere, out _, out _)));
        }

        [Test]
        public void ASizeThatCannotBeServedIsRefused() {
            using var grid = Marked();
            Assert.That(grid.AddSizeClass(0.25f), Is.EqualTo(-1), "half a cell: it has no guarantee");
            Assert.That(grid.AddSizeClass(1.57f), Is.EqualTo(-1), "it needs more than the cap of 1 m can say");
            Assert.That(grid.AddSizeClass(float.NaN), Is.EqualTo(-1));
            Assert.That(grid.SizeClassCount, Is.EqualTo(0));
            using var high = new WallGrid(Grid, 1f / 128f, 255);
            Assert.That(high.AddSizeClass(1.57f), Is.EqualTo(0));
        }

        [Test]
        public void ANumberThatNamesNoSizeClassIsRefused() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            Assert.That(() => grid.IsOpen(body + 1, float3.zero), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.IsOpen(-1, float3.zero), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.ScheduleRegions(7, Everywhere), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.HasRegions(1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.RegionsOf(1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.SizeOf(1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void TheGridCountsItsRegionsBytes() {
            using var grid = new WallGrid(Grid);
            var before = grid.ByteCount;
            grid.AddSizeClass(0.51f);
            Assert.That(grid.ByteCount, Is.EqualTo(before + Grid.CellCount + 16));
        }

        [Test]
        public void ItIsTheCellThatIsOpenNotThePoint() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            // The wall's face is at x = 4.2. This point's cell has its centre 0.55 from it and is open;
            // the point itself is 0.3 from it, where a body of radius 0.51 does not fit.
            var point = new float3(4.5f, 1.25f, 5.25f);
            Assert.That(grid.IsOpen(body, point), Is.True);
            Assert.That(grid.TryDistance(point, 2f, out var exact), Is.True);
            Assert.That(exact, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(exact, Is.LessThan(grid.SizeOf(body).Radius));
        }

        [Test]
        public void AfterDisposeRegionsRefuseToBeUsed() {
            var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            grid.ScheduleRegions(body, LeftRoomOnly);
            Assert.DoesNotThrow(() => grid.Dispose(), "with a flood still running");
            Assert.That(() => grid.IsOpen(body, float3.zero), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.ScheduleRegions(body, Everywhere), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.AddSizeClass(0.51f), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.HasRegions(body), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => grid.TryNearestOpen(body, float3.zero, 1f, out _), Throws.InstanceOf<ObjectDisposedException>());
        }

        // A job of a caller's that reads a size class's regions after a wait. Not for Burst.
        private struct SlowCountOpenJob : IJob {
            [ReadOnly] public NativeArray<byte>.ReadOnly Region;
            public NativeArray<int> Open;
            public int Milliseconds;

            public void Execute() {
                System.Threading.Thread.Sleep(Milliseconds);
                var open = 0;
                for (var i = 0; i < Region.Length; i++) { if (Region[i] != 0) { open++; } }
                Open[0] = open;
            }
        }

        [Test]
        public void AFloodRunsAfterACallersReaderOfTheRegions() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            var everywhere = grid.Reached(body);
            var open = new NativeArray<int>(1, Allocator.TempJob);
            var reader = new SlowCountOpenJob { Region = grid.RegionsOf(body), Open = open, Milliseconds = 300 }.Schedule();
            grid.AddReader(reader);

            // With no seeds the next flood leaves no cell open: the reader must have counted before it.
            grid.ScheduleRegions(body, ReadOnlySpan<Solid>.Empty);
            Assert.That(grid.Reached(body), Is.EqualTo(0));
            Assert.That(reader.IsCompleted, Is.True);
            Assert.That(open[0], Is.EqualTo(everywhere));
            open.Dispose();
        }

        [Test]
        public void ReadingOneSizeClassDoesNotWaitForACallersReader() {
            using var grid = Marked();
            var body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            var open = new NativeArray<int>(1, Allocator.TempJob);
            var reader = new SlowCountOpenJob { Region = grid.RegionsOf(body), Open = open, Milliseconds = 500 }.Schedule();
            grid.AddReader(reader);
            Assert.That(grid.IsOpen(body, new float3(2, 2, 2)), Is.True);
            Assert.That(grid.HasRegions(body), Is.True);
            Assert.That(grid.TryNearestOpen(body, new float3(2, 2, 0.2f), 3f, out _), Is.True);
            Assert.That(reader.IsCompleted, Is.False, "none of those waited half a second for the reader");
            reader.Complete();
            open.Dispose();
        }
    }
}
