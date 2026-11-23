using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// The shapes in one grid's frame that nothing may pass through: solids and triangles, with the
    /// box around each and a <see cref="BoxTree"/> over those boxes. Six spans that belong together.
    /// </summary>
    /// <remarks>
    /// Shape i is solid i while i is below the number of solids, and after that triangle
    /// i - solids. A triangle is three corner numbers; corner n is the three coordinates from
    /// Corners[3 * n]. <see cref="Shapes.Build"/> fills the boxes, the nodes and the order.
    /// <c>default</c> is a set with no shapes.
    /// </remarks>
    public readonly ref struct ShapeSet {
        public readonly ReadOnlySpan<Solid> Solids;
        public readonly ReadOnlySpan<float> Corners;
        public readonly ReadOnlySpan<int> Triangles;
        public readonly ReadOnlySpan<Box> Boxes;
        public readonly ReadOnlySpan<BoxNode> Nodes;
        public readonly ReadOnlySpan<int> Order;

        /// <param name="nodes">Cut to the node count that <see cref="Shapes.Build"/> gave.</param>
        public ShapeSet(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<Box> boxes, ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order) {
            Solids = solids; Corners = corners; Triangles = triangles;
            Boxes = boxes; Nodes = nodes; Order = order;
        }

        /// <summary>The number of shapes, or -1 if the six spans do not belong together.</summary>
        public int Count {
            get {
                if (Corners.Length % 3 != 0 || Triangles.Length % 3 != 0) { return -1; }
                var count = (long)Solids.Length + Triangles.Length / 3;
                return Boxes.Length == count && Order.Length == count && Nodes.Length <= count && (Nodes.Length == 0) == (count == 0)
                    ? (int)count : -1;
            }
        }
    }
}
