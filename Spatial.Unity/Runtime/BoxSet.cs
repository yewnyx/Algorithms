using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    /// <summary>
    /// A set of boxes that jobs can query: which are within a radius of a point, and which are nearest.
    /// Each box belongs to an item the caller names with an id, and follows that item's pose.
    /// </summary>
    /// <remarks>
    /// Used from the main thread, which only notes what changes: a job puts the changes into effect.
    /// Items are added, removed and moved at any time, and the changes take effect, in the order they
    /// were made, when <see cref="Schedule(JobHandle)"/> is next called. Only items moved since then
    /// have their boxes worked out again, and a Schedule with nothing added, removed or moved does no
    /// work. Queries are scheduled after it and answer with the caller's ids.
    /// <para>
    /// After <see cref="Dispose"/>, every member that reads or changes the set throws
    /// <see cref="ObjectDisposedException"/>. <see cref="Complete"/> and the plain values
    /// (<see cref="Capacity"/>, <see cref="Count"/>, <see cref="Extent"/>, <see cref="Rebuilds"/>,
    /// <see cref="IsDisposed"/>) are still safe.
    /// </para>
    /// </remarks>
    public sealed class BoxSet : IDisposable {
        // The calls to Add, Remove and Move since a Schedule, in order, for a job to read.
        private struct ChangeLog {
            public NativeList<Change> Changes;
            public NativeList<Pose> Poses;
            public NativeList<Shape> Shapes;

            public ChangeLog(Allocator allocator) {
                Changes = new NativeList<Change>(allocator);
                Poses = new NativeList<Pose>(allocator);
                Shapes = new NativeList<Shape>(allocator);
            }

            public void Clear() { Changes.Clear(); Poses.Clear(); Shapes.Clear(); }
            public void Dispose() { Changes.Dispose(); Poses.Dispose(); Shapes.Dispose(); }
        }

        private readonly int _capacity;
        private int _count, _stamp;
        private bool _disposed;

        // The update and rebuild last scheduled, and the queries scheduled since.
        private JobHandle _update, _readers;

        // The ids in the set, counting every change made so far. Only the main thread uses it.
        private NativeHashSet<int> _members;

        // Changes are written to one log while a job may still read the other.
        private ChangeLog _pending, _applying;

        // Per slot, owned by the jobs. Slots 0 .. _liveCount[0] - 1 are in use.
        private NativeArray<Box> _boxes;
        private NativeArray<float> _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
        private NativeArray<int> _ids, _stampOfSlot, _liveCount;
        private NativeArray<float3> _centres, _halfExtents;
        private NativeHashMap<int, int> _slotOfId;

        private NativeArray<BoxNode> _nodes;
        private NativeArray<int> _order, _workspace, _nodeCount;

        private NativeList<int> _awake;
        private NativeArray<int> _noIds;
        private NativeArray<float3> _noPositions;
        private NativeArray<quaternion> _noRotations;

        /// <param name="capacity">The most items the set can hold.</param>
        /// <param name="extent">The region the items lie in. One outside it costs the tree speed, not correctness.</param>
        public BoxSet(int capacity, Box extent, Allocator allocator = Allocator.Persistent) {
            if (capacity < 0) { throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be 0 or more."); }
            _capacity = capacity;
            Extent = extent;
            _members = new NativeHashSet<int>(capacity, allocator);
            _pending = new ChangeLog(allocator);
            _applying = new ChangeLog(allocator);
            _boxes = new NativeArray<Box>(capacity, allocator);
            _minX = new NativeArray<float>(capacity, allocator);
            _minY = new NativeArray<float>(capacity, allocator);
            _minZ = new NativeArray<float>(capacity, allocator);
            _maxX = new NativeArray<float>(capacity, allocator);
            _maxY = new NativeArray<float>(capacity, allocator);
            _maxZ = new NativeArray<float>(capacity, allocator);
            _ids = new NativeArray<int>(capacity, allocator);
            _stampOfSlot = new NativeArray<int>(capacity, allocator);
            _liveCount = new NativeArray<int>(1, allocator);
            _centres = new NativeArray<float3>(capacity, allocator);
            _halfExtents = new NativeArray<float3>(capacity, allocator);
            _slotOfId = new NativeHashMap<int, int>(capacity, allocator);
            _nodes = new NativeArray<BoxNode>(BoxTree.NodesLength(capacity), allocator);
            _order = new NativeArray<int>(capacity, allocator);
            _workspace = new NativeArray<int>(BoxTree.WorkspaceLength(capacity), allocator);
            _nodeCount = new NativeArray<int>(1, allocator);
            _awake = new NativeList<int>(allocator);
            _noIds = new NativeArray<int>(0, allocator);
            _noPositions = new NativeArray<float3>(0, allocator);
            _noRotations = new NativeArray<quaternion>(0, allocator);
        }

        public int Capacity => _capacity;

        /// <summary>The number of items as of the last Schedule.</summary>
        public int Count => _count;

        /// <summary>The region the items lie in. A change takes effect at the next rebuild.</summary>
        public Box Extent { get; set; }

        public bool IsDisposed => _disposed;

        /// <summary>How many times a rebuild of the tree has been scheduled.</summary>
        public int Rebuilds { get; private set; }

        /// <summary>
        /// Adds an item: a box of the given half-extents around <paramref name="centre"/> in the item's
        /// own frame, at the given pose. Returns false if the id is in use or the set is full.
        /// </summary>
        public bool Add(int id, float3 centre, float3 halfExtents, float3 position, quaternion rotation) {
            _requireAlive();
            if (_members.Count >= _capacity || !_members.Add(id)) { return false; }
            _pending.Changes.Add(new Change { Id = id, Kind = ChangeKind.Add });
            _pending.Poses.Add(new Pose { Position = position, Rotation = rotation });
            _pending.Shapes.Add(new Shape { Centre = centre, HalfExtents = halfExtents });
            return true;
        }

        /// <summary>Removes an item. Returns false if there is none with that id.</summary>
        public bool Remove(int id) {
            _requireAlive();
            if (!_members.Remove(id)) { return false; }
            _pending.Changes.Add(new Change { Id = id, Kind = ChangeKind.Remove });
            return true;
        }

        /// <summary>
        /// Gives an item a new pose. Returns false if there is no item with that id. For many items
        /// each frame, hand the poses over in arrays to
        /// <see cref="Schedule(NativeArray{int}, NativeArray{float3}, NativeArray{quaternion}, JobHandle)"/>.
        /// </summary>
        public bool Move(int id, float3 position, quaternion rotation) {
            _requireAlive();
            if (!_members.Contains(id)) { return false; }
            _pending.Changes.Add(new Change { Id = id, Kind = ChangeKind.Move });
            _pending.Poses.Add(new Pose { Position = position, Rotation = rotation });
            return true;
        }

        /// <summary>
        /// Schedules the jobs that put every change since the last call into effect and bring the boxes
        /// and the tree up to date. Returns their handle, for queries to depend on.
        /// </summary>
        /// <remarks>
        /// The jobs run after the queries scheduled so far, which answer for the set as it was. The
        /// calling thread waits only for the previous call's update, which is normally long done.
        /// With nothing added, removed or moved it schedules nothing.
        /// </remarks>
        public JobHandle Schedule(JobHandle dependsOn = default) => _schedule(_noIds, _noPositions, _noRotations, dependsOn);

        /// <summary>
        /// As <see cref="Schedule(JobHandle)"/>, and also moves each item named in
        /// <paramref name="movedIds"/> to the pose at the same place in the other two arrays.
        /// </summary>
        /// <remarks>
        /// The arrays are read by a job, after <paramref name="dependsOn"/>, so a job of the caller's
        /// can fill them and the calling thread need not touch one item. They are read after the
        /// changes made by Add, Remove and Move, and an id that is not in the set is passed over.
        /// Keep the arrays until the returned handle is complete.
        /// </remarks>
        public JobHandle Schedule(NativeArray<int> movedIds, NativeArray<float3> positions, NativeArray<quaternion> rotations, JobHandle dependsOn = default) {
            if (positions.Length != movedIds.Length) { throw new ArgumentException($"Needs {movedIds.Length} elements, one per id.", nameof(positions)); }
            if (rotations.Length != movedIds.Length) { throw new ArgumentException($"Needs {movedIds.Length} elements, one per id.", nameof(rotations)); }
            return _schedule(movedIds, positions, rotations, dependsOn);
        }

        /// <summary>
        /// Schedules one query per sphere (x, y, z and a radius in w): the ids of the items within it.
        /// </summary>
        /// <remarks>
        /// Query q writes its ids to results[q * stride .. q * stride + counts[q]), at most stride of them,
        /// and how many items matched to totals[q]. counts[q] is -1 if the query was refused.
        /// With <paramref name="tree"/> the tree is walked; without, every box is tested, which is quicker
        /// for a few queries over a small set. The ids are those of the last Schedule. Queries scheduled
        /// between two Schedules do not wait for one another.
        /// </remarks>
        public JobHandle ScheduleInSphere(NativeArray<float4> spheres, NativeArray<int> results, NativeArray<int> counts,
            NativeArray<int> totals, int stride, bool tree, JobHandle dependsOn = default) {
            _requireAlive();
            _requireRoom(spheres.Length, stride, results.Length, counts.Length, nameof(results));
            if (totals.Length < spheres.Length) { throw new ArgumentException($"Needs at least {spheres.Length} elements.", nameof(totals)); }
            var handle = new InSphereJob {
                Set = _view(), Spheres = spheres, Results = results, Counts = counts, Totals = totals, Stride = stride, Tree = tree,
            }.Schedule(spheres.Length, 8, JobHandle.CombineDependencies(_update, dependsOn));
            _read(handle);
            return handle;
        }

        /// <summary>
        /// Schedules one query per point (x, y, z and the greatest distance in w): the ids of the nearest
        /// items, nearest first, and their squared distances.
        /// </summary>
        /// <remarks>
        /// Query q writes up to stride ids to results[q * stride ..] and their squared distances to the
        /// same places in distancesSquared, and how many to counts[q] (-1 if the query was refused).
        /// </remarks>
        public JobHandle ScheduleNearest(NativeArray<float4> points, NativeArray<int> results, NativeArray<float> distancesSquared,
            NativeArray<int> counts, int stride, bool tree, JobHandle dependsOn = default) {
            _requireAlive();
            _requireRoom(points.Length, stride, results.Length, counts.Length, nameof(results));
            if (distancesSquared.Length < (long)points.Length * stride) { throw new ArgumentException($"Needs at least {(long)points.Length * stride} elements.", nameof(distancesSquared)); }
            var handle = new NearestJob {
                Set = _view(), Points = points, Results = results, DistancesSquared = distancesSquared, Counts = counts, Stride = stride, Tree = tree,
            }.Schedule(points.Length, 8, JobHandle.CombineDependencies(_update, dependsOn));
            _read(handle);
            return handle;
        }

        /// <summary>Waits for every job this set has scheduled.</summary>
        public void Complete() {
            _update.Complete();
            _readers.Complete();
        }

        /// <summary>The boxes as of the last Schedule, once its jobs are done. Box i belongs to <see cref="Ids"/>[i].</summary>
        public NativeArray<Box>.ReadOnly Boxes { get { _settle(); return _boxes.GetSubArray(0, _liveCount[0]).AsReadOnly(); } }

        /// <summary>The caller's id for each box in <see cref="Boxes"/>.</summary>
        public NativeArray<int>.ReadOnly Ids { get { _settle(); return _ids.GetSubArray(0, _liveCount[0]).AsReadOnly(); } }

        /// <summary>The tree's nodes as of the last rebuild, once its jobs are done.</summary>
        public NativeArray<BoxNode>.ReadOnly Nodes { get { _settle(); return _nodes.GetSubArray(0, math.max(_nodeCount[0], 0)).AsReadOnly(); } }

        /// <summary>
        /// Which boxes the last Schedule worked out again, as positions in <see cref="Boxes"/>, in
        /// ascending order: the items added or moved before it. The rest were resting. Made afresh at
        /// each read, which goes over every item, so it is for looking, not for every frame.
        /// </summary>
        public NativeArray<int>.ReadOnly Awake {
            get {
                _settle();
                _awake.Clear();
                for (var slot = 0; slot < _liveCount[0]; slot++) {
                    if (_stampOfSlot[slot] == _stamp) { _awake.Add(slot); }
                }
                return _awake.AsArray().AsReadOnly();
            }
        }

        public void Dispose() {
            if (_disposed) { return; }
            _disposed = true;
            Complete();
            _members.Dispose(); _pending.Dispose(); _applying.Dispose();
            _boxes.Dispose(); _minX.Dispose(); _minY.Dispose(); _minZ.Dispose(); _maxX.Dispose(); _maxY.Dispose(); _maxZ.Dispose();
            _ids.Dispose(); _stampOfSlot.Dispose(); _liveCount.Dispose(); _centres.Dispose(); _halfExtents.Dispose(); _slotOfId.Dispose();
            _nodes.Dispose(); _order.Dispose(); _workspace.Dispose(); _nodeCount.Dispose();
            _awake.Dispose(); _noIds.Dispose(); _noPositions.Dispose(); _noRotations.Dispose();
        }

        private JobHandle _schedule(NativeArray<int> movedIds, NativeArray<float3> positions, NativeArray<quaternion> rotations, JobHandle dependsOn) {
            _requireAlive();
            // The last update's job reads the log that is about to be written again.
            _update.Complete();
            _stamp++;
            (_pending, _applying) = (_applying, _pending);
            _pending.Clear();
            _count = _members.Count;
            if (_applying.Changes.Length == 0 && movedIds.Length == 0) { return dependsOn; }

            // After the queries scheduled so far, which read what this writes.
            var handle = new UpdateJob {
                Changes = _applying.Changes.AsArray(), Poses = _applying.Poses.AsArray(), Shapes = _applying.Shapes.AsArray(),
                MovedIds = movedIds, MovedPositions = positions, MovedRotations = rotations, Stamp = _stamp,
                Boxes = _boxes, MinX = _minX, MinY = _minY, MinZ = _minZ, MaxX = _maxX, MaxY = _maxY, MaxZ = _maxZ,
                Ids = _ids, StampOfSlot = _stampOfSlot, Count = _liveCount, Centres = _centres, HalfExtents = _halfExtents, SlotOfId = _slotOfId,
            }.Schedule(JobHandle.CombineDependencies(dependsOn, _readers));
            handle = new BuildJob {
                Boxes = _boxes, Count = _liveCount, Extent = Extent, Nodes = _nodes, Order = _order, Workspace = _workspace, NodeCount = _nodeCount,
            }.Schedule(handle);
            Rebuilds++;
            _update = handle;
            _readers = default;
            return handle;
        }

        private BoxSetView _view() => new BoxSetView {
            Boxes = _boxes, MinX = _minX, MinY = _minY, MinZ = _minZ, MaxX = _maxX, MaxY = _maxY, MaxZ = _maxZ,
            Ids = _ids, Nodes = _nodes, Order = _order, NodeCount = _nodeCount, Count = _liveCount,
        };

        // Keeps a query's handle for the next update to run after. Queries that are done are let go,
        // so a set that never changes does not gather handles without end.
        private void _read(JobHandle query) {
            if (_readers.IsCompleted) {
                _readers.Complete();
                _readers = query;
                return;
            }
            _readers = JobHandle.CombineDependencies(_readers, query);
        }

        private void _settle() {
            _requireAlive();
            Complete();
        }

        private void _requireAlive() {
            if (_disposed) { throw new ObjectDisposedException(nameof(BoxSet)); }
        }

        private static void _requireRoom(int queries, int stride, int resultsLength, int countsLength, string resultsName) {
            if (stride < 0) { throw new ArgumentOutOfRangeException(nameof(stride), stride, "Must be 0 or more."); }
            if (resultsLength < (long)queries * stride) { throw new ArgumentException($"Needs at least {(long)queries * stride} elements.", resultsName); }
            if (countsLength < queries) { throw new ArgumentException($"Needs at least {queries} elements.", "counts"); }
        }
    }
}
