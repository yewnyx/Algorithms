using System;

namespace Yewnyx.Spatial {
    /// <summary>How a search for a route ended.</summary>
    public enum RouteStatus {
        /// <summary>Nothing was looked for, because something given cannot be used. There are no points.</summary>
        Refused = 0,
        /// <summary>The points lead from the start to the goal, or as near the goal as <see cref="RouteEnds"/> says. There is at least one.</summary>
        Found = 1,
        /// <summary>There is no way from the start to the goal for a body of this size. There are no points.</summary>
        Unreachable = 2,
        /// <summary>There is a route, and the buffer is too short for its points. The count is how many it has.</summary>
        BufferTooShort = 3,
        /// <summary>
        /// The search held as many places as its arrays have room for before it found a way:
        /// there may be one. There are no points. Only <see cref="TreeRoutes.Find"/> ends so.
        /// </summary>
        SearchFull = 4,
    }

    /// <summary>What a route does not promise at its two ends.</summary>
    [Flags]
    public enum RouteEnds {
        /// <summary>Neither of the two below: nothing is left unproven at either end.</summary>
        None = 0,
        /// <summary>
        /// The way from the start to the route's first point is not proven clear: the start is
        /// nearer a shape than the radius, or beside a closed cell. The first point is then the
        /// centre of the nearest open cell the start can see.
        /// </summary>
        FirstLegUnproven = 1,
        /// <summary>
        /// The route ends at the centre of an open cell near the goal and not at the goal: the way
        /// from there to the goal is not proven clear.
        /// </summary>
        StopsShortOfGoal = 2,
    }

    /// <summary>What <see cref="Routes.Find"/> or <see cref="TreeRoutes.Find"/> found. <c>default</c> is a search that was refused.</summary>
    public readonly struct RouteResult {
        /// <summary>How the search ended.</summary>
        public readonly RouteStatus Status;
        /// <summary>What the route does not promise at its two ends.</summary>
        public readonly RouteEnds Ends;
        /// <summary>
        /// How many points the route has: at least one when there is a route. More than the buffer
        /// holds when the status is <see cref="RouteStatus.BufferTooShort"/>.
        /// </summary>
        public readonly int Count;
        /// <summary>How many cells the search took up, or how many places over a tree; 0 when the straight leg from start to goal was enough.</summary>
        public readonly int Expanded;
        /// <summary>
        /// What the way costs from cell to cell, before it is pulled straight, from the centre of
        /// the start's cell to the centre of the goal's; 0 when nothing was searched. The way
        /// from the start to the first of those centres and from the last to the goal is not
        /// in it, so a short route can be longer than this says.
        /// </summary>
        public readonly float Cost;

        internal RouteResult(RouteStatus status, RouteEnds ends, int count, int expanded, float cost) {
            Status = status; Ends = ends; Count = count; Expanded = expanded; Cost = cost;
        }
    }

    /// <summary>
    /// The way for a body of one size from one point of a grid to another: a list of points to go
    /// to in turn, in straight legs.
    /// </summary>
    /// <remarks>
    /// The start is not among the points. Every leg is one of two kinds. A straight leg is proven
    /// clear along its whole length for the class's full <see cref="SizeClass.Radius"/>: by the
    /// wall map where the map can say, and by the shapes where it cannot. A step is a leg between
    /// the centres of two open cells that touch, and keeps the class's <see cref="SizeClass.Guarantee"/>.
    /// A leg never passes through a cell that is closed by cost, nor nearer one than half a cell,
    /// and never leaves the grid. The two ends are the exception, and <see cref="RouteEnds"/>
    /// says when. Half a cell is less than a body's radius: to keep a body off something that
    /// is shut by cost alone, close the cells out to the radius beyond it.
    /// The way is found among open cells, cheapest first, where a step into a cell is its length
    /// times 1 + cost / 16 for that cell. It is then pulled straight wherever a straight leg is
    /// clear and is no worse than the part of the way it takes the place of, in two things.
    /// It passes through no cell that costs more than the costliest cell of that part, with
    /// the cells the leg's two ends are in. And it costs no more as flown: its length in each
    /// cell times 1 + cost / 16 for that cell, against the same along the straight lines from
    /// centre to centre of that part. So a route never costs more as flown than the way left
    /// as cells does, it goes round an area that the way went round, and it crosses one only
    /// where that costs no more than what the way did. A leg that goes through an edge or a
    /// corner between cells does not pass through the other cells that meet there, as a step
    /// across a square or a cube does not.
    /// Pulling straight takes, from each point, the farthest cell of the way that such a leg
    /// reaches. That gives few legs, and a route that is nearly always shorter than the cells'
    /// way, but not always the shortest there is: now and then a nearer turning point would have
    /// made the leg after it shorter.
    /// Nothing is allocated and nothing throws.
    /// </remarks>
    public static class Routes {
        /// <summary>The most cells a grid may have for a search.</summary>
        public const int MostCells = (1 << 26) - 1;

        /// <summary>The length of the sums <see cref="Find"/> needs: two floats per cell. 0 for a grid that cannot be searched.</summary>
        public static int SumsLength(in GridShape shape) => 2 * _cells(shape);

        /// <summary>The length of the links <see cref="Find"/> needs: one int per cell. 0 for a grid that cannot be searched.</summary>
        public static int LinksLength(in GridShape shape) => _cells(shape);

        /// <summary>The length of the heap <see cref="Find"/> needs: one int per cell. 0 for a grid that cannot be searched.</summary>
        public static int HeapLength(in GridShape shape) => _cells(shape);

        private static int _cells(in GridShape shape) {
            var cells = shape.CellCount;
            return cells > MostCells ? 0 : cells;
        }

        /// <summary>
        /// Finds the way from one point to another for a size class, and writes its points, three
        /// floats each, the goal last.
        /// </summary>
        /// <remarks>
        /// In order: which region each end is in; the straight leg from start to goal, taken
        /// at once if it is clear and sure to cost no more as flown than any way there is; a
        /// search over the open cells of the region; pulling the cells' way straight, which
        /// tries the leg from start to goal again, against what the way costs. With
        /// <paramref name="straighten"/> false the last and the straight leg are left out, and
        /// the points are the centres of the cells the way steps through, from the start's cell.
        /// An end in an open cell is in that cell's region. Any other end takes the nearest open
        /// centre it can see, no farther than 1.73 times the radius and a cell: one that the
        /// straight line to it reaches without meeting a shape, not counting the first 5 cm, since
        /// an end may lie on a floor. With no shapes it is the nearest, seen or not. Ends in two
        /// regions, or an end with no such centre, are <see cref="RouteStatus.Unreachable"/>, and
        /// nothing is searched. Where a cell has been closed by cost since the regions were made,
        /// a start in it is routed out of it, and a goal in it is unreachable.
        /// <paramref name="shapes"/> may be a set with no shapes: a leg is then proven by the map
        /// alone, which needs more room than the radius. <paramref name="cost"/> may be empty.
        /// <paramref name="sums"/>, <paramref name="links"/> and <paramref name="heap"/> are the
        /// search's own, and may be longer than this grid needs, so that one set serves grids of
        /// every size. The links must be all zero the first time; a search leaves them so, and
        /// the set can then be used again without clearing. If they are not zero the search still
        /// ends, and may say unreachable where there is a way.
        /// <paramref name="leeway"/> is how much more than the cheapest the way may cost, as a
        /// share of it: with 0.1, up to a tenth more. The search then reckons what is left to go
        /// at that much over, heads for the goal sooner, and takes up far fewer cells where the
        /// cells' costs differ across the grid. The straight leg from start to goal is taken at
        /// once on the same terms. With 0 the way is the cheapest there is. The legs are held to
        /// the way that was found, whatever the leeway: a route costs no more as flown than its
        /// way left as cells.
        /// <paramref name="leastCost"/> is a cost that no cell with a region is below, such as
        /// <see cref="Regions.LeastCost"/> gives; 0 is always right. Where every cell costs
        /// something it saves work twice. No way between two centres can cost less than the
        /// straight line between them at that cost, so the straight leg from start to goal is
        /// taken with nothing searched where it costs no more than that, with the way from each
        /// end to its cell's centre; where no cell has a cost that is every clear leg. And a
        /// search reckons each metre still to go at that cost, and takes up fewer cells. One
        /// that is too high loses the cheapest way, and nothing else.
        /// It saves that work only where it is above 0, which is where every cell with a region
        /// has a cost. Where a whole grid is meant to cost more, give every cell of it the cost:
        /// one cell with a region left at the normal cost, such as one beyond a doorway that the
        /// grid also covers, and the least is 0.
        /// The status is <see cref="RouteStatus.Refused"/> if the map cannot be used, the class does not
        /// fit it, the grid has more than <see cref="MostCells"/> cells, an array is too short (the
        /// cost array is neither empty nor one byte per cell), the shapes' spans do not belong
        /// together, a point is not a finite number, or the leeway is below 0 or not finite.
        /// </remarks>
        public static RouteResult Find(in WallMap wall, in SizeClass size, in ShapeSet shapes, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ, bool straighten,
            Span<float> sums, Span<int> links, Span<int> heap, Span<float> points, byte leastCost = 0, float leeway = 0f) {
            var count = 0;
            var ends = RouteEnds.None;
            var shape = wall.Shape;
            var cells = shape.CellCount;
            if (!wall.IsValid || !size.Fits(wall) || cells > MostCells || region.Length < cells || (cost.Length != 0 && cost.Length < cells) ||
                sums.Length < 2 * cells || links.Length < cells || heap.Length < cells || shapes.Count < 0) { return default; }
            var sum = (double)fromX + fromY + fromZ + toX + toY + toZ;
            if (!(sum - sum == 0d)) { return default; }
            if (!(leeway >= 0f) || float.IsInfinity(leeway)) { return default; }
            // How many times what the cheapest way costs the way found may cost.
            var times = 1d + leeway;

            var reach = Math.Sqrt(3d) * ((double)size.Radius + shape.CellSize);
            if (!_end(shape, shapes, region, fromX, fromY, fromZ, reach, out var start) ||
                !_end(shape, shapes, region, toX, toY, toZ, reach, out var goal) || region[start] != region[goal]) {
                return new RouteResult(RouteStatus.Unreachable, RouteEnds.None, 0, 0, 0f);
            }

            // What the cells that hold the two ends cost. A leg from an end may pass through cells
            // that cost as much as the one the end is in.
            int fromCost = Legs.CostAt(shape, cost, fromX, fromY, fromZ), toCost = Legs.CostAt(shape, cost, toX, toY, toZ);
            var priced = cost.Length != 0;

            // What each metre costs at the least: no cell of a way costs less.
            var weight = 1d + leastCost / 16d;
            // What the way's two ends cost as flown: from the start to the centre of its cell, and
            // from the centre of the goal's cell to the goal.
            double fromWorth = 0d, toWorth = 0d;
            float atX = 0f, atY = 0f, atZ = 0f;
            // Whether the straight leg from start to goal is sure to be no worse than any way there
            // is, with nothing searched. Every way goes from the start to the centre of its cell,
            // from there to the centre of the goal's cell, and on to the goal. Between the two
            // centres it is no shorter than the straight line, and none of its cells costs less
            // than the least. It begins and ends in those two cells, so the costliest of its cells
            // costs no less than they do.
            var sure = true;
            if (priced && straighten) {
                _centre(shape, start, out atX, out atY, out atZ);
                Legs.Flown(shape, cost, Legs.AnyCost, fromX, fromY, fromZ, atX, atY, atZ, out fromWorth);
                double dx = atX, dy = atY, dz = atZ;
                _centre(shape, goal, out atX, out atY, out atZ);
                Legs.Flown(shape, cost, Legs.AnyCost, atX, atY, atZ, toX, toY, toZ, out toWorth);
                dx -= atX; dy -= atY; dz -= atZ;
                var costliest = fromCost > toCost ? fromCost : toCost;
                if (cost[start] > costliest) { costliest = cost[start]; }
                if (cost[goal] > costliest) { costliest = cost[goal]; }
                sure = Legs.Flown(shape, cost, costliest, fromX, fromY, fromZ, toX, toY, toZ, out var flown) &&
                       flown <= (fromWorth + Math.Sqrt(dx * dx + dy * dy + dz * dz) * weight + toWorth) * times * Spare;
            }
            if (straighten && sure && Legs.Clear(wall, shapes, cost, Legs.AnyCost, Legs.AnySpend, size.Radius, size.LegNeed, fromX, fromY, fromZ, toX, toY, toZ)) {
                _add(points, ref count, toX, toY, toZ);
                return new RouteResult(_fits(points, count), ends, count, 0, 0f);
            }

            var steps = _search(wall, size, region, cost, start, goal, weight * times, sums.Slice(0, cells), sums.Slice(cells, cells),
                links.Slice(0, cells), heap.Slice(0, cells), out var expanded);
            if (steps == 0) { return new RouteResult(RouteStatus.Unreachable, RouteEnds.None, 0, expanded, 0f); }
            // The cells of the way, the goal's first, and what the way costs.
            ReadOnlySpan<int> way = heap.Slice(0, steps);
            var spent = sums[goal];

            if (!straighten) {
                _centre(shape, way[steps - 1], out atX, out atY, out atZ);
                if (!Legs.Clear(wall, shapes, cost, Legs.AnyCost, Legs.AnySpend, size.Radius, size.LegNeed, fromX, fromY, fromZ, atX, atY, atZ)) { ends |= RouteEnds.FirstLegUnproven; }
                for (var j = steps - 1; j >= 0; j--) {
                    _centre(shape, way[j], out atX, out atY, out atZ);
                    _add(points, ref count, atX, atY, atZ);
                }
                if (atX == toX && atY == toY && atZ == toZ) {
                    // The goal is the last centre.
                } else if (Legs.Clear(wall, shapes, cost, Legs.AnyCost, Legs.AnySpend, size.Radius, size.LegNeed, atX, atY, atZ, toX, toY, toZ)) {
                    _add(points, ref count, toX, toY, toZ);
                } else {
                    ends |= RouteEnds.StopsShortOfGoal;
                }
                return new RouteResult(_fits(points, count), ends, count, expanded, spent);
            }

            // For each cell of the way, two things about the part of the way from the cell the
            // route has reached to it: the highest cost among its cells, and what it costs as
            // flown from centre to centre. The search is done with its sums, so they are kept there.
            Span<float> highest = sums.Slice(0, steps), worth = sums.Slice(cells, steps);
            if (priced) {
                _stretch(shape, cost, way, steps - 1, highest, worth);
                // The straight leg from start to goal again, now that there is a way to hold it
                // to. One that was sure before the search was not clear, and is not now.
                if (!sure) {
                    var most = fromCost > toCost ? fromCost : toCost;
                    if (highest[0] > most) { most = (int)highest[0]; }
                    if (Legs.Clear(wall, shapes, cost, most, (fromWorth + worth[0] + toWorth) * Spare, size.Radius, size.LegNeed, fromX, fromY, fromZ, toX, toY, toZ)) {
                        _add(points, ref count, toX, toY, toZ);
                        return new RouteResult(_fits(points, count), ends, count, expanded, spent);
                    }
                }
            }

            // The first leg: straight from the start to the farthest cell of the way it is clear
            // to. The leg to the centre of the start's own cell is not held to a cost: it is the
            // way's own first piece.
            var index = -1;
            for (var j = 0; j < steps; j++) {
                _centre(shape, way[j], out atX, out atY, out atZ);
                var held = priced && j < steps - 1;
                var most = !held ? Legs.AnyCost : fromCost > highest[j] ? fromCost : (int)highest[j];
                var spend = held ? (fromWorth + worth[j]) * Spare : Legs.AnySpend;
                if (Legs.Clear(wall, shapes, cost, most, spend, size.Radius, size.LegNeed, fromX, fromY, fromZ, atX, atY, atZ)) {
                    index = j;
                    break;
                }
            }
            if (index < 0) {
                index = steps - 1;
                ends |= RouteEnds.FirstLegUnproven;
            }
            // A start that is that centre is there already.
            if (atX != fromX || atY != fromY || atZ != fromZ) { _add(points, ref count, atX, atY, atZ); }

            while (true) {
                // A goal that is the centre reached is the route's last point already. Where the
                // start is that centre too, it is the route's one point: a route has a point.
                if (atX == toX && atY == toY && atZ == toZ) {
                    if (count == 0) { _add(points, ref count, atX, atY, atZ); }
                    break;
                }
                if (priced) { _stretch(shape, cost, way, index, highest, worth); }
                // From the centre of the goal's own cell, the leg to the goal is not held to a
                // cost: it is the way's own last piece.
                var held = priced && index > 0;
                var most = !held ? Legs.AnyCost : toCost > highest[0] ? toCost : (int)highest[0];
                var spend = held ? (worth[0] + toWorth) * Spare : Legs.AnySpend;
                if (Legs.Clear(wall, shapes, cost, most, spend, size.Radius, size.LegNeed, atX, atY, atZ, toX, toY, toZ)) {
                    _add(points, ref count, toX, toY, toZ);
                    break;
                }
                if (index == 0) {
                    ends |= RouteEnds.StopsShortOfGoal;
                    break;
                }
                // The farthest cell of the way that a straight leg is clear to, or else the next
                // one, which is a step.
                var next = index - 1;
                for (var j = 0; j < index - 1; j++) {
                    _centre(shape, way[j], out var x, out var y, out var z);
                    if (Legs.Clear(wall, shapes, cost, priced ? (int)highest[j] : Legs.AnyCost, priced ? worth[j] * Spare : Legs.AnySpend,
                            size.Radius, size.LegNeed, atX, atY, atZ, x, y, z)) {
                        next = j;
                        break;
                    }
                }
                index = next;
                _centre(shape, way[index], out atX, out atY, out atZ);
                _add(points, ref count, atX, atY, atZ);
            }
            // A route has a point. One that stops short at the very centre it starts from has that
            // centre for its one point.
            if (count == 0) { _add(points, ref count, atX, atY, atZ); }
            return new RouteResult(_fits(points, count), ends, count, expanded, spent);
        }

        // What a straight leg may cost as flown, as a share of what the part of the way it replaces
        // costs: a hair over all of it, for the rounding of the two sums.
        private const double Spare = 1d + 1e-6;

        // Writes, for each cell of the way from the one at an index on to the goal's, two things
        // about the part of the way from the cell at the index to it: the highest cost among its
        // cells, and what it costs as flown along the straight lines from centre to centre. Half
        // of each of those lines lies in each of its two cells. The way's cells are the goal's first.
        private static void _stretch(in GridShape shape, ReadOnlySpan<byte> cost, ReadOnlySpan<int> way, int index, Span<float> highest, Span<float> worth) {
            int countX = shape.CountX, countY = shape.CountY, layer = countX * countY;
            double cell = shape.CellSize;
            int most = cost[way[index]];
            var flown = 0d;
            highest[index] = most;
            worth[index] = 0f;
            for (var j = index - 1; j >= 0; j--) {
                int a = way[j + 1], b = way[j];
                int dx = b % countX - a % countX, dy = b / countX % countY - a / countX % countY, dz = b / layer - a / layer;
                flown += cell * Math.Sqrt(dx * dx + dy * dy + dz * dz) * (1d + (cost[a] + cost[b]) / 32d);
                if (cost[b] > most) { most = cost[b]; }
                highest[j] = most;
                worth[j] = (float)flown;
            }
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

        // The cell an end belongs to: its own if that is open, and otherwise the nearest open one,
        // within reach, whose centre it can see. Nearest first; among equals the lowest in x, then
        // y, then z.
        private static bool _end(in GridShape shape, in ShapeSet shapes, ReadOnlySpan<byte> region, float x, float y, float z, double reach, out int cell) {
            if (shape.TryCell(x, y, z, out var ownX, out var ownY, out var ownZ)) {
                cell = shape.Index(ownX, ownY, ownZ);
                if (region[cell] != 0) { return true; }
            }
            cell = -1;
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
                float centreX = 0f, centreY = 0f, centreZ = 0f;
                for (var cellX = fromX; cellX <= toX; cellX++) {
                    var cx = shape.Centre(shape.OriginX, cellX);
                    double dx = (double)cx - x;
                    for (var cellY = fromY; cellY <= toY; cellY++) {
                        var cy = shape.Centre(shape.OriginY, cellY);
                        double dy = (double)cy - y;
                        for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                            var at = shape.Index(cellX, cellY, cellZ);
                            if (region[at] == 0) { continue; }
                            var cz = shape.Centre(shape.OriginZ, cellZ);
                            double dz = (double)cz - z;
                            var squared = dx * dx + dy * dy + dz * dz;
                            if (!(squared <= farthest)) { continue; }
                            var order = ((long)cellX * shape.CountY + cellY) * shape.CountZ + cellZ;
                            if (squared < lastSquared || (squared == lastSquared && order <= lastOrder)) { continue; }
                            if (found >= 0 && (squared > best || (squared == best && order > bestOrder))) { continue; }
                            found = at;
                            best = squared;
                            bestOrder = order;
                            centreX = cx; centreY = cy; centreZ = cz;
                        }
                    }
                }
                if (found < 0) { return false; }
                if (!looks || Legs.Sees(shapes, shape.CellSize, x, y, z, centreX, centreY, centreZ)) {
                    cell = found;
                    return true;
                }
                lastSquared = best;
                lastOrder = bestOrder;
            }
        }

        // What a link's low bits say: which of the 27 cells round a cell it was reached from, from
        // 1, with 14 for the cell itself, which is the start. The bits above hold where the cell
        // sits in the heap, from 1, while it waits there; 0 once it has been taken up.
        private const int FromBits = 31;
        private const int Start = 14;
        private const int OnWay = 32;

        // The cheapest way from start to goal by steps between open cells of the start's region.
        // Returns how many cells it has, written into heap from 0 with the goal's first, or 0 if
        // there is none. total is the cost so far per cell; keys and heap are a heap of the cells
        // that wait, least key first; link is all zero before and after.
        // weight is what a metre still to go is reckoned at: the least a metre costs, times how
        // much over the cheapest the way may be. With no leeway the first time a cell is taken up
        // is by the cheapest way to it. With some it need not be, and the way found still costs
        // no more than that many times the cheapest: the estimate never falls faster from cell
        // to cell than that many times what the step costs.
        private static int _search(in WallMap wall, in SizeClass size, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, int start, int goal, double weight,
            Span<float> total, Span<float> keys, Span<int> link, Span<int> heap, out int expanded) {
            var shape = wall.Shape;
            ReadOnlySpan<byte> held = wall.Distance;
            int countX = shape.CountX, countY = shape.CountY, countZ = shape.CountZ, layer = countX * countY, cells = layer * countZ;
            double cell = shape.CellSize, plane = cell * Math.Sqrt(2d), space = cell * Math.Sqrt(3d);
            // The same three lengths at the least a metre costs, for the estimate.
            double leastCell = cell * weight, leastPlane = plane * weight, leastSpace = space * weight;
            var wanted = region[start];
            int goalX = goal % countX, goalY = goal / countX % countY, goalZ = goal / layer;

            var waiting = 0;
            var taken = 0;
            link[start] = Start;
            total[start] = 0f;
            _up(heap, keys, link, waiting++, start, (float)_estimate(start % countX - goalX, start / countX % countY - goalY, start / layer - goalZ, leastCell, leastPlane, leastSpace));
            var found = false;
            while (waiting > 0) {
                var current = heap[0];
                waiting--;
                if (waiting > 0) { _down(heap, keys, link, waiting, heap[waiting], keys[waiting]); }
                // Taken up: out of the heap, and onto the list that grows down from the far end of
                // it. The two never meet, as a cell is in one or the other.
                link[current] &= FromBits;
                heap[cells - 1 - taken] = current;
                taken++;
                if (current == goal) {
                    found = true;
                    break;
                }
                int cx = current % countX, cy = current / countX % countY, cz = current / layer;
                double so = total[current];
                for (var dz = -1; dz <= 1; dz++) {
                    if ((uint)(cz + dz) >= (uint)countZ) { continue; }
                    for (var dy = -1; dy <= 1; dy++) {
                        if ((uint)(cy + dy) >= (uint)countY) { continue; }
                        for (var dx = -1; dx <= 1; dx++) {
                            if ((uint)(cx + dx) >= (uint)countX || (dx | dy | dz) == 0) { continue; }
                            int stepX = dx, stepY = dy * countX, stepZ = dz * layer;
                            var next = current + stepX + stepY + stepZ;
                            if (!_open(region, cost, wanted, next)) { continue; }
                            var state = link[next];
                            var slot = (int)((uint)state >> 5) - 1;
                            if (state != 0 && slot < 0) { continue; }
                            var axes = dx * dx + dy * dy + dz * dz;
                            double length;
                            if (axes == 1) {
                                length = cell;
                            } else if (axes == 2) {
                                // Across a square: both ends far enough out, and the square's other two cells open.
                                if (held[current] < size.Plane || held[next] < size.Plane) { continue; }
                                int a = dx == 0 ? stepY : stepX, b = dz == 0 ? stepY : stepZ;
                                if (!_open(region, cost, wanted, current + a) || !_open(region, cost, wanted, current + b)) { continue; }
                                length = plane;
                            } else {
                                // Across a cube: both ends farther out still, and the cube's other six cells open.
                                if (held[current] < size.Space || held[next] < size.Space) { continue; }
                                if (!_open(region, cost, wanted, current + stepX) || !_open(region, cost, wanted, current + stepY) ||
                                    !_open(region, cost, wanted, current + stepZ) || !_open(region, cost, wanted, current + stepX + stepY) ||
                                    !_open(region, cost, wanted, current + stepX + stepZ) || !_open(region, cost, wanted, current + stepY + stepZ)) { continue; }
                                length = space;
                            }
                            // A step pays for the cell it enters.
                            var reached = (float)(so + (cost.Length == 0 ? length : length * (1d + cost[next] / 16d)));
                            // A link is only trusted to say a cell waits if the heap holds the cell there.
                            var waits = state != 0 && slot < waiting && heap[slot] == next;
                            if (waits && !(reached < total[next])) { continue; }
                            total[next] = reached;
                            var key = (float)(reached + _estimate(cx + dx - goalX, cy + dy - goalY, cz + dz - goalZ, leastCell, leastPlane, leastSpace));
                            // Reached from the cell on the other side of the step.
                            link[next] = (1 - dx) + 3 * (1 - dy) + 9 * (1 - dz) + 1;
                            _up(heap, keys, link, waits ? slot : waiting++, next, key);
                        }
                    }
                }
            }
            expanded = taken;

            // The cells that still wait are let go.
            for (var i = 0; i < waiting; i++) { link[heap[i]] = 0; }
            var steps = 0;
            if (found) {
                // The way's cells are marked, the goal's first, each by the one it was reached from.
                var at = goal;
                while (true) {
                    var from = link[at] & FromBits;
                    link[at] |= OnWay;
                    steps++;
                    if (from == Start || steps > taken) { break; }
                    at = _back(at, from, countX, layer);
                    // Only a link this search did not write can lead out of the cells it took up.
                    if ((uint)at >= (uint)cells || link[at] == 0 || (link[at] & OnWay) != 0) {
                        steps = 0;
                        break;
                    }
                }
                if (steps > taken) { steps = 0; }
            }
            // Every other cell taken up is let go, and then the way is written where the heap was.
            for (var i = 0; i < taken; i++) {
                var at = heap[cells - 1 - i];
                if (steps == 0 || (link[at] & OnWay) == 0) { link[at] = 0; }
            }
            if (steps > 0) {
                var at = goal;
                for (var i = 0; i < steps; i++) {
                    var from = link[at] & FromBits;
                    link[at] = 0;
                    heap[i] = at;
                    at = _back(at, from, countX, layer);
                }
            }
            return steps;
        }

        // The cell a link says a cell was reached from.
        private static int _back(int cell, int from, int countX, int layer) {
            var code = from - 1;
            return cell + (code % 3 - 1) + (code / 3 % 3 - 1) * countX + (code / 9 - 1) * layer;
        }

        // Open, of the wanted region, and not closed by cost since the regions were made.
        private static bool _open(ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, byte wanted, int cell) =>
            region[cell] == wanted && (cost.Length == 0 || cost[cell] != CellCost.Closed);

        // The least a way between two cells can cost: the length of the shortest way by steps
        // through a grid with nothing in it. Given how many cells apart they are on each axis.
        private static double _estimate(int dx, int dy, int dz, double cell, double plane, double space) {
            dx = dx < 0 ? -dx : dx; dy = dy < 0 ? -dy : dy; dz = dz < 0 ? -dz : dz;
            int low = dx < dy ? dx : dy, high = dx < dy ? dy : dx;
            int least = low < dz ? low : dz, most = high > dz ? high : dz;
            var middle = dx + dy + dz - least - most;
            return (most - middle) * cell + (middle - least) * plane + least * space;
        }

        // Puts a cell at a place in the heap or above it, by its key.
        private static void _up(Span<int> heap, Span<float> keys, Span<int> link, int slot, int cell, float key) {
            while (slot > 0) {
                var above = (slot - 1) >> 1;
                if (!(key < keys[above])) { break; }
                var moved = heap[above];
                heap[slot] = moved;
                keys[slot] = keys[above];
                link[moved] = (link[moved] & FromBits) | ((slot + 1) << 5);
                slot = above;
            }
            heap[slot] = cell;
            keys[slot] = key;
            link[cell] = (link[cell] & FromBits) | ((slot + 1) << 5);
        }

        // Puts a cell at the top of a heap of count cells or below it, by its key.
        private static void _down(Span<int> heap, Span<float> keys, Span<int> link, int count, int cell, float key) {
            var slot = 0;
            while (true) {
                var below = 2 * slot + 1;
                if (below >= count) { break; }
                if (below + 1 < count && keys[below + 1] < keys[below]) { below++; }
                if (!(keys[below] < key)) { break; }
                var moved = heap[below];
                heap[slot] = moved;
                keys[slot] = keys[below];
                link[moved] = (link[moved] & FromBits) | ((slot + 1) << 5);
                slot = below;
            }
            heap[slot] = cell;
            keys[slot] = key;
            link[cell] = (link[cell] & FromBits) | ((slot + 1) << 5);
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
