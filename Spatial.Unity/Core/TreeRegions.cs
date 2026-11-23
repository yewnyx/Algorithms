using System;
using System.Buffers.Binary;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Which cells of a <see cref="WallTree"/>'s grid a body of one size can be in, and which of
    /// them can be reached from which: what <see cref="Regions"/> keeps as a byte a cell, kept as
    /// a byte a brick and a byte an open cube.
    /// </summary>
    /// <remarks>
    /// The cells that are open, the regions and their numbers are those that
    /// <see cref="Regions.FromSeeds"/> gives for the dense map with its counts put down as the tree
    /// keeps them, with no costs.
    /// A brick's byte says which region its open cells are in. A brick whose open cells are not
    /// all in one region, because a seed reached only some of them or because two regions meet in
    /// it, is kept in full after the bytes: its number and a byte a cell, 68 bytes. So the bytes
    /// follow the shapes' surface, as the tree does, and not the grid's volume.
    /// The bytes are good with the tree they were made for and no other. Nothing is allocated and
    /// nothing throws. After a false return the bytes say that no cell is open.
    /// </remarks>
    public static class TreeRegions {
        // The count a cell must hold to be open, three bytes of nothing, and how many bricks are kept in full.
        private const int Header = 8;
        // A brick kept in full: its number among the bricks, and a region for each cell.
        private const int Record = 4 + WallTrees.BrickCells;
        // What a brick's byte holds when its cells are kept in full. No region has this number.
        private const byte Kept = 255;

        // The cells of a brick that lie in its first and last layers along x and along y.
        private const ulong X0 = 0x1111111111111111UL, X3 = 0x8888888888888888UL, Y0 = 0x000F000F000F000FUL, Y3 = 0xF000F000F000F000UL;

        /// <summary>
        /// The most bytes <see cref="FromSeeds"/> can need for a tree: every brick kept in full.
        /// What it used is far less as a rule, and it says how much. 0 if the tree cannot be used
        /// or that is more than an int holds.
        /// </summary>
        public static int MostLength(in WallTree tree) {
            if (!tree.IsValid) { return 0; }
            long bricks = tree.Bricks.Length / WallTrees.BrickBytes;
            var most = Header + bricks + WallTrees.OpenCubes(tree) + bricks * Record;
            return most <= int.MaxValue ? (int)most : 0;
        }

        /// <summary>The least bytes <see cref="FromSeeds"/> can do with: no brick kept in full. 0 if the tree cannot be used.</summary>
        public static int LeastLength(in WallTree tree) {
            if (!tree.IsValid) { return 0; }
            var least = Header + (long)(tree.Bricks.Length / WallTrees.BrickBytes) + WallTrees.OpenCubes(tree);
            return least <= int.MaxValue ? (int)least : 0;
        }

        /// <summary>The length of the marks <see cref="FromSeeds"/> needs: a little over one ulong per brick. 0 if the tree cannot be used.</summary>
        public static int MarksLength(in WallTree tree) {
            if (!tree.IsValid) { return 0; }
            var bricks = tree.Bricks.Length / WallTrees.BrickBytes;
            return bricks + (bricks + 63) / 64;
        }

        /// <summary>The length of the queue <see cref="FromSeeds"/> needs: one int per brick and per open cube. 0 if the tree cannot be used.</summary>
        public static int QueueLength(in WallTree tree) {
            var least = LeastLength(tree);
            return least > 0 ? least - Header : 0;
        }

        /// <summary>
        /// Floods from the open cells whose centres are inside each seed, through open cells that
        /// share a face, as <see cref="Regions.FromSeeds"/> does over a dense map: every cell
        /// reached gets its region's number, from 1, and seeds that turn out to be joined share
        /// one. <paramref name="length"/> is how many bytes of <paramref name="region"/> were
        /// used: keep that many.
        /// </summary>
        /// <remarks>
        /// <paramref name="reached"/> is how many cells got a number, and <paramref name="empty"/>
        /// how many seeds held no open cell. The flood goes brick by brick and cube by cube, so
        /// its work follows the shapes' surface and its queue is short.
        /// A seed is a solid, and like any solid may reach no farther than 1,024 m from the
        /// frame's origin: one seed cannot hold the whole of a grid longer than that, which is
        /// the kind of grid a tree is for. Seed such a grid where bodies come into it.
        /// <paramref name="region"/> needs <see cref="LeastLength"/> bytes and 68 more for every
        /// brick it turns out to keep in full; <see cref="MostLength"/> is always enough.
        /// <paramref name="marks"/> and <paramref name="queue"/> are the flood's own, of
        /// <see cref="MarksLength"/> and <see cref="QueueLength"/>, and can be let go after.
        /// Returns false if the tree cannot be used, the class does not fit it (see
        /// <see cref="SizeClass.Fits(in WallTree)"/>), a buffer is too short, a seed may not be
        /// stored, or the seeds reach more than <see cref="Regions.Most"/> regions.
        /// </remarks>
        public static bool FromSeeds(in WallTree tree, in SizeClass size, ReadOnlySpan<Solid> seeds,
            Span<byte> region, Span<ulong> marks, Span<int> queue, out int length, out int reached, out int empty) {
            length = 0; reached = 0; empty = 0;
            (region.Length > Header ? region.Slice(0, Header) : region).Clear();
            int least = LeastLength(tree), marksLength = MarksLength(tree);
            if (least == 0 || !size.Fits(tree) || region.Length < least || marks.Length < marksLength || queue.Length < least - Header) { return false; }
            var shape = tree.Shape;
            var work = new Work {
                Tree = tree, Numbers = region.Slice(Header, least - Header), Records = region.Slice(least), Marks = marks.Slice(0, marksLength), Queue = queue,
                Bricks = tree.Bricks.Length / WallTrees.BrickBytes, Need = size.Cell,
            };
            work.Numbers.Clear();
            work.Marks.Clear();

            double cellSize = shape.CellSize;
            int countX = shape.CountX, countY = shape.CountY, countZ = shape.CountZ;
            int regions = 0, without = 0;
            for (var seed = 0; seed < seeds.Length; seed++) {
                var solid = seeds[seed];
                if (!ShapeDistance.TryBox(solid, out var box)) { return false; }
                // The cells whose centres can be inside the seed.
                int fromX = _first(box.MinX, shape.OriginX, cellSize, countX), toX = _last(box.MaxX, shape.OriginX, cellSize, countX);
                int fromY = _first(box.MinY, shape.OriginY, cellSize, countY), toY = _last(box.MaxY, shape.OriginY, cellSize, countY);
                int fromZ = _first(box.MinZ, shape.OriginZ, cellSize, countZ), toZ = _last(box.MaxZ, shape.OriginZ, cellSize, countZ);
                var found = false;
                for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                    double centreZ = shape.Centre(shape.OriginZ, cellZ);
                    for (var cellY = fromY; cellY <= toY; cellY++) {
                        double centreY = shape.Centre(shape.OriginY, cellY);
                        for (var cellX = fromX; cellX <= toX; cellX++) {
                            var leaf = WallTrees.LeafOf(tree, cellX, cellY, cellZ, out var side);
                            if ((uint)leaf >= (uint)work.Numbers.Length) { continue; }
                            var cell = (cellX & 3) | ((cellY & 3) << 2) | ((cellZ & 3) << 4);
                            if (leaf < work.Bricks && WallTrees.CountIn(tree, leaf, cell) < work.Need) { continue; }
                            if (ShapeDistance.SolidSquared(solid, shape.Centre(shape.OriginX, cellX), centreY, centreZ) != 0d) { continue; }
                            found = true;
                            // Already reached, from this seed or an earlier one: the same region.
                            if (work.NumberOf(leaf, cell) != 0) { continue; }
                            if (regions == Regions.Most || !work.Flood(cellX, cellY, cellZ, leaf, side, (byte)++regions)) { return false; }
                        }
                    }
                }
                if (!found) { without++; }
            }
            if (!work.Finish()) { return false; }
            region[0] = work.Need;
            BinaryPrimitives.WriteInt32LittleEndian(region.Slice(4), work.KeptCount);
            length = least + work.KeptCount * Record;
            reached = work.Reached;
            empty = without;
            return true;
        }

        /// <summary>
        /// Whether the cell that holds a point is open and was reached by a seed: what
        /// <see cref="Regions.IsOpen"/> says over a dense map. False if the point is outside the
        /// grid or not a number, the tree cannot be used, or the bytes are not a flood's of this tree.
        /// </summary>
        /// <remarks>It is the cell that is open, not the point: a body at the cell's centre fits, and the point can be most of a cell from there.</remarks>
        public static bool IsOpen(in WallTree tree, ReadOnlySpan<byte> region, float x, float y, float z) =>
            _view(tree, region, out var view) && tree.Shape.TryCell(x, y, z, out var cellX, out var cellY, out var cellZ) &&
            _number(tree, region, view, cellX, cellY, cellZ) != 0;

        /// <summary>
        /// The centre of the nearest cell that is open and was reached by a seed, no farther than
        /// <paramref name="reach"/> from the point, in a straight line. Among centres equally near,
        /// the lowest in x, then in y, then in z.
        /// </summary>
        /// <remarks>
        /// Nearest is by straight line, whatever lies between: the cell can be on the other side of
        /// a thin wall or under a floor. Every cell within the reach is looked at, so the work
        /// grows with the cube of the reach.
        /// The point need not be inside the grid. Returns false if there is no such cell, the point
        /// or the reach is not a number, the reach is negative, the tree cannot be used, or the
        /// bytes are not a flood's of this tree.
        /// </remarks>
        public static bool NearestOpen(in WallTree tree, ReadOnlySpan<byte> region, float x, float y, float z, float reach,
            out float openX, out float openY, out float openZ) {
            openX = 0f; openY = 0f; openZ = 0f;
            if (!_view(tree, region, out var view) || !(reach >= 0f) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return false; }
            var shape = tree.Shape;
            double size = shape.CellSize, far = reach;
            // One more cell each way, so that rounding here never leaves out a centre exactly at the reach.
            var wide = far + size;
            int fromX = _first(x - wide, shape.OriginX, size, shape.CountX), toX = _last(x + wide, shape.OriginX, size, shape.CountX);
            int fromY = _first(y - wide, shape.OriginY, size, shape.CountY), toY = _last(y + wide, shape.OriginY, size, shape.CountY);
            int fromZ = _first(z - wide, shape.OriginZ, size, shape.CountZ), toZ = _last(z + wide, shape.OriginZ, size, shape.CountZ);
            var best = far * far;
            var found = false;
            // Lowest x first, then y, then z, and only a nearer cell replaces the one held.
            for (var cellX = fromX; cellX <= toX; cellX++) {
                var centreX = shape.Centre(shape.OriginX, cellX);
                double dx = (double)centreX - x;
                if (dx * dx > best) { continue; }
                for (var cellY = fromY; cellY <= toY; cellY++) {
                    var centreY = shape.Centre(shape.OriginY, cellY);
                    double dy = (double)centreY - y;
                    if (dx * dx + dy * dy > best) { continue; }
                    for (var cellZ = fromZ; cellZ <= toZ; cellZ++) {
                        var centreZ = shape.Centre(shape.OriginZ, cellZ);
                        double dz = (double)centreZ - z;
                        var squared = dx * dx + dy * dy + dz * dz;
                        if (!(found ? squared < best : squared <= best)) { continue; }
                        if (_number(tree, region, view, cellX, cellY, cellZ) == 0) { continue; }
                        best = squared;
                        found = true;
                        openX = centreX; openY = centreY; openZ = centreZ;
                    }
                }
            }
            return found;
        }

        // The region of a cell of the grid: 0 for a cell that is not open, that no seed reached, or
        // that is not in the grid, and where the bytes are not a flood's of this tree.
        internal static byte NumberAt(in WallTree tree, ReadOnlySpan<byte> region, int x, int y, int z) =>
            _view(tree, region, out var view) ? _number(tree, region, view, x, y, z) : (byte)0;

        // What a flood's bytes say of themselves, once they are known to be long enough for the tree.
        private readonly struct View {
            public readonly int Bricks, Leaves, Kept;
            public readonly byte Need;
            public View(int bricks, int leaves, int kept, byte need) { Bricks = bricks; Leaves = leaves; Kept = kept; Need = need; }
        }

        private static bool _view(in WallTree tree, ReadOnlySpan<byte> region, out View view) {
            view = default;
            var least = LeastLength(tree);
            if (least == 0 || region.Length < least || region[0] == 0) { return false; }
            var kept = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(4));
            if (kept < 0 || (long)least + (long)kept * Record > region.Length) { return false; }
            view = new View(tree.Bricks.Length / WallTrees.BrickBytes, least - Header, kept, region[0]);
            return true;
        }

        private static byte _number(in WallTree tree, ReadOnlySpan<byte> region, in View view, int x, int y, int z) {
            var leaf = WallTrees.LeafOf(tree, x, y, z, out _);
            if ((uint)leaf >= (uint)view.Leaves) { return 0; }
            var number = region[Header + leaf];
            if (number == 0) { return 0; }
            // An open cube is in one region all through.
            if (leaf >= view.Bricks) { return number != Kept ? number : (byte)0; }
            var cell = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4);
            if (number != Kept) { return WallTrees.CountIn(tree, leaf, cell) >= view.Need ? number : (byte)0; }
            // Kept in full: the records lie in the bricks' order.
            var records = region.Slice(Header + view.Leaves);
            int low = 0, high = view.Kept - 1;
            while (low <= high) {
                var middle = (int)(((long)low + high) >> 1);
                var brick = BinaryPrimitives.ReadInt32LittleEndian(records.Slice(middle * Record));
                if (brick == leaf) { return records[middle * Record + 4 + cell]; }
                if (brick < leaf) { low = middle + 1; } else { high = middle - 1; }
            }
            return 0;
        }

        // A flood as it goes. Numbers is a byte a brick and then a byte an open cube. A brick's
        // mark is the cells of it reached so far, or, once the brick is kept in full, which record
        // is its own. After the bricks' marks come the bits that say which bricks wait in the queue.
        private ref struct Work {
            public WallTree Tree;
            public Span<byte> Numbers, Records;
            public Span<ulong> Marks;
            public Span<int> Queue;
            public int Bricks, Waiting, KeptCount, Reached;
            public byte Need;

            // The region a cell has so far.
            public byte NumberOf(int leaf, int cell) {
                var number = Numbers[leaf];
                if (leaf >= Bricks || number == 0) { return number; }
                if (number == Kept) { return Records[(int)Marks[leaf] * Record + 4 + cell]; }
                return ((Marks[leaf] >> cell) & 1UL) != 0UL ? number : (byte)0;
            }

            // Gives every open cell joined to a cell the number. False if a brick has to be kept
            // in full and there is no room for it.
            public bool Flood(int x, int y, int z, int leaf, int side, byte number) {
                var shape = Tree.Shape;
                int countX = shape.CountX, layer = countX * shape.CountY;
                var start = x + y * countX + z * layer;
                if (leaf >= Bricks) {
                    _cube(leaf, side, number, start);
                } else if (!_add(leaf, 1UL << ((x & 3) | ((y & 3) << 2) | ((z & 3) << 4)), number, start)) {
                    return false;
                }
                while (Waiting > 0) {
                    var at = Queue[--Waiting];
                    x = at % countX; y = at / countX % shape.CountY; z = at / layer;
                    leaf = WallTrees.LeafOf(Tree, x, y, z, out side);
                    int originX = x & ~(side - 1), originY = y & ~(side - 1), originZ = z & ~(side - 1);
                    if (leaf >= Bricks) {
                        // An open cube: on to whatever lies across each square of four cells by four of its six faces.
                        for (var v = 0; v < side; v += 4) {
                            for (var u = 0; u < side; u += 4) {
                                if (!_across(originX - 1, originY + u, originZ + v, 0, 3, 0xFFFF, number) || !_across(originX + side, originY + u, originZ + v, 0, 0, 0xFFFF, number) ||
                                    !_across(originX + u, originY - 1, originZ + v, 1, 3, 0xFFFF, number) || !_across(originX + u, originY + side, originZ + v, 1, 0, 0xFFFF, number) ||
                                    !_across(originX + u, originY + v, originZ - 1, 2, 3, 0xFFFF, number) || !_across(originX + u, originY + v, originZ + side, 2, 0, 0xFFFF, number)) { return false; }
                            }
                        }
                        continue;
                    }
                    // A brick: no longer waiting. Its reached cells spread through its open cells,
                    // and from the cells on its faces to what lies across them.
                    Marks[Bricks + (leaf >> 6)] &= ~(1UL << (leaf & 63));
                    ulong open = _open(leaf), before = _reached(leaf), all = before;
                    while (true) {
                        var grown = (all | ((all << 1) & ~X0) | ((all >> 1) & ~X3) | ((all << 4) & ~Y0) | ((all >> 4) & ~Y3) | (all << 16) | (all >> 16)) & open;
                        if (grown == all) { break; }
                        all = grown;
                    }
                    if (all != before && !_add(leaf, all & ~before, number, -1)) { return false; }
                    if (!_across(originX - 1, originY, originZ, 0, 3, _face(all, 0, 0), number) || !_across(originX + 4, originY, originZ, 0, 0, _face(all, 0, 3), number) ||
                        !_across(originX, originY - 1, originZ, 1, 3, _face(all, 1, 0), number) || !_across(originX, originY + 4, originZ, 1, 0, _face(all, 1, 3), number) ||
                        !_across(originX, originY, originZ - 1, 2, 3, _face(all, 2, 0), number) || !_across(originX, originY, originZ + 4, 2, 0, _face(all, 2, 3), number)) { return false; }
                }
                return true;
            }

            // After the last flood: a brick with open cells that no seed reached is kept in full
            // too, and the records are put in the bricks' order. False if there is no room.
            public bool Finish() {
                for (var brick = 0; brick < Bricks; brick++) {
                    var number = Numbers[brick];
                    if (number == 0 || number == Kept || Marks[brick] == _open(brick)) { continue; }
                    if (!_keep(brick, number)) { return false; }
                }
                _sort(Records, KeptCount);
                return true;
            }

            // Takes a square of four cells by four that lies across a face into the flood: an open
            // cube whole, and of a brick the cells of its near layer that the pattern names and
            // that are open. The pattern has a bit for each cell of the square, along the lower
            // of the two other axes first.
            private bool _across(int x, int y, int z, int axis, int layer, int pattern, byte number) {
                var shape = Tree.Shape;
                if (pattern == 0 || (uint)x >= (uint)shape.CountX || (uint)y >= (uint)shape.CountY || (uint)z >= (uint)shape.CountZ) { return true; }
                var leaf = WallTrees.LeafOf(Tree, x, y, z, out var side);
                if ((uint)leaf >= (uint)Numbers.Length) { return true; }
                var at = x + (y + z * shape.CountY) * shape.CountX;
                if (leaf >= Bricks) {
                    if (Numbers[leaf] == 0) { _cube(leaf, side, number, at); }
                    return true;
                }
                var entered = _spread(pattern, axis, layer) & _open(leaf) & ~_reached(leaf);
                return entered == 0UL || _add(leaf, entered, number, at);
            }

            private void _cube(int leaf, int side, byte number, int at) {
                Numbers[leaf] = number;
                Reached += side * side * side;
                Queue[Waiting++] = at;
            }

            // Gives cells of a brick that were not yet reached the number, and has the brick wait
            // in the queue, as the cell at, unless it waits there already or at is below 0.
            private bool _add(int brick, ulong cells, byte number, int at) {
                var held = Numbers[brick];
                if (held == 0 || held == number) {
                    Numbers[brick] = number;
                    Marks[brick] |= cells;
                } else {
                    // A second region in one brick: from here on its cells are kept in full.
                    if (held != Kept && !_keep(brick, held)) { return false; }
                    var record = Records.Slice((int)Marks[brick] * Record + 4, WallTrees.BrickCells);
                    for (var cell = 0; cell < WallTrees.BrickCells; cell++) {
                        if (((cells >> cell) & 1UL) != 0UL) { record[cell] = number; }
                    }
                }
                Reached += _count(cells);
                if (at >= 0) {
                    ref var waits = ref Marks[Bricks + (brick >> 6)];
                    var bit = 1UL << (brick & 63);
                    if ((waits & bit) == 0UL) {
                        waits |= bit;
                        Queue[Waiting++] = at;
                    }
                }
                return true;
            }

            // Makes a brick's record from its mark: the cells reached so far hold the number. The
            // mark then says which record it is.
            private bool _keep(int brick, byte number) {
                if (((long)KeptCount + 1) * Record > Records.Length) { return false; }
                var record = Records.Slice(KeptCount * Record, Record);
                BinaryPrimitives.WriteInt32LittleEndian(record, brick);
                var cells = Marks[brick];
                for (var cell = 0; cell < WallTrees.BrickCells; cell++) { record[4 + cell] = ((cells >> cell) & 1UL) != 0UL ? number : (byte)0; }
                Numbers[brick] = Kept;
                Marks[brick] = (ulong)KeptCount++;
                return true;
            }

            // The cells of a brick that are open for the class.
            private ulong _open(int brick) {
                var open = 0UL;
                for (var cell = 0; cell < WallTrees.BrickCells; cell++) {
                    if (WallTrees.CountIn(Tree, brick, cell) >= Need) { open |= 1UL << cell; }
                }
                return open;
            }

            // The cells of a brick that have been reached, by any region.
            private ulong _reached(int brick) {
                if (Numbers[brick] != Kept) { return Marks[brick]; }
                var record = Records.Slice((int)Marks[brick] * Record + 4, WallTrees.BrickCells);
                var reached = 0UL;
                for (var cell = 0; cell < WallTrees.BrickCells; cell++) {
                    if (record[cell] != 0) { reached |= 1UL << cell; }
                }
                return reached;
            }

        }

        // Puts records in the order of their bricks: a heap, the highest brick at the top, taken apart from the top.
        private static void _sort(Span<byte> records, int count) {
            Span<byte> spare = stackalloc byte[Record];
            for (var i = count / 2 - 1; i >= 0; i--) { _sift(records, i, count, spare); }
            for (var end = count - 1; end > 0; end--) {
                _swap(records, 0, end, spare);
                _sift(records, 0, end, spare);
            }
        }

        private static int _brickOf(Span<byte> records, int record) => BinaryPrimitives.ReadInt32LittleEndian(records.Slice(record * Record));

        private static void _swap(Span<byte> records, int a, int b, Span<byte> spare) {
            Span<byte> first = records.Slice(a * Record, Record), second = records.Slice(b * Record, Record);
            first.CopyTo(spare);
            second.CopyTo(first);
            spare.CopyTo(second);
        }

        // Lets a record sink among the first count records.
        private static void _sift(Span<byte> records, int at, int count, Span<byte> spare) {
            while (true) {
                var below = 2 * at + 1;
                if (below >= count) { return; }
                if (below + 1 < count && _brickOf(records, below + 1) > _brickOf(records, below)) { below++; }
                if (_brickOf(records, at) >= _brickOf(records, below)) { return; }
                _swap(records, at, below, spare);
                at = below;
            }
        }

        // The cells of a brick's layer along an axis, as a pattern of sixteen bits.
        private static int _face(ulong cells, int axis, int layer) {
            if (axis == 2) { return (int)((cells >> (16 * layer)) & 0xFFFFUL); }
            var pattern = 0;
            for (var k = 0; k < 16; k++) {
                var cell = axis == 0 ? layer + 4 * k : (k & 3) + 4 * layer + 16 * (k >> 2);
                pattern |= (int)((cells >> cell) & 1UL) << k;
            }
            return pattern;
        }

        // A pattern of sixteen bits as cells of a brick's layer along an axis.
        private static ulong _spread(int pattern, int axis, int layer) {
            if (axis == 2) { return (ulong)pattern << (16 * layer); }
            var cells = 0UL;
            for (var k = 0; k < 16; k++) {
                var cell = axis == 0 ? layer + 4 * k : (k & 3) + 4 * layer + 16 * (k >> 2);
                cells |= (ulong)((pattern >> k) & 1) << cell;
            }
            return cells;
        }

        // How many bits are set. Written out: the older of the two targets has no call for it.
        private static int _count(ulong bits) {
            bits -= (bits >> 1) & 0x5555555555555555UL;
            bits = (bits & 0x3333333333333333UL) + ((bits >> 2) & 0x3333333333333333UL);
            bits = (bits + (bits >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((bits * 0x0101010101010101UL) >> 56);
        }

        // The first and last cells along an axis whose centres lie at or beyond a bound, held to the grid.
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
