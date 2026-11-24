using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Where a grid of cubic cells sits in its own frame: its lowest corner, the size of a cell and
    /// how many cells lie along each axis.
    /// </summary>
    /// <remarks>
    /// Queries take and return points, never cell numbers. Arrays that hold one value per cell run
    /// along x first, then y, then z, so the cells of one z layer lie together; a caller that walks
    /// such an array it owns, to draw it for instance, finds a cell's place with
    /// <see cref="IndexOf"/> and its centre with <see cref="CentreOf"/>.
    /// </remarks>
    public readonly struct GridShape {
        /// <summary>The lowest corner of the grid, in its frame.</summary>
        public readonly float OriginX, OriginY, OriginZ;
        /// <summary>The length of a cell's side. Every cell is a cube.</summary>
        public readonly float CellSize;
        /// <summary>How many cells lie along x, y and z.</summary>
        public readonly int CountX, CountY, CountZ;

        /// <summary>Makes a shape from its lowest corner, the size of a cell and the number of cells along each axis. Nothing is checked: <see cref="CellCount"/> is 0 if the shape cannot be used.</summary>
        public GridShape(float originX, float originY, float originZ, float cellSize, int countX, int countY, int countZ) {
            OriginX = originX; OriginY = originY; OriginZ = originZ;
            CellSize = cellSize;
            CountX = countX; CountY = countY; CountZ = countZ;
        }

        /// <summary>
        /// The number of cells, or 0 if the shape cannot be used: a cell size that is not above zero,
        /// an origin or size that is not a finite number, an axis with no cells, or more cells than an
        /// int holds.
        /// </summary>
        public int CellCount {
            get {
                if (!(CellSize > 0f) || float.IsInfinity(CellSize) || CountX <= 0 || CountY <= 0 || CountZ <= 0) { return 0; }
                var sum = (double)OriginX + OriginY + OriginZ;
                if (!(sum - sum == 0d)) { return 0; }
                var cells = (long)CountX * CountY * CountZ;
                return cells <= int.MaxValue && (long)CountX * CountY <= int.MaxValue ? (int)cells : 0;
            }
        }

        /// <summary>The number of cells in one z layer, or 0 if the shape cannot be used.</summary>
        public int LayerLength => CellCount == 0 ? 0 : CountX * CountY;

        /// <summary>The box the cells fill.</summary>
        public Box Bounds => new Box(OriginX, OriginY, OriginZ,
            (float)(OriginX + CountX * (double)CellSize), (float)(OriginY + CountY * (double)CellSize), (float)(OriginZ + CountZ * (double)CellSize));

        /// <summary>
        /// The centre of the cell that holds a point. Returns false if the point is outside the grid
        /// or not a number, or the shape cannot be used.
        /// </summary>
        public bool TryCentre(float x, float y, float z, out float centreX, out float centreY, out float centreZ) {
            if (!TryCell(x, y, z, out var cellX, out var cellY, out var cellZ)) {
                centreX = 0f; centreY = 0f; centreZ = 0f;
                return false;
            }
            centreX = Centre(OriginX, cellX); centreY = Centre(OriginY, cellY); centreZ = Centre(OriginZ, cellZ);
            return true;
        }

        /// <summary>
        /// Where the value of the cell that is cellX along x, cellY along y and cellZ along z sits in
        /// an array of one value per cell, or -1 if there is no such cell or the shape cannot be used.
        /// </summary>
        public int IndexOf(int cellX, int cellY, int cellZ) =>
            CellCount != 0 && (uint)cellX < (uint)CountX && (uint)cellY < (uint)CountY && (uint)cellZ < (uint)CountZ ? Index(cellX, cellY, cellZ) : -1;

        /// <summary>The centre of the cell that is cellX along x, cellY along y and cellZ along z. The cell need not be in the grid.</summary>
        public void CentreOf(int cellX, int cellY, int cellZ, out float x, out float y, out float z) {
            x = Centre(OriginX, cellX); y = Centre(OriginY, cellY); z = Centre(OriginZ, cellZ);
        }

        // Whether a point is in the box the cells fill, its faces included. It is tested against
        // Bounds as a caller sees them, rounded to single, so that every corner of Bounds is inside.
        internal bool Holds(float x, float y, float z) {
            if (CellCount == 0) { return false; }
            var bounds = Bounds;
            // A comparison with not-a-number is false, so such a point is outside.
            return x >= bounds.MinX && x <= bounds.MaxX && y >= bounds.MinY && y <= bounds.MaxY && z >= bounds.MinZ && z <= bounds.MaxZ;
        }

        // Which cell holds a point. A point on the grid's upper faces is outside.
        internal bool TryCell(float x, float y, float z, out int cellX, out int cellY, out int cellZ) {
            cellX = 0; cellY = 0; cellZ = 0;
            if (CellCount == 0) { return false; }
            double atX = Math.Floor(((double)x - OriginX) / CellSize), atY = Math.Floor(((double)y - OriginY) / CellSize), atZ = Math.Floor(((double)z - OriginZ) / CellSize);
            // A comparison with not-a-number is false, so such a point is outside.
            if (!(atX >= 0d && atX < CountX && atY >= 0d && atY < CountY && atZ >= 0d && atZ < CountZ)) { return false; }
            cellX = (int)atX; cellY = (int)atY; cellZ = (int)atZ;
            return true;
        }

        // The one place a cell's centre is worked out, so every caller agrees on it to the last bit.
        internal float Centre(float origin, int cell) => (float)(origin + (cell + 0.5d) * CellSize);

        internal int Index(int cellX, int cellY, int cellZ) => cellX + CountX * (cellY + CountY * cellZ);
    }
}
