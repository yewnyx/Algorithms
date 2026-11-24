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
    /// Used from the main thread. The field owns its distances and must be disposed; it holds no
    /// memory of the grid's. It answers only while it is the field of the grid's regions as they
    /// are: once those regions are forgotten or flooded again (see <see cref="WallGrid.HasRegions"/>),
    /// <see cref="IsBuilt"/> is false and nothing is sampled, since the way it led may now be shut.
    /// Build a new one then.
    /// <para>
    /// A field outlives its grid. Once the grid is disposed, a field that was good at that moment
    /// goes on answering as it was built, so a caller short of memory can let a grid go and keep
    /// its fields. Nothing can then tell the field that the way has changed: if a door shuts or a
    /// cost changes in that place, the field does not refuse, and the caller must dispose it.
    /// </para>
    /// </remarks>
    public sealed class WallGridField : IDisposable {
        private readonly WallGrid _grid;
        private readonly GridShape _shape;
        private readonly int _stamp;
        private NativeArray<ushort> _distance;
        // Whether the build was done (1 or 0), how many cells it reached, and how many it cut.
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

        /// <summary>The number of the size class the field is for.</summary>
        public int SizeClass { get; }

        /// <summary>Metres of travel per count.</summary>
        public float Unit { get; }

        /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>The handle of the job that builds the field.</summary>
        public JobHandle Handle => _build;

        /// <summary>
        /// Whether the field can be followed: its build, once done, was not refused, and the regions
        /// it was built from are still the grid's, or were when the grid was disposed.
        /// </summary>
        /// <remarks>
        /// A build is refused if the size class had no regions when it ran, or a seed may not be
        /// used (see <see cref="Travel.Build"/>). A field whose seeds started no cell is built all
        /// the same, and reaches nothing: read <see cref="Reached"/> to tell.
        /// </remarks>
        public bool IsBuilt {
            get {
                _requireAlive();
                _build.Complete();
                return _result[0] != 0 && _current();
            }
        }

        /// <summary>
        /// How many cells the field reaches; 0 if its build was refused, or if no seed held a cell it
        /// could start from, in which case a larger seed may.
        /// </summary>
        public int Reached { get { _requireAlive(); _build.Complete(); return _result[1]; } }

        /// <summary>How many cells were left unreached because their distance is more than 65,534 counts.</summary>
        public int Cut { get { _requireAlive(); _build.Complete(); return _result[2]; } }

        /// <summary>
        /// The bytes the field's distances take: two a cell. Not counted: while it is being built it
        /// also holds scratch of 12 bytes a cell, which is freed when the build is done.
        /// </summary>
        public long ByteCount { get { _requireAlive(); return (long)_distance.Length * 2 + 12; } }

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
        /// </remarks>
        public bool TrySample(float3 point, out float3 direction, out float distance) {
            direction = float3.zero;
            distance = 0f;
            if (!IsBuilt) { return false; }
            var found = Travel.Sample(new TravelField(_shape, Unit, _distance.AsReadOnlySpan()), point.x, point.y, point.z, out var x, out var y, out var z, out distance);
            direction = new float3(x, y, z);
            return found;
        }

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
            var handle = new SampleJob {
                Shape = _shape, Unit = Unit, Stale = !_current(), Distance = _distance, Result = _result,
                Points = points, Directions = directions, Distances = distances,
            }.Schedule(points.Length, 64, JobHandle.CombineDependencies(_build, dependsOn));
            _read(handle);
            return handle;
        }

        /// <summary>
        /// The field's count per cell, in the same order as <see cref="WallGrid.Wall"/>, once it is
        /// built: <see cref="Travel.Unreached"/> for a cell it does not reach. Whatever
        /// <see cref="IsBuilt"/> says, these are the counts as they were built. A job of the
        /// caller's that reads them must be handed to <see cref="AddReader"/>.
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

        /// <summary>Waits for the build, for every sampling job this field has scheduled, and for the readers it was told of.</summary>
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
        }

        // Keeps the handle of a job that reads the field's counts. Those that are done are let go,
        // so a field that is sampled every frame does not gather handles without end.
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
