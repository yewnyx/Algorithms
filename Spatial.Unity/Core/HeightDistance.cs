using System;

namespace Yewnyx.Spatial {
    /// <summary>The distance from a point to one <see cref="HeightField"/>, the box around it, and the height of its surface.</summary>
    /// <remarks>
    /// A heightfield is the solids of its blocks together. A block's solid is everything over its
    /// own rectangle from the floor up to its surface, so a point is inside when it is under the
    /// surface of the block it is over, and beside a block it is as near as the block's side is,
    /// wherever it is below the block's rim there. Everywhere else the nearest part is the
    /// surface, and that is triangles.
    /// Distances are worked out in the heightfield's own axes, in double. As with a box, each axis
    /// is brought to length 1 first. A heightfield that holds a number that is not finite never
    /// reads as farther than it is: it is at distance 0 from everything.
    /// </remarks>
    internal static class HeightDistance {
        /// <summary>A block is this many squares along each side, or fewer at the far edges.</summary>
        public const int Squares = 16;

        /// <summary>The most squares a heightfield may have along a side.</summary>
        public const int Most = 16384;

        /// <summary>The highest number a kept height can be.</summary>
        public const int Top = 65535;

        /// <summary>
        /// The box around a heightfield, from its floor to as high as a kept height can stand.
        /// Returns false for one that may not be stored: counts or sizes that are not above zero or
        /// too large, a flag that is set, a floor above its lowest height, a number that is
        /// not finite, axes that are not of length 1 and square to each other, or one that reaches
        /// beyond <see cref="Shapes.Reach"/>. It does not look at the blocks.
        /// </summary>
        /// <remarks>Add, subtract, multiply and compare only, rounded to single at each step.</remarks>
        public static bool TryBox(in HeightField field, out Box box) {
            box = default;
            if (field.CountU < 1 || field.CountW < 1 || field.CountU > Most || field.CountW > Most) { return false; }
            if (field.Flags != 0 || field.FirstBlock < 0 || field.FirstHeight < 0) { return false; }
            // A comparison with not-a-number is false, so such a number is refused.
            if (!(field.SizeU > 0f && field.SizeW > 0f && field.Step >= 0f && field.Gap >= 0f && field.Floor <= field.Lowest)) { return false; }
            float halfU = (float)((float)(field.SizeU * field.CountU) * 0.5f), halfW = (float)((float)(field.SizeW * field.CountW) * 0.5f);
            var top = (float)(field.Lowest + (float)(field.Step * Top));
            float halfV = (float)((float)(top - field.Floor) * 0.5f), middle = (float)((float)(top + field.Floor) * 0.5f);
            var centreX = (float)((float)((float)(field.X + (float)(field.UX * halfU)) + (float)(field.VX * middle)) + (float)(field.WX * halfW));
            var centreY = (float)((float)((float)(field.Y + (float)(field.UY * halfU)) + (float)(field.VY * middle)) + (float)(field.WY * halfW));
            var centreZ = (float)((float)((float)(field.Z + (float)(field.UZ * halfU)) + (float)(field.VZ * middle)) + (float)(field.WZ * halfW));
            // The centre is worked out in single: twice the pad on each half extent covers its rounding.
            const float Wider = 2f * Shapes.Pad;
            return ShapeDistance.TryBox(Solid.Box(centreX, centreY, centreZ, field.UX, field.UY, field.UZ, field.VX, field.VY, field.VZ,
                field.WX, field.WY, field.WZ, (float)(halfU + Wider), (float)(halfV + Wider), (float)(halfW + Wider)), out box);
        }

        /// <summary>
        /// Whether every block of a heightfield can be read: each lies within the blocks given, keeps
        /// its heights at a spacing that divides its sides, and has all of them within the heights given.
        /// </summary>
        public static bool Check(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights) {
            if (field.CountU < 1 || field.CountW < 1 || field.CountU > Most || field.CountW > Most) { return false; }
            int blocksU = (field.CountU + Squares - 1) / Squares, blocksW = (field.CountW + Squares - 1) / Squares;
            for (var blockW = 0; blockW < blocksW; blockW++) {
                for (var blockU = 0; blockU < blocksU; blockU++) {
                    if (!_kept(field, blocks, heights, blockU, blockW, blocksU, out _, out _, out _, out _, out _, out _)) { return false; }
                }
            }
            return true;
        }

        /// <summary>
        /// The squared distance from a point to a heightfield, 0 inside it, if that is below
        /// <paramref name="within"/>; otherwise a number that is not below it.
        /// Returns false if a block cannot be read.
        /// </summary>
        public static bool TrySquared(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights,
            double x, double y, double z, double within, out double squared) {
            squared = 0d;
            int countU = field.CountU, countW = field.CountW;
            if (countU < 1 || countW < 1 || countU > Most || countW > Most) { return false; }
            double sizeU = field.SizeU, sizeW = field.SizeW, lowest = field.Lowest, step = field.Step, floor = field.Floor;
            double px = x - field.X, py = y - field.Y, pz = z - field.Z;
            var u = (px * field.UX + py * field.UY + pz * field.UZ) * _toUnit(field.UX, field.UY, field.UZ);
            var v = (px * field.VX + py * field.VY + pz * field.VZ) * _toUnit(field.VX, field.VY, field.VZ);
            var w = (px * field.WX + py * field.WY + pz * field.WZ) * _toUnit(field.WX, field.WY, field.WZ);
            // A number that is not finite, or a size that is not above zero: it touches everything.
            var sum = u + v + w + sizeU + sizeW + lowest + step + floor;
            if (!(sum - sum == 0d) || !(sizeU > 0d) || !(sizeW > 0d) || !(step >= 0d)) { return true; }

            double lengthU = sizeU * countU, lengthW = sizeW * countW;
            double offU = u < 0d ? -u : u > lengthU ? u - lengthU : 0d, offW = w < 0d ? -w : w > lengthW ? w - lengthW : 0d;
            // How far the point is from the rectangle, seen from above.
            var flat = offU * offU + offW * offW;
            squared = within;
            if (!(flat < within)) { return true; }
            if (v < floor) {
                // Under the floor, which is the whole rectangle.
                var under = flat + (floor - v) * (floor - v);
                if (under < within) { squared = under; }
                return true;
            }
            var top = lowest + step * Top;
            if (v > top && !(flat + (v - top) * (v - top) < within)) { return true; }

            int blocksU = (countU + Squares - 1) / Squares, blocksW = (countW + Squares - 1) / Squares;
            double blockU = sizeU * Squares, blockW = sizeW * Squares;
            var best = within;
            // The block the point is over, or the nearest to that, first: what it finds lets most others be passed by.
            int nearU = _index(u / blockU, blocksU), nearW = _index(w / blockW, blocksW);
            if (!_block(field, blocks, heights, nearU, nearW, blocksU, u, v, w, ref best)) { squared = 0d; return false; }
            if (best > 0d) {
                var reach = Math.Sqrt(best);
                int fromU = _index((u - reach) / blockU, blocksU), toU = _index((u + reach) / blockU, blocksU);
                int fromW = _index((w - reach) / blockW, blocksW), toW = _index((w + reach) / blockW, blocksW);
                for (var atW = fromW; atW <= toW; atW++) {
                    // How far the point is from this row of blocks and then from each block, seen from
                    // above, and how far above every height it is: most blocks are passed by on that alone.
                    double rowMin = atW * blockW, rowMax = atW + 1 < blocksW ? rowMin + blockW : lengthW;
                    var besideW = w < rowMin ? rowMin - w : w > rowMax ? w - rowMax : 0d;
                    var apart = besideW * besideW + (v > top ? (v - top) * (v - top) : 0d);
                    if (!(apart < best)) { continue; }
                    for (var atU = fromU; atU <= toU; atU++) {
                        if (atU == nearU && atW == nearW) { continue; }
                        double blockMin = atU * blockU, blockMax = atU + 1 < blocksU ? blockMin + blockU : lengthU;
                        var besideU = u < blockMin ? blockMin - u : u > blockMax ? u - blockMax : 0d;
                        if (!(apart + besideU * besideU < best)) { continue; }
                        if (!_block(field, blocks, heights, atU, atW, blocksU, u, v, w, ref best)) { squared = 0d; return false; }
                    }
                }
            }
            squared = best;
            return true;
        }

        /// <summary>
        /// The height of a block's surface, as a kept height's number, at a place given in squares
        /// from the block's first corner. The place must be inside the block.
        /// </summary>
        /// <param name="kept">The block's kept heights, <paramref name="acrossU"/> to a row.</param>
        /// <param name="acrossU">How many kept heights there are in a row.</param>
        /// <param name="acrossW">How many rows of kept heights there are.</param>
        /// <param name="spacing">How many squares lie between one kept height and the next.</param>
        /// <param name="atU">The place along U, in squares from the block's first corner.</param>
        /// <param name="atW">The place along W, in squares from the block's first corner.</param>
        public static double Level(ReadOnlySpan<ushort> kept, int acrossU, int acrossW, int spacing, double atU, double atW) {
            double coarseU = atU / spacing, coarseW = atW / spacing;
            int i = coarseU >= 0d ? (coarseU < acrossU - 1 ? (int)coarseU : acrossU - 2) : 0;
            int j = coarseW >= 0d ? (coarseW < acrossW - 1 ? (int)coarseW : acrossW - 2) : 0;
            double a = coarseU - i, b = coarseW - j;
            var at = j * acrossU + i;
            double h00 = kept[at], h10 = kept[at + 1], h01 = kept[at + acrossU], h11 = kept[at + acrossU + 1];
            // The square is split from (0, 0) to (1, 1): the triangle on the U side of that line, or the one on the W side.
            return a >= b ? h00 + a * (h10 - h00) + b * (h11 - h10) : h00 + b * (h01 - h00) + a * (h11 - h01);
        }

        /// <summary>
        /// The lowest and the highest kept height, as numbers, among the blocks that a range of
        /// the rectangle touches. The range is in metres from the first corner and is held to the
        /// rectangle. Returns false if a block cannot be read.
        /// </summary>
        public static bool Span(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights,
            double fromU, double toU, double fromW, double toW, out int lowest, out int highest) {
            lowest = Top; highest = 0;
            if (field.CountU < 1 || field.CountW < 1 || field.CountU > Most || field.CountW > Most) { return false; }
            int blocksU = (field.CountU + Squares - 1) / Squares, blocksW = (field.CountW + Squares - 1) / Squares;
            double blockU = (double)field.SizeU * Squares, blockW = (double)field.SizeW * Squares;
            int firstU = _index(fromU / blockU, blocksU), lastU = _index(toU / blockU, blocksU);
            int firstW = _index(fromW / blockW, blocksW), lastW = _index(toW / blockW, blocksW);
            for (var atW = firstW; atW <= lastW; atW++) {
                for (var atU = firstU; atU <= lastU; atU++) {
                    if (!_kept(field, blocks, heights, atU, atW, blocksU, out var first, out var acrossU, out var acrossW, out _, out _, out _)) { return false; }
                    var kept = heights.Slice(first, acrossU * acrossW);
                    for (var k = 0; k < kept.Length; k++) {
                        int held = kept[k];
                        if (held < lowest) { lowest = held; }
                        if (held > highest) { highest = held; }
                    }
                }
            }
            return true;
        }

        // One block's kept heights and where it lies, in squares. False if it cannot be read.
        private static bool _kept(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights, int blockU, int blockW, int blocksU,
            out int first, out int acrossU, out int acrossW, out int spacing, out int width, out int depth) {
            first = 0; acrossU = 0; acrossW = 0; spacing = 0; width = 0; depth = 0;
            var index = (long)field.FirstBlock + (long)blockW * blocksU + blockU;
            if (index < 0 || index >= blocks.Length) { return false; }
            var entry = blocks[(int)index];
            spacing = 1 << (int)(entry & 3u);
            width = field.CountU - blockU * Squares; depth = field.CountW - blockW * Squares;
            if (width > Squares) { width = Squares; }
            if (depth > Squares) { depth = Squares; }
            if (width < 1 || depth < 1 || width % spacing != 0 || depth % spacing != 0) { return false; }
            acrossU = width / spacing + 1; acrossW = depth / spacing + 1;
            var from = (long)field.FirstHeight + (entry >> 2);
            if (from < 0 || from > heights.Length - acrossU * acrossW) { return false; }
            first = (int)from;
            return true;
        }

        // Lowers best to the squared distance from a point, in the heightfield's axes, to one
        // block's solid, where that is nearer. The point is at or above the floor.
        private static bool _block(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights, int blockU, int blockW, int blocksU,
            double u, double v, double w, ref double best) {
            if (!_kept(field, blocks, heights, blockU, blockW, blocksU, out var first, out var acrossU, out var acrossW, out var spacing, out var width, out var depth)) { return false; }
            double sizeU = field.SizeU, sizeW = field.SizeW;
            double minU = blockU * Squares * sizeU, maxU = (blockU * Squares + width) * sizeU;
            double minW = blockW * Squares * sizeW, maxW = (blockW * Squares + depth) * sizeW;
            double offU = u < minU ? minU - u : u > maxU ? u - maxU : 0d, offW = w < minW ? minW - w : w > maxW ? w - maxW : 0d;
            // How far the point is from the block's rectangle, seen from above. Nothing of the block is nearer.
            var flat = offU * offU + offW * offW;
            if (!(flat < best)) { return true; }

            var kept = heights.Slice(first, acrossU * acrossW);
            int high = kept[0];
            for (var k = 1; k < kept.Length; k++) {
                if (kept[k] > high) { high = kept[k]; }
            }
            double lowest = field.Lowest, step = field.Step;
            var top = lowest + step * high;
            if (v >= top && !(flat + (v - top) * (v - top) < best)) { return true; }
            // The surface straight under the point, or at the place of the block's rim that is
            // nearest it. At or below that the point is under the surface, or beside the block and
            // below its rim. Above it, that place of the surface is one that can be no nearer than
            // the nearest, which keeps the search that follows short.
            double nearU = u < minU ? minU : u > maxU ? maxU : u, nearW = w < minW ? minW : w > maxW ? maxW : w;
            var under = lowest + step * Level(kept, acrossU, acrossW, spacing, (nearU - minU) / sizeU, (nearW - minW) / sizeW);
            if (v <= under) {
                best = flat;
                return true;
            }
            var straight = flat + (v - under) * (v - under);
            if (straight < best) { best = straight; }

            // The surface: the squares between kept heights that can hold something nearer. A point
            // above the block's highest height is that far from all of it, whatever lies to the side.
            var room = v > top ? best - (v - top) * (v - top) : best;
            var reach = Math.Sqrt(room > 0d ? room : 0d);
            double squareU = spacing * sizeU, squareW = spacing * sizeW;
            int fromI = _index((u - reach - minU) / squareU, acrossU - 1), toI = _index((u + reach - minU) / squareU, acrossU - 1);
            int fromJ = _index((w - reach - minW) / squareW, acrossW - 1), toJ = _index((w + reach - minW) / squareW, acrossW - 1);
            for (var j = fromJ; j <= toJ; j++) {
                double w0 = minW + j * squareW, w1 = minW + (j + 1) * squareW;
                var besideW = w < w0 ? w0 - w : w > w1 ? w - w1 : 0d;
                for (var i = fromI; i <= toI; i++) {
                    double u0 = minU + i * squareU, u1 = minU + (i + 1) * squareU;
                    var besideU = u < u0 ? u0 - u : u > u1 ? u - u1 : 0d;
                    var beside = besideU * besideU + besideW * besideW;
                    if (!(beside < best)) { continue; }
                    var at = j * acrossU + i;
                    double h00 = lowest + step * kept[at], h10 = lowest + step * kept[at + 1];
                    double h01 = lowest + step * kept[at + acrossU], h11 = lowest + step * kept[at + acrossU + 1];
                    // A point above all four corners is at least that far above the square.
                    double highest = h00 > h10 ? h00 : h10, other = h01 > h11 ? h01 : h11;
                    if (other > highest) { highest = other; }
                    if (v > highest && !(beside + (v - highest) * (v - highest) < best)) { continue; }
                    var one = ShapeDistance.TriangleSquared(u0, h00, w0, u1, h10, w0, u1, h11, w1, u, v, w);
                    var two = ShapeDistance.TriangleSquared(u0, h00, w0, u1, h11, w1, u0, h01, w1, u, v, w);
                    if (one < best) { best = one; }
                    if (two < best) { best = two; }
                }
            }
            return true;
        }

        // Which of count rows a place falls in, held to them. A place that is not a number is in the first.
        private static int _index(double at, int count) => at >= 0d ? (at < count ? (int)at : count - 1) : 0;

        private static double _toUnit(double x, double y, double z) => 1.5d - 0.5d * (x * x + y * y + z * z);
    }
}
