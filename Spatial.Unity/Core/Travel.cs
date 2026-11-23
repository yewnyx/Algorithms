using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Builds a <see cref="TravelField"/> and says, at any point, how far the field is from where it
    /// starts and which way leads down it.
    /// </summary>
    /// <remarks>
    /// A field spreads from its seeds through the cells of a region array (see <see cref="Regions"/>),
    /// by steps between cells that share a face, so it goes exactly where a body of that size class
    /// can, and never across a diagonal. Distances are worked out by fast marching: a cell's is found
    /// from its settled neighbours along all three axes at once, so it is close to the straight-line
    /// distance through open space and not the longer distance along the grid's axes.
    /// A step pays for the cell it enters: a cell whose cost is c is 1 + c / 16 times as far to cross.
    /// Nothing is allocated and nothing throws. Times are rounded to single at each step, so every
    /// runtime builds the same field.
    /// </remarks>
    public static class Travel {
        /// <summary>The count of a cell the field does not reach.</summary>
        public const ushort Unreached = ushort.MaxValue;

        /// <summary>The length of the times <see cref="Build"/> needs: one per cell.</summary>
        public static int TimesLength(in GridShape shape) => shape.CellCount;

        /// <summary>The length of the heap <see cref="Build"/> needs: two ints per cell, or 0 if that is more than an int holds.</summary>
        public static int HeapLength(in GridShape shape) => shape.CellCount <= int.MaxValue / 2 ? 2 * shape.CellCount : 0;

        /// <summary>
        /// Builds a field: every cell that can be reached from a seed, through cells whose region is
        /// not 0 and whose cost is not <see cref="CellCost.Closed"/>, gets its travel distance from
        /// where the seeds start, as a count of <paramref name="unit"/>, rounded to the nearest.
        /// Every other cell gets <see cref="Unreached"/>.
        /// </summary>
        /// <remarks>
        /// A seed starts from the cells it can enter whose centres are inside its solid and that have
        /// a straight way to where it starts from: every cell on the line from the centre to the
        /// seed's point, or square to its plane, can be entered. (The last cell's length before a
        /// point is let off, since a goal may itself be too near a wall for its cell to be open.)
        /// A cell behind the plane always starts. The cells of the solid that are left out are
        /// reached from the others, so none is left as a low place with no way on. A seed that
        /// starts no cell starts nothing: the field then reaches nothing, and
        /// <paramref name="reached"/> is 0. Where seeds overlap, a cell starts at the least of what
        /// they give it.
        /// A distance of more than 65,534 counts does not fit: such a cell is left unreached and
        /// counted in <paramref name="cut"/>, never held at the largest count, which would leave a
        /// flat stretch with no way down. <paramref name="reached"/> is how many cells got a distance.
        /// <paramref name="cost"/> may be empty, meaning every cell is normal. <paramref name="distance"/>
        /// needs one count per cell, <paramref name="times"/> <see cref="TimesLength"/> and
        /// <paramref name="heap"/> <see cref="HeapLength"/>; the last two are only scratch.
        /// Returns false if the shape cannot be used, the unit is not above zero or is more than a
        /// quarter of a cell (a coarser one could give two neighbours the same count), a buffer is
        /// too short, the cost array is neither empty nor one byte per cell, or a seed may not be
        /// used: its solid may not be stored (see <see cref="Shapes.Build"/>), or its point or its
        /// plane's normal is not finite, or the normal has no length. The grid's cells in
        /// <paramref name="distance"/> are then all <see cref="Unreached"/>.
        /// </remarks>
        public static bool Build(in GridShape shape, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, ReadOnlySpan<TravelSeed> seeds, float unit,
            Span<ushort> distance, Span<float> times, Span<int> heap, out int reached, out int cut) {
            reached = 0;
            cut = 0;
            var cells = shape.CellCount;
            var heapLength = HeapLength(shape);
            if (cells == 0 || heapLength == 0 || !(unit > 0f) || !(unit <= (float)(shape.CellSize * 0.25f)) || region.Length < cells ||
                (cost.Length != 0 && cost.Length < cells) || distance.Length < cells || times.Length < cells || heap.Length < heapLength) {
                (cells > 0 && distance.Length > cells ? distance.Slice(0, cells) : distance).Fill(Unreached);
                return false;
            }
            distance = distance.Slice(0, cells);
            times = times.Slice(0, cells);
            // The heap holds cells, least time first; place says where in it each cell is.
            Span<int> queue = heap.Slice(0, cells), place = heap.Slice(cells, cells);
            times.Fill(float.PositiveInfinity);
            place.Fill(Far);
            var held = 0;

            double size = shape.CellSize;
            int countX = shape.CountX, countY = shape.CountY, countZ = shape.CountZ, layer = countX * countY;
            for (var seed = 0; seed < seeds.Length; seed++) {
                var from = seeds[seed];
                if (!ShapeDistance.TryBox(from.Solid, out var box) || !_usable(from)) {
                    distance.Fill(Unreached);
                    return false;
                }
                int fromX = _first(box.MinX, shape.OriginX, size, countX), toX = _last(box.MaxX, shape.OriginX, size, countX);
                int fromY = _first(box.MinY, shape.OriginY, size, countY), toY = _last(box.MaxY, shape.OriginY, size, countY);
                int fromZ = _first(box.MinZ, shape.OriginZ, size, countZ), toZ = _last(box.MaxZ, shape.OriginZ, size, countZ);
                for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                    var centreZ = shape.Centre(shape.OriginZ, cellZ);
                    for (var cellY = fromY; cellY <= toY; cellY++) {
                        var centreY = shape.Centre(shape.OriginY, cellY);
                        for (var cellX = fromX; cellX <= toX; cellX++) {
                            var cell = shape.Index(cellX, cellY, cellZ);
                            if (!_passable(region, cost, cell)) { continue; }
                            var centreX = shape.Centre(shape.OriginX, cellX);
                            if (ShapeDistance.SolidSquared(from.Solid, centreX, centreY, centreZ) != 0d) { continue; }
                            if (!_clearWay(shape, region, cost, from, centreX, centreY, centreZ)) { continue; }
                            var start = _start(from, centreX, centreY, centreZ);
                            if (start < times[cell]) { held = _lower(times, queue, place, held, cell, start); }
                        }
                    }
                }
            }

            // Fast marching: the cell with the least time is settled, and each of its neighbours that
            // is not is worked out again from the settled cells beside it.
            while (held > 0) {
                var cell = _pop(times, queue, place, ref held);
                int cellX = cell % countX, cellY = cell / countX % countY, cellZ = cell / layer;
                if (cellX > 0) { held = _reach(shape, region, cost, times, queue, place, held, cell - 1, cellX - 1, cellY, cellZ, layer); }
                if (cellX < countX - 1) { held = _reach(shape, region, cost, times, queue, place, held, cell + 1, cellX + 1, cellY, cellZ, layer); }
                if (cellY > 0) { held = _reach(shape, region, cost, times, queue, place, held, cell - countX, cellX, cellY - 1, cellZ, layer); }
                if (cellY < countY - 1) { held = _reach(shape, region, cost, times, queue, place, held, cell + countX, cellX, cellY + 1, cellZ, layer); }
                if (cellZ > 0) { held = _reach(shape, region, cost, times, queue, place, held, cell - layer, cellX, cellY, cellZ - 1, layer); }
                if (cellZ < countZ - 1) { held = _reach(shape, region, cost, times, queue, place, held, cell + layer, cellX, cellY, cellZ + 1, layer); }
            }

            int stored = 0, over = 0;
            for (var cell = 0; cell < cells; cell++) {
                if (place[cell] != Settled) {
                    distance[cell] = Unreached;
                    continue;
                }
                // To the nearest count, a half going up: written out, since runtimes differ on how they round a half.
                var counts = Math.Floor((double)times[cell] / unit + 0.5d);
                if (counts < Unreached) {
                    distance[cell] = (ushort)counts;
                    stored++;
                } else {
                    distance[cell] = Unreached;
                    over++;
                }
            }
            reached = stored;
            cut = over;
            return true;
        }

        /// <summary>
        /// The travel distance at a point, in metres, and the direction that leads down the field
        /// from there, of length 1.
        /// </summary>
        /// <remarks>
        /// The distance is blended between the eight cells whose centres surround the point. A cell
        /// of those that the field does not reach counts as one cell farther than the farthest that
        /// it does, so the slope leads away from it.
        /// The direction is down that slope where it truly leads down: where the blended distance
        /// half a cell along it is lower by a tenth of a cell or more. Beside cells the field does
        /// not reach, the blend can point back and forth; there the direction is instead towards the
        /// centre of the lowest of the six cells that share a face with the nearest reached cell.
        /// Every reached cell that no seed started has such a lower neighbour. A started cell may
        /// not, so where none of the six is lower the lowest of all 26 cells round it is taken.
        /// Where none of those is lower either, which is where the field starts, the direction is
        /// the slope's if there is one, and otherwise 0, 0, 0.
        /// At a goal the direction never settles: it turns about as a follower passes the lowest
        /// cell, which can be most of a cell from the goal itself. A follower has arrived when the
        /// distance falls below about a cell, not when the direction goes to nothing.
        /// The point need not be inside the grid. Returns false, with every output 0, if none of the
        /// eight cells is reached, the point is not a number, or the field cannot be used.
        /// </remarks>
        public static bool Sample(in TravelField field, float x, float y, float z,
            out float directionX, out float directionY, out float directionZ, out float distance) {
            directionX = 0f; directionY = 0f; directionZ = 0f; distance = 0f;
            if (!field.IsValid || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return false; }
            var shape = field.Shape;
            double size = shape.CellSize;
            if (!_blend(field, x, y, z, out var here, out var slopeX, out var slopeY, out var slopeZ)) { return false; }
            distance = (float)here;

            var steep = slopeX * slopeX + slopeY * slopeY + slopeZ * slopeZ;
            double downX = 0d, downY = 0d, downZ = 0d;
            var sloped = steep > Flat;
            if (sloped) {
                var length = Math.Sqrt(steep);
                // From 0, so that a slope of nothing along an axis gives 0 and not minus 0.
                downX = 0d - slopeX / length; downY = 0d - slopeY / length; downZ = 0d - slopeZ / length;
                var half = 0.5d * size;
                if (_blend(field, x + downX * half, y + downY * half, z + downZ * half, out var ahead, out _, out _, out _) && ahead < here - 0.1d * size) {
                    directionX = (float)downX; directionY = (float)downY; directionZ = (float)downZ;
                    return true;
                }
            }
            if (_stepDown(field, x, y, z, out var towardX, out var towardY, out var towardZ)) {
                directionX = (float)towardX; directionY = (float)towardY; directionZ = (float)towardZ;
                return true;
            }
            if (sloped) {
                directionX = (float)downX; directionY = (float)downY; directionZ = (float)downZ;
            }
            return true;
        }

        // A cell's place in the heap, or one of these.
        private const int Far = -1, Settled = -2;

        // A slope below this, squared, is no slope.
        private const double Flat = 1e-12;

        private static bool _passable(ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, int cell) =>
            region[cell] != 0 && (cost.Length == 0 || cost[cell] != CellCost.Closed);

        private static bool _usable(in TravelSeed seed) {
            var sum = (double)seed.X + seed.Y + seed.Z;
            if (!(sum - sum == 0d)) { return false; }
            if (seed.Start == TravelStart.FromPoint) { return true; }
            if (seed.Start != TravelStart.FromPlane) { return false; }
            var length = (double)seed.NormalX * seed.NormalX + (double)seed.NormalY * seed.NormalY + (double)seed.NormalZ * seed.NormalZ;
            return length > 0d && length - length == 0d;
        }

        // What a cell inside a seed starts at.
        private static float _start(in TravelSeed seed, float x, float y, float z) {
            double dx = (double)x - seed.X, dy = (double)y - seed.Y, dz = (double)z - seed.Z;
            if (seed.Start == TravelStart.FromPoint) { return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
            double nx = seed.NormalX, ny = seed.NormalY, nz = seed.NormalZ;
            var ahead = (dx * nx + dy * ny + dz * nz) / Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return ahead > 0d ? (float)ahead : 0f;
        }

        // Whether the straight way from a cell's centre to where a seed starts from lies through
        // cells that can be entered: to the seed's point, or square to its plane. Looked at every
        // quarter of a cell. Before a point the last cell's length is let off, since the point may
        // be nearer a wall than a cell can be; before a plane, only the last quarter of a cell.
        // A cell behind the plane has no way to go and always passes.
        private static bool _clearWay(in GridShape shape, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, in TravelSeed seed, double x, double y, double z) {
            double size = shape.CellSize, toX = seed.X - x, toY = seed.Y - y, toZ = seed.Z - z, letOff = size;
            if (seed.Start == TravelStart.FromPlane) {
                double nx = seed.NormalX, ny = seed.NormalY, nz = seed.NormalZ;
                var squared = nx * nx + ny * ny + nz * nz;
                // How far the centre is in front of the plane, as a part of the normal's length.
                var ahead = -(toX * nx + toY * ny + toZ * nz) / squared;
                if (!(ahead > 0d)) { return true; }
                toX = -ahead * nx; toY = -ahead * ny; toZ = -ahead * nz;
                letOff = 0.25d * size;
            }
            var length = Math.Sqrt(toX * toX + toY * toY + toZ * toZ);
            var step = 0.25d * size;
            var steps = Math.Floor((length - letOff) / step);
            for (var k = 1; k <= steps; k++) {
                var along = k * step / length;
                double atX = Math.Floor((x + toX * along - shape.OriginX) / size), atY = Math.Floor((y + toY * along - shape.OriginY) / size), atZ = Math.Floor((z + toZ * along - shape.OriginZ) / size);
                if (!(atX >= 0d && atX < shape.CountX && atY >= 0d && atY < shape.CountY && atZ >= 0d && atZ < shape.CountZ)) { return false; }
                if (!_passable(region, cost, shape.Index((int)atX, (int)atY, (int)atZ))) { return false; }
            }
            return true;
        }

        // Works a cell out again from the settled cells beside it, and takes the time if it is lower.
        private static int _reach(in GridShape shape, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost, Span<float> times, Span<int> queue, Span<int> place,
            int held, int cell, int cellX, int cellY, int cellZ, int layer) {
            if (place[cell] == Settled || !_passable(region, cost, cell)) { return held; }
            // The least settled time on each axis, or infinity where neither neighbour is settled.
            var a = _least(times, place, cell, cellX > 0, cellX < shape.CountX - 1, 1);
            var b = _least(times, place, cell, cellY > 0, cellY < shape.CountY - 1, shape.CountX);
            var c = _least(times, place, cell, cellZ > 0, cellZ < shape.CountZ - 1, layer);
            float swap;
            if (a > b) { swap = a; a = b; b = swap; }
            if (b > c) { swap = b; b = c; c = swap; }
            if (a > b) { swap = a; a = b; b = swap; }
            // The step into this cell: its size, times what the cell costs to cross.
            var step = cost.Length == 0 ? shape.CellSize : (float)(shape.CellSize * (float)(1f + (float)(cost[cell] * 0.0625f)));
            var time = _solve(a, b, c, step);
            return time < times[cell] ? _lower(times, queue, place, held, cell, time) : held;
        }

        private static float _least(ReadOnlySpan<float> times, ReadOnlySpan<int> place, int cell, bool low, bool high, int stride) {
            var least = float.PositiveInfinity;
            if (low && place[cell - stride] == Settled) { least = times[cell - stride]; }
            if (high && place[cell + stride] == Settled && times[cell + stride] < least) { least = times[cell + stride]; }
            return least;
        }

        // The largest t with (t - a)^2 + (t - b)^2 + (t - c)^2 = step^2, counting only the terms
        // whose neighbour is below t: one, two or all three, with a <= b <= c. Written from a, so
        // that a long way from the seed nothing is lost to rounding. Each step is rounded to single.
        private static float _solve(float a, float b, float c, float step) {
            var one = (float)(a + step);
            if (one <= b) { return one; }
            var db = (float)(b - a);
            var two = (float)(a + (float)((float)(db + _root((float)((float)(2f * (float)(step * step)) - (float)(db * db)))) * 0.5f));
            if (two <= c) { return two; }
            float dc = (float)(c - a), between = (float)(dc - db);
            var spread = (float)((float)((float)(db * db) + (float)(dc * dc)) + (float)(between * between));
            return (float)(a + (float)((float)((float)(db + dc) + _root((float)((float)(3f * (float)(step * step)) - spread))) / 3f));
        }

        private static float _root(float value) => value > 0f ? (float)Math.Sqrt(value) : 0f;

        // Gives a cell a lower time: puts it in the heap if it is not there, and moves it up.
        private static int _lower(Span<float> times, Span<int> queue, Span<int> place, int held, int cell, float time) {
            times[cell] = time;
            var at = place[cell];
            if (at < 0) { at = held++; }
            while (at > 0) {
                var up = (at - 1) >> 1;
                var above = queue[up];
                if (times[above] <= time) { break; }
                queue[at] = above;
                place[above] = at;
                at = up;
            }
            queue[at] = cell;
            place[cell] = at;
            return held;
        }

        // Takes the cell with the least time out of the heap and settles it.
        private static int _pop(ReadOnlySpan<float> times, Span<int> queue, Span<int> place, ref int held) {
            var top = queue[0];
            place[top] = Settled;
            var last = queue[--held];
            if (held == 0) { return top; }
            var time = times[last];
            var at = 0;
            while (true) {
                var child = 2 * at + 1;
                if (child >= held) { break; }
                if (child + 1 < held && times[queue[child + 1]] < times[queue[child]]) { child++; }
                if (times[queue[child]] >= time) { break; }
                queue[at] = queue[child];
                place[queue[at]] = at;
                at = child;
            }
            queue[at] = last;
            place[last] = at;
            return top;
        }

        // The distance at a point, in metres, blended between the eight cells whose centres surround
        // it, and its slope per metre. A cell the field does not reach, or one outside the grid,
        // counts as a cell farther than the farthest reached of the eight. False if none is reached.
        private static bool _blend(in TravelField field, double x, double y, double z, out double value, out double slopeX, out double slopeY, out double slopeZ) {
            value = 0d; slopeX = 0d; slopeY = 0d; slopeZ = 0d;
            var shape = field.Shape;
            double size = shape.CellSize, unit = field.Unit;
            double atX = (x - shape.OriginX) / size - 0.5d, atY = (y - shape.OriginY) / size - 0.5d, atZ = (z - shape.OriginZ) / size - 0.5d;
            double lowX = Math.Floor(atX), lowY = Math.Floor(atY), lowZ = Math.Floor(atZ);
            // Far outside the grid none of the eight is in it; this also keeps the casts below in range.
            if (!(lowX >= -1d && lowX < shape.CountX && lowY >= -1d && lowY < shape.CountY && lowZ >= -1d && lowZ < shape.CountZ)) { return false; }
            double fx = atX - lowX, fy = atY - lowY, fz = atZ - lowZ;
            int cellX = (int)lowX, cellY = (int)lowY, cellZ = (int)lowZ;
            ReadOnlySpan<ushort> held = field.Distance;
            Span<double> corner = stackalloc double[8];
            var any = false;
            var farthest = 0d;
            for (var k = 0; k < 8; k++) {
                int cx = cellX + (k & 1), cy = cellY + ((k >> 1) & 1), cz = cellZ + (k >> 2);
                var at = -1d;
                if ((uint)cx < (uint)shape.CountX && (uint)cy < (uint)shape.CountY && (uint)cz < (uint)shape.CountZ) {
                    var count = held[shape.Index(cx, cy, cz)];
                    if (count != Unreached) { at = count * unit; }
                }
                corner[k] = at;
                if (at >= 0d) {
                    any = true;
                    if (at > farthest) { farthest = at; }
                }
            }
            if (!any) { return false; }
            for (var k = 0; k < 8; k++) {
                if (corner[k] < 0d) { corner[k] = farthest + size; }
            }
            double gx = 1d - fx, gy = 1d - fy, gz = 1d - fz;
            // Corner k is x + 2 y + 4 z.
            slopeX = (gy * gz * (corner[1] - corner[0]) + fy * gz * (corner[3] - corner[2]) + gy * fz * (corner[5] - corner[4]) + fy * fz * (corner[7] - corner[6])) / size;
            slopeY = (gx * gz * (corner[2] - corner[0]) + fx * gz * (corner[3] - corner[1]) + gx * fz * (corner[6] - corner[4]) + fx * fz * (corner[7] - corner[5])) / size;
            slopeZ = (gx * gy * (corner[4] - corner[0]) + fx * gy * (corner[5] - corner[1]) + gx * fy * (corner[6] - corner[2]) + fx * fy * (corner[7] - corner[3])) / size;
            value = gz * (gy * (gx * corner[0] + fx * corner[1]) + fy * (gx * corner[2] + fx * corner[3])) +
                    fz * (gy * (gx * corner[4] + fx * corner[5]) + fy * (gx * corner[6] + fx * corner[7]));
            return true;
        }

        // The direction from a point towards the centre of the lowest cell that shares a face with
        // the reached cell nearest the point (the point's own cell, or one of the 26 round it); or,
        // where none of those six is lower, of the lowest of all 26 round it.
        // False if none of those is reached, or the nearest has no lower neighbour at all.
        private static bool _stepDown(in TravelField field, double x, double y, double z, out double towardX, out double towardY, out double towardZ) {
            towardX = 0d; towardY = 0d; towardZ = 0d;
            var shape = field.Shape;
            double size = shape.CellSize;
            double ownX = Math.Floor((x - shape.OriginX) / size), ownY = Math.Floor((y - shape.OriginY) / size), ownZ = Math.Floor((z - shape.OriginZ) / size);
            if (!(ownX >= -1d && ownX <= shape.CountX && ownY >= -1d && ownY <= shape.CountY && ownZ >= -1d && ownZ <= shape.CountZ)) { return false; }
            ReadOnlySpan<ushort> held = field.Distance;
            int fromX = -1, fromY = -1, fromZ = -1;
            var nearest = double.MaxValue;
            // Lowest x first, then y, then z, and only a nearer cell replaces the one held.
            for (var cx = (int)ownX - 1; cx <= (int)ownX + 1; cx++) {
                for (var cy = (int)ownY - 1; cy <= (int)ownY + 1; cy++) {
                    for (var cz = (int)ownZ - 1; cz <= (int)ownZ + 1; cz++) {
                        if ((uint)cx >= (uint)shape.CountX || (uint)cy >= (uint)shape.CountY || (uint)cz >= (uint)shape.CountZ) { continue; }
                        if (held[shape.Index(cx, cy, cz)] == Unreached) { continue; }
                        double dx = shape.Centre(shape.OriginX, cx) - x, dy = shape.Centre(shape.OriginY, cy) - y, dz = shape.Centre(shape.OriginZ, cz) - z;
                        var squared = dx * dx + dy * dy + dz * dz;
                        if (squared < nearest) {
                            nearest = squared;
                            fromX = cx; fromY = cy; fromZ = cz;
                        }
                    }
                }
            }
            if (fromX < 0) { return false; }
            int lowest = held[shape.Index(fromX, fromY, fromZ)], toX = fromX, toY = fromY, toZ = fromZ;
            var found = false;
            for (var side = 0; side < 6; side++) {
                int cx = fromX + (side == 0 ? -1 : side == 1 ? 1 : 0), cy = fromY + (side == 2 ? -1 : side == 3 ? 1 : 0), cz = fromZ + (side == 4 ? -1 : side == 5 ? 1 : 0);
                if ((uint)cx >= (uint)shape.CountX || (uint)cy >= (uint)shape.CountY || (uint)cz >= (uint)shape.CountZ) { continue; }
                int count = held[shape.Index(cx, cy, cz)];
                if (count == Unreached || count >= lowest) { continue; }
                lowest = count;
                toX = cx; toY = cy; toZ = cz;
                found = true;
            }
            if (!found) {
                // A cell a seed started can have nothing lower among the six: then any of the 26.
                // Lowest x first, then y, then z, and only a lower cell replaces the one held.
                for (var cx = fromX - 1; cx <= fromX + 1; cx++) {
                    for (var cy = fromY - 1; cy <= fromY + 1; cy++) {
                        for (var cz = fromZ - 1; cz <= fromZ + 1; cz++) {
                            if ((uint)cx >= (uint)shape.CountX || (uint)cy >= (uint)shape.CountY || (uint)cz >= (uint)shape.CountZ) { continue; }
                            int count = held[shape.Index(cx, cy, cz)];
                            if (count == Unreached || count >= lowest) { continue; }
                            lowest = count;
                            toX = cx; toY = cy; toZ = cz;
                            found = true;
                        }
                    }
                }
            }
            if (!found) { return false; }
            double wayX = shape.Centre(shape.OriginX, toX) - x, wayY = shape.Centre(shape.OriginY, toY) - y, wayZ = shape.Centre(shape.OriginZ, toZ) - z;
            var length = wayX * wayX + wayY * wayY + wayZ * wayZ;
            if (!(length > Flat)) { return false; }
            length = Math.Sqrt(length);
            towardX = wayX / length; towardY = wayY / length; towardZ = wayZ / length;
            return true;
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
