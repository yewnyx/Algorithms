using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Tables of costs for kinds of agent, over a grid's costs read as what each cell is: a level
    /// of danger in the high four bits and a level of fog in the low four, each 0 to 15.
    /// </summary>
    /// <remarks>
    /// One cost array then serves every kind of agent: write what the cells are with
    /// <see cref="WallGrid.FillCost"/> and <see cref="Of"/>, and give each kind its table with
    /// <see cref="WallGridRoute.SetCostTable"/>. Only <see cref="CellCost.Closed"/> closes a cell for
    /// every kind, and <see cref="Of"/> never gives it.
    /// </remarks>
    public static class CostTable {
        /// <summary>
        /// The byte for a cell with a level of danger and a level of fog, each held to 0 to 15. The
        /// highest of both is held to 254, the same as a fog of 14, so that it does not close the cell.
        /// </summary>
        public static byte Of(int danger, int fog) {
            var value = (Math.Min(15, Math.Max(0, danger)) << 4) | Math.Min(15, Math.Max(0, fog));
            return value == CellCost.Closed ? (byte)(CellCost.Closed - 1) : (byte)value;
        }

        /// <summary>
        /// Writes the table for one kind of agent: a level of danger costs it <paramref name="danger"/>,
        /// and a level of fog <paramref name="fog"/>, in the grid's units of cost (16 doubles a step).
        /// </summary>
        /// <remarks>
        /// The costs are moved up together so the cheapest cell costs 0, since a cost can only make a
        /// step dearer. So a weight below 0 is a preference: with a fog of -4, the most fogged cells
        /// cost 0 and clear ones 60, which an agent that keeps to fog wants. Each cost is held to 254;
        /// <see cref="CellCost.Closed"/> stays closed. Returns false, writing nothing, if
        /// <paramref name="table"/> is shorter than 256 or a weight is not finite.
        /// </remarks>
        public static bool Make(float danger, float fog, Span<byte> table) {
            if (table.Length < CellCost.TableLength || !(danger - danger == 0f) || !(fog - fog == 0f)) { return false; }
            var least = double.PositiveInfinity;
            for (var value = 0; value < CellCost.Closed; value++) { least = Math.Min(least, (double)danger * (value >> 4) + (double)fog * (value & 15)); }
            for (var value = 0; value < CellCost.Closed; value++) {
                var cost = (double)danger * (value >> 4) + (double)fog * (value & 15) - least;
                table[value] = (byte)Math.Min(254d, Math.Round(cost));
            }
            table[CellCost.Closed] = CellCost.Closed;
            return true;
        }
    }
}
