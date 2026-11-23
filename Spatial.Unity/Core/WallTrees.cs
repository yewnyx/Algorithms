using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Builds a <see cref="WallTree"/> from shapes, stores one as a block of bytes and reads it
    /// back, and says how much room there is at a point from the tree alone.
    /// </summary>
    /// <remarks>
    /// A tree holds, for every cell of its grid, the count that <see cref="WallDistance.MarkLayer"/>
    /// gives that cell from the same shapes, for shapes that are whole, put down to a fifteenth
    /// of the cap: a brick keeps four bits a cell. A count c is kept as c * 15 / cap, rounded
    /// down, and read back as the least count that is kept so. So a cell's count is never more
    /// than the dense map's and less by under a fifteenth of the cap, the cap and 0 are kept as
    /// they are, and with a cap of 15 or less nothing is lost. <see cref="RoomAt"/> gives what
    /// <see cref="WallDistance.RoomAt"/> gives for the dense map with its counts put down the
    /// same way, to the bit: a little less room than the dense map proves, never more. What
    /// else differs is what is kept: a count for every cell only near the shapes' surfaces.
    /// A solid that is damaged reads as at distance 0 from everything. The dense map gives 0 to
    /// every cell near its box. A tree measures a shape only while its box is nearer than the
    /// nearest shape found so far, so where another shape is nearer than that box a cell holds
    /// the distance to the other shape: more than the dense map holds, and never more than the
    /// distance to the damaged solid's box.
    /// Nothing is allocated and nothing throws. After a false return the outputs are not to be read.
    /// The same shapes give the same tree, and the same block, on every runtime.
    /// </remarks>
    public static class WallTrees {
        /// <summary>The cells of a brick: 4 by 4 by 4.</summary>
        internal const int BrickCells = 64;

        /// <summary>The bytes of a brick: four bits a cell, the lower half of a byte for the first of its two cells.</summary>
        public const int BrickBytes = 32;

        // How many steps below the cap a brick's four bits tell apart.
        private const int Steps = 15;

        /// <summary>The version of the block's layout. A block of any other version is refused.</summary>
        public const int Version = 1;

        /// <summary>An open cube is never more than this many cells along a side.</summary>
        internal const int MostOpen = 16;

        // "YWTR" when the block is read in the order it was written.
        private const int Magic = 0x52545759;
        private const int Header = 64, NodeBytes = 24;
        // A root of 4 to the power of 15 cells along a side, which is 2 to the power of 30: the
        // largest whose cubes' first cells an int can count.
        private const int MostLevels = 14;
        private const int Solid = 0, Open = 1, Mixed = 2, Damaged = -1;
        private const int QueryStack = 64;

        /// <summary>
        /// How many levels of nodes a tree over a grid has, or 0 if the shape cannot be used or
        /// has more than 2 to the power of 30 cells along a side: no tree is made for such a
        /// grid. The root is a cube of 4 times 4 to the power of that many cells along a side:
        /// the smallest such cube that holds the grid, from the grid's first corner.
        /// </summary>
        public static int Levels(in GridShape shape) {
            if (shape.CellCount == 0) { return 0; }
            var longest = shape.CountX > shape.CountY ? shape.CountX : shape.CountY;
            if (shape.CountZ > longest) { longest = shape.CountZ; }
            if (longest > 1 << 30) { return 0; }
            var levels = 1;
            while ((4L << (2 * levels)) < longest) { levels++; }
            return levels;
        }

        /// <summary>
        /// How many nodes and bricks <see cref="Build"/> needs room for. These are upper figures,
        /// found without marking a cell: a build keeps fewer bricks, since some turn out to hold
        /// one count all through.
        /// </summary>
        /// <remarks>
        /// Returns false if the shape cannot be used or is too long for a tree (see
        /// <see cref="Levels"/>), the unit is not above zero, the cap is zero,
        /// or the set's spans do not belong together or its tree is found damaged. It looks at
        /// the shapes' boxes and at heightfields only: damage that only marking meets, such as a
        /// triangle that names a corner that is not there, is found by <see cref="Build"/>.
        /// </remarks>
        public static bool Measure(in GridShape shape, in ShapeSet shapes, float unit, byte cap, out int nodes, out int bricks) {
            nodes = 0; bricks = 0;
            Span<int> starts = stackalloc int[MostLevels], used = stackalloc int[MostLevels];
            used.Clear();
            if (!_walk(shape, shapes, unit, cap, false, default, default, starts, used, out var brickCount)) { return false; }
            long total = 0;
            for (var level = 0; level < MostLevels; level++) { total += used[level]; }
            if (total > int.MaxValue || brickCount > int.MaxValue / BrickBytes) { return false; }
            nodes = (int)total; bricks = brickCount;
            return true;
        }

        /// <summary>
        /// Builds the tree of a grid from shapes: the counts that
        /// <see cref="WallDistance.MarkLayer"/> gives layer by layer, each put down to a fifteenth
        /// of the cap, worked out only near the shapes' surfaces. A <see cref="WallTree"/> of <paramref name="nodes"/> cut to
        /// <paramref name="nodeCount"/> and <paramref name="bricks"/> cut to
        /// <paramref name="brickCount"/> bricks of <see cref="BrickBytes"/> is then ready to use.
        /// </summary>
        /// <remarks>
        /// A cube of cells is passed over whole where no shape is near enough to bring any of its
        /// cells below the cap, or where it lies wholly inside one box, sphere, capsule or
        /// heightfield. So the cost follows the shapes' surface and not the grid's volume.
        /// Returns false if the shape cannot be used or is too long for a tree, the unit is not
        /// above zero, the cap is zero,
        /// a buffer is shorter than <see cref="Measure"/> says, or the set's spans do not belong
        /// together or are found damaged. The counts are then 0: a build that was refused gives
        /// no tree.
        /// </remarks>
        public static bool Build(in GridShape shape, in ShapeSet shapes, float unit, byte cap, Span<WallNode> nodes, Span<byte> bricks,
            out int nodeCount, out int brickCount) {
            nodeCount = 0; brickCount = 0;
            var levels = Levels(shape);
            Span<int> starts = stackalloc int[MostLevels], most = stackalloc int[MostLevels], used = stackalloc int[MostLevels];
            most.Clear();
            if (!_walk(shape, shapes, unit, cap, false, default, default, starts, most, out var mostBricks)) { return false; }
            // Each level gets room for as many nodes as it can come to, the root's level first.
            long room = 0;
            for (var level = levels - 1; level >= 0; level--) { starts[level] = (int)room; room += most[level]; }
            if (room > nodes.Length || (long)mostBricks * BrickBytes > bricks.Length) { return false; }
            used.Clear();
            if (!_walk(shape, shapes, unit, cap, true, nodes, bricks, starts, used, out var kept)) { return false; }

            // Packed down: the levels one after another, the root first. A node's mixed children
            // were added to the level below one after another, so each node only has to learn
            // where that level now begins, and which number its first open child has.
            Span<int> begins = stackalloc int[MostLevels];
            var at = 0;
            for (var level = levels - 1; level >= 0; level--) { begins[level] = at; at += used[level]; }
            var leaves = 0;
            for (var level = levels - 1; level >= 0; level--) {
                for (var i = 0; i < used[level]; i++) {
                    var node = nodes[starts[level] + i];
                    nodes[begins[level] + i] = new WallNode(node.Mixed, node.Open, level > 0 ? begins[level - 1] + node.First : node.First, leaves);
                    leaves += _count(node.Open);
                }
            }
            nodeCount = at; brickCount = kept;
            return true;
        }

        /// <summary>The bytes a block of this many nodes and bricks takes, or -1 if that is more than an int holds or a count is negative.</summary>
        public static int BlockLength(int nodes, int bricks) {
            if (nodes < 0 || bricks < 0) { return -1; }
            var length = Header + (long)nodes * NodeBytes + (long)bricks * BrickBytes;
            return length <= int.MaxValue ? (int)length : -1;
        }

        /// <summary>
        /// Writes a tree as one block: the bytes to store. <paramref name="length"/> is how many
        /// bytes were written, which is <see cref="BlockLength"/> of its nodes and bricks.
        /// </summary>
        /// <remarks>
        /// <paramref name="shapes"/> are the shapes the tree was built from. The block keeps a
        /// fingerprint of them, eight bytes, so that <see cref="TryRead"/> can refuse it beside
        /// any others: a tree of other shapes would prove room that is not there.
        /// Returns false if the tree cannot be used, the shapes' spans do not belong together, the
        /// block is too short, or on a machine that stores numbers highest byte first.
        /// </remarks>
        public static bool Write(in WallTree tree, in ShapeSet shapes, Span<byte> block, out int length) {
            length = 0;
            if (!BitConverter.IsLittleEndian || !tree.IsValid || shapes.Count < 0) { return false; }
            int nodes = tree.Nodes.Length, bricks = tree.Bricks.Length / BrickBytes;
            var needed = BlockLength(nodes, bricks);
            if (needed < 0 || block.Length < needed) { return false; }
            var header = block.Slice(0, Header);
            header.Clear();
            var shape = tree.Shape;
            BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), Version);
            var numbers = MemoryMarshal.Cast<byte, float>(header.Slice(8, 16));
            numbers[0] = shape.OriginX; numbers[1] = shape.OriginY; numbers[2] = shape.OriginZ; numbers[3] = shape.CellSize;
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), shape.CountX);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28), shape.CountY);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32), shape.CountZ);
            MemoryMarshal.Cast<byte, float>(header.Slice(36, 4))[0] = tree.Unit;
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40), tree.Cap);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(44), nodes);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(48), bricks);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(52), Shapes.Fingerprint(shapes));
            tree.Nodes.CopyTo(MemoryMarshal.Cast<byte, WallNode>(block.Slice(Header, nodes * NodeBytes)));
            tree.Bricks.CopyTo(block.Slice(Header + nodes * NodeBytes));
            length = needed;
            return true;
        }

        /// <summary>
        /// Views a block that <see cref="Write"/> made as a <see cref="WallTree"/>, without
        /// copying. <paramref name="block"/> must be cut to the length that Write gave, and
        /// <paramref name="shapes"/> must be the shapes it was written with.
        /// </summary>
        /// <remarks>
        /// A block written with other shapes is refused: its fingerprint is not theirs. Shapes
        /// that are the same number for number are the same, wherever they are kept. Working the
        /// fingerprint out reads every shape once: about a millisecond for two megabytes of them.
        /// Every node is looked at once: each must say that its children lie where the nodes
        /// before it leave off, that no child is both mixed and open, and that no open child is
        /// larger than an open cube may be. A block that passes can send no query outside its
        /// nodes or its bricks. Returns false for a block that does not pass, one of another
        /// version, one written highest byte first, or on such a machine; and if the shapes'
        /// spans do not belong together.
        /// The tree views the block's bytes, so it is good for as long as they are.
        /// </remarks>
        public static bool TryRead(ReadOnlySpan<byte> block, in ShapeSet shapes, out WallTree tree) {
            tree = default;
            if (!BitConverter.IsLittleEndian || block.Length < Header || shapes.Count < 0) { return false; }
            if (BinaryPrimitives.ReadInt32LittleEndian(block) != Magic || BinaryPrimitives.ReadInt32LittleEndian(block.Slice(4)) != Version) { return false; }
            var shape = new GridShape(MemoryMarshal.Read<float>(block.Slice(8)), MemoryMarshal.Read<float>(block.Slice(12)), MemoryMarshal.Read<float>(block.Slice(16)),
                MemoryMarshal.Read<float>(block.Slice(20)), BinaryPrimitives.ReadInt32LittleEndian(block.Slice(24)), BinaryPrimitives.ReadInt32LittleEndian(block.Slice(28)),
                BinaryPrimitives.ReadInt32LittleEndian(block.Slice(32)));
            var unit = MemoryMarshal.Read<float>(block.Slice(36));
            int cap = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(40));
            int nodeCount = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(44)), brickCount = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(48));
            var levels = Levels(shape);
            if (levels == 0 || cap < 1 || cap > 255 || !WallDistance.Usable(unit, (byte)cap) || nodeCount < 1 || brickCount < 0) { return false; }
            if (BlockLength(nodeCount, brickCount) != block.Length) { return false; }
            var nodes = MemoryMarshal.Cast<byte, WallNode>(block.Slice(Header, nodeCount * NodeBytes));

            // Level by level from the root: a level's nodes follow the level above, and its mixed
            // children, counted in order, are the level below.
            int at = 0, inLevel = 1, leaves = 0;
            for (var level = levels - 1; level >= 0; level--) {
                // Where the level below begins: for the lowest nodes, the first brick.
                var below = level > 0 ? at + inLevel : 0;
                long children = 0;
                if (inLevel > nodeCount - at) { return false; }
                for (var i = 0; i < inLevel; i++) {
                    var node = nodes[at + i];
                    if ((node.Mixed & node.Open) != 0UL || node.First != below + children || node.FirstLeaf != leaves) { return false; }
                    if (node.Open != 0UL && (4L << (2 * level)) > MostOpen) { return false; }
                    children += _count(node.Mixed);
                    leaves += _count(node.Open);
                }
                at += inLevel;
                if (level > 0) {
                    if (children > nodeCount - at) { return false; }
                    inLevel = (int)children;
                } else if (children != brickCount) {
                    return false;
                }
            }
            if (at != nodeCount) { return false; }
            if (BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(52)) != Shapes.Fingerprint(shapes)) { return false; }
            tree = new WallTree(shape, unit, (byte)cap, nodes, block.Slice(Header + nodeCount * NodeBytes));
            return true;
        }

        /// <summary>
        /// A distance that the nearest shape is proven to be at least, at any point of the grid,
        /// from the tree alone: what <see cref="WallDistance.RoomAt"/> gives for the dense map with
        /// its counts put down as the tree keeps them.
        /// </summary>
        /// <remarks>Returns false if the tree cannot be used or the point is outside the grid or not a number.</remarks>
        public static bool RoomAt(in WallTree tree, float x, float y, float z, out float room) => _room(tree, x, y, z, false, 0f, out room, out _);

        /// <summary>
        /// Whether the nearest shape is proven to be at least <paramref name="radius"/> from a
        /// point: what <see cref="RoomAt"/> gives, compared, and what
        /// <see cref="WallDistance.HasRoom"/> gives for the dense map. False where RoomAt would be
        /// refused.
        /// </summary>
        /// <remarks>
        /// Where the point lies among cells of one open cube, and the radius is no more than such
        /// cells are sure to prove, the answer is yes as soon as the cube is found: no count is
        /// read and nothing is worked out.
        /// </remarks>
        public static bool HasRoom(in WallTree tree, float x, float y, float z, float radius) {
            if (!_room(tree, x, y, z, true, radius, out var room, out var known)) { return false; }
            return known != 0 ? known > 0 : room >= radius;
        }

        // The count a cell holds: 0 for a cell that is not in the grid, or where the tree cannot be used.
        internal static byte CellOf(in WallTree tree, int x, int y, int z) {
            var shape = tree.Shape;
            if (!tree.IsValid || (uint)x >= (uint)shape.CountX || (uint)y >= (uint)shape.CountY || (uint)z >= (uint)shape.CountZ) { return 0; }
            return _from(tree, 0, tree.Levels - 1, x, y, z);
        }

        // How many open cubes a tree has: the last node's are the last of them.
        internal static int OpenCubes(in WallTree tree) {
            if (tree.Nodes.Length == 0) { return 0; }
            var last = tree.Nodes[tree.Nodes.Length - 1];
            var cubes = (long)last.FirstLeaf + _count(last.Open);
            return last.FirstLeaf >= 0 && cubes <= int.MaxValue ? (int)cubes : 0;
        }

        // The leaf that holds a cell of the grid, and how many cells it has along a side: a
        // brick's number, or the number of bricks plus an open cube's number. -1 for a cell in a
        // cube of zeros, a cell that is not in the grid, and where the tree is not whole. The
        // tree must be one that can be used.
        internal static int LeafOf(in WallTree tree, int x, int y, int z, out int side) {
            side = 0;
            var shape = tree.Shape;
            if ((uint)x >= (uint)shape.CountX || (uint)y >= (uint)shape.CountY || (uint)z >= (uint)shape.CountZ) { return -1; }
            ReadOnlySpan<WallNode> nodes = tree.Nodes;
            var node = 0;
            for (var level = tree.Levels - 1; ; level--) {
                var shift = 2 * (level + 1);
                var child = ((x >> shift) & 3) | (((y >> shift) & 3) << 2) | (((z >> shift) & 3) << 4);
                var bit = 1UL << child;
                var here = nodes[node];
                if ((here.Mixed & bit) == 0UL) {
                    if ((here.Open & bit) == 0UL) { return -1; }
                    side = 1 << shift;
                    var cube = (long)tree.Bricks.Length / BrickBytes + here.FirstLeaf + _count(here.Open & (bit - 1UL));
                    return here.FirstLeaf >= 0 && cube <= int.MaxValue ? (int)cube : -1;
                }
                var at = here.First + _count(here.Mixed & (bit - 1UL));
                if (level == 0) {
                    side = 4;
                    return at >= 0 && ((long)at + 1) * BrickBytes <= tree.Bricks.Length ? at : -1;
                }
                if ((uint)at >= (uint)nodes.Length || at <= node) { return -1; }
                node = at;
            }
        }

        // RoomAt's arithmetic over the tree. When asking, known says where the answer to "at least
        // the radius?" is sure without the room being worked out: 1 for yes, -1 for no.
        private static bool _room(in WallTree tree, float x, float y, float z, bool asking, float radius, out float room, out int known) {
            room = 0f; known = 0;
            var shape = tree.Shape;
            if (!tree.IsValid || !shape.Holds(x, y, z)) { return false; }
            double size = shape.CellSize;
            WallDistance.Between(x, shape.OriginX, size, shape.CountX, out var lowX, out var highX, out var fx, out var offX);
            WallDistance.Between(y, shape.OriginY, size, shape.CountY, out var lowY, out var highY, out var fy, out var offY);
            WallDistance.Between(z, shape.OriginZ, size, shape.CountZ, out var lowZ, out var highZ, out var fz, out var offZ);

            double unit = tree.Unit;
            double d000, d100, d010, d110, d001, d101, d011, d111;
            // Down from the root for as long as one child holds all eight cells: the low and the
            // high cell are in the same child on every axis.
            ReadOnlySpan<WallNode> nodes = tree.Nodes;
            int node = 0, level = tree.Levels - 1;
            while (true) {
                var shift = 2 * (level + 1);
                var child = ((lowX >> shift) & 3) | (((lowY >> shift) & 3) << 2) | (((lowZ >> shift) & 3) << 4);
                if (child != (((highX >> shift) & 3) | (((highY >> shift) & 3) << 2) | (((highZ >> shift) & 3) << 4))) {
                    // They part here: each is found from this node.
                    d000 = _from(tree, node, level, lowX, lowY, lowZ) * unit; d100 = _from(tree, node, level, highX, lowY, lowZ) * unit;
                    d010 = _from(tree, node, level, lowX, highY, lowZ) * unit; d110 = _from(tree, node, level, highX, highY, lowZ) * unit;
                    d001 = _from(tree, node, level, lowX, lowY, highZ) * unit; d101 = _from(tree, node, level, highX, lowY, highZ) * unit;
                    d011 = _from(tree, node, level, lowX, highY, highZ) * unit; d111 = _from(tree, node, level, highX, highY, highZ) * unit;
                    break;
                }
                var bit = 1UL << child;
                var here = nodes[node];
                if ((here.Mixed & bit) == 0UL) {
                    // One cube holds all eight, and one count: the answer may need no arithmetic.
                    var open = (here.Open & bit) != 0UL;
                    if (asking && !open && radius > 0f) { known = -1; return true; }
                    if (asking && open && radius <= tree.OpenRoom && offX == 0d && offY == 0d && offZ == 0d) { known = 1; return true; }
                    d000 = d100 = d010 = d110 = d001 = d101 = d011 = d111 = open ? tree.Cap * unit : 0d;
                    break;
                }
                var at = here.First + _count(here.Mixed & (bit - 1UL));
                if (level == 0) {
                    // One brick holds all eight.
                    ReadOnlySpan<byte> bricks = tree.Bricks;
                    var first = (long)at * BrickBytes;
                    if (at < 0 || first + BrickBytes > bricks.Length) {
                        d000 = d100 = d010 = d110 = d001 = d101 = d011 = d111 = 0d;
                        break;
                    }
                    var brick = bricks.Slice((int)first, BrickBytes);
                    var cap = tree.Cap;
                    int x0 = lowX & 3, x1 = highX & 3, y0 = (lowY & 3) << 2, y1 = (highY & 3) << 2, z0 = (lowZ & 3) << 4, z1 = (highZ & 3) << 4;
                    d000 = _kept(brick, x0 | y0 | z0, cap) * unit; d100 = _kept(brick, x1 | y0 | z0, cap) * unit;
                    d010 = _kept(brick, x0 | y1 | z0, cap) * unit; d110 = _kept(brick, x1 | y1 | z0, cap) * unit;
                    d001 = _kept(brick, x0 | y0 | z1, cap) * unit; d101 = _kept(brick, x1 | y0 | z1, cap) * unit;
                    d011 = _kept(brick, x0 | y1 | z1, cap) * unit; d111 = _kept(brick, x1 | y1 | z1, cap) * unit;
                    break;
                }
                if ((uint)at >= (uint)nodes.Length || at <= node) {
                    d000 = d100 = d010 = d110 = d001 = d101 = d011 = d111 = 0d;
                    break;
                }
                node = at;
                level--;
            }
            // Eight cells at the cap, in more than one open cube, are as sure as eight in one.
            if (asking && radius <= tree.OpenRoom && offX == 0d && offY == 0d && offZ == 0d) {
                var most = tree.Cap * unit;
                if (d000 == most && d100 == most && d010 == most && d110 == most && d001 == most && d101 == most && d011 == most && d111 == most) { known = 1; return true; }
            }
            room = WallDistance.Proven(d000, d100, d010, d110, d001, d101, d011, d111, fx, fy, fz, lowX != highX && lowY != highY && lowZ != highZ, size,
                Math.Sqrt(offX * offX + offY * offY + offZ * offZ), tree.Cap * unit);
            return true;
        }

        // The count of a cell of the grid, found from a node that holds it, at that node's level.
        // A step that would leave the nodes or the bricks gives 0: a tree that is not whole
        // claims no room.
        private static byte _from(in WallTree tree, int node, int level, int x, int y, int z) {
            ReadOnlySpan<WallNode> nodes = tree.Nodes;
            for (; ; level--) {
                var shift = 2 * (level + 1);
                var child = ((x >> shift) & 3) | (((y >> shift) & 3) << 2) | (((z >> shift) & 3) << 4);
                var bit = 1UL << child;
                var here = nodes[node];
                if ((here.Mixed & bit) == 0UL) { return (here.Open & bit) != 0UL ? tree.Cap : (byte)0; }
                var at = here.First + _count(here.Mixed & (bit - 1UL));
                if (level == 0) {
                    var first = (long)at * BrickBytes;
                    if (at < 0 || first + BrickBytes > tree.Bricks.Length) { return 0; }
                    return _kept(tree.Bricks.Slice((int)first, BrickBytes), (x & 3) | ((y & 3) << 2) | ((z & 3) << 4), tree.Cap);
                }
                if ((uint)at >= (uint)nodes.Length || at <= node) { return 0; }
                node = at;
            }
        }

        // The count a brick of a tree gives back for one of its cells.
        internal static byte CountIn(in WallTree tree, int brick, int cell) => _kept(tree.Bricks.Slice(brick * BrickBytes, BrickBytes), cell, tree.Cap);

        // The count a brick gives back for one of its cells: the least count that is kept as the
        // cell's four bits. The cap is kept as itself, and so is 0.
        private static byte _kept(ReadOnlySpan<byte> brick, int cell, byte cap) {
            var level = (cell & 1) == 0 ? brick[cell >> 1] & 15 : brick[cell >> 1] >> 4;
            return (byte)((level * cap + Steps - 1) / Steps);
        }

        // How many bits are set. Written out: the older of the two targets has no call for it.
        private static int _count(ulong bits) {
            bits -= (bits >> 1) & 0x5555555555555555UL;
            bits = (bits & 0x3333333333333333UL) + ((bits >> 2) & 0x3333333333333333UL);
            bits = (bits + (bits >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((bits * 0x0101010101010101UL) >> 56);
        }

        // One node as it is being built: its cube's first cell, the child that is looked at next,
        // and what its children so far have come to.
        private struct Frame {
            public ulong Mixed, Open;
            public int X, Y, Z, Child, First, AllOpen;
        }

        // Walks the grid's cubes from the root down. Counting, it only says how many nodes each
        // level and how many bricks the tree can come to. Filling, it marks the bricks and writes
        // each level's nodes from where starts says, with First counted within the level below.
        private static bool _walk(in GridShape shape, in ShapeSet shapes, float unit, byte cap, bool fill, Span<WallNode> nodes, Span<byte> bricks,
            Span<int> starts, Span<int> used, out int brickCount) {
            brickCount = 0;
            var levels = Levels(shape);
            if (levels == 0 || levels > MostLevels || !WallDistance.Usable(unit, cap) || shapes.Count < 0) { return false; }
            // As MarkLayer has them: how near a shape must be to bring a cell below the cap, and
            // how near its box must be to a cell's centre for the shape to be looked at.
            var reach = (double)cap * unit + Shapes.Slack;
            var margin = reach + unit;

            Span<Frame> stack = stackalloc Frame[MostLevels];
            var depth = 0;
            stack[0] = default;
            while (true) {
                ref var frame = ref stack[depth];
                var level = levels - 1 - depth;
                if (frame.Child == 64) {
                    // The node is whole. What it is to the node above: a cube of zeros, an open
                    // cube if it is small enough to be one, or a node of its own.
                    var kind = Mixed;
                    if (depth > 0 && frame.AllOpen == 0 && frame.Mixed == 0UL) {
                        if (frame.Open == 0UL) { kind = Solid; } else if (frame.Open == ulong.MaxValue && (4 << (2 * (level + 1))) <= MostOpen) { kind = Open; }
                    }
                    if (kind == Mixed) {
                        if (fill) {
                            var at = (long)starts[level] + used[level];
                            if (at >= nodes.Length) { return false; }
                            nodes[(int)at] = new WallNode(frame.Mixed, frame.Open, frame.First, 0);
                        }
                        used[level]++;
                    }
                    if (depth == 0) { return true; }
                    depth--;
                    ref var above = ref stack[depth];
                    var which = 1UL << (above.Child - 1);
                    if (kind == Mixed) { above.Mixed |= which; } else if (kind == Open) { above.Open |= which; }
                    continue;
                }

                var child = frame.Child++;
                var side = 4 << (2 * level);
                int x = frame.X + (child & 3) * side, y = frame.Y + ((child >> 2) & 3) * side, z = frame.Z + ((child >> 4) & 3) * side;
                var bit = 1UL << child;
                var allOpen = frame.AllOpen;
                if (allOpen == 0) {
                    // Cells of the root's cube that are not in the grid hold 0.
                    if (x >= shape.CountX || y >= shape.CountY || z >= shape.CountZ) { continue; }
                    var near = _near(shape, shapes, x, y, z, side, reach, margin);
                    if (near == Damaged) { return false; }
                    if (near == Solid) { continue; }
                    if (near == Open && x + side <= shape.CountX && y + side <= shape.CountY && z + side <= shape.CountZ) { allOpen = 1; }
                }
                if (allOpen != 0 && side <= MostOpen) {
                    frame.Open |= bit;
                    continue;
                }
                if (level == 0) {
                    if (!fill) {
                        brickCount++;
                        frame.Mixed |= bit;
                        continue;
                    }
                    var kind = _brick(shape, shapes, unit, cap, x, y, z, reach * reach, bricks, brickCount);
                    if (kind == Damaged) { return false; }
                    if (kind == Mixed) { brickCount++; frame.Mixed |= bit; } else if (kind == Open) { frame.Open |= bit; }
                    continue;
                }
                // A node of the level below. Its mixed children will follow those already there.
                depth++;
                stack[depth] = new Frame { X = x, Y = y, Z = z, AllOpen = allOpen, First = level - 1 == 0 ? brickCount : used[level - 2] };
            }
        }

        // What the shapes say of a cube of cells without marking it: Solid where every centre is
        // inside one solid, Open where no shape is near enough to bring any cell below the cap,
        // and Mixed where the cells have to be looked at.
        private static int _near(in GridShape shape, in ShapeSet shapes, int x, int y, int z, int side, double reach, double margin) {
            int lastX = x + side <= shape.CountX ? x + side - 1 : shape.CountX - 1;
            int lastY = y + side <= shape.CountY ? y + side - 1 : shape.CountY - 1;
            int lastZ = z + side <= shape.CountZ ? z + side - 1 : shape.CountZ - 1;
            // The box of the cells' centres.
            double minX = shape.Centre(shape.OriginX, x), maxX = shape.Centre(shape.OriginX, lastX);
            double minY = shape.Centre(shape.OriginY, y), maxY = shape.Centre(shape.OriginY, lastY);
            double minZ = shape.Centre(shape.OriginZ, z), maxZ = shape.Centre(shape.OriginZ, lastZ);

            ReadOnlySpan<BoxNode> nodes = shapes.Nodes;
            ReadOnlySpan<int> order = shapes.Order;
            ReadOnlySpan<Box> boxes = shapes.Boxes;
            ReadOnlySpan<Solid> solids = shapes.Solids;
            var count = shapes.Count;
            var firstField = count - shapes.HeightFields.Length;
            var result = Open;
            if (count == 0) { return result; }
            Span<int> stack = stackalloc int[QueryStack];
            int top = 0, visits = 0;
            stack[top++] = 0;
            while (top > 0) {
                if (++visits > nodes.Length) { return Damaged; }
                var index = stack[--top];
                var node = nodes[index];
                if (!_reaches(node.MinX, node.MinY, node.MinZ, node.MaxX, node.MaxY, node.MaxZ, minX, minY, minZ, maxX, maxY, maxZ, margin)) { continue; }
                if (node.Count <= 0) {
                    if (!(node.First > index + 1 && node.First < nodes.Length && top + 2 <= QueryStack)) { return Damaged; }
                    stack[top++] = node.First;
                    stack[top++] = index + 1;
                    continue;
                }
                if (!(node.First >= 0 && node.Count <= order.Length && node.First <= order.Length - node.Count)) { return Damaged; }
                for (var j = 0; j < node.Count; j++) {
                    var item = order[node.First + j];
                    if ((uint)item >= (uint)count) { return Damaged; }
                    var box = boxes[item];
                    // What MarkLayer asks of each cell: a shape whose box is not within the margin of a centre is not looked at.
                    if (!_reaches(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ, minX, minY, minZ, maxX, maxY, maxZ, margin)) { continue; }
                    if (item >= firstField) {
                        var said = _ground(shapes.HeightFields[item - firstField], shapes.HeightBlocks, shapes.Heights, minX, minY, minZ, maxX, maxY, maxZ, reach, margin);
                        if (said == Damaged || said == Solid) { return said; }
                        if (said == Mixed) { result = Mixed; }
                        continue;
                    }
                    result = Mixed;
                    if (item < solids.Length) {
                        // A box, a sphere and a capsule have no hollows: with the eight corners inside, every centre is.
                        var solid = solids[item];
                        if (ShapeDistance.SolidSquared(solid, minX, minY, minZ) == 0d && ShapeDistance.SolidSquared(solid, maxX, minY, minZ) == 0d &&
                            ShapeDistance.SolidSquared(solid, minX, maxY, minZ) == 0d && ShapeDistance.SolidSquared(solid, maxX, maxY, minZ) == 0d &&
                            ShapeDistance.SolidSquared(solid, minX, minY, maxZ) == 0d && ShapeDistance.SolidSquared(solid, maxX, minY, maxZ) == 0d &&
                            ShapeDistance.SolidSquared(solid, minX, maxY, maxZ) == 0d && ShapeDistance.SolidSquared(solid, maxX, maxY, maxZ) == 0d && _known(solid)) { return Solid; }
                    }
                }
            }
            return result;
        }

        // A solid of a kind that is not known, or with a number that is not finite, touches
        // everything, and is no reason to pass a cube over: its cells are marked one by one.
        private static bool _known(in Solid solid) {
            var sum = (double)solid.X + solid.Y + solid.Z + solid.UX + solid.UY + solid.UZ + solid.VX + solid.VY + solid.VZ + solid.WX + solid.WY + solid.WZ + solid.HalfU + solid.HalfV + solid.HalfW;
            return sum - sum == 0d && (solid.Kind == SolidKind.Box || solid.Kind == SolidKind.Sphere || solid.Kind == SolidKind.Capsule);
        }

        private static bool _reaches(float boxMinX, float boxMinY, float boxMinZ, float boxMaxX, float boxMaxY, float boxMaxZ,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ, double margin) =>
            !(minX > boxMaxX + margin || maxX < boxMinX - margin || minY > boxMaxY + margin || maxY < boxMinY - margin || minZ > boxMaxZ + margin || maxZ < boxMinZ - margin);

        // The same for a heightfield, from the lowest and highest heights its blocks keep. The box
        // of centres is taken into the heightfield's own axes, and the box round it there is used:
        // wider than the cube where the heightfield is turned, which only means looking more closely.
        private static int _ground(in HeightField field, ReadOnlySpan<uint> blocks, ReadOnlySpan<ushort> heights,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ, double reach, double margin) {
            double minU = double.PositiveInfinity, minV = double.PositiveInfinity, minW = double.PositiveInfinity;
            double maxU = double.NegativeInfinity, maxV = double.NegativeInfinity, maxW = double.NegativeInfinity;
            double unitU = 1.5d - 0.5d * ((double)field.UX * field.UX + (double)field.UY * field.UY + (double)field.UZ * field.UZ);
            double unitV = 1.5d - 0.5d * ((double)field.VX * field.VX + (double)field.VY * field.VY + (double)field.VZ * field.VZ);
            double unitW = 1.5d - 0.5d * ((double)field.WX * field.WX + (double)field.WY * field.WY + (double)field.WZ * field.WZ);
            for (var corner = 0; corner < 8; corner++) {
                double px = ((corner & 1) == 0 ? minX : maxX) - field.X, py = ((corner & 2) == 0 ? minY : maxY) - field.Y, pz = ((corner & 4) == 0 ? minZ : maxZ) - field.Z;
                double u = (px * field.UX + py * field.UY + pz * field.UZ) * unitU, v = (px * field.VX + py * field.VY + pz * field.VZ) * unitV, w = (px * field.WX + py * field.WY + pz * field.WZ) * unitW;
                minU = u < minU ? u : minU; maxU = u > maxU ? u : maxU;
                minV = v < minV ? v : minV; maxV = v > maxV ? v : maxV;
                minW = w < minW ? w : minW; maxW = w > maxW ? w : maxW;
            }
            double lengthU = (double)field.SizeU * field.CountU, lengthW = (double)field.SizeW * field.CountW;
            double lowest = field.Lowest, step = field.Step, floor = field.Floor;
            // A number that is not finite, or sizes that are not above zero: its cells are marked one by one.
            var sum = minU + maxU + minV + maxV + minW + maxW + lengthU + lengthW + lowest + step + floor;
            if (!(sum - sum == 0d) || !(lengthU > 0d) || !(lengthW > 0d) || !(step >= 0d)) { return Mixed; }
            // Nothing of it within the margin of any centre, seen from above.
            if (maxU + margin < 0d || minU - margin > lengthU || maxW + margin < 0d || minW - margin > lengthW) { return Open; }
            // A hair, so that a cube passed over is one whose cells MarkLayer leaves alone whatever the rounding.
            const double Hair = 1e-6;
            if (!HeightDistance.Span(field, blocks, heights, minU - margin, maxU + margin, minW - margin, maxW + margin, out _, out var high)) { return Damaged; }
            // Every centre is the reach or more above all the ground that is within the margin of it.
            if (minV - (lowest + step * high) >= reach + Hair) { return Open; }
            if (minU >= 0d && maxU <= lengthU && minW >= 0d && maxW <= lengthW && minV >= floor + Hair) {
                if (!HeightDistance.Span(field, blocks, heights, minU, maxU, minW, maxW, out var low, out _)) { return Damaged; }
                // Every centre is under the lowest height of every block it could be over.
                if (maxV <= lowest + step * low - Hair) { return Solid; }
            }
            return Mixed;
        }

        // Marks one brick with MarkLayer's own arithmetic. It is kept unless all its cells hold 0
        // or all hold the cap; cells that are not in the grid hold 0.
        private static int _brick(in GridShape shape, in ShapeSet shapes, float unit, byte cap, int x0, int y0, int z0, double reachSquared, Span<byte> bricks, int brickCount) {
            Span<byte> cells = stackalloc byte[BrickBytes];
            bool allZero = true, allCap = true;
            for (var i = 0; i < BrickCells; i++) {
                int x = x0 + (i & 3), y = y0 + ((i >> 2) & 3), z = z0 + ((i >> 4) & 3);
                byte value = 0;
                if (x < shape.CountX && y < shape.CountY && z < shape.CountZ) {
                    if (!Shapes.NearestSquared(shapes, Shapes.Every, shape.Centre(shape.OriginX, x), shape.Centre(shape.OriginY, y), shape.Centre(shape.OriginZ, z), reachSquared, out var squared)) { return Damaged; }
                    if (!(squared < reachSquared)) {
                        value = cap;
                    } else {
                        var counts = Math.Floor((Math.Sqrt(squared) - Shapes.Slack) / unit);
                        value = counts >= cap ? cap : counts > 0d ? (byte)counts : (byte)0;
                    }
                }
                // Four bits: which fifteenth of the cap the count is in. Only the cap itself is the fifteenth.
                var kept = value * Steps / cap;
                cells[i >> 1] |= (byte)(kept << ((i & 1) << 2));
                allZero &= kept == 0;
                allCap &= value == cap;
            }
            if (allZero) { return Solid; }
            if (allCap) { return Open; }
            var at = (long)brickCount * BrickBytes;
            if (at + BrickBytes > bricks.Length) { return Damaged; }
            cells.CopyTo(bricks.Slice((int)at, BrickBytes));
            return Mixed;
        }
    }
}
