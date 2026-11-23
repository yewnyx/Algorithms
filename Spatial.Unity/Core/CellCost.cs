using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// One byte per cell that the caller writes, saying what it costs to move through the cell.
    /// </summary>
    /// <remarks>
    /// 0 is the normal cost, and an empty array means every cell is normal. 255 closes a cell. Any
    /// other value scales a step into the cell by 1 + value / 16, so a cell is never cheaper than
    /// normal. Nothing is allocated and nothing throws.
    /// </remarks>
    public static class CellCost {
        /// <summary>The value that closes a cell.</summary>
        public const byte Closed = 255;

        /// <summary>
        /// Writes <paramref name="value"/> into every cell whose centre is inside the solid. Cells
        /// outside it are left as they are, so later fills overwrite earlier ones where they overlap.
        /// </summary>
        /// <remarks>
        /// Returns false, writing nothing, if the shape cannot be used, <paramref name="cost"/> holds
        /// fewer bytes than the grid has cells, or the solid may not be stored (see <see cref="Shapes.Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/>).
        /// </remarks>
        public static bool Fill(in GridShape shape, in Solid solid, byte value, Span<byte> cost) => Fill(shape, solid, value, cost, out _);

        /// <summary>
        /// The same, and says how many cells it wrote: 0 for a solid that holds no cell's centre,
        /// which a solid thinner than a cell may be.
        /// </summary>
        public static bool Fill(in GridShape shape, in Solid solid, byte value, Span<byte> cost, out int written) {
            written = 0;
            var cells = shape.CellCount;
            if (cells == 0 || cost.Length < cells || !ShapeDistance.TryBox(solid, out var box)) { return false; }
            double size = shape.CellSize;
            int fromX = _clamp(Math.Floor((box.MinX - shape.OriginX) / size - 0.5d), shape.CountX), toX = _clamp(Math.Ceiling((box.MaxX - shape.OriginX) / size - 0.5d), shape.CountX);
            int fromY = _clamp(Math.Floor((box.MinY - shape.OriginY) / size - 0.5d), shape.CountY), toY = _clamp(Math.Ceiling((box.MaxY - shape.OriginY) / size - 0.5d), shape.CountY);
            int fromZ = _clamp(Math.Floor((box.MinZ - shape.OriginZ) / size - 0.5d), shape.CountZ), toZ = _clamp(Math.Ceiling((box.MaxZ - shape.OriginZ) / size - 0.5d), shape.CountZ);
            for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                double centreZ = shape.Centre(shape.OriginZ, cellZ);
                for (var cellY = fromY; cellY <= toY; cellY++) {
                    double centreY = shape.Centre(shape.OriginY, cellY);
                    for (var cellX = fromX; cellX <= toX; cellX++) {
                        if (ShapeDistance.SolidSquared(solid, shape.Centre(shape.OriginX, cellX), centreY, centreZ) == 0d) {
                            cost[shape.Index(cellX, cellY, cellZ)] = value;
                            written++;
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// The cost of the cell that holds a point; 0 when <paramref name="cost"/> is empty. Returns
        /// false if the point is outside the grid or not a number, the shape cannot be used, or the
        /// array is neither empty nor one byte per cell.
        /// </summary>
        public static bool At(in GridShape shape, ReadOnlySpan<byte> cost, float x, float y, float z, out byte value) {
            value = 0;
            if (!shape.TryCell(x, y, z, out var cellX, out var cellY, out var cellZ)) { return false; }
            if (cost.Length == 0) { return true; }
            if (cost.Length < shape.CellCount) { return false; }
            value = cost[shape.Index(cellX, cellY, cellZ)];
            return true;
        }

        private static int _clamp(double cell, int count) => cell > 0d ? (cell < count - 1 ? (int)cell : count - 1) : 0;
    }
}
