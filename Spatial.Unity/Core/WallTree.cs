using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// One node of a <see cref="WallTree"/>: a cube of 4 by 4 by 4 children, numbered along x, then
    /// y, then z. A child that is mixed is a node of the next level down or, under the lowest
    /// nodes, a brick of 4 by 4 by 4 cells. A child that is not mixed holds one count all through:
    /// the cap where it is open, and otherwise 0.
    /// </summary>
    /// <remarks>Twenty-four bytes, so it can be stored as it is.</remarks>
    public readonly struct WallNode {
        /// <summary>A bit for each child that is a node or a brick.</summary>
        public readonly ulong Mixed;
        /// <summary>A bit for each child whose cells all hold the cap. No child is both mixed and open.</summary>
        public readonly ulong Open;
        /// <summary>Where its first mixed child lies among the nodes, or among the bricks. The others follow it in order.</summary>
        public readonly int First;
        /// <summary>The number of its first open child among all the tree's open cubes, counted in the nodes' order.</summary>
        public readonly int FirstLeaf;

        internal WallNode(ulong mixed, ulong open, int first, int firstLeaf) {
            Mixed = mixed; Open = open; First = first; FirstLeaf = firstLeaf;
        }
    }

    /// <summary>
    /// Wall distance for one grid as a tree: the counts a <see cref="WallMap"/> holds for the same
    /// grid and the same whole shapes, kept only where they differ and to four bits a cell.
    /// Space that is all open, or all inside a solid, is one bit.
    /// </summary>
    /// <remarks>
    /// For a grid that is mostly open air, where a count for every cell is too much to keep. The
    /// nodes lie level by level, the root first; under the lowest nodes are bricks of 64 cells,
    /// each kept to a fifteenth of the cap (see <see cref="WallTrees"/>). A cube of cells that
    /// all hold the cap is kept as one open child, and is never larger than 16 cells along a
    /// side; a cube of cells that all read 0 is one child of any size. Cells of the root's cube
    /// that are not in the grid hold 0.
    /// Make one with <see cref="WallTrees.Build"/> or <see cref="WallTrees.TryRead"/>. A tree put
    /// together any other way reads nothing out of bounds, and can give wrong answers.
    /// </remarks>
    public readonly ref struct WallTree {
        /// <summary>The grid whose cells the tree holds counts for.</summary>
        public readonly GridShape Shape;
        /// <summary>Metres per count.</summary>
        public readonly float Unit;
        /// <summary>The largest count stored.</summary>
        public readonly byte Cap;
        /// <summary>How many levels of nodes there are: see <see cref="WallTrees.Levels"/>.</summary>
        public readonly int Levels;
        /// <summary>The nodes, level by level, the root first.</summary>
        public readonly ReadOnlySpan<WallNode> Nodes;
        /// <summary>64 cells a brick, along x, then y, then z, four bits each: <see cref="WallTrees.BrickBytes"/> bytes a brick.</summary>
        public readonly ReadOnlySpan<byte> Bricks;
        // What eight cells at the cap are sure to prove at any point among them.
        internal readonly float OpenRoom;

        /// <param name="shape">The grid.</param>
        /// <param name="unit">Metres per count.</param>
        /// <param name="cap">The largest count stored.</param>
        /// <param name="nodes">Cut to the node count that the build gave.</param>
        /// <param name="bricks">Cut to the bricks that the build gave, <see cref="WallTrees.BrickBytes"/> bytes each.</param>
        public WallTree(GridShape shape, float unit, byte cap, ReadOnlySpan<WallNode> nodes, ReadOnlySpan<byte> bricks) {
            Shape = shape; Unit = unit; Cap = cap; Nodes = nodes; Bricks = bricks;
            Levels = WallTrees.Levels(shape);
            OpenRoom = WallDistance.Usable(unit, cap) ? (float)WallDistance.OpenRoom(unit, cap, shape.CellSize) : 0f;
        }

        /// <summary>
        /// Whether the tree can be used: a usable shape, a unit above zero, a cap above zero, a
        /// root, and whole bricks.
        /// </summary>
        public bool IsValid => WallDistance.Usable(Unit, Cap) && Levels > 0 && Nodes.Length > 0 && Bricks.Length % WallTrees.BrickBytes == 0;
    }
}
