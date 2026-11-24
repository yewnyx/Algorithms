using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Builds a <see cref="ShapeSet"/>, stores one as a block of bytes and reads it back, and gives
    /// the exact distance from a point to the nearest shape.
    /// </summary>
    /// <remarks>
    /// Nothing is allocated and nothing throws. A set or block that is damaged reads nothing out of
    /// bounds and ends in bounded time; it can still give wrong answers, so a block is only as good
    /// as the shapes it was made from.
    /// The same shapes give the same block on every runtime: what is given is stored as it is, and
    /// what is worked out (each shape's box and the tree) uses add, subtract, multiply, one division
    /// and compare, each rounded to single.
    /// </remarks>
    public static class Shapes {
        /// <summary>
        /// Shapes must lie within this distance of the frame's origin on every axis. It bounds the
        /// rounding error of a distance, and so <see cref="Slack"/>.
        /// </summary>
        public const float Reach = 1024f;

        /// <summary>
        /// A distance this library works out is within this much of the true one, for shapes inside
        /// <see cref="Reach"/> and a point no farther than that from them.
        /// </summary>
        public const float Slack = 1f / 4096f;

        /// <summary>How much wider than its shape each stored box is, so rounding never leaves a shape outside its box.</summary>
        internal const float Pad = 1f / 1024f;

        /// <summary>The version of the block's layout and of the order its tree is in. A block of any other version is refused.</summary>
        public const int Version = 3;

        // A number that is the same for the same shapes and, as good as always, another for
        // others: what a tree's block keeps of the shapes it was built from. Every number of every
        // shape goes into it, and how many of each kind there are. The boxes and the tree over
        // them do not: they follow from the shapes.
        internal static ulong Fingerprint(in ShapeSet shapes) {
            var mark = 14695981039346656037UL;
            mark = _mix(mark, MemoryMarshal.Cast<Solid, uint>(shapes.Solids));
            mark = _mix(mark, MemoryMarshal.Cast<float, uint>(shapes.Corners));
            mark = _mix(mark, MemoryMarshal.Cast<int, uint>(shapes.Triangles));
            mark = _mix(mark, MemoryMarshal.Cast<HeightField, uint>(shapes.HeightFields));
            mark = _mix(mark, shapes.HeightBlocks);
            var heights = shapes.Heights;
            mark = (mark ^ (uint)heights.Length) * 1099511628211UL;
            for (var i = 0; i < heights.Length; i++) { mark = (mark ^ heights[i]) * 1099511628211UL; }
            return mark;
        }

        private static ulong _mix(ulong mark, ReadOnlySpan<uint> words) {
            mark = (mark ^ (uint)words.Length) * 1099511628211UL;
            for (var i = 0; i < words.Length; i++) { mark = (mark ^ words[i]) * 1099511628211UL; }
            return mark;
        }

        /// <summary>Which shapes a distance is taken to: all of them, all but the heightfields, or the heightfields alone.</summary>
        internal const int Every = 0, NoHeightFields = 1, OnlyHeightFields = 2;

        // "YSHP" when the block is read in the order it was written. Read the other way round it is a different number.
        private const int Magic = 0x50485359;
        private const int Header = 40;
        private const int SolidBytes = 64, CornerBytes = 12, TriangleBytes = 12, FieldBytes = 92, BlockBytes = 4, HeightBytes = 2;
        private const int BoxBytes = 24, OrderBytes = 4, NodeBytes = 32;
        private const int QueryStack = 64;

        /// <summary>Nodes needed for this many shapes.</summary>
        public static int NodesLength(int count) => BoxTree.NodesLength(count);

        /// <summary>Workspace needed to build over this many shapes.</summary>
        public static int WorkspaceLength(int count) => BoxTree.WorkspaceLength(count);

        /// <summary>
        /// Works out the box around every shape and the tree over them. A <see cref="ShapeSet"/> of the
        /// same solids, corners and triangles with these boxes, <paramref name="nodes"/> cut to
        /// <paramref name="nodeCount"/> and this order is then ready to use.
        /// </summary>
        /// <remarks>
        /// Returns false if a buffer is too short (boxes and order need one element per shape, nodes
        /// <see cref="NodesLength"/> and workspace <see cref="WorkspaceLength"/>), a triangle names a
        /// corner that is not there, or a shape may not be stored: a number that is not finite, a
        /// negative size, a box whose axes are not of length 1 and square to each other, or a shape
        /// that reaches beyond <see cref="Reach"/>.
        /// </remarks>
        public static bool Build(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            Span<Box> boxes, Span<BoxNode> nodes, Span<int> order, Span<int> workspace, out int nodeCount) =>
            Build(solids, corners, triangles, default, default, default, boxes, nodes, order, workspace, out nodeCount);

        /// <summary>
        /// As the other Build, with heightfields: the shapes after the triangles. Each must be as
        /// <see cref="HeightFields.Pack"/> made it, with its blocks and kept heights in
        /// <paramref name="heightBlocks"/> and <paramref name="heights"/> from where it says.
        /// </summary>
        /// <remarks>
        /// Also returns false if a heightfield may not be stored: its axes are not of length 1 and
        /// square to each other, its floor is above its lowest height, it reaches beyond
        /// <see cref="Reach"/>, or one of its blocks cannot be read from the spans given.
        /// </remarks>
        public static bool Build(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<HeightField> heightFields, ReadOnlySpan<uint> heightBlocks, ReadOnlySpan<ushort> heights,
            Span<Box> boxes, Span<BoxNode> nodes, Span<int> order, Span<int> workspace, out int nodeCount) {
            nodeCount = 0;
            if (corners.Length % 3 != 0 || triangles.Length % 3 != 0) { return false; }
            var total = (long)solids.Length + triangles.Length / 3 + heightFields.Length;
            if (total > int.MaxValue / 4) { return false; }
            var count = (int)total;
            if (boxes.Length < count || order.Length < count || nodes.Length < NodesLength(count) || workspace.Length < WorkspaceLength(count)) { return false; }

            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            int cornerCount = corners.Length / 3, firstField = solids.Length + triangles.Length / 3;
            for (var i = 0; i < count; i++) {
                Box box;
                if (i < solids.Length) {
                    if (!ShapeDistance.TryBox(solids[i], out box)) { return false; }
                } else if (i < firstField) {
                    var at = (i - solids.Length) * 3;
                    int a = triangles[at], b = triangles[at + 1], c = triangles[at + 2];
                    if ((uint)a >= (uint)cornerCount || (uint)b >= (uint)cornerCount || (uint)c >= (uint)cornerCount) { return false; }
                    a *= 3; b *= 3; c *= 3;
                    if (!ShapeDistance.TryBox(corners[a], corners[a + 1], corners[a + 2], corners[b], corners[b + 1], corners[b + 2],
                            corners[c], corners[c + 1], corners[c + 2], out box)) { return false; }
                } else {
                    var field = heightFields[i - firstField];
                    if (!HeightDistance.TryBox(field, out box) || !HeightDistance.Check(field, heightBlocks, heights)) { return false; }
                }
                boxes[i] = box;
                minX = box.MinX < minX ? box.MinX : minX; minY = box.MinY < minY ? box.MinY : minY; minZ = box.MinZ < minZ ? box.MinZ : minZ;
                maxX = box.MaxX > maxX ? box.MaxX : maxX; maxY = box.MaxY > maxY ? box.MaxY : maxY; maxZ = box.MaxZ > maxZ ? box.MaxZ : maxZ;
            }
            var extent = count == 0 ? default : new Box(minX, minY, minZ, maxX, maxY, maxZ);
            return BoxTree.Build(boxes.Slice(0, count), extent, nodes, order, workspace, out nodeCount);
        }

        /// <summary>
        /// The most bytes a block of this many solids, corners and triangles takes, or -1 if that is
        /// more than an int holds or a count is negative.
        /// </summary>
        public static int BlockLength(int solids, int corners, int triangles) => BlockLength(solids, corners, triangles, 0, 0, 0);

        /// <summary>
        /// The most bytes a block takes with this many heightfields as well, their blocks and their
        /// kept heights, or -1 if that is more than an int holds or a count is negative.
        /// </summary>
        public static int BlockLength(int solids, int corners, int triangles, int heightFields, int heightBlocks, int heights) {
            if (solids < 0 || corners < 0 || triangles < 0 || heightFields < 0 || heightBlocks < 0 || heights < 0) { return -1; }
            var count = (long)solids + triangles + heightFields;
            var length = _before(solids, corners, triangles, heightFields, heightBlocks, heights) + count * (BoxBytes + OrderBytes + NodeBytes);
            return count <= int.MaxValue / 4 && length <= int.MaxValue ? (int)length : -1;
        }

        // The bytes before the boxes: the header and what was given. The kept heights are two bytes
        // each, and end on a multiple of four so that what follows stays in step.
        private static long _before(long solids, long corners, long triangles, long heightFields, long heightBlocks, long heights) =>
            Header + solids * SolidBytes + corners * CornerBytes + triangles * TriangleBytes +
            heightFields * FieldBytes + heightBlocks * BlockBytes + (heights + 1) / 2 * (2 * HeightBytes);

        /// <summary>
        /// Builds the boxes and the tree and writes everything as one block: the bytes to store.
        /// <paramref name="length"/> is how many bytes were written, never more than <see cref="BlockLength(int, int, int)"/>.
        /// </summary>
        /// <remarks>
        /// Returns false where <see cref="Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/>
        /// would, if <paramref name="block"/> is shorter than <see cref="BlockLength(int, int, int)"/>,
        /// or on a machine that stores numbers highest byte first. The block may hold anything after
        /// a false return.
        /// </remarks>
        public static bool Write(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            Span<int> workspace, Span<byte> block, out int length) =>
            Write(solids, corners, triangles, default, default, default, workspace, block, out length);

        /// <summary>As the other Write, with heightfields: see <see cref="Build(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, ReadOnlySpan{HeightField}, ReadOnlySpan{uint}, ReadOnlySpan{ushort}, Span{Box}, Span{BoxNode}, Span{int}, Span{int}, out int)"/>.</summary>
        public static bool Write(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<HeightField> heightFields, ReadOnlySpan<uint> heightBlocks, ReadOnlySpan<ushort> heights,
            Span<int> workspace, Span<byte> block, out int length) {
            length = 0;
            if (!BitConverter.IsLittleEndian || corners.Length % 3 != 0 || triangles.Length % 3 != 0) { return false; }
            int cornerCount = corners.Length / 3, triangleCount = triangles.Length / 3;
            var most = BlockLength(solids.Length, cornerCount, triangleCount, heightFields.Length, heightBlocks.Length, heights.Length);
            if (most < 0 || block.Length < most) { return false; }
            var count = solids.Length + triangleCount + heightFields.Length;

            var at = Header;
            var solidPart = MemoryMarshal.Cast<byte, Solid>(block.Slice(at, solids.Length * SolidBytes)); at += solids.Length * SolidBytes;
            var cornerPart = MemoryMarshal.Cast<byte, float>(block.Slice(at, cornerCount * CornerBytes)); at += cornerCount * CornerBytes;
            var trianglePart = MemoryMarshal.Cast<byte, int>(block.Slice(at, triangleCount * TriangleBytes)); at += triangleCount * TriangleBytes;
            var fieldPart = MemoryMarshal.Cast<byte, HeightField>(block.Slice(at, heightFields.Length * FieldBytes)); at += heightFields.Length * FieldBytes;
            var blockPart = MemoryMarshal.Cast<byte, uint>(block.Slice(at, heightBlocks.Length * BlockBytes)); at += heightBlocks.Length * BlockBytes;
            var heightRoom = block.Slice(at, (heights.Length + 1) / 2 * (2 * HeightBytes)); at += heightRoom.Length;
            var boxPart = MemoryMarshal.Cast<byte, Box>(block.Slice(at, count * BoxBytes)); at += count * BoxBytes;
            var orderPart = MemoryMarshal.Cast<byte, int>(block.Slice(at, count * OrderBytes)); at += count * OrderBytes;
            var nodePart = MemoryMarshal.Cast<byte, BoxNode>(block.Slice(at, count * NodeBytes));
            if (!Build(solids, corners, triangles, heightFields, heightBlocks, heights, boxPart, nodePart, orderPart, workspace, out var nodeCount)) { return false; }
            solids.CopyTo(solidPart);
            corners.CopyTo(cornerPart);
            triangles.CopyTo(trianglePart);
            heightFields.CopyTo(fieldPart);
            heightBlocks.CopyTo(blockPart);
            // What the buffer held before must not show where an odd count leaves two bytes over.
            heightRoom.Clear();
            heights.CopyTo(MemoryMarshal.Cast<byte, ushort>(heightRoom));

            var header = block.Slice(0, Header);
            header.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), solids.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), cornerCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16), triangleCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20), nodeCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), heightFields.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28), heightBlocks.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32), heights.Length);
            length = at + nodeCount * NodeBytes;
            return true;
        }

        /// <summary>
        /// Views a block that <see cref="Write(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{int}, Span{byte}, out int)"/>
        /// made as a <see cref="ShapeSet"/>, without copying. <paramref name="block"/> must be cut to
        /// the length that Write gave.
        /// </summary>
        /// <remarks>
        /// Checks the header, the version and that the lengths add up to the block's, in constant
        /// time. It does not look at the shapes; queries check each number they read. Returns false
        /// for a block of another version, one written highest byte first, or on such a machine.
        /// The set views the block's bytes, so it is good for as long as they are.
        /// </remarks>
        public static bool TryRead(ReadOnlySpan<byte> block, out ShapeSet shapes) {
            shapes = default;
            if (!BitConverter.IsLittleEndian || block.Length < Header) { return false; }
            if (BinaryPrimitives.ReadInt32LittleEndian(block) != Magic || BinaryPrimitives.ReadInt32LittleEndian(block.Slice(4)) != Version) { return false; }
            int solids = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(8)), corners = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(12));
            int triangles = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(16)), nodes = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(20));
            int fields = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(24)), blocks = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(28));
            var heights = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(32));
            if (solids < 0 || corners < 0 || triangles < 0 || nodes < 0 || fields < 0 || blocks < 0 || heights < 0) { return false; }
            // Added in 64 bits: counts near the top of an int would wrap round to a length that fits.
            var count = (long)solids + triangles + fields;
            var length = _before(solids, corners, triangles, fields, blocks, heights) + count * (BoxBytes + OrderBytes) + (long)nodes * NodeBytes;
            if (length != block.Length || nodes > count || (nodes == 0) != (count == 0)) { return false; }

            var at = Header;
            var solidPart = MemoryMarshal.Cast<byte, Solid>(block.Slice(at, solids * SolidBytes)); at += solids * SolidBytes;
            var cornerPart = MemoryMarshal.Cast<byte, float>(block.Slice(at, corners * CornerBytes)); at += corners * CornerBytes;
            var trianglePart = MemoryMarshal.Cast<byte, int>(block.Slice(at, triangles * TriangleBytes)); at += triangles * TriangleBytes;
            var fieldPart = MemoryMarshal.Cast<byte, HeightField>(block.Slice(at, fields * FieldBytes)); at += fields * FieldBytes;
            var blockPart = MemoryMarshal.Cast<byte, uint>(block.Slice(at, blocks * BlockBytes)); at += blocks * BlockBytes;
            var heightPart = MemoryMarshal.Cast<byte, ushort>(block.Slice(at, heights * HeightBytes)); at += (heights + 1) / 2 * (2 * HeightBytes);
            var boxPart = MemoryMarshal.Cast<byte, Box>(block.Slice(at, (int)count * BoxBytes)); at += (int)count * BoxBytes;
            var orderPart = MemoryMarshal.Cast<byte, int>(block.Slice(at, (int)count * OrderBytes)); at += (int)count * OrderBytes;
            var nodePart = MemoryMarshal.Cast<byte, BoxNode>(block.Slice(at, nodes * NodeBytes));
            shapes = new ShapeSet(solidPart, cornerPart, trianglePart, fieldPart, blockPart, heightPart, boxPart, nodePart, orderPart);
            return true;
        }

        /// <summary>
        /// The distance from a point to the nearest shape, 0 inside a solid or under a heightfield's
        /// surface, or <paramref name="limit"/> if nothing is nearer than that.
        /// </summary>
        /// <remarks>
        /// Within <see cref="Slack"/> of the true distance. A set with no shapes gives the limit.
        /// Returns false if the point is not a number, the limit is negative or not a number, the
        /// set's spans do not belong together, or the walk finds the set damaged; the distance is
        /// then 0, so that a refused query never reads as room. It says how far
        /// the nearest surface is, not whether the point is in open space: a point deep inside a
        /// closed shell of triangles is far from every one of them.
        /// </remarks>
        public static bool Distance(in ShapeSet shapes, float x, float y, float z, float limit, out float distance) =>
            DistanceAmong(shapes, Every, x, y, z, limit, out distance);

        // The same, to some of the shapes only.
        internal static bool DistanceAmong(in ShapeSet shapes, int which, float x, float y, float z, float limit, out float distance) {
            distance = 0f;
            if (!(limit >= 0f) || !NearestSquared(shapes, which, x, y, z, (double)limit * limit, out var best)) { return false; }
            var found = (float)Math.Sqrt(best);
            distance = found < limit ? found : limit;
            return true;
        }

        // The squared distance from a point to the nearest of some of the shapes, where that is
        // below within, and otherwise within. False, with 0, where Distance would be refused.
        internal static bool NearestSquared(in ShapeSet shapes, int which, float x, float y, float z, double within, out double best) {
            best = 0d;
            var count = shapes.Count;
            if (count < 0 || !(within >= 0d) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return false; }
            if (count == 0) {
                best = within;
                return true;
            }

            ReadOnlySpan<BoxNode> nodes = shapes.Nodes;
            ReadOnlySpan<int> order = shapes.Order;
            ReadOnlySpan<Box> boxes = shapes.Boxes;
            var firstField = count - shapes.HeightFields.Length;
            Span<int> stack = stackalloc int[QueryStack];
            // The best squared distance so far. A node or a shape whose box is no nearer cannot beat it.
            best = within;
            int top = 0, visits = 0;
            stack[top++] = 0;
            while (top > 0 && best > 0d) {
                if (++visits > nodes.Length) { best = 0d; return false; }
                var index = stack[--top];
                var node = nodes[index];
                if (!(BoxDistance.Squared(node.MinX, node.MinY, node.MinZ, node.MaxX, node.MaxY, node.MaxZ, x, y, z) < best)) { continue; }
                if (node.Count <= 0) {
                    if (!(node.First > index + 1 && node.First < nodes.Length && top + 2 <= QueryStack)) { best = 0d; return false; }
                    // The nearer child is walked first, so the farther one is more often skipped.
                    BoxNode left = nodes[index + 1], right = nodes[node.First];
                    var leftReach = BoxDistance.Squared(left.MinX, left.MinY, left.MinZ, left.MaxX, left.MaxY, left.MaxZ, x, y, z);
                    var rightReach = BoxDistance.Squared(right.MinX, right.MinY, right.MinZ, right.MaxX, right.MaxY, right.MaxZ, x, y, z);
                    if (leftReach <= rightReach) {
                        stack[top++] = node.First;
                        stack[top++] = index + 1;
                    } else {
                        stack[top++] = index + 1;
                        stack[top++] = node.First;
                    }
                    continue;
                }
                if (!(node.First >= 0 && node.Count <= order.Length && node.First <= order.Length - node.Count)) { best = 0d; return false; }
                for (var j = 0; j < node.Count; j++) {
                    var item = order[node.First + j];
                    if ((uint)item >= (uint)count) { best = 0d; return false; }
                    if (which != Every && (which == NoHeightFields) == (item >= firstField)) { continue; }
                    var box = boxes[item];
                    if (!(BoxDistance.Squared(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ, x, y, z) < best)) { continue; }
                    if (!TrySquared(shapes, item, x, y, z, best, out var squared)) { best = 0d; return false; }
                    if (squared < best) { best = squared; }
                }
            }
            return true;
        }

        // The squared distance from a point to shape number item, which must be below the set's
        // count. For a heightfield it is that only where it is below within, and otherwise a number
        // that is not below it. False if a triangle names a corner that is not there, or a
        // heightfield's block cannot be read.
        internal static bool TrySquared(in ShapeSet shapes, int item, double x, double y, double z, double within, out double squared) {
            if (item < shapes.Solids.Length) {
                squared = ShapeDistance.SolidSquared(shapes.Solids[item], x, y, z);
                return true;
            }
            squared = 0d;
            ReadOnlySpan<float> corners = shapes.Corners;
            ReadOnlySpan<int> triangles = shapes.Triangles;
            var at = (item - shapes.Solids.Length) * 3;
            if (at >= triangles.Length) {
                var field = (at - triangles.Length) / 3;
                return (uint)field < (uint)shapes.HeightFields.Length &&
                       HeightDistance.TrySquared(shapes.HeightFields[field], shapes.HeightBlocks, shapes.Heights, x, y, z, within, out squared);
            }
            // Each number is compared with a length before it is used to read with.
            if (at < 0 || at > triangles.Length - 3) { return false; }
            int a = triangles[at], b = triangles[at + 1], c = triangles[at + 2];
            var cornerCount = corners.Length / 3;
            if ((uint)a >= (uint)cornerCount || (uint)b >= (uint)cornerCount || (uint)c >= (uint)cornerCount) { return false; }
            a *= 3; b *= 3; c *= 3;
            squared = ShapeDistance.TriangleSquared(corners[a], corners[a + 1], corners[a + 2], corners[b], corners[b + 1], corners[b + 2],
                corners[c], corners[c + 1], corners[c + 2], x, y, z);
            return true;
        }
    }
}
