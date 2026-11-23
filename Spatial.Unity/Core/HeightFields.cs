using System;

namespace Yewnyx.Spatial {
    /// <summary>Makes a <see cref="HeightField"/> from a grid of heights, keeping only as many as it needs.</summary>
    /// <remarks>
    /// Nothing is allocated and nothing throws. The same heights give the same result on every
    /// runtime: add, subtract, multiply, divide and compare in double, and one rounding to single.
    /// </remarks>
    public static class HeightFields {
        /// <summary>The blocks a heightfield of this many squares has, or 0 if it may not be that size.</summary>
        public static int BlocksLength(int countU, int countW) {
            if (countU < 1 || countW < 1 || countU > HeightDistance.Most || countW > HeightDistance.Most) { return 0; }
            return ((countU + HeightDistance.Squares - 1) / HeightDistance.Squares) * ((countW + HeightDistance.Squares - 1) / HeightDistance.Squares);
        }

        /// <summary>
        /// The most heights a heightfield of this many squares keeps, which is what
        /// <see cref="Pack"/> needs room for, or 0 if it may not be that size. It is every height,
        /// and those on a line between two blocks once for each.
        /// </summary>
        public static int HeightsLength(int countU, int countW) {
            if (BlocksLength(countU, countW) == 0) { return 0; }
            long blocksU = (countU + HeightDistance.Squares - 1) / HeightDistance.Squares, blocksW = (countW + HeightDistance.Squares - 1) / HeightDistance.Squares;
            return (int)((countU + blocksU) * (countW + blocksW));
        }

        /// <summary>
        /// Packs a grid of heights: for each block of 16 by 16 squares, the heights at every 8th,
        /// 4th or 2nd place where that keeps the surface within <paramref name="bound"/> of the one
        /// given, and every height where it does not.
        /// </summary>
        /// <remarks>
        /// <paramref name="heights"/> is one height for each corner of a square, countU + 1 to a
        /// row and countW + 1 rows, each within <see cref="Shapes.Reach"/>. A heightfield whose
        /// highest height and the bound together pass the reach is packed, and then refused when
        /// it is built into a set: leave the bound's room below the reach.
        /// The surface given is taken to split each square into two triangles from its corner at
        /// low U and low W to the one at high U and high W, and the surface kept splits them the
        /// same way.
        /// The surface kept is nowhere below the one given. A height that is kept stands where it
        /// was, raised by the most the ground rises above the flat pieces that meet there, and
        /// then rounded up to one of 65536 steps, so a plain slope costs nothing and only ground
        /// that curves between kept heights does. <see cref="HeightField.Gap"/> is the most the
        /// surface kept lies above the one given: no more than the bound, or than one step where
        /// the bound is less than that.
        /// <paramref name="blocks"/> needs <see cref="BlocksLength"/> elements and
        /// <paramref name="kept"/> <see cref="HeightsLength"/>; <paramref name="keptCount"/> is how
        /// many of the latter were written. The heightfield made has its first corner at the
        /// frame's origin, its axes along the frame's (U along x, V along y, W along z), and its
        /// floor at its lowest height: see <see cref="HeightField.Placed"/>.
        /// Returns false if a count or a size is not above zero, there are more than 16384 squares
        /// along a side, the heights are not as many as the counts say, a height is not within the
        /// reach, the bound is negative or not a number, or a buffer is too short.
        /// </remarks>
        public static bool Pack(ReadOnlySpan<float> heights, int countU, int countW, float sizeU, float sizeW, float bound,
            Span<uint> blocks, Span<ushort> kept, out HeightField field, out int keptCount) {
            field = default;
            keptCount = 0;
            var blockCount = BlocksLength(countU, countW);
            if (blockCount == 0 || heights.Length != (long)(countU + 1) * (countW + 1)) { return false; }
            if (blocks.Length < blockCount || kept.Length < HeightsLength(countU, countW)) { return false; }
            // A comparison with not-a-number is false, so such a number is refused.
            if (!(sizeU > 0f && sizeW > 0f && bound >= 0f) || float.IsInfinity(sizeU) || float.IsInfinity(sizeW) || float.IsInfinity(bound)) { return false; }
            float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
            for (var i = 0; i < heights.Length; i++) {
                var height = heights[i];
                if (!(height >= -Shapes.Reach && height <= Shapes.Reach)) { return false; }
                if (height < lowest) { lowest = height; }
                if (height > highest) { highest = height; }
            }
            // A kept height may stand as much as the bound above the highest given. The step is
            // taken a hair wide, so that rounding it to single never leaves the top out of reach.
            var step = (float)(((double)highest - lowest + bound) / HeightDistance.Top * 1.000001d);

            var across = countU + 1;
            var blocksU = (countU + HeightDistance.Squares - 1) / HeightDistance.Squares;
            Span<double> raise = stackalloc double[(HeightDistance.Squares + 1) * (HeightDistance.Squares + 1)];
            var gap = 0d;
            for (var block = 0; block < blockCount; block++) {
                int u0 = block % blocksU * HeightDistance.Squares, w0 = block / blocksU * HeightDistance.Squares;
                int width = countU - u0 < HeightDistance.Squares ? countU - u0 : HeightDistance.Squares;
                int depth = countW - w0 < HeightDistance.Squares ? countW - w0 : HeightDistance.Squares;
                var packed = false;
                // The widest spacing first: 8, 4, 2, and then every height, which always does.
                for (var power = 3; power >= 0 && !packed; power--) {
                    var spacing = 1 << power;
                    if (width % spacing != 0 || depth % spacing != 0) { continue; }
                    int acrossU = width / spacing + 1, acrossW = depth / spacing + 1;
                    var slot = kept.Slice(keptCount, acrossU * acrossW);
                    if (!_keep(heights, across, u0, w0, width, depth, spacing, lowest, step, slot, raise, out var worst)) { continue; }
                    if (power > 0 && !(worst <= bound)) { continue; }
                    blocks[block] = ((uint)keptCount << 2) | (uint)power;
                    keptCount += slot.Length;
                    if (worst > gap) { gap = worst; }
                    packed = true;
                }
                if (!packed) { return false; }
            }
            // Rounded up to single.
            var most = (float)gap;
            if (most < gap) { most = (float)(gap * 1.000001d); }
            field = new HeightField(0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, sizeU, sizeW, countU, countW, lowest, step, lowest, most, 0, 0, 0);
            return true;
        }

        // Fills one block's kept heights at a spacing, and says how far its surface then lies
        // above the heights given, at the most. False if a kept height would stand too high to store.
        private static bool _keep(ReadOnlySpan<float> heights, int across, int u0, int w0, int width, int depth, int spacing,
            float lowest, float step, Span<ushort> slot, Span<double> raise, out double worst) {
            worst = 0d;
            int acrossU = width / spacing + 1, acrossW = depth / spacing + 1;
            raise = raise.Slice(0, acrossU * acrossW);
            raise.Clear();
            if (spacing > 1) {
                // Each flat piece between three kept heights: how far the ground given rises above
                // it. Its three corners are raised by at least that, so the piece then clears the ground.
                for (var j = 0; j + 1 < acrossW; j++) {
                    for (var i = 0; i + 1 < acrossU; i++) {
                        int cornerU = u0 + i * spacing, cornerW = w0 + j * spacing;
                        double h00 = heights[cornerW * across + cornerU], h10 = heights[cornerW * across + cornerU + spacing];
                        double h01 = heights[(cornerW + spacing) * across + cornerU], h11 = heights[(cornerW + spacing) * across + cornerU + spacing];
                        double one = 0d, two = 0d;
                        for (var b = 0; b <= spacing; b++) {
                            for (var a = 0; a <= spacing; a++) {
                                double given = heights[(cornerW + b) * across + cornerU + a], alongU = (double)a / spacing, alongW = (double)b / spacing;
                                // A height on the line the square is split along stands on both pieces.
                                if (a >= b) { var over = given - (h00 + alongU * (h10 - h00) + alongW * (h11 - h10)); if (over > one) { one = over; } }
                                if (a <= b) { var over = given - (h00 + alongW * (h01 - h00) + alongU * (h11 - h01)); if (over > two) { two = over; } }
                            }
                        }
                        int at00 = j * acrossU + i, at10 = at00 + 1, at01 = at00 + acrossU, at11 = at01 + 1;
                        _raise(raise, at00, one); _raise(raise, at10, one); _raise(raise, at11, one);
                        _raise(raise, at00, two); _raise(raise, at11, two); _raise(raise, at01, two);
                    }
                }
            }
            for (var j = 0; j < acrossW; j++) {
                for (var i = 0; i < acrossU; i++) {
                    var at = j * acrossU + i;
                    var wanted = heights[(w0 + j * spacing) * across + u0 + i * spacing] + raise[at];
                    // Rounded up to a step. Where the step is 0 every height is the lowest.
                    var number = step > 0f ? Math.Ceiling((wanted - lowest) / step) : 0d;
                    if (number < 0d) { number = 0d; }
                    while (number <= HeightDistance.Top && lowest + (double)step * number < wanted) { number += 1d; }
                    if (!(number <= HeightDistance.Top)) { return false; }
                    slot[at] = (ushort)number;
                }
            }
            // Checked as it will be read. Where rounding has left the surface a hair under a
            // height given, every kept height goes up a step.
            for (var tries = 0; tries < 4; tries++) {
                var under = false;
                worst = 0d;
                for (var b = 0; b <= depth && !under; b++) {
                    for (var a = 0; a <= width; a++) {
                        var over = lowest + (double)step * HeightDistance.Level(slot, acrossU, acrossW, spacing, a, b) - heights[(w0 + b) * across + u0 + a];
                        if (over < 0d) { under = true; break; }
                        if (over > worst) { worst = over; }
                    }
                }
                if (!under) { return true; }
                for (var k = 0; k < slot.Length; k++) {
                    if (slot[k] == HeightDistance.Top) { return false; }
                    slot[k]++;
                }
            }
            return false;
        }

        private static void _raise(Span<double> raise, int at, double by) {
            if (by > raise[at]) { raise[at] = by; }
        }
    }
}
