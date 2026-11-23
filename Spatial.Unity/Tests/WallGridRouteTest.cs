using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial.Tests {
    public class WallGridRouteTest {
        // 16 cells of half a metre each way from the origin, and the same with half the height.
        private static readonly GridShape Grid = new GridShape(0, 0, 0, 0.5f, 16, 16, 16);
        private static readonly GridShape Low = new GridShape(0, 0, 0, 0.5f, 16, 16, 8);

        // A floor, and a wall 0.4 thick between two rooms with an opening 2 m wide and 3 m high.
        private static readonly Solid[] TwoRooms = {
            Solid.Box(new Box(-1, -1, -1, 9, 9, 0.2f)),
            Solid.Box(new Box(3.8f, -1, 0, 4.2f, 3, 9)),
            Solid.Box(new Box(3.8f, 5, 0, 4.2f, 9, 9)),
            Solid.Box(new Box(3.8f, 3, 3, 4.2f, 5, 9)),
        };

        private static readonly Solid[] Everywhere = { Solid.Box(new Box(-1, -1, -1, 9, 9, 9)) };

        // The opening's cells, to close.
        private static readonly Solid Opening = Solid.Box(new Box(3.6f, 2.5f, 0, 4.4f, 5.5f, 4));

        // From the left-hand room to the right-hand one: the wall is in the way.
        private static readonly float3 Left = new float3(1.25f, 1.25f, 1.25f), Right = new float3(6.75f, 1.25f, 1.25f);

        [OneTimeSetUp]
        public void CompileFirstAndLookForLeaks() {
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
        }

        // A grid marked and flooded for one size of body.
        private static WallGrid Flooded(out int body, bool low = false) {
            var grid = new WallGrid(low ? Low : Grid);
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleMark();
            body = grid.AddSizeClass(0.51f);
            grid.ScheduleRegions(body, Everywhere);
            return grid;
        }

        // The same search by the core on this thread, with no job and no Burst.
        private static RouteResult ByTheCore(WallGrid grid, int body, float3 from, float3 to, bool straighten, float leeway, out float3[] points) {
            var shape = grid.Shape;
            Solid[] solids = grid.Solids.ToArray();
            float[] corners = grid.Corners.ToArray();
            int[] triangles = grid.Triangles.ToArray();
            var count = solids.Length + triangles.Length / 3;
            var boxes = new Box[count];
            var nodes = new BoxNode[Shapes.NodesLength(count)];
            var order = new int[count];
            Assert.That(Shapes.Build(solids, corners, triangles, boxes, nodes, order, new int[Shapes.WorkspaceLength(count)], out var nodeCount), Is.True);
            var set = new ShapeSet(solids, corners, triangles, boxes, nodes.AsSpan(0, nodeCount), order);
            var floats = new float[3 * 128];
            byte[] regions = grid.RegionsOf(body).ToArray(), cost = grid.Cost.ToArray();
            var result = Routes.Find(new WallMap(shape, grid.Unit, grid.Cap, grid.Wall.ToArray()), grid.SizeOf(body), set,
                regions, cost, from.x, from.y, from.z, to.x, to.y, to.z, straighten,
                new float[Routes.SumsLength(shape)], new int[Routes.LinksLength(shape)], new int[Routes.HeapLength(shape)], floats,
                Regions.LeastCost(shape, regions, cost), leeway);
            points = new float3[result.Status == RouteStatus.Found ? result.Count : 0];
            for (var i = 0; i < points.Length; i++) { points[i] = new float3(floats[3 * i], floats[3 * i + 1], floats[3 * i + 2]); }
            return result;
        }

        private static void IsWhatTheCoreGives(WallGridRoute route, WallGrid grid, int body, float3 from, float3 to, bool straighten = true) {
            var expected = ByTheCore(grid, body, from, to, straighten, route.Leeway, out var points);
            var why = $"from {from} to {to}, straighten {straighten}, leeway {route.Leeway}";
            Assert.That(route.Status, Is.EqualTo(expected.Status), why);
            Assert.That(route.Ends, Is.EqualTo(expected.Ends), why);
            Assert.That(route.Count, Is.EqualTo(expected.Count), why);
            Assert.That(route.Expanded, Is.EqualTo(expected.Expanded), why);
            Assert.That(route.Cost, Is.EqualTo(expected.Cost), why);
            Assert.That(route.Points.ToArray(), Is.EqualTo(points), why);
        }

        [Test]
        public void TheJobGivesWhatTheCoreGives() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            (float3 from, float3 to)[] pairs = {
                (Left, Right), (Right, Left),
                (new float3(1.3f, 1.1f, 1.2f), new float3(2.6f, 6.9f, 5.1f)),   // one straight leg
                (new float3(3.4f, 6.3f, 1.3f), new float3(4.6f, 1.3f, 1.3f)),   // both ends inside the body's clearance of the wall
                (new float3(0.6f, 4.1f, 0.9f), new float3(6.6f, 6.4f, 0.2f)),   // a goal lying on the floor
                (Left, new float3(3.285f, 6.2f, 2.6f)),                         // a goal 0.515 m from the wall
                (new float3(2.25f, 4f, 1.25f), new float3(5.75f, 4f, 1.25f)),   // straight through the opening
                (Left, Left),
            };
            var searched = 0;
            foreach (var (from, to) in pairs) {
                foreach (var straighten in new[] { true, false }) {
                    route.Schedule(grid, body, from, to, straighten);
                    IsWhatTheCoreGives(route, grid, body, from, to, straighten);
                    Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
                    Assert.That(route.Count, Is.GreaterThan(0));
                    searched += route.Expanded > 0 ? 1 : 0;
                }
            }
            Assert.That(searched, Is.GreaterThan(6));
        }

        [Test]
        public void ARouteLeadsThroughTheOpeningAndEndsAtTheGoal() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            // Before any search there is nothing.
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            Assert.That(route.Count, Is.EqualTo(0));
            Assert.That(route.Points.Length, Is.EqualTo(0));
            Assert.That(route.IsCurrent, Is.False);

            var handle = route.Schedule(grid, body, Left, Right);
            Assert.That(route.Handle, Is.EqualTo(handle));
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(handle.IsCompleted, Is.True, "reading the answer waits for the search");
            Assert.That(route.Ends, Is.EqualTo(RouteEnds.None));
            Assert.That(route.Expanded, Is.GreaterThan(0));
            Assert.That(route.Cost, Is.GreaterThan(6.5f), "more than the 5.5 m of the straight line, which the wall is across");
            var points = route.Points;
            Assert.That(points.Length, Is.EqualTo(route.Count));
            Assert.That(points.Length, Is.InRange(2, 6));
            Assert.That(points[points.Length - 1], Is.EqualTo(Right));
            Assert.That(route.IsCurrent, Is.True);
            // Every leg keeps the body's radius from every shape, by the grid's own exact distance.
            var at = Left;
            for (var i = 0; i < points.Length; i++) {
                for (var k = 0; k <= 200; k++) {
                    Assert.That(grid.TryDistance(math.lerp(at, points[i], k / 200f), 5f, out var distance), Is.True);
                    Assert.That(distance, Is.GreaterThanOrEqualTo(0.51f - 1e-4f), $"on leg {i}");
                }
                at = points[i];
            }
            // A goal 0.515 m from the wall is reached: the shapes prove what the wall distance cannot.
            var near = new float3(3.285f, 6.2f, 2.6f);
            route.Schedule(grid, body, Left, near);
            Assert.That(route.Ends, Is.EqualTo(RouteEnds.None));
            Assert.That(route.Points[route.Count - 1], Is.EqualTo(near));
            // One 0.3 m from it is not: the route stops at an open centre beside it, and says so.
            route.Schedule(grid, body, Left, new float3(3.5f, 6.2f, 2.6f));
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Ends, Is.EqualTo(RouteEnds.StopsShortOfGoal));
            // With every cell kept there are more points, and the same search under them.
            route.Schedule(grid, body, Left, Right);
            var cost = route.Cost;
            var count = route.Count;
            route.Schedule(grid, body, Left, Right, straighten: false);
            Assert.That(route.Count, Is.GreaterThan(count + 5));
            Assert.That(route.Cost, Is.EqualTo(cost));
        }

        [Test]
        public void ARouteGoesRoundAnAreaThatCostsMoreWhereItsWayDid() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            // Down the left-hand room, with nothing between the two points: one straight leg.
            float3 from = new float3(1.75f, 0.75f, 2.25f), to = new float3(1.75f, 7.25f, 2.25f);
            route.Schedule(grid, body, from, to);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Count, Is.EqualTo(1));
            Assert.That(route.Expanded, Is.EqualTo(0));

            // An area across that leg that costs three times as much, with room to go round it.
            Assert.That(grid.FillCost(Solid.Box(new Box(1, 3.5f, 0, 2.5f, 4.5f, 9)), 32), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, from, to);
            IsWhatTheCoreGives(route, grid, body, from, to);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Count, Is.InRange(2, 4));
            // No leg passes through the area, looked at every centimetre. Two looks in a row must
            // find it, as a leg may pass through the very corner of a cell.
            var at = from;
            foreach (var point in route.Points.ToArray()) {
                var length = math.distance(at, point);
                byte last = 0;
                for (var along = 0f; along <= length; along += 0.01f) {
                    Assert.That(grid.TryCost(math.lerp(at, point, along / length), out var cost), Is.True);
                    Assert.That(Math.Min(cost, last), Is.EqualTo(0), $"on the leg from {at} to {point}");
                    last = cost;
                }
                at = point;
            }
            Assert.That(math.all(at == to), Is.True);

            // To a goal in the area, one leg again: it costs no more than the way straight to it
            // does. The way is searched for first, since only it says what the leg may cost.
            var inside = new float3(1.75f, 4.25f, 2.25f);
            route.Schedule(grid, body, from, inside);
            IsWhatTheCoreGives(route, grid, body, from, inside);
            Assert.That(route.Count, Is.EqualTo(1));
            Assert.That(route.Expanded, Is.GreaterThan(0));

            // With the costs cleared, the straight leg again.
            grid.ClearCost();
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, from, to);
            Assert.That(route.Count, Is.EqualTo(1));
        }

        [Test]
        public void ARouteIsCurrentByTheGridItWasLastSearchedOn() {
            using var first = Flooded(out var body);
            using var second = Flooded(out var other);
            using var route = new WallGridRoute();
            // The first grid is flooded twice more, so that the two grids' regions are not stamped alike.
            first.ScheduleRegions(body, Everywhere);
            first.ScheduleRegions(body, Everywhere);
            route.Schedule(first, body, Left, Right);
            Assert.That(route.IsCurrent, Is.True);
            route.Schedule(second, other, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.IsCurrent, Is.True);
            // A change to the first grid is nothing to a route that was last searched on the second.
            Assert.That(first.FillCost(Opening, CellCost.Closed), Is.True);
            first.ScheduleRegions(body, Everywhere);
            Assert.That(route.IsCurrent, Is.True);
            Assert.That(second.FillCost(Opening, 32), Is.True);
            Assert.That(route.IsCurrent, Is.False);
        }

        [Test]
        public void WhereEveryCellCostsTheSameAStraightLegIsTakenWithNothingSearched() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            float3 from = new float3(1.3f, 1.1f, 1.2f), to = new float3(2.6f, 6.9f, 5.1f);
            // Every cell costs three times. The flood finds the least cost among the cells it
            // reaches, and a search is told it: no way can cost less than the straight leg does.
            Assert.That(grid.FillCost(Everywhere[0], 32), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, from, to);
            IsWhatTheCoreGives(route, grid, body, from, to);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Count, Is.EqualTo(1));
            Assert.That(route.Expanded, Is.EqualTo(0));
            // Through the opening there is a search, and it takes up as few cells as with no costs.
            route.Schedule(grid, body, Left, Right);
            IsWhatTheCoreGives(route, grid, body, Left, Right);
            var costly = route.Expanded;
            var cost = route.Cost;
            // One corner of the room at the normal cost: the least is 0, and the leg waits for a search.
            Assert.That(grid.FillCost(Solid.Box(new Box(6, 6, 6, 8, 8, 8)), 0), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, from, to);
            IsWhatTheCoreGives(route, grid, body, from, to);
            Assert.That(route.Count, Is.EqualTo(1));
            Assert.That(route.Expanded, Is.GreaterThan(0));
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Expanded, Is.GreaterThan(2 * costly));
            Assert.That(route.Cost, Is.EqualTo(cost).Within(1e-3f));
            // With the costs cleared the least is 0 again, and nothing is searched for the straight leg.
            grid.ClearCost();
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, from, to);
            Assert.That(route.Expanded, Is.EqualTo(0));
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Expanded, Is.InRange(costly * 4 / 5, costly * 6 / 5));
        }

        [Test]
        public void ALeewayIsHowMuchMoreThanTheCheapestAWayMayCost() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            Assert.That(route.Leeway, Is.EqualTo(WallGridRoute.DefaultLeeway));
            Assert.That(WallGridRoute.DefaultLeeway, Is.EqualTo(0.1f));
            // With none, the cheapest way through the opening.
            route.Leeway = 0f;
            route.Schedule(grid, body, Left, Right);
            IsWhatTheCoreGives(route, grid, body, Left, Right);
            var cheapest = route.Cost;
            var taken = route.Expanded;
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            // With a tenth, and with a half: no dearer than that, by fewer cells, and what the core gives.
            foreach (var leeway in new[] { WallGridRoute.DefaultLeeway, 0.5f }) {
                route.Leeway = leeway;
                route.Schedule(grid, body, Left, Right);
                IsWhatTheCoreGives(route, grid, body, Left, Right);
                Assert.That(route.Cost, Is.InRange(cheapest * (1 - 1e-5f), cheapest * (1 + leeway) * (1 + 1e-5f)));
                Assert.That(route.Expanded, Is.LessThan(taken));
                Assert.That(route.Leeway, Is.EqualTo(leeway), "it is kept from one search to the next");
            }
            // What is not a share is not taken, and the leeway stays as it was.
            Assert.That(() => route.Leeway = -0.1f, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => route.Leeway = float.NaN, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => route.Leeway = float.PositiveInfinity, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(route.Leeway, Is.EqualTo(0.5f));
        }

        [Test]
        public void ASearchIsOnlyAsGoodAsTheRegionsItRunsThrough() {
            using var grid = new WallGrid(Grid);
            using var route = new WallGridRoute();
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            grid.ScheduleMark();
            var body = grid.AddSizeClass(0.51f);
            // Not flooded yet.
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            Assert.That(route.Points.Length, Is.EqualTo(0));
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.IsCurrent, Is.True);

            // The opening is shut. The route found before is kept, and is no longer current.
            Assert.That(grid.FillCost(Opening, CellCost.Closed), Is.True);
            Assert.That(route.IsCurrent, Is.False);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Points.Length, Is.GreaterThan(1));
            // A new search has no regions to run through until they are flooded again.
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            grid.ScheduleRegions(body, Everywhere);
            Assert.That(route.IsCurrent, Is.False, "flooding again does not make an old search good");
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Unreachable));
            Assert.That(route.Expanded, Is.EqualTo(0), "the regions say so before anything is searched");
            Assert.That(route.Points.Length, Is.EqualTo(0));
            Assert.That(route.IsCurrent, Is.True);
            // Within one room there is still a way.
            route.Schedule(grid, body, Left, new float3(1.25f, 6.75f, 5.25f));
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));

            // Opened again.
            grid.ClearCost();
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, Left, Right);
            IsWhatTheCoreGives(route, grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));

            // New shapes: no regions, and after marking and flooding, a route again.
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(route.IsCurrent, Is.False);
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            grid.ScheduleMark();
            grid.ScheduleRegions(body, Everywhere);
            route.Schedule(grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));

            // A point that is not a number.
            route.Schedule(grid, body, new float3(float.NaN, 1, 1), Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            Assert.That(route.Count, Is.EqualTo(0));
        }

        [Test]
        public void OneRouteServesGridsOfDifferentSizesInTurn() {
            using var high = Flooded(out var body);
            using var low = Flooded(out var lowBody, low: true);
            using var route = new WallGridRoute(32);
            var bare = route.ByteCount;
            Assert.That(bare, Is.EqualTo(32 * 12 + 20));
            for (var round = 0; round < 3; round++) {
                route.Schedule(low, lowBody, Left, Right);
                IsWhatTheCoreGives(route, low, lowBody, Left, Right);
                Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
                // It holds 16 bytes a cell of the largest grid it has searched, and does not shrink.
                Assert.That(route.ByteCount, Is.EqualTo(bare + 16L * (round == 0 ? Low.CellCount : Grid.CellCount)));
                route.Schedule(high, body, Right, Left);
                IsWhatTheCoreGives(route, high, body, Right, Left);
                Assert.That(route.ByteCount, Is.EqualTo(bare + 16L * Grid.CellCount));
                // A search with every cell kept leaves a great many cells to let go.
                route.Schedule(high, body, Left, new float3(6.75f, 1.25f, 7.25f), straighten: false);
                IsWhatTheCoreGives(route, high, body, Left, new float3(6.75f, 1.25f, 7.25f), straighten: false);
            }
        }

        // A job that takes a while. Not for Burst.
        private struct WaitJob : IJob {
            public int Milliseconds;
            public void Execute() => System.Threading.Thread.Sleep(Milliseconds);
        }

        [Test]
        public void ASearchScheduledWhileTheLastRunsRunsAfterItAndReplacesItsAnswer() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            // The first cannot start for 300 ms.
            var first = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(first.IsCompleted, Is.False);
            JobHandle second = default;
            var goal = new float3(1.25f, 6.75f, 5.25f);
            Assert.DoesNotThrow(() => second = route.Schedule(grid, body, Left, goal));
            Assert.That(second.IsCompleted, Is.False, "it waits for the first, which uses the same arrays");
            IsWhatTheCoreGives(route, grid, body, Left, goal);
            Assert.That(first.IsCompleted, Is.True);
            Assert.That(route.Count, Is.EqualTo(1));
        }

        [Test]
        public void ASearchRunsAfterAMarkingScheduledSinceTheFlood() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            // A flood, then a marking of the same shapes, then the search, with nothing waited for.
            // The search reads the wall distance that the marking writes: it must run after it.
            grid.ScheduleRegions(body, Everywhere);
            grid.ScheduleMark();
            Assert.DoesNotThrow(() => route.Schedule(grid, body, Left, Right));
            IsWhatTheCoreGives(route, grid, body, Left, Right);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
        }

        [Test]
        public void ALargerGridWhileASearchStillRunsWaitsForItBeforeTheArraysGrow() {
            using var high = Flooded(out var body);
            using var low = Flooded(out var lowBody, low: true);
            using var route = new WallGridRoute();
            var first = route.Schedule(low, lowBody, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(first.IsCompleted, Is.False);
            // The larger grid needs longer arrays; the ones in use are not freed under the search.
            Assert.DoesNotThrow(() => route.Schedule(high, body, Right, Left));
            Assert.That(first.IsCompleted, Is.True);
            IsWhatTheCoreGives(route, high, body, Right, Left);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
        }

        [Test]
        public void WhatWritesTheGridWaitsForASearchThatHasNotRunYet() {
            using var grid = Flooded(out var body);
            using var route = new WallGridRoute();
            var search = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(search.IsCompleted, Is.False);
            // A change of cost writes what the search reads: it waits, and the route is of the grid before it.
            Assert.That(grid.FillCost(Opening, CellCost.Closed), Is.True);
            Assert.That(search.IsCompleted, Is.True);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found), "searched before the opening was shut");
            Assert.That(route.IsCurrent, Is.False, "and no longer current");

            // The same for new shapes and for a marking.
            grid.ClearCost();
            grid.ScheduleRegions(body, Everywhere);
            search = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(grid.SetShapes(TwoRooms, Array.Empty<float>(), Array.Empty<int>()), Is.True);
            Assert.That(search.IsCompleted, Is.True);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            grid.ScheduleMark();
            grid.ScheduleRegions(body, Everywhere);
            search = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            var mark = grid.ScheduleMark();
            mark.Complete();
            Assert.That(search.IsCompleted, Is.True, "the marking ran after the search that reads the wall distance");
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));

            // And for clearing the costs, which frees the bytes that a search reads.
            Assert.That(grid.FillCost(Solid.Box(new Box(1, 5, 0, 2, 6, 2)), 32), Is.True);
            grid.ScheduleRegions(body, Everywhere);
            search = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.That(search.IsCompleted, Is.False);
            Assert.DoesNotThrow(() => grid.ClearCost());
            Assert.That(search.IsCompleted, Is.True);
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.IsCurrent, Is.False);
        }

        [Test]
        public void DisposingTheGridOrTheRouteWaitsForASearchThatIsStillRunning() {
            var grid = Flooded(out var body);
            var route = new WallGridRoute();
            var search = route.Schedule(grid, body, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.DoesNotThrow(() => grid.Dispose(), "the grid, with a search that has not run");
            Assert.That(search.IsCompleted, Is.True);
            // The route is kept: it is of the grid as it was.
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Points.Length, Is.GreaterThan(1));
            Assert.That(() => route.Schedule(grid, body, Left, Right), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found), "a search that could not be scheduled changes nothing");

            using var other = Flooded(out var again);
            search = route.Schedule(other, again, Left, Right, dependsOn: new WaitJob { Milliseconds = 300 }.Schedule());
            Assert.DoesNotThrow(() => route.Dispose(), "the route, with a search that has not run");
            Assert.That(search.IsCompleted, Is.True);
            route.Dispose();
            Assert.That(route.IsDisposed, Is.True);
            Assert.DoesNotThrow(() => route.Complete());
            Assert.That(() => route.Status, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Ends, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Count, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Expanded, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Cost, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Points, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.IsCurrent, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.ByteCount, Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => route.Schedule(other, again, Left, Right), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void ARouteWithMorePointsThanItHoldsSaysHowManyThereAre() {
            using var grid = Flooded(out var body);
            using var whole = new WallGridRoute();
            whole.Schedule(grid, body, Left, Right, straighten: false);
            Assert.That(whole.Count, Is.GreaterThan(10));
            using var small = new WallGridRoute(10);
            small.Schedule(grid, body, Left, Right, straighten: false);
            Assert.That(small.Status, Is.EqualTo(RouteStatus.BufferTooShort));
            Assert.That(small.Count, Is.EqualTo(whole.Count));
            Assert.That(small.Points.Length, Is.EqualTo(0), "no part of a route is handed out as if it were one");
            // One that fits.
            small.Schedule(grid, body, Left, Right);
            Assert.That(small.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(small.Points.Length, Is.EqualTo(small.Count));
        }

        [Test]
        public void WhatCannotBeUsedIsRefused() {
            using var grid = Flooded(out var body);
            Assert.That(() => new WallGridRoute(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new WallGridRoute(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            using var route = new WallGridRoute(1);
            Assert.That(() => route.Schedule(null, body, Left, Right), Throws.ArgumentNullException);
            Assert.That(() => route.Schedule(grid, body + 1, Left, Right), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => route.Schedule(grid, -1, Left, Right), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Refused));
            // One point is enough for a straight leg.
            route.Schedule(grid, body, Left, new float3(1.25f, 6.75f, 5.25f));
            Assert.That(route.Status, Is.EqualTo(RouteStatus.Found));
            Assert.That(route.Points.Length, Is.EqualTo(1));
        }
    }
}
