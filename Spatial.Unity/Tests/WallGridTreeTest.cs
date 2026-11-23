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

        // A point above the floor in the open.
        private static readonly Solid[] Seeds = { Solid.Sphere(2, 2, 3, 0.5f) };

        [Test]
        public void ATreeKeepsNoCostsAndNoFields() {
            using var tree = Marked(true);
            Assert.That(tree.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 2)), 32), Is.False);
            Assert.That(tree.TryCost(new float3(1, 1, 1), out var cost), Is.True);
            Assert.That(cost, Is.EqualTo(0));
            Assert.That(tree.AddSizeClass(0.51f), Is.EqualTo(0));
            // A field is refused as one over a class with no regions is: not built, and with no array a cell.
            tree.ScheduleRegions(0, Seeds);
            using var field = tree.ScheduleField(0, new[] { TravelSeed.FromPoint(Solid.Sphere(2, 2, 3, 0.5f), 2, 2, 3) });
            Assert.That(field.IsBuilt, Is.False);
            Assert.That(field.Reached, Is.EqualTo(0));
            Assert.That(field.ByteCount, Is.LessThan(100));
            Assert.That(field.TrySample(new float3(2, 2, 3), out _, out _), Is.False);
            Assert.That(() => tree.ScheduleField(0, Array.Empty<TravelSeed>(), unit: 0f), Throws.InstanceOf<ArgumentOutOfRangeException>());
            using var dense = Marked(false);
            Assert.That(dense.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 2)), 32), Is.True);
        }

        [Test]
        public void RegionsOverATreeAreThoseOverCountsPutDownAsTheTreeKeepsThem() {
            using var tree = Marked(true);
            using var dense = Marked(false);
            var kept = PutDown(dense);
            Assert.That(tree.AddSizeClass(0.25f), Is.EqualTo(-1), "half a cell or less");
            Assert.That(tree.AddSizeClass(0.51f), Is.EqualTo(0));
            Assert.That(dense.AddSizeClass(0.51f), Is.EqualTo(0));
            Assert.That(tree.SizeOf(0), Is.EqualTo(dense.SizeOf(0)));
            Assert.That(tree.HasRegions(0), Is.False);
            Assert.That(tree.IsOpen(0, new float3(2, 2, 3)), Is.False, "not flooded yet");
            var bare = tree.ByteCount;

            tree.ScheduleRegions(0, Seeds);
            dense.ScheduleRegions(0, Seeds);
            // The dense flood over the counts as the tree keeps them.
            var region = new byte[Grid.CellCount];
            Assert.That(Regions.FromSeeds(new WallMap(Grid, dense.Unit, dense.Cap, kept), dense.SizeOf(0), default, Seeds, region, new int[Regions.QueueLength(Grid)], out var reached, out _), Is.True);
            Assert.That(tree.HasRegions(0), Is.True);
            Assert.That(tree.Reached(0), Is.EqualTo(reached));
            Assert.That(tree.Reached(0), Is.GreaterThan(3000));
            Assert.That(tree.Reached(0), Is.LessThanOrEqualTo(dense.Reached(0)), "never more than over the counts as marked");
            Assert.That(tree.EmptySeeds(0), Is.EqualTo(0));
            int open = 0, near = 0;
            foreach (var point in Points()) {
                var isOpen = Regions.IsOpen(Grid, region, point.x, point.y, point.z);
                Assert.That(tree.IsOpen(0, point), Is.EqualTo(isOpen));
                if (isOpen) { open++; }
                var found = Regions.NearestOpen(Grid, region, point.x, point.y, point.z, 1.2f, out var cx, out var cy, out var cz);
                Assert.That(tree.TryNearestOpen(0, point, 1.2f, out var same), Is.EqualTo(found));
                Assert.That(same, Is.EqualTo(new float3(cx, cy, cz)));
                if (found && !isOpen) { near++; }
            }
            Assert.That(open, Is.GreaterThan(5000));
            Assert.That(near, Is.GreaterThan(1000));

            // What it keeps: a byte a brick and a byte an open cube, and a few bricks in full; no byte a cell.
            Assert.That(tree.ByteCount - bare, Is.InRange(100, Grid.CellCount / 4));
            Assert.That(tree.RegionsOf(0).Length, Is.EqualTo(0));
            Assert.That(dense.RegionsOf(0).Length, Is.EqualTo(Grid.CellCount));
            Assert.That(tree.TryNearestOpen(0, new float3(2, 2, 3), 2f, new float3(3, 3, 3), out _), Is.False, "not asked of a tree");

            // A seed with no open cell, and one in the same region as the first.
            tree.ScheduleRegions(0, new[] { Seeds[0], Solid.Sphere(8.5f, 2.5f, 4f, 0.5f), Solid.Sphere(3, 3, 5, 0.5f) });
            Assert.That(tree.Reached(0), Is.EqualTo(reached));
            Assert.That(tree.EmptySeeds(0), Is.EqualTo(1));

            // New shapes: the regions say nothing of them.
            Assert.That(tree.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(tree.HasRegions(0), Is.False);
            Assert.That(tree.IsOpen(0, new float3(2, 2, 3)), Is.False);
            Assert.That(tree.ByteCount, Is.LessThan(bare), "neither tree nor regions");
            // Marked again and not yet flooded: the tree is back, and nothing of the old regions with it.
            tree.ScheduleMark();
            Assert.That(tree.IsMarked, Is.True);
            Assert.That(tree.HasRegions(0), Is.False);
            Assert.That(tree.IsOpen(0, new float3(2, 2, 3)), Is.False);
            Assert.That(tree.TryNearestOpen(0, new float3(2, 2, 3), 2f, out _), Is.False);
            Assert.That(tree.ByteCount, Is.EqualTo(bare));
            tree.ScheduleRegions(0, Seeds);
            Assert.That(tree.Reached(0), Is.EqualTo(reached));
            // A flood before the marking that new shapes need is refused.
            Assert.That(tree.SetShapes(Solids, Corners, Triangles), Is.True);
            tree.ScheduleRegions(0, Seeds);
            Assert.That(tree.HasRegions(0), Is.False);
            Assert.That(tree.Reached(0), Is.EqualTo(0));
            tree.ScheduleMark();
            tree.ScheduleRegions(0, Seeds);
            Assert.That(tree.Reached(0), Is.EqualTo(reached));
        }

        private static float Length(float3 from, NativeArray<float3>.ReadOnly points) {
            var length = 0f;
            foreach (var point in points) {
                length += math.distance(from, point);
                from = point;
            }
            return length;
        }

        [Test]
        public void ARouteOverATreeIsFoundAsOverCounts() {
            using var tree = Marked(true);
            using var dense = Marked(false);
            tree.AddSizeClass(0.51f);
            dense.AddSizeClass(0.51f);
            tree.ScheduleRegions(0, Seeds);
            dense.ScheduleRegions(0, Seeds);
            using var route = new WallGridRoute(64);
            using var other = new WallGridRoute(64);
            var bare = route.ByteCount;

            // Ends in the open, behind the box and the ball, by the pillar, on the floor and inside the ball.
            float3[] ends = {
                new float3(1.2f, 1.3f, 2.1f), new float3(8.9f, 8.7f, 6.2f), new float3(3.1f, 8.4f, 1.6f), new float3(8.6f, 0.9f, 2.2f),
                new float3(5.6f, 5.4f, 6.9f), new float3(0.4f, 9.2f, 7.3f), new float3(6.1f, 2.2f, 0.45f), new float3(8.5f, 2.5f, 4f),
            };
            int found = 0, searched = 0;
            foreach (var from in ends) {
                foreach (var to in ends) {
                    if (from.Equals(to)) { continue; }
                    route.Schedule(tree, 0, from, to);
                    other.Schedule(dense, 0, from, to);
                    Assert.That(route.Status, Is.EqualTo(other.Status), $"from {from} to {to}");
                    Assert.That(route.Ends, Is.EqualTo(other.Ends));
                    if (route.Status != RouteStatus.Found) { continue; }
                    found++;
                    if (route.Expanded > 0) { searched++; }
                    Assert.That(route.Expanded == 0, Is.EqualTo(other.Expanded == 0), "one straight leg over the tree where it is one over counts");
                    Assert.That(route.Points[route.Count - 1], Is.EqualTo(other.Points[other.Count - 1]));
                    Assert.That(Length(from, route.Points), Is.LessThan(Length(from, other.Points) * 1.3f + 0.01f));
                    // Every leg but an unproven first one keeps the class's guarantee, by the shapes themselves.
                    var at = from;
                    for (var i = 0; i < route.Count; i++) {
                        var point = route.Points[i];
                        if (i > 0 || (route.Ends & RouteEnds.FirstLegUnproven) == 0) {
                            var steps = math.max(1, (int)math.ceil(math.distance(at, point) / 0.02f));
                            for (var k = 0; k <= steps; k++) {
                                Assert.That(tree.TryDistance(math.lerp(at, point, (float)k / steps), 2f, out var distance), Is.True);
                                Assert.That(distance, Is.GreaterThanOrEqualTo(tree.SizeOf(0).Guarantee - 1e-3f), $"on leg {i} from {from} to {to}");
                            }
                        }
                        at = point;
                    }
                }
            }
            Assert.That(found, Is.GreaterThan(30));
            Assert.That(searched, Is.GreaterThan(8));
            route.Schedule(tree, 0, ends[0], ends[7]);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Unreachable), "the middle of the ball");
            Assert.That(route.IsCurrent, Is.True);
            Assert.That(route.ByteCount - bare, Is.EqualTo(4L * (TreeRoutes.LinksLength(WallGridRoute.DefaultTreePlaces) + TreeRoutes.SumsLength(WallGridRoute.DefaultTreePlaces))));
            Assert.That(route.ByteCount - bare, Is.EqualTo(28L * 32768), "sized to the search and not to the grid: under a megabyte");

            // A search scheduled while the last has not been read runs after it, with the same arrays.
            other.Schedule(tree, 0, ends[2], ends[5]);
            var alone = (other.Status, other.Count, other.Expanded, other.Cost);
            route.Schedule(tree, 0, ends[0], ends[1]);
            route.Schedule(tree, 0, ends[2], ends[5]);
            Assert.That((route.Status, route.Count, route.Expanded, route.Cost), Is.EqualTo(alone));
            Assert.That(route.Points.ToArray(), Is.EqualTo(other.Points.ToArray()));

            // With room for a dozen places a search that needs more says so, and with room again it finds the way.
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Expanded, Is.GreaterThan(12));
            // Over a tree the leeway is a quarter at the least: as it starts, a tenth, the search is the one a quarter gives.
            var asItStarts = route.Expanded;
            route.Leeway = 0f;
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Expanded, Is.EqualTo(asItStarts));
            route.Leeway = WallGridRoute.TreeLeeway;
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Expanded, Is.EqualTo(asItStarts));
            route.Leeway = 3f;
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Expanded, Is.LessThan(asItStarts), "with more leeway it takes up fewer places");
            route.Leeway = WallGridRoute.DefaultLeeway;
            route.TreePlaces = 12;
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.SearchFull));
            Assert.That(route.Points.Length, Is.EqualTo(0));
            route.TreePlaces = WallGridRoute.DefaultTreePlaces;
            route.Schedule(tree, 0, ends[0], ends[1], straighten: false);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Points[0], Is.EqualTo(new float3(1.25f, 1.25f, 2.25f)), "left as cells, the way begins at the centre of the start's cell");
            Assert.That(() => route.TreePlaces = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => route.TreePlaces = int.MaxValue, Throws.InstanceOf<ArgumentOutOfRangeException>());

            // The same object goes on serving a grid of counts, whose search needs its own arrays as it left them.
            for (var round = 0; round < 2; round++) {
                route.Schedule(dense, 0, ends[0], ends[1]);
                other.Schedule(dense, 0, ends[0], ends[1]);
                Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
                Assert.That(route.Points.ToArray(), Is.EqualTo(other.Points.ToArray()));
                route.Schedule(tree, 0, ends[0], ends[1]);
                Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            }

            // Before any flood, and once the shapes are taken again, a search is refused.
            Assert.That(tree.SetShapes(Solids, Corners, Triangles), Is.True);
            Assert.That(route.IsCurrent, Is.False);
            route.Schedule(tree, 0, ends[0], ends[1]);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
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
