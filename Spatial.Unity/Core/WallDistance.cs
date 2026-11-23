using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Fills a <see cref="WallMap"/> from shapes, and says how much room there is at a point from
    /// the map alone.
    /// </summary>
    /// <remarks>
    /// Nothing is allocated and nothing throws. After a false return the outputs are not to be read.
    /// </remarks>
    public static class WallDistance {
        /// <summary>
        /// Fills one z layer of a wall map: each cell gets the distance from its centre to the nearest
        /// shape as a count of <paramref name="unit"/>, rounded down, 0 inside a solid and
        /// <paramref name="cap"/> for that far or more. Layers do not depend on each other, so they
        /// can be filled side by side.
        /// </summary>
        /// <remarks>
        /// For every cell, count * unit is no more than the true distance, and for a cell below the
        /// cap it is more than the true distance - unit - 2 * <see cref="Shapes.Slack"/>.
        /// <paramref name="layer"/> is the layer's own cells, <see cref="GridShape.LayerLength"/> of
        /// them. Each shape is tested against the cells its box can reach, so the cost follows the
        /// shapes' surface and not the grid's volume.
        /// Returns false if the shape cannot be used, the unit is not above zero, the cap is zero,
        /// z is not a layer, the layer is too short, or the set's spans do not belong together or
        /// are found damaged. The layer's cells are then 0, which claims no room: a layer that was
        /// refused never reads as open. (If the layer's length is not known, because the shape cannot
        /// be used, or the span is shorter than a layer, all of the span is set to 0.)
        /// </remarks>
        public static bool MarkLayer(in GridShape shape, in ShapeSet shapes, float unit, byte cap, int z, Span<byte> layer) {
            var given = layer;
            var count = shapes.Count;
            var length = shape.LayerLength;
            if (count < 0 || length == 0 || !Usable(unit, cap) || (uint)z >= (uint)shape.CountZ || layer.Length < length) {
                (length > 0 && given.Length > length ? given.Slice(0, length) : given).Clear();
                return false;
            }
            layer = layer.Slice(0, length);
            layer.Fill(cap);

            // A cell this far or more from a shape keeps the cap whatever the shape does.
            var reach = (double)cap * unit + Shapes.Slack;
            var reachSquared = reach * reach;
            // What a box must be within, of a cell's centre, for its shape to matter. The unit on top covers rounding here.
            var margin = reach + unit;
            double size = shape.CellSize, centreZ = shape.Centre(shape.OriginZ, z);
            ReadOnlySpan<Box> boxes = shapes.Boxes;
            for (var item = 0; item < count; item++) {
                var box = boxes[item];
                if (centreZ < box.MinZ - margin || centreZ > box.MaxZ + margin) { continue; }
                // The cells whose centres lie within the margin of the box, on x and y. A side that is
                // not a number fails both tests and so leaves the whole row in.
                int fromX = _first(box.MinX - margin, shape.OriginX, size, shape.CountX), toX = _last(box.MaxX + margin, shape.OriginX, size, shape.CountX);
                int fromY = _first(box.MinY - margin, shape.OriginY, size, shape.CountY), toY = _last(box.MaxY + margin, shape.OriginY, size, shape.CountY);
                for (var cellY = fromY; cellY <= toY; cellY++) {
                    double centreY = shape.Centre(shape.OriginY, cellY);
                    var row = cellY * shape.CountX;
                    for (var cellX = fromX; cellX <= toX; cellX++) {
                        var held = layer[row + cellX];
                        if (held == 0) { continue; }
                        if (!Shapes.TrySquared(shapes, item, shape.Centre(shape.OriginX, cellX), centreY, centreZ, reachSquared, out var squared)) {
                            // The layer was filled with the cap to start from, and must not be left saying so.
                            layer.Clear();
                            return false;
                        }
                        if (!(squared < reachSquared)) { continue; }
                        var counts = Math.Floor((Math.Sqrt(squared) - Shapes.Slack) / unit);
                        var value = counts >= cap ? cap : counts > 0d ? (byte)counts : (byte)0;
                        if (value < held) { layer[row + cellX] = value; }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// A distance that the nearest shape is proven to be at least, at any point of the grid, from
        /// the map alone. It is never more than the true distance and never more than the cap.
        /// </summary>
        /// <remarks>
        /// It reads the eight cells whose centres surround the point. With d the distance each holds
        /// and w the usual weights for blending between eight corners, the point is at least
        /// sqrt(sum of w * d * d - spread) from every shape, where the spread is the cell size squared
        /// times the sum over the axes of f * (1 - f), f being how far the point sits between the two
        /// rows of centres on that axis. When all eight are more than 0.866 of a cell from every shape
        /// no shape is inside the cube of the eight, and a second bound applies: the distance to one
        /// of the cube's faces plus what that face's four cells prove on it. The larger is returned,
        /// less <see cref="Shapes.Slack"/>.
        /// It says how far the nearest surface is, not whether the point is in open space.
        /// A point on any face of the grid's box is inside for this, the upper faces too: room is the
        /// same kind of answer either side of a cell's edge, and needs no cell to belong to.
        /// Returns false if the map cannot be used or the point is outside the grid or not a number.
        /// </remarks>
        public static bool RoomAt(in WallMap wall, float x, float y, float z, out float room) {
            room = 0f;
            var shape = wall.Shape;
            if (!wall.IsValid || !shape.Holds(x, y, z)) { return false; }
            double size = shape.CellSize;
            _between(x, shape.OriginX, size, shape.CountX, out var lowX, out var highX, out var fx, out var offX);
            _between(y, shape.OriginY, size, shape.CountY, out var lowY, out var highY, out var fy, out var offY);
            _between(z, shape.OriginZ, size, shape.CountZ, out var lowZ, out var highZ, out var fz, out var offZ);

            ReadOnlySpan<byte> held = wall.Distance;
            double unit = wall.Unit;
            double d000 = held[shape.Index(lowX, lowY, lowZ)] * unit, d100 = held[shape.Index(highX, lowY, lowZ)] * unit;
            double d010 = held[shape.Index(lowX, highY, lowZ)] * unit, d110 = held[shape.Index(highX, highY, lowZ)] * unit;
            double d001 = held[shape.Index(lowX, lowY, highZ)] * unit, d101 = held[shape.Index(highX, lowY, highZ)] * unit;
            double d011 = held[shape.Index(lowX, highY, highZ)] * unit, d111 = held[shape.Index(highX, highY, highZ)] * unit;

            double gx = 1d - fx, gy = 1d - fy, gz = 1d - fz;
            var blend = gz * (gy * (gx * d000 * d000 + fx * d100 * d100) + fy * (gx * d010 * d010 + fx * d110 * d110)) +
                        fz * (gy * (gx * d001 * d001 + fx * d101 * d101) + fy * (gx * d011 * d011 + fx * d111 * d111));
            var first = blend - size * size * (fx * gx + fy * gy + fz * gz);
            var proven = first > 0d ? Math.Sqrt(first) : 0d;

            // The second bound needs a cube with all three sides, and every corner clear of its middle.
            var least = _least(_least(d000, d100, d010, d110), _least(d001, d101, d011, d111), double.MaxValue, double.MaxValue);
            if (lowX != highX && lowY != highY && lowZ != highZ && least * least > 0.75d * size * size) {
                var half = 0.5d * size * size;
                var second = fx * size + Math.Sqrt(_square(_least(d000, d010, d001, d011)) - half);
                second = _lower(second, gx * size + Math.Sqrt(_square(_least(d100, d110, d101, d111)) - half));
                second = _lower(second, fy * size + Math.Sqrt(_square(_least(d000, d100, d001, d101)) - half));
                second = _lower(second, gy * size + Math.Sqrt(_square(_least(d010, d110, d011, d111)) - half));
                second = _lower(second, fz * size + Math.Sqrt(_square(_least(d000, d100, d010, d110)) - half));
                second = _lower(second, gz * size + Math.Sqrt(_square(_least(d001, d101, d011, d111)) - half));
                if (second > proven) { proven = second; }
            }

            // In the outer half cell the point is not among any eight centres: what holds at the
            // nearest point that is, less the way there, holds here.
            proven -= Math.Sqrt(offX * offX + offY * offY + offZ * offZ) + Shapes.Slack;
            var most = wall.Cap * unit;
            room = (float)(proven > most ? most : proven > 0d ? proven : 0d);
            return true;
        }

        internal static bool Usable(float unit, byte cap) => unit > 0f && !float.IsInfinity(unit) && cap > 0;

        // The two rows of centres a coordinate lies between, how far between them it sits (0 at the
        // low row, 1 at the high), and how far outside both it is (0 unless it is in the outer half cell).
        private static void _between(float value, float origin, double size, int count, out int low, out int high, out double between, out double off) {
            var at = ((double)value - origin) / size - 0.5d;
            var floor = Math.Floor(at);
            low = floor < 0d ? 0 : floor > count - 1 ? count - 1 : (int)floor;
            high = low + 1 < count ? low + 1 : low;
            between = at - low;
            between = high == low ? 0d : between < 0d ? 0d : between > 1d ? 1d : between;
            off = (at - low - between) * size;
        }

        // The first and last cells along an axis whose centres can lie at or beyond a bound.
        private static int _first(double bound, float origin, double size, int count) {
            var at = Math.Floor((bound - origin) / size - 0.5d);
            return at > 0d ? (at < count ? (int)at : count) : 0;
        }

        private static int _last(double bound, float origin, double size, int count) {
            var at = Math.Ceiling((bound - origin) / size - 0.5d);
            return at < count - 1 ? (at >= 0d ? (int)at : -1) : count - 1;
        }

        private static double _least(double a, double b, double c, double d) {
            var least = a < b ? a : b;
            least = c < least ? c : least;
            return d < least ? d : least;
        }

        private static double _lower(double a, double b) => b < a ? b : a;

        private static double _square(double value) => value * value;
    }
}
