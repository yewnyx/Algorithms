using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    /// <summary>
    /// One grid over geometry that does not move: the shapes nothing may pass through, each cell's
    /// distance to the nearest of them, and a cost per cell that the caller writes. It answers how
    /// much room there is at a point.
    /// </summary>
    /// <remarks>
    /// Everything is in the grid's own frame; it holds no transform. Used from the main thread.
    /// The shapes are copied into arrays the grid owns, so nothing it reads can be unloaded under a
    /// job. <see cref="ScheduleMark"/> fills the wall distance in jobs; a member that reads the
    /// wall distance waits for those jobs first, and for nothing else. Only what writes or frees
    /// the grid's arrays waits for a caller's own readers (see <see cref="AddReader"/>).
    /// The wall distance only ever claims room that the shapes it holds allow: it is 0 everywhere
    /// until a marking is done, 0 again from the moment new shapes are taken, and 0 in any layer
    /// whose marking was refused.
    /// <para>
    /// For each size of body added with <see cref="AddSizeClass"/> the grid also holds regions:
    /// which cells a body of that size can be in, and which of them join.
    /// <see cref="ScheduleRegions"/> floods them from seeds. They follow the wall distance and the
    /// closed cells, so they too say nothing until they are flooded, and nothing again from the
    /// moment new shapes are taken or a cost is changed.
    /// </para>
    /// <para>
    /// A grid of more than <see cref="DenseMost"/> cells keeps its wall distance as a tree and not
    /// as a count for every cell (see <see cref="WallTrees"/>): for a part that is mostly open air,
    /// where a count a cell is tens of megabytes. The tree keeps each count to a fifteenth of the
    /// cap, so <see cref="TryRoom"/> and <see cref="HasRoom"/> can prove a little less room than
    /// counts would, never more; <see cref="TryDistance"/> answers the same either way. Costs, size classes, regions, fields
    /// and routes are for grids that keep a count for every cell: on a tree
    /// <see cref="AddSizeClass"/> gives -1 and <see cref="FillCost"/> false.
    /// </para>
    /// <para>
    /// After <see cref="Dispose"/>, every member that reads or changes the grid throws
    /// <see cref="ObjectDisposedException"/>. <see cref="Complete"/> and the plain values
    /// (<see cref="Shape"/>, <see cref="Unit"/>, <see cref="Cap"/>, <see cref="IsDisposed"/>) are still safe.
    /// </para>
    /// </remarks>
    public sealed class WallGrid : IDisposable {
        private readonly Allocator _allocator;
        private bool _disposed;

        // The marking last scheduled, and the jobs scheduled since that read what it wrote.
        private JobHandle _mark, _readers;

        private NativeArray<byte> _wall, _cost, _filled;
        // The wall distance of a grid that keeps it as a tree. Empty until it is built, and in a grid that keeps a count for every cell.
        private NativeList<WallNode> _treeNodes;
        private NativeList<byte> _treeBricks;
        private NativeArray<Solid> _solids;
        private NativeArray<float> _corners;
        private NativeArray<int> _triangles, _order;
        private NativeArray<HeightField> _heightFields;
        private NativeArray<uint> _heightBlocks;
        private NativeArray<ushort> _heights;
        private NativeArray<Box> _boxes;
        private NativeArray<BoxNode> _nodes;

        // One size of body: its class, a region number per cell, and what the last flood came to
        // (done or not, cells reached, seeds with no open cell).
        private sealed class Sized {
            public SizeClass Size;
            public NativeArray<byte> Region;
            public NativeArray<int> Result;
            public JobHandle Flood;
            // Goes up each time the regions are forgotten or flooded again, so that a field built
            // from them can tell whether they are still the ones it was built from.
            public int Stamp;
        }

        private readonly List<Sized> _sizes = new List<Sized>();

        /// <param name="shape">Where the grid sits in its own frame.</param>
        /// <param name="unit">Metres per count of wall distance.</param>
        /// <param name="cap">The largest count stored: no query can prove more room than cap * unit.</param>
        /// <param name="allocator">How the grid's arrays are allocated.</param>
        public WallGrid(GridShape shape, float unit = 1f / 128f, byte cap = 128, Allocator allocator = Allocator.Persistent)
            : this(shape, unit, cap, allocator, shape.CellCount > DenseMost) { }

        // With the storage said outright, for tests of a tree over a grid small enough to mark both ways.
        internal WallGrid(GridShape shape, float unit, byte cap, Allocator allocator, bool tree) {
            if (shape.CellCount == 0) { throw new ArgumentException("The shape cannot be used: see GridShape.CellCount.", nameof(shape)); }
            if (!(unit > 0f) || float.IsInfinity(unit)) { throw new ArgumentOutOfRangeException(nameof(unit), unit, "Must be above 0."); }
            if (cap == 0) { throw new ArgumentOutOfRangeException(nameof(cap), cap, "Must be above 0."); }
            Shape = shape;
            Unit = unit;
            Cap = cap;
            _allocator = allocator;
            IsTree = tree;
            // Until it is marked, the wall distance is 0 everywhere: nothing is proven.
            _wall = new NativeArray<byte>(tree ? 0 : shape.CellCount, allocator);
            _treeNodes = new NativeList<WallNode>(1, allocator);
            _treeBricks = new NativeList<byte>(1, allocator);
            // No byte of cost is held until one is written: an empty array means every cell is normal.
            _cost = new NativeArray<byte>(0, allocator);
            // One element a layer, or one for the tree.
            _filled = new NativeArray<byte>(tree ? 1 : shape.CountZ, allocator);
            _allocateShapes(0, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>Where the grid sits in its own frame.</summary>
        public GridShape Shape { get; }

        /// <summary>Metres per count of wall distance.</summary>
        public float Unit { get; }

        /// <summary>The largest count of wall distance stored.</summary>
        public byte Cap { get; }

        /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>The most cells a grid keeps a count for each of: 4 MB of wall distance. A grid of more keeps a tree.</summary>
        public const int DenseMost = 1 << 22;

        /// <summary>Whether the grid keeps its wall distance as a tree, which is so above <see cref="DenseMost"/> cells.</summary>
        public bool IsTree { get; }

        /// <summary>The number of shapes held.</summary>
        public int ShapeCount { get { _requireAlive(); return _boxes.Length; } }

        /// <summary>
        /// The bytes the grid's arrays take, for a caller that keeps grids within a budget: one a
        /// cell for the wall distance or the tree's nodes and bricks, one a cell for each size class,
        /// one a cell for the costs once a cost has been written, and the shapes, heightfields among them. Not counted: while a flood of regions runs it
        /// also holds a queue of 4 bytes a cell, which is freed when the flood is done.
        /// </summary>
        /// <remarks>
        /// On a grid that keeps a tree this waits for a build that is running, which can be
        /// seconds: what the tree comes to is not known before. A grid that keeps a count for
        /// every cell answers at once.
        /// </remarks>
        public long ByteCount {
            get {
                _requireAlive();
                if (IsTree) { _mark.Complete(); }
                return (long)_wall.Length + (long)_treeNodes.Length * 24 + _treeBricks.Length + _cost.Length + _filled.Length + (long)_solids.Length * 64 + (long)_corners.Length * 4 +
                       (long)_triangles.Length * 4 + (long)_heightFields.Length * 92 + (long)_heightBlocks.Length * 4 + (long)_heights.Length * 2 +
                       (long)_order.Length * 4 + (long)_boxes.Length * 24 + (long)_nodes.Length * 32 +
                       (long)_sizes.Count * (_wall.Length + 16);
            }
        }

        /// <summary>
        /// Takes the shapes from a block that <see cref="Shapes.Write"/> made, cut to its length, and
        /// copies them. The block is not needed afterwards. Returns false, keeping the shapes it had,
        /// if the block is refused (see <see cref="Shapes.TryRead"/>).
        /// </summary>
        /// <remarks>
        /// Once the new shapes are taken, the wall distance says nothing of them: it is set to 0 and
        /// <see cref="IsMarked"/> is false until the next <see cref="ScheduleMark"/> is done.
        /// </remarks>
        public bool SetShapes(ReadOnlySpan<byte> block) {
            _settle();
            if (!Shapes.TryRead(block, out var shapes)) { return false; }
            _disposeShapes();
            _allocateShapes(shapes.Solids.Length, shapes.Corners.Length, shapes.Triangles.Length,
                shapes.HeightFields.Length, shapes.HeightBlocks.Length, shapes.Heights.Length, shapes.Nodes.Length);
            shapes.Solids.CopyTo(_solids.AsSpan());
            shapes.Corners.CopyTo(_corners.AsSpan());
            shapes.Triangles.CopyTo(_triangles.AsSpan());
            shapes.HeightFields.CopyTo(_heightFields.AsSpan());
            shapes.HeightBlocks.CopyTo(_heightBlocks.AsSpan());
            shapes.Heights.CopyTo(_heights.AsSpan());
            shapes.Boxes.CopyTo(_boxes.AsSpan());
            shapes.Nodes.CopyTo(_nodes.AsSpan());
            shapes.Order.CopyTo(_order.AsSpan());
            _forget();
            return true;
        }

        /// <summary>
        /// Takes the shapes as they are given: solids, corner coordinates in threes, and three corner
        /// numbers per triangle. Returns false, keeping the shapes it had, if a shape may not be
        /// stored (see <see cref="Shapes.Build"/>).
        /// </summary>
        /// <remarks>
        /// Once the new shapes are taken, the wall distance says nothing of them: it is set to 0 and
        /// <see cref="IsMarked"/> is false until the next <see cref="ScheduleMark"/> is done.
        /// </remarks>
        public bool SetShapes(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles) =>
            _setShapes(solids, corners, triangles, default, default, default);

        /// <summary>
        /// Takes the shapes as <see cref="ColliderShapes.Collect(IReadOnlyList{UnityEngine.Collider}, UnityEngine.Matrix4x4, in Box, CollectedShapes)"/>
        /// collected them, heightfields among them, and copies them. Returns false, keeping the
        /// shapes it had, if a shape may not be stored.
        /// </summary>
        /// <remarks>As with the other overloads, the wall distance says nothing until the next marking is done.</remarks>
        public bool SetShapes(CollectedShapes shapes) {
            if (shapes == null) { throw new ArgumentNullException(nameof(shapes)); }
            return _setShapes(shapes.Solids.ToArray(), shapes.Corners.ToArray(), shapes.Triangles.ToArray(),
                shapes.HeightFields.ToArray(), shapes.HeightBlocks.ToArray(), shapes.Heights.ToArray());
        }

        private bool _setShapes(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles,
            ReadOnlySpan<HeightField> heightFields, ReadOnlySpan<uint> heightBlocks, ReadOnlySpan<ushort> heights) {
            _settle();
            if (corners.Length % 3 != 0 || triangles.Length % 3 != 0) { return false; }
            var total = (long)solids.Length + triangles.Length / 3 + heightFields.Length;
            if (total > int.MaxValue / 4) { return false; }
            var count = (int)total;
            var boxes = new NativeArray<Box>(count, Allocator.Temp);
            var nodes = new NativeArray<BoxNode>(Shapes.NodesLength(count), Allocator.Temp);
            var order = new NativeArray<int>(count, Allocator.Temp);
            var workspace = new NativeArray<int>(Shapes.WorkspaceLength(count), Allocator.Temp);
            var built = Shapes.Build(solids, corners, triangles, heightFields, heightBlocks, heights, boxes.AsSpan(), nodes.AsSpan(), order.AsSpan(), workspace.AsSpan(), out var nodeCount);
            if (built) {
                _disposeShapes();
                _allocateShapes(solids.Length, corners.Length, triangles.Length, heightFields.Length, heightBlocks.Length, heights.Length, nodeCount);
                solids.CopyTo(_solids.AsSpan());
                corners.CopyTo(_corners.AsSpan());
                triangles.CopyTo(_triangles.AsSpan());
                heightFields.CopyTo(_heightFields.AsSpan());
                heightBlocks.CopyTo(_heightBlocks.AsSpan());
                heights.CopyTo(_heights.AsSpan());
                boxes.AsSpan().CopyTo(_boxes.AsSpan());
                nodes.AsSpan().Slice(0, nodeCount).CopyTo(_nodes.AsSpan());
                order.AsSpan().CopyTo(_order.AsSpan());
                _forget();
            }
            boxes.Dispose(); nodes.Dispose(); order.Dispose(); workspace.Dispose();
            return built;
        }

        /// <summary>
        /// Schedules the jobs that fill the wall distance from the shapes, one z layer per index, side
        /// by side. Returns their handle.
        /// </summary>
        /// <remarks>
        /// They run after the jobs scheduled so far that read the grid. <see cref="IsMarked"/> says
        /// afterwards whether every layer was filled. A layer that is refused, because the shapes
        /// are found damaged, is left at 0.
        /// A grid that keeps a tree builds it in one job. That takes seconds for a part of some
        /// hundreds of metres: build it once where the part is baked, keep what
        /// <see cref="TryWriteTree"/> gives, and hand it to <see cref="SetTree"/> when the part loads.
        /// </remarks>
        public JobHandle ScheduleMark(JobHandle dependsOn = default) {
            _requireAlive();
            var after = JobHandle.CombineDependencies(dependsOn, _mark, _readers);
            _mark = IsTree
                ? new TreeJob { Shape = Shape, Shapes = _shapes(), Unit = Unit, Cap = Cap, Nodes = _treeNodes, Bricks = _treeBricks, Filled = _filled }.Schedule(after)
                : new MarkJob { Shape = Shape, Shapes = _shapes(), Unit = Unit, Cap = Cap, Wall = _wall, Filled = _filled }.Schedule(Shape.CountZ, 1, after);
            _readers = default;
            return _mark;
        }

        /// <summary>
        /// The tree of a grid that keeps one, as a block of bytes to store (see
        /// <see cref="WallTrees.Write"/>). Returns false if the grid keeps a count for every cell,
        /// or its wall distance is not that of the shapes held (see <see cref="IsMarked"/>).
        /// </summary>
        /// <remarks>The block keeps a fingerprint of the shapes the grid holds, which <see cref="SetTree"/> checks.</remarks>
        public bool TryWriteTree(out byte[] block) {
            block = Array.Empty<byte>();
            if (!IsTree || !IsMarked) { return false; }
            var bytes = new byte[WallTrees.BlockLength(_treeNodes.Length, _treeBricks.Length / WallTrees.BrickBytes)];
            if (!WallTrees.Write(_tree(), _shapes().Set, bytes, out _)) { return false; }
            block = bytes;
            return true;
        }

        /// <summary>
        /// Takes the wall distance from a block that <see cref="TryWriteTree"/> made, in place of a
        /// marking, and copies it. Returns false, keeping what it had, if the grid keeps a count
        /// for every cell, the block is refused (see <see cref="WallTrees.TryRead"/>), or it is of
        /// a grid of another shape, unit or cap, or of other shapes than the grid holds.
        /// </summary>
        /// <remarks>
        /// Give the grid its shapes first. The block keeps a fingerprint of the shapes its tree
        /// was built from, and is refused unless the grid holds the same ones, number for number:
        /// a tree of other shapes would prove room that is not there. Checking reads every shape
        /// once, about a millisecond for two megabytes of them.
        /// </remarks>
        public bool SetTree(ReadOnlySpan<byte> block) {
            _settle();
            if (!IsTree || !WallTrees.TryRead(block, _shapes().Set, out var tree)) { return false; }
            var shape = tree.Shape;
            if (shape.OriginX != Shape.OriginX || shape.OriginY != Shape.OriginY || shape.OriginZ != Shape.OriginZ || shape.CellSize != Shape.CellSize ||
                shape.CountX != Shape.CountX || shape.CountY != Shape.CountY || shape.CountZ != Shape.CountZ || tree.Unit != Unit || tree.Cap != Cap) { return false; }
            _treeNodes.ResizeUninitialized(tree.Nodes.Length);
            _treeBricks.ResizeUninitialized(tree.Bricks.Length);
            tree.Nodes.CopyTo(_treeNodes.AsArray().AsSpan());
            tree.Bricks.CopyTo(_treeBricks.AsArray().AsSpan());
            _filled[0] = 1;
            return true;
        }

        /// <summary>
        /// Whether the wall distance is that of the shapes held: the last marking, once its jobs are
        /// done, filled every layer, and no shapes have been taken since. False before the first
        /// marking, from <see cref="SetShapes(ReadOnlySpan{byte})"/> until the next marking is done,
        /// and if the shapes were found damaged.
        /// </summary>
        public bool IsMarked {
            get {
                _marked();
                for (var z = 0; z < _filled.Length; z++) {
                    if (_filled[z] == 0) { return false; }
                }
                return true;
            }
        }

        /// <summary>
        /// A distance that the nearest shape is proven to be at least, from the wall distance alone:
        /// never more than the true distance, and never more than cap * unit. It reads eight cells.
        /// Returns false if the point is outside the grid or not a number.
        /// </summary>
        /// <remarks>
        /// It says how far the nearest surface is, not whether the point is in open space: deep
        /// inside a closed shell of triangles there is room.
        /// A grid that keeps a tree has none until it is built or set, and then proves nothing: 0
        /// at every point, and a point on one of the grid's three upper faces is refused, where a
        /// grid of counts not yet marked gives 0 there too.
        /// </remarks>
        public bool TryRoom(float3 point, out float room) {
            _marked();
            if (!IsTree) { return WallDistance.RoomAt(new WallMap(Shape, Unit, Cap, _wall.AsReadOnlySpan()), point.x, point.y, point.z, out room); }
            // Until a tree is built there is none: nothing is proven, as with counts that are all 0.
            room = 0f;
            return _treeNodes.Length == 0 ? Shape.TryCentre(point.x, point.y, point.z, out _, out _, out _) : WallTrees.RoomAt(_tree(), point.x, point.y, point.z, out room);
        }

        /// <summary>
        /// Whether the nearest shape is proven to be at least <paramref name="radius"/> from a
        /// point: what <see cref="TryRoom"/> gives, compared. False if the point is outside the
        /// grid or not a number.
        /// </summary>
        /// <remarks>
        /// A grid that keeps a tree answers in open air without reading a count. As with TryRoom,
        /// it says how far the nearest surface is, not whether the point can be reached.
        /// Until such a grid's tree is built or set the answer is no for every radius, 0 and
        /// below too, where a grid of counts not yet marked says yes to those.
        /// </remarks>
        public bool HasRoom(float3 point, float radius) {
            _marked();
            return IsTree
                ? WallTrees.HasRoom(_tree(), point.x, point.y, point.z, radius)
                : WallDistance.HasRoom(new WallMap(Shape, Unit, Cap, _wall.AsReadOnlySpan()), point.x, point.y, point.z, radius);
        }

        /// <summary>
        /// The exact distance from a point to the nearest shape, 0 inside a solid, or
        /// <paramref name="limit"/> if nothing is nearer. The point need not be inside the grid.
        /// Returns false if the point is not a number, the limit is negative, or the shapes are found damaged.
        /// </summary>
        /// <remarks>
        /// It is the distance to the shapes held. Shapes that were collected within a box hold
        /// nothing of what lies beyond it, so with those the answer is the true one only for a
        /// point inside the grid and a limit no more than the box was grown by (see
        /// <see cref="ColliderShapes.Within"/>: the cap times the unit, a unit and a cell).
        /// </remarks>
        public bool TryDistance(float3 point, float limit, out float distance) {
            // The shapes are written only by SetShapes, on this thread: there is nothing to wait for.
            _requireAlive();
            return Shapes.Distance(_shapes().Set, point.x, point.y, point.z, limit, out distance);
        }

        /// <summary>The centre of the cell that holds a point. Returns false if the point is outside the grid.</summary>
        public bool TryCentre(float3 point, out float3 centre) {
            var found = Shape.TryCentre(point.x, point.y, point.z, out var x, out var y, out var z);
            centre = new float3(x, y, z);
            return found;
        }

        /// <summary>
        /// Writes <paramref name="value"/> as the cost of every cell whose centre is inside the solid:
        /// 0 is normal, <see cref="CellCost.Closed"/> closes the cell, and anything else scales a
        /// step into it by 1 + value / 16. Returns false if the solid may not be stored.
        /// </summary>
        /// <remarks>
        /// Regions follow the closed cells, so a change of cost leaves every size class without
        /// regions until <see cref="ScheduleRegions"/> floods them again.
        /// The grid holds a byte of cost a cell from the first time a cost other than 0 is written
        /// into a cell, until <see cref="ClearCost"/>. A solid that holds no cell's centre writes none.
        /// To make a whole grid cost more, fill a solid that covers all of it, and not only the
        /// room inside it: a route is found with far less work where every cell that a flood
        /// reaches has a cost (see <see cref="Routes.Find"/>), and one such cell left at the normal
        /// cost undoes that.
        /// </remarks>
        public bool FillCost(in Solid solid, byte value) {
            _settle();
            // A grid that keeps a tree keeps no cost for each cell.
            if (IsTree) { return false; }
            var none = _cost.Length == 0;
            if (none) {
                _cost.Dispose();
                _cost = new NativeArray<byte>(Shape.CellCount, _allocator);
            }
            var filled = CellCost.Fill(Shape, solid, value, _cost.AsSpan(), out var written);
            // Nothing was written that is not normal already: the bytes are not kept.
            if (none && (!filled || value == 0 || written == 0)) {
                _cost.Dispose();
                _cost = new NativeArray<byte>(0, _allocator);
            }
            if (!filled) { return false; }
            _forgetRegions();
            return true;
        }

        /// <summary>
        /// Makes every cell's cost normal again, and lets the bytes of cost go. As with
        /// <see cref="FillCost"/>, regions must be flooded again.
        /// </summary>
        public void ClearCost() {
            _settle();
            if (_cost.Length != 0) {
                _cost.Dispose();
                _cost = new NativeArray<byte>(0, _allocator);
            }
            _forgetRegions();
        }

        /// <summary>The cost of the cell that holds a point. Returns false if the point is outside the grid.</summary>
        public bool TryCost(float3 point, out byte cost) {
            // The costs are written only on this thread.
            _requireAlive();
            return CellCost.At(Shape, _cost.AsReadOnlySpan(), point.x, point.y, point.z, out cost);
        }

        /// <summary>
        /// Adds a size of body: a radius, in metres, that regions can then be flooded for. Returns the
        /// number to name the class by, counted from 0, or -1 if the class is refused: the radius is
        /// not above half a cell, or it needs more than the grid's cap can say (see
        /// <see cref="SizeClass.TryMake"/>), or the grid keeps its wall distance as a tree (see <see cref="IsTree"/>).
        /// </summary>
        public int AddSizeClass(float radius) {
            _requireAlive();
            // Regions are a byte for every cell, which a grid that keeps a tree does not have room for.
            if (IsTree) { return -1; }
            if (!SizeClass.TryMake(radius, new WallMap(Shape, Unit, Cap, default), out var size)) { return -1; }
            _sizes.Add(new Sized {
                Size = size,
                Region = new NativeArray<byte>(Shape.CellCount, _allocator),
                Result = new NativeArray<int>(4, _allocator),
            });
            return _sizes.Count - 1;
        }

        /// <summary>How many size classes have been added.</summary>
        public int SizeClassCount => _sizes.Count;

        /// <summary>What a size class needs of the grid and is promised by it.</summary>
        public SizeClass SizeOf(int sizeClass) => _sized(sizeClass).Size;

        /// <summary>
        /// Schedules the job that floods a size class's regions from seeds: every cell open for the
        /// class that can be reached from an open cell inside a seed, by steps between cells that
        /// share a face. Returns its handle.
        /// </summary>
        /// <remarks>
        /// It runs after the marking, and after the jobs scheduled so far that read the grid. The
        /// seeds are copied, so the caller's need not be kept. A seed with no open cell inside reaches
        /// nothing and is counted in <see cref="EmptySeeds"/>; it is not moved to look for one.
        /// The flood is refused, leaving the class without regions, unless the wall distance is that
        /// of the shapes held (see <see cref="IsMarked"/>) when it runs: mark first, then flood.
        /// </remarks>
        public JobHandle ScheduleRegions(int sizeClass, ReadOnlySpan<Solid> seeds, JobHandle dependsOn = default) {
            var sized = _sized(sizeClass);
            var copy = new NativeArray<Solid>(seeds.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            seeds.CopyTo(copy.AsSpan());
            sized.Flood = new FloodJob {
                Shape = Shape, Unit = Unit, Cap = Cap, Size = sized.Size, Wall = _wall, Cost = _cost, Filled = _filled, Seeds = copy,
                Queue = new NativeArray<int>(Regions.QueueLength(Shape), Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Region = sized.Region, Result = sized.Result,
            }.Schedule(JobHandle.CombineDependencies(JobHandle.CombineDependencies(dependsOn, _mark), sized.Flood, _readers));
            // It reads the wall distance and the costs, so the next marking and the next change of cost wait for it.
            _read(sized.Flood);
            sized.Stamp++;
            return sized.Flood;
        }

        /// <summary>
        /// Schedules the job that builds a travel field for a size class: how far each cell of its
        /// regions is from where the seeds start, as a count of <paramref name="unit"/> metres.
        /// Returns the field at once; its <see cref="WallGridField.IsBuilt"/> says whether it can be
        /// followed. The caller owns the field and disposes it.
        /// </summary>
        /// <remarks>
        /// The build runs after the size class's flood. It only reads the grid, so builds do not wait
        /// for one another: several scheduled together run side by side. The seeds are copied. It is refused, leaving a field that reaches nothing, if
        /// the size class has no regions when it runs (see <see cref="HasRegions"/>) or a seed may
        /// not be used (see <see cref="Travel.Build"/>). A step pays for the cell it enters, at the
        /// grid's costs as they are when the build runs.
        /// The field is of the regions as they are now: when they are next forgotten or flooded
        /// again it stops answering, and a new one must be built. A field outlives the grid: after
        /// <see cref="Dispose"/> it answers as it was built, and the caller must drop it if the
        /// way changes (see <see cref="WallGridField"/>).
        /// </remarks>
        public WallGridField ScheduleField(int sizeClass, ReadOnlySpan<TravelSeed> seeds, float unit = 0.01f, JobHandle dependsOn = default) {
            var sized = _sized(sizeClass);
            if (!(unit > 0f) || !(unit <= Shape.CellSize * 0.25f)) { throw new ArgumentOutOfRangeException(nameof(unit), unit, "Must be above 0 and no more than a quarter of a cell."); }
            var heapLength = Travel.HeapLength(Shape);
            if (heapLength == 0) { throw new InvalidOperationException("The grid has too many cells for a travel field."); }
            var copy = new NativeArray<TravelSeed>(seeds.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            seeds.CopyTo(copy.AsSpan());
            var distance = new NativeArray<ushort>(Shape.CellCount, _allocator, NativeArrayOptions.UninitializedMemory);
            var result = new NativeArray<int>(3, _allocator);
            var build = new FieldJob {
                Shape = Shape, Unit = unit, Region = sized.Region, Cost = _cost, Flooded = sized.Result, Seeds = copy,
                Times = new NativeArray<float>(Travel.TimesLength(Shape), Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Heap = new NativeArray<int>(heapLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Distance = distance, Result = result,
            }.Schedule(JobHandle.CombineDependencies(dependsOn, sized.Flood));
            // It reads the regions and the costs, so what next writes them waits for it. It does not
            // wait for the grid's other readers itself: it writes nothing they read.
            _read(build);
            return new WallGridField(this, sizeClass, sized.Stamp, unit, distance, result, build);
        }

        // Schedules a search for a route with the arrays of a WallGridRoute. It reads the wall
        // distance, the costs, the regions and the shapes, so what next writes them waits for it.
        internal JobHandle ScheduleRoute(int sizeClass, float3 from, float3 to, bool straighten, float leeway,
            NativeArray<float> sums, NativeArray<int> links, NativeArray<int> heap, NativeArray<float3> points, NativeArray<RouteResult> result, JobHandle dependsOn) {
            var sized = _sized(sizeClass);
            var search = new RouteJob {
                Shape = Shape, Unit = Unit, Cap = Cap, Size = sized.Size, Shapes = _shapes(), Straighten = straighten, Leeway = leeway,
                Wall = _wall, Region = sized.Region, Cost = _cost, Flooded = sized.Result, From = from, To = to,
                Sums = sums, Links = links, Heap = heap, Points = points, Result = result,
            }.Schedule(JobHandle.CombineDependencies(dependsOn, sized.Flood, _mark));
            _read(search);
            return search;
        }

        // What a size class's regions are stamped with: it changes whenever they do.
        internal int RegionStamp(int sizeClass) => _sizes[sizeClass].Stamp;

        /// <summary>
        /// Whether a size class's regions are those of the shapes and costs held: its last flood,
        /// once done, was not refused, the wall distance it read was marked from the shapes held, and
        /// no shapes were taken and no cost changed since.
        /// </summary>
        public bool HasRegions(int sizeClass) => _flooded(sizeClass).Result[0] != 0;

        /// <summary>How many cells the last flood of a size class reached; 0 if it has no regions.</summary>
        public int Reached(int sizeClass) => _flooded(sizeClass).Result[1];

        /// <summary>How many seeds of the last flood of a size class held no open cell; 0 if it has no regions.</summary>
        public int EmptySeeds(int sizeClass) => _flooded(sizeClass).Result[2];

        /// <summary>
        /// Whether the cell that holds a point is open for a size class: its centre is far enough
        /// from every shape, it is not closed, and a seed reached it. False outside the grid, and
        /// while the class has no regions.
        /// </summary>
        /// <remarks>
        /// It is the cell that is open, not the point: a body at the cell's centre fits, and the point
        /// can be most of a cell from there, so nearer a shape than the radius. To ask whether a body
        /// fits at the point itself, compare <see cref="TryRoom"/> or <see cref="TryDistance"/> with its radius.
        /// </remarks>
        public bool IsOpen(int sizeClass, float3 point) =>
            Regions.IsOpen(Shape, _flooded(sizeClass).Region.AsReadOnlySpan(), point.x, point.y, point.z);

        /// <summary>
        /// The centre of the nearest cell that is open for a size class, no farther than
        /// <paramref name="reach"/> from the point in a straight line. Returns false if there is none.
        /// </summary>
        /// <remarks>
        /// Nearest is by straight line, whatever lies between: the cell can be on the other side of
        /// a thin wall or under a floor.
        /// </remarks>
        public bool TryNearestOpen(int sizeClass, float3 point, float reach, out float3 open) {
            var found = Regions.NearestOpen(Shape, _flooded(sizeClass).Region.AsReadOnlySpan(), point.x, point.y, point.z, reach, out var x, out var y, out var z);
            open = new float3(x, y, z);
            return found;
        }

        /// <summary>
        /// As <see cref="TryNearestOpen(int, float3, float, out float3)"/>, but only among cells that
        /// can be reached from <paramref name="like"/>: the nearest place a body there could get to.
        /// Also returns false if <paramref name="like"/> is not itself open.
        /// </summary>
        /// <remarks>
        /// Still by straight line: where both sides of a thin wall are joined some other way, the
        /// cell can be on the far side.
        /// </remarks>
        public bool TryNearestOpen(int sizeClass, float3 point, float reach, float3 like, out float3 open) {
            var found = Regions.NearestOpen(Shape, _flooded(sizeClass).Region.AsReadOnlySpan(), point.x, point.y, point.z, reach,
                like.x, like.y, like.z, out var x, out var y, out var z);
            open = new float3(x, y, z);
            return found;
        }

        /// <summary>
        /// A size class's region per cell, in the same order as <see cref="Wall"/>, once its flood is
        /// done: 0 for a cell that is not open or was not reached. The numbers themselves mean
        /// nothing outside the grid. A job of the caller's that reads it must be handed to <see cref="AddReader"/>.
        /// </summary>
        public NativeArray<byte>.ReadOnly RegionsOf(int sizeClass) => _flooded(sizeClass).Region.AsReadOnly();

        /// <summary>
        /// The wall distance, one count per cell along x, then y, then z, once the marking is done.
        /// A job of the caller's that reads it must be handed to <see cref="AddReader"/>.
        /// Empty in a grid that keeps a tree.
        /// </summary>
        public NativeArray<byte>.ReadOnly Wall { get { _marked(); return _wall.AsReadOnly(); } }

        // The nodes of a grid that keeps a tree, once it is built or set: with TreeBricks, what a
        // job needs to make a WallTree of the grid's shape, unit and cap. A job that reads them
        // must be handed to AddReader, and they are not to be kept past the next marking or
        // SetTree. Empty in a grid that keeps a count for every cell. Not public until a caller
        // outside the package has a job that reads them.
        internal NativeArray<WallNode>.ReadOnly TreeNodes { get { _marked(); return _treeNodes.AsArray().AsReadOnly(); } }

        // The bricks of a grid that keeps a tree: 64 cells each, at four bits a cell.
        internal NativeArray<byte>.ReadOnly TreeBricks { get { _marked(); return _treeBricks.AsArray().AsReadOnly(); } }

        /// <summary>
        /// The cost per cell, in the same order as <see cref="Wall"/>, or an empty array while
        /// every cell is normal: none is held until <see cref="FillCost"/> writes one. A job of the
        /// caller's that reads it must be handed to <see cref="AddReader"/>.
        /// </summary>
        /// <remarks>
        /// Read it again after <see cref="FillCost"/> or <see cref="ClearCost"/>, and do not keep it
        /// across them: the first cost written and the clearing put another array in its place. One
        /// kept from before is then of freed bytes, or is the empty one, which reads every cell as
        /// normal, the closed ones too. In the editor Unity's checks throw on the freed one; in a
        /// player nothing does.
        /// </remarks>
        public NativeArray<byte>.ReadOnly Cost { get { _requireAlive(); return _cost.AsReadOnly(); } }

        /// <summary>
        /// Tells the grid of a job of the caller's that reads <see cref="Wall"/>, <see cref="Cost"/>
        /// or the shapes. Whatever the grid does next that writes or frees those arrays waits for it:
        /// the next marking runs after it, and taking new shapes, changing costs and
        /// <see cref="Dispose"/> complete it first.
        /// </summary>
        /// <remarks>
        /// Without this the grid does not know of the job. In the editor Unity's checks then throw
        /// when the arrays are written under it; in a player nothing does.
        /// </remarks>
        public void AddReader(JobHandle reader) {
            _requireAlive();
            _read(reader);
        }

        // Keeps the handle of a job that reads the grid's arrays, for what next writes them to run after.
        private void _read(JobHandle reader) {
            // Readers that are done are let go, so a grid that is never marked again does not gather handles without end.
            if (_readers.IsCompleted) {
                _readers.Complete();
                _readers = reader;
                return;
            }
            _readers = JobHandle.CombineDependencies(_readers, reader);
        }

        /// <summary>The solids held, in the order they were given.</summary>
        public NativeArray<Solid>.ReadOnly Solids { get { _requireAlive(); return _solids.AsReadOnly(); } }

        /// <summary>The triangles' corner coordinates, in threes.</summary>
        public NativeArray<float>.ReadOnly Corners { get { _requireAlive(); return _corners.AsReadOnly(); } }

        /// <summary>Three corner numbers per triangle.</summary>
        public NativeArray<int>.ReadOnly Triangles { get { _requireAlive(); return _triangles.AsReadOnly(); } }

        /// <summary>Waits for every job this grid has scheduled.</summary>
        public void Complete() {
            _mark.Complete();
            _readers.Complete();
            // Every flood is also among the readers, or among what the marking waited for, so the two
            // lines above have already waited for it. This says so outright, and costs nothing.
            foreach (var sized in _sizes) { sized.Flood.Complete(); }
        }

        /// <summary>
        /// Frees the grid's arrays, after waiting for every job that uses them. Fields built from
        /// the grid are the caller's and are not freed: one that was good goes on answering.
        /// </summary>
        public void Dispose() {
            if (_disposed) { return; }
            _disposed = true;
            Complete();
            _wall.Dispose(); _cost.Dispose(); _filled.Dispose(); _treeNodes.Dispose(); _treeBricks.Dispose();
            _disposeShapes();
            foreach (var sized in _sizes) { sized.Region.Dispose(); sized.Result.Dispose(); }
        }

        // The tree as it is now. Only after the marking is done: its job writes these.
        private WallTree _tree() => new WallTree(Shape, Unit, Cap, _treeNodes.AsArray().AsReadOnlySpan(), _treeBricks.AsArray().AsReadOnlySpan());

        private ShapeArrays _shapes() => new ShapeArrays {
            Solids = _solids, Corners = _corners, Triangles = _triangles,
            HeightFields = _heightFields, HeightBlocks = _heightBlocks, Heights = _heights,
            Boxes = _boxes, Nodes = _nodes, Order = _order,
        };

        private void _allocateShapes(int solids, int corners, int triangles, int heightFields, int heightBlocks, int heights, int nodes) {
            var count = solids + triangles / 3 + heightFields;
            _heightFields = new NativeArray<HeightField>(heightFields, _allocator, NativeArrayOptions.UninitializedMemory);
            _heightBlocks = new NativeArray<uint>(heightBlocks, _allocator, NativeArrayOptions.UninitializedMemory);
            _heights = new NativeArray<ushort>(heights, _allocator, NativeArrayOptions.UninitializedMemory);
            _solids = new NativeArray<Solid>(solids, _allocator, NativeArrayOptions.UninitializedMemory);
            _corners = new NativeArray<float>(corners, _allocator, NativeArrayOptions.UninitializedMemory);
            _triangles = new NativeArray<int>(triangles, _allocator, NativeArrayOptions.UninitializedMemory);
            _boxes = new NativeArray<Box>(count, _allocator, NativeArrayOptions.UninitializedMemory);
            _nodes = new NativeArray<BoxNode>(nodes, _allocator, NativeArrayOptions.UninitializedMemory);
            _order = new NativeArray<int>(count, _allocator, NativeArrayOptions.UninitializedMemory);
        }

        private void _disposeShapes() {
            _solids.Dispose(); _corners.Dispose(); _triangles.Dispose(); _heightFields.Dispose(); _heightBlocks.Dispose(); _heights.Dispose();
            _boxes.Dispose(); _nodes.Dispose(); _order.Dispose();
        }

        // The wall distance was worked out from other shapes: it proves nothing of these, and
        // neither do the regions that were flooded from it.
        private void _forget() {
            _wall.AsSpan().Clear();
            _treeNodes.Clear();
            _treeBricks.Clear();
            _filled.AsSpan().Clear();
            _forgetRegions();
        }

        // The regions were flooded from another wall distance or other closed cells. Every job is done by now.
        private void _forgetRegions() {
            foreach (var sized in _sizes) {
                sized.Region.AsSpan().Clear();
                sized.Result.AsSpan().Clear();
                sized.Stamp++;
            }
        }

        private Sized _sized(int sizeClass) {
            _requireAlive();
            if ((uint)sizeClass >= (uint)_sizes.Count) { throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "No such size class: see AddSizeClass."); }
            return _sizes[sizeClass];
        }

        // Before reading a size class's regions: its flood is done.
        private Sized _flooded(int sizeClass) {
            var sized = _sized(sizeClass);
            sized.Flood.Complete();
            return sized;
        }

        // Before writing or freeing the grid's arrays: every job that reads or writes them is done.
        private void _settle() {
            _requireAlive();
            Complete();
        }

        // Before reading the wall distance: the marking is done. A caller's readers only read, and are not waited for.
        private void _marked() {
            _requireAlive();
            _mark.Complete();
        }

        private void _requireAlive() {
            if (_disposed) { throw new ObjectDisposedException(nameof(WallGrid)); }
        }
    }
}
