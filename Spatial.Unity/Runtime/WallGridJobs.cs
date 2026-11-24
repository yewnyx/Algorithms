using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    /// <summary>The shapes a <see cref="WallGrid"/> owns, as a job reads them. Nothing here is written by a job.</summary>
    internal struct ShapeArrays {
        [ReadOnly] public NativeArray<Solid> Solids;
        [ReadOnly] public NativeArray<float> Corners;
        [ReadOnly] public NativeArray<int> Triangles;
        [ReadOnly] public NativeArray<HeightField> HeightFields;
        [ReadOnly] public NativeArray<uint> HeightBlocks;
        [ReadOnly] public NativeArray<ushort> Heights;
        [ReadOnly] public NativeArray<Box> Boxes;
        [ReadOnly] public NativeArray<BoxNode> Nodes;
        [ReadOnly] public NativeArray<int> Order;

        public ShapeSet Set => new ShapeSet(Solids.AsReadOnlySpan(), Corners.AsReadOnlySpan(), Triangles.AsReadOnlySpan(),
            HeightFields.AsReadOnlySpan(), HeightBlocks.AsReadOnlySpan(), Heights.AsReadOnlySpan(),
            Boxes.AsReadOnlySpan(), Nodes.AsReadOnlySpan(), Order.AsReadOnlySpan());
    }

    /// <summary>Floods the regions of one size class from its seeds.</summary>
    [BurstCompile]
    internal struct FloodJob : IJob {
        public GridShape Shape;
        public float Unit;
        public byte Cap;
        public SizeClass Size;
        [ReadOnly] public NativeArray<byte> Wall, Cost;
        // 1 for each layer of the wall distance that the last marking filled.
        [ReadOnly] public NativeArray<byte> Filled;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<Solid> Seeds;
        [DeallocateOnJobCompletion] public NativeArray<int> Queue;
        public NativeArray<byte> Region;
        // Whether the flood was done (1 or 0), how many cells it reached, how many seeds held no
        // open cell, and 0.
        public NativeArray<int> Result;
        // The costs the cells it reached hold, a bit for each of the 256.
        public NativeArray<ulong> Present;

        public void Execute() {
            // Regions are only as good as the wall distance they are flooded from: unless every layer
            // of it was marked from the shapes held, the flood is refused and no cell is left open.
            var marked = true;
            for (var z = 0; z < Filled.Length; z++) { marked &= Filled[z] != 0; }
            int reached = 0, empty = 0;
            var done = marked && Regions.FromSeeds(new WallMap(Shape, Unit, Cap, Wall.AsReadOnlySpan()), Size, Cost.AsReadOnlySpan(), Seeds.AsReadOnlySpan(),
                Region.AsSpan(), Queue.AsSpan(), out reached, out empty);
            // The regions are already 0 whenever the wall distance is not marked, since what unmarks it
            // forgets them. Cleared here all the same, so that a refused flood never leaves a cell open.
            if (!marked) { Region.AsSpan().Clear(); }
            Result[0] = done ? 1 : 0;
            Result[1] = reached;
            Result[2] = empty;
            Result[3] = 0;
            if (!done || !Regions.CostsIn(Shape, Region.AsReadOnlySpan(), Cost.AsReadOnlySpan(), Present.AsSpan())) { Present.AsSpan().Clear(); }
        }
    }

    /// <summary>Builds a travel field over one size class's regions.</summary>
    [BurstCompile]
    internal struct FieldJob : IJob {
        public GridShape Shape;
        public float Unit;
        [ReadOnly] public NativeArray<byte> Region, Cost;
        // What each cost costs this field's kind of agent: 256, or none.
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<byte> Table;
        // The size class's flood: element 0 is 1 if its regions are those of the shapes and costs held.
        [ReadOnly] public NativeArray<int> Flooded;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TravelSeed> Seeds;
        [DeallocateOnJobCompletion] public NativeArray<float> Times;
        [DeallocateOnJobCompletion] public NativeArray<int> Heap;
        public NativeArray<ushort> Distance;
        // Whether the field was built (1 or 0), how many cells it reached, and how many were cut.
        public NativeArray<int> Result;

        public void Execute() {
            // A field is only as good as the regions it spreads through.
            int reached = 0, cut = 0;
            var built = Flooded[0] != 0 && Travel.Build(Shape, Region.AsReadOnlySpan(), Cost.AsReadOnlySpan(), Seeds.AsReadOnlySpan(), Unit,
                Distance.AsSpan(), Times.AsSpan(), Heap.AsSpan(), out reached, out cut, Table.AsReadOnlySpan());
            if (!built) { Distance.AsSpan().Fill(Travel.Unreached); }
            Result[0] = built ? 1 : 0;
            Result[1] = reached;
            Result[2] = cut;
        }
    }

    /// <summary>Finds a route for one size class.</summary>
    [BurstCompile]
    internal struct RouteJob : IJob {
        public GridShape Shape;
        public float Unit;
        public byte Cap;
        public SizeClass Size;
        public ShapeArrays Shapes;
        public bool Straighten;
        public float Leeway;
        [ReadOnly] public NativeArray<byte> Wall, Region, Cost, Table;
        // The size class's flood: element 0 is 1 if its regions are those of the shapes and costs
        // held. And the costs their cells hold, from which the least a step can cost is worked out.
        [ReadOnly] public NativeArray<int> Flooded;
        [ReadOnly] public NativeArray<ulong> Present;
        public float3 From, To;
        // The search's own.
        public NativeArray<float> Sums;
        public NativeArray<int> Links, Heap;
        public NativeArray<float3> Points;
        public NativeArray<RouteResult> Result;

        public void Execute() {
            // A route is only as good as the regions it is searched through, and they are only
            // there while the wall distance is that of the shapes held.
            if (Flooded[0] == 0) {
                Result[0] = default;
                return;
            }
            Result[0] = Routes.Find(new WallMap(Shape, Unit, Cap, Wall.AsReadOnlySpan()), Size, Shapes.Set,
                Region.AsReadOnlySpan(), Cost.AsReadOnlySpan(), From.x, From.y, From.z, To.x, To.y, To.z, Straighten,
                Sums.AsSpan(), Links.AsSpan(), Heap.AsSpan(), Points.Reinterpret<float>(12).AsSpan(),
                // With no costs kept every cell is normal, whatever the table.
                Cost.Length == 0 ? (byte)0 : CellCost.Least(Present.AsReadOnlySpan(), Table.AsReadOnlySpan()), Leeway, Table.AsReadOnlySpan());
        }
    }

    /// <summary>Floods the regions of one size class over a grid's tree.</summary>
    [BurstCompile]
    internal struct TreeFloodJob : IJob {
        public GridShape Shape;
        public float Unit;
        public byte Cap;
        public SizeClass Size;
        [ReadOnly] public NativeList<WallNode> Nodes;
        [ReadOnly] public NativeList<byte> Bricks;
        // The costs over the tree, a byte a leaf, or empty.
        [ReadOnly] public NativeList<byte> Cost;
        // One element: 1 if the tree is that of the shapes held.
        [ReadOnly] public NativeArray<byte> Filled;
        [ReadOnly, DeallocateOnJobCompletion] public NativeArray<Solid> Seeds;
        // What TreeRegions.FromSeeds makes, cut to what it used: empty after a flood that was refused.
        public NativeList<byte> Region;
        // Whether the flood was done (1 or 0), how many cells it reached, how many seeds held no
        // open cell, and 0.
        public NativeArray<int> Result;
        // The costs the leaves it reached hold, a bit for each of the 256.
        public NativeArray<ulong> Present;

        public void Execute() {
            Result[0] = 0; Result[1] = 0; Result[2] = 0; Result[3] = 0;
            Region.Clear();
            if (Filled[0] == 0) { return; }
            var tree = new WallTree(Shape, Unit, Cap, Nodes.AsArray().AsReadOnlySpan(), Bricks.AsArray().AsReadOnlySpan());
            var most = TreeRegions.MostLength(tree);
            if (most == 0) { return; }
            Region.ResizeUninitialized(most);
            var marks = new NativeArray<ulong>(TreeRegions.MarksLength(tree), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var queue = new NativeArray<int>(TreeRegions.QueueLength(tree), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var done = TreeRegions.FromSeeds(tree, Size, Cost.AsArray().AsReadOnlySpan(), Seeds.AsReadOnlySpan(), Region.AsArray().AsSpan(), marks.AsSpan(), queue.AsSpan(), out var length, out var reached, out var empty);
            marks.Dispose();
            queue.Dispose();
            // What was not needed is given back: the bytes follow the shapes' surface.
            Region.ResizeUninitialized(done ? length : 0);
            Region.TrimExcess();
            if (!done || !TreeRegions.CostsIn(tree, Region.AsArray().AsReadOnlySpan(), Cost.AsArray().AsReadOnlySpan(), Present.AsSpan())) { Present.AsSpan().Clear(); }
            Result[0] = done ? 1 : 0;
            Result[1] = reached;
            Result[2] = empty;
        }
    }

    /// <summary>Finds a route for one size class over a grid's tree.</summary>
    [BurstCompile]
    internal struct TreeRouteJob : IJob {
        public GridShape Shape;
        public float Unit;
        public byte Cap;
        public SizeClass Size;
        public ShapeArrays Shapes;
        public bool Straighten;
        public float Leeway;
        [ReadOnly] public NativeList<WallNode> Nodes;
        [ReadOnly] public NativeList<byte> Bricks;
        [ReadOnly] public NativeList<byte> Region;
        // The size class's flood: element 0 is 1 if its regions are those of the shapes held.
        [ReadOnly] public NativeArray<int> Flooded;
        // The costs over the tree, the route's table, and the costs the regions' leaves hold.
        [ReadOnly] public NativeList<byte> Cost;
        [ReadOnly] public NativeArray<byte> Table;
        [ReadOnly] public NativeArray<ulong> Present;
        public float3 From, To;
        // The search's own.
        public NativeArray<float> Sums;
        public NativeArray<int> Links;
        public NativeArray<float3> Points;
        public NativeArray<RouteResult> Result;

        public void Execute() {
            if (Flooded[0] == 0) {
                Result[0] = default;
                return;
            }
            var tree = new WallTree(Shape, Unit, Cap, Nodes.AsArray().AsReadOnlySpan(), Bricks.AsArray().AsReadOnlySpan());
            var least = Cost.Length == 0 ? (byte)0 : CellCost.Least(Present.AsReadOnlySpan(), Table.AsReadOnlySpan());
            Result[0] = TreeRoutes.Find(tree, Size, Shapes.Set, Region.AsArray().AsReadOnlySpan(), Cost.AsArray().AsReadOnlySpan(), From.x, From.y, From.z, To.x, To.y, To.z, Straighten,
                Sums.AsSpan(), Links.AsSpan(), Points.Reinterpret<float>(12).AsSpan(), least, Leeway, Table.AsReadOnlySpan());
        }
    }

    /// <summary>One sample of a travel field per point.</summary>
    [BurstCompile]
    internal struct SampleJob : IJobParallelFor {
        public GridShape Shape;
        public float Unit;
        // The regions the field was built from were no longer the grid's when this was scheduled.
        public bool Stale;
        [ReadOnly] public NativeArray<ushort> Distance;
        // The field's build: element 0 is 1 if it was built.
        [ReadOnly] public NativeArray<int> Result;
        [ReadOnly] public NativeArray<float3> Points;
        public NativeArray<float3> Directions;
        public NativeArray<float> Distances;

        public void Execute(int index) {
            var point = Points[index];
            float x = 0f, y = 0f, z = 0f, distance = 0f;
            var found = !Stale && Result[0] != 0 &&
                        Travel.Sample(new TravelField(Shape, Unit, Distance.AsReadOnlySpan()), point.x, point.y, point.z, out x, out y, out z, out distance);
            Directions[index] = found ? new float3(x, y, z) : float3.zero;
            Distances[index] = found ? distance : -1f;
        }
    }

    /// <summary>Builds a travel field over a grid's tree.</summary>
    [BurstCompile]
    internal struct TreeFieldJob : IJob {
        public GridShape Shape;
        public float Unit, FieldUnit;
        public byte Cap;
        [ReadOnly] public NativeList<WallNode> Nodes;
        [ReadOnly] public NativeList<byte> Bricks, Region;
        // The flood's result: element 0 is 1 if it was done.
        [ReadOnly] public NativeArray<int> Flooded;
        [ReadOnly] public NativeArray<TravelSeed> Seeds;
        // The costs over the tree, as the field keeps them, and the field's table.
        [ReadOnly] public NativeArray<byte> Cost, Table;
        // What TreeFields.Build writes, cut to its length: empty after a build that was refused.
        public NativeList<byte> Field;
        // Whether the build was done (1 or 0), how many places it reached, and how many it cut.
        public NativeArray<int> Result;

        public void Execute() {
            Result[0] = 0; Result[1] = 0; Result[2] = 0;
            Field.Clear();
            if (Flooded[0] == 0) { return; }
            var tree = new WallTree(Shape, Unit, Cap, Nodes.AsArray().AsReadOnlySpan(), Bricks.AsArray().AsReadOnlySpan());
            var region = Region.AsArray().AsReadOnlySpan();
            if (!TreeFields.Measure(tree, region, Seeds.AsReadOnlySpan(), out var length, out var places, out var work)) { return; }
            Field.ResizeUninitialized(length);
            var sums = new NativeArray<float>(places, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var ints = new NativeArray<int>(work, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var done = TreeFields.Build(tree, region, Cost.AsReadOnlySpan(), Seeds.AsReadOnlySpan(), FieldUnit, Field.AsArray().AsSpan(), sums.AsSpan(), ints.AsSpan(),
                out var written, out var reached, out var cut, Table.AsReadOnlySpan());
            sums.Dispose();
            ints.Dispose();
            Field.ResizeUninitialized(done ? written : 0);
            Result[0] = done ? 1 : 0;
            Result[1] = reached;
            Result[2] = cut;
        }
    }

    /// <summary>Asks a field over a grid's tree, or over counts, for many points: the travel at each, and the point to fly to.</summary>
    [BurstCompile]
    internal struct FieldAskJob : IJobParallelFor {
        public GridShape Shape;
        public float Unit, FieldUnit;
        public byte Cap;
        // What to ask: 0 the travel and the direction, 1 the travel and the place, 2 the point to fly to.
        public int Ask;
        public bool Tree, Stale;
        [ReadOnly] public NativeList<WallNode> Nodes;
        [ReadOnly] public NativeList<byte> Bricks, Region, Field;
        [ReadOnly] public NativeArray<TravelSeed> Seeds;
        // Over a tree: the costs the field was built with, and its table.
        [ReadOnly] public NativeArray<byte> Cost, Table;
        [ReadOnly] public NativeArray<ushort> Distance;
        // The field's build: element 0 is 1 if it was built.
        [ReadOnly] public NativeArray<int> Result;
        [ReadOnly] public NativeArray<float3> Points;
        // The direction, or the point to fly to; the travel, -1 where refused; and the place.
        public NativeArray<float3> Vectors;
        public NativeArray<float> Travels;
        public NativeArray<int> Places;

        public void Execute(int index) {
            var point = Points[index];
            var found = false;
            var vector = float3.zero;
            float travel = 0f;
            var place = -1;
            if (!Stale && Result[0] != 0) {
                if (Tree) {
                    var field = new TreeField(new WallTree(Shape, Unit, Cap, Nodes.AsArray().AsReadOnlySpan(), Bricks.AsArray().AsReadOnlySpan()),
                        Region.AsArray().AsReadOnlySpan(), Field.AsArray().AsReadOnlySpan(), Seeds.AsReadOnlySpan(), FieldUnit, Cost.AsReadOnlySpan(), Table.AsReadOnlySpan());
                    found = WallGridField.AskTree(field, Ask, point, out vector, out travel, out place);
                } else {
                    found = WallGridField.AskCounts(new TravelField(Shape, FieldUnit, Distance.AsReadOnlySpan()), Ask, point, out vector, out travel, out place);
                }
            }
            if (Vectors.Length > index) { Vectors[index] = found ? vector : float3.zero; }
            Travels[index] = found ? travel : -1f;
            if (Places.Length > index) { Places[index] = found ? place : -1; }
        }
    }

    /// <summary>Builds the wall distance of a large grid as a tree.</summary>
    [BurstCompile]
    internal struct TreeJob : IJob {
        public GridShape Shape;
        public ShapeArrays Shapes;
        public float Unit;
        public byte Cap;
        public NativeList<WallNode> Nodes;
        public NativeList<byte> Bricks;
        // One element: 1 if the tree was built, 0 if the build was refused.
        public NativeArray<byte> Filled;

        public void Execute() {
            // A build that is refused leaves no tree: nothing is proven.
            Filled[0] = 0;
            Nodes.Clear();
            Bricks.Clear();
            var set = Shapes.Set;
            if (!WallTrees.Measure(Shape, set, Unit, Cap, out var nodes, out var bricks)) { return; }
            Nodes.ResizeUninitialized(nodes);
            Bricks.ResizeUninitialized(bricks * WallTrees.BrickBytes);
            if (!WallTrees.Build(Shape, set, Unit, Cap, Nodes.AsArray().AsSpan(), Bricks.AsArray().AsSpan(), out var nodeCount, out var brickCount)) {
                Nodes.Clear();
                Bricks.Clear();
                return;
            }
            // Measuring gives room for every brick that might be kept; what was not needed is given back.
            Nodes.ResizeUninitialized(nodeCount);
            Bricks.ResizeUninitialized(brickCount * WallTrees.BrickBytes);
            Nodes.TrimExcess();
            Bricks.TrimExcess();
            Filled[0] = 1;
        }
    }

    /// <summary>Fills one z layer of the wall distance per index.</summary>
    [BurstCompile]
    internal struct MarkJob : IJobParallelFor {
        public GridShape Shape;
        public ShapeArrays Shapes;
        public float Unit;
        public byte Cap;

        // Layer z writes only wall[z * layer .. (z + 1) * layer), so no two layers write the same place.
        [NativeDisableParallelForRestriction] public NativeArray<byte> Wall;
        // 1 for each layer that was filled, 0 for one that was refused.
        public NativeArray<byte> Filled;

        public void Execute(int z) {
            var length = Shape.LayerLength;
            var layer = Wall.AsSpan().Slice(z * length, length);
            Filled[z] = WallDistance.MarkLayer(Shape, Shapes.Set, Unit, Cap, z, layer) ? (byte)1 : (byte)0;
        }
    }
}
