using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Yewnyx.Spatial {
    /// <summary>The shapes a <see cref="WallGrid"/> owns, as a job reads them. Nothing here is written by a job.</summary>
    internal struct ShapeArrays {
        [ReadOnly] public NativeArray<Solid> Solids;
        [ReadOnly] public NativeArray<float> Corners;
        [ReadOnly] public NativeArray<int> Triangles;
        [ReadOnly] public NativeArray<Box> Boxes;
        [ReadOnly] public NativeArray<BoxNode> Nodes;
        [ReadOnly] public NativeArray<int> Order;

        public ShapeSet Set => new ShapeSet(Solids.AsReadOnlySpan(), Corners.AsReadOnlySpan(), Triangles.AsReadOnlySpan(),
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
        // Whether the flood was done (1 or 0), how many cells it reached, and how many seeds held no open cell.
        public NativeArray<int> Result;

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
