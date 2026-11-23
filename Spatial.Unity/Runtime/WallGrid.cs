using System;
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
    /// the grid's arrays waits for a caller's own readers (see <see cref="AddReader"/>). The wall distance only ever claims room that the shapes it holds allow:
    /// it is 0 everywhere until a marking is done, 0 again from the moment new shapes are taken,
    /// and 0 in any layer whose marking was refused.
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
        private NativeArray<Solid> _solids;
        private NativeArray<float> _corners;
        private NativeArray<int> _triangles, _order;
        private NativeArray<Box> _boxes;
        private NativeArray<BoxNode> _nodes;

        /// <param name="shape">Where the grid sits in its own frame.</param>
        /// <param name="unit">Metres per count of wall distance.</param>
        /// <param name="cap">The largest count stored: no query can prove more room than cap * unit.</param>
        public WallGrid(GridShape shape, float unit = 1f / 128f, byte cap = 128, Allocator allocator = Allocator.Persistent) {
            if (shape.CellCount == 0) { throw new ArgumentException("The shape cannot be used: see GridShape.CellCount.", nameof(shape)); }
            if (!(unit > 0f) || float.IsInfinity(unit)) { throw new ArgumentOutOfRangeException(nameof(unit), unit, "Must be above 0."); }
            if (cap == 0) { throw new ArgumentOutOfRangeException(nameof(cap), cap, "Must be above 0."); }
            Shape = shape;
            Unit = unit;
            Cap = cap;
            _allocator = allocator;
            // Until it is marked, the wall distance is 0 everywhere: nothing is proven.
            _wall = new NativeArray<byte>(shape.CellCount, allocator);
            // No byte of cost is held until one is written: an empty array means every cell is normal.
            _cost = new NativeArray<byte>(0, allocator);
            _filled = new NativeArray<byte>(shape.CountZ, allocator);
            _allocateShapes(0, 0, 0, 0);
        }

        public GridShape Shape { get; }

        /// <summary>Metres per count of wall distance.</summary>
        public float Unit { get; }

        /// <summary>The largest count of wall distance stored.</summary>
        public byte Cap { get; }

        public bool IsDisposed => _disposed;

        /// <summary>The number of shapes held.</summary>
        public int ShapeCount { get { _requireAlive(); return _boxes.Length; } }

        /// <summary>
        /// The bytes the grid's arrays take, for a caller that keeps grids within a budget: one a
        /// cell for the wall distance, one a cell for the costs once a cost has been written, and
        /// the shapes.
        /// </summary>
        public long ByteCount {
            get {
                _requireAlive();
                return (long)_wall.Length + _cost.Length + _filled.Length + (long)_solids.Length * 64 + (long)_corners.Length * 4 +
                       (long)_triangles.Length * 4 + (long)_order.Length * 4 + (long)_boxes.Length * 24 + (long)_nodes.Length * 32;
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
            _allocateShapes(shapes.Solids.Length, shapes.Corners.Length, shapes.Triangles.Length, shapes.Nodes.Length);
            shapes.Solids.CopyTo(_solids.AsSpan());
            shapes.Corners.CopyTo(_corners.AsSpan());
            shapes.Triangles.CopyTo(_triangles.AsSpan());
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
        public bool SetShapes(ReadOnlySpan<Solid> solids, ReadOnlySpan<float> corners, ReadOnlySpan<int> triangles) {
            _settle();
            if (corners.Length % 3 != 0 || triangles.Length % 3 != 0) { return false; }
            var total = (long)solids.Length + triangles.Length / 3;
            if (total > int.MaxValue / 4) { return false; }
            var count = (int)total;
            var boxes = new NativeArray<Box>(count, Allocator.Temp);
            var nodes = new NativeArray<BoxNode>(Shapes.NodesLength(count), Allocator.Temp);
            var order = new NativeArray<int>(count, Allocator.Temp);
            var workspace = new NativeArray<int>(Shapes.WorkspaceLength(count), Allocator.Temp);
            var built = Shapes.Build(solids, corners, triangles, boxes.AsSpan(), nodes.AsSpan(), order.AsSpan(), workspace.AsSpan(), out var nodeCount);
            if (built) {
                _disposeShapes();
                _allocateShapes(solids.Length, corners.Length, triangles.Length, nodeCount);
                solids.CopyTo(_solids.AsSpan());
                corners.CopyTo(_corners.AsSpan());
                triangles.CopyTo(_triangles.AsSpan());
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
        /// </remarks>
        public JobHandle ScheduleMark(JobHandle dependsOn = default) {
            _requireAlive();
            _mark = new MarkJob {
                Shape = Shape, Shapes = _shapes(), Unit = Unit, Cap = Cap, Wall = _wall, Filled = _filled,
            }.Schedule(Shape.CountZ, 1, JobHandle.CombineDependencies(dependsOn, _mark, _readers));
            _readers = default;
            return _mark;
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
        /// </remarks>
        public bool TryRoom(float3 point, out float room) {
            _marked();
            return WallDistance.RoomAt(new WallMap(Shape, Unit, Cap, _wall.AsReadOnlySpan()), point.x, point.y, point.z, out room);
        }

        /// <summary>
        /// The exact distance from a point to the nearest shape, 0 inside a solid, or
        /// <paramref name="limit"/> if nothing is nearer. The point need not be inside the grid.
        /// Returns false if the point is not a number, the limit is negative, or the shapes are found damaged.
        /// </summary>
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
        /// The grid holds a byte of cost a cell from the first time a cost other than 0 is written
        /// into a cell, until <see cref="ClearCost"/>. A solid that holds no cell's centre writes none.
        /// </remarks>
        public bool FillCost(in Solid solid, byte value) {
            _settle();
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
            return true;
        }

        /// <summary>Makes every cell's cost normal again, and lets the bytes of cost go.</summary>
        public void ClearCost() {
            _settle();
            if (_cost.Length != 0) {
                _cost.Dispose();
                _cost = new NativeArray<byte>(0, _allocator);
            }
        }

        /// <summary>The cost of the cell that holds a point. Returns false if the point is outside the grid.</summary>
        public bool TryCost(float3 point, out byte cost) {
            // The costs are written only on this thread.
            _requireAlive();
            return CellCost.At(Shape, _cost.AsReadOnlySpan(), point.x, point.y, point.z, out cost);
        }

        /// <summary>
        /// The wall distance, one count per cell along x, then y, then z, once the marking is done.
        /// A job of the caller's that reads it must be handed to <see cref="AddReader"/>.
        /// </summary>
        public NativeArray<byte>.ReadOnly Wall { get { _marked(); return _wall.AsReadOnly(); } }

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
        }

        public void Dispose() {
            if (_disposed) { return; }
            _disposed = true;
            Complete();
            _wall.Dispose(); _cost.Dispose(); _filled.Dispose();
            _disposeShapes();
        }

        private ShapeArrays _shapes() => new ShapeArrays {
            Solids = _solids, Corners = _corners, Triangles = _triangles, Boxes = _boxes, Nodes = _nodes, Order = _order,
        };

        private void _allocateShapes(int solids, int corners, int triangles, int nodes) {
            var count = solids + triangles / 3;
            _solids = new NativeArray<Solid>(solids, _allocator, NativeArrayOptions.UninitializedMemory);
            _corners = new NativeArray<float>(corners, _allocator, NativeArrayOptions.UninitializedMemory);
            _triangles = new NativeArray<int>(triangles, _allocator, NativeArrayOptions.UninitializedMemory);
            _boxes = new NativeArray<Box>(count, _allocator, NativeArrayOptions.UninitializedMemory);
            _nodes = new NativeArray<BoxNode>(nodes, _allocator, NativeArrayOptions.UninitializedMemory);
            _order = new NativeArray<int>(count, _allocator, NativeArrayOptions.UninitializedMemory);
        }

        private void _disposeShapes() {
            _solids.Dispose(); _corners.Dispose(); _triangles.Dispose(); _boxes.Dispose(); _nodes.Dispose(); _order.Dispose();
        }

        // The wall distance was worked out from other shapes: it proves nothing of these.
        private void _forget() {
            _wall.AsSpan().Clear();
            _filled.AsSpan().Clear();
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
