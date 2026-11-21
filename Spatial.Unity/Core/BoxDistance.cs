using System.Runtime.CompilerServices;

namespace Yewnyx.Spatial {
    /// <summary>The one distance every query uses, so the scan and the tree agree on ties.</summary>
    internal static class BoxDistance {
        /// <summary>The squared distance from a point to a box (0 inside it).</summary>
        /// <remarks>
        /// A not-a-number coordinate leaves that side of the box open: the box reaches without limit
        /// that way, and the side adds nothing to the distance.
        /// Each step is cast to float because some runtimes (Mono) otherwise carry wider values
        /// between steps, and would then round differently from the others in the last bit.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Squared(float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
            float x, float y, float z) {
            float belowX = (float)(minX - x), aboveX = (float)(x - maxX);
            float belowY = (float)(minY - y), aboveY = (float)(y - maxY);
            float belowZ = (float)(minZ - z), aboveZ = (float)(z - maxZ);
            // A comparison with not-a-number is false, so such a side never takes part.
            var dx = belowX > 0f ? belowX : 0f; dx = aboveX > dx ? aboveX : dx;
            var dy = belowY > 0f ? belowY : 0f; dy = aboveY > dy ? aboveY : dy;
            var dz = belowZ > 0f ? belowZ : 0f; dz = aboveZ > dz ? aboveZ : dz;
            return (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
        }
    }
}
