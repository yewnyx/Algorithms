using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// Boxes held as six columns, one span per coordinate, so a pass over them can test several
    /// boxes per instruction. Box i is (MinX[i], MinY[i], MinZ[i]) to (MaxX[i], MaxY[i], MaxZ[i]).
    /// </summary>
    /// <remarks>
    /// Each span is cut to the number of boxes, not to the capacity of what it views.
    /// </remarks>
    public readonly ref struct BoxColumns {
        public readonly ReadOnlySpan<float> MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public BoxColumns(ReadOnlySpan<float> minX, ReadOnlySpan<float> minY, ReadOnlySpan<float> minZ,
            ReadOnlySpan<float> maxX, ReadOnlySpan<float> maxY, ReadOnlySpan<float> maxZ) {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        /// <summary>The number of boxes, or -1 if the six columns differ in length.</summary>
        public int Count {
            get {
                var count = MinX.Length;
                return MinY.Length == count && MinZ.Length == count &&
                       MaxX.Length == count && MaxY.Length == count && MaxZ.Length == count ? count : -1;
            }
        }
    }
}
