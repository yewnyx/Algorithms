using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Costs over a <see cref="WallTree"/>: one byte for each leaf, a brick or an open cube, where a
    /// dense grid keeps one for each cell (see <see cref="CellCost"/>).
    /// </summary>
    /// <remarks>
    /// The bytes mean what <see cref="CellCost"/>'s do, and a table prices them for one kind of agent
    /// the same way. A leaf holds one cost for all its cells: a brick is 4 cells along a side, and an
    /// open cube at most 16. A cost written on part of a leaf is the whole leaf's, so a body keeps
    /// away from a little more than the solid filled. Nothing is allocated and nothing throws.
    /// </remarks>
    public static class TreeCosts {
        /// <summary>How many bytes a layer of costs over the tree is: one for each brick and each open cube. 0 if the tree cannot be used.</summary>
        public static int LayerLength(in WallTree tree) {
            if (!tree.IsValid) { return 0; }
            var leaves = (long)(tree.Bricks.Length / WallTrees.BrickBytes) + WallTrees.OpenCubes(tree);
            return leaves <= int.MaxValue ? (int)leaves : 0;
        }

        /// <summary>
        /// Writes <paramref name="value"/> as the cost of every leaf that holds the centre of a cell
        /// inside the solid. <paramref name="changed"/> is how many leaves it changed, and
        /// <paramref name="opened"/> how many of those were closed and are no longer.
        /// </summary>
        /// <remarks>
        /// The rule is <see cref="CellCost.Fill(in GridShape, in Solid, byte, Span{byte}, out int)"/>'s,
        /// taken up to the leaf. A cell inside a solid belongs to no leaf, and takes no cost. Returns
        /// false, writing nothing, if the tree cannot be used, <paramref name="layer"/> is not
        /// <see cref="LayerLength"/> bytes, or the solid may not be stored.
        /// </remarks>
        /// <param name="tree">The tree.</param>
        /// <param name="solid">Where to write.</param>
        /// <param name="value">The cost.</param>
        /// <param name="layer">The costs, a byte a leaf.</param>
        /// <param name="changed">How many leaves now hold another value.</param>
        /// <param name="opened">How many leaves were closed and are now open.</param>
        public static bool Fill(in WallTree tree, in Solid solid, byte value, Span<byte> layer, out int changed, out int opened) {
            changed = 0; opened = 0;
            var leaves = LayerLength(tree);
            if (leaves == 0 || layer.Length != leaves || !ShapeDistance.TryBox(solid, out var box)) { return false; }
            var shape = tree.Shape;
            double size = shape.CellSize;
            int fromX = _first(box.MinX, shape.OriginX, size, shape.CountX), toX = _last(box.MaxX, shape.OriginX, size, shape.CountX);
            int fromY = _first(box.MinY, shape.OriginY, size, shape.CountY), toY = _last(box.MaxY, shape.OriginY, size, shape.CountY);
            int fromZ = _first(box.MinZ, shape.OriginZ, size, shape.CountZ), toZ = _last(box.MaxZ, shape.OriginZ, size, shape.CountZ);
            for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                double centreZ = shape.Centre(shape.OriginZ, cellZ);
                for (var cellY = fromY; cellY <= toY; cellY++) {
                    double centreY = shape.Centre(shape.OriginY, cellY);
                    for (var cellX = fromX; cellX <= toX; cellX++) {
                        var leaf = WallTrees.LeafOf(tree, cellX, cellY, cellZ, out _);
                        // A leaf already given the value needs no more of its cells looked at.
                        if ((uint)leaf >= (uint)leaves || layer[leaf] == value) { continue; }
                        if (ShapeDistance.SolidSquared(solid, shape.Centre(shape.OriginX, cellX), centreY, centreZ) != 0d) { continue; }
                        if (layer[leaf] == CellCost.Closed) { opened++; }
                        layer[leaf] = value;
                        changed++;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// The cost of the leaf that holds a point, as the grid finds the point's cell: false, with 0,
        /// if the point is not in the grid, its cell is inside a solid, or the layer is not the tree's.
        /// </summary>
        public static bool At(in WallTree tree, ReadOnlySpan<byte> layer, float x, float y, float z, out byte value) {
            value = 0;
            var leaves = LayerLength(tree);
            if (leaves == 0 || layer.Length != leaves || !tree.Shape.TryCell(x, y, z, out var cellX, out var cellY, out var cellZ)) { return false; }
            var leaf = WallTrees.LeafOf(tree, cellX, cellY, cellZ, out _);
            if ((uint)leaf >= (uint)leaves) { return false; }
            value = layer[leaf];
            return true;
        }

        // The first and last cells along an axis whose centres lie at or within a bound, held to the grid.
        private static int _first(double bound, float origin, double size, int count) {
            var at = Math.Ceiling((bound - origin) / size - 0.5d);
            return at > 0d ? (at < count ? (int)at : count) : 0;
        }

        private static int _last(double bound, float origin, double size, int count) {
            var at = Math.Floor((bound - origin) / size - 0.5d);
            return at < count - 1 ? (at >= 0d ? (int)at : -1) : count - 1;
        }
    }
}
