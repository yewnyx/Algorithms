using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// The shapes in one grid's frame that nothing may pass through: solids, triangles and
    /// heightfields, with the box around each and a <see cref="BoxTree"/> over those boxes. Spans
    /// that belong together.
    /// </summary>
    /// <remarks>
    /// Shape i is solid i while i is below the number of solids, after that triangle
    /// i - solids, and after those a heightfield. A triangle is three corner numbers; corner n is
    /// the three coordinates from Corners[3 * n]. A heightfield's blocks and kept heights lie in
    /// <see cref="HeightBlocks"/> and <see cref="Heights"/> from where it says (see
    /// <see cref="HeightFields.Pack"/>). <see cref="Shapes.Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/>
    /// fills the boxes, the nodes and the order. <c>default</c> is a set with no shapes.
    /// </remarks>
    public readonly ref struct ShapeSet {
        /// <summary>The solids. Shape i is solid i while i is below their number.</summary>
        public readonly ReadOnlySpan<Solid> Solids;
        /// <summary>The triangles' corner coordinates, three for each corner.</summary>
        public readonly ReadOnlySpan<float> Corners;
        /// <summary>Three corner numbers for each triangle.</summary>
        public readonly ReadOnlySpan<int> Triangles;
        /// <summary>The heightfields, which come after the triangles among the shapes.</summary>
        public readonly ReadOnlySpan<HeightField> HeightFields;
        /// <summary>The blocks of all the heightfields together. Each heightfield's begin at its <see cref="HeightField.FirstBlock"/>.</summary>
        public readonly ReadOnlySpan<uint> HeightBlocks;
        /// <summary>The kept heights of all the heightfields together. Each heightfield's begin at its <see cref="HeightField.FirstHeight"/>.</summary>
        public readonly ReadOnlySpan<ushort> Heights;
        /// <summary>The box around each shape: the solids first, then the triangles, then the heightfields.</summary>
        public readonly ReadOnlySpan<Box> Boxes;
        /// <summary>The nodes of the <see cref="BoxTree"/> over the boxes.</summary>
        public readonly ReadOnlySpan<BoxNode> Nodes;
        /// <summary>The tree's order: the shape numbers, as the tree's leaves hold them.</summary>
        public readonly ReadOnlySpan<int> Order;

        /// <summary>Views spans as a set with no heightfields. Nothing is copied or checked: <see cref="Count"/> says whether they belong together.</summary>
        /// <param name="solids">The solids.</param>
        /// <param name="corners">The triangles' corner coordinates, three for each corner.</param>
        /// <param name="triangles">Three corner numbers for each triangle.</param>
        /// <param name="boxes">The box around each shape: the solids first, then the triangles.</param>
        /// <param name="order">The tree's order, as the build gave it.</param>
        /// <param name="nodes">Cut to the node count that <see cref="Shapes.Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/> gave.</param>
        public ShapeSet(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<Box> boxes, ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order) {
            Solids = solids; Corners = corners; Triangles = triangles;
            HeightFields = default; HeightBlocks = default; Heights = default;
            Boxes = boxes; Nodes = nodes; Order = order;
        }

        /// <summary>Views spans as a set with heightfields. Nothing is copied or checked: <see cref="Count"/> says whether they belong together.</summary>
        /// <param name="solids">The solids.</param>
        /// <param name="corners">The triangles' corner coordinates, three for each corner.</param>
        /// <param name="triangles">Three corner numbers for each triangle.</param>
        /// <param name="heightFields">The heightfields.</param>
        /// <param name="heightBlocks">The blocks of all the heightfields together.</param>
        /// <param name="heights">The kept heights of all the heightfields together.</param>
        /// <param name="boxes">The box around each shape: the solids first, then the triangles, then the heightfields.</param>
        /// <param name="order">The tree's order, as the build gave it.</param>
        /// <param name="nodes">Cut to the node count that the build gave.</param>
        public ShapeSet(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<HeightField> heightFields, ReadOnlySpan<uint> heightBlocks, ReadOnlySpan<ushort> heights,
            ReadOnlySpan<Box> boxes, ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order) {
            Solids = solids; Corners = corners; Triangles = triangles;
            HeightFields = heightFields; HeightBlocks = heightBlocks; Heights = heights;
            Boxes = boxes; Nodes = nodes; Order = order;
        }

        /// <summary>The number of shapes, or -1 if the spans do not belong together.</summary>
        public int Count {
            get {
                if (Corners.Length % 3 != 0 || Triangles.Length % 3 != 0) { return -1; }
                var count = (long)Solids.Length + Triangles.Length / 3 + HeightFields.Length;
                return Boxes.Length == count && Order.Length == count && Nodes.Length <= count && (Nodes.Length == 0) == (count == 0)
                    ? (int)count : -1;
            }
        }

        /// <summary>The most any of the heightfields' surfaces lies above the ground it was made from: 0 with none.</summary>
        internal float Gap {
            get {
                var most = 0f;
                for (var i = 0; i < HeightFields.Length; i++) {
                    if (HeightFields[i].Gap > most) { most = HeightFields[i].Gap; }
                }
                return most;
            }
        }
    }
}
