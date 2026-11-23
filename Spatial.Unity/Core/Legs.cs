using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Whether a ball can move along a straight leg without coming nearer a shape than its radius:
    /// from the wall map where the map can say, and from the shapes themselves where it cannot.
    /// </summary>
    /// <remarks>
    /// The map is asked first, at samples no more than a quarter of a cell apart, with one at each
    /// end. A stretch between two samples is proven by the map when both prove the class's
    /// <see cref="SizeClass.LegNeed"/>. Any other stretch goes to the shapes: exact distances at
    /// points along it, each a step on from the last that the last one's distance allows. With no
    /// shapes such a stretch fails.
    /// A leg never leaves the grid, and never comes nearer than half a cell to a cell that is
    /// closed by cost: none of the eight cells whose centres surround a sample may be closed.
    /// A leg can also be held to a cost: it then passes through no cell that costs more, and
    /// costs no more as flown than it is given to spend. What a leg costs as flown is its length
    /// in each cell times 1 + cost / 16 for that cell. For this the leg is walked from cell to
    /// cell, and not sampled. Where it goes from one cell to another through an edge or a corner
    /// of the two, the other cells that meet there are only touched, and do not count.
    /// A leg that passes keeps the radius everywhere. One that clears the radius by less than 2 mm
    /// may be refused.
    /// </remarks>
    internal static class Legs {
        /// <summary>How much more than the radius an exact distance must read for its point to pass.</summary>
        public const float Margin = 3f / 2048f;

        /// <summary>How much of a line of sight is not looked at, at the end it starts from: an end may lie on a surface.</summary>
        public const float Allowance = 0.05f;

        // What a point that passed is truly clear by, beyond the radius: an exact distance is within
        // the slack of the true one, and the point asked about within the slack of the one meant.
        private const double Least = Margin - 2d * Shapes.Slack;

        /// <summary>The cost that holds a leg to nothing: no cell costs more.</summary>
        public const int AnyCost = CellCost.Closed;

        /// <summary>The spend that holds a leg to nothing.</summary>
        public const double AnySpend = double.PositiveInfinity;

        /// <summary>
        /// Whether a ball of the radius can move from a to b, through no cell that costs more than
        /// <paramref name="most"/>, at no more than <paramref name="spend"/> as flown.
        /// <paramref name="need"/> is what a sample must prove from the map.
        /// </summary>
        public static bool Clear(in WallMap wall, in ShapeSet shapes, ReadOnlySpan<byte> cost, int most, double spend, float radius, float need,
            double ax, double ay, double az, double bx, double by, double bz) {
            var shape = wall.Shape;
            if (!shape.Holds((float)ax, (float)ay, (float)az) || !shape.Holds((float)bx, (float)by, (float)bz)) { return false; }
            double size = shape.CellSize, dx = bx - ax, dy = by - ay, dz = bz - az;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var stretches = (int)Math.Ceiling(length / (0.25d * size));
            var exact = shapes.Count > 0;
            var limit = (float)(radius + size);
            // How far along the leg the shapes have proven it so far.
            var covered = -1d;
            var before = false;
            // The cost first: it is the cheapest to look at.
            if (cost.Length != 0 && (most < AnyCost || spend < AnySpend) &&
                !(Flown(shape, cost, most, ax, ay, az, bx, by, bz, out var flown) && flown <= spend)) { return false; }
            for (var k = 0; k <= stretches; k++) {
                var along = stretches == 0 ? 0d : (double)k / stretches;
                float x = (float)(ax + dx * along), y = (float)(ay + dy * along), z = (float)(az + dz * along);
                if (cost.Length != 0 && _besideClosed(shape, cost, x, y, z)) { return false; }
                var proven = WallDistance.RoomAt(wall, x, y, z, out var room) && room >= need;
                if (k > 0 && !(proven && before)) {
                    // The map does not prove the stretch that ends here, so the shapes must.
                    double from = (k - 1) * length / stretches, to = k * length / stretches;
                    if (covered < to) {
                        if (!exact) { return false; }
                        if (!_march(shapes, ax, ay, az, dx / length, dy / length, dz / length, radius, limit, from > covered ? from : covered, to, ref covered)) { return false; }
                    }
                }
                before = proven;
            }
            if (stretches == 0 && !before) {
                // A leg with no length is one point.
                return exact && Shapes.Distance(shapes, (float)ax, (float)ay, (float)az, limit, out var distance) && distance >= (double)radius + Margin;
            }
            return true;
        }

        /// <summary>
        /// Whether the straight line from an end to a point meets no shape, not counting the first
        /// <see cref="Allowance"/> of it.
        /// </summary>
        public static bool Sees(in ShapeSet shapes, float cellSize, double endX, double endY, double endZ, double x, double y, double z) {
            double dx = x - endX, dy = y - endY, dz = z - endZ;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= Allowance) { return true; }
            var covered = -1d;
            return _march(shapes, endX, endY, endZ, dx / length, dy / length, dz / length, 0d, cellSize, Allowance, length, ref covered);
        }

        /// <summary>What the cell that holds a point costs: 0 where no cost is kept. A point on the grid's faces is in the cell beside it.</summary>
        public static int CostAt(in GridShape shape, ReadOnlySpan<byte> cost, float x, float y, float z) {
            if (cost.Length == 0) { return 0; }
            double size = shape.CellSize;
            return cost[shape.Index(_cell(x, shape.OriginX, size, shape.CountX), _cell(y, shape.OriginY, size, shape.CountY), _cell(z, shape.OriginZ, size, shape.CountZ))];
        }

        // Proves the part of a leg from one distance along it to another, in metres from a, against
        // the shapes themselves. Each point asked about must read the radius and the margin. What
        // it reads beyond the radius is how far on the leg is then sure to be clear; where that is
        // short, the next point is taken a fixed step on, short enough that two points which both
        // pass prove everything between them. covered is set to how far the leg is proven.
        private static bool _march(in ShapeSet shapes, double ax, double ay, double az, double ux, double uy, double uz,
            double radius, float limit, double from, double to, ref double covered) {
            // Two points a distance L apart that are both d from every shape have everything
            // between them at least sqrt(d * d - L * L / 4) from every shape.
            var fixedStep = 2d * Math.Sqrt((radius + Least) * (radius + Least) - radius * radius);
            var at = from;
            while (true) {
                if (!Shapes.Distance(shapes, (float)(ax + ux * at), (float)(ay + uy * at), (float)(az + uz * at), limit, out var distance) ||
                    !(distance >= radius + Margin)) { return false; }
                var sure = distance - 2d * Shapes.Slack - radius;
                if (at + sure >= to || at >= to) {
                    covered = at + sure;
                    return true;
                }
                var step = sure > fixedStep ? sure : fixedStep;
                at = at + step < to ? at + step : to;
            }
        }

        // Whether any of the eight cells whose centres surround a point is closed by cost.
        private static bool _besideClosed(in GridShape shape, ReadOnlySpan<byte> cost, float x, float y, float z) {
            double size = shape.CellSize;
            _rows(x, shape.OriginX, size, shape.CountX, out var lowX, out var highX);
            _rows(y, shape.OriginY, size, shape.CountY, out var lowY, out var highY);
            _rows(z, shape.OriginZ, size, shape.CountZ, out var lowZ, out var highZ);
            return cost[shape.Index(lowX, lowY, lowZ)] == CellCost.Closed || cost[shape.Index(highX, lowY, lowZ)] == CellCost.Closed ||
                   cost[shape.Index(lowX, highY, lowZ)] == CellCost.Closed || cost[shape.Index(highX, highY, lowZ)] == CellCost.Closed ||
                   cost[shape.Index(lowX, lowY, highZ)] == CellCost.Closed || cost[shape.Index(highX, lowY, highZ)] == CellCost.Closed ||
                   cost[shape.Index(lowX, highY, highZ)] == CellCost.Closed || cost[shape.Index(highX, highY, highZ)] == CellCost.Closed;
        }

        /// <summary>
        /// What the straight leg from a to b costs as flown: its length in each cell it passes
        /// through times 1 + cost / 16 for that cell, or its length alone where no cost is kept.
        /// Returns false, with what it had cost so far, at the first cell that costs more than
        /// <paramref name="most"/>.
        /// </summary>
        /// <remarks>
        /// The leg is walked from each cell to the next it enters. Where it crosses two or three
        /// of the planes between cells at one point, which is an edge or a corner, it goes straight
        /// to the cell beyond: the other cells that meet there are touched and not passed through.
        /// </remarks>
        public static bool Flown(in GridShape shape, ReadOnlySpan<byte> cost, int most, double ax, double ay, double az, double bx, double by, double bz, out double flown) {
            double size = shape.CellSize, dx = bx - ax, dy = by - ay, dz = bz - az;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            flown = 0d;
            if (cost.Length == 0) {
                flown = length;
                return true;
            }
            int x = _cell(ax, shape.OriginX, size, shape.CountX), y = _cell(ay, shape.OriginY, size, shape.CountY), z = _cell(az, shape.OriginZ, size, shape.CountZ);
            int stepX = dx > 0d ? 1 : dx < 0d ? -1 : 0, stepY = dy > 0d ? 1 : dy < 0d ? -1 : 0, stepZ = dz > 0d ? 1 : dz < 0d ? -1 : 0;
            int here = cost[shape.Index(x, y, z)];
            if (here > most) { return false; }
            // Crossings less than a millionth of a cell apart along the leg are at one point.
            var same = length > 0d ? 1e-6 * size / length : 0d;
            // How far along the leg, from 0 to 1, it came into this cell.
            var entered = 0d;
            while (true) {
                // How far along the leg it leaves this cell on each axis.
                double tx = _leaves(ax, dx, shape.OriginX, size, x, stepX), ty = _leaves(ay, dy, shape.OriginY, size, y, stepY), tz = _leaves(az, dz, shape.OriginZ, size, z, stepZ);
                var t = tx < ty ? tx : ty;
                if (tz < t) { t = tz; }
                if (!(t <= 1d)) {
                    flown += (1d - entered) * length * (1d + here / 16d);
                    return true;
                }
                if (t > entered) {
                    flown += (t - entered) * length * (1d + here / 16d);
                    entered = t;
                }
                // A leg that runs along a face of the grid does not leave it.
                if (tx <= t + same) { if ((uint)(x + stepX) < (uint)shape.CountX) { x += stepX; } else { stepX = 0; } }
                if (ty <= t + same) { if ((uint)(y + stepY) < (uint)shape.CountY) { y += stepY; } else { stepY = 0; } }
                if (tz <= t + same) { if ((uint)(z + stepZ) < (uint)shape.CountZ) { z += stepZ; } else { stepZ = 0; } }
                here = cost[shape.Index(x, y, z)];
                if (here > most) { return false; }
            }
        }

        // How far along a leg, from 0 at a to 1 at its end, it crosses the plane it leaves a cell
        // by on one axis. Infinity if it does not move along that axis.
        private static double _leaves(double a, double d, float origin, double size, int cell, int step) =>
            step == 0 ? double.PositiveInfinity : (origin + (cell + (step > 0 ? 1 : 0)) * size - a) / d;

        // The cell along one axis that holds a coordinate, held to the grid.
        private static int _cell(double value, float origin, double size, int count) {
            var floor = Math.Floor((value - origin) / size);
            return floor < 0d ? 0 : floor > count - 1 ? count - 1 : (int)floor;
        }

        // The two rows of centres a coordinate lies between, held to the grid.
        private static void _rows(float value, float origin, double size, int count, out int low, out int high) {
            var floor = Math.Floor(((double)value - origin) / size - 0.5d);
            low = floor < 0d ? 0 : floor > count - 1 ? count - 1 : (int)floor;
            high = low + 1 < count ? low + 1 : low;
        }
    }
}
