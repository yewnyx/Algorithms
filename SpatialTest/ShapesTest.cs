using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class ShapesTest {
    // What a distance may differ from the true one by, on either side.
    private const double Slack = Shapes.Slack;

    // Points on a lattice through and around a region, at steps that do not line up with anything.
    private static IEnumerable<(float x, float y, float z)> Lattice(float from, float to, int steps) {
        for (var i = 0; i < steps; i++) {
            for (var j = 0; j < steps; j++) {
                for (var k = 0; k < steps; k++) {
                    var span = to - from;
                    yield return (from + span * (i + 0.37f) / steps, from + span * (j + 0.61f) / steps, from + span * (k + 0.13f) / steps);
                }
            }
        }
    }

    private static void AgreesWithLoop(ShapeScene scene, float from, float to, int steps) {
        foreach (var (x, y, z) in Lattice(from, to, steps)) {
            var exact = scene.Exact(x, y, z);
            ((double)scene.Distance(x, y, z)).Should().BeInRange(exact - Slack, exact + Slack, $"at {x}, {y}, {z}");
        }
    }

    [Fact]
    public void ABoxAlongTheAxesIsMeasuredFromItsFacesEdgesAndCorners() {
        var scene = new ShapeScene([Solid.Box(new Box(1, 1, 1, 3, 2, 5))]);
        scene.Distance(2, 1.5f, 3).Should().Be(0f, "inside");
        scene.Distance(4.25f, 1.5f, 3).Should().BeApproximately(1.25f, 1e-6f, "off a face");
        scene.Distance(6, 6, 3).Should().BeApproximately(5f, 1e-6f, "off an edge: 3 and 4 away");
        scene.Distance(5, 4, 11).Should().BeApproximately(MathF.Sqrt(4 + 4 + 36), 1e-5f, "off a corner");
    }

    [Fact]
    public void ABoxAtAnAngleIsMeasuredAlongItsOwnAxes() {
        // Turned 30 degrees about z: its U axis is (cos 30, sin 30, 0).
        var scene = new ShapeScene([ShapeScene.TurnedBox(2, 3, 4, 1.5, 0.5, 0.25, 30)]);
        float ux = MathF.Cos(MathF.PI / 6), uy = MathF.Sin(MathF.PI / 6);
        scene.Distance(2 + ux * 2.2f, 3 + uy * 2.2f, 4).Should().BeApproximately(0.7f, 1e-5f, "0.7 beyond the face at the end of U");
        scene.Distance(2 - uy * 1.5f, 3 + ux * 1.5f, 4).Should().BeApproximately(1f, 1e-5f, "1 beyond the face at the end of V");
        scene.Distance(2 + ux, 3 + uy, 4.1f).Should().Be(0f, "inside");
    }

    [Fact]
    public void SolidsAtOddAnglesAgreeWithALoopOverThem() {
        var scene = new ShapeScene([
            ShapeScene.TurnedBox(0.5, -1, 2, 1.5, 0.4, 0.9, 37, 21),
            ShapeScene.TurnedBox(-2, 2, -1, 0.3, 2.5, 0.05, -64, 80),
            Solid.Sphere(1.5f, 2.5f, -2f, 0.8f),
            Solid.Capsule(-3f, -2f, 0.5f, -1f, -3.5f, 2.5f, 0.35f),
            Solid.Capsule(3f, 3f, 3f, 3f, 3f, 3f, 0.5f),
        ]);
        AgreesWithLoop(scene, -4.5f, 4.5f, 11);
    }

    [Fact]
    public void ATriangleIsMeasuredFromItsFaceItsEdgesAndItsCorners() {
        // A right triangle in the plane z = 0, with its square corner at the origin.
        var scene = new ShapeScene([], [0, 0, 0, 4, 0, 0, 0, 3, 0], [0, 1, 2]);
        scene.Distance(1, 1, 0.75f).Should().BeApproximately(0.75f, 1e-6f, "over the face");
        scene.Distance(1, 1, -0.75f).Should().BeApproximately(0.75f, 1e-6f, "under the face: a triangle has no inside");
        scene.Distance(2, -1.5f, 0).Should().BeApproximately(1.5f, 1e-6f, "off the edge along x");
        scene.Distance(2, -1.5f, 2).Should().BeApproximately(2.5f, 1e-6f, "off that edge and above it");
        scene.Distance(4, 3, 0).Should().BeApproximately(2.4f, 1e-6f, "off the long edge: 12 / 5");
        scene.Distance(-3, -4, 0).Should().BeApproximately(5f, 1e-6f, "off the corner at the origin");
        scene.Distance(7, -4, 0).Should().BeApproximately(5f, 1e-6f, "off the corner on x");
        scene.Distance(1, 1, 0).Should().Be(0f, "on the face");
    }

    [Fact]
    public void TheOrderOfATrianglesCornersDoesNotMatter() {
        float[] corners = [0.25f, -1f, 2f, 3.5f, 0.75f, 1f, -0.5f, 2.25f, -1.5f];
        ShapeScene[] scenes = [new([], corners, [0, 1, 2]), new([], corners, [0, 2, 1]), new([], corners, [1, 2, 0]), new([], corners, [2, 1, 0])];
        foreach (var (x, y, z) in Lattice(-3f, 4f, 7)) {
            var exact = scenes[0].Exact(x, y, z);
            foreach (var scene in scenes) {
                ((double)scene.Distance(x, y, z)).Should().BeInRange(exact - Slack, exact + Slack);
            }
        }
    }

    [Fact]
    public void ATriangleWithNoAreaIsItsEdges() {
        // Three corners in a line, and three corners at one point.
        var line = new ShapeScene([], [0, 0, 0, 1, 0, 0, 3, 0, 0], [0, 1, 2]);
        line.Distance(2, 0.5f, 0).Should().BeApproximately(0.5f, 1e-6f);
        line.Distance(4, 0, 0).Should().BeApproximately(1f, 1e-6f);
        var point = new ShapeScene([], [1, 2, 3, 1, 2, 3, 1, 2, 3], [0, 1, 2]);
        point.Distance(1, 2, 5).Should().BeApproximately(2f, 1e-6f);
    }

    [Fact]
    public void ALongThinTriangleIsMeasuredAsWellAsAnyOther() {
        // 20 m long and a tenth of a millimetre wide, lying at an angle to every axis.
        var scene = new ShapeScene([], [0, 0, 0, 16, 12, 0, 16.00006f, 11.99992f, 0], [0, 1, 2]);
        // Above the middle of the long side, and beside it in the triangle's plane.
        scene.Distance(8, 6, 0.3f).Should().BeApproximately(0.3f, 1e-5f);
        scene.Distance(8 - 0.6f * 0.5f, 6 + 0.8f * 0.5f, 0).Should().BeApproximately(0.5f, 1e-5f);
        foreach (var (x, y, z) in Lattice(-2f, 18f, 9)) {
            var exact = scene.Exact(x, y, z);
            ((double)scene.Distance(x, y, z)).Should().BeInRange(exact - Slack, exact + Slack, $"at {x}, {y}, {z}");
        }
    }

    [Fact]
    public void ALargeTriangleFarFromTheOriginIsMeasuredAsWellAsAnyOther() {
        var scene = new ShapeScene([], [900, 950, 1000, 1010, 940, 880, 870, 1015, 990], [0, 1, 2]);
        foreach (var (x, y, z) in Lattice(860f, 1020f, 9)) {
            var exact = scene.Exact(x, y, z);
            ((double)scene.Distance(x, y, z, limit: 500f)).Should().BeInRange(exact - Slack, exact + Slack, $"at {x}, {y}, {z}");
        }
    }

    [Fact]
    public void TheNearestShapeIsFoundNotTheNearestBox() {
        // The point is inside the box around a large slanted triangle, whose face is 5 away, and
        // 0.5 from a small sphere whose own box is farther off than that.
        var scene = new ShapeScene([Solid.Sphere(2, 2, 3.5f, 0.5f)], [10, 0, 0, 0, 10, 0, 0, 0, 10], [0, 1, 2]);
        scene.Distance(2, 2, 2.5f).Should().BeApproximately(0.5f, 1e-5f);
        // Without the sphere the triangle is nearest, at its true distance and not its box's zero.
        new ShapeScene([], [10, 0, 0, 0, 10, 0, 0, 0, 10], [0, 1, 2]).Distance(2, 2, 2.5f).Should().BeApproximately(3.5f / MathF.Sqrt(3), 1e-5f);
    }

    [Fact]
    public void ManyShapesWithinTheLimitNeedNoBuffer() {
        // 600 small spheres all within 1 of the point; the nearest is the one made last.
        var solids = new Solid[600];
        for (var i = 0; i < solids.Length; i++) {
            float angle = i * 0.61f, rise = (i % 20 - 10) * 0.04f, away = 0.9f - i * 0.001f;
            solids[i] = Solid.Sphere(5 + MathF.Cos(angle) * away, 5 + MathF.Sin(angle) * away, 5 + rise, 0.02f);
        }
        var scene = new ShapeScene(solids);
        var exact = scene.Exact(5, 5, 5);
        ((double)scene.Distance(5, 5, 5, limit: 1f)).Should().BeInRange(exact - Slack, exact + Slack);
        exact.Should().BeLessThan(0.5);
    }

    [Fact]
    public void NothingNearerThanTheLimitGivesTheLimit() {
        var scene = new ShapeScene([Solid.Sphere(0, 0, 0, 1)]);
        scene.Distance(4, 0, 0, limit: 2f).Should().Be(2f);
        scene.Distance(4, 0, 0, limit: 3f).Should().Be(3f, "exactly at the limit is not nearer");
        scene.Distance(4, 0, 0, limit: 3.5f).Should().BeApproximately(3f, 1e-6f);
        scene.Distance(4, 0, 0, limit: 0f).Should().Be(0f);
        new ShapeScene([]).Distance(1, 2, 3, limit: 7f).Should().Be(7f, "no shapes");
        Shapes.Distance(default, 1, 2, 3, 7f, out var room).Should().BeTrue("default is a set with no shapes");
        room.Should().Be(7f);
    }

    [Fact]
    public void ARefusedQueryReturnsFalse() {
        var scene = new ShapeScene([Solid.Sphere(0, 0, 0, 1)]);
        // A refused query's distance is 0: it must never be read as room.
        Shapes.Distance(scene.Set, float.NaN, 0, 0, 5f, out var refused).Should().BeFalse();
        refused.Should().Be(0f);
        Shapes.Distance(scene.Set, 0, 0, 0, -1f, out refused).Should().BeFalse();
        refused.Should().Be(0f);
        Shapes.Distance(scene.Set, 0, 0, 0, float.NaN, out refused).Should().BeFalse();
        refused.Should().Be(0f);
        // Spans that do not belong together: one box too few.
        var broken = new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, [], scene.Nodes.AsSpan(0, scene.NodeCount), scene.Order);
        broken.Count.Should().Be(-1);
        Shapes.Distance(broken, 0, 0, 0, 5f, out refused).Should().BeFalse();
        refused.Should().Be(0f);
    }

    [Fact]
    public void AWalkThroughDamagedNodesEndsWithinOneVisitPerNode() {
        // Forty spheres far from the point, and in place of their tree forty nodes that each name
        // the next two as its children. Walked without a count of visits that is a hundred million
        // steps; the walk gives up after one visit per node.
        var solids = new Solid[40];
        for (var i = 0; i < solids.Length; i++) { solids[i] = Solid.Sphere(500 + 10 * i, 0, 0, 1); }
        var scene = new ShapeScene(solids);
        var nodes = new BoxNode[40];
        for (var i = 0; i < nodes.Length; i++) {
            nodes[i] = new BoxNode { MinX = -1000, MinY = -1000, MinZ = -1000, MaxX = 1000, MaxY = 1000, MaxZ = 1000, First = i < 38 ? i + 2 : 0, Count = i < 38 ? 0 : 1 };
        }
        var looped = new ShapeSet(scene.Solids, scene.Corners, scene.Triangles, scene.Boxes, nodes, scene.Order);
        looped.Count.Should().Be(40);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Shapes.Distance(looped, 0, 0, 0, 100f, out var distance).Should().BeFalse();
        distance.Should().Be(0f);
        watch.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    [Fact]
    public void BuildRefusesShapesThatMayNotBeStored() {
        static bool Builds(Solid[] solids, float[]? corners = null, int[]? triangles = null) {
            corners ??= []; triangles ??= [];
            var count = solids.Length + triangles.Length / 3;
            return Shapes.Build(solids, corners, triangles, new Box[count], new BoxNode[count], new int[count], new int[Shapes.WorkspaceLength(count)], out _);
        }

        Builds([Solid.Sphere(0, 0, 0, 1)]).Should().BeTrue();
        Builds([Solid.Sphere(float.NaN, 0, 0, 1)]).Should().BeFalse("a centre that is not a number");
        Builds([Solid.Sphere(0, float.PositiveInfinity, 0, 1)]).Should().BeFalse("a centre that is not finite");
        Builds([Solid.Sphere(0, 0, 0, -1)]).Should().BeFalse("a negative radius");
        Builds([Solid.Sphere(0, 0, 0, float.NaN)]).Should().BeFalse();
        Builds([Solid.Sphere(1023.5f, 0, 0, 1)]).Should().BeFalse("beyond the reach");
        Builds([Solid.Sphere(1022f, 0, 0, 1)]).Should().BeTrue("inside the reach");
        Builds([Solid.Capsule(0, 0, 0, float.NaN, 0, 0, 1)]).Should().BeFalse();
        Builds([Solid.Capsule(0, 0, 0, 0, 0, 2000, 1)]).Should().BeFalse();
        Builds([Solid.Box(0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1)]).Should().BeFalse("an axis that carries a scale");
        Builds([Solid.Box(0, 0, 0, 1, 0, 0, 0.6f, 0.8f, 0, 0, 0, 1, 1, 1, 1)]).Should().BeFalse("axes that are not square");
        Builds([Solid.Box(0, 0, 0, 1.0001f, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1)]).Should().BeFalse("an axis 1 part in 10,000 too long");
        Builds([Solid.Box(0, 0, 0, 1, 0, 0, 0.0001f, 1, 0, 0, 0, 1, 1, 1, 1)]).Should().BeFalse("axes 1 part in 10,000 off square");
        Builds([Solid.Box(0, 0, 0, 1.000005f, 0, 0, 0.000005f, 1, 0, 0, 0, 1, 1, 1, 1)]).Should().BeTrue("axes a few parts in a million off");
        Builds([Solid.Box(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, -1, 1)]).Should().BeFalse("a negative half extent");
        Builds([default(Solid)]).Should().BeFalse("a box with no axes");

        Builds([], [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 2]).Should().BeTrue();
        Builds([], [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 3]).Should().BeFalse("a corner that is not there");
        Builds([], [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, -1, 2]).Should().BeFalse();
        Builds([], [0, 0, 0, 1, 0, 0, 0, float.NaN, 0], [0, 1, 2]).Should().BeFalse();
        Builds([], [0, 0, 0, 1, 0, 0, 0, 1500, 0], [0, 1, 2]).Should().BeFalse();
        Builds([], [0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2]).Should().BeFalse("corners not in threes");
        Builds([], [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1]).Should().BeFalse("corner numbers not in threes");

        // Buffers one too short, each in turn.
        Solid[] one = [Solid.Sphere(0, 0, 0, 1)];
        Shapes.Build(one, [], [], [], new BoxNode[1], new int[1], new int[Shapes.WorkspaceLength(1)], out _).Should().BeFalse();
        Shapes.Build(one, [], [], new Box[1], [], new int[1], new int[Shapes.WorkspaceLength(1)], out _).Should().BeFalse();
        Shapes.Build(one, [], [], new Box[1], new BoxNode[1], [], new int[Shapes.WorkspaceLength(1)], out _).Should().BeFalse();
        Shapes.Build(one, [], [], new Box[1], new BoxNode[1], new int[1], new int[Shapes.WorkspaceLength(1) - 1], out _).Should().BeFalse();
    }

    [Fact]
    public void ALargeBoxWithAxesAHairOffIsStillMeasuredAndBoxedSafely() {
        // Half extents of 400, 300 and 200 m, with axes nearly as far off as is accepted: U is 5
        // parts in a million too long, and V leans towards U by the same. That hair times 400 m is
        // 2 mm, eight times the slack.
        const double halfU = 400, halfV = 300, halfW = 200, off = 5e-6;
        (double x, double y, double z) u = (1 + off, 0, 0), v = (off, 1, 0), w = (0, 0, 1);
        var solid = Solid.Box(10, -20, 30, (float)u.x, (float)u.y, (float)u.z, (float)v.x, (float)v.y, (float)v.z, (float)w.x, (float)w.y, (float)w.z, (float)halfU, (float)halfV, (float)halfW);
        var scene = new ShapeScene([solid]);

        // The box is the space between three pairs of planes square to the axes as given. A point
        // whose place along the unit normals is (a, b, c) is where those three planes meet:
        // along V's normal it is b, and V's normal leans, so x and y are solved together.
        double vLength = Math.Sqrt(off * off + 1);
        (double x, double y, double z) At(double a, double b, double c) {
            // a = x - 10 (U's normal is x), b = ((x - 10) * off + (y + 20)) / vLength, c = z - 30.
            return (10 + a, -20 + b * vLength - a * off, 30 + c);
        }

        // Beyond one face by half a metre, well inside the other two: the distance is half a metre.
        var beyondU = At(halfU + 0.5, 0.3 * halfV, -0.2 * halfW);
        ((double)scene.Distance((float)beyondU.x, (float)beyondU.y, (float)beyondU.z)).Should().BeApproximately(0.5, 2 * Shapes.Slack);
        var beyondV = At(-0.6 * halfU, -(halfV + 0.5), 0.4 * halfW);
        ((double)scene.Distance((float)beyondV.x, (float)beyondV.y, (float)beyondV.z)).Should().BeApproximately(0.5, 2 * Shapes.Slack);
        var within = At(halfU - 0.01, halfV - 0.01, halfW - 0.01);
        scene.Distance((float)within.x, (float)within.y, (float)within.z).Should().Be(0f);

        // Every corner of that box is inside the box stored for it, so the tree never passes it by.
        var box = scene.Boxes[0];
        foreach (var a in new[] { -halfU, halfU }) {
            foreach (var b in new[] { -halfV, halfV }) {
                foreach (var c in new[] { -halfW, halfW }) {
                    var corner = At(a, b, c);
                    corner.x.Should().BeInRange(box.MinX, box.MaxX);
                    corner.y.Should().BeInRange(box.MinY, box.MaxY);
                    corner.z.Should().BeInRange(box.MinZ, box.MaxZ);
                }
            }
        }
        // And the stored box is not much wider than it need be: centimetres, not metres.
        ((double)box.MaxX - (10 + halfU)).Should().BeLessThan(0.5);
    }

    [Fact]
    public void ABoxAlongTheAxesIsStoredNoWiderThanItsPad() {
        var scene = new ShapeScene([Solid.Box(new Box(-400, -300, -200, 400, 300, 200))]);
        const float pad = 1f / 1024f;
        scene.Boxes[0].Should().Be(new Box(-400 - pad, -300 - pad, -200 - pad, 400 + pad, 300 + pad, 200 + pad));
    }

    [Fact]
    public void RoundingNeverLeavesAShapeOutsideItsStoredBox() {
        // A stored box's sides are rounded to single, and rounding goes either way. Each box is
        // made a little wider than its shape so that the shape's farthest points, worked out in
        // double, are still inside. Sixty balls at awkward places: without the widening some stick out.
        var solids = new Solid[60];
        for (var i = 0; i < solids.Length; i++) { solids[i] = Solid.Sphere(900.3f + i * 1.7f, -700.1f + i * 0.9f, 333.3f + i * 3.1f, 0.7f + i * 0.013f); }
        var scene = new ShapeScene(solids);
        for (var i = 0; i < solids.Length; i++) {
            var (solid, box) = (solids[i], scene.Boxes[i]);
            ((double)box.MinX).Should().BeLessThanOrEqualTo((double)solid.X - solid.HalfU);
            ((double)box.MaxX).Should().BeGreaterThanOrEqualTo((double)solid.X + solid.HalfU);
            ((double)box.MinY).Should().BeLessThanOrEqualTo((double)solid.Y - solid.HalfU);
            ((double)box.MaxY).Should().BeGreaterThanOrEqualTo((double)solid.Y + solid.HalfU);
            ((double)box.MinZ).Should().BeLessThanOrEqualTo((double)solid.Z - solid.HalfU);
            ((double)box.MaxZ).Should().BeGreaterThanOrEqualTo((double)solid.Z + solid.HalfU);
        }
    }

    [Fact]
    public void EveryShapeLiesInsideItsStoredBox() {
        var scene = new ShapeScene(
            [ShapeScene.TurnedBox(100.3, -200.7, 300.1, 12.5, 0.4, 3.9, 37, 21), Solid.Sphere(-512.3f, 77.7f, 3.1f, 4.4f), Solid.Capsule(-3f, -2f, 0.5f, -1f, -3.5f, 2.5f, 0.35f)],
            [900.1f, 950.2f, 1000.3f, 1010.4f, 940.5f, 880.6f, 870.7f, 1015.8f, 990.9f], [0, 1, 2]);
        // A point just outside a shape's box is never nearer to the shape than it is to the box.
        for (var item = 0; item < scene.Boxes.Length; item++) {
            var box = scene.Boxes[item];
            ShapeScene alone = item < 3 ? new([scene.Solids[item]]) : new([], scene.Corners, scene.Triangles);
            foreach (var (fx, fy, fz) in Lattice(-0.2f, 1.2f, 6)) {
                double x = box.MinX + (box.MaxX - box.MinX) * fx, y = box.MinY + (box.MaxY - box.MinY) * fy, z = box.MinZ + (box.MaxZ - box.MinZ) * fz;
                double dx = Math.Max(0, Math.Max(box.MinX - x, x - box.MaxX)), dy = Math.Max(0, Math.Max(box.MinY - y, y - box.MaxY)), dz = Math.Max(0, Math.Max(box.MinZ - z, z - box.MaxZ));
                alone.Exact(x, y, z).Should().BeGreaterThanOrEqualTo(Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        }
    }
}
