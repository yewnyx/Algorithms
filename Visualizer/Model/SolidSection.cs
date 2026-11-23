using Yewnyx.Spatial;

namespace Yewnyx.Algorithms.Visualizer.Model;

/// <summary>
/// Where a plane cuts a <see cref="Solid"/>, as a closed outline, worked out from the solid's public fields. The plane has
/// one coordinate fixed: z for the x-by-y slice, y for the x-by-z slice. Outlines are in (u, v) metres, u being x and v
/// being y or z. A sphere gives a circle, a box the polygon its edges cross the plane in, a capsule a convex outline
/// found by walking out along rays from a point inside.
/// </summary>
public static class SolidSection {
    private const int Steps = 64;

    public static (double U, double V)[]? Cut(in Solid solid, SlicePlane plane, double fixedAt) => solid.Kind switch {
        SolidKind.Sphere => Sphere(solid, plane, fixedAt),
        SolidKind.Box => Box(solid, plane, fixedAt),
        SolidKind.Capsule => Capsule(solid, plane, fixedAt),
        _ => null,
    };

    // A point's in-plane coordinates and how far it is from the plane.
    private static (double u, double v, double off) Split(SlicePlane plane, double fixedAt, double x, double y, double z) =>
        plane == SlicePlane.Xy ? (x, y, z - fixedAt) : (x, z, y - fixedAt);

    private static (double U, double V)[]? Sphere(in Solid s, SlicePlane plane, double fixedAt) {
        var (u, v, off) = Split(plane, fixedAt, s.X, s.Y, s.Z);
        var inside = (double)s.HalfU * s.HalfU - off * off;
        if (!(inside > 0)) { return null; }
        var radius = Math.Sqrt(inside);
        var outline = new (double U, double V)[Steps];
        for (var i = 0; i < Steps; i++) {
            var angle = i * 2 * Math.PI / Steps;
            outline[i] = (u + radius * Math.Cos(angle), v + radius * Math.Sin(angle));
        }
        return outline;
    }

    private static (double U, double V)[]? Box(in Solid s, SlicePlane plane, double fixedAt) {
        // The eight corners: bit 0 picks the side along U, bit 1 along V, bit 2 along W.
        Span<(double u, double v, double off)> corners = stackalloc (double, double, double)[8];
        for (var i = 0; i < 8; i++) {
            double a = (i & 1) == 0 ? -s.HalfU : s.HalfU, b = (i & 2) == 0 ? -s.HalfV : s.HalfV, c = (i & 4) == 0 ? -s.HalfW : s.HalfW;
            corners[i] = Split(plane, fixedAt, s.X + a * s.UX + b * s.VX + c * s.WX, s.Y + a * s.UY + b * s.VY + c * s.WY, s.Z + a * s.UZ + b * s.VZ + c * s.WZ);
        }
        var points = new List<(double U, double V)>();
        for (var i = 0; i < 8; i++) {
            if (corners[i].off == 0) { points.Add((corners[i].u, corners[i].v)); }
            for (var bit = 1; bit < 8; bit <<= 1) {
                var j = i | bit;
                if (j == i) { continue; }
                var (p, q) = (corners[i], corners[j]);
                if (p.off * q.off < 0) {
                    var t = p.off / (p.off - q.off);
                    points.Add((p.u + t * (q.u - p.u), p.v + t * (q.v - p.v)));
                }
            }
        }
        if (points.Count < 3) { return null; }
        // The cut of a convex solid is convex, so ordering by angle about the middle draws it.
        double mu = points.Average(p => p.U), mv = points.Average(p => p.V);
        points.Sort((a, b) => Math.Atan2(a.V - mv, a.U - mu).CompareTo(Math.Atan2(b.V - mv, b.U - mu)));
        return points.ToArray();
    }

    private static (double U, double V)[]? Capsule(in Solid s, SlicePlane plane, double fixedAt) {
        double r = s.HalfU;
        var a = Split(plane, fixedAt, s.X, s.Y, s.Z);
        var b = Split(plane, fixedAt, s.UX, s.UY, s.UZ);
        // A point of the plane that is inside: where the axis crosses it, else under the nearer end if that is within r.
        double cu, cv;
        if (a.off * b.off <= 0 && a.off != b.off) {
            var t = a.off / (a.off - b.off);
            (cu, cv) = (a.u + t * (b.u - a.u), a.v + t * (b.v - a.v));
        } else {
            var near = Math.Abs(a.off) <= Math.Abs(b.off) ? a : b;
            if (!(Math.Abs(near.off) < r)) { return null; }
            (cu, cv) = (near.u, near.v);
        }
        double length = Math.Sqrt((b.u - a.u) * (b.u - a.u) + (b.v - a.v) * (b.v - a.v) + (b.off - a.off) * (b.off - a.off));
        var far = 2 * (r + length) + 1;
        var outline = new (double U, double V)[Steps];
        for (var i = 0; i < Steps; i++) {
            double du = Math.Cos(i * 2 * Math.PI / Steps), dv = Math.Sin(i * 2 * Math.PI / Steps);
            // How far along the ray the capsule reaches: the distance to the axis grows steadily along it, so bisect.
            double low = 0, high = far;
            for (var k = 0; k < 40; k++) {
                var mid = (low + high) / 2;
                if (AxisDistance(a, b, cu + mid * du, cv + mid * dv) <= r) { low = mid; } else { high = mid; }
            }
            outline[i] = (cu + low * du, cv + low * dv);
        }
        return outline;
    }

    // The distance from a point of the plane (its offset 0) to the segment between two ends.
    private static double AxisDistance((double u, double v, double off) a, (double u, double v, double off) b, double u, double v) {
        double dx = b.u - a.u, dy = b.v - a.v, dz = b.off - a.off;
        double px = u - a.u, py = v - a.v, pz = -a.off;
        var along = dx * dx + dy * dy + dz * dz;
        var t = along > 0 ? Math.Clamp((px * dx + py * dy + pz * dz) / along, 0, 1) : 0;
        double ex = px - t * dx, ey = py - t * dy, ez = pz - t * dz;
        return Math.Sqrt(ex * ex + ey * ey + ez * ez);
    }
}
