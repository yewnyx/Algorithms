using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Queries that test every box, in passes that have no branches, so they can test several boxes
    /// per instruction. There is nothing to build or keep current. InSphere is O(n). Nearest is O(n)
    /// plus the cost of keeping its results in order, at worst O(n * k) for k results when the boxes
    /// come farthest first, so it suits a small k.
    /// </summary>
    /// <remarks>
    /// Nothing is allocated and nothing throws. Results are the boxes' indices and depend only on
    /// the inputs. A query whose columns differ in length writes nothing and returns -1.
    /// A not-a-number coordinate leaves that side of its box open: the box reaches without limit that way.
    /// A negative or not-a-number radius, or a not-a-number point, matches nothing.
    /// Distances are rounded to single precision at every step, so a runtime that carries wider
    /// values between steps gives the same results as one that does not.
    /// </remarks>
    public static class BoxScan {
        private const int Block = 128;

        /// <summary>
        /// Writes the boxes into six columns. Returns false, writing nothing, if a column is shorter
        /// than <paramref name="boxes"/>.
        /// </summary>
        public static bool Pack(ReadOnlySpan<Box> boxes, Span<float> minX, Span<float> minY, Span<float> minZ,
            Span<float> maxX, Span<float> maxY, Span<float> maxZ) {
            var n = boxes.Length;
            if (minX.Length < n || minY.Length < n || minZ.Length < n ||
                maxX.Length < n || maxY.Length < n || maxZ.Length < n) { return false; }
            for (var i = 0; i < n; i++) {
                var box = boxes[i];
                minX[i] = box.MinX; minY[i] = box.MinY; minZ[i] = box.MinZ;
                maxX[i] = box.MaxX; maxY[i] = box.MaxY; maxZ[i] = box.MaxZ;
            }
            return true;
        }

        /// <summary>
        /// Writes the indices of the boxes within <paramref name="radius"/> of the point, in ascending
        /// order, and returns how many were written (never more than results holds).
        /// </summary>
        /// <remarks>
        /// <paramref name="total"/> is how many matched, so a total above the return value means
        /// <paramref name="results"/> was too short for them all. A box that only touches the sphere matches.
        /// Returns -1, with total -1 and nothing written, if the columns differ in length.
        /// </remarks>
        public static int InSphere(in BoxColumns boxes, float x, float y, float z, float radius,
            Span<int> results, out int total) {
            var n = boxes.Count;
            if (n < 0) { total = -1; return -1; }
            total = 0;
            if (!(radius >= 0f) || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return 0; }

            Span<float> distances = stackalloc float[Block];
            var limit = (float)(radius * radius);
            var written = 0;
            for (var start = 0; start < n; start += Block) {
                var length = Math.Min(Block, n - start);
                _distances(boxes, start, length, x, y, z, distances);
                for (var i = 0; i < length; i++) {
                    if (distances[i] <= limit) {
                        if (written < results.Length) { results[written++] = start + i; }
                        total++;
                    }
                }
            }
            return written;
        }

        /// <summary>
        /// Writes the nearest boxes within <paramref name="maxDistanceSquared"/> of the point, nearest
        /// first, with their squared distances, and returns how many were written.
        /// </summary>
        /// <remarks>
        /// As many are written as the shorter of <paramref name="results"/> and
        /// <paramref name="distancesSquared"/> holds. Of two boxes at the same distance the lower index
        /// comes first. Returns -1, writing nothing, if the columns differ in length.
        /// </remarks>
        public static int Nearest(in BoxColumns boxes, float x, float y, float z, float maxDistanceSquared,
            Span<int> results, Span<float> distancesSquared) {
            var n = boxes.Count;
            if (n < 0) { return -1; }
            var k = Math.Min(results.Length, distancesSquared.Length);
            if (k == 0 || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { return 0; }

            Span<float> distances = stackalloc float[Block];
            var count = 0;
            // The distance a box must beat to be kept: the limit until k are held, then the k-th.
            var worst = maxDistanceSquared;
            for (var start = 0; start < n; start += Block) {
                var length = Math.Min(Block, n - start);
                _distances(boxes, start, length, x, y, z, distances);
                for (var i = 0; i < length; i++) {
                    var distance = distances[i];
                    if (!(distance <= maxDistanceSquared)) { continue; }
                    if (distance > worst || (count == k && distance == worst)) { continue; }

                    var at = count < k ? count++ : k - 1;
                    while (at > 0 && distancesSquared[at - 1] > distance) {
                        distancesSquared[at] = distancesSquared[at - 1];
                        results[at] = results[at - 1];
                        at--;
                    }
                    distancesSquared[at] = distance;
                    results[at] = start + i;
                    if (count == k) { worst = distancesSquared[k - 1]; }
                }
            }
            return count;
        }

        // The squared distance from the point to each of a block of boxes. No branches.
        private static void _distances(in BoxColumns boxes, int start, int length, float x, float y, float z,
            Span<float> distances) {
            ReadOnlySpan<float> minX = boxes.MinX.Slice(start, length), minY = boxes.MinY.Slice(start, length);
            ReadOnlySpan<float> minZ = boxes.MinZ.Slice(start, length), maxX = boxes.MaxX.Slice(start, length);
            ReadOnlySpan<float> maxY = boxes.MaxY.Slice(start, length), maxZ = boxes.MaxZ.Slice(start, length);
            for (var i = 0; i < length; i++) {
                distances[i] = BoxDistance.Squared(minX[i], minY[i], minZ[i], maxX[i], maxY[i], maxZ[i], x, y, z);
            }
        }
    }
}
