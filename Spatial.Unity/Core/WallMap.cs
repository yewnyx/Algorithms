using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Wall distance for one grid: for each cell, how far its centre is from the nearest shape, as a
    /// count of <see cref="Unit"/>. The shape, the unit, the cap and the array belong together.
    /// </summary>
    /// <remarks>
    /// The one rule everything built on a wall map relies on: a cell's count times the unit is never
    /// more than the true distance from that cell's centre to the nearest shape. A cell that holds
    /// <see cref="Cap"/> is that far or more.
    /// </remarks>
    public readonly ref struct WallMap {
        /// <summary>The grid the map is over.</summary>
        public readonly GridShape Shape;
        /// <summary>Metres per count.</summary>
        public readonly float Unit;
        /// <summary>The largest count stored.</summary>
        public readonly byte Cap;
        /// <summary>One count per cell, along x, then y, then z.</summary>
        public readonly ReadOnlySpan<byte> Distance;

        /// <summary>Views an array of counts as a wall map. Nothing is copied or checked: <see cref="IsValid"/> says whether it can be used.</summary>
        public WallMap(in GridShape shape, float unit, byte cap, ReadOnlySpan<byte> distance) {
            Shape = shape; Unit = unit; Cap = cap; Distance = distance;
        }

        /// <summary>
        /// Whether the map can be used: a usable shape, a unit above zero, a cap above zero and at
        /// least one count per cell.
        /// </summary>
        public bool IsValid => WallDistance.Usable(Unit, Cap) && Shape.CellCount > 0 && Distance.Length >= Shape.CellCount;
    }
}
