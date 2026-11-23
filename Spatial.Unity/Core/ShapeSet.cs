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
        public readonly ReadOnlySpan<Solid> Solids;
        public readonly ReadOnlySpan<float> Corners;
        public readonly ReadOnlySpan<int> Triangles;
        public readonly ReadOnlySpan<HeightField> HeightFields;
        public readonly ReadOnlySpan<uint> HeightBlocks;
        public readonly ReadOnlySpan<ushort> Heights;
        public readonly ReadOnlySpan<Box> Boxes;
        public readonly ReadOnlySpan<BoxNode> Nodes;
        public readonly ReadOnlySpan<int> Order;

        /// <summary>A set with no heightfields.</summary>
        /// <param name="nodes">Cut to the node count that <see cref="Shapes.Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/> gave.</param>
        public ShapeSet(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<Box> boxes, ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order) {
            Solids = solids; Corners = corners; Triangles = triangles;
            HeightFields = default; HeightBlocks = default; Heights = default;
            Boxes = boxes; Nodes = nodes; Order = order;
        }

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
