using System;

namespace Yewnyx.Spatial {
    /// <summary>The distance from a point to one shape, and the box around a shape.</summary>
    /// <remarks>
    /// Distances are worked out in double and rounded once by the caller. A triangle's distance goes
    /// through products of its edges, which single precision cannot hold for a long thin triangle.
    /// A shape that holds a number that is not finite, or a solid of a kind that is not known, never
    /// reads as farther than it is: damaged data must not read as room. A solid of an unknown kind
    /// or with a centre that is not finite is at distance 0 from everything, and a box with one bad
    /// axis or half extent reaches without end along that axis. The tree may still pass such a shape
    /// by where its stored box is far from the point.
    /// </remarks>
    internal static class ShapeDistance {
        /// <summary>The squared distance from a point to a solid, 0 inside it.</summary>
        public static double SolidSquared(in Solid solid, double x, double y, double z) {
            double px = x - solid.X, py = y - solid.Y, pz = z - solid.Z;
            double result;
            switch (solid.Kind) {
                case SolidKind.Box: {
                    // The point in the box's own axes, then how far it lies beyond each pair of faces.
                    // An axis is at most a hair off length 1, and that hair times a large box is
                    // centimetres, so each is brought to length 1 first: for a squared length of
                    // 1 + e, the factor 1.5 - 0.5 * (1 + e) is 1 / sqrt(1 + e) to within e * e.
                    var du = Math.Abs((px * solid.UX + py * solid.UY + pz * solid.UZ) * _toUnit(solid.UX, solid.UY, solid.UZ)) - solid.HalfU;
                    var dv = Math.Abs((px * solid.VX + py * solid.VY + pz * solid.VZ) * _toUnit(solid.VX, solid.VY, solid.VZ)) - solid.HalfV;
                    var dw = Math.Abs((px * solid.WX + py * solid.WY + pz * solid.WZ) * _toUnit(solid.WX, solid.WY, solid.WZ)) - solid.HalfW;
                    // A comparison with not-a-number is false, so such an axis adds nothing.
                    du = du > 0d ? du : 0d; dv = dv > 0d ? dv : 0d; dw = dw > 0d ? dw : 0d;
                    result = du * du + dv * dv + dw * dw;
                    break;
                }
                case SolidKind.Sphere: {
                    var beyond = Math.Sqrt(px * px + py * py + pz * pz) - solid.HalfU;
                    result = beyond > 0d ? beyond * beyond : 0d;
                    break;
                }
                case SolidKind.Capsule: {
                    var beyond = Math.Sqrt(SegmentSquared(solid.X, solid.Y, solid.Z, solid.UX, solid.UY, solid.UZ, x, y, z)) - solid.HalfU;
                    result = beyond > 0d ? beyond * beyond : 0d;
                    break;
                }
                default:
                    return 0d;
            }
            // A result that is not finite comes from a solid that is not: it touches.
            return result > 0d && result - result == 0d ? result : 0d;
        }

        private static double _toUnit(double x, double y, double z) => 1.5d - 0.5d * (x * x + y * y + z * z);

        /// <summary>The squared distance from a point to the segment between a and b.</summary>
        public static double SegmentSquared(double ax, double ay, double az, double bx, double by, double bz, double x, double y, double z) {
            double abx = bx - ax, aby = by - ay, abz = bz - az;
            double apx = x - ax, apy = y - ay, apz = z - az;
            var length = abx * abx + aby * aby + abz * abz;
            // How far along the segment the nearest point lies, from 0 at a to 1 at b.
            var along = length > 0d ? (apx * abx + apy * aby + apz * abz) / length : 0d;
            along = along > 0d ? (along < 1d ? along : 1d) : 0d;
            double dx = apx - along * abx, dy = apy - along * aby, dz = apz - along * abz;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>The squared distance from a point to the triangle a, b, c. The order of the corners does not matter.</summary>
        public static double TriangleSquared(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz,
            double x, double y, double z) {
            double abx = bx - ax, aby = by - ay, abz = bz - az;
            double acx = cx - ax, acy = cy - ay, acz = cz - az;
            double apx = x - ax, apy = y - ay, apz = z - az;
            var sum = abx + aby + abz + acx + acy + acz + apx + apy + apz;
            if (!(sum - sum == 0d)) { return 0d; }

            double nx = aby * acz - abz * acy, ny = abz * acx - abx * acz, nz = abx * acy - aby * acx;
            var area = nx * nx + ny * ny + nz * nz;
            if (area > 0d) {
                // The point lies over the triangle's face if it is on the inner side of all three edges.
                // Then the plane is the nearest part. If rounding says "over the face" for a point just
                // outside an edge, the plane's distance is still no more than the true one.
                double bpx = x - bx, bpy = y - by, bpz = z - bz, bcx = cx - bx, bcy = cy - by, bcz = cz - bz;
                double cpx = x - cx, cpy = y - cy, cpz = z - cz;
                var sideAb = nx * (aby * apz - abz * apy) + ny * (abz * apx - abx * apz) + nz * (abx * apy - aby * apx);
                var sideBc = nx * (bcy * bpz - bcz * bpy) + ny * (bcz * bpx - bcx * bpz) + nz * (bcx * bpy - bcy * bpx);
                var sideCa = nx * (acz * cpy - acy * cpz) + ny * (acx * cpz - acz * cpx) + nz * (acy * cpx - acx * cpy);
                if (sideAb >= 0d && sideBc >= 0d && sideCa >= 0d) {
                    var height = nx * apx + ny * apy + nz * apz;
                    return height * height / area;
                }
            }
            // Otherwise an edge or a corner is nearest. A triangle with no area is only its edges.
            var nearest = SegmentSquared(ax, ay, az, bx, by, bz, x, y, z);
            var other = SegmentSquared(bx, by, bz, cx, cy, cz, x, y, z);
            if (other < nearest) { nearest = other; }
            other = SegmentSquared(cx, cy, cz, ax, ay, az, x, y, z);
            if (other < nearest) { nearest = other; }
            return nearest > 0d ? nearest : 0d;
        }

        /// <summary>
        /// The box around a solid, widened by <see cref="Shapes.Pad"/>. Returns false for a solid that
        /// may not be stored: an unknown kind, a number that is not finite, a negative size, a box
        /// whose axes are not of length 1 and square to each other, or one that reaches beyond
        /// <see cref="Shapes.Reach"/>.
        /// </summary>
        /// <remarks>
        /// Add, subtract, multiply and compare only, rounded to single at each step, so the same solid
        /// gives the same box on every runtime.
        /// A box is the space between three pairs of planes, each pair square to one axis as given.
        /// When the axes are a little off square to each other, or off length 1, its corners are not
        /// quite where exact axes would put them: by no more than how far off they are, times the
        /// sum of the half extents. The stored box is widened by twice that, so it still holds them.
        /// </remarks>
        public static bool TryBox(in Solid solid, out Box box) {
            box = default;
            float reachX, reachY, reachZ;
            float minX = solid.X, minY = solid.Y, minZ = solid.Z, maxX = solid.X, maxY = solid.Y, maxZ = solid.Z;
            switch (solid.Kind) {
                case SolidKind.Box:
                    if (!(solid.HalfU >= 0f && solid.HalfV >= 0f && solid.HalfW >= 0f)) { return false; }
                    // How far each axis is from length 1, and each pair from square: the largest of the six.
                    var off = _larger(_larger(_offUnit(solid.UX, solid.UY, solid.UZ), _offUnit(solid.VX, solid.VY, solid.VZ)), _offUnit(solid.WX, solid.WY, solid.WZ));
                    off = _larger(off, _offSquare(solid.UX, solid.UY, solid.UZ, solid.VX, solid.VY, solid.VZ));
                    off = _larger(off, _offSquare(solid.VX, solid.VY, solid.VZ, solid.WX, solid.WY, solid.WZ));
                    off = _larger(off, _offSquare(solid.WX, solid.WY, solid.WZ, solid.UX, solid.UY, solid.UZ));
                    // A comparison with not-a-number is false, so such an axis is refused.
                    if (!(off <= AxisSlack)) { return false; }
                    var wider = (float)((float)(2f * off) * (float)((float)(solid.HalfU + solid.HalfV) + solid.HalfW));
                    reachX = (float)(_reach(solid.UX, solid.VX, solid.WX, solid.HalfU, solid.HalfV, solid.HalfW) + wider);
                    reachY = (float)(_reach(solid.UY, solid.VY, solid.WY, solid.HalfU, solid.HalfV, solid.HalfW) + wider);
                    reachZ = (float)(_reach(solid.UZ, solid.VZ, solid.WZ, solid.HalfU, solid.HalfV, solid.HalfW) + wider);
                    break;
                case SolidKind.Sphere:
                    if (!(solid.HalfU >= 0f)) { return false; }
                    reachX = solid.HalfU; reachY = solid.HalfU; reachZ = solid.HalfU;
                    break;
                case SolidKind.Capsule:
                    if (!(solid.HalfU >= 0f)) { return false; }
                    if (solid.UX < minX) { minX = solid.UX; } else { maxX = solid.UX; }
                    if (solid.UY < minY) { minY = solid.UY; } else { maxY = solid.UY; }
                    if (solid.UZ < minZ) { minZ = solid.UZ; } else { maxZ = solid.UZ; }
                    reachX = solid.HalfU; reachY = solid.HalfU; reachZ = solid.HalfU;
                    break;
                default:
                    return false;
            }
            box = new Box((float)((float)(minX - reachX) - Shapes.Pad), (float)((float)(minY - reachY) - Shapes.Pad), (float)((float)(minZ - reachZ) - Shapes.Pad),
                (float)((float)(maxX + reachX) + Shapes.Pad), (float)((float)(maxY + reachY) + Shapes.Pad), (float)((float)(maxZ + reachZ) + Shapes.Pad));
            return Within(box);
        }

        /// <summary>The box around a triangle, widened by <see cref="Shapes.Pad"/>. Returns false if it reaches beyond <see cref="Shapes.Reach"/> or a corner is not finite.</summary>
        public static bool TryBox(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, out Box box) {
            float minX = ax < bx ? ax : bx, minY = ay < by ? ay : by, minZ = az < bz ? az : bz;
            float maxX = ax > bx ? ax : bx, maxY = ay > by ? ay : by, maxZ = az > bz ? az : bz;
            minX = cx < minX ? cx : minX; minY = cy < minY ? cy : minY; minZ = cz < minZ ? cz : minZ;
            maxX = cx > maxX ? cx : maxX; maxY = cy > maxY ? cy : maxY; maxZ = cz > maxZ ? cz : maxZ;
            box = new Box((float)(minX - Shapes.Pad), (float)(minY - Shapes.Pad), (float)(minZ - Shapes.Pad),
                (float)(maxX + Shapes.Pad), (float)(maxY + Shapes.Pad), (float)(maxZ + Shapes.Pad));
            // A corner that is not a number loses every comparison above, so each is tested by itself.
            return Within(box) && _finite(ax) && _finite(ay) && _finite(az) && _finite(bx) && _finite(by) && _finite(bz) &&
                   _finite(cx) && _finite(cy) && _finite(cz);
        }

        // Inside the reach on every side. False for a number that is not finite.
        public static bool Within(in Box box) =>
            box.MinX >= -Shapes.Reach && box.MinY >= -Shapes.Reach && box.MinZ >= -Shapes.Reach &&
            box.MaxX <= Shapes.Reach && box.MaxY <= Shapes.Reach && box.MaxZ <= Shapes.Reach &&
            box.MinX <= box.MaxX && box.MinY <= box.MaxY && box.MinZ <= box.MaxZ;

        private static bool _finite(float value) => value >= -Shapes.Reach && value <= Shapes.Reach;

        // How far a box reaches along one of the frame's axes: its half extents, each scaled by how
        // much of its axis points that way.
        private static float _reach(float u, float v, float w, float halfU, float halfV, float halfW) =>
            (float)((float)((float)((u < 0f ? -u : u) * halfU) + (float)((v < 0f ? -v : v) * halfV)) + (float)((w < 0f ? -w : w) * halfW));

        // How far an axis's squared length is from 1. Not a number for an axis that holds one.
        private static float _offUnit(float x, float y, float z) {
            var off = (float)((float)((float)((float)(x * x) + (float)(y * y)) + (float)(z * z)) - 1f);
            return off < 0f ? -off : off;
        }

        // How far two axes are from square to each other: 0 when they are.
        private static float _offSquare(float ax, float ay, float az, float bx, float by, float bz) {
            var dot = (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
            return dot < 0f ? -dot : dot;
        }

        // The larger of two, or not a number if either is.
        private static float _larger(float a, float b) => a > b ? a : b >= a ? b : float.NaN;

        // How far a box's axes may be from length 1 and from square. It refuses axes that carry a
        // scale or a shear; axes worked out in single precision are a hundred times inside it. At
        // its edge a box's faces are 15 millionths of its size from where exact axes would put them.
        private const float AxisSlack = 1f / 65536f;
    }
}
