using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Which cells of a grid a body of one size can be in, and which of them can be reached from
    /// which: one byte per cell, 0 for a cell that is not open or that no seed reaches, and otherwise
    /// the number of the region the cell belongs to.
    /// </summary>
    /// <remarks>
    /// A cell is open for a <see cref="SizeClass"/> when its centre is far enough from every shape
    /// and its cost is not <see cref="CellCost.Closed"/>. Two open cells are in one region when a
    /// path of steps between cells that share a face joins them: never a diagonal step, so two rooms
    /// that touch only at a corner are two regions, and a sheet with no thickness is never crossed.
    /// Region numbers are the library's own. Callers ask of points: is this point open, and which
    /// is the nearest open point, in any region or in the same one as another point.
    /// "Open" is said of cells, not of points: a body at an open cell's centre fits, and a point
    /// in that cell can be most of a cell from the centre, so nearer a shape than the radius. To
    /// ask whether a body fits at a point itself, compare <see cref="WallDistance.RoomAt"/> or
    /// <see cref="Shapes.Distance"/> there with its radius.
    /// Nothing is allocated and nothing throws. After a false return the grid's cells in the region
    /// array hold 0, which says no cell is open.
    /// </remarks>
    public static class Regions {
        /// <summary>The most regions a grid can hold. A flood that would need more is refused.</summary>
        public const int Most = 254;

        /// <summary>The length of the queue <see cref="FromSeeds"/> needs: one int per cell.</summary>
        public static int QueueLength(in GridShape shape) => shape.CellCount;

        /// <summary>
        /// Floods from the open cells whose centres are inside each seed, through open cells that
        /// share a face. Every cell reached gets its region's number, from 1; seeds that turn out to
        /// be joined share one. Cells that no seed reaches are left at 0, open or not.
        /// </summary>
        /// <remarks>
        /// <paramref name="reached"/> is how many cells got a number, and <paramref name="empty"/>
        /// how many seeds held no open cell: such a seed reaches nothing, and is not moved to look
        /// for one. <paramref name="cost"/> may be empty, meaning no cell is closed.
        /// <paramref name="region"/> needs one byte per cell and <paramref name="queue"/>
        /// <see cref="QueueLength"/> ints.
        /// Returns false if the map cannot be used, the class does not fit it (see
        /// <see cref="SizeClass.Fits(in WallMap)"/>), a buffer is too short, the cost array is neither empty nor
        /// one byte per cell, a seed may not be stored (see <see cref="Shapes.Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/>), or the
        /// seeds reach more than <see cref="Most"/> regions. The grid's cells in the region array
        /// are then 0. (If the number of cells is not known, because the map cannot be used, or the
        /// array is shorter than that, all of it is set to 0.)
        /// </remarks>
        public static bool FromSeeds(in WallMap wall, in SizeClass size, ReadOnlySpan<byte> cost, ReadOnlySpan<Solid> seeds,
            Span<byte> region, Span<int> queue, out int reached, out int empty) {
            reached = 0;
            empty = 0;
            var shape = wall.Shape;
            var cells = shape.CellCount;
            if (!wall.IsValid || !size.Fits(wall) || region.Length < cells || queue.Length < cells || (cost.Length != 0 && cost.Length < cells)) {
                (cells > 0 && region.Length > cells ? region.Slice(0, cells) : region).Clear();
                return false;
            }
            region = region.Slice(0, cells);
            region.Clear();

            ReadOnlySpan<byte> held = wall.Distance;
            double cellSize = shape.CellSize;
            int countX = shape.CountX, countY = shape.CountY, countZ = shape.CountZ, layer = countX * countY;
            int regions = 0, total = 0, without = 0;
            for (var seed = 0; seed < seeds.Length; seed++) {
                var solid = seeds[seed];
                if (!ShapeDistance.TryBox(solid, out var box)) {
                    region.Clear();
                    return false;
                }
                // The cells whose centres can be inside the seed.
                int fromX = _first(box.MinX, shape.OriginX, cellSize, countX), toX = _last(box.MaxX, shape.OriginX, cellSize, countX);
                int fromY = _first(box.MinY, shape.OriginY, cellSize, countY), toY = _last(box.MaxY, shape.OriginY, cellSize, countY);
                int fromZ = _first(box.MinZ, shape.OriginZ, cellSize, countZ), toZ = _last(box.MaxZ, shape.OriginZ, cellSize, countZ);
                var found = false;
                for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                    double centreZ = shape.Centre(shape.OriginZ, cellZ);
                    for (var cellY = fromY; cellY <= toY; cellY++) {
                        double centreY = shape.Centre(shape.OriginY, cellY);
                        for (var cellX = fromX; cellX <= toX; cellX++) {
                            var start = shape.Index(cellX, cellY, cellZ);
                            if (!_open(held, cost, size.Cell, start)) { continue; }
                            if (ShapeDistance.SolidSquared(solid, shape.Centre(shape.OriginX, cellX), centreY, centreZ) != 0d) { continue; }
                            found = true;
                            // Already reached, from this seed or an earlier one: the same region.
                            if (region[start] != 0) { continue; }
                            if (regions == Most) {
                                region.Clear();
                                reached = 0;
                                empty = 0;
                                return false;
                            }
                            total += _flood(held, cost, size.Cell, region, queue, start, (byte)++regions, countX, countY, countZ, layer);
                        }
                    }
                }
                if (!found) { without++; }
            }
            reached = total;
            empty = without;
            return true;
        }

        /// <summary>
        /// The least cost among the cells that have a region. No way through the regions enters a
        /// cell that costs less, which is what <see cref="Routes.Find"/> wants to know.
        /// </summary>
        /// <remarks>
        /// 0 if <paramref name="cost"/> is empty, no cell has a region, the shape cannot be used
        /// or an array is shorter than one byte per cell.
        /// </remarks>
        public static byte LeastCost(in GridShape shape, ReadOnlySpan<byte> region, ReadOnlySpan<byte> cost) {
            var cells = shape.CellCount;
            if (cells == 0 || cost.Length < cells || region.Length < cells) { return 0; }
            var least = -1;
            for (var i = 0; i < cells; i++) {
                if (region[i] != 0 && (least < 0 || cost[i] < least)) {
                    least = cost[i];
                    if (least == 0) { break; }
                }
            }
            return least < 0 ? (byte)0 : (byte)least;
        }

        /// <summary>
        /// Whether the cell that holds a point is open and was reached by a seed. False if the point
        /// is outside the grid or not a number, the shape cannot be used, or the region array is
        /// shorter than one byte per cell.
        /// </summary>
        /// <remarks>
        /// It is the cell that is open, not the point: a body at the cell's centre fits, and the point
        /// can be most of a cell from there. With cells of half a metre and a radius of 0.51 m, a
        /// point for which this is true can be 0.08 m from a wall.
        /// </remarks>
        public static bool IsOpen(in GridShape shape, ReadOnlySpan<byte> region, float x, float y, float z) =>
            shape.TryCell(x, y, z, out var cellX, out var cellY, out var cellZ) && region.Length >= shape.CellCount &&
            region[shape.Index(cellX, cellY, cellZ)] != 0;

        /// <summary>
        /// The centre of the nearest cell that is open and was reached by a seed, no farther than
        /// <paramref name="reach"/> from the point, in a straight line. Among centres equally near,
        /// the lowest in x, then in y, then in z.
        /// </summary>
        /// <remarks>
        /// Nearest is by straight line, whatever lies between: the cell can be on the other side of
        /// a thin wall or under a floor. A caller that needs to get there has to find a way.
        /// The point need not be inside the grid. Returns false if there is no such cell, the point
        /// or the reach is not a number, the reach is negative, the shape cannot be used, or the
        /// region array is shorter than one byte per cell.
        /// </remarks>
        public static bool NearestOpen(in GridShape shape, ReadOnlySpan<byte> region, float x, float y, float z, float reach,
            out float openX, out float openY, out float openZ) =>
            _nearest(shape, region, x, y, z, reach, 0, out openX, out openY, out openZ);

        /// <summary>
        /// As <see cref="NearestOpen(in GridShape, ReadOnlySpan{byte}, float, float, float, float, out float, out float, out float)"/>,
        /// but only among the cells in the same region as the cell that holds another point: the
        /// nearest place that can be reached from there.
        /// </summary>
        /// <remarks>
        /// Still by straight line: where both sides of a thin wall are one region, joined some other
        /// way, the cell can be on the far side. Also returns false if the other point is not in an
        /// open cell that a seed reached.
        /// </remarks>
        public static bool NearestOpen(in GridShape shape, ReadOnlySpan<byte> region, float x, float y, float z, float reach,
            float likeX, float likeY, float likeZ, out float openX, out float openY, out float openZ) {
            openX = 0f; openY = 0f; openZ = 0f;
            if (!shape.TryCell(likeX, likeY, likeZ, out var cellX, out var cellY, out var cellZ) || region.Length < shape.CellCount) { return false; }
            var wanted = region[shape.Index(cellX, cellY, cellZ)];
            return wanted != 0 && _nearest(shape, region, x, y, z, reach, wanted, out openX, out openY, out openZ);
        }

        // The nearest cell of the wanted region, or of any region when wanted is 0.
        private static bool _nearest(in GridShape shape, ReadOnlySpan<byte> region, float x, float y, float z, float reach, byte wanted,
            out float openX, out float openY, out float openZ) {
            openX = 0f; openY = 0f; openZ = 0f;
            var cells = shape.CellCount;
            if (cells == 0 || region.Length < cells || !(reach >= 0f) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return false; }
            double size = shape.CellSize, far = reach;
            // The cells whose centres can be within reach of the point on each axis, and one more
            // each way so that rounding here never leaves out a centre exactly at the reach.
            var wide = far + size;
            int fromX = _first(x - wide, shape.OriginX, size, shape.CountX), toX = _last(x + wide, shape.OriginX, size, shape.CountX);
            int fromY = _first(y - wide, shape.OriginY, size, shape.CountY), toY = _last(y + wide, shape.OriginY, size, shape.CountY);
            int fromZ = _first(z - wide, shape.OriginZ, size, shape.CountZ), toZ = _last(z + wide, shape.OriginZ, size, shape.CountZ);
            var best = far * far;
            var found = false;
            // Lowest x first, then y, then z, and only a nearer cell replaces the one held: so among
            // equals the first met stays, which is the lowest in that order.
            for (var cellX = fromX; cellX <= toX; cellX++) {
                var centreX = shape.Centre(shape.OriginX, cellX);
                double dx = (double)centreX - x;
                for (var cellY = fromY; cellY <= toY; cellY++) {
                    var centreY = shape.Centre(shape.OriginY, cellY);
                    double dy = (double)centreY - y;
                    for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                        var number = region[shape.Index(cellX, cellY, cellZ)];
                        if (number == 0 || (wanted != 0 && number != wanted)) { continue; }
                        var centreZ = shape.Centre(shape.OriginZ, cellZ);
                        double dz = (double)centreZ - z;
                        var squared = dx * dx + dy * dy + dz * dz;
                        if (found ? squared < best : squared <= best) {
                            best = squared;
                            found = true;
                            openX = centreX; openY = centreY; openZ = centreZ;
                        }
                    }
                }
            }
            return found;
        }

        // Open for the class: far enough from every shape, and not closed by cost.
        private static bool _open(ReadOnlySpan<byte> held, ReadOnlySpan<byte> cost, byte need, int cell) =>
            held[cell] >= need && (cost.Length == 0 || cost[cell] != CellCost.Closed);

        // Gives every open cell joined to start, by steps between cells that share a face, the
        // number, and returns how many there were. The queue holds each cell once.
        private static int _flood(ReadOnlySpan<byte> held, ReadOnlySpan<byte> cost, byte need, Span<byte> region, Span<int> queue,
            int start, byte number, int countX, int countY, int countZ, int layer) {
            int head = 0, tail = 0;
            queue[tail++] = start;
            region[start] = number;
            while (head < tail) {
                var cell = queue[head++];
                int cellX = cell % countX, cellY = cell / countX % countY, cellZ = cell / layer;
                if (cellX > 0) { tail = _take(held, cost, need, region, queue, tail, cell - 1, number); }
                if (cellX < countX - 1) { tail = _take(held, cost, need, region, queue, tail, cell + 1, number); }
                if (cellY > 0) { tail = _take(held, cost, need, region, queue, tail, cell - countX, number); }
                if (cellY < countY - 1) { tail = _take(held, cost, need, region, queue, tail, cell + countX, number); }
                if (cellZ > 0) { tail = _take(held, cost, need, region, queue, tail, cell - layer, number); }
                if (cellZ < countZ - 1) { tail = _take(held, cost, need, region, queue, tail, cell + layer, number); }
            }
            return tail;
        }

        private static int _take(ReadOnlySpan<byte> held, ReadOnlySpan<byte> cost, byte need, Span<byte> region, Span<int> queue, int tail, int cell, byte number) {
            if (region[cell] != 0 || !_open(held, cost, need, cell)) { return tail; }
            region[cell] = number;
            queue[tail] = cell;
            return tail + 1;
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
