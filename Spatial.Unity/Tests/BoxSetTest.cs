using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class BoxSetTest {
        private static readonly Box Extent = new Box(0, 0, 0, 100, 100, 100);
        private static readonly float3 Unit = new float3(0.5f, 0.5f, 0.5f);

        [OneTimeSetUp]
        public void CompileFirstAndLookForLeaks() {
            // Without this a job runs as managed code until Burst has finished compiling it.
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
        }

        [Test]
        public void BurstIsOn() => Assert.That(BurstCompiler.IsEnabled, Is.True);

        // A unit cube whose lowest corner is at (x, 0, 0), as an item with its origin at its middle.
        private static bool AddCube(BoxSet set, int id, float x) =>
            set.Add(id, float3.zero, Unit, new float3(x + 0.5f, 0.5f, 0.5f), quaternion.identity);

        private static bool MoveCube(BoxSet set, int id, float x) =>
            set.Move(id, new float3(x + 0.5f, 0.5f, 0.5f), quaternion.identity);

        private static Box Cube(float x) => new Box(x, 0, 0, x + 1, 1, 1);

        // Unit cubes named 10, 20, 30, 40, 50 with their lowest corners at x = 0, 2, 4, 6, 8.
        private static BoxSet FiveCubes() {
            var set = new BoxSet(8, Extent);
            for (var i = 0; i < 5; i++) { AddCube(set, 10 * (i + 1), 2 * i); }
            set.Schedule();
            return set;
        }

        // One in-sphere query through the job, by scan or by tree: the ids found, in the order given.
        private static int[] InSphereAsGiven(BoxSet set, float3 point, float radius, bool tree, out int total) {
            var spheres = new NativeArray<float4>(1, Allocator.TempJob);
            var results = new NativeArray<int>(set.Capacity, Allocator.TempJob);
            var counts = new NativeArray<int>(1, Allocator.TempJob);
            var totals = new NativeArray<int>(1, Allocator.TempJob);
            spheres[0] = new float4(point, radius);
            set.ScheduleInSphere(spheres, results, counts, totals, set.Capacity, tree).Complete();
            var found = new int[math.max(counts[0], 0)];
            for (var i = 0; i < found.Length; i++) { found[i] = results[i]; }
            total = totals[0];
            spheres.Dispose(); results.Dispose(); counts.Dispose(); totals.Dispose();
            return found;
        }

        // The same items are expected from the scan and from the tree: their ids, in ascending order.
        private static int[] InSphere(BoxSet set, float3 point, float radius) {
            var scan = InSphereAsGiven(set, point, radius, false, out var scanTotal);
            var tree = InSphereAsGiven(set, point, radius, true, out var treeTotal);
            Array.Sort(scan);
            Array.Sort(tree);
            Assert.That(tree, Is.EqualTo(scan));
            Assert.That(treeTotal, Is.EqualTo(scanTotal));
            Assert.That(scanTotal, Is.EqualTo(scan.Length));
            return scan;
        }

        private static (int[] ids, float[] distances) Nearest(BoxSet set, float3 point, float distance, int k, bool tree) {
            var points = new NativeArray<float4>(1, Allocator.TempJob);
            var results = new NativeArray<int>(k, Allocator.TempJob);
            var distances = new NativeArray<float>(k, Allocator.TempJob);
            var counts = new NativeArray<int>(1, Allocator.TempJob);
            points[0] = new float4(point, distance);
            set.ScheduleNearest(points, results, distances, counts, k, tree).Complete();
            var ids = new int[math.max(counts[0], 0)];
            var found = new float[ids.Length];
            for (var i = 0; i < ids.Length; i++) { ids[i] = results[i]; found[i] = distances[i]; }
            points.Dispose(); results.Dispose(); distances.Dispose(); counts.Dispose();
            return (ids, found);
        }

        [Test]
        public void AddedItemsAreFoundByTheirIds() {
            // Unit cubes in a row along x, a gap of 1 between them, named 10, 20, 30.
            using var set = new BoxSet(8, Extent);
            Assert.That(AddCube(set, 10, 0) && AddCube(set, 20, 2) && AddCube(set, 30, 4), Is.True);
            set.Schedule();

            Assert.That(set.Count, Is.EqualTo(3));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(0), Cube(2), Cube(4) }));
            Assert.That(InSphere(set, new float3(2.5f, 0.5f, 0.5f), 1.6f), Is.EqualTo(new[] { 10, 20, 30 }));
            Assert.That(InSphere(set, new float3(2.5f, 0.5f, 0.5f), 1.4f), Is.EqualTo(new[] { 20 }));
            Assert.That(InSphere(set, new float3(50, 50, 50), 1), Is.Empty);
        }

        [Test]
        public void NothingTakesEffectUntilSchedule() {
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0);

            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1), Is.Empty);
            set.Schedule();
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1), Is.EqualTo(new[] { 10 }));

            // A removal waits too.
            Assert.That(set.Remove(10), Is.True);
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1), Is.EqualTo(new[] { 10 }));
            set.Schedule();
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1), Is.Empty);
        }

        [Test]
        public void ARemovedItemIsGoneAndTheLastTakesItsPlace() {
            using var set = FiveCubes();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 10, 20, 30, 40, 50 }));

            Assert.That(set.Remove(20), Is.True);
            set.Schedule();

            Assert.That(set.Count, Is.EqualTo(4));
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 10, 50, 30, 40 }));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(0), Cube(8), Cube(4), Cube(6) }));
            // Around where 20 was: its neighbours, by their own ids, though 50 now sits in 20's place.
            Assert.That(InSphere(set, new float3(2.5f, 0.5f, 0.5f), 1.6f), Is.EqualTo(new[] { 10, 30 }));
            Assert.That(InSphere(set, new float3(8.5f, 0.5f, 0.5f), 0.1f), Is.EqualTo(new[] { 50 }));
        }

        [Test]
        public void TheItemThatTakesAPlaceKeepsItsOwnShapeAndId() {
            // Cubes of half-size 0.5, 1 and 2, named 10, 20 and 30, at x = 10, 20 and 30.
            using var set = new BoxSet(8, Extent);
            set.Add(10, float3.zero, new float3(0.5f), new float3(10, 50, 50), quaternion.identity);
            set.Add(20, float3.zero, new float3(1), new float3(20, 50, 50), quaternion.identity);
            set.Add(30, float3.zero, new float3(2), new float3(30, 50, 50), quaternion.identity);
            set.Schedule();
            set.Remove(10);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 30, 20 }));

            // 30 now sits where 10 was. Moved, it is found by its own id and is still its own size.
            Assert.That(set.Move(30, new float3(60, 50, 50), quaternion.identity), Is.True);
            set.Schedule();

            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { new Box(58, 48, 48, 62, 52, 52), new Box(19, 49, 49, 21, 51, 51) }));
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 0 }));
            Assert.That(InSphere(set, new float3(60, 50, 50), 0.1f), Is.EqualTo(new[] { 30 }));
            Assert.That(InSphere(set, new float3(30, 50, 50), 0.1f), Is.Empty);

            // And it can be removed by that id.
            Assert.That(set.Remove(30), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 20 }));
        }

        [Test]
        public void ChangesTakeEffectInTheOrderMade() {
            using var set = FiveCubes();

            // A removal, then a move of the item that takes the freed place.
            set.Remove(20);
            Assert.That(MoveCube(set, 50, 20), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 10, 50, 30, 40 }));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(0), Cube(20), Cube(4), Cube(6) }));
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 1 }));

            // A move, then a removal that sends the moved item to another place.
            Assert.That(MoveCube(set, 40, 30), Is.True);
            set.Remove(10);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 40, 50, 30 }));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(30), Cube(20), Cube(4) }));
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 0 }));

            // An item added and moved before it joins: it joins at the later pose.
            AddCube(set, 60, 40);
            Assert.That(MoveCube(set, 60, 50), Is.True);
            set.Schedule();
            Assert.That(set.Boxes[3], Is.EqualTo(Cube(50)));

            // Removed and added again under the same id: the new item, at the end.
            set.Remove(40);
            Assert.That(AddCube(set, 40, 70), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 60, 50, 30, 40 }));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(50), Cube(20), Cube(4), Cube(70) }));

            // Added, moved and removed before it joins: it never joins, and nothing else is disturbed.
            AddCube(set, 70, 80);
            MoveCube(set, 70, 90);
            Assert.That(set.Remove(70), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 60, 50, 30, 40 }));
            Assert.That(set.Awake.ToArray(), Is.Empty);
        }

        [Test]
        public void OnlyChangesCauseWork() {
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0); AddCube(set, 20, 2); AddCube(set, 30, 4);
            set.Schedule();
            Assert.That(set.Rebuilds, Is.EqualTo(1));
            var before = set.Boxes.ToArray();

            // The three items just added were all worked out.
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 0, 1, 2 }));

            // Nothing added, removed or moved: nothing is scheduled.
            set.Schedule();
            set.Schedule();
            Assert.That(set.Rebuilds, Is.EqualTo(1));
            Assert.That(set.Awake.ToArray(), Is.Empty);

            // One item moves: its box changes and the others' do not.
            Assert.That(MoveCube(set, 20, 12), Is.True);
            set.Schedule();
            Assert.That(set.Rebuilds, Is.EqualTo(2));
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 1 }));
            var after = set.Boxes.ToArray();
            Assert.That(after[0], Is.EqualTo(before[0]));
            Assert.That(after[2], Is.EqualTo(before[2]));
            Assert.That(after[1], Is.EqualTo(Cube(12)));
            Assert.That(InSphere(set, new float3(2.5f, 0.5f, 0.5f), 1.6f), Is.EqualTo(new[] { 10, 30 }));
            Assert.That(InSphere(set, new float3(12.5f, 0.5f, 0.5f), 0.1f), Is.EqualTo(new[] { 20 }));
        }

        [Test]
        public void TheLastPoseGivenWins() {
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0);
            AddCube(set, 20, 2);
            set.Schedule();

            MoveCube(set, 10, 20);
            MoveCube(set, 10, 40);
            set.Schedule();

            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(40), Cube(2) }));
            // Moved twice, worked out once.
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void PosesHandedOverInArraysMoveTheItemsNamed() {
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0); AddCube(set, 20, 2); AddCube(set, 30, 4);
            set.Schedule();
            var ids = new NativeArray<int>(new[] { 30, 99, 10 }, Allocator.TempJob);
            var positions = new NativeArray<float3>(3, Allocator.TempJob);
            var rotations = new NativeArray<quaternion>(3, Allocator.TempJob);
            for (var i = 0; i < 3; i++) { positions[i] = new float3(60.5f + 10 * i, 0.5f, 0.5f); rotations[i] = quaternion.identity; }

            // 30 goes to x = 60 and 10 to x = 80. There is no 99, so its pose is passed over.
            set.Schedule(ids, positions, rotations);
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(80), Cube(2), Cube(60) }));
            Assert.That(set.Awake.ToArray(), Is.EqualTo(new[] { 0, 2 }));
            Assert.That(set.Rebuilds, Is.EqualTo(2));
            Assert.That(InSphere(set, new float3(60.5f, 0.5f, 0.5f), 0.1f), Is.EqualTo(new[] { 30 }));

            // The arrays are read after the changes made by calls, and after the job that fills them:
            // 20 is removed, 40 joins, and the job puts 30 at x = 20 and 40 at x = 30.
            ids[1] = 40; ids[2] = 20;
            set.Remove(20);
            AddCube(set, 40, 6);
            var filled = new FillJob { Positions = positions, FirstX = 20.5f }.Schedule();
            set.Schedule(ids, positions, rotations, filled);
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 10, 30, 40 }));
            Assert.That(set.Boxes.ToArray(), Is.EqualTo(new[] { Cube(80), Cube(20), Cube(30) }));

            // No poses and no changes: nothing is scheduled.
            var none = new NativeArray<int>(0, Allocator.TempJob);
            set.Schedule(none, positions.GetSubArray(0, 0), rotations.GetSubArray(0, 0));
            Assert.That(set.Rebuilds, Is.EqualTo(3));

            // One pose for each id.
            Assert.Throws<ArgumentException>(() => set.Schedule(ids, positions.GetSubArray(0, 2), rotations));
            Assert.Throws<ArgumentException>(() => set.Schedule(ids, positions, rotations.GetSubArray(0, 2)));
            set.Complete();
            ids.Dispose(); positions.Dispose(); rotations.Dispose(); none.Dispose();
        }

        // Writes x = FirstX, FirstX + 10, FirstX + 20 ... at the height of the unit cubes' middles.
        [BurstCompile]
        private struct FillJob : IJob {
            public NativeArray<float3> Positions;
            public float FirstX;

            public void Execute() {
                for (var i = 0; i < Positions.Length; i++) { Positions[i] = new float3(FirstX + 10 * i, 0.5f, 0.5f); }
            }
        }

        [Test]
        public void ATurnedItemsBoxCoversIt() {
            // Half-extents 1, 2, 3 around a centre 10 along the item's own x, turned a quarter about z:
            // the item's x now points along y, so the centre is 10 up and the extents on x and y swap.
            using var set = new BoxSet(8, Extent);
            set.Add(10, new float3(10, 0, 0), new float3(1, 2, 3), new float3(50, 50, 50), quaternion.RotateZ(math.PI / 2));
            set.Schedule();

            var box = set.Boxes[0];
            Assert.That(box.MinX, Is.EqualTo(48f).Within(1e-4f));
            Assert.That(box.MaxX, Is.EqualTo(52f).Within(1e-4f));
            Assert.That(box.MinY, Is.EqualTo(59f).Within(1e-4f));
            Assert.That(box.MaxY, Is.EqualTo(61f).Within(1e-4f));
            Assert.That(box.MinZ, Is.EqualTo(47f).Within(1e-4f));
            Assert.That(box.MaxZ, Is.EqualTo(53f).Within(1e-4f));
        }

        [Test]
        public void IdsThatAreNotThereAreRefused() {
            using var set = new BoxSet(2, Extent);
            Assert.That(set.Move(10, float3.zero, quaternion.identity), Is.False);
            Assert.That(set.Remove(10), Is.False);

            Assert.That(AddCube(set, 10, 0), Is.True);
            Assert.That(AddCube(set, 10, 2), Is.False, "the id is waiting to join");
            set.Schedule();
            Assert.That(AddCube(set, 10, 2), Is.False, "the id is in the set");

            // The set holds two.
            Assert.That(AddCube(set, 20, 2), Is.True);
            Assert.That(AddCube(set, 30, 4), Is.False, "the set is full");

            // An item removed before it joined never joins, and frees its place.
            Assert.That(set.Remove(20), Is.True);
            Assert.That(AddCube(set, 30, 4), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 10, 30 }));

            // Removed twice, moved after removal.
            Assert.That(set.Remove(10), Is.True);
            Assert.That(set.Remove(10), Is.False);
            Assert.That(set.Move(10, float3.zero, quaternion.identity), Is.False);

            // Its id is free again at once, and takes the freed place.
            Assert.That(AddCube(set, 10, 6), Is.True);
            set.Schedule();
            Assert.That(set.Ids.ToArray(), Is.EqualTo(new[] { 30, 10 }));
            Assert.That(InSphere(set, new float3(6.5f, 0.5f, 0.5f), 0.1f), Is.EqualTo(new[] { 10 }));
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 0.1f), Is.Empty);
        }

        [Test]
        public void NearestGivesIdsNearestFirst() {
            using var set = new BoxSet(8, Extent);
            for (var i = 0; i < 5; i++) { AddCube(set, 10 * (5 - i), 2 * i); }   // 50, 40, 30, 20, 10 along x
            set.Schedule();

            // From the middle of the middle cube (30): its neighbours are equally far, and the one
            // that was added first (40) comes first.
            foreach (var tree in new[] { false, true }) {
                var (ids, distances) = Nearest(set, new float3(4.5f, 0.5f, 0.5f), 100, 3, tree);
                Assert.That(ids, Is.EqualTo(new[] { 30, 40, 20 }));
                Assert.That(distances, Is.EqualTo(new[] { 0f, 2.25f, 2.25f }));
                Assert.That(Nearest(set, new float3(4.5f, 0.5f, 0.5f), 1.4f, 3, tree).ids, Is.EqualTo(new[] { 30 }));
                Assert.That(Nearest(set, new float3(4.5f, 0.5f, 0.5f), -1, 3, tree).ids, Is.Empty);
            }
        }

        [Test]
        public void TheScanAnswersInTheOrderAddedAndTheTreeAlongItsCurve() {
            // Cubes added at x = 8, 0 and 4. The scan gives them as they were added; the tree sorts
            // them along its curve, which here runs along x.
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 8); AddCube(set, 20, 0); AddCube(set, 30, 4);
            set.Schedule();

            Assert.That(InSphereAsGiven(set, new float3(4.5f, 0.5f, 0.5f), 100, false, out _), Is.EqualTo(new[] { 10, 20, 30 }));
            Assert.That(InSphereAsGiven(set, new float3(4.5f, 0.5f, 0.5f), 100, true, out _), Is.EqualTo(new[] { 20, 30, 10 }));
        }

        [Test]
        public void TheTreeIsWalkedOnlyWhenAsked() {
            // Two cubes and a tree of no nodes, as the jobs see them. A walk of the tree refuses that;
            // a scan never looks at the tree, and nearest answers the same either way otherwise.
            var boxes = new NativeArray<Box>(new[] { Cube(0), Cube(2) }, Allocator.TempJob);
            var columns = new NativeArray<float>[6];
            for (var i = 0; i < 6; i++) { columns[i] = new NativeArray<float>(2, Allocator.TempJob); }
            BoxScan.Pack(boxes.AsReadOnlySpan(), columns[0].AsSpan(), columns[1].AsSpan(), columns[2].AsSpan(), columns[3].AsSpan(), columns[4].AsSpan(), columns[5].AsSpan());
            var ids = new NativeArray<int>(new[] { 10, 20 }, Allocator.TempJob);
            var order = new NativeArray<int>(new[] { 0, 1 }, Allocator.TempJob);
            var nodes = new NativeArray<BoxNode>(2, Allocator.TempJob);
            var nodeCount = new NativeArray<int>(new[] { 0 }, Allocator.TempJob);
            var count = new NativeArray<int>(new[] { 2 }, Allocator.TempJob);
            var view = new BoxSetView {
                Boxes = boxes, MinX = columns[0], MinY = columns[1], MinZ = columns[2], MaxX = columns[3], MaxY = columns[4], MaxZ = columns[5],
                Ids = ids, Order = order, Nodes = nodes, NodeCount = nodeCount, Count = count,
            };
            var points = new NativeArray<float4>(new[] { new float4(0.5f, 0.5f, 0.5f, 100) }, Allocator.TempJob);
            var results = new NativeArray<int>(2, Allocator.TempJob);
            var distances = new NativeArray<float>(2, Allocator.TempJob);
            var counts = new NativeArray<int>(1, Allocator.TempJob);
            var totals = new NativeArray<int>(1, Allocator.TempJob);

            new InSphereJob { Set = view, Spheres = points, Results = results, Counts = counts, Totals = totals, Stride = 2, Tree = false }.Schedule(1, 1).Complete();
            Assert.That(counts[0], Is.EqualTo(2));
            Assert.That(totals[0], Is.EqualTo(2));
            new InSphereJob { Set = view, Spheres = points, Results = results, Counts = counts, Totals = totals, Stride = 2, Tree = true }.Schedule(1, 1).Complete();
            Assert.That(counts[0], Is.EqualTo(-1));
            Assert.That(totals[0], Is.EqualTo(-1));

            new NearestJob { Set = view, Points = points, Results = results, DistancesSquared = distances, Counts = counts, Stride = 2, Tree = false }.Schedule(1, 1).Complete();
            Assert.That(counts[0], Is.EqualTo(2));
            Assert.That(results.ToArray(), Is.EqualTo(new[] { 10, 20 }));
            new NearestJob { Set = view, Points = points, Results = results, DistancesSquared = distances, Counts = counts, Stride = 2, Tree = true }.Schedule(1, 1).Complete();
            Assert.That(counts[0], Is.EqualTo(-1));

            boxes.Dispose(); ids.Dispose(); order.Dispose(); nodes.Dispose(); nodeCount.Dispose(); count.Dispose();
            points.Dispose(); results.Dispose(); distances.Dispose(); counts.Dispose(); totals.Dispose();
            for (var i = 0; i < 6; i++) { columns[i].Dispose(); }
        }

        [Test]
        public void ManyQueriesAtOnceAnswerInOrder() {
            using var set = FiveCubes();
            var spheres = new NativeArray<float4>(3, Allocator.TempJob);
            var results = new NativeArray<int>(6, Allocator.TempJob);
            var counts = new NativeArray<int>(3, Allocator.TempJob);
            var totals = new NativeArray<int>(3, Allocator.TempJob);
            for (var i = 0; i < results.Length; i++) { results[i] = -1; }
            spheres[0] = new float4(0.5f, 0.5f, 0.5f, 0.1f);     // cube 10 alone
            spheres[1] = new float4(4.5f, 0.5f, 0.5f, 100);      // all five, but room for two
            spheres[2] = new float4(50, 50, 50, 1);              // nothing

            set.ScheduleInSphere(spheres, results, counts, totals, 2, false).Complete();

            Assert.That(counts.ToArray(), Is.EqualTo(new[] { 1, 2, 0 }));
            Assert.That(totals.ToArray(), Is.EqualTo(new[] { 1, 5, 0 }));
            Assert.That(results.ToArray(), Is.EqualTo(new[] { 10, -1, 10, 20, -1, -1 }));
            spheres.Dispose(); results.Dispose(); counts.Dispose(); totals.Dispose();
        }

        [Test]
        public void QueriesNotYetDoneAnswerForTheSetAsItWas() {
            // Two batches are scheduled and left. Then two items go and the set is scheduled again,
            // which must wait for neither on this thread and must run after both.
            using var set = FiveCubes();
            var points = new NativeArray<float4>(1, Allocator.TempJob);
            var inSphere = new NativeArray<int>(5, Allocator.TempJob);
            var nearest = new NativeArray<int>(5, Allocator.TempJob);
            var distances = new NativeArray<float>(5, Allocator.TempJob);
            var counts = new NativeArray<int>(1, Allocator.TempJob);
            var nearestCounts = new NativeArray<int>(1, Allocator.TempJob);
            var totals = new NativeArray<int>(1, Allocator.TempJob);
            points[0] = new float4(0.5f, 0.5f, 0.5f, 100);
            var first = set.ScheduleInSphere(points, inSphere, counts, totals, 5, false);
            var second = set.ScheduleNearest(points, nearest, distances, nearestCounts, 5, true);

            set.Remove(20);
            set.Remove(40);
            var updated = set.Schedule();
            updated.Complete();

            // The update ran after both, so both are done, and answered with all five.
            Assert.That(first.IsCompleted && second.IsCompleted, Is.True);
            Assert.That(inSphere.ToArray(), Is.EqualTo(new[] { 10, 20, 30, 40, 50 }));
            Assert.That(nearest.ToArray(), Is.EqualTo(new[] { 10, 20, 30, 40, 50 }));
            Assert.That(distances.ToArray(), Is.EqualTo(new[] { 0f, 2.25f, 12.25f, 30.25f, 56.25f }));
            first.Complete();
            second.Complete();

            // Asked again, three are left.
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 100), Is.EqualTo(new[] { 10, 30, 50 }));
            points.Dispose(); inSphere.Dispose(); nearest.Dispose(); distances.Dispose(); counts.Dispose(); nearestCounts.Dispose(); totals.Dispose();
        }

        [Test]
        public void AnItemExactlyOnTheSpheresEdgeIsFound() {
            // The cube 20 is exactly 1.5 from the point, by numbers that single precision holds exactly.
            using var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0); AddCube(set, 20, 2);
            set.Schedule();

            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1.5f), Is.EqualTo(new[] { 10, 20 }));
            Assert.That(InSphere(set, new float3(0.5f, 0.5f, 0.5f), 1.4999999f), Is.EqualTo(new[] { 10 }));
        }

        [Test]
        public void BuffersTooShortAreRefusedUpFront() {
            using var set = new BoxSet(8, Extent);
            var spheres = new NativeArray<float4>(2, Allocator.TempJob);
            var results = new NativeArray<int>(3, Allocator.TempJob);
            var distances = new NativeArray<float>(4, Allocator.TempJob);
            var counts = new NativeArray<int>(2, Allocator.TempJob);
            var one = new NativeArray<int>(1, Allocator.TempJob);

            // Two queries of two results each need four.
            Assert.Throws<ArgumentException>(() => set.ScheduleInSphere(spheres, results, counts, counts, 2, false));
            Assert.Throws<ArgumentException>(() => set.ScheduleNearest(spheres, results, distances, counts, 2, false));
            Assert.Throws<ArgumentException>(() => set.ScheduleInSphere(spheres, distances.Reinterpret<int>(), one, counts, 2, false));
            Assert.Throws<ArgumentException>(() => set.ScheduleInSphere(spheres, distances.Reinterpret<int>(), counts, one, 2, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => set.ScheduleInSphere(spheres, results, counts, counts, -1, false));
            spheres.Dispose(); results.Dispose(); distances.Dispose(); counts.Dispose(); one.Dispose();
        }

        [Test]
        public void ADisposedSetRefusesEverythingThatTouchesIt() {
            var set = new BoxSet(8, Extent);
            AddCube(set, 10, 0);
            set.Schedule();
            set.Dispose();
            set.Dispose();
            var points = new NativeArray<float4>(1, Allocator.TempJob);
            var results = new NativeArray<int>(1, Allocator.TempJob);
            var distances = new NativeArray<float>(1, Allocator.TempJob);
            var ids = new NativeArray<int>(1, Allocator.TempJob);
            var positions = new NativeArray<float3>(1, Allocator.TempJob);
            var rotations = new NativeArray<quaternion>(1, Allocator.TempJob);

            RefusedByTheSet(() => AddCube(set, 20, 2));
            RefusedByTheSet(() => set.Remove(10));
            RefusedByTheSet(() => MoveCube(set, 10, 2));
            RefusedByTheSet(() => set.Schedule());
            RefusedByTheSet(() => set.Schedule(ids, positions, rotations));
            RefusedByTheSet(() => set.ScheduleInSphere(points, results, results, results, 1, false));
            RefusedByTheSet(() => set.ScheduleNearest(points, results, distances, results, 1, false));
            RefusedByTheSet(() => { var _ = set.Boxes; });
            RefusedByTheSet(() => { var _ = set.Ids; });
            RefusedByTheSet(() => { var _ = set.Nodes; });
            RefusedByTheSet(() => { var _ = set.Awake; });

            // Nothing is left to wait for, and the plain values are still there.
            set.Complete();
            Assert.That(set.IsDisposed, Is.True);
            Assert.That(set.Count, Is.EqualTo(1));
            Assert.That(set.Capacity, Is.EqualTo(8));
            points.Dispose(); results.Dispose(); distances.Dispose(); ids.Dispose(); positions.Dispose(); rotations.Dispose();
        }

        [Test]
        public void DisposeWaitsForQueriesStillRunning() {
            // 512 queries over all five cubes are scheduled and the set is disposed at once.
            var set = FiveCubes();
            var points = new NativeArray<float4>(512, Allocator.TempJob);
            var results = new NativeArray<int>(512 * 5, Allocator.TempJob);
            var counts = new NativeArray<int>(512, Allocator.TempJob);
            var totals = new NativeArray<int>(512, Allocator.TempJob);
            for (var i = 0; i < points.Length; i++) { points[i] = new float4(4.5f, 0.5f, 0.5f, 100); }
            var running = set.ScheduleInSphere(points, results, counts, totals, 5, false);

            set.Dispose();

            // Its arrays are gone, so the queries must have finished first, with their answers.
            Assert.That(running.IsCompleted, Is.True);
            running.Complete();
            Assert.That(totals[0], Is.EqualTo(5));
            Assert.That(totals[511], Is.EqualTo(5));
            Assert.That(results.GetSubArray(511 * 5, 5).ToArray(), Is.EqualTo(new[] { 10, 20, 30, 40, 50 }));
            points.Dispose(); results.Dispose(); counts.Dispose(); totals.Dispose();
        }

        // Refused by the set's own check, which names it. Unity's check on a disposed array throws
        // the same type in the editor, and a player build has only the set's.
        private static void RefusedByTheSet(TestDelegate action) =>
            Assert.That(Assert.Throws<ObjectDisposedException>(action).ObjectName, Is.EqualTo("BoxSet"));

        [Test]
        public void AnEmptySetAnswersNothing() {
            using var set = new BoxSet(0, Extent);
            set.Schedule();

            Assert.That(set.Rebuilds, Is.EqualTo(0));
            Assert.That(InSphere(set, float3.zero, 100), Is.Empty);
            Assert.That(Nearest(set, float3.zero, 100, 3, true).ids, Is.Empty);
        }
    }
}
