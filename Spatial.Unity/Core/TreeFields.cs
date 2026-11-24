using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// A travel field over a <see cref="WallTree"/>: how far each place is from where the field
    /// starts, through the places a body of one size can be in. The tree, the regions, the bytes,
    /// the seeds and the unit belong together.
    /// </summary>
    /// <remarks>
    /// What <see cref="TravelField"/> is over a dense map, for a grid too large to keep a count
    /// for every cell. Make the bytes with <see cref="TreeFields.Build"/>, and ask them with
    /// <see cref="TreeFields.TravelAt"/> and <see cref="TreeFields.Next"/>. Nothing is copied or
    /// checked here.
    /// </remarks>
    public readonly ref struct TreeField {
        /// <summary>The tree the field is over.</summary>
        public readonly WallTree Tree;
        /// <summary>The regions the field was built through: what <see cref="TreeRegions.FromSeeds"/> made for the tree.</summary>
        public readonly ReadOnlySpan<byte> Region;
        /// <summary>What <see cref="TreeFields.Build"/> wrote, cut to the length it gave.</summary>
        public readonly ReadOnlySpan<byte> Bytes;
        /// <summary>The seeds the field was built from, in the same order.</summary>
        public readonly ReadOnlySpan<TravelSeed> Seeds;
        /// <summary>Metres of travel per count.</summary>
        public readonly float Unit;

        /// <summary>Views a field. Nothing is copied or checked.</summary>
        /// <param name="tree">The tree.</param>
        /// <param name="region">The regions it was built through.</param>
        /// <param name="bytes">What the build wrote, cut to its length.</param>
        /// <param name="seeds">The seeds it was built from.</param>
        /// <param name="unit">Metres per count.</param>
        public TreeField(WallTree tree, ReadOnlySpan<byte> region, ReadOnlySpan<byte> bytes, ReadOnlySpan<TravelSeed> seeds, float unit) {
            Tree = tree; Region = region; Bytes = bytes; Seeds = seeds; Unit = unit;
        }
    }

    /// <summary>A place near a point, as <see cref="TreeFields.Around"/> gives it: the place and its point.</summary>
    public struct TreeNear {
        /// <summary>The place.</summary>
        public int Place;
        /// <summary>The place's point.</summary>
        public float X, Y, Z;
    }

    /// <summary>
    /// Builds a travel field over a <see cref="WallTree"/>, and says how far a point is along it
    /// and which point to fly to next.
    /// </summary>
    /// <remarks>
    /// The field is kept for places, not cells: an open cube; the open cells of a brick when they
    /// are joined and plain (see <see cref="WallTrees"/>); and each open cell of any other brick.
    /// A place's point is the cube's centre, the middle of the brick's open cells, or the cell's
    /// centre, and the field holds the travel to it. Open means open for the regions' size
    /// class and reached by their seeds: a field goes through one size class's regions, as a
    /// dense field does.
    /// The travel is worked out any angle: a place takes its travel straight from the place
    /// before last where every cell the line between their points touches is open, so through
    /// open space it is close to the straight line. A place's count is never below that of the
    /// place it was reached from, plus one, so every place a seed did not start has a neighbour
    /// lower than itself.
    /// A point belongs to the cell <see cref="GridShape.TryCentre"/> finds it in, as over a dense map,
    /// so a point on the face between two cells is in the higher one. A point whose cell is not
    /// open is refused, where a dense field may still answer from the eight cells round it.
    /// Seeds start as a dense field's do (see <see cref="TravelSeed"/>): the open cells whose
    /// centres are inside the solid, with a clear way to the point or square to the plane. The
    /// field keeps which cells they are.
    /// A tree has no costs, so neither has a field over it. Nothing is allocated and nothing
    /// throws.
    /// </remarks>
    public static class TreeFields {
        /// <summary>A count that the field does not reach.</summary>
        public const ushort Unreached = ushort.MaxValue;
        /// <summary>The most seeds a field can start from.</summary>
        public const int MostSeeds = 256;
        // The ints of the build's work for each place, each brick and open cube, and each cell a seed starts.
        internal const int WorkPerPlace = 6, WorkPerLeaf = 1, WorkPerStart = 4;

        // A brick's count when its cells are kept one at a time, in a record.
        private const ushort Kept = ushort.MaxValue - 1;
        // The largest count a field holds.
        private const int Most = ushort.MaxValue - 2;
        // The header: bricks, cubes, bricks kept a cell at a time, started cells.
        private const int Header = 16;
        // A record: the brick, then a count for each of its cells.
        private const int Record = 4 + 2 * WallTrees.BrickCells;
        // A started cell: its place, its cell of the grid, its count, which seed, and a spare byte.
        private const int StartBytes = 12;
        // How many places on the point to fly to looks.
        private const int Ahead = 3;
        // The most places that can touch a place across one face: a cube of 16 cells beside bricks kept a cell at a time.
        private const int MostContacts = 16 * 16;
        /// <summary>The most places <see cref="Around"/> gives: the point's own, and those across its six faces.</summary>
        public const int MostNear = 1 + 6 * MostContacts;
        // How near, as a part of a line, two crossings of cell faces are taken as one.
        private const double Graze = 1e-5d;

        /// <summary>
        /// How long a field's bytes are, how many places it can have, and how many ints the build's
        /// work needs, for a tree, its regions and the seeds.
        /// </summary>
        /// <remarks>
        /// The build needs a float for each place and the ints said. Returns false if the tree
        /// cannot be used, the regions are not a flood's of this tree, there are more than
        /// <see cref="MostSeeds"/> seeds, a seed may not be used (as <see cref="Travel.Build"/> says),
        /// or a length is more than an int holds.
        /// </remarks>
        /// <param name="tree">The tree.</param>
        /// <param name="region">The regions to build through.</param>
        /// <param name="seeds">Where the field starts.</param>
        /// <param name="length">The field's bytes.</param>
        /// <param name="places">The floats of the build's sums.</param>
        /// <param name="work">The ints of the build's work.</param>
        public static bool Measure(in WallTree tree, ReadOnlySpan<byte> region, ReadOnlySpan<TravelSeed> seeds, out int length, out int places, out int work) {
            length = 0; places = 0; work = 0;
            if (seeds.Length > MostSeeds || !tree.IsValid || !TreeRegions.TryView(tree, region, out var view)) { return false; }
            int bricks = view.Bricks, cubes = view.Leaves - view.Bricks;
            var kept = 0L;
            for (var brick = 0; brick < bricks; brick++) {
                var cells = TreeRegions.CellsOf(tree, region, view, brick);
                if (cells != 0UL && !_plain(cells)) { kept++; }
            }
            var started = 0L;
            for (var seed = 0; seed < seeds.Length; seed++) {
                if (!_usable(seeds[seed])) { return false; }
                started += _started(tree, region, view, seeds[seed], seed, default);
            }
            var total = Header + 2L * (bricks + cubes) + kept * Record + started * StartBytes;
            var all = (long)bricks + cubes + kept * WallTrees.BrickCells;
            var ints = all * WorkPerPlace + (long)(bricks + cubes) * WorkPerLeaf + started * WorkPerStart;
            if (total > int.MaxValue || all > int.MaxValue || ints > int.MaxValue) { return false; }
            length = (int)total; places = (int)all; work = (int)ints;
            return true;
        }

        /// <summary>
        /// Builds a field over a tree: every place that can be reached from a seed through the
        /// regions' open cells gets its travel from where the seeds start, as a count of
        /// <paramref name="unit"/>, rounded to the nearest.
        /// </summary>
        /// <remarks>
        /// <paramref name="field"/>, <paramref name="sums"/> and <paramref name="work"/> need what
        /// <see cref="Measure"/> says; the last two are only scratch. <paramref name="length"/> is
        /// the field's length: keep that many bytes. <paramref name="reached"/> is how many places
        /// got a count, and <paramref name="cut"/> how many were left unreached because theirs does
        /// not fit in 16 bits. Returns false if <see cref="Measure"/> would, the unit is not above
        /// zero or is more than a quarter of a cell, or a buffer is too short.
        /// </remarks>
        /// <param name="tree">The tree.</param>
        /// <param name="region">The regions to build through.</param>
        /// <param name="seeds">Where the field starts.</param>
        /// <param name="unit">Metres per count.</param>
        /// <param name="field">The field's bytes.</param>
        /// <param name="sums">A float for each place.</param>
        /// <param name="work">The ints Measure says.</param>
        /// <param name="length">How many bytes the field is.</param>
        /// <param name="reached">How many places got a count.</param>
        /// <param name="cut">How many were too far for a count.</param>
        public static bool Build(in WallTree tree, ReadOnlySpan<byte> region, ReadOnlySpan<TravelSeed> seeds, float unit,
            Span<byte> field, Span<float> sums, Span<int> work, out int length, out int reached, out int cut) {
            length = 0; reached = 0; cut = 0;
            if (!Measure(tree, region, seeds, out var need, out var places, out var ints) || !(unit > 0f) || !(unit <= tree.Shape.CellSize * 0.25f) ||
                field.Length < need || sums.Length < places || work.Length < ints) {
                return false;
            }
            TreeRegions.TryView(tree, region, out var view);
            int bricks = view.Bricks, cubes = view.Leaves - view.Bricks;
            // For each place: its value; the place it was reached from; what its line is drawn from (a
            // place; -1 nothing; -2 - s for seed s's point; -2 - seeds - k for started cell k); where it waits in the heap (0 not
            // yet, -1 taken up); the heap, then the order places are taken up in; and a cell of it.
            var value = sums.Slice(0, places);
            Span<int> via = work.Slice(0, places), from = work.Slice(places, places), heapAt = work.Slice(2 * places, places);
            Span<int> heap = work.Slice(3 * places, places), order = work.Slice(4 * places, places), cellOf = work.Slice(5 * places, places);
            var keptOf = work.Slice(WorkPerPlace * places, bricks + cubes);
            var starts = work.Slice(WorkPerPlace * places + WorkPerLeaf * (bricks + cubes));
            value.Fill(float.PositiveInfinity);
            via.Fill(-1); from.Fill(-1); heapAt.Fill(0); cellOf.Fill(-1);

            var kept = 0;
            for (var brick = 0; brick < bricks; brick++) {
                var cells = TreeRegions.CellsOf(tree, region, view, brick);
                keptOf[brick] = cells != 0UL && !_plain(cells) ? kept++ : -1;
            }
            var at = new Places {
                Tree = tree, Region = region, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept, KeptOf = keptOf,
                Block = stackalloc int[8], Leaves = stackalloc int[8], Sides = stackalloc int[8], Masks = stackalloc ulong[8], Contacts = stackalloc Contact[MostContacts],
            };
            at.Block.Fill(-1);

            // The seeds: each started cell's place starts at the least of a cell's start and the way from it to the place's point.
            var started = 0;
            for (var seed = 0; seed < seeds.Length; seed++) { started += _started(tree, region, view, seeds[seed], seed, starts.Slice(WorkPerStart * started)); }
            var shape = tree.Shape;
            for (var k = 0; k < started; k++) {
                var cell = starts[WorkPerStart * k + 1];
                _cellOf(shape, cell, out var cx, out var cy, out var cz);
                starts[WorkPerStart * k] = _placeOf(ref at, cx, cy, cz);
            }
            _sortStarts(starts, started);
            var held = 0;
            for (var k = 0; k < started; k++) {
                int place = starts[WorkPerStart * k], cell = starts[WorkPerStart * k + 1], seed = starts[WorkPerStart * k + 3];
                if (place < 0) { continue; }
                if (cellOf[place] < 0) { cellOf[place] = cell; }
                _point(ref at, place, cellOf[place], out var px, out var py, out var pz);
                _centre(shape, cell, out var cx, out var cy, out var cz);
                var begin = (float)(_unfixed(starts[WorkPerStart * k + 2]) + _length(px, py, pz, cx, cy, cz));
                if (begin < value[place]) {
                    value[place] = begin;
                    from[place] = seeds[seed].Start == TravelStart.FromPoint ? -2 - seed : -2 - seeds.Length - k;
                    held = _lower(value, heap, heapAt, held, place);
                }
            }

            // Theta*: the place with the least value is taken up, and each neighbour not yet taken
            // up is offered the way through it, or straight from the place it was drawn from.
            var taken = 0;
            while (held > 0) {
                var place = _pop(value, heap, heapAt, ref held);
                order[taken++] = place;
                var spent = value[place];
                _point(ref at, place, cellOf[place], out var ax, out var ay, out var az);
                var up = from[place];
                double ux = 0d, uy = 0d, uz = 0d, upValue = 0d;
                if (up >= 0) { _point(ref at, up, cellOf[up], out ux, out uy, out uz); upValue = value[up]; }
                else if (up <= -2 && -2 - up < seeds.Length) { var s = seeds[-2 - up]; ux = s.X; uy = s.Y; uz = s.Z; }
                else if (up <= -2) { var k = -2 - seeds.Length - up; _centre(shape, starts[WorkPerStart * k + 1], out ux, out uy, out uz); upValue = _unfixed(starts[WorkPerStart * k + 2]); }
                var seeded = _firstStart(starts, started, place);
                for (var face = 0; face < 6; face++) {
                var count = _contacts(ref at, place, cellOf[place], face);
                for (var c = 0; c < count; c++) {
                    var next = at.Contacts[c].Place;
                    if (heapAt[next] < 0) { continue; }
                    if (cellOf[next] < 0) { cellOf[next] = at.Contacts[c].Outside; }
                    _point(ref at, next, cellOf[next], out var nx, out var ny, out var nz);
                    var floor = (double)spent + 1e-3d;
                    var offer = (float)((double)spent + _length(ax, ay, az, nx, ny, nz));
                    var line = place;
                    if (up != -1) {
                        var straight = (float)Math.Max(upValue + _length(ux, uy, uz, nx, ny, nz), floor);
                        if (straight < offer && _clear(ref at, ux, uy, uz, nx, ny, nz)) { offer = straight; line = up; }
                    }
                    // From a place a seed started: straight from its started cells, where the line is clear.
                    for (var k = seeded; k >= 0 && k < started && starts[WorkPerStart * k] == place; k++) {
                        _centre(shape, starts[WorkPerStart * k + 1], out var cx, out var cy, out var cz);
                        var fromCell = (float)Math.Max(_unfixed(starts[WorkPerStart * k + 2]) + _length(cx, cy, cz, nx, ny, nz), floor);
                        if (fromCell < offer && _clear(ref at, cx, cy, cz, nx, ny, nz)) { offer = fromCell; line = -2 - seeds.Length - k; }
                    }
                    if (offer < value[next]) {
                        value[next] = offer;
                        via[next] = place;
                        from[next] = line;
                        held = _lower(value, heap, heapAt, held, next);
                    }
                }
                }
            }

            // The counts, in the order the places were taken up: never below the count of the place reached from, plus one.
            var counts = heap;
            counts.Fill(Unreached);
            var over = 0;
            for (var k = 0; k < taken; k++) {
                var place = order[k];
                var rounded = Math.Floor((double)value[place] / unit + 0.5d);
                var before = via[place];
                if (before >= 0) {
                    if (counts[before] == Unreached) { over++; continue; }
                    if (rounded < counts[before] + 1d) { rounded = counts[before] + 1d; }
                }
                if (rounded > Most) { over++; continue; }
                counts[place] = (int)rounded;
            }

            // The bytes.
            field.Slice(0, need).Clear();
            _put(field, 0, bricks); _put(field, 4, cubes); _put(field, 8, kept); _put(field, 12, started);
            var records = Header + 2 * (bricks + cubes);
            var stored = 0;
            for (var brick = 0; brick < bricks; brick++) {
                var k = keptOf[brick];
                if (k < 0) {
                    _put16(field, Header + 2 * brick, counts[brick]);
                    if (counts[brick] != Unreached) { stored++; }
                    continue;
                }
                _put16(field, Header + 2 * brick, Kept);
                var record = records + k * Record;
                _put(field, record, brick);
                for (var cell = 0; cell < WallTrees.BrickCells; cell++) {
                    var c = counts[bricks + cubes + k * WallTrees.BrickCells + cell];
                    _put16(field, record + 4 + 2 * cell, c);
                    if (c != Unreached) { stored++; }
                }
            }
            for (var cube = 0; cube < cubes; cube++) {
                var c = counts[bricks + cube];
                _put16(field, Header + 2 * (bricks + cube), c);
                if (c != Unreached) { stored++; }
            }
            var list = records + kept * Record;
            for (var k = 0; k < started; k++) {
                var e = list + k * StartBytes;
                _put(field, e, starts[WorkPerStart * k]);
                _put(field, e + 4, starts[WorkPerStart * k + 1]);
                var start = Math.Floor(_unfixed(starts[WorkPerStart * k + 2]) / (double)unit + 0.5d);
                _put16(field, e + 8, start > Most ? Most : (int)start);
                field[e + 10] = (byte)starts[WorkPerStart * k + 3];
            }
            length = need;
            reached = stored;
            cut = over;
            return true;
        }

        /// <summary>
        /// How far a point is along a field, in metres: cheap enough to ask of every field of a
        /// grid at every step. <paramref name="place"/> is the place the point is in.
        /// </summary>
        /// <remarks>
        /// The least, over the point's place and the places beside it, of a place's travel and the
        /// straight distance to its point. In a cell a seed started, what the seed starts the point
        /// itself at. Returns false, with travel 0 and place -1, if the point is not in the grid or
        /// not a number, its cell is not open, the field does not reach its place, or the field
        /// cannot be used.
        /// </remarks>
        /// <param name="field">The field.</param>
        /// <param name="x">The point.</param>
        /// <param name="y">The point.</param>
        /// <param name="z">The point.</param>
        /// <param name="travel">Metres along the field.</param>
        /// <param name="place">The place the point is in.</param>
        public static bool TravelAt(in TreeField field, float x, float y, float z, out float travel, out int place) {
            travel = 0f; place = -1;
            if (!_check(field, out var view, out var bricks, out var cubes, out var kept)) { return false; }
            var at = new Places {
                Tree = field.Tree, Region = field.Region, Field = field.Bytes, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept,
                Block = stackalloc int[8], Leaves = stackalloc int[8], Sides = stackalloc int[8], Masks = stackalloc ulong[8], Contacts = stackalloc Contact[MostContacts],
            };
            at.Block.Fill(-1);
            var shape = at.Tree.Shape;
            if (!shape.TryCell(x, y, z, out var cx, out var cy, out var cz)) { return false; }
            var here = _placeOf(ref at, cx, cy, cz);
            if (here < 0) { return false; }
            var own = _count(at, here);
            if (own == Unreached) { return false; }
            var cell = shape.Index(cx, cy, cz);
            var bytes = field.Bytes;
            for (var k = _firstStarted(at, here); k >= 0 && k < _int(bytes, 12) && _int(bytes, _list(at) + k * StartBytes) == here; k++) {
                if (_int(bytes, _list(at) + k * StartBytes + 4) != cell) { continue; }
                place = here;
                var which = bytes[_list(at) + k * StartBytes + 10];
                if (which >= field.Seeds.Length) { return false; }
                travel = _start(field.Seeds[which], x, y, z);
                return true;
            }
            _point(ref at, here, cell, out var hx, out var hy, out var hz);
            var best = own * (double)field.Unit + _length(x, y, z, hx, hy, hz);
            for (var face = 0; face < 6; face++) {
                var count = _contacts(ref at, here, cell, face);
                for (var c = 0; c < count; c++) {
                    var other = _count(at, at.Contacts[c].Place);
                    if (other == Unreached) { continue; }
                    _point(ref at, at.Contacts[c].Place, at.Contacts[c].Outside, out var px, out var py, out var pz);
                    var through = other * (double)field.Unit + _length(x, y, z, px, py, pz);
                    if (through < best) { best = through; }
                }
            }
            place = here;
            travel = (float)best;
            return true;
        }

        /// <summary>
        /// The place a point is in, as <see cref="TravelAt"/> finds it, and nothing else: cheap, for
        /// telling when a follower has entered another place.
        /// </summary>
        /// <remarks>
        /// The same for every field over the same tree and regions, so it may give a place the field
        /// does not reach, which <see cref="TravelAt"/> refuses. Returns false, with -1, where the point
        /// is not in the grid or not a number, its cell is not open, or the field cannot be used.
        /// </remarks>
        /// <param name="field">Any field over the tree and regions.</param>
        /// <param name="x">The point.</param>
        /// <param name="y">The point.</param>
        /// <param name="z">The point.</param>
        /// <param name="place">The place the point is in.</param>
        public static bool PlaceAt(in TreeField field, float x, float y, float z, out int place) {
            place = -1;
            if (!_check(field, out var view, out var bricks, out var cubes, out var kept)) { return false; }
            var at = new Places {
                Tree = field.Tree, Region = field.Region, Field = field.Bytes, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept,
                Block = stackalloc int[8], Leaves = stackalloc int[8], Sides = stackalloc int[8], Masks = stackalloc ulong[8],
            };
            at.Block.Fill(-1);
            if (!at.Tree.Shape.TryCell(x, y, z, out var cx, out var cy, out var cz)) { return false; }
            place = _placeOf(ref at, cx, cy, cz);
            return place >= 0;
        }

        /// <summary>
        /// The places <see cref="TravelAt"/> looks at for a point: its own first, then those across
        /// its faces, each with its point. Found once, they can be asked of every field over the same
        /// tree and regions, with <see cref="TravelNear"/>, while the point stays in the same place.
        /// </summary>
        /// <remarks>
        /// Most points have about ten places round them; <see cref="MostNear"/> is enough for any. Where
        /// a point has more than <paramref name="near"/> holds, it returns false and
        /// <paramref name="count"/> says how many it has, and what was written is not to be used: ask
        /// again with room for them, or use TravelAt. Returns false, with 0, as <see cref="PlaceAt"/> does.
        /// </remarks>
        /// <param name="field">Any field over the tree and regions.</param>
        /// <param name="x">The point.</param>
        /// <param name="y">The point.</param>
        /// <param name="z">The point.</param>
        /// <param name="near">Where the places are written.</param>
        /// <param name="count">How many places the point has.</param>
        public static bool Around(in TreeField field, float x, float y, float z, Span<TreeNear> near, out int count) {
            count = 0;
            if (near.Length == 0 || !_check(field, out var view, out var bricks, out var cubes, out var kept)) { return false; }
            var at = new Places {
                Tree = field.Tree, Region = field.Region, Field = field.Bytes, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept,
                Block = stackalloc int[8], Leaves = stackalloc int[8], Sides = stackalloc int[8], Masks = stackalloc ulong[8], Contacts = stackalloc Contact[MostContacts],
            };
            at.Block.Fill(-1);
            var shape = at.Tree.Shape;
            if (!shape.TryCell(x, y, z, out var cx, out var cy, out var cz)) { return false; }
            var here = _placeOf(ref at, cx, cy, cz);
            if (here < 0) { return false; }
            var cell = shape.Index(cx, cy, cz);
            _point(ref at, here, cell, out var hx, out var hy, out var hz);
            near[count++] = new TreeNear { Place = here, X = (float)hx, Y = (float)hy, Z = (float)hz };
            var room = near.Length;
            for (var face = 0; face < 6; face++) {
                var contacts = _contacts(ref at, here, cell, face);
                // Past the room given, only counted.
                if (count + contacts > room) { count += contacts; continue; }
                for (var c = 0; c < contacts; c++) {
                    _point(ref at, at.Contacts[c].Place, at.Contacts[c].Outside, out var px, out var py, out var pz);
                    near[count++] = new TreeNear { Place = at.Contacts[c].Place, X = (float)px, Y = (float)py, Z = (float)pz };
                }
            }
            return count <= room;
        }

        /// <summary>
        /// How far a point is along a field, from the places <see cref="Around"/> found for it: what
        /// <see cref="TravelAt"/> gives, to within a few hundredths of a millimetre since the places'
        /// points are kept as floats, with the places looked up once for every field.
        /// </summary>
        /// <remarks>
        /// The places must be <see cref="Around"/>'s for a field over the same tree and regions, at a
        /// point in the same place as this one (<see cref="PlaceAt"/> tells). Returns false, with 0, as
        /// <see cref="TravelAt"/> does, or if a place is not one of this field's.
        /// </remarks>
        /// <param name="field">The field.</param>
        /// <param name="x">The point.</param>
        /// <param name="y">The point.</param>
        /// <param name="z">The point.</param>
        /// <param name="near">The places Around gave, cut to its count.</param>
        /// <param name="travel">Metres along the field.</param>
        public static bool TravelNear(in TreeField field, float x, float y, float z, ReadOnlySpan<TreeNear> near, out float travel) {
            travel = 0f;
            if (near.Length == 0 || !_check(field, out var view, out var bricks, out var cubes, out var kept)) { return false; }
            var at = new Places { Tree = field.Tree, Region = field.Region, Field = field.Bytes, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept };
            var places = bricks + cubes + kept * WallTrees.BrickCells;
            var here = near[0].Place;
            if ((uint)here >= (uint)places) { return false; }
            var own = _count(at, here);
            if (own == Unreached) { return false; }
            var bytes = field.Bytes;
            var first = _firstStarted(at, here);
            if (first >= 0) {
                // A place a seed started: in one of its started cells, what the seed starts the point at.
                var shape = at.Tree.Shape;
                if (!shape.TryCell(x, y, z, out var cx, out var cy, out var cz)) { return false; }
                var cell = shape.Index(cx, cy, cz);
                for (var k = first; k < _int(bytes, 12) && _int(bytes, _list(at) + k * StartBytes) == here; k++) {
                    if (_int(bytes, _list(at) + k * StartBytes + 4) != cell) { continue; }
                    var which = bytes[_list(at) + k * StartBytes + 10];
                    if (which >= field.Seeds.Length) { return false; }
                    travel = _start(field.Seeds[which], x, y, z);
                    return true;
                }
            }
            var best = own * (double)field.Unit + _length(x, y, z, near[0].X, near[0].Y, near[0].Z);
            for (var i = 1; i < near.Length; i++) {
                var place = near[i].Place;
                if ((uint)place >= (uint)places) { return false; }
                var other = _count(at, place);
                if (other == Unreached) { continue; }
                var through = other * (double)field.Unit + _length(x, y, z, near[i].X, near[i].Y, near[i].Z);
                if (through < best) { best = through; }
            }
            travel = (float)best;
            return true;
        }

        /// <summary>
        /// The point to fly to next along a field, and how far the point asked about is along it.
        /// Fly straight to it, and ask again on entering another place or on reaching it.
        /// </summary>
        /// <remarks>
        /// From a place, the next is a lower place beside it: the one by which the way on is
        /// shortest, as its travel and the straight way to it say. The point is the centre of that
        /// place's cell across the face they share or, where that cell is not in sight, first the
        /// centre of this place's own cell beside it. Then, while the next place's own point to fly
        /// to is in sight, that one, up to three places on. In sight means that every cell the
        /// straight line passes through is open, and so is every cell beside an edge or a corner it
        /// crosses: a body at the point asked about can fly straight to the point given and stay in
        /// open cells. (A line that runs along a face or an edge, from a point on it, may touch the
        /// closed cells beyond it without entering them.) In a cell a seed
        /// started the point is the seed's point, or just behind its plane, square to it, or as far
        /// towards it as is in sight; from elsewhere in such a place, the nearest started cell in
        /// sight or, where none is, a step towards one through the brick's open cells.
        /// Returns false as <see cref="TravelAt"/> does, and also where there is nowhere lower to go,
        /// which is at the seed's point itself, behind its plane, or as near the point as open cells
        /// let a point be: a point there has arrived.
        /// </remarks>
        /// <param name="field">The field.</param>
        /// <param name="x">The point.</param>
        /// <param name="y">The point.</param>
        /// <param name="z">The point.</param>
        /// <param name="toX">The point to fly to.</param>
        /// <param name="toY">The point to fly to.</param>
        /// <param name="toZ">The point to fly to.</param>
        /// <param name="travel">Metres along the field.</param>
        /// <param name="place">The place the point is in.</param>
        public static bool Next(in TreeField field, float x, float y, float z, out float toX, out float toY, out float toZ, out float travel, out int place) {
            toX = 0f; toY = 0f; toZ = 0f; travel = 0f; place = -1;
            if (!_check(field, out var view, out var bricks, out var cubes, out var kept)) { return false; }
            var at = new Places {
                Tree = field.Tree, Region = field.Region, Field = field.Bytes, View = view, Bricks = bricks, Cubes = cubes, KeptCount = kept,
                Block = stackalloc int[8], Leaves = stackalloc int[8], Sides = stackalloc int[8], Masks = stackalloc ulong[8], Contacts = stackalloc Contact[MostContacts],
            };
            at.Block.Fill(-1);
            var shape = at.Tree.Shape;
            if (!shape.TryCell(x, y, z, out var cx, out var cy, out var cz)) { return false; }
            var here = _placeOf(ref at, cx, cy, cz);
            if (here < 0 || _count(at, here) == Unreached) { return false; }
            var cell = shape.Index(cx, cy, cz);
            var chosen = _hop(ref at, field, here, cell, x, y, z, out var bx, out var by, out var bz, out var chosenCell, out var best);
            if (chosen == -1) { return false; }
            for (var ahead = 0; ahead < Ahead && chosen >= 0; ahead++) {
                if (_firstStarted(at, chosen) >= 0) { break; }
                var further = _hop(ref at, field, chosen, chosenCell, bx, by, bz, out var fx, out var fy, out var fz, out var furtherCell, out _);
                if (further == -1 || !_clear(ref at, x, y, z, fx, fy, fz)) { break; }
                bx = fx; by = fy; bz = fz; chosen = further; chosenCell = furtherCell;
            }
            toX = (float)bx; toY = (float)by; toZ = (float)bz;
            // Rounded to floats the point can lie a rounding step past where the line was looked at: then it is pulled back along the line.
            if (!_clear(ref at, x, y, z, toX, toY, toZ)) {
                var pulled = false;
                for (var back = 1e-6d; back < 1d && !pulled; back *= 4d) {
                    float px = (float)(bx + (x - bx) * back), py = (float)(by + (y - by) * back), pz = (float)(bz + (z - bz) * back);
                    if (_clear(ref at, x, y, z, px, py, pz)) { toX = px; toY = py; toZ = pz; pulled = true; }
                }
                if (!pulled) { toX = x; toY = y; toZ = z; }
            }
            // The point given is the point asked: there is nowhere to go from here.
            if (toX == x && toY == y && toZ == z) { toX = 0f; toY = 0f; toZ = 0f; return false; }
            travel = (float)best;
            place = here;
            return true;
        }

        // One hop from a point in a place: the lower place to go to, the point to aim at and the
        // cell of the place it is in; -2 for into a seed, -3 for a started cell, -1 for none.
        private static int _hop(ref Places at, in TreeField field, int here, int cell, double x, double y, double z,
            out double bx, out double by, out double bz, out int aimCell, out double best) {
            bx = 0d; by = 0d; bz = 0d; aimCell = -1; best = double.PositiveInfinity;
            var chosen = -1;
            var shape = at.Tree.Shape;
            var own = _count(at, here);
            for (var face = 0; face < 6; face++) {
            var count = _contacts(ref at, here, cell, face);
            for (var c = 0; c < count; c++) {
                var contact = at.Contacts[c];
                var other = _count(at, contact.Place);
                if (other == Unreached || other >= own) { continue; }
                _centre(shape, contact.Outside, out var ox, out var oy, out var oz);
                _point(ref at, contact.Place, contact.Outside, out var px, out var py, out var pz);
                var d = other * (double)field.Unit + _length(ox, oy, oz, px, py, pz) + _length(x, y, z, ox, oy, oz);
                if (!(d < best)) { continue; }
                best = d; chosen = contact.Place;
                // The other place's cell if it is in sight; otherwise this place's own cell beside it first.
                if (contact.Inside == cell || _clear(ref at, x, y, z, ox, oy, oz)) { bx = ox; by = oy; bz = oz; aimCell = contact.Outside; }
                else {
                    // This place's own cell beside the face; through a brick's open cells one step at a time, where the straight line to it is not clear.
                    _centre(shape, contact.Inside, out bx, out by, out bz); aimCell = contact.Inside; chosen = here;
                    if (here < at.Bricks && !_clear(ref at, x, y, z, bx, by, bz)) {
                        var step = _stepToward(ref at, cell, contact.Inside);
                        if (step >= 0) { _centre(shape, step, out bx, out by, out bz); aimCell = step; }
                    }
                }
            }
            }
            // In a place a seed started: into the seed from one of its cells, to the nearest of them in sight from elsewhere.
            var bytes = field.Bytes;
            var hidden = -1; var hiddenBest = double.PositiveInfinity;
            for (var k = _firstStarted(at, here); k >= 0 && k < _int(bytes, 12) && _int(bytes, _list(at) + k * StartBytes) == here; k++) {
                var e = _list(at) + k * StartBytes;
                var started = _int(bytes, e + 4);
                if (bytes[e + 10] >= field.Seeds.Length) { continue; }
                var seed = field.Seeds[bytes[e + 10]];
                _centre(shape, started, out var sx, out var sy, out var sz);
                if (started == cell) {
                    var into = (double)_start(seed, (float)x, (float)y, (float)z);
                    if (!(into <= best)) { continue; }
                    best = into; chosen = -2; aimCell = cell;
                    // At the seed's point or behind its plane: nowhere lower.
                    if (!(into > 0d)) { bx = x; by = y; bz = z; continue; }
                    _into(seed, x, y, z, out bx, out by, out bz);
                    if (_clear(ref at, x, y, z, bx, by, bz)) { continue; }
                    // Not in sight: as far into the seed as is in sight along the one line from the cell's centre, the same from
                    // anywhere in the cell, so that a follower comes to rest there. To the centre first where that point is not in sight.
                    _into(seed, sx, sy, sz, out var fx, out var fy, out var fz);
                    _farthest(ref at, sx, sy, sz, ref fx, ref fy, ref fz);
                    if (_length(x, y, z, fx, fy, fz) < 1e-3d) { bx = x; by = y; bz = z; }
                    else if (_clear(ref at, x, y, z, fx, fy, fz)) { bx = fx; by = fy; bz = fz; }
                    else { bx = sx; by = sy; bz = sz; }
                    continue;
                }
                var d = _get16(bytes, e + 8) * (double)field.Unit + _length(x, y, z, sx, sy, sz);
                if (!(d < best)) { continue; }
                if (_clear(ref at, x, y, z, sx, sy, sz)) { best = d; bx = sx; by = sy; bz = sz; aimCell = started; chosen = -3; }
                else if (d < hiddenBest) { hiddenBest = d; hidden = started; }
            }
            // None lower in sight, in a brick that is not convex: towards the best started cell through the brick's open cells.
            if (chosen == -1 && hidden >= 0 && here < at.Bricks) {
                var step = _stepToward(ref at, cell, hidden);
                if (step >= 0) { _centre(shape, step, out bx, out by, out bz); aimCell = step; best = hiddenBest; chosen = here; }
            }
            // Nowhere lower than here: the point is at the seed's point, behind its plane, or as near it as is in sight.
            if (chosen == -2 && _length(x, y, z, bx, by, bz) < 1e-6d) { return -1; }
            return chosen;
        }

        // What can be asked of places: during a build, or of a built field.
        private ref struct Places {
            public WallTree Tree;
            public ReadOnlySpan<byte> Region, Field;
            public TreeRegions.View View;
            public int Bricks, Cubes, KeptCount;
            // During a build, each brick's record or -1. Empty when a field is asked.
            public Span<int> KeptOf;
            // The leaves last found, by the block of four cells each way that was asked for, and each one's open cells if it is a brick.
            public Span<int> Block, Leaves, Sides;
            public Span<ulong> Masks;
            // Where a place touches others across one face, as _contacts last found them.
            public Span<Contact> Contacts;
        }

        // Where one place touches another: a cell of each, either side of a face they share.
        private struct Contact {
            public int Place, Inside, Outside;
        }

        // Whether a field's bytes belong with its tree and regions, and what they say of themselves.
        private static bool _check(in TreeField field, out TreeRegions.View view, out int bricks, out int cubes, out int kept) {
            view = default; bricks = 0; cubes = 0; kept = 0;
            var tree = field.Tree;
            if (!tree.IsValid || !(field.Unit > 0f) || float.IsInfinity(field.Unit) || !TreeRegions.TryView(tree, field.Region, out view)) { return false; }
            var bytes = field.Bytes;
            if (bytes.Length < Header) { return false; }
            bricks = _int(bytes, 0); cubes = _int(bytes, 4); kept = _int(bytes, 8);
            var started = _int(bytes, 12);
            if (bricks != view.Bricks || cubes != view.Leaves - view.Bricks || kept < 0 || started < 0 ||
                Header + 2L * (bricks + cubes) + (long)kept * Record + (long)started * StartBytes != bytes.Length) { return false; }
            return true;
        }

        private static int _list(in Places at) => Header + 2 * (at.Bricks + at.Cubes) + at.KeptCount * Record;

        // The first started cell of a place in a field, or -1.
        private static int _firstStarted(in Places at, int place) {
            var bytes = at.Field;
            int list = _list(at), low = 0, high = _int(bytes, 12) - 1, found = -1;
            while (low <= high) {
                var middle = (int)(((long)low + high) >> 1);
                var other = _int(bytes, list + middle * StartBytes);
                if (other >= place) { if (other == place) { found = middle; } high = middle - 1; } else { low = middle + 1; }
            }
            return found;
        }

        // The first started cell of a place during a build, or -1: the starts are in places' order.
        private static int _firstStart(Span<int> starts, int started, int place) {
            int low = 0, high = started - 1, found = -1;
            while (low <= high) {
                var middle = (int)(((long)low + high) >> 1);
                var other = starts[WorkPerStart * middle];
                if (other >= place) { if (other == place) { found = middle; } high = middle - 1; } else { low = middle + 1; }
            }
            return found;
        }

        // The place a cell is in, or -1 if the cell is not open or not in the grid.
        private static int _placeOf(ref Places at, int x, int y, int z) {
            var leaf = _leaf(ref at, x, y, z, out _, out var cells);
            if (leaf < 0) { return -1; }
            if (leaf >= at.Bricks) { return leaf; }
            var cell = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4);
            if (((cells >> cell) & 1UL) == 0UL) { return -1; }
            var kept = _keptOf(at, leaf);
            return kept < 0 ? leaf : at.Bricks + at.Cubes + kept * WallTrees.BrickCells + cell;
        }

        // The leaf that holds a cell, its side and, for a brick, its open cells that were reached:
        // -1 for a cell in a cube of zeros, in an open cube the regions did not reach, or not in the grid.
        private static int _leaf(ref Places at, int x, int y, int z, out int side, out ulong cells) {
            side = 0; cells = 0UL;
            var shape = at.Tree.Shape;
            if ((uint)x >= (uint)shape.CountX || (uint)y >= (uint)shape.CountY || (uint)z >= (uint)shape.CountZ) { return -1; }
            int blockX = x >> 2, blockY = y >> 2, blockZ = z >> 2;
            var slot = (blockX & 1) | ((blockY & 1) << 1) | ((blockZ & 1) << 2);
            var key = blockX + (blockY + blockZ * ((shape.CountY + 3) >> 2)) * ((shape.CountX + 3) >> 2);
            if (at.Block[slot] != key) {
                var leaf = WallTrees.LeafOf(at.Tree, x, y, z, out var found);
                var mask = 0UL;
                if (leaf >= 0 && !TreeRegions.Reached(at.Region, leaf)) { leaf = -1; }
                else if (leaf >= 0 && leaf < at.Bricks) { mask = TreeRegions.CellsOf(at.Tree, at.Region, at.View, leaf); }
                at.Block[slot] = key; at.Leaves[slot] = leaf; at.Sides[slot] = found; at.Masks[slot] = mask;
            }
            side = at.Sides[slot];
            cells = at.Masks[slot];
            return at.Leaves[slot];
        }

        // A brick's record, or -1 for a brick that is one place.
        private static int _keptOf(in Places at, int brick) {
            if (at.KeptOf.Length > 0) { return at.KeptOf[brick]; }
            if (_get16(at.Field, Header + 2 * brick) != Kept) { return -1; }
            var records = Header + 2 * (at.Bricks + at.Cubes);
            int low = 0, high = at.KeptCount - 1;
            while (low <= high) {
                var middle = (int)(((long)low + high) >> 1);
                var other = _int(at.Field, records + middle * Record);
                if (other == brick) { return middle; }
                if (other < brick) { low = middle + 1; } else { high = middle - 1; }
            }
            return -1;
        }

        // A place's count in a built field.
        private static int _count(in Places at, int place) {
            var leaves = at.Bricks + at.Cubes;
            if (place < leaves) { return _get16(at.Field, Header + 2 * place); }
            var kept = (place - leaves) >> 6;
            if (kept >= at.KeptCount) { return Unreached; }
            return _get16(at.Field, Header + 2 * leaves + kept * Record + 4 + 2 * ((place - leaves) & 63));
        }

        // A place's point, from one of its cells: an open cube's centre, the middle of a brick's open cells, or a cell's centre.
        private static void _point(ref Places at, int place, int cell, out double x, out double y, out double z) {
            var shape = at.Tree.Shape;
            double size = shape.CellSize;
            _cellOf(shape, cell, out var cx, out var cy, out var cz);
            if (place >= at.Bricks + at.Cubes) { _centre(shape, cell, out x, out y, out z); return; }
            if (place >= at.Bricks) {
                _leaf(ref at, cx, cy, cz, out var side, out _);
                int ox = cx & ~(side - 1), oy = cy & ~(side - 1), oz = cz & ~(side - 1);
                x = shape.OriginX + (ox + side * 0.5d) * size; y = shape.OriginY + (oy + side * 0.5d) * size; z = shape.OriginZ + (oz + side * 0.5d) * size;
                return;
            }
            _leaf(ref at, cx, cy, cz, out _, out var cells);
            _middle(cells, out var mx, out var my, out var mz);
            x = shape.OriginX + ((cx & ~3) + mx) * size; y = shape.OriginY + ((cy & ~3) + my) * size; z = shape.OriginZ + ((cz & ~3) + mz) * size;
        }

        // The middle of cells of a brick, in cells from its origin.
        private static void _middle(ulong cells, out double x, out double y, out double z) {
            var n = WallTrees.Count(cells);
            if (n == 0) { x = y = z = 2d; return; }
            // Each cell counted by its layer along each axis: the cells in the second layer once, the third twice, the fourth three times.
            var sx = WallTrees.Count(cells & 0x2222222222222222UL) + 2 * WallTrees.Count(cells & 0x4444444444444444UL) + 3 * WallTrees.Count(cells & 0x8888888888888888UL);
            var sy = WallTrees.Count(cells & 0x00F000F000F000F0UL) + 2 * WallTrees.Count(cells & 0x0F000F000F000F00UL) + 3 * WallTrees.Count(cells & 0xF000F000F000F000UL);
            var sz = WallTrees.Count(cells & 0x00000000FFFF0000UL) + 2 * WallTrees.Count(cells & 0x0000FFFF00000000UL) + 3 * WallTrees.Count(cells & 0xFFFF000000000000UL);
            x = (double)sx / n + 0.5d; y = (double)sy / n + 0.5d; z = (double)sz / n + 0.5d;
        }

        // Where a place touches others across one of its faces, written to contacts; how many.
        private static int _contacts(ref Places at, int place, int cell, int face) {
            var contacts = at.Contacts;
            var shape = at.Tree.Shape;
            _cellOf(shape, cell, out var cx, out var cy, out var cz);
            var count = 0;
            var leaves = at.Bricks + at.Cubes;
            if (place >= leaves) {
                // One cell of a brick kept a cell at a time: the cell beside it.
                var oneAxis = face >> 1;
                var step = (face & 1) == 0 ? -1 : 1;
                int nx = cx + (oneAxis == 0 ? step : 0), ny = cy + (oneAxis == 1 ? step : 0), nz = cz + (oneAxis == 2 ? step : 0);
                var other = _placeOf(ref at, nx, ny, nz);
                if (other < 0 || other == place) { return 0; }
                contacts[count++] = new Contact { Place = other, Inside = cell, Outside = shape.Index(nx, ny, nz) };
                return count;
            }
            var brick = place < at.Bricks;
            _leaf(ref at, cx, cy, cz, out var side, out var own);
            int ox = cx & ~(side - 1), oy = cy & ~(side - 1), oz = cz & ~(side - 1);
            if (brick) { side = 4; }
            {
                var axis = face >> 1;
                var low = (face & 1) == 0;
                var origin = axis == 0 ? ox : axis == 1 ? oy : oz;
                int ownLayer = low ? origin : origin + side - 1, across = low ? origin - 1 : origin + side;
                if ((uint)across >= (uint)(axis == 0 ? shape.CountX : axis == 1 ? shape.CountY : shape.CountZ)) { return 0; }
                int originU = axis == 0 ? oy : ox, originV = axis == 2 ? oy : oz;
                // The place's own cells on this face: all of a cube's, and a brick's open ones.
                var pattern = brick ? WallTrees.Layer(own, axis, low ? 0 : 3) : 0xFFFF;
                if (pattern == 0) { return 0; }
                for (var v = originV; v < originV + side; v += 4) {
                    for (var u = originU; u < originU + side; u += 4) {
                        var leaf = _leaf(ref at, _x(axis, across, u, v), _y(axis, across, u, v), _z(axis, across, u, v), out var otherSide, out var cells);
                        if (leaf < 0) { continue; }
                        if (leaf >= at.Bricks) {
                            // An open cube: once for the part of the face the two share, at the cell of it nearest the cube's centre.
                            int fromU = u & ~(otherSide - 1), fromV = v & ~(otherSide - 1), toU = fromU + otherSide, toV = fromV + otherSide;
                            var middleU = fromU + otherSide / 2; var middleV = fromV + otherSide / 2;
                            if (fromU < originU) { fromU = originU; }
                            if (fromV < originV) { fromV = originV; }
                            if (toU > originU + side) { toU = originU + side; }
                            if (toV > originV + side) { toV = originV + side; }
                            if (!brick && (u != (fromU & ~3) || v != (fromV & ~3))) { continue; }
                            int bu, bv;
                            if (brick) { _nearestBit(pattern, middleU - u, middleV - v, out bu, out bv); bu += u; bv += v; }
                            else { bu = Math.Max(fromU, Math.Min(toU - 1, middleU)); bv = Math.Max(fromV, Math.Min(toV - 1, middleV)); }
                            contacts[count++] = new Contact {
                                Place = leaf, Inside = shape.Index(_x(axis, ownLayer, bu, bv), _y(axis, ownLayer, bu, bv), _z(axis, ownLayer, bu, bv)),
                                Outside = shape.Index(_x(axis, across, bu, bv), _y(axis, across, bu, bv), _z(axis, across, bu, bv)),
                            };
                            continue;
                        }
                        // A brick: its open cells on the near side that touch this place's.
                        var near = WallTrees.Layer(cells, axis, low ? 3 : 0) & pattern;
                        if (near == 0) { continue; }
                        var kept = _keptOf(at, leaf);
                        if (kept < 0) {
                            _middle(cells, out var mx, out var my, out var mz);
                            var mu = axis == 0 ? my : mx; var mv = axis == 2 ? my : mz;
                            _nearestBit(near, (int)Math.Floor(mu), (int)Math.Floor(mv), out var nu, out var nv);
                            int pu = u + nu, pv = v + nv;
                            contacts[count++] = new Contact {
                                Place = leaf, Inside = shape.Index(_x(axis, ownLayer, pu, pv), _y(axis, ownLayer, pu, pv), _z(axis, ownLayer, pu, pv)),
                                Outside = shape.Index(_x(axis, across, pu, pv), _y(axis, across, pu, pv), _z(axis, across, pu, pv)),
                            };
                            continue;
                        }
                        for (var left = near; left != 0; left &= left - 1) {
                            var k = _lowest((ulong)left);
                            int pu = u + (k & 3), pv = v + (k >> 2);
                            int tx = _x(axis, across, pu, pv), ty = _y(axis, across, pu, pv), tz = _z(axis, across, pu, pv);
                            contacts[count++] = new Contact {
                                Place = leaves + kept * WallTrees.BrickCells + ((tx & 3) | ((ty & 3) << 2) | ((tz & 3) << 4)),
                                Inside = shape.Index(_x(axis, ownLayer, pu, pv), _y(axis, ownLayer, pu, pv), _z(axis, ownLayer, pu, pv)),
                                Outside = shape.Index(tx, ty, tz),
                            };
                        }
                    }
                }
            }
            return count;
        }

        // Through a brick's open cells, from one to another: the cell beside the first on the shortest way by faces, or -1.
        private static int _stepToward(ref Places at, int from, int to) {
            var shape = at.Tree.Shape;
            _cellOf(shape, from, out var fx, out var fy, out var fz);
            _cellOf(shape, to, out var tx, out var ty, out var tz);
            _leaf(ref at, fx, fy, fz, out _, out var cells);
            int ox = fx & ~3, oy = fy & ~3, oz = fz & ~3;
            int start = (fx & 3) | ((fy & 3) << 2) | ((fz & 3) << 4), goal = (tx & 3) | ((ty & 3) << 2) | ((tz & 3) << 4);
            if (start == goal) { return -1; }
            // Outward from the goal, a ring of cells at a time, until the start is in a ring: then the start's neighbour in the ring before.
            ulong reached = 1UL << goal, ring = reached;
            Span<ulong> rings = stackalloc ulong[WallTrees.BrickCells];
            var count = 0;
            rings[count++] = ring;
            while ((reached & (1UL << start)) == 0UL) {
                // One face step at a time: what touches the ring by a face.
                var grown = _faces(ring) & cells & ~reached;
                if (grown == 0UL) { return -1; }
                reached |= grown; ring = grown; rings[count++] = ring;
            }
            var before = count >= 2 ? rings[count - 2] : 0UL;
            var next = _faces(1UL << start) & before;
            if (next == 0UL) { return -1; }
            var k = _lowest(next);
            return shape.Index(ox + (k & 3), oy + ((k >> 2) & 3), oz + (k >> 4));
        }

        // The cells of a brick that share a face with some of the cells given.
        private static ulong _faces(ulong cells) =>
            ((cells << 1) & ~0x1111111111111111UL) | ((cells >> 1) & ~0x8888888888888888UL) | ((cells << 4) & ~0x000F000F000F000FUL) | ((cells >> 4) & ~0xF000F000F000F000UL) | (cells << 16) | (cells >> 16);

        // Of a pattern of sixteen cells, the one nearest a cell of it, as u and v.
        private static void _nearestBit(int pattern, int u, int v, out int bestU, out int bestV) {
            bestU = 0; bestV = 0;
            var least = int.MaxValue;
            for (var k = 0; k < 16; k++) {
                if (((pattern >> k) & 1) == 0) { continue; }
                int du = (k & 3) - u, dv = (k >> 2) - v;
                var d = du * du + dv * dv;
                if (d < least) { least = d; bestU = k & 3; bestV = k >> 2; }
            }
        }

        // Whether every cell the segment between two points touches is open: walked from cell to
        // cell, and where it passes through an edge or a corner, the cells beside that too.
        private static bool _clear(ref Places at, double ax, double ay, double az, double bx, double by, double bz) {
            var shape = at.Tree.Shape;
            double size = shape.CellSize;
            double fx = (ax - shape.OriginX) / size, fy = (ay - shape.OriginY) / size, fz = (az - shape.OriginZ) / size;
            double tx = (bx - shape.OriginX) / size, ty = (by - shape.OriginY) / size, tz = (bz - shape.OriginZ) / size;
            int x = (int)Math.Floor(fx), y = (int)Math.Floor(fy), z = (int)Math.Floor(fz);
            int ex = (int)Math.Floor(tx), ey = (int)Math.Floor(ty), ez = (int)Math.Floor(tz);
            double dx = tx - fx, dy = ty - fy, dz = tz - fz;
            int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
            double nx = sx != 0 ? Math.Abs(1d / dx) : double.PositiveInfinity, ny = sy != 0 ? Math.Abs(1d / dy) : double.PositiveInfinity, nz = sz != 0 ? Math.Abs(1d / dz) : double.PositiveInfinity;
            double mx = sx > 0 ? (x + 1 - fx) * nx : sx < 0 ? (fx - x) * nx : double.PositiveInfinity;
            double my = sy > 0 ? (y + 1 - fy) * ny : sy < 0 ? (fy - y) * ny : double.PositiveInfinity;
            double mz = sz > 0 ? (z + 1 - fz) * nz : sz < 0 ? (fz - z) * nz : double.PositiveInfinity;
            var cells = Math.Abs(ex - x) + Math.Abs(ey - y) + Math.Abs(ez - z) + 1;
            for (var guard = 0; guard <= cells; guard++) {
                var place = _placeOf(ref at, x, y, z);
                if (place < 0) { return false; }
                if (x == ex && y == ey && z == ez) { return true; }
                // Across an open cube at once: every cell of it is open. The walk starts again just inside where the line leaves it.
                if (place >= at.Bricks && place < at.Bricks + at.Cubes) {
                    _leaf(ref at, x, y, z, out var side, out _);
                    if (side >= 8) {
                        int ox = x & ~(side - 1), oy = y & ~(side - 1), oz = z & ~(side - 1);
                        var leave = Math.Min(sx > 0 ? (ox + side - fx) / dx : sx < 0 ? (ox - fx) / dx : double.PositiveInfinity,
                            Math.Min(sy > 0 ? (oy + side - fy) / dy : sy < 0 ? (oy - fy) / dy : double.PositiveInfinity,
                                sz > 0 ? (oz + side - fz) / dz : sz < 0 ? (oz - fz) / dz : double.PositiveInfinity));
                        if (leave >= 1d) { return true; }
                        var along = leave - 1e-9d;
                        double px = fx + dx * along, py = fy + dy * along, pz = fz + dz * along;
                        int jx = (int)Math.Floor(px), jy = (int)Math.Floor(py), jz = (int)Math.Floor(pz);
                        if (jx >= ox && jx < ox + side && jy >= oy && jy < oy + side && jz >= oz && jz < oz + side && along > Math.Min(mx, Math.Min(my, mz)) - 1d) {
                            x = jx; y = jy; z = jz;
                            mx = sx > 0 ? along + (x + 1 - px) * nx : sx < 0 ? along + (px - x) * nx : double.PositiveInfinity;
                            my = sy > 0 ? along + (y + 1 - py) * ny : sy < 0 ? along + (py - y) * ny : double.PositiveInfinity;
                            mz = sz > 0 ? along + (z + 1 - pz) * nz : sz < 0 ? along + (pz - z) * nz : double.PositiveInfinity;
                        }
                    }
                }
                var least = Math.Min(mx, Math.Min(my, mz));
                if (least > 1d) { return true; }
                // Crossings this near each other are taken as one, through an edge or a corner, so that a line that only grazes one is held to the cells beside it.
                bool stepX = mx - least < Graze, stepY = my - least < Graze, stepZ = mz - least < Graze;
                if ((stepX ? 1 : 0) + (stepY ? 1 : 0) + (stepZ ? 1 : 0) > 1) {
                    if (stepX && _placeOf(ref at, x + sx, y, z) < 0) { return false; }
                    if (stepY && _placeOf(ref at, x, y + sy, z) < 0) { return false; }
                    if (stepZ && _placeOf(ref at, x, y, z + sz) < 0) { return false; }
                    // Through a corner: the cells that share only an edge with this one too.
                    if (stepX && stepY && stepZ && (_placeOf(ref at, x + sx, y + sy, z) < 0 || _placeOf(ref at, x + sx, y, z + sz) < 0 || _placeOf(ref at, x, y + sy, z + sz) < 0)) { return false; }
                }
                if (stepX) { x += sx; mx += nx; }
                if (stepY) { y += sy; my += ny; }
                if (stepZ) { z += sz; mz += nz; }
            }
            return false;
        }

        // The cells a seed starts, as a dense field's do: open, centre inside the solid, with a
        // clear way to the point or square to the plane. Each is written to starts (if it has
        // room) as its place (worked out later), its cell, its start's bits and the seed. How many.
        private static int _started(in WallTree tree, ReadOnlySpan<byte> region, in TreeRegions.View view, in TravelSeed seed, int index, Span<int> starts) {
            if (!ShapeDistance.TryBox(seed.Solid, out var box)) { return 0; }
            var shape = tree.Shape;
            double size = shape.CellSize;
            int fromX = _first(box.MinX, shape.OriginX, size, shape.CountX), toX = _last(box.MaxX, shape.OriginX, size, shape.CountX);
            int fromY = _first(box.MinY, shape.OriginY, size, shape.CountY), toY = _last(box.MaxY, shape.OriginY, size, shape.CountY);
            int fromZ = _first(box.MinZ, shape.OriginZ, size, shape.CountZ), toZ = _last(box.MaxZ, shape.OriginZ, size, shape.CountZ);
            var count = 0;
            for (var z = fromZ; z <= toZ; z++) {
                for (var y = fromY; y <= toY; y++) {
                    for (var x = fromX; x <= toX; x++) {
                        if (TreeRegions.Number(tree, region, view, x, y, z) == 0) { continue; }
                        double cx = shape.Centre(shape.OriginX, x), cy = shape.Centre(shape.OriginY, y), cz = shape.Centre(shape.OriginZ, z);
                        if (ShapeDistance.SolidSquared(seed.Solid, cx, cy, cz) != 0d) { continue; }
                        if (!_clearWay(tree, region, view, seed, cx, cy, cz)) { continue; }
                        if (starts.Length >= WorkPerStart * (count + 1)) {
                            starts[WorkPerStart * count] = -1;
                            starts[WorkPerStart * count + 1] = shape.Index(x, y, z);
                            starts[WorkPerStart * count + 2] = _fixed(_start(seed, (float)cx, (float)cy, (float)cz));
                            starts[WorkPerStart * count + 3] = index;
                        }
                        count++;
                    }
                }
            }
            return count;
        }

        // As a dense field's: whether the straight way from a centre to a seed's point, or square
        // to its plane, lies through open cells, looked at every quarter of a cell. Before a point
        // the last cell's length is let off; before a plane, the last quarter of a cell.
        private static bool _clearWay(in WallTree tree, ReadOnlySpan<byte> region, in TreeRegions.View view, in TravelSeed seed, double x, double y, double z) {
            var shape = tree.Shape;
            double size = shape.CellSize, toX = seed.X - x, toY = seed.Y - y, toZ = seed.Z - z, letOff = size;
            if (seed.Start == TravelStart.FromPlane) {
                double nx = seed.NormalX, ny = seed.NormalY, nz = seed.NormalZ;
                var ahead = -(toX * nx + toY * ny + toZ * nz) / (nx * nx + ny * ny + nz * nz);
                if (!(ahead > 0d)) { return true; }
                toX = -ahead * nx; toY = -ahead * ny; toZ = -ahead * nz;
                letOff = 0.25d * size;
            }
            var length = Math.Sqrt(toX * toX + toY * toY + toZ * toZ);
            var step = 0.25d * size;
            var steps = Math.Floor((length - letOff) / step);
            for (var k = 1; k <= steps; k++) {
                var along = k * step / length;
                double atX = Math.Floor((x + toX * along - shape.OriginX) / size), atY = Math.Floor((y + toY * along - shape.OriginY) / size), atZ = Math.Floor((z + toZ * along - shape.OriginZ) / size);
                if (!(atX >= 0d && atX < shape.CountX && atY >= 0d && atY < shape.CountY && atZ >= 0d && atZ < shape.CountZ)) { return false; }
                if (TreeRegions.Number(tree, region, view, (int)atX, (int)atY, (int)atZ) == 0) { return false; }
            }
            return true;
        }

        private static bool _usable(in TravelSeed seed) {
            var sum = (double)seed.X + seed.Y + seed.Z;
            if (!(sum - sum == 0d)) { return false; }
            if (seed.Start == TravelStart.FromPoint) { return ShapeDistance.TryBox(seed.Solid, out _); }
            if (seed.Start != TravelStart.FromPlane) { return false; }
            var length = (double)seed.NormalX * seed.NormalX + (double)seed.NormalY * seed.NormalY + (double)seed.NormalZ * seed.NormalZ;
            return length > 0d && length - length == 0d && ShapeDistance.TryBox(seed.Solid, out _);
        }

        // What a point inside a seed starts at: its distance from the point, or how far it is in front of the plane.
        private static float _start(in TravelSeed seed, float x, float y, float z) {
            double dx = (double)x - seed.X, dy = (double)y - seed.Y, dz = (double)z - seed.Z;
            if (seed.Start == TravelStart.FromPoint) { return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
            double nx = seed.NormalX, ny = seed.NormalY, nz = seed.NormalZ;
            var ahead = (dx * nx + dy * ny + dz * nz) / Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return ahead > 0d ? (float)ahead : 0f;
        }

        // Moves the end of the line from a to b back to the farthest point along it that is clear from a, to within a 65,536th of its length.
        private static void _farthest(ref Places at, double ax, double ay, double az, ref double bx, ref double by, ref double bz) {
            double low = 0d, high = 1d;
            for (var i = 0; i < 16; i++) {
                var mid = (low + high) * 0.5d;
                if (_clear(ref at, ax, ay, az, ax + (bx - ax) * mid, ay + (by - ay) * mid, az + (bz - az) * mid)) { low = mid; } else { high = mid; }
            }
            bx = ax + (bx - ax) * low; by = ay + (by - ay) * low; bz = az + (bz - az) * low;
        }

        // Where a point in a started cell flies to: the seed's point, or 5 cm behind the plane, square to it.
        private static void _into(in TravelSeed seed, double x, double y, double z, out double tx, out double ty, out double tz) {
            if (seed.Start == TravelStart.FromPoint) { tx = seed.X; ty = seed.Y; tz = seed.Z; return; }
            double nx = seed.NormalX, ny = seed.NormalY, nz = seed.NormalZ, length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            nx /= length; ny /= length; nz /= length;
            var back = (x - seed.X) * nx + (y - seed.Y) * ny + (z - seed.Z) * nz + 0.05d;
            tx = x - nx * back; ty = y - ny * back; tz = z - nz * back;
        }

        // Whether a brick's open cells are one place: joined inside it, and plain.
        private static bool _plain(ulong cells) => WallTrees.Joined(cells & (~cells + 1UL), cells) == cells && WallTrees.Plain(cells);

        // The heap of places, least value first; ties go to the lower place. heapAt is a place's
        // position plus one, 0 for one not in it, and -1 for one taken out.
        private static bool _before(Span<float> value, int a, int b) => value[a] < value[b] || (value[a] == value[b] && a < b);

        private static int _lower(Span<float> value, Span<int> heap, Span<int> heapAt, int held, int place) {
            var at = heapAt[place] > 0 ? heapAt[place] - 1 : held++;
            while (at > 0) {
                var above = (at - 1) >> 1;
                if (!_before(value, place, heap[above])) { break; }
                heap[at] = heap[above];
                heapAt[heap[at]] = at + 1;
                at = above;
            }
            heap[at] = place;
            heapAt[place] = at + 1;
            return held;
        }

        private static int _pop(Span<float> value, Span<int> heap, Span<int> heapAt, ref int held) {
            var top = heap[0];
            held--;
            if (held > 0) {
                var moved = heap[held];
                var at = 0;
                while (true) {
                    var below = 2 * at + 1;
                    if (below >= held) { break; }
                    if (below + 1 < held && _before(value, heap[below + 1], heap[below])) { below++; }
                    if (!_before(value, heap[below], moved)) { break; }
                    heap[at] = heap[below];
                    heapAt[heap[at]] = at + 1;
                    at = below;
                }
                heap[at] = moved;
                heapAt[moved] = at + 1;
            }
            heapAt[top] = -1;
            return top;
        }

        // The started cells in the order of their places, then their cells: a heap sort of records of four ints.
        private static void _sortStarts(Span<int> starts, int count) {
            for (var at = count / 2 - 1; at >= 0; at--) { _sink(starts, at, count); }
            for (var end = count - 1; end > 0; end--) {
                _swap(starts, 0, end);
                _sink(starts, 0, end);
            }
        }

        private static bool _later(Span<int> starts, int a, int b) {
            int pa = starts[WorkPerStart * a], pb = starts[WorkPerStart * b];
            return pa > pb || (pa == pb && starts[WorkPerStart * a + 1] > starts[WorkPerStart * b + 1]);
        }

        private static void _sink(Span<int> starts, int at, int count) {
            while (true) {
                var below = 2 * at + 1;
                if (below >= count) { return; }
                if (below + 1 < count && _later(starts, below + 1, below)) { below++; }
                if (!_later(starts, below, at)) { return; }
                _swap(starts, at, below);
                at = below;
            }
        }

        private static void _swap(Span<int> starts, int a, int b) {
            for (var k = 0; k < WorkPerStart; k++) {
                var held = starts[WorkPerStart * a + k];
                starts[WorkPerStart * a + k] = starts[WorkPerStart * b + k];
                starts[WorkPerStart * b + k] = held;
            }
        }

        private static void _cellOf(in GridShape shape, int cell, out int x, out int y, out int z) {
            x = cell % shape.CountX; y = cell / shape.CountX % shape.CountY; z = cell / shape.LayerLength;
        }

        private static void _centre(in GridShape shape, int cell, out double x, out double y, out double z) {
            _cellOf(shape, cell, out var cx, out var cy, out var cz);
            x = shape.Centre(shape.OriginX, cx); y = shape.Centre(shape.OriginY, cy); z = shape.Centre(shape.OriginZ, cz);
        }

        // A start in metres as an int of 1/1024 m, and back: no float is kept in the work's ints.
        private static int _fixed(float metres) => (int)Math.Floor(metres * 1024d + 0.5d);
        private static double _unfixed(int fixedMetres) => fixedMetres / 1024d;

        private static double _length(double ax, double ay, double az, double bx, double by, double bz) {
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // The cell at a place along an axis and at u and v along the other two.
        private static int _x(int axis, int along, int u, int v) => axis == 0 ? along : u;
        private static int _y(int axis, int along, int u, int v) => axis == 1 ? along : axis == 0 ? u : v;
        private static int _z(int axis, int along, int u, int v) => axis == 2 ? along : v;

        private static int _lowest(ulong cells) => WallTrees.Count((cells & (~cells + 1UL)) - 1UL);

        // Ints and 16-bit counts as bytes, the lowest first, wherever they are read or written.
        private static int _int(ReadOnlySpan<byte> bytes, int at) => bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);

        private static int _get16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | (bytes[at + 1] << 8);

        private static void _put(Span<byte> bytes, int at, int value) {
            bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); bytes[at + 2] = (byte)(value >> 16); bytes[at + 3] = (byte)(value >> 24);
        }

        private static void _put16(Span<byte> bytes, int at, int value) {
            bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8);
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
