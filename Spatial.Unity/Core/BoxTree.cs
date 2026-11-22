using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// One node of a <see cref="BoxTree"/>: the box around everything beneath it.
    /// </summary>
    /// <remarks>
    /// A leaf (Count above 0) holds order[First .. First + Count). Any other node has its left child
    /// at its own index + 1 and its right child at First. A child's index is always above its parent's.
    /// </remarks>
    public struct BoxNode {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int First;
        public int Count;
    }

    /// <summary>
    /// A tree of boxes around boxes, rebuilt whole when the boxes change. Queries walk only the
    /// branches near the point: about O(log n) each for boxes that are spread out.
    /// </summary>
    /// <remarks>
    /// Boxes are sorted along a Morton curve and split at the middle, O(n) after the sort. Nothing
    /// is allocated and nothing throws. The tree and the results depend only on the inputs.
    /// A query that is handed spans that don't belong together, or nodes that are damaged, reads
    /// and writes nothing out of bounds, ends within one visit per node, and returns -1 where it can
    /// tell. Nothing is to be read from the outputs of a query that returned -1.
    /// A not-a-number coordinate leaves that side of its box open, exactly as in <see cref="BoxScan"/>.
    /// </remarks>
    public static class BoxTree {
        private const int LeafSize = 4;
        private const int BuildStack = 3 * 40;
        private const int QueryStack = 64;

        /// <summary>Nodes needed for a tree over this many boxes.</summary>
        public static int NodesLength(int count) => count;

        /// <summary>Workspace needed to build a tree over this many boxes.</summary>
        public static int WorkspaceLength(int count) => 3 * count + 256 + BuildStack;

        /// <summary>
        /// Builds the tree. <paramref name="order"/> receives the boxes' indices in tree order and
        /// <paramref name="nodeCount"/> how many nodes were written. Returns false, writing nothing, if
        /// a buffer is too short.
        /// </summary>
        /// <remarks>
        /// <paramref name="nodes"/> needs <see cref="NodesLength"/> elements, order one per box and
        /// <paramref name="workspace"/> <see cref="WorkspaceLength"/>. <paramref name="extent"/> is the
        /// region the boxes lie in; one outside it is sorted as if at its edge, which costs speed, not
        /// correctness. Queries take nodes cut to nodeCount and order cut to the number of boxes.
        /// </remarks>
        public static bool Build(ReadOnlySpan<Box> boxes, in Box extent, Span<BoxNode> nodes, Span<int> order,
            Span<int> workspace, out int nodeCount) {
            var n = boxes.Length;
            nodeCount = 0;
            if (nodes.Length < NodesLength(n) || order.Length < n || workspace.Length < WorkspaceLength(n)) { return false; }
            if (n == 0) { return true; }

            // Workspace: keys, then a second copy of keys and of order for the sort, a histogram, a stack.
            Span<int> keys = workspace.Slice(0, n), otherKeys = workspace.Slice(n, n), otherOrder = workspace.Slice(2 * n, n);
            Span<int> histogram = workspace.Slice(3 * n, 256), stack = workspace.Slice(3 * n + 256, BuildStack);
            var sorted = order.Slice(0, n);

            float scaleX = _scale(extent.MinX, extent.MaxX), scaleY = _scale(extent.MinY, extent.MaxY), scaleZ = _scale(extent.MinZ, extent.MaxZ);
            for (var i = 0; i < n; i++) {
                var box = boxes[i];
                var cellX = _cell(_scaled(box.MinX, box.MaxX, extent.MinX, scaleX));
                var cellY = _cell(_scaled(box.MinY, box.MaxY, extent.MinY, scaleY));
                var cellZ = _cell(_scaled(box.MinZ, box.MaxZ, extent.MinZ, scaleZ));
                keys[i] = _spread(cellX) | (_spread(cellY) << 1) | (_spread(cellZ) << 2);
                sorted[i] = i;
            }

            // Four passes of 8 bits sort the 30-bit keys. Boxes with equal keys keep their order,
            // and an even number of passes leaves the result in keys and sorted.
            _pass(keys, sorted, otherKeys, otherOrder, histogram, 0);
            _pass(otherKeys, otherOrder, keys, sorted, histogram, 8);
            _pass(keys, sorted, otherKeys, otherOrder, histogram, 16);
            _pass(otherKeys, otherOrder, keys, sorted, histogram, 24);

            // Split each range at its middle. Nodes are numbered as they are met going down the
            // left first, so a left child follows its parent directly. The stack holds, per pending
            // range, its two ends and the parent whose right child it is (-1 for a left child).
            int top = 0, count = 0;
            stack[top++] = 0; stack[top++] = n; stack[top++] = -1;
            while (top > 0) {
                int parent = stack[--top], end = stack[--top], start = stack[--top];
                var index = count++;
                if (parent >= 0) { nodes[parent].First = index; }
                if (end - start <= LeafSize) {
                    nodes[index].First = start;
                    nodes[index].Count = end - start;
                    continue;
                }
                var middle = start + ((end - start) >> 1);
                nodes[index].First = 0;
                nodes[index].Count = 0;
                stack[top++] = middle; stack[top++] = end; stack[top++] = index;
                stack[top++] = start; stack[top++] = middle; stack[top++] = -1;
            }

            // Children come after their parent, so one pass backwards fits every node's box.
            for (var i = count - 1; i >= 0; i--) {
                ref var node = ref nodes[i];
                if (node.Count > 0) {
                    // A not-a-number coordinate leaves that side of the box open, and so that side of the node.
                    float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
                    float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
                    for (var j = 0; j < node.Count; j++) {
                        var box = boxes[sorted[node.First + j]];
                        minX = _lower(minX, box.MinX); minY = _lower(minY, box.MinY); minZ = _lower(minZ, box.MinZ);
                        maxX = _upper(maxX, box.MaxX); maxY = _upper(maxY, box.MaxY); maxZ = _upper(maxZ, box.MaxZ);
                    }
                    node.MinX = minX; node.MinY = minY; node.MinZ = minZ; node.MaxX = maxX; node.MaxY = maxY; node.MaxZ = maxZ;
                } else {
                    BoxNode left = nodes[i + 1], right = nodes[node.First];
                    node.MinX = left.MinX < right.MinX ? left.MinX : right.MinX;
                    node.MinY = left.MinY < right.MinY ? left.MinY : right.MinY;
                    node.MinZ = left.MinZ < right.MinZ ? left.MinZ : right.MinZ;
                    node.MaxX = left.MaxX > right.MaxX ? left.MaxX : right.MaxX;
                    node.MaxY = left.MaxY > right.MaxY ? left.MaxY : right.MaxY;
                    node.MaxZ = left.MaxZ > right.MaxZ ? left.MaxZ : right.MaxZ;
                }
            }
            nodeCount = count;
            return true;
        }

        /// <summary>
        /// Writes the indices of the boxes within <paramref name="radius"/> of the point, in tree order,
        /// and returns how many were written (never more than results holds).
        /// </summary>
        /// <remarks>
        /// <paramref name="total"/> is how many matched, so a total above the return value means
        /// <paramref name="results"/> was too short for them all. The same boxes match as with
        /// <see cref="BoxScan.InSphere"/>, in a different order. Returns -1, with total -1, if order and
        /// boxes differ in length or the walk finds the nodes damaged.
        /// </remarks>
        public static int InSphere(ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order, ReadOnlySpan<Box> boxes,
            float x, float y, float z, float radius, Span<int> results, out int total) {
            total = -1;
            if (order.Length != boxes.Length || (nodes.Length == 0) != (boxes.Length == 0)) { return -1; }
            total = 0;
            if (!(radius >= 0f) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || nodes.Length == 0) { return 0; }

            Span<int> stack = stackalloc int[QueryStack];
            var limit = (float)(radius * radius);
            int top = 0, written = 0, matched = 0, visits = 0;
            stack[top++] = 0;
            while (top > 0) {
                // A tree is walked at most once per node; more means the nodes are not one.
                if (++visits > nodes.Length) { total = -1; return -1; }
                var index = stack[--top];
                var node = nodes[index];
                if (BoxDistance.Squared(node.MinX, node.MinY, node.MinZ, node.MaxX, node.MaxY, node.MaxZ, x, y, z) > limit) { continue; }
                if (node.Count <= 0) {
                    if (!_children(nodes.Length, index, node.First, top)) { total = -1; return -1; }
                    stack[top++] = node.First;
                    stack[top++] = index + 1;
                    continue;
                }
                if (!_leaf(order.Length, node.First, node.Count)) { total = -1; return -1; }
                for (var j = 0; j < node.Count; j++) {
                    var item = order[node.First + j];
                    if ((uint)item >= (uint)boxes.Length) { total = -1; return -1; }
                    var box = boxes[item];
                    if (BoxDistance.Squared(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ, x, y, z) <= limit) {
                        if (written < results.Length) { results[written++] = item; }
                        matched++;
                    }
                }
            }
            total = matched;
            return written;
        }

        /// <summary>
        /// Writes the nearest boxes within <paramref name="maxDistanceSquared"/> of the point, nearest
        /// first, with their squared distances, and returns how many were written.
        /// </summary>
        /// <remarks>
        /// As many are written as the shorter of <paramref name="results"/> and
        /// <paramref name="distancesSquared"/> holds. Of two boxes at the same distance the lower index
        /// comes first, so the result is exactly that of <see cref="BoxScan.Nearest"/>. Returns -1 if
        /// order and boxes differ in length or the walk finds the nodes damaged.
        /// </remarks>
        public static int Nearest(ReadOnlySpan<BoxNode> nodes, ReadOnlySpan<int> order, ReadOnlySpan<Box> boxes,
            float x, float y, float z, float maxDistanceSquared, Span<int> results, Span<float> distancesSquared) {
            if (order.Length != boxes.Length || (nodes.Length == 0) != (boxes.Length == 0)) { return -1; }
            var k = Math.Min(results.Length, distancesSquared.Length);
            if (k == 0 || nodes.Length == 0 || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return 0; }

            Span<int> stack = stackalloc int[QueryStack];
            int top = 0, count = 0, visits = 0;
            // What a box must beat to be kept: the limit until k are held, then the k-th held.
            var worst = maxDistanceSquared;
            stack[top++] = 0;
            while (top > 0) {
                if (++visits > nodes.Length) { return -1; }
                var index = stack[--top];
                var node = nodes[index];
                var reach = BoxDistance.Squared(node.MinX, node.MinY, node.MinZ, node.MaxX, node.MaxY, node.MaxZ, x, y, z);
                // A node exactly as far as the k-th held may still hold a lower index, so only a farther one is skipped.
                if (!(reach <= worst)) { continue; }
                if (node.Count <= 0) {
                    if (!_children(nodes.Length, index, node.First, top)) { return -1; }
                    // The nearer child is walked first, so the farther one is more often skipped.
                    BoxNode left = nodes[index + 1], right = nodes[node.First];
                    var leftReach = BoxDistance.Squared(left.MinX, left.MinY, left.MinZ, left.MaxX, left.MaxY, left.MaxZ, x, y, z);
                    var rightReach = BoxDistance.Squared(right.MinX, right.MinY, right.MinZ, right.MaxX, right.MaxY, right.MaxZ, x, y, z);
                    if (leftReach <= rightReach) {
                        stack[top++] = node.First;
                        stack[top++] = index + 1;
                    } else {
                        stack[top++] = index + 1;
                        stack[top++] = node.First;
                    }
                    continue;
                }
                if (!_leaf(order.Length, node.First, node.Count)) { return -1; }
                for (var j = 0; j < node.Count; j++) {
                    var item = order[node.First + j];
                    if ((uint)item >= (uint)boxes.Length) { return -1; }
                    var box = boxes[item];
                    var distance = BoxDistance.Squared(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ, x, y, z);
                    if (!(distance <= maxDistanceSquared)) { continue; }
                    if (count == k && (distance > worst || (distance == worst && item > results[k - 1]))) { continue; }

                    // Held results are in order of distance, then index.
                    var at = count < k ? count++ : k - 1;
                    while (at > 0 && (distancesSquared[at - 1] > distance || (distancesSquared[at - 1] == distance && results[at - 1] > item))) {
                        distancesSquared[at] = distancesSquared[at - 1];
                        results[at] = results[at - 1];
                        at--;
                    }
                    distancesSquared[at] = distance;
                    results[at] = item;
                    if (count == k) { worst = distancesSquared[k - 1]; }
                }
            }
            return count;
        }

        // A node's children must lie above it and inside the nodes, and two more must fit on the stack.
        private static bool _children(int nodeCount, int index, int right, int top) =>
            right > index + 1 && right < nodeCount && top + 2 <= QueryStack;

        // A leaf's items must lie inside order.
        private static bool _leaf(int orderLength, int first, int count) =>
            first >= 0 && count <= orderLength && first <= orderLength - count;

        private static float _lower(float bound, float value) => float.IsNaN(value) ? float.NegativeInfinity : value < bound ? value : bound;

        private static float _upper(float bound, float value) => float.IsNaN(value) ? float.PositiveInfinity : value > bound ? value : bound;

        // Each step is rounded to single, as in BoxDistance, so a runtime that carries wider values
        // between steps sorts the boxes into the same order, and so builds the same tree.
        private static float _scale(float min, float max) => max > min ? (float)(1024f / (float)(max - min)) : 0f;

        // Where a box's middle lies across the extent, from 0 to 1024.
        private static float _scaled(float min, float max, float extentMin, float scale) =>
            (float)((float)((float)((float)(min + max) * 0.5f) - extentMin) * scale);

        // Which of 1024 cells a scaled coordinate falls in. Anything out of range, or not a number, is put at an end.
        private static int _cell(float scaled) => scaled >= 0f ? (scaled < 1024f ? (int)scaled : 1023) : 0;

        // Spreads the low 10 bits so that two zero bits follow each.
        private static int _spread(int value) {
            var bits = (uint)value & 0x3FF;
            bits = (bits | (bits << 16)) & 0x030000FF;
            bits = (bits | (bits << 8)) & 0x0300F00F;
            bits = (bits | (bits << 4)) & 0x030C30C3;
            bits = (bits | (bits << 2)) & 0x09249249;
            return (int)bits;
        }

        // One pass of a radix sort on 8 bits of the keys, carrying the items along.
        private static void _pass(Span<int> keys, Span<int> items, Span<int> toKeys, Span<int> toItems, Span<int> histogram, int shift) {
            histogram.Clear();
            for (var i = 0; i < keys.Length; i++) { histogram[(keys[i] >> shift) & 0xFF]++; }
            var sum = 0;
            for (var i = 0; i < 256; i++) {
                var bucket = histogram[i];
                histogram[i] = sum;
                sum += bucket;
            }
            for (var i = 0; i < keys.Length; i++) {
                var key = keys[i];
                var at = histogram[(key >> shift) & 0xFF]++;
                toKeys[at] = key;
                toItems[at] = items[i];
            }
        }
    }
}
