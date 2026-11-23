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
