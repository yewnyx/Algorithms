namespace Yewnyx.Spatial.Test;

// Shapes built into a set, with the same distances worked out a plainer way to check against.
internal sealed class ShapeScene {
    public readonly Solid[] Solids;
    public readonly float[] Corners;
    public readonly int[] Triangles;
    public readonly Box[] Boxes;
    public readonly BoxNode[] Nodes;
    public readonly int[] Order;
    public readonly int NodeCount;

    public ShapeScene(Solid[] solids, float[]? corners = null, int[]? triangles = null) {
        Solids = solids;
        Corners = corners ?? [];
        Triangles = triangles ?? [];
        var count = Solids.Length + Triangles.Length / 3;
        Boxes = new Box[count];
        Nodes = new BoxNode[Shapes.NodesLength(count)];
        Order = new int[count];
        if (!Shapes.Build(Solids, Corners, Triangles, Boxes, Nodes, Order, new int[Shapes.WorkspaceLength(count)], out NodeCount)) {
            throw new InvalidOperationException("The scene's shapes were refused.");
        }
    }

    public ShapeSet Set => new(Solids, Corners, Triangles, Boxes, Nodes.AsSpan(0, NodeCount), Order);

    public float Distance(float x, float y, float z, float limit = 100f) {
        if (!Shapes.Distance(Set, x, y, z, limit, out var distance)) { throw new InvalidOperationException("The query was refused."); }
        return distance;
    }

    // The distance to the nearest shape by a loop over every shape, in double.
    public double Exact(double x, double y, double z) {
        var best = double.PositiveInfinity;
        foreach (var solid in Solids) { best = Math.Min(best, Exact(solid, x, y, z)); }
        for (var t = 0; t < Triangles.Length; t += 3) {
            int a = Triangles[t] * 3, b = Triangles[t + 1] * 3, c = Triangles[t + 2] * 3;
            best = Math.Min(best, Math.Sqrt(TriangleSquared(
                (Corners[a], Corners[a + 1], Corners[a + 2]), (Corners[b], Corners[b + 1], Corners[b + 2]), (Corners[c], Corners[c + 1], Corners[c + 2]), (x, y, z))));
        }
        return best;
    }

    // A box at an angle: turned about z, then tilted about x.
    public static Solid TurnedBox(double centreX, double centreY, double centreZ, double halfU, double halfV, double halfW, double turnDegrees, double tiltDegrees = 0) {
        double turn = turnDegrees * Math.PI / 180, tilt = tiltDegrees * Math.PI / 180;
        (double x, double y, double z) Turn((double x, double y, double z) v) {
            var afterZ = (x: v.x * Math.Cos(turn) - v.y * Math.Sin(turn), y: v.x * Math.Sin(turn) + v.y * Math.Cos(turn), v.z);
            return (afterZ.x, afterZ.y * Math.Cos(tilt) - afterZ.z * Math.Sin(tilt), afterZ.y * Math.Sin(tilt) + afterZ.z * Math.Cos(tilt));
        }
        var (u, v, w) = (Turn((1, 0, 0)), Turn((0, 1, 0)), Turn((0, 0, 1)));
        return Solid.Box((float)centreX, (float)centreY, (float)centreZ, (float)u.x, (float)u.y, (float)u.z, (float)v.x, (float)v.y, (float)v.z,
            (float)w.x, (float)w.y, (float)w.z, (float)halfU, (float)halfV, (float)halfW);
    }

    // The same solids, read back through their public fields.
    private static double Exact(Solid solid, double x, double y, double z) {
        float[] f = [solid.X, solid.Y, solid.Z, solid.UX, solid.UY, solid.UZ, solid.VX, solid.VY, solid.VZ, solid.WX, solid.WY, solid.WZ, solid.HalfU, solid.HalfV, solid.HalfW];
        switch (solid.Kind) {
            case SolidKind.Box: {
                // The nearest point of the box: the point's own place along each axis, held within the half extents.
                double px = x - f[0], py = y - f[1], pz = z - f[2];
                double nearX = f[0], nearY = f[1], nearZ = f[2];
                for (var axis = 0; axis < 3; axis++) {
                    double ax = f[3 + axis * 3], ay = f[4 + axis * 3], az = f[5 + axis * 3], half = f[12 + axis];
                    var along = Math.Clamp(px * ax + py * ay + pz * az, -half, half);
                    nearX += along * ax; nearY += along * ay; nearZ += along * az;
                }
                return Length(x - nearX, y - nearY, z - nearZ);
            }
            case SolidKind.Sphere:
                return Math.Max(0, Length(x - f[0], y - f[1], z - f[2]) - f[12]);
            default: {
                double abx = f[3] - f[0], aby = f[4] - f[1], abz = f[5] - f[2];
                var length = abx * abx + aby * aby + abz * abz;
                var along = length > 0 ? Math.Clamp(((x - f[0]) * abx + (y - f[1]) * aby + (z - f[2]) * abz) / length, 0, 1) : 0;
                return Math.Max(0, Length(x - f[0] - along * abx, y - f[1] - along * aby, z - f[2] - along * abz) - f[12]);
            }
        }
    }

    private static double Length(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    // The squared distance from a point to a triangle, by finding which of the triangle's seven
    // parts (its face, three edges, three corners) the point is nearest. Not the library's way.
    public static double TriangleSquared((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c,
        (double x, double y, double z) p) {
        static (double x, double y, double z) Sub((double x, double y, double z) u, (double x, double y, double z) v) => (u.x - v.x, u.y - v.y, u.z - v.z);
        static double Dot((double x, double y, double z) u, (double x, double y, double z) v) => u.x * v.x + u.y * v.y + u.z * v.z;
        double Away((double x, double y, double z) q) { var d = Sub(p, q); return Dot(d, d); }
        (double x, double y, double z) Mix((double x, double y, double z) from, (double x, double y, double z) step, double t) => (from.x + step.x * t, from.y + step.y * t, from.z + step.z * t);

        var (ab, ac, ap) = (Sub(b, a), Sub(c, a), Sub(p, a));
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { return Away(a); }
        var bp = Sub(p, b);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { return Away(b); }
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { return Away(Mix(a, ab, d1 / (d1 - d3))); }
        var cp = Sub(p, c);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { return Away(c); }
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { return Away(Mix(a, ac, d2 / (d2 - d6))); }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { return Away(Mix(b, Sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6)))); }
        var sum = va + vb + vc;
        return Away(Mix(Mix(a, ab, vb / sum), ac, vc / sum));
    }
}
