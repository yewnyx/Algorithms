using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Finds the way for a body of one size from one point of a <see cref="WallGrid"/> to another,
    /// in a job, and holds the answer: the points to go to in turn, in straight legs. See
    /// <see cref="Routes"/> for what a route promises.
    /// </summary>
    /// <remarks>
    /// Used from the main thread. One of these serves any number of grids, one search at a time:
    /// it owns the search's own arrays, 16 bytes for each cell of the largest grid it has searched,
    /// and keeps them from one search to the next. For a grid that keeps a tree the arrays are
    /// sized to the search and not to the grid: room for <see cref="DefaultTreePlaces"/>
    /// places, under a megabyte (see <see cref="TreeRoutes"/>). Keep one for each search that runs at once,
    /// not one for each thing that follows a route: copy the points out when a search is done,
    /// and the same object serves the next.
    /// A route is of the grid as it was when the search ran. <see cref="IsCurrent"/> says whether
    /// the grid's regions are still the ones it was searched through; the points are kept either
    /// way, and it is the caller's to stop following them.
    /// </remarks>
    public sealed class WallGridRoute : IDisposable {
        private readonly Allocator _allocator;
        private NativeArray<float> _sums, _treeSums;
        private NativeArray<int> _links, _heap, _treeLinks;
        private int _treePlaces = DefaultTreePlaces;
        private NativeArray<float3> _points;
        private NativeArray<RouteResult> _result;
        private JobHandle _search;
        private WallGrid _grid;
        private int _sizeClass, _stamp;
        private float _leeway = DefaultLeeway;
        private bool _disposed;

        /// <summary>The leeway a route object starts with: a way may cost a tenth more than the cheapest.</summary>
        public const float DefaultLeeway = 0.1f;

        /// <summary>The least leeway a search over a tree is run with: see <see cref="TreeRoutes.Find"/>.</summary>
        public const float TreeLeeway = 0.25f;

        /// <summary>
        /// The most places a search over a grid that keeps a tree may hold, taken up or waiting:
        /// under a megabyte of arrays. A search that would hold more ends with
        /// <see cref="RouteStatus.SearchFull"/>, though there may be a way.
        /// </summary>
        /// <remarks>
        /// It is not a wide margin for a large part. On one of 450 m each way, searches between
        /// points up to 80 m apart held 6,896 places at the most. Between points some hundreds of
        /// metres apart they held up to 18,876 and more, and 3 in 20,000 filled the room where
        /// there was a route: rarely, a search of a few hundred metres across a part that size
        /// can end with SearchFull.
        /// </remarks>
        public const int DefaultTreePlaces = 24 * 1024;

        /// <param name="points">The most points a route can have. A route with more is not lost: its status says so, and how many it has.</param>
        /// <param name="allocator">How the route's arrays are allocated.</param>
        public WallGridRoute(int points = 128, Allocator allocator = Allocator.Persistent) {
            if (points < 1) { throw new ArgumentOutOfRangeException(nameof(points), points, "Must be at least 1."); }
            _allocator = allocator;
            _points = new NativeArray<float3>(points, allocator);
            _result = new NativeArray<RouteResult>(1, allocator);
            _sums = new NativeArray<float>(0, allocator);
            _links = new NativeArray<int>(0, allocator);
            _heap = new NativeArray<int>(0, allocator);
            _treeSums = new NativeArray<float>(0, allocator);
            _treeLinks = new NativeArray<int>(0, allocator);
        }

        /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// How much more than the cheapest way the way of a route may cost, as a share of it, for the
        /// searches scheduled from now on. See <see cref="Routes.Find"/>.
        /// </summary>
        /// <remarks>
        /// With some leeway a search takes up far fewer cells: about a tenth as many, at
        /// <see cref="DefaultLeeway"/>, where the grid has no costs or the same cost all through. In
        /// a grid where much of the room costs more than the rest it saves little, unless it is as
        /// large as that cost is. 0 gives the cheapest way. A route still costs no more as flown
        /// than the way that was found for it.
        /// Over a grid that keeps a tree neither holds as written: the way is not the cheapest
        /// whatever the leeway (see <see cref="TreeRoutes"/>), and a search runs with
        /// <see cref="TreeLeeway"/> or this, whichever is more.
        /// </remarks>
        public float Leeway {
            get => _leeway;
            set {
                if (!(value >= 0f) || float.IsInfinity(value)) { throw new ArgumentOutOfRangeException(nameof(value), value, "Must be 0 or more, and finite."); }
                _leeway = value;
            }
        }

        // The most places a search over a grid that keeps a tree may hold, for the searches
        // scheduled from now on. It bounds what such a search can cost, in time and in bytes: 28
        // bytes a slot, for the power of two of slots that is at least a third more than the
        // places, so 37 to 75 bytes a place. Not public until a caller has a part that needs
        // another figure than the one it starts with.
        internal int TreePlaces {
            get => _treePlaces;
            set {
                if (value < 1 || TreeRoutes.LinksLength(value) == 0) { throw new ArgumentOutOfRangeException(nameof(value), value, "Must be at least 1, and no more than a search's arrays can be made for."); }
                _treePlaces = value;
            }
        }

        /// <summary>The handle of the last search scheduled.</summary>
        public JobHandle Handle => _search;

        /// <summary>
        /// Schedules the search for the way from one point to another for a size class of a grid.
        /// Returns the job's handle; the answer is read from this object once it is done.
        /// </summary>
        /// <remarks>
        /// It runs after the size class's flood, the marking and <paramref name="dependsOn"/>, and
        /// reads the grid as it is then. It is refused if the size class has no regions when it
        /// runs (see <see cref="WallGrid.HasRegions"/>).
        /// With <paramref name="straighten"/> false the points are the centres of the cells the
        /// way steps through.
        /// A search scheduled while the last is still running runs after it, and the last one's
        /// answer is lost.
        /// Over a grid that keeps a tree the search is <see cref="TreeRoutes.Find"/>'s: among
        /// open cubes and bricks, with no more than <see cref="DefaultTreePlaces"/> of them held,
        /// and with a leeway of <see cref="TreeLeeway"/> or <see cref="Leeway"/>, whichever is more.
        /// </remarks>
        public JobHandle Schedule(WallGrid grid, int sizeClass, float3 from, float3 to, bool straighten = true, JobHandle dependsOn = default) {
            _requireAlive();
            if (grid == null) { throw new ArgumentNullException(nameof(grid)); }
            if (grid.IsTree) {
                int links = TreeRoutes.LinksLength(_treePlaces), sums = TreeRoutes.SumsLength(_treePlaces);
                if (_treeLinks.Length != links) {
                    _search.Complete();
                    _treeSums.Dispose(); _treeLinks.Dispose();
                    _treeSums = new NativeArray<float>(sums, _allocator, NativeArrayOptions.UninitializedMemory);
                    _treeLinks = new NativeArray<int>(links, _allocator, NativeArrayOptions.UninitializedMemory);
                }
                _search = grid.ScheduleTreeRoute(sizeClass, from, to, straighten, math.max(_leeway, TreeLeeway), _treeSums, _treeLinks, _points, _result,
                    JobHandle.CombineDependencies(dependsOn, _search));
                _grid = grid;
                _sizeClass = sizeClass;
                _stamp = grid.RegionStamp(sizeClass);
                return _search;
            }
            var cells = Routes.LinksLength(grid.Shape);
            if (cells == 0) { throw new InvalidOperationException("The grid has too many cells for a route."); }
            if (_links.Length < cells) {
                // A larger grid than any so far. The arrays are let go once the search that uses them is done.
                _search.Complete();
                _sums.Dispose(); _links.Dispose(); _heap.Dispose();
                _sums = new NativeArray<float>(2 * cells, _allocator, NativeArrayOptions.UninitializedMemory);
                // The links must start at zero, and every search leaves them so.
                _links = new NativeArray<int>(cells, _allocator);
                _heap = new NativeArray<int>(cells, _allocator, NativeArrayOptions.UninitializedMemory);
            }
            _search = grid.ScheduleRoute(sizeClass, from, to, straighten, _leeway, _sums, _links, _heap, _points, _result,
                JobHandle.CombineDependencies(dependsOn, _search));
            _grid = grid;
            _sizeClass = sizeClass;
            _stamp = grid.RegionStamp(sizeClass);
            return _search;
        }

        /// <summary>How the last search ended. <see cref="RouteStatus.Refused"/> before any search.</summary>
        public RouteStatus Status => _answer().Status;

        /// <summary>What the route does not promise at its two ends.</summary>
        public RouteEnds Ends => _answer().Ends;

        /// <summary>
        /// How many points the route has: at least one when there is a route. When the status is
        /// <see cref="RouteStatus.BufferTooShort"/> this is more than <see cref="Points"/> holds.
        /// </summary>
        public int Count => _answer().Count;

        /// <summary>How many cells the search took up, or how many places over a tree; 0 when the straight leg from start to goal was enough.</summary>
        public int Expanded => _answer().Expanded;

        /// <summary>
        /// What the way costs from cell to cell, before it is pulled straight; 0 when nothing was
        /// searched. It is from the centre of the start's cell to the centre of the goal's: the
        /// way between an end and that centre is not in it. With a <see cref="Leeway"/> it can be
        /// that much more than the cheapest way costs.
        /// </summary>
        public float Cost => _answer().Cost;

        /// <summary>
        /// The route's points in the grid's frame, the goal's last; the start is not among them.
        /// None unless the status is <see cref="RouteStatus.Found"/>. They are overwritten by the
        /// next search: copy them out to keep them.
        /// </summary>
        public NativeArray<float3>.ReadOnly Points {
            get {
                var answer = _answer();
                return _points.GetSubArray(0, answer.Status == RouteStatus.Found ? answer.Count : 0).AsReadOnly();
            }
        }

        /// <summary>
        /// Whether the regions the route was searched through are still the grid's: false once a
        /// cost has changed, new shapes were taken or the regions were flooded again, and before
        /// any search. A route that is not current may lead through what is now shut.
        /// </summary>
        /// <remarks>
        /// It is about the grid alone, and waits for nothing. It is true for a search that was
        /// refused because the regions had been forgotten when it was scheduled, so read
        /// <see cref="Status"/> as well. It stays true after the grid is disposed, as a field's does.
        /// </remarks>
        public bool IsCurrent {
            get {
                _requireAlive();
                return _grid != null && _grid.RegionStamp(_sizeClass) == _stamp;
            }
        }

        /// <summary>The bytes this holds: 16 for each cell of the largest grid searched, the arrays of a search over a tree once one has run, and 12 a point.</summary>
        public long ByteCount {
            get {
                _requireAlive();
                return (long)_sums.Length * 4 + (long)_links.Length * 4 + (long)_heap.Length * 4 + (long)_treeSums.Length * 4 + (long)_treeLinks.Length * 4 + (long)_points.Length * 12 + 20;
            }
        }

        /// <summary>Waits for the search.</summary>
        public void Complete() => _search.Complete();

        /// <summary>Waits for the search, then frees the route's arrays. Calling it again does nothing.</summary>
        public void Dispose() {
            if (_disposed) { return; }
            _disposed = true;
            _search.Complete();
            _sums.Dispose(); _links.Dispose(); _heap.Dispose(); _treeSums.Dispose(); _treeLinks.Dispose(); _points.Dispose(); _result.Dispose();
        }

        private RouteResult _answer() {
            _requireAlive();
            _search.Complete();
            return _result[0];
        }

        private void _requireAlive() {
            if (_disposed) { throw new ObjectDisposedException(nameof(WallGridRoute)); }
        }
    }
}
