using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    /// <summary>
    /// A travel field over a <see cref="WallGrid"/>, for one of its size classes: how far each cell
    /// a body of that size can be in is from where the field starts. Something with no plan of its
    /// own follows it downhill. Made by <see cref="WallGrid.ScheduleField"/>, which builds it in a job.
    /// </summary>
    /// <remarks>
    /// Used from the main thread. The field owns what it keeps and must be disposed. It answers
    /// only while it is the field of the grid's regions as they are: once those regions are
    /// forgotten or flooded again (see <see cref="WallGrid.HasRegions"/>), <see cref="IsBuilt"/>
    /// is false and nothing is answered, since the way it led may now be shut. Build a new one then.
    /// <para>
    /// A field over counts outlives its grid. Once the grid is disposed, a field that was good at
    /// that moment goes on answering as it was built, so a caller short of memory can let a grid
    /// go and keep its fields. Nothing can then tell the field that the way has changed: if a door
    /// shuts or a cost changes in that place, the field does not refuse, and the caller must
    /// dispose it.
    /// </para>
    /// <para>
    /// On a grid that keeps a tree the field is kept for places, not cells (see
    /// <see cref="TreeFields"/>), and reads the grid's tree as it is asked: it answers only while
    /// the grid is not disposed. There <see cref="TryNext"/> is the call to follow it by, and
    /// <see cref="TrySample"/> gives the way towards that point.
    /// </para>
    /// </remarks>
    public sealed class WallGridField : IDisposable {
        private readonly WallGrid _grid;
        private readonly GridShape _shape;
        private readonly int _stamp;
        private readonly bool _tree;
        private NativeArray<ushort> _distance;
        // Over a tree: the field's bytes, and the seeds it was built from.
        private NativeList<byte> _bytes;
        private NativeArray<TravelSeed> _seeds;
        // Over a tree: the costs and the table it was built and is asked through.
        private NativeArray<byte> _cost, _table;
        // Empty containers for what an asking job does not read, made once and kept with the field.
        private NativeList<WallNode> _noNodes;
        private NativeList<byte> _noBytes;
        private NativeArray<TravelSeed> _noSeeds;
        private NativeArray<byte> _noArray;
        // Whether the build was done (1 or 0), how many cells or places it reached, and how many it cut.
        private NativeArray<int> _result;
        private JobHandle _build, _samples;
        private bool _disposed;

        internal WallGridField(WallGrid grid, int sizeClass, int stamp, float unit, NativeArray<ushort> distance, NativeArray<int> result, JobHandle build) {
            _grid = grid;
            _shape = grid.Shape;
            SizeClass = sizeClass;
            _stamp = stamp;
            Unit = unit;
            _distance = distance;
            _result = result;
            _build = build;
        }

        internal WallGridField(WallGrid grid, int sizeClass, int stamp, float unit, NativeList<byte> bytes, NativeArray<TravelSeed> seeds, NativeArray<byte> cost, NativeArray<byte> table, NativeArray<int> result, JobHandle build)
            : this(grid, sizeClass, stamp, unit, new NativeArray<ushort>(0, Allocator.Persistent), result, build) {
            _tree = true;
            _bytes = bytes;
            _seeds = seeds;
            _cost = cost;
            _table = table;
        }

        /// <summary>The number of the size class the field is for.</summary>
        public int SizeClass { get; }

        /// <summary>Metres of travel per count.</summary>
        public float Unit { get; }

        /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Whether the field keeps a count for every cell, in <see cref="Distance"/>: true on a grid
        /// that keeps counts. On a grid that keeps a tree the field keeps counts for places,
        /// <see cref="Distance"/> is empty however the field is built, and it is asked with
        /// <see cref="TryTravel"/>, <see cref="TryNext"/> and their jobs.
        /// </summary>
        public bool KeepsCells => !_tree;

        /// <summary>The handle of the job that builds the field.</summary>
        public JobHandle Handle => _build;

        /// <summary>
        /// Whether the field can be followed: its build, once done, was not refused, and the regions
        /// it was built from are still the grid's, or were when the grid was disposed. A field over a
        /// tree also needs the grid not to be disposed.
        /// </summary>
        /// <remarks>
        /// A build is refused if the size class had no regions when it ran, or a seed may not be
        /// used (see <see cref="Travel.Build"/>). A field whose seeds started no cell is built all
        /// the same, and reaches nothing: read <see cref="Reached"/> to tell. Built says the field can
        /// be followed, not that <see cref="Distance"/> holds counts: over a tree it is empty (see
        /// <see cref="KeepsCells"/>).
        /// </remarks>
        public bool IsBuilt {
            get {
                _requireAlive();
                _build.Complete();
                return _result[0] != 0 && _current() && !(_tree && _grid.IsDisposed);
            }
        }

        /// <summary>
        /// How many cells the field reaches, or over a tree how many places; 0 if its build was
        /// refused, or if no seed held a cell it could start from, in which case a larger seed may.
        /// </summary>
        public int Reached { get { _requireAlive(); _build.Complete(); return _result[1]; } }

        /// <summary>How many cells or places were left unreached because their distance is more than 65,534 counts.</summary>
        public int Cut { get { _requireAlive(); _build.Complete(); return _result[2]; } }

        /// <summary>
        /// The bytes the field keeps: two a cell, or over a tree two a place and its seeds. Not
        /// counted: while it is being built it also holds scratch, which is freed when the build is done.
        /// </summary>
        public long ByteCount {
            get {
                _requireAlive();
                if (!_tree) { return (long)_distance.Length * 2 + 12; }
                _build.Complete();
                return (long)_bytes.Length + (long)_seeds.Length * 92 + 12;
            }
        }

        /// <summary>
        /// The travel distance at a point, in metres, and the direction that leads down the field
        /// from there, of length 1 (or 0, 0, 0 where there is no way down). See <see cref="Travel.Sample"/>.
        /// </summary>
        /// <remarks>
        /// Returns false if none of the eight cells round the point is reached, or the field is not
        /// built (see <see cref="IsBuilt"/>). It says nothing of what lies between cells: whatever
        /// follows it needs its own way of not hitting things.
        /// At a goal the direction never settles, since the lowest cell can be most of a cell from
        /// the goal itself. A follower has arrived when the distance falls below about a cell.
        /// Over a tree the distance is what <see cref="TryTravel"/> gives and the direction is
        /// towards the point <see cref="TryNext"/> gives; the point's own cell must be open.
        /// </remarks>
        public bool TrySample(float3 point, out float3 direction, out float distance) {
            if (_tree) { return _ask(0, point, out direction, out distance, out _); }
            direction = float3.zero;
            distance = 0f;
            if (!IsBuilt) { return false; }
            var found = Travel.Sample(new TravelField(_shape, Unit, _distance.AsReadOnlySpan()), point.x, point.y, point.z, out var x, out var y, out var z, out distance);
            direction = new float3(x, y, z);
            return found;
        }

        /// <summary>
        /// How far a point is along the field, in metres, and the place it is in: cheap enough to
        /// ask of every field at every step. Over counts the place is the point's cell.
        /// </summary>
        /// <remarks>
        /// Over counts, the distance <see cref="TrySample"/> gives. Over a tree, what
        /// <see cref="TreeFields.TravelAt"/> gives. Returns false where those do, or the field is
        /// not built.
        /// </remarks>
        public bool TryTravel(float3 point, out float travel, out int place) => _ask(1, point, out _, out travel, out place);

        /// <summary>
        /// The point to fly to next, the travel at the point asked about, and the place it is in.
        /// Fly straight to it, and ask again on entering another place or on reaching it.
        /// </summary>
        /// <remarks>
        /// Over a tree, what <see cref="TreeFields.Next"/> gives: the point is in sight, and
        /// following it never leaves open cells. Over counts the point is one cell along the
        /// direction <see cref="TrySample"/> gives, and the place is the point's cell. Returns
        /// false where those are refused, at the seed's own point over a tree, or where the field is
        /// not built.
        /// </remarks>
        public bool TryNext(float3 point, out float3 next, out float travel, out int place) => _ask(2, point, out next, out travel, out place);

        /// <summary>
        /// Schedules one sample per point, side by side: directions[i] and distances[i] are what
        /// <see cref="TrySample"/> gives for points[i]. Where a sample is refused, the distance is -1
        /// and the direction 0, 0, 0. Returns the job's handle.
        /// </summary>
        /// <remarks>
        /// It runs after the build, and after <paramref name="dependsOn"/>, so a job of the caller's
        /// can fill the points. If the field's regions are no longer the grid's when this is called,
        /// every sample is refused. Keep the three arrays until the handle is complete.
        /// </remarks>
        public JobHandle ScheduleSample(NativeArray<float3> points, NativeArray<float3> directions, NativeArray<float> distances, JobHandle dependsOn = default) {
            _requireAlive();
            if (directions.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(directions)); }
            if (distances.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(distances)); }
            if (!_tree) {
                // Over counts, the sample alone: nothing made or freed for it.
                var sampled = new SampleJob {
                    Shape = _shape, Unit = Unit, Stale = !_current(), Distance = _distance, Result = _result,
                    Points = points, Directions = directions, Distances = distances,
                }.Schedule(points.Length, 64, JobHandle.CombineDependencies(_build, dependsOn));
                _read(sampled);
                return sampled;
            }
            var places = new NativeArray<int>(0, Allocator.TempJob);
            var handle = _schedule(0, points, directions, distances, places, dependsOn);
            places.Dispose(handle);
            return handle;
        }

        /// <summary>
        /// Schedules <see cref="TryTravel"/> for many points, side by side: travels[i] and places[i]
        /// are its answers for points[i], -1 and -1 where it is refused. Returns the job's handle.
        /// </summary>
        /// <remarks>It runs after the build and after <paramref name="dependsOn"/>. Keep the arrays until the handle is complete.</remarks>
        public JobHandle ScheduleTravel(NativeArray<float3> points, NativeArray<float> travels, NativeArray<int> places, JobHandle dependsOn = default) {
            _requireAlive();
            if (travels.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(travels)); }
            if (places.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(places)); }
            var vectors = new NativeArray<float3>(0, Allocator.TempJob);
            var handle = _schedule(1, points, vectors, travels, places, dependsOn);
            vectors.Dispose(handle);
            return handle;
        }

        /// <summary>
        /// Schedules <see cref="TryNext"/> for many points, side by side: nexts[i], travels[i] and
        /// places[i] are its answers for points[i]; 0, 0, 0, -1 and -1 where it is refused. Returns
        /// the job's handle.
        /// </summary>
        /// <remarks>It runs after the build and after <paramref name="dependsOn"/>. Keep the arrays until the handle is complete.</remarks>
        public JobHandle ScheduleNext(NativeArray<float3> points, NativeArray<float3> nexts, NativeArray<float> travels, NativeArray<int> places, JobHandle dependsOn = default) {
            _requireAlive();
            if (nexts.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(nexts)); }
            if (travels.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(travels)); }
            if (places.Length < points.Length) { throw new ArgumentException($"Needs at least {points.Length} elements, one per point.", nameof(places)); }
            return _schedule(2, points, nexts, travels, places, dependsOn);
        }

        /// <summary>
        /// The field's count per cell, in the same order as <see cref="WallGrid.Wall"/>, once it is
        /// built: <see cref="Travel.Unreached"/> for a cell it does not reach. Whatever
        /// <see cref="IsBuilt"/> says, these are the counts as they were built. Empty over a tree,
        /// even once built: read <see cref="KeepsCells"/> before indexing it.
        /// A job of the caller's that reads them must be handed to <see cref="AddReader"/>.
        /// </summary>
        public NativeArray<ushort>.ReadOnly Distance { get { _requireAlive(); _build.Complete(); return _distance.AsReadOnly(); } }

        /// <summary>
        /// Tells the field of a job of the caller's that reads <see cref="Distance"/>, so that
        /// <see cref="Dispose"/> waits for it before the counts are freed.
        /// </summary>
        /// <remarks>
        /// Without this the field does not know of the job. In the editor Unity's checks then throw
        /// when the field is disposed under it; in a player nothing does.
        /// </remarks>
        public void AddReader(JobHandle reader) {
            _requireAlive();
            _read(reader);
        }

        /// <summary>Waits for the build, for every job this field has scheduled, and for the readers it was told of.</summary>
        public void Complete() {
            _build.Complete();
            _samples.Complete();
        }

        /// <summary>Waits for the build and for every job of the field's, then frees its arrays. Calling it again does nothing.</summary>
        public void Dispose() {
            if (_disposed) { return; }
            _disposed = true;
            Complete();
            _distance.Dispose();
            _result.Dispose();
            if (_tree) {
                _bytes.Dispose();
                _seeds.Dispose();
                _cost.Dispose();
                _table.Dispose();
            }
            if (_noBytes.IsCreated) {
                _noNodes.Dispose();
                _noBytes.Dispose();
                _noSeeds.Dispose();
                _noArray.Dispose();
            }
        }

        // One question of the field on the main thread: 0 the travel and the direction, 1 the travel and the place, 2 the point to fly to.
        private bool _ask(int ask, float3 point, out float3 vector, out float travel, out int place) {
            vector = float3.zero; travel = 0f; place = -1;
            if (!IsBuilt) { return false; }
            if (!_tree) { return AskCounts(new TravelField(_shape, Unit, _distance.AsReadOnlySpan()), ask, point, out vector, out travel, out place); }
            // The grid may be marking or flooding again with the same shapes, which keeps the field current: it reads what they write.
            _grid.TreeWriters(SizeClass).Complete();
            var field = new TreeField(new WallTree(_shape, _grid.Unit, _grid.Cap, _grid.TreeNodeList.AsArray().AsReadOnlySpan(), _grid.TreeBrickList.AsArray().AsReadOnlySpan()),
                _grid.TreeRegionOf(SizeClass).AsArray().AsReadOnlySpan(), _bytes.AsArray().AsReadOnlySpan(), _seeds.AsReadOnlySpan(), Unit,
                _cost.AsReadOnlySpan(), _table.AsReadOnlySpan());
            return AskTree(field, ask, point, out vector, out travel, out place);
        }

        // The same questions of a field over counts, as the job asks them too.
        internal static bool AskCounts(in TravelField field, int ask, float3 point, out float3 vector, out float travel, out int place) {
            vector = float3.zero; travel = 0f; place = -1;
            if (!Travel.Sample(field, point.x, point.y, point.z, out var x, out var y, out var z, out travel)) { return false; }
            var shape = field.Shape;
            // The point's cell, as the grid finds it: a point on a face is in the higher cell.
            double atX = math.floor(((double)point.x - shape.OriginX) / shape.CellSize), atY = math.floor(((double)point.y - shape.OriginY) / shape.CellSize), atZ = math.floor(((double)point.z - shape.OriginZ) / shape.CellSize);
            if (atX >= 0d && atX < shape.CountX && atY >= 0d && atY < shape.CountY && atZ >= 0d && atZ < shape.CountZ) { place = shape.IndexOf((int)atX, (int)atY, (int)atZ); }
            var direction = new float3(x, y, z);
            vector = ask == 2 ? point + direction * shape.CellSize : ask == 0 ? direction : float3.zero;
            return true;
        }

        // The same questions of a field over a tree, as the job asks them too.
        internal static bool AskTree(in TreeField field, int ask, float3 point, out float3 vector, out float travel, out int place) {
            vector = float3.zero;
            if (ask == 1) { return TreeFields.TravelAt(field, point.x, point.y, point.z, out travel, out place); }
            if (ask == 2) {
                var found = TreeFields.Next(field, point.x, point.y, point.z, out var nx, out var ny, out var nz, out travel, out place);
                vector = new float3(nx, ny, nz);
                return found;
            }
            if (!TreeFields.TravelAt(field, point.x, point.y, point.z, out travel, out place)) { return false; }
            if (TreeFields.Next(field, point.x, point.y, point.z, out var tx, out var ty, out var tz, out _, out _)) {
                var way = new float3(tx, ty, tz) - point;
                var length = math.length(way);
                if (length > 1e-6f) { vector = way / length; }
            }
            return true;
        }

        private JobHandle _schedule(int ask, NativeArray<float3> points, NativeArray<float3> vectors, NativeArray<float> travels, NativeArray<int> places, JobHandle dependsOn) {
            var tree = _tree && !_grid.IsDisposed;
            var job = new FieldAskJob {
                Shape = _shape, Unit = tree ? _grid.Unit : 0f, Cap = tree ? _grid.Cap : (byte)0, FieldUnit = Unit, Ask = ask, Tree = _tree,
                Stale = !_current() || (_tree && _grid.IsDisposed), Distance = _distance, Result = _result,
                Points = points, Vectors = vectors, Travels = travels, Places = places,
            };
            // A job needs every container made: what a field does not read is an empty one, made once.
            if (!_noBytes.IsCreated) {
                _noNodes = new NativeList<WallNode>(0, Allocator.Persistent);
                _noBytes = new NativeList<byte>(0, Allocator.Persistent);
                _noSeeds = new NativeArray<TravelSeed>(0, Allocator.Persistent);
                _noArray = new NativeArray<byte>(0, Allocator.Persistent);
            }
            job.Nodes = tree ? _grid.TreeNodeList : _noNodes;
            job.Bricks = tree ? _grid.TreeBrickList : _noBytes;
            job.Region = tree ? _grid.TreeRegionOf(SizeClass) : _noBytes;
            job.Field = _tree ? _bytes : _noBytes;
            job.Seeds = _tree ? _seeds : _noSeeds;
            job.Cost = tree ? _cost : _noArray;
            job.Table = _tree ? _table : _noArray;
            var handle = job.Schedule(points.Length, 64, JobHandle.CombineDependencies(_build, dependsOn, tree ? _grid.TreeWriters(SizeClass) : default));
            _read(handle);
            // A job over the tree reads the grid's tree and regions: what next writes them waits for it.
            if (tree) { _grid.AddReader(handle); }
            return handle;
        }

        // Keeps the handle of a job that reads the field. Those that are done are let go, so a field
        // that is sampled every frame does not gather handles without end.
        private void _read(JobHandle reader) {
            if (_samples.IsCompleted) {
                _samples.Complete();
                _samples = reader;
                return;
            }
            _samples = JobHandle.CombineDependencies(_samples, reader);
        }

        // Whether the regions the field was built from are still the grid's. A grid that has been
        // disposed keeps the stamps it had, so a field that was good then stays good.
        private bool _current() => _grid.RegionStamp(SizeClass) == _stamp;

        private void _requireAlive() {
            if (_disposed) { throw new ObjectDisposedException(nameof(WallGridField)); }
        }
    }
}
