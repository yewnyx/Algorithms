using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// What a body of one radius needs of a <see cref="WallMap"/>, and what it is promised in return.
    /// Made for one map's cell size and unit, and only good with a map that has the same.
    /// </summary>
    /// <remarks>
    /// With s the cell size and r the radius: a cell is open for the class when its centre is at
    /// least r from every shape. Between two open centres one cell apart, every point is at least
    /// sqrt(r * r - s * s / 4) from every shape: the <see cref="Guarantee"/>. A step across a
    /// diagonal keeps the same guarantee only if both its ends are farther out, which
    /// <see cref="Plane"/> and <see cref="Space"/> say. A radius of half a cell or less has no
    /// guarantee and is refused. <c>default</c> fits no map.
    /// </remarks>
    public readonly struct SizeClass {
        /// <summary>The radius the class was made for, in metres.</summary>
        public readonly float Radius;
        /// <summary>The least distance from every shape that is kept on any step between open cells.</summary>
        /// <remarks>
        /// Worked out from the radius asked for, and the figure to hold a body's size against. What
        /// a map's cells meet is this or a little more, never less: a cell is open from
        /// <see cref="Cell"/> counts, which is the radius rounded up to a whole count.
        /// </remarks>
        public readonly float Guarantee;
        /// <summary>
        /// What a sample along a straight leg must prove, so that the points between two samples a
        /// quarter of a cell apart keep <see cref="Radius"/>.
        /// </summary>
        public readonly float LegNeed;
        /// <summary>The cell size of the map the class was made for.</summary>
        public readonly float CellSize;
        /// <summary>The unit of the map the class was made for.</summary>
        public readonly float Unit;
        /// <summary>The least count a cell must hold to be open.</summary>
        public readonly byte Cell;
        /// <summary>The least count both ends of a step across a square's diagonal must hold.</summary>
        public readonly byte Plane;
        /// <summary>The least count both ends of a step across a cube's diagonal must hold.</summary>
        public readonly byte Space;
        /// <summary>The least cap a map must have for the class.</summary>
        public readonly byte Cap;

        private SizeClass(float radius, float guarantee, float legNeed, float cellSize, float unit, byte cell, byte plane, byte space, byte cap) {
            Radius = radius; Guarantee = guarantee; LegNeed = legNeed; CellSize = cellSize; Unit = unit;
            Cell = cell; Plane = plane; Space = space; Cap = cap;
        }

        /// <summary>
        /// Makes the class of a radius for a wall map. Only the map's cell size, unit and cap are
        /// read, so it need not be filled yet.
        /// </summary>
        /// <remarks>
        /// Returns false if the radius is not above half a cell, or the class needs more than the
        /// map's cap can say: a cell at the cap is only known to be that far or more, so a body that
        /// needed more would be told it fits where it may not. The cap must cover a step across a
        /// cube's diagonal, and a leg's sample in open space, where every cell holds the cap and the
        /// least the map proves, at the middle of four centres in a plane, is
        /// sqrt(cap * cap - s * s / 2).
        /// </remarks>
        public static bool TryMake(float radius, in WallMap wall, out SizeClass size) => _make(radius, wall.Shape.CellSize, wall.Unit, wall.Cap, out size);

        /// <summary>
        /// Makes the class of a radius for a wall tree: the class that
        /// <see cref="TryMake(float, in WallMap, out SizeClass)"/> makes for a map of the same cell
        /// size, unit and cap, and false where that is. Only those three are read.
        /// </summary>
        public static bool TryMake(float radius, in WallTree tree, out SizeClass size) => _make(radius, tree.Shape.CellSize, tree.Unit, tree.Cap, out size);

        private static bool _make(float radius, float cellSize, float mapUnit, byte mapCap, out SizeClass size) {
            size = default;
            double s = cellSize, unit = mapUnit, r = radius;
            if (!WallDistance.Usable(mapUnit, mapCap) || !(s > 0d) || double.IsInfinity(s) || !(r > 0.5d * s) || double.IsInfinity(r)) { return false; }
            double plane = Math.Sqrt(r * r + 0.25d * s * s), space = Math.Sqrt(r * r + 0.5d * s * s);
            var legNeed = Math.Sqrt(r * r + s * s / 64d) + Shapes.Slack;
            // The room at a point is what the cells prove less the slack, so the cap must cover that too.
            var open = Math.Sqrt((legNeed + Shapes.Slack) * (legNeed + Shapes.Slack) + 0.5d * s * s);
            var cap = Math.Ceiling((space > open ? space : open) / unit);
            if (!(cap <= mapCap)) { return false; }
            size = new SizeClass(radius, (float)Math.Sqrt(r * r - 0.25d * s * s), (float)legNeed, cellSize, mapUnit,
                (byte)Math.Ceiling(r / unit), (byte)Math.Ceiling(plane / unit), (byte)Math.Ceiling(space / unit), (byte)cap);
            return true;
        }

        /// <summary>Whether the class can be used with a map: it was made for the same cell size and unit, and the map's cap is enough.</summary>
        public bool Fits(in WallMap wall) => Cell > 0 && CellSize == wall.Shape.CellSize && Unit == wall.Unit && Cap <= wall.Cap;

        /// <summary>Whether the class can be used with a tree: it was made for the same cell size and unit, and the tree's cap is enough.</summary>
        public bool Fits(in WallTree tree) => Cell > 0 && CellSize == tree.Shape.CellSize && Unit == tree.Unit && Cap <= tree.Cap;
    }
}
