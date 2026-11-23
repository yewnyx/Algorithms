using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class WallGridFieldTest {
        // 16 cells of half a metre each way from the origin.
        private static readonly GridShape Grid = new GridShape(0, 0, 0, 0.5f, 16, 16, 16);

        // A floor, and a wall 0.4 thick between two rooms with an opening 2 m wide and 3 m high.
        private static readonly Solid[] TwoRooms = {
            Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f)),
            Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)),
            Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)),
            Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
        };

        private static readonly Solid[] Everywhere = { Solid.Box(new Box(-1, -1, -1, 9, 9, 9)) };

        // A field towards a point in the left-hand room, from the cells within 0.8 m of it.
        private static readonly TravelSeed[] ToTheLeftRoom = { TravelSeed.FromPoint(Solid.Sphere(2, 2, 2, 0.8f), 2, 2, 2) };

        // The opening's cells, to cost more or to close.
        private static readonly Solid Opening = Solid.Box(new Box(3.6f, 2.5f, 0, 4.4f, 5.5f, 4));

        [OneTimeSetUp]
        public void CompileFirstAndLookForLeaks() {
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
        }

        // A grid marked and flooded for one size of body.
        private static WallGrid Flooded(out int body) {
            var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleMark();
            body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            return grid;
        }

        // The same field built by the core on this thread, with no job and no Burst.
        private static ushort[] BuiltByTheCore(WallGrid grid, int body, TravelSeed[] seeds, float unit, out int reached, out int cut) {
            var distance = new ushort[Grid.CellCount];
            Assert.That(Travel.Build(Grid, grid.RegionsOf(body).ToArray(), grid.Cost.ToArray(), seeds, unit, distance,
                new float[Travel.TimesLength(Grid)], new int[Travel.HeapLength(Grid)], out reached, out cut), Is.True);
            return distance;
        }

        [Test]
        public void TheBuildJobGivesWhatTheCoreGivesByteForByte() {
            using var grid = Flooded(out var body);
            // With a cost on the opening, so that the costs are read too.
            Assert.That(grid.FillCost(Opening, 32), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            // Scheduled straight after the flood, without waiting for it.
            using var field = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(field.IsBuilt, Is.True);
            var expected = BuiltByTheCore(grid, body, ToTheLeftRoom, 0.01f, out var reached, out var cut);
            Assert.That(field.Distance.ToArray(), Is.EqualTo(expected));
            Assert.That(field.Reached, Is.EqualTo(reached));
            Assert.That(field.Cut, Is.EqualTo(cut));
            Assert.That(reached, Is.EqualTo(grid.Reached(body)), "every cell of the regions is reached");
            Assert.That(field.SizeClass, Is.EqualTo(body));
            Assert.That(field.Unit, Is.EqualTo(0.01f));
            Assert.That(field.ByteCount, Is.EqualTo(2L * Grid.CellCount + 12));

            // Another unit.
            using var coarse = grid.ScheduleField(body, ToTheLeftRoom, 0.125f);
            Assert.That(coarse.Distance.ToArray(), Is.EqualTo(BuiltByTheCore(grid, body, ToTheLeftRoom, 0.125f, out _, out _)));
        }

        [Test]
        public void ASampleIsWhatTheCoreGives() {
            using var grid = Flooded(out var body);
            using var field = grid.ScheduleField(body, ToTheLeftRoom);
            var distance = field.Distance.ToArray();
            var answered = 0;
            for (var i = 0; i < 9; i++) {
                for (var j = 0; j < 9; j++) {
                    for (var k = 0; k < 5; k++) {
                        var point = new float3(8f * (i + 0.31f) / 9, 8f * (j + 0.57f) / 9, 8f * (k + 0.83f) / 5);
                        var expected = Travel.Sample(new TravelField(Grid, 0.01f, distance), point.x, point.y, point.z, out var x, out var y, out var z, out var travel);
                        Assert.That(field.TrySample(point, out var direction, out var sampled), Is.EqualTo(expected), $"at {point}");
                        Assert.That(direction, Is.EqualTo(new float3(x, y, z)), $"at {point}");
                        Assert.That(sampled, Is.EqualTo(travel), $"at {point}");
                        if (expected) { answered++; }
                    }
                }
            }
            Assert.That(answered, Is.GreaterThan(250));

            // From the far room the way leads to the opening and through it: at the opening it is along -x.
            Assert.That(field.TrySample(new float3(4.5f, 4, 1.5f), out var through, out var far), Is.True);
            Assert.That(through.x, Is.LessThan(-0.7f));
            Assert.That(far, Is.GreaterThan(2.5f));
            Assert.That(field.TrySample(new float3(4, 1, 5), out _, out _), Is.False, "inside the wall, where none of the eight cells is reached");
        }

        [Test]
        public void ManySamplesAtOnceAreTheSameAsOneAtATime() {
            using var grid = Flooded(out var body);
            using var field = grid.ScheduleField(body, ToTheLeftRoom);
            const int count = 500;
            var points = new NativeArray<float3>(count, Allocator.TempJob);
            var directions = new NativeArray<float3>(count, Allocator.TempJob);
            var distances = new NativeArray<float>(count, Allocator.TempJob);
            for (var i = 0; i < count; i++) {
                // Some inside the wall and some outside the grid, where a sample is refused.
                points[i] = new float3((i * 37 % 97) / 97f * 9f - 0.5f, (i * 53 % 89) / 89f * 9f - 0.5f, (i * 71 % 83) / 83f * 9f - 0.5f);
            }
            field.ScheduleSample(points, directions, distances).Complete();
            int answered = 0, refused = 0;
            for (var i = 0; i < count; i++) {
                if (field.TrySample(points[i], out var direction, out var distance)) {
                    Assert.That(directions[i], Is.EqualTo(direction), $"point {i}");
                    Assert.That(distances[i], Is.EqualTo(distance), $"point {i}");
                    answered++;
                } else {
                    Assert.That(distances[i], Is.EqualTo(-1f), $"point {i}");
                    Assert.That(directions[i], Is.EqualTo(float3.zero), $"point {i}");
                    refused++;
                }
            }
            Assert.That(answered, Is.GreaterThan(250));
            Assert.That(refused, Is.GreaterThan(20));

            // Arrays that are too short are refused when the job is scheduled.
            var few = new NativeArray<float>(count - 1, Allocator.TempJob);
            var fewDirections = new NativeArray<float3>(count - 1, Allocator.TempJob);
            Assert.That(() => field.ScheduleSample(points, directions, few), Throws.ArgumentException);
            Assert.That(() => field.ScheduleSample(points, fewDirections, distances), Throws.ArgumentException);
            points.Dispose(); directions.Dispose(); distances.Dispose(); few.Dispose(); fewDirections.Dispose();
        }

        // A job of a caller's that fills the points after a wait. Not for Burst.
        private struct SlowFillJob : IJob {
            public NativeArray<float3> Points;
            public float3 Point;
            public int Milliseconds;

            public void Execute() {
                System.Threading.Thread.Sleep(Milliseconds);
                for (var i = 0; i < Points.Length; i++) { Points[i] = Point; }
            }
        }

        [Test]
        public void SamplesRunAfterTheJobThatFillsThePoints() {
            using var grid = Flooded(out var body);
            using var field = grid.ScheduleField(body, ToTheLeftRoom);
            var points = new NativeArray<float3>(8, Allocator.TempJob);
            var directions = new NativeArray<float3>(8, Allocator.TempJob);
            var distances = new NativeArray<float>(8, Allocator.TempJob);
            var point = new float3(6, 6, 2);
            var filled = new SlowFillJob { Points = points, Point = point, Milliseconds = 200 }.Schedule();
            var sampled = field.ScheduleSample(points, directions, distances, filled);
            Assert.That(sampled.IsCompleted, Is.False);
            // Disposing the field waits for its samples.
            Assert.That(field.TrySample(point, out var direction, out var distance), Is.True);
            field.Complete();
            Assert.That(sampled.IsCompleted, Is.True);
            for (var i = 0; i < 8; i++) {
                Assert.That(directions[i], Is.EqualTo(direction));
                Assert.That(distances[i], Is.EqualTo(distance));
            }
            points.Dispose(); directions.Dispose(); distances.Dispose();
        }

        [Test]
        public void AStepPaysForTheCellItEntersAtTheGridsCosts() {
            using var grid = Flooded(out var body);
            using var plain = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(plain.TrySample(new float3(6, 4, 1.5f), out _, out var near), Is.True);
            Assert.That(plain.TrySample(new float3(1, 3, 1.5f), out _, out var before), Is.True);

            // The opening now costs three times as much to cross. The far room is farther by it.
            Assert.That(grid.FillCost(Opening, 32), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            using var costly = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(costly.TrySample(new float3(6, 4, 1.5f), out _, out var far), Is.True);
            Assert.That(far, Is.GreaterThan(near + 1f), "two cells of half a metre, each twice as far again");
            // And the near room is as it was.
            Assert.That(costly.TrySample(new float3(1, 3, 1.5f), out _, out var same), Is.True);
            Assert.That(same, Is.EqualTo(before));
        }

        [Test]
        public void AFieldOfRegionsThatAreNoLongerTheGridsIsNotFollowed() {
            var point = new float3(6, 6, 2);
            WallGridField Fresh(WallGrid grid, int body) {
                var field = grid.ScheduleField(body, ToTheLeftRoom);
                Assert.That(field.IsBuilt, Is.True);
                Assert.That(field.TrySample(point, out _, out _), Is.True);
                return field;
            }
            void IsStale(WallGridField field, string why) {
                Assert.That(field.IsBuilt, Is.False, why);
                Assert.That(field.TrySample(point, out var direction, out var distance), Is.False, why);
                Assert.That(direction, Is.EqualTo(float3.zero), why);
                Assert.That(distance, Is.EqualTo(0f), why);
                var points = new NativeArray<float3>(1, Allocator.TempJob);
                var directions = new NativeArray<float3>(1, Allocator.TempJob);
                var distances = new NativeArray<float>(1, Allocator.TempJob);
                points[0] = point;
                field.ScheduleSample(points, directions, distances).Complete();
                Assert.That(distances[0], Is.EqualTo(-1f), why);
                Assert.That(directions[0], Is.EqualTo(float3.zero), why);
                points.Dispose(); directions.Dispose(); distances.Dispose();
            }

            using var grid = Flooded(out var body);

            // The opening is shut: the field still leads through it, and must not be followed.
            using var first = Fresh(grid, body);
            Assert.That(grid.FillCost(Opening, CellCost.Closed), Is.True);
            IsStale(first, "after a change of cost");
            grid.ScheduleRegions(body, Everywhere);
            IsStale(first, "flooding again does not make an old field good");
            // A new one is of the regions as they are: it does not reach the far room.
            using var shut = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(shut.IsBuilt, Is.True);
            Assert.That(shut.TrySample(point, out _, out _), Is.False, "the far room is not reached");
            Assert.That(shut.TrySample(new float3(1, 3, 1.5f), out _, out _), Is.True);
            Assert.That(shut.Reached, Is.LessThan(grid.Reached(body)));

            // Opened again.
            grid.ClearCost();
            IsStale(shut, "after the costs are cleared");
            grid.ScheduleRegions(body, Everywhere);
            using var open = Fresh(grid, body);

            // Flooded again with nothing else changed: the regions may be the same, but the field is of the old ones.
            grid.ScheduleRegions(body, Everywhere);
            IsStale(open, "after another flood");
            using var again = Fresh(grid, body);

            // New shapes.
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            IsStale(again, "after new shapes");
        }

        [Test]
        public void AFieldBuiltWithoutRegionsOrWithASeedThatMayNotBeUsedReachesNothing() {
            using var grid = new WallGrid(Grid);
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleMark();
            var body = grid.AddSizeClass(0.51f);

            // Before the size class has regions.
            using var early = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(early.IsBuilt, Is.False);
            Assert.That(early.Reached, Is.EqualTo(0));
            Assert.That(early.TrySample(new float3(2, 2, 2), out _, out _), Is.False);
            Assert.That(early.Distance.ToArray(), Is.All.EqualTo(Travel.Unreached));

            grid.ScheduleRegions(body, Everywhere);
            Assert.That(early.IsBuilt, Is.False, "a flood afterwards does not make it good");

            // A seed that may not be used.
            TravelSeed[] bad = { ToTheLeftRoom[0], TravelSeed.FromPlane(Solid.Sphere(2, 2, 2, 1), 2, 2, 2, 0, 0, 0) };
            using var refused = grid.ScheduleField(body, bad);
            Assert.That(refused.IsBuilt, Is.False);
            Assert.That(refused.Distance.ToArray(), Is.All.EqualTo(Travel.Unreached));

            // A seed that holds no open cell: built, and reaches nothing. Reached is what tells.
            TravelSeed[] nowhere = { TravelSeed.FromPoint(Solid.Sphere(4, 1, 5, 0.1f), 4, 1, 5) };
            using var empty = grid.ScheduleField(body, nowhere);
            Assert.That(empty.IsBuilt, Is.True);
            Assert.That(empty.Reached, Is.EqualTo(0));
            Assert.That(empty.TrySample(new float3(2, 2, 2), out _, out _), Is.False);

            // No seeds at all.
            using var none = grid.ScheduleField(body, ReadOnlySpan<TravelSeed>.Empty);
            Assert.That(none.IsBuilt, Is.True);
            Assert.That(none.Reached, Is.EqualTo(0));
        }

        [Test]
        public void AUnitOrSizeClassThatCannotBeUsedIsRefused() {
            using var grid = Flooded(out var body);
            Assert.That(() => grid.ScheduleField(body, ToTheLeftRoom, 0f), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.ScheduleField(body, ToTheLeftRoom, -0.01f), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.ScheduleField(body, ToTheLeftRoom, float.NaN), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.ScheduleField(body, ToTheLeftRoom, 0.13f), Throws.InstanceOf<ArgumentOutOfRangeException>(), "more than a quarter of a cell");
            Assert.That(() => grid.ScheduleField(body + 1, ToTheLeftRoom), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => grid.ScheduleField(-1, ToTheLeftRoom), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        // A job that only waits, for a build to be made to run after.
        private struct WaitJob : IJob {
            public int Milliseconds;
            public void Execute() => System.Threading.Thread.Sleep(Milliseconds);
        }

        [Test]
        public void WhatWritesTheGridWaitsForABuildThatHasNotRunYet() {
            using var grid = Flooded(out var body);
            // The build cannot start for 300 ms.
            using var field = grid.ScheduleField(body, ToTheLeftRoom, 0.01f, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(field.Handle.IsCompleted, Is.False);
            // A change of cost writes what the build reads: it waits, and the field was built from the costs before it.
            Assert.That(grid.FillCost(Opening, CellCost.Closed), Is.True);
            Assert.That(field.Handle.IsCompleted, Is.True);
            Assert.That(field.Reached, Is.EqualTo(BuiltByTheCoreReached(grid, body)), "built before the opening was shut");
            Assert.That(field.IsBuilt, Is.False, "and stale at once");
        }

        // How many cells a field reaches with the opening as it is in the shapes alone: every cell of the regions flooded with no cost.
        private static int BuiltByTheCoreReached(WallGrid grid, int body) {
            using var open = new WallGrid(Grid);
            open.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>());
            open.ScheduleMark();
            var same = open.AddSizeClass(grid.SizeOf(body).Radius);
            open.ScheduleRegions(same, Everywhere);
            return open.Reached(same);
        }

        [Test]
        public void ABuildRunsAfterTheFloodEvenWithAMarkingScheduledBetween() {
            using var grid = Flooded(out var body);
            // A flood, then a marking of the same shapes, then the field, with nothing waited for.
            // The marking takes the flood as something to run after; the build must still find it.
            grid.ScheduleRegions(body, Everywhere);
            grid.ScheduleMark();
            WallGridField field = null;
            Assert.DoesNotThrow(() => field = grid.ScheduleField(body, ToTheLeftRoom));
            Assert.That(field.IsBuilt, Is.True);
            Assert.That(field.Distance.ToArray(), Is.EqualTo(BuiltByTheCore(grid, body, ToTheLeftRoom, 0.01f, out _, out _)));
            field.Dispose();
        }

        // A job of a caller's that reads a field's counts after a wait. Not for Burst.
        private struct SlowCountReachedJob : IJob {
            [ReadOnly] public NativeArray<ushort>.ReadOnly Distance;
            public NativeArray<int> Reached;
            public int Milliseconds;

            public void Execute() {
                System.Threading.Thread.Sleep(Milliseconds);
                var reached = 0;
                for (var i = 0; i < Distance.Length; i++) { if (Distance[i] != Travel.Unreached) { reached++; } }
                Reached[0] = reached;
            }
        }

        [Test]
        public void DisposingAFieldWaitsForACallersReaderOfItsCounts() {
            using var grid = Flooded(out var body);
            var field = grid.ScheduleField(body, ToTheLeftRoom);
            var expected = field.Reached;
            var reached = new NativeArray<int>(1, Allocator.TempJob);
            var reader = new SlowCountReachedJob { Distance = field.Distance, Reached = reached, Milliseconds = 300 }.Schedule();
            field.AddReader(reader);
            // What only reads the field does not wait for the reader.
            Assert.That(field.TrySample(new float3(2, 2, 2), out _, out _), Is.True);
            Assert.That(field.IsBuilt, Is.True);
            Assert.That(reader.IsCompleted, Is.False);
            // Disposing frees the counts: it waits.
            Assert.DoesNotThrow(() => field.Dispose());
            Assert.That(reader.IsCompleted, Is.True);
            Assert.That(reached[0], Is.EqualTo(expected));
            reached.Dispose();
            Assert.That(() => field.AddReader(default), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void BuildsScheduledTogetherDoNotWaitForOneAnother() {
            using var grid = Flooded(out var body);
            // The first build cannot start for 400 ms. The second is scheduled after it and has nothing to wait for.
            using var first = grid.ScheduleField(body, ToTheLeftRoom, 0.01f, new WaitJob { Milliseconds = 400 }.Schedule());
            using var second = grid.ScheduleField(body, ToTheLeftRoom);
            second.Handle.Complete();
            Assert.That(first.Handle.IsCompleted, Is.False, "the second finished while the first was still waiting to start");
            Assert.That(second.IsBuilt, Is.True);
            Assert.That(first.IsBuilt, Is.True);
            Assert.That(first.Distance.ToArray(), Is.EqualTo(second.Distance.ToArray()));
        }

        [Test]
        public void TwoBatchesAtOnceAreBothWaitedFor() {
            using var grid = Flooded(out var body);
            var field = grid.ScheduleField(body, ToTheLeftRoom);
            var points = new NativeArray<float3>(4, Allocator.TempJob);
            var firstDirections = new NativeArray<float3>(4, Allocator.TempJob);
            var firstDistances = new NativeArray<float>(4, Allocator.TempJob);
            var secondDirections = new NativeArray<float3>(4, Allocator.TempJob);
            var secondDistances = new NativeArray<float>(4, Allocator.TempJob);
            // The first cannot start for 300 ms; the second can start at once.
            var first = field.ScheduleSample(points, firstDirections, firstDistances, new WaitJob { Milliseconds = 300 }.Schedule());
            var second = field.ScheduleSample(points, secondDirections, secondDistances);
            Assert.That(first.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => field.Dispose());
            Assert.That(first.IsCompleted, Is.True);
            Assert.That(second.IsCompleted, Is.True);
            points.Dispose(); firstDirections.Dispose(); firstDistances.Dispose(); secondDirections.Dispose(); secondDistances.Dispose();
        }

        [Test]
        public void AFieldAnswersAsItWasBuiltAfterItsGridIsDisposed() {
            var points = new NativeArray<float3>(64, Allocator.TempJob);
            for (var i = 0; i < points.Length; i++) { points[i] = new float3(0.6f + i % 8 * 0.9f, 0.7f + i / 8 * 0.9f, 1.1f + i % 5 * 0.7f); }
            var before = new NativeArray<float>(64, Allocator.TempJob);
            var after = new NativeArray<float>(64, Allocator.TempJob);
            var beforeDirections = new NativeArray<float3>(64, Allocator.TempJob);
            var afterDirections = new NativeArray<float3>(64, Allocator.TempJob);

            var grid = Flooded(out var body);
            var field = grid.ScheduleField(body, ToTheLeftRoom);
            // One whose regions are forgotten before the grid goes: it was not good then, and stays so.
            var stale = grid.ScheduleField(body, ToTheLeftRoom);
            Assert.That(stale.IsBuilt, Is.True);
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(stale.IsBuilt, Is.False);
            var good = grid.ScheduleField(body, ToTheLeftRoom);
            field.Dispose();
            field = good;
            var expected = field.Distance.ToArray();
            var reached = field.Reached;
            field.ScheduleSample(points, beforeDirections, before).Complete();
            var far = new float3(6, 6, 2);
            Assert.That(field.TrySample(far, out var direction, out var distance), Is.True);

            grid.Dispose();
            Assert.That(field.IsBuilt, Is.True);
            Assert.That(field.Reached, Is.EqualTo(reached));
            Assert.That(field.Distance.ToArray(), Is.EqualTo(expected));
            Assert.That(field.TrySample(far, out var directionAfter, out var distanceAfter), Is.True);
            Assert.That(directionAfter, Is.EqualTo(direction));
            Assert.That(distanceAfter, Is.EqualTo(distance));
            field.ScheduleSample(points, afterDirections, after).Complete();
            Assert.That(after.ToArray(), Is.EqualTo(before.ToArray()));
            Assert.That(afterDirections.ToArray(), Is.EqualTo(beforeDirections.ToArray()));
            Assert.That(after.ToArray(), Has.Some.GreaterThan(0f));
            Assert.That(stale.IsBuilt, Is.False, "disposing the grid does not make an old field good");
            Assert.That(stale.TrySample(far, out _, out _), Is.False);

            // Nothing tells such a field that the way has changed. A new grid of the same place
            // with the opening shut says the far room cannot get to the goal; the old field still
            // leads there, through the opening. The caller must drop it.
            using var shut = Flooded(out var again);
            Assert.That(shut.FillCost(Opening, CellCost.Closed), Is.True);
            shut.ScheduleRegions(again, Everywhere);
            using var truth = shut.ScheduleField(again, ToTheLeftRoom);
            Assert.That(truth.IsBuilt, Is.True);
            Assert.That(truth.TrySample(far, out _, out _), Is.False, "the far room is cut off");
            Assert.That(field.IsBuilt, Is.True, "the field of the grid that is gone does not know");
            Assert.That(field.TrySample(far, out _, out _), Is.True);

            field.Dispose();
            stale.Dispose();
            points.Dispose(); before.Dispose(); after.Dispose(); beforeDirections.Dispose(); afterDirections.Dispose();
        }

        [Test]
        public void DisposingTheGridOrTheFieldWaitsForWhatIsStillRunning() {
            var grid = Flooded(out var body);
            var field = grid.ScheduleField(body, ToTheLeftRoom, 0.01f, new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(field.Handle.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => grid.Dispose(), "the grid, with a build that has not run");
            Assert.That(field.Handle.IsCompleted, Is.True);
            // The field outlives the grid, built.
            Assert.That(field.IsBuilt, Is.True);
            Assert.That(field.TrySample(new float3(2, 2, 2), out _, out _), Is.True);
            Assert.DoesNotThrow(() => field.Dispose());
            field.Dispose();
            Assert.That(field.IsDisposed, Is.True);
            Assert.DoesNotThrow(() => field.Complete());
            Assert.That(() => field.IsBuilt, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => field.TrySample(float3.zero, out _, out _), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => field.Reached, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => field.Distance, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => field.ByteCount, Throws.InstanceOf<ObjectDisposedException>());

            // The field first, with a build and a sampling still to run.
            using var second = Flooded(out body);
            var other = second.ScheduleField(body, ToTheLeftRoom, 0.01f, new WaitJob { Milliseconds = 300 }.Schedule());
            var points = new NativeArray<float3>(4, Allocator.TempJob);
            var directions = new NativeArray<float3>(4, Allocator.TempJob);
            var distances = new NativeArray<float>(4, Allocator.TempJob);
            var sampled = other.ScheduleSample(points, directions, distances);
            Assert.That(sampled.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => other.Dispose());
            Assert.That(sampled.IsCompleted, Is.True);
            Assert.That(() => other.ScheduleSample(points, directions, distances), Throws.InstanceOf<ObjectDisposedException>());
            points.Dispose(); directions.Dispose(); distances.Dispose();
            Assert.That(() => second.ScheduleField(body, ToTheLeftRoom).Dispose(), Throws.Nothing);
            second.Dispose();
            Assert.That(() => second.ScheduleField(body, ToTheLeftRoom), Throws.InstanceOf<ObjectDisposedException>());
        }
    }
}
