using System;

namespace Yewnyx.Spatial {
    // A cost array as one kind of agent sees it: each byte through its table, or as it is where there is none.
    internal readonly ref struct Priced {
        public readonly ReadOnlySpan<byte> Layer, Table;

        public Priced(ReadOnlySpan<byte> layer, ReadOnlySpan<byte> table) { Layer = layer; Table = table; }

        public int Length => Layer.Length;

        public byte this[int cell] => Table.Length == 0 ? Layer[cell] : Table[Layer[cell]];
    }

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

        /// <summary>How many costs a table holds: one for each value a cell's byte can take.</summary>
        public const int TableLength = 256;

        /// <summary>
        /// Whether a table can be used: empty, meaning each byte is its own cost, or
        /// <see cref="TableLength"/> costs, one for each byte, that leave <see cref="Closed"/> closed.
        /// </summary>
        /// <remarks>
        /// A table says what each byte of the cost array costs one kind of agent, so one array can
        /// mean different costs to different kinds. It may close a byte for its kind by giving it
        /// <see cref="Closed"/>, but cannot open a cell the array closes: regions are made from the
        /// array, for every kind at once. An empty cost array is every cell normal whatever the
        /// table, so a table that prices 0 above 0 changes nothing until there is an array to read.
        /// </remarks>
        public static bool IsTable(ReadOnlySpan<byte> table) => table.Length == 0 || (table.Length == TableLength && table[Closed] == Closed);

        /// <summary>
        /// The least cost a table gives over the values marked in <paramref name="present"/>, a bit for
        /// each of the 256 (see <see cref="Regions.CostsIn"/>); with an empty table, the least value.
        /// What a search wants as its least cost, for one kind of agent, with no pass over the cells.
        /// </summary>
        /// <remarks>
        /// 0 if no value is marked, <paramref name="present"/> has fewer than 4 elements, or the table
        /// cannot be used. A mark too many only makes the least lower, which is always safe.
        /// </remarks>
        public static byte Least(ReadOnlySpan<ulong> present, ReadOnlySpan<byte> table) {
            if (present.Length < 4 || !IsTable(table)) { return 0; }
            var least = -1;
            for (var value = 0; value < TableLength; value++) {
                if (((present[value >> 6] >> (value & 63)) & 1UL) == 0UL) { continue; }
                int cost = table.Length == 0 ? value : table[value];
                if (least < 0 || cost < least) { least = cost; }
            }
            return least < 0 ? (byte)0 : (byte)least;
        }

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
        public static bool Fill(in GridShape shape, in Solid solid, byte value, Span<byte> cost, out int written) => Fill(shape, solid, value, cost, out written, out _);

        /// <summary>
        /// The same, and says how many of the cells it wrote were closed and are no longer: with
        /// none, and a value that is not <see cref="Closed"/>, the fill closed and opened nothing,
        /// so regions made before it still hold.
        /// </summary>
        public static bool Fill(in GridShape shape, in Solid solid, byte value, Span<byte> cost, out int written, out int opened) {
            written = 0;
            opened = 0;
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
                            var cell = shape.Index(cellX, cellY, cellZ);
                            if (cost[cell] == Closed && value != Closed) { opened++; }
                            cost[cell] = value;
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
