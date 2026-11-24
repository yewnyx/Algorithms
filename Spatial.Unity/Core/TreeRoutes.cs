using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// The way for a body of one size from one point of a <see cref="WallTree"/>'s grid to
    /// another: what <see cref="Routes"/> finds over a dense map, for a grid too large to search
    /// cell by cell.
    /// </summary>
    /// <remarks>
    /// The points and the legs between them are of the kinds <see cref="Routes"/> gives, and are
    /// proven the same way: a straight leg is clear along its whole length for the class's full
    /// <see cref="SizeClass.Radius"/>, a step between the centres of two open cells that touch
    /// keeps the class's <see cref="SizeClass.Guarantee"/>, and the two ends are the exception,
    /// as <see cref="RouteEnds"/> says. A tree keeps no costs, so no leg is held to any.
    /// The way is found among places, not cells. In open air a place is an open cube whole: the
    /// way comes into it at one cell and cuts straight across to the cell it leaves by, which
    /// every cell of the cube being open proves clear. Near shapes a place is the open cells of
    /// one brick that are joined inside it, come into through one face: the way goes through it
    /// by the dense search's own steps from open cell to open cell, and what that costs is
    /// worked out and not guessed. So a search takes up a few hundred places where the dense
    /// search takes up many thousands of cells, and its arrays are sized to the search and not
    /// to the grid.
    /// What that gives up: the way is not the cheapest there is. The first way found into a
    /// place is the one kept, a way leaves a place only across a face, and the search heads for
    /// the goal harder than a cheapest way allows (see the leeway of <see cref="Find"/>). Pulled
    /// straight, the route is as a rule the length of the dense search's: among shapes set
    /// close together, the worst seen is a quarter longer. And a search can fill its arrays
    /// before it finds a way, which it says with <see cref="RouteStatus.SearchFull"/>.
    /// Nothing is allocated and nothing throws.
    /// </remarks>
    public static class TreeRoutes {
        // A slot of the search's table is five ints and two floats: the place, the slot it was
        // reached from, the cell it was come into at, where it waits in the heap, a place of the
        // heap; what the way to it costs, and what it is ranked by.
        private const int LeastSlots = 16, MostSlots = 1 << 26;
        private const uint Scatter = 2654435769u;
        // The numbers a brick has for its places: 64 for joined cells by their lowest cell, and
        // 96 for cells come into at a cell of a face, by the face and the cell.
        private const int PerBrick = 256, ByFace = 64;

        /// <summary>
        /// The length of the links <see cref="Find"/> needs for a search that may hold so many
        /// places, taken up or waiting: five ints a slot, for a number of slots that is a power of
        /// two and a third more than the places. 0 if that is below 1 or too many.
        /// </summary>
        public static int LinksLength(int places) => 5 * _slotsFor(places);

        /// <summary>The length of the sums <see cref="Find"/> needs for a search that may hold so many places: two floats a slot.</summary>
        public static int SumsLength(int places) => 2 * _slotsFor(places);

        private static int _slotsFor(int places) {
            if (places < 1) { return 0; }
            var needed = ((long)places * 4 + 2) / 3;
            long slots = LeastSlots;
            while (slots < needed) { slots <<= 1; }
            return slots <= MostSlots ? (int)slots : 0;
        }

        // How many slots two arrays hold: the largest power of two that both have room for.
        private static int _slotsOf(int links, int sums) {
            var most = links / 5 < sums / 2 ? links / 5 : sums / 2;
            if (most < LeastSlots) { return 0; }
            var slots = LeastSlots;
            while (slots < MostSlots && slots * 2 <= most) { slots <<= 1; }
            return slots;
        }

        /// <summary>
        /// Finds the way from one point to another for a size class, and writes its points, three
        /// floats each, the goal last.
        /// </summary>
        /// <remarks>
        /// In order, as <see cref="Routes.Find"/>: which region each end is in; the straight leg
        /// from start to goal, taken at once if it is clear; a search over the places of the
        /// tree; pulling the way straight, which tries one cell of the way in four and so now and
        /// then leaves a point that the dense search would not. With <paramref name="straighten"/> false the last and
        /// the straight leg are left out, and the points are centres of cells of the way: every
        /// cell it steps through in a brick, and of an open cube the cell it comes in at and the
        /// cell it leaves by.
        /// <paramref name="region"/> is what <see cref="TreeRegions.FromSeeds"/> made for this
        /// tree and this class. An end in an open cell is in that cell's region. Any other end
        /// takes the nearest open centre it can see, as over a dense map. Ends in two regions, or
        /// an end with no such centre, are <see cref="RouteStatus.Unreachable"/>, and nothing is
        /// searched.
        /// <paramref name="shapes"/> may be a set with no shapes: a leg is then proven by the tree
        /// alone, which needs more room than the radius.
        /// <paramref name="sums"/> and <paramref name="links"/> are the search's own, of
        /// <see cref="SumsLength"/> and <see cref="LinksLength"/> for the most places a search may
        /// hold: that is the caller's to choose, and it bounds what a search can cost. They need
        /// not be cleared. A search that would hold more ends with
        /// <see cref="RouteStatus.SearchFull"/>: there may be a way, and it was not found.
        /// <paramref name="leeway"/> is how hard the search heads for the goal: it reckons each
        /// metre still to go at 1 plus the leeway. With 0 it looks at every place a cheaper way
        /// could pass through. With a quarter it takes up far fewer, and the routes come out the
        /// same length on the whole: measured over open ground, a twentieth longer at the worst.
        /// The status is <see cref="RouteStatus.Refused"/> if the tree cannot be used, the class
        /// does not fit it, the region bytes are not a flood's of this tree, an array has room
        /// for fewer than 16 slots, the shapes' spans do not belong together, a point is not a
        /// finite number, or the leeway is below 0 or not finite.
        /// </remarks>
        public static RouteResult Find(in WallTree tree, in SizeClass size, in ShapeSet shapes, ReadOnlySpan<byte> region,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ, bool straighten,
            Span<float> sums, Span<int> links, Span<float> points, float leeway = 0.25f) {
            var count = 0;
            var ends = RouteEnds.None;
            var shape = tree.Shape;
            var slots = _slotsOf(links.Length, sums.Length);
            if (!tree.IsValid || !size.Fits(tree) || slots == 0 || shapes.Count < 0 || !TreeRegions.TryView(tree, region, out var view)) { return default; }
            // Every place has a number: the bricks' first, then the open cubes, then the goal.
            long firstCube = (long)tree.Bricks.Length / WallTrees.BrickBytes * PerBrick, goalPlace = firstCube + WallTrees.OpenCubes(tree);
            if (goalPlace >= int.MaxValue) { return default; }
            var sum = (double)fromX + fromY + fromZ + toX + toY + toZ;
            if (!(sum - sum == 0d)) { return default; }
            if (!(leeway >= 0f) || float.IsInfinity(leeway)) { return default; }

            var reach = Math.Sqrt(3d) * ((double)size.Radius + shape.CellSize);
            if (!_end(tree, shapes, region, view, fromX, fromY, fromZ, reach, out var start, out var startRegion) ||
                !_end(tree, shapes, region, view, toX, toY, toZ, reach, out var goal, out var goalRegion) || startRegion != goalRegion) {
                return new RouteResult(RouteStatus.Unreachable, RouteEnds.None, 0, 0, 0f);
            }
            if (straighten && Legs.Clear(tree, shapes, size.Radius, size.LegNeed, fromX, fromY, fromZ, toX, toY, toZ)) {
                _add(points, ref count, toX, toY, toZ);
                return new RouteResult(_fits(points, count), ends, count, 0, 0f);
            }

            Span<int> block = stackalloc int[8], leaves = stackalloc int[8], sides = stackalloc int[8];
            Span<float> near = stackalloc float[WallTrees.BrickCells];
            Span<byte> back = stackalloc byte[WallTrees.BrickCells];
            block.Fill(-1);
            var shift = 32;
            for (var s = slots; s > 1; s >>= 1) { shift--; }
            int countX = shape.CountX, countY = shape.CountY, layer = countX * countY;
            var search = new Search {
                Tree = tree, Need = size.Cell, Plane = size.Plane, Space = size.Space,
                NeedLevel = WallTrees.LevelFor(size.Cell, tree.Cap), PlaneLevel = WallTrees.LevelFor(size.Plane, tree.Cap), SpaceLevel = WallTrees.LevelFor(size.Space, tree.Cap),
                Keys = links.Slice(0, slots), Parent = links.Slice(slots, slots), Cell = links.Slice(2 * slots, slots),
                HeapAt = links.Slice(3 * slots, slots), Heap = links.Slice(4 * slots, slots),
                Spent = sums.Slice(0, slots), Rank = sums.Slice(slots, slots),
                Block = block, Leaves = leaves, Sides = sides, Near = near, Back = back,
                Slots = slots, Shift = shift, Most = slots / 4 * 3,
                Bricks = tree.Bricks.Length / WallTrees.BrickBytes, FirstCube = (int)firstCube, GoalPlace = (int)goalPlace,
                CountX = countX, CountY = countY, CountZ = shape.CountZ, Layer = layer,
                BlocksX = (countX + 3) >> 2, BlocksY = (countY + 3) >> 2,
                GoalX = goal % countX, GoalY = goal / countX % countY, GoalZ = goal / layer, CellSize = shape.CellSize,
                Ahead = (1d + leeway) * shape.CellSize,
            };
            search.Keys.Clear();
            var goalSlot = search.Run(start);
            var expanded = search.Taken;
            if (goalSlot < 0) { return new RouteResult(search.Full ? RouteStatus.SearchFull : RouteStatus.Unreachable, RouteEnds.None, 0, expanded, 0f); }

            // The cells of the way, the goal's first: where the heap was, which is done with.
            var way = links.Slice(3 * slots, 2 * slots);
            var steps = search.Way(goalSlot, way, out var spent);
            if (steps == 0) { return new RouteResult(RouteStatus.SearchFull, RouteEnds.None, 0, expanded, 0f); }
            way = way.Slice(0, steps);

            float atX, atY, atZ;
            if (!straighten) {
                _centre(shape, way[steps - 1], out atX, out atY, out atZ);
                if (!Legs.Clear(tree, shapes, size.Radius, size.LegNeed, fromX, fromY, fromZ, atX, atY, atZ)) { ends |= RouteEnds.FirstLegUnproven; }
                for (var j = steps - 1; j >= 0; j--) {
                    _centre(shape, way[j], out atX, out atY, out atZ);
                    _add(points, ref count, atX, atY, atZ);
                }
                if (atX == toX && atY == toY && atZ == toZ) {
                    // The goal is the last centre.
                } else if (Legs.Clear(tree, shapes, size.Radius, size.LegNeed, atX, atY, atZ, toX, toY, toZ)) {
                    _add(points, ref count, toX, toY, toZ);
                } else {
                    ends |= RouteEnds.StopsShortOfGoal;
                }
                return new RouteResult(_fits(points, count), ends, count, expanded, spent);
            }

            // The first leg: straight from the start to the farthest cell of the way it is clear to.
            var index = _farthest(tree, shapes, size, way, steps, fromX, fromY, fromZ);
            // Not one of those tried: every cell is, before the leg is said to be unproven.
            for (var j = 0; index < 0 && j < steps; j++) {
                _centre(shape, way[j], out atX, out atY, out atZ);
                if (Legs.Clear(tree, shapes, size.Radius, size.LegNeed, fromX, fromY, fromZ, atX, atY, atZ)) { index = j; }
            }
            if (index < 0) {
                index = steps - 1;
                ends |= RouteEnds.FirstLegUnproven;
            }
            _centre(shape, way[index], out atX, out atY, out atZ);
            // A start that is that centre is there already.
            if (atX != fromX || atY != fromY || atZ != fromZ) { _add(points, ref count, atX, atY, atZ); }

            while (true) {
                // A goal that is the centre reached is the route's last point already.
                if (atX == toX && atY == toY && atZ == toZ) { break; }
                if (Legs.Clear(tree, shapes, size.Radius, size.LegNeed, atX, atY, atZ, toX, toY, toZ)) {
                    _add(points, ref count, toX, toY, toZ);
                    break;
                }
                if (index == 0) {
                    ends |= RouteEnds.StopsShortOfGoal;
                    break;
                }
                // The farthest cell of the way that a straight leg is clear to, or else the next
                // one: a step, or a cut across one open cube.
                var next = _farthest(tree, shapes, size, way, index - 1, atX, atY, atZ);
                index = next < 0 ? index - 1 : next;
                _centre(shape, way[index], out atX, out atY, out atZ);
                _add(points, ref count, atX, atY, atZ);
            }
            // A route has a point. One whose start and goal are the one centre has that centre.
            if (count == 0) { _add(points, ref count, atX, atY, atZ); }
            return new RouteResult(_fits(points, count), ends, count, expanded, spent);
        }

        // Pulling straight tries one cell of the way in this many, and then those beside the
        // first it finds.
        private const int Stride = 4;

        // A cell of the way, among the first so many of it, that a straight leg from a point is
        // clear to: as far along the way as it finds one, or -1. The way's cells are the goal's
        // first. Testing a leg is most of what a route costs, so not every cell is tried: every
        // fourth from the far end, then the three beyond the first of those that is clear, and
        // failing all of those the three nearest. A cell not tried can only make the route a
        // point longer, never unsafe.
        private static int _farthest(in WallTree tree, in ShapeSet shapes, in SizeClass size, ReadOnlySpan<int> way, int count, float x, float y, float z) {
            var shape = tree.Shape;
            for (var j = 0; j < count; j += Stride) {
                _centre(shape, way[j], out var atX, out var atY, out var atZ);
                if (!Legs.Clear(tree, shapes, size.Radius, size.LegNeed, x, y, z, atX, atY, atZ)) { continue; }
                for (var k = j - Stride + 1; k < j; k++) {
                    if (k < 0) { continue; }
                    _centre(shape, way[k], out atX, out atY, out atZ);
                    if (Legs.Clear(tree, shapes, size.Radius, size.LegNeed, x, y, z, atX, atY, atZ)) { return k; }
                }
                return j;
            }
            for (var j = count - Stride + 1; j < count; j++) {
                if (j < 0 || j % Stride == 0) { continue; }
                _centre(shape, way[j], out var atX, out var atY, out var atZ);
                if (Legs.Clear(tree, shapes, size.Radius, size.LegNeed, x, y, z, atX, atY, atZ)) { return j; }
            }
            return -1;
        }

        private static void _add(Span<float> points, ref int count, float x, float y, float z) {
            var at = 3L * count;
            if (at + 3 <= points.Length) {
                points[(int)at] = x; points[(int)at + 1] = y; points[(int)at + 2] = z;
            }
            count++;
        }

        private static RouteStatus _fits(Span<float> points, int count) =>
            3L * count <= points.Length ? RouteStatus.Found : RouteStatus.BufferTooShort;

        private static void _centre(in GridShape shape, int cell, out float x, out float y, out float z) {
            x = shape.Centre(shape.OriginX, cell % shape.CountX);
            y = shape.Centre(shape.OriginY, cell / shape.CountX % shape.CountY);
            z = shape.Centre(shape.OriginZ, cell / (shape.CountX * shape.CountY));
        }

        // The cell an end belongs to, and its region: its own if that has one, and otherwise the
        // nearest cell with a region, within reach, whose centre it can see. Nearest first; among
        // equals the lowest in x, then y, then z.
        private static bool _end(in WallTree tree, in ShapeSet shapes, ReadOnlySpan<byte> region, in TreeRegions.View view,
            float x, float y, float z, double reach, out int cell, out byte number) {
            var shape = tree.Shape;
            if (shape.TryCell(x, y, z, out var ownX, out var ownY, out var ownZ)) {
                number = TreeRegions.Number(tree, region, view, ownX, ownY, ownZ);
                if (number != 0) {
                    cell = shape.Index(ownX, ownY, ownZ);
                    return true;
                }
            }
            cell = -1;
            number = 0;
            double size = shape.CellSize, wide = reach + size, farthest = reach * reach;
            int fromX = _first(x - wide, shape.OriginX, size, shape.CountX), toX = _last(x + wide, shape.OriginX, size, shape.CountX);
            int fromY = _first(y - wide, shape.OriginY, size, shape.CountY), toY = _last(y + wide, shape.OriginY, size, shape.CountY);
            int fromZ = _first(z - wide, shape.OriginZ, size, shape.CountZ), toZ = _last(z + wide, shape.OriginZ, size, shape.CountZ);
            var looks = shapes.Count > 0;
            // The one tried last: the next is the nearest that comes after it.
            var lastSquared = -1d;
            var lastOrder = -1L;
            while (true) {
                var found = -1;
                var best = 0d;
                var bestOrder = 0L;
                byte bestNumber = 0;
                float centreX = 0f, centreY = 0f, centreZ = 0f;
                for (var cellX = fromX; cellX <= toX; cellX++) {
                    var cx = shape.Centre(shape.OriginX, cellX);
                    double dx = (double)cx - x;
                    for (var cellY = fromY; cellY <= toY; cellY++) {
                        var cy = shape.Centre(shape.OriginY, cellY);
                        double dy = (double)cy - y;
                        for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                            var cz = shape.Centre(shape.OriginZ, cellZ);
                            double dz = (double)cz - z;
                            var squared = dx * dx + dy * dy + dz * dz;
                            if (!(squared <= farthest)) { continue; }
                            var order = ((long)cellX * shape.CountY + cellY) * shape.CountZ + cellZ;
                            if (squared < lastSquared || (squared == lastSquared && order <= lastOrder)) { continue; }
                            if (found >= 0 && (squared > best || (squared == best && order > bestOrder))) { continue; }
                            var here = TreeRegions.Number(tree, region, view, cellX, cellY, cellZ);
                            if (here == 0) { continue; }
                            found = shape.Index(cellX, cellY, cellZ);
                            best = squared;
                            bestOrder = order;
                            bestNumber = here;
                            centreX = cx; centreY = cy; centreZ = cz;
                        }
                    }
                }
                if (found < 0) { return false; }
                if (!looks || Legs.Sees(shapes, shape.CellSize, x, y, z, centreX, centreY, centreZ)) {
                    cell = found;
                    number = bestNumber;
                    return true;
                }
                lastSquared = best;
                lastOrder = bestOrder;
            }
        }

        // A search as it goes. A place is an open cube, or the open cells of a brick that are
        // joined inside it: most bricks are one place. Where those cells are not plain (see
        // WallTrees.Plain) the way through them can go a long way round, and they are a place
        // for each cell of a face of the brick that they are come into at: the two sides of a
        // sheet are come into at different cells. A brick's places are numbered from its own number times
        // PerBrick; the cubes are numbered after the bricks, and the goal after those. The table
        // finds a place's slot by its number; a key is the number and one, and 0 for a slot not
        // in use. HeapAt is where a slot waits in the heap, from 1, and -1 once it has been
        // taken up.
        private ref struct Search {
            public WallTree Tree;
            public Span<int> Keys, Parent, Cell, HeapAt, Heap;
            public Span<float> Spent, Rank;
            // The leaves last found, by the block of four cells each way that was asked for: one
            // for each of the eight blocks that can meet at a corner.
            public Span<int> Block, Leaves, Sides;
            // For the brick in hand: what the way to each of its cells costs from the cell it was
            // come into at, in cells, and the cell each was reached from.
            public Span<float> Near;
            public Span<byte> Back;
            public int Slots, Shift, Most, Used, Waiting, Taken, Bricks, FirstCube, GoalPlace;
            public int CountX, CountY, CountZ, Layer, BlocksX, BlocksY, GoalX, GoalY, GoalZ;
            // The size of a cell, and what a cell's length still to go is reckoned at.
            public double CellSize, Ahead;
            public byte Need, Plane, Space;
            // The same as the least four bits that read back as them.
            public byte NeedLevel, PlaneLevel, SpaceLevel;
            public bool Full;

            // From the start's cell until the goal is taken up. Returns the goal's slot, or -1
            // where there is no way or the table is full.
            public int Run(int start) {
                int x = start % CountX, y = start / CountX % CountY, z = start / Layer;
                var leaf = _leaf(x, y, z, out _);
                if (leaf < 0) { return -1; }
                var place = FirstCube + (leaf - Bricks);
                if (leaf < Bricks) {
                    var open = WallTrees.CellsAt(Tree, leaf, Need);
                    var cell = 1UL << ((x & 3) | ((y & 3) << 2) | ((z & 3) << 4));
                    if ((open & cell) == 0UL) { return -1; }
                    place = leaf * PerBrick + _lowest(WallTrees.Joined(cell, open));
                }
                if (!_offer(place, x, y, z, 0d, -1)) { return -1; }
                while (Waiting > 0) {
                    var slot = _pop();
                    place = Keys[slot] - 1;
                    if (place == GoalPlace) { return slot; }
                    var at = Cell[slot];
                    x = at % CountX; y = at / CountX % CountY; z = at / Layer;
                    if (!(place < FirstCube ? _leave(slot, place / PerBrick, x, y, z) : _cross(slot, x, y, z))) { return -1; }
                }
                return -1;
            }

            // The cells of the way, the goal's first, written to way, and what the way costs.
            // Returns how many there are, or 0 if they do not fit.
            public int Way(int goalSlot, Span<int> way, out float spent) {
                spent = 0f;
                var steps = 0;
                way[steps++] = Cell[goalSlot];
                var length = 0d;
                for (var slot = Parent[goalSlot]; slot >= 0; slot = Parent[slot]) {
                    if (steps + WallTrees.BrickCells + 1 > way.Length) { return 0; }
                    var cell = Cell[slot];
                    var place = Keys[slot] - 1;
                    int x = cell % CountX, y = cell / CountX % CountY, z = cell / Layer;
                    var side = 4;
                    if (place >= FirstCube) { _leaf(x, y, z, out side); }
                    // The way leaves the place by its cell nearest the one that follows, which lies
                    // across a face of it, or is the goal inside it.
                    var next = way[steps - 1];
                    int originX = x & ~(side - 1), originY = y & ~(side - 1), originZ = z & ~(side - 1);
                    int leaveX = _clamp(next % CountX, originX, side), leaveY = _clamp(next / CountX % CountY, originY, side), leaveZ = _clamp(next / Layer, originZ, side);
                    var leave = leaveX + leaveY * CountX + leaveZ * Layer;
                    if (leave != next) {
                        length += _between(leave, next);
                        way[steps++] = leave;
                    }
                    if (cell == leave) { continue; }
                    if (place >= FirstCube) {
                        // An open cube: one cut, from the cell it was come into at.
                        length += _between(cell, leave);
                        way[steps++] = cell;
                        continue;
                    }
                    // A brick: by the dense search's steps between its open cells, the cheapest
                    // way there is inside it from the cell it was come into at.
                    int first = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4), last = (leaveX & 3) | ((leaveY & 3) << 2) | ((leaveZ & 3) << 4);
                    _through(place / PerBrick, first, last);
                    if (float.IsInfinity(Near[last])) { return 0; }
                    length += Near[last];
                    for (var at = last; at != first;) {
                        at = Back[at];
                        way[steps++] = originX + (at & 3) + (originY + ((at >> 2) & 3)) * CountX + (originZ + (at >> 4)) * Layer;
                    }
                }
                spent = (float)(length * CellSize);
                return steps;
            }

            // What the way to each cell of a brick costs from one of its open cells, in cells, by
            // steps to the cells round a cell under the dense search's rules; and the cell each
            // was reached from. A cell with no way to it is left at infinity. With a last cell
            // given it stops when that one's way is known.
            private void _through(int brick, int first, int last) {
                Span<byte> levels = stackalloc byte[WallTrees.BrickCells];
                WallTrees.LevelsIn(Tree, brick, levels);
                var open = WallTrees.CellsAt(Tree, brick, Need);
                Near.Fill(float.PositiveInfinity);
                Near[first] = 0f;
                ulong done = 0UL, waiting = 1UL << first;
                while (waiting != 0UL) {
                    // The nearest cell that waits; of cells equally near, the lowest.
                    var at = -1;
                    var least = float.PositiveInfinity;
                    for (var left = waiting; left != 0UL; left &= left - 1UL) {
                        var cell = _lowest(left);
                        if (Near[cell] < least) {
                            least = Near[cell];
                            at = cell;
                        }
                    }
                    if (at == last) { return; }
                    waiting &= ~(1UL << at);
                    done |= 1UL << at;
                    int atX = at & 3, atY = (at >> 2) & 3, atZ = at >> 4;
                    // The open cells round it that are not done, lowest first.
                    for (var round = WallTrees.Around(1UL << at) & open & ~done; round != 0UL; round &= round - 1UL) {
                        var next = _lowest(round);
                        int dx = (next & 3) - atX, dy = ((next >> 2) & 3) - atY, dz = (next >> 4) - atZ;
                        int stepX = dx, stepY = dy * 4, stepZ = dz * 16;
                        var axes = dx * dx + dy * dy + dz * dz;
                        var length = 1f;
                        if (axes == 2) {
                            // Across a square: both ends far enough out, and the square's other two cells open.
                            if (levels[at] < PlaneLevel || levels[next] < PlaneLevel) { continue; }
                            int a = dx == 0 ? stepY : stepX, b = dz == 0 ? stepY : stepZ;
                            if (levels[at + a] < NeedLevel || levels[at + b] < NeedLevel) { continue; }
                            length = 1.41421354f;
                        } else if (axes == 3) {
                            // Across a cube: both ends farther out still, and the cube's other six cells open.
                            if (levels[at] < SpaceLevel || levels[next] < SpaceLevel) { continue; }
                            if (levels[at + stepX] < NeedLevel || levels[at + stepY] < NeedLevel || levels[at + stepZ] < NeedLevel ||
                                levels[at + stepX + stepY] < NeedLevel || levels[at + stepX + stepZ] < NeedLevel || levels[at + stepY + stepZ] < NeedLevel) { continue; }
                            length = 1.73205078f;
                        }
                        // Rounded to a float before it is compared: a runtime that adds floats
                        // as doubles would otherwise break ties another way.
                        var reached = (float)((double)Near[at] + length);
                        if (reached < Near[next]) {
                            Near[next] = reached;
                            Back[next] = (byte)at;
                            waiting |= 1UL << next;
                        }
                    }
                }
            }

            private static int _clamp(int value, int first, int side) => value < first ? first : value > first + side - 1 ? first + side - 1 : value;

            // How far apart two cells' centres are, in cells.
            private double _between(int a, int b) {
                int dx = a % CountX - b % CountX, dy = a / CountX % CountY - b / CountX % CountY, dz = a / Layer - b / Layer;
                return Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
            }

            // The leaf that holds a cell and how many cells it has along a side: -1 and 0 for a
            // cell in a cube of zeros or not in the grid.
            private int _leaf(int x, int y, int z, out int side) {
                side = 0;
                if ((uint)x >= (uint)CountX || (uint)y >= (uint)CountY || (uint)z >= (uint)CountZ) { return -1; }
                int blockX = x >> 2, blockY = y >> 2, blockZ = z >> 2;
                var at = (blockX & 1) | ((blockY & 1) << 1) | ((blockZ & 1) << 2);
                var block = blockX + (blockY + blockZ * BlocksY) * BlocksX;
                if (Block[at] != block) {
                    Leaves[at] = WallTrees.LeafOf(Tree, x, y, z, out var found);
                    Sides[at] = found;
                    Block[at] = block;
                }
                side = Sides[at];
                return Leaves[at];
            }

            // Which bit is the lowest one set.
            private static int _lowest(ulong cells) => WallTrees.Count((cells & (~cells + 1UL)) - 1UL);


            // From a brick's place, come into at a cell: on to the goal if it is among the place's
            // cells, and across each of the brick's six faces to whatever lies beside the place's
            // cells there. Through plain cells the way to a cell is reckoned as the straight
            // line. Through others it is worked out first, so that what is offered is what the
            // way costs: a sheet through the middle of a brick can make its far side a long way
            // round.
            private bool _leave(int slot, int brick, int x, int y, int z) {
                var first = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4);
                var cells = WallTrees.Joined(1UL << first, WallTrees.CellsAt(Tree, brick, Need));
                var exact = !WallTrees.Plain(cells);
                if (exact) { _through(brick, first, -1); }
                int originX = x & ~3, originY = y & ~3, originZ = z & ~3;
                if ((GoalX & ~3) == originX && (GoalY & ~3) == originY && (GoalZ & ~3) == originZ) {
                    var goal = (GoalX & 3) | ((GoalY & 3) << 2) | ((GoalZ & 3) << 4);
                    if (((cells >> goal) & 1UL) != 0UL &&
                        !_offer(GoalPlace, GoalX, GoalY, GoalZ, Spent[slot] + CellSize * (exact ? Near[goal] : _cut(x, y, z, GoalX, GoalY, GoalZ)), slot)) { return false; }
                }
                for (var face = 0; face < 6; face++) {
                    int axis = face >> 1, layer = (face & 1) == 0 ? 0 : 3;
                    var pattern = WallTrees.Layer(cells, axis, layer);
                    if (pattern == 0) { continue; }
                    var origin = axis == 0 ? originX : axis == 1 ? originY : originZ;
                    if (!_onto(slot, exact, x, y, z, axis, origin + layer, layer == 0 ? origin - 1 : origin + 4, axis == 0 ? originY : originX, axis == 2 ? originY : originZ, pattern)) { return false; }
                }
                return true;
            }

            // From an open cube, come into at a cell: straight to the goal if it is in the cube,
            // and across each face, a square of four cells by four at a time.
            private bool _cross(int slot, int x, int y, int z) {
                _leaf(x, y, z, out var side);
                int originX = x & ~(side - 1), originY = y & ~(side - 1), originZ = z & ~(side - 1);
                if ((GoalX & ~(side - 1)) == originX && (GoalY & ~(side - 1)) == originY && (GoalZ & ~(side - 1)) == originZ) {
                    return _offer(GoalPlace, GoalX, GoalY, GoalZ, Spent[slot] + CellSize * _cut(x, y, z, GoalX, GoalY, GoalZ), slot);
                }
                for (var face = 0; face < 6; face++) {
                    var axis = face >> 1;
                    var origin = axis == 0 ? originX : axis == 1 ? originY : originZ;
                    int own = (face & 1) == 0 ? origin : origin + side - 1, across = (face & 1) == 0 ? origin - 1 : origin + side;
                    if ((uint)across >= (uint)(axis == 0 ? CountX : axis == 1 ? CountY : CountZ)) { continue; }
                    int originU = axis == 0 ? originY : originX, originV = axis == 2 ? originY : originZ;
                    // Where the straight line to the goal passes through the cube's own layer, if it does.
                    _aim(x, y, z, axis, own, out var aimU, out var aimV);
                    for (var v = originV; v < originV + side; v += 4) {
                        for (var u = originU; u < originU + side; u += 4) {
                            var leaf = _leaf(_x(axis, across, u, v), _y(axis, across, u, v), _z(axis, across, u, v), out var other);
                            if (leaf < Bricks) {
                                if (!_onto(slot, false, x, y, z, axis, own, across, u, v, 0xFFFF)) { return false; }
                                continue;
                            }
                            // Another open cube: the part of the face the two share is dealt with
                            // once, from its first square, and left by its cell nearest that line.
                            int fromU = u & ~(other - 1), fromV = v & ~(other - 1), toU = fromU + other, toV = fromV + other;
                            if (fromU < originU) { fromU = originU; }
                            if (fromV < originV) { fromV = originV; }
                            if (toU > originU + side) { toU = originU + side; }
                            if (toV > originV + side) { toV = originV + side; }
                            if (u != fromU || v != fromV) { continue; }
                            int exitU = aimU < fromU ? fromU : aimU > toU - 1 ? toU - 1 : aimU, exitV = aimV < fromV ? fromV : aimV > toV - 1 ? toV - 1 : aimV;
                            if (!_offer(FirstCube + (leaf - Bricks), _x(axis, across, exitU, exitV), _y(axis, across, exitU, exitV), _z(axis, across, exitU, exitV),
                                    Spent[slot] + CellSize * (_cut(x, y, z, _x(axis, own, exitU, exitV), _y(axis, own, exitU, exitV), _z(axis, own, exitU, exitV)) + 1d), slot)) { return false; }
                        }
                    }
                }
                return true;
            }

            // Offers what lies across a square of four cells by four of a face its way on from the
            // place in a slot, come into at (x, y, z). The square is in the place's own layer
            // along the axis and begins at fromU and fromV along the other two; the pattern says
            // which of its cells are the place's own. What lies across is one leaf: a cube of
            // zeros, an open cube, or a brick, of which each place that has an open cell beside
            // one of the place's own is offered.
            private bool _onto(int slot, bool exact, int x, int y, int z, int axis, int own, int across, int fromU, int fromV, int pattern) {
                if ((uint)across >= (uint)(axis == 0 ? CountX : axis == 1 ? CountY : CountZ)) { return true; }
                var leaf = _leaf(_x(axis, across, fromU, fromV), _y(axis, across, fromU, fromV), _z(axis, across, fromU, fromV), out _);
                if (leaf < 0) { return true; }
                double spent = Spent[slot];
                if (leaf >= Bricks) {
                    var by = _best(exact, x, y, z, axis, own, across, fromU, fromV, pattern, out var reach);
                    int u = fromU + (by & 3), v = fromV + (by >> 2);
                    return _offer(FirstCube + (leaf - Bricks), _x(axis, across, u, v), _y(axis, across, u, v), _z(axis, across, u, v), spent + CellSize * (reach + 1d), slot);
                }
                var open = WallTrees.CellsAt(Tree, leaf, Need);
                var near = (across & 3) == 0 ? 0 : 3;
                var entered = WallTrees.FromLayer(pattern, axis, near) & open;
                while (entered != 0UL) {
                    var joined = WallTrees.Joined(entered & (~entered + 1UL), open);
                    var into = WallTrees.Layer(entered & joined, axis, near);
                    entered &= ~joined;
                    // Plain cells are one place, come into at the best of them. Others are a
                    // place for each cell they are come into at: what joins two of those cells
                    // inside the brick may be a long way round.
                    var plain = WallTrees.Plain(joined);
                    while (into != 0) {
                        var part = plain ? into : into & -into;
                        into &= ~part;
                        var by = _best(exact, x, y, z, axis, own, across, fromU, fromV, part, out var reach);
                        int u = fromU + (by & 3), v = fromV + (by >> 2);
                        var place = leaf * PerBrick + (plain ? _lowest(joined) : ByFace + 16 * (2 * axis + (near == 0 ? 0 : 1)) + by);
                        if (!_offer(place, _x(axis, across, u, v), _y(axis, across, u, v), _z(axis, across, u, v), spent + CellSize * (reach + 1d), slot)) { return false; }
                    }
                }
                return true;
            }

            // The cell of a pattern by which the way on is reckoned shortest: through the place to
            // its own cell there, a step across, and straight on to the goal. reach is how far the
            // way through the place is to that cell, in cells: what _through found where that
            // was worked out, and otherwise the straight cut.
            private int _best(bool exact, int x, int y, int z, int axis, int own, int across, int fromU, int fromV, int pattern, out double reach) {
                var best = 0;
                var least = double.PositiveInfinity;
                reach = 0d;
                for (var k = 0; k < 16; k++) {
                    if (((pattern >> k) & 1) == 0) { continue; }
                    int u = fromU + (k & 3), v = fromV + (k >> 2);
                    int ownX = _x(axis, own, u, v), ownY = _y(axis, own, u, v), ownZ = _z(axis, own, u, v);
                    var within = exact ? Near[(ownX & 3) | ((ownY & 3) << 2) | ((ownZ & 3) << 4)] : _cut(x, y, z, ownX, ownY, ownZ);
                    var whole = within + _cut(_x(axis, across, u, v), _y(axis, across, u, v), _z(axis, across, u, v), GoalX, GoalY, GoalZ);
                    if (whole < least) {
                        least = whole;
                        best = k;
                        reach = within;
                    }
                }
                return best;
            }

            // The cell at a place along an axis and at u and v along the other two.
            private static int _x(int axis, int along, int u, int v) => axis == 0 ? along : u;
            private static int _y(int axis, int along, int u, int v) => axis == 1 ? along : axis == 0 ? u : v;
            private static int _z(int axis, int along, int u, int v) => axis == 2 ? along : v;

            // Where the straight line from a cell to the goal's cell crosses a layer along an axis,
            // as u and v; where it does not cross it, the goal's own.
            private void _aim(int x, int y, int z, int axis, int layer, out int u, out int v) {
                int from = axis == 0 ? x : axis == 1 ? y : z, to = axis == 0 ? GoalX : axis == 1 ? GoalY : GoalZ;
                var t = to != from ? (layer - from) / (double)(to - from) : 2d;
                if (!(t >= 0d && t <= 1d)) { t = 1d; }
                double atX = x + (GoalX - x) * t, atY = y + (GoalY - y) * t, atZ = z + (GoalZ - z) * t;
                // Halves go up: rounding to the nearest even number is not the same on every runtime.
                u = (int)Math.Floor((axis == 0 ? atY : atX) + 0.5d);
                v = (int)Math.Floor((axis == 2 ? atY : atZ) + 0.5d);
            }

            // How far apart two cells are, in cells.
            private static double _cut(int x, int y, int z, int toX, int toY, int toZ) {
                double dx = toX - x, dy = toY - y, dz = toZ - z;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            // Offers a place a way that costs so much and comes into it at a cell. A place taken
            // up keeps the way it has, and one that waits takes a cheaper one. False when the
            // table has no slot left for a place not yet in it.
            private bool _offer(int place, int x, int y, int z, double spent, int from) {
                var slot = (int)(((uint)place * Scatter) >> Shift);
                while (true) {
                    var key = Keys[slot];
                    if (key == place + 1) { break; }
                    if (key == 0) {
                        if (Used == Most) {
                            Full = true;
                            return false;
                        }
                        Keys[slot] = place + 1;
                        HeapAt[slot] = 0;
                        Used++;
                        break;
                    }
                    slot = (slot + 1) & (Slots - 1);
                }
                var waits = HeapAt[slot];
                if (waits < 0 || (waits > 0 && !(spent < Spent[slot]))) { return true; }
                Spent[slot] = (float)spent;
                Parent[slot] = from;
                Cell[slot] = x + y * CountX + z * Layer;
                double dx = x - GoalX, dy = y - GoalY, dz = z - GoalZ;
                var rank = (float)(spent + Ahead * Math.Sqrt(dx * dx + dy * dy + dz * dz));
                // A place that waits can rank higher than it did: come into more cheaply at a cell
                // farther from the goal. Then down the heap.
                if (waits > 0 && rank > Rank[slot]) {
                    Rank[slot] = rank;
                    _down(waits - 1, slot, rank);
                    return true;
                }
                // Up the heap from where it waits, or from a new last place.
                var at = waits > 0 ? waits - 1 : Waiting++;
                while (at > 0) {
                    var above = (at - 1) >> 1;
                    var other = Heap[above];
                    if (!(rank < Rank[other])) { break; }
                    Heap[at] = other;
                    HeapAt[other] = at + 1;
                    at = above;
                }
                Heap[at] = slot;
                HeapAt[slot] = at + 1;
                Rank[slot] = rank;
                return true;
            }

            // Puts a slot of a rank at a place in the heap or below it.
            private void _down(int at, int slot, float rank) {
                while (true) {
                    var below = 2 * at + 1;
                    if (below >= Waiting) { break; }
                    if (below + 1 < Waiting && Rank[Heap[below + 1]] < Rank[Heap[below]]) { below++; }
                    if (!(Rank[Heap[below]] < rank)) { break; }
                    Heap[at] = Heap[below];
                    HeapAt[Heap[at]] = at + 1;
                    at = below;
                }
                Heap[at] = slot;
                HeapAt[slot] = at + 1;
            }

            // Takes the slot with the least rank out of the heap.
            private int _pop() {
                var top = Heap[0];
                Waiting--;
                if (Waiting > 0) {
                    var moved = Heap[Waiting];
                    _down(0, moved, Rank[moved]);
                }
                HeapAt[top] = -1;
                Taken++;
                return top;
            }
        }

        // The first and last cells along an axis whose centres lie at or beyond a bound, held to the grid.
        private static int _first(double bound, float origin, double size, int count) {
            var at = Math.Ceiling((bound - origin) / size - 0.5d);
            return at > 0d ? (at < count ? (int)at : count) : 0;
        }

        private static int _last(double bound, float origin, double size, int count) {
            var at = Math.Floor((bound - origin) / size - 0.5d);
            return at < count - 1 ? (at >= 0d ? (int)at : -1) : count - 1;
        }
    }
}
