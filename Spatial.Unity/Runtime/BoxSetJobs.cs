using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Yewnyx.Spatial {
    internal enum ChangeKind { Add, Remove, Move }

    /// <summary>One call to Add, Remove or Move, kept in the order the calls were made.</summary>
    internal struct Change {
        public int Id;
        public ChangeKind Kind;
    }

    /// <summary>Where an item is: one per Add and per Move, in the order of those changes.</summary>
    internal struct Pose {
        public float3 Position;
        public quaternion Rotation;
    }

    /// <summary>An item's box in its own frame: one per Add, in the order of those changes.</summary>
    internal struct Shape {
        public float3 Centre, HalfExtents;
    }

    /// <summary>What a query job reads of a <see cref="BoxSet"/>. Nothing here is written by a query.</summary>
    internal struct BoxSetView {
        [ReadOnly] public NativeArray<Box> Boxes;
        [ReadOnly] public NativeArray<float> MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        [ReadOnly] public NativeArray<int> Ids, Order, NodeCount, Count;
        [ReadOnly] public NativeArray<BoxNode> Nodes;

        public BoxColumns Columns {
            get {
                var count = Count[0];
                return new BoxColumns(
                    MinX.AsReadOnlySpan().Slice(0, count), MinY.AsReadOnlySpan().Slice(0, count), MinZ.AsReadOnlySpan().Slice(0, count),
                    MaxX.AsReadOnlySpan().Slice(0, count), MaxY.AsReadOnlySpan().Slice(0, count), MaxZ.AsReadOnlySpan().Slice(0, count));
            }
        }

        public ReadOnlySpan<BoxNode> TreeNodes => Nodes.AsReadOnlySpan().Slice(0, math.max(NodeCount[0], 0));
        public ReadOnlySpan<int> TreeOrder => Order.AsReadOnlySpan().Slice(0, Count[0]);
        public ReadOnlySpan<Box> LiveBoxes => Boxes.AsReadOnlySpan().Slice(0, Count[0]);
    }

    /// <summary>
    /// Puts the changes since the last update into effect, in the order they were made, then the poses
    /// handed over in arrays, and works out the box of every item added or moved.
    /// </summary>
    [BurstCompile]
    internal struct UpdateJob : IJob {
        [ReadOnly] public NativeArray<Change> Changes;
        [ReadOnly] public NativeArray<Pose> Poses;
        [ReadOnly] public NativeArray<Shape> Shapes;
        [ReadOnly] public NativeArray<int> MovedIds;
        [ReadOnly] public NativeArray<float3> MovedPositions;
        [ReadOnly] public NativeArray<quaternion> MovedRotations;
        public int Stamp;

        // Per slot. Slots 0 .. Count[0] - 1 are in use.
        public NativeArray<Box> Boxes;
        public NativeArray<float> MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public NativeArray<int> Ids, StampOfSlot, Count;
        public NativeArray<float3> Centres, HalfExtents;
        public NativeHashMap<int, int> SlotOfId;

        public void Execute() {
            var count = Count[0];
            int pose = 0, shape = 0, slot;
            for (var i = 0; i < Changes.Length; i++) {
                var change = Changes[i];
                switch (change.Kind) {
                    case ChangeKind.Add:
                        var at = Poses[pose++];
                        var box = Shapes[shape++];
                        // The set checked both when the item was added; an item is never half added.
                        if (count >= Boxes.Length || !SlotOfId.TryAdd(change.Id, count)) { break; }
                        Ids[count] = change.Id;
                        Centres[count] = box.Centre;
                        HalfExtents[count] = box.HalfExtents;
                        _place(count++, at.Position, at.Rotation);
                        break;
                    case ChangeKind.Move:
                        var to = Poses[pose++];
                        if (SlotOfId.TryGetValue(change.Id, out slot)) { _place(slot, to.Position, to.Rotation); }
                        break;
                    default:
                        if (!SlotOfId.TryGetValue(change.Id, out slot)) { break; }
                        SlotOfId.Remove(change.Id);
                        // The last item moves into the gap, with everything that is kept for it.
                        var last = --count;
                        if (slot == last) { break; }
                        Boxes[slot] = Boxes[last];
                        MinX[slot] = MinX[last]; MinY[slot] = MinY[last]; MinZ[slot] = MinZ[last];
                        MaxX[slot] = MaxX[last]; MaxY[slot] = MaxY[last]; MaxZ[slot] = MaxZ[last];
                        Ids[slot] = Ids[last];
                        Centres[slot] = Centres[last];
                        HalfExtents[slot] = HalfExtents[last];
                        StampOfSlot[slot] = StampOfSlot[last];
                        SlotOfId[Ids[slot]] = slot;
                        break;
                }
            }

            // Poses handed over in arrays. An id that is not in the set is passed over.
            for (var i = 0; i < MovedIds.Length; i++) {
                if (SlotOfId.TryGetValue(MovedIds[i], out slot)) { _place(slot, MovedPositions[i], MovedRotations[i]); }
            }
            Count[0] = count;
        }

        // Works out the box of the item in a slot at a pose, and marks the slot as worked out this time.
        private void _place(int slot, float3 position, quaternion turn) {
            var rotation = new float3x3(turn);
            var centre = position + math.mul(rotation, Centres[slot]);
            var half = HalfExtents[slot];
            var reach = math.abs(rotation.c0) * half.x + math.abs(rotation.c1) * half.y + math.abs(rotation.c2) * half.z;
            var min = centre - reach;
            var max = centre + reach;
            Boxes[slot] = new Box(min.x, min.y, min.z, max.x, max.y, max.z);
            MinX[slot] = min.x; MinY[slot] = min.y; MinZ[slot] = min.z;
            MaxX[slot] = max.x; MaxY[slot] = max.y; MaxZ[slot] = max.z;
            StampOfSlot[slot] = Stamp;
        }
    }

    /// <summary>Rebuilds the tree over the set's boxes.</summary>
    [BurstCompile]
    internal struct BuildJob : IJob {
        [ReadOnly] public NativeArray<Box> Boxes;
        [ReadOnly] public NativeArray<int> Count;
        public Box Extent;
        public NativeArray<BoxNode> Nodes;
        public NativeArray<int> Order, Workspace, NodeCount;

        public void Execute() {
            var built = BoxTree.Build(Boxes.AsReadOnlySpan().Slice(0, Count[0]), Extent, Nodes.AsSpan(), Order.AsSpan(), Workspace.AsSpan(), out var nodeCount);
            NodeCount[0] = built ? nodeCount : -1;
        }
    }

    /// <summary>One query per sphere: the ids of the items within it.</summary>
    [BurstCompile]
    internal struct InSphereJob : IJobParallelFor {
        public BoxSetView Set;
        [ReadOnly] public NativeArray<float4> Spheres;
        public int Stride;
        public bool Tree;

        // Query q writes only results[q * Stride .. (q + 1) * Stride), so no two queries write the same place.
        [NativeDisableParallelForRestriction] public NativeArray<int> Results;
        public NativeArray<int> Counts, Totals;

        public void Execute(int query) {
            var sphere = Spheres[query];
            var results = Results.AsSpan().Slice(query * Stride, Stride);
            int total;
            var written = Tree
                ? BoxTree.InSphere(Set.TreeNodes, Set.TreeOrder, Set.LiveBoxes, sphere.x, sphere.y, sphere.z, sphere.w, results, out total)
                : BoxScan.InSphere(Set.Columns, sphere.x, sphere.y, sphere.z, sphere.w, results, out total);
            for (var i = 0; i < written; i++) { results[i] = Set.Ids[results[i]]; }
            Counts[query] = written;
            Totals[query] = total;
        }
    }

    /// <summary>One query per point: the ids of the nearest items and their squared distances.</summary>
    [BurstCompile]
    internal struct NearestJob : IJobParallelFor {
        public BoxSetView Set;
        [ReadOnly] public NativeArray<float4> Points;
        public int Stride;
        public bool Tree;

        // Query q writes only [q * Stride .. (q + 1) * Stride) of each, so no two queries write the same place.
        [NativeDisableParallelForRestriction] public NativeArray<int> Results;
        [NativeDisableParallelForRestriction] public NativeArray<float> DistancesSquared;
        public NativeArray<int> Counts;

        public void Execute(int query) {
            var point = Points[query];
            var results = Results.AsSpan().Slice(query * Stride, Stride);
            var distances = DistancesSquared.AsSpan().Slice(query * Stride, Stride);
            // A distance below 0, or not a number, finds nothing.
            var limit = point.w >= 0f ? point.w * point.w : -1f;
            var written = Tree
                ? BoxTree.Nearest(Set.TreeNodes, Set.TreeOrder, Set.LiveBoxes, point.x, point.y, point.z, limit, results, distances)
                : BoxScan.Nearest(Set.Columns, point.x, point.y, point.z, limit, results, distances);
            for (var i = 0; i < written; i++) { results[i] = Set.Ids[results[i]]; }
            Counts[query] = written;
        }
    }
}
