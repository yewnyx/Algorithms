using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yewnyx.Spatial.Tests {
    public class ColliderShapesTest {
        private readonly List<GameObject> _made = new List<GameObject>();
        private Transform _root;

        [SetUp]
        public void MakeAFrameThatIsNotTheWorlds() {
            // The grid's frame: moved and turned, so that nothing passes by leaving the frame out.
            _root = Make("root", null, new Vector3(40, -7, 13), Quaternion.Euler(20, 130, -35), Vector3.one).transform;
        }

        [TearDown]
        public void DestroyWhatWasMade() {
            foreach (var made in _made) { if (made != null) { Object.DestroyImmediate(made); } }
            _made.Clear();
        }

        private GameObject Make(string name, Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale) {
            var made = new GameObject(name);
            _made.Add(made);
            made.transform.SetParent(parent, false);
            made.transform.localPosition = localPosition;
            made.transform.localRotation = localRotation;
            made.transform.localScale = localScale;
            return made;
        }

        // The collected shapes, built, with the exact distance from a point of the frame.
        private sealed class Built {
            private readonly Solid[] _solids;
            private readonly float[] _corners;
            private readonly int[] _triangles;
            private readonly Box[] _boxes;
            private readonly BoxNode[] _nodes;
            private readonly int[] _order;
            private readonly int _nodeCount;
            public readonly CollectReport Report;
            public int Solids => _solids.Length;
            public int TriangleCount => _triangles.Length / 3;

            public Built(Collider[] colliders, Matrix4x4 worldToFrame) {
                List<Solid> solids = new List<Solid>();
                List<float> corners = new List<float>();
                List<int> triangles = new List<int>();
                Report = ColliderShapes.Collect(colliders, worldToFrame, solids, corners, triangles);
                _solids = solids.ToArray(); _corners = corners.ToArray(); _triangles = triangles.ToArray();
                var count = _solids.Length + _triangles.Length / 3;
                _boxes = new Box[count]; _nodes = new BoxNode[Shapes.NodesLength(count)]; _order = new int[count];
                Assert.That(Shapes.Build(_solids, _corners, _triangles, _boxes, _nodes, _order, new int[Shapes.WorkspaceLength(count)], out _nodeCount), Is.True);
            }

            public float Distance(Vector3 point) {
                var set = new ShapeSet(_solids, _corners, _triangles, _boxes, new System.ReadOnlySpan<BoxNode>(_nodes, 0, _nodeCount), _order);
                Assert.That(Shapes.Distance(set, point.x, point.y, point.z, 100f, out var distance), Is.True);
                return distance;
            }
        }

        // Points of the frame on a lattice around the origin, at steps that line up with nothing.
        private static IEnumerable<Vector3> Lattice(float reach, int steps) {
            for (var i = 0; i < steps; i++) {
                for (var j = 0; j < steps; j++) {
                    for (var k = 0; k < steps; k++) {
                        yield return new Vector3(-reach + 2 * reach * (i + 0.37f) / steps, -reach + 2 * reach * (j + 0.61f) / steps, -reach + 2 * reach * (k + 0.13f) / steps);
                    }
                }
            }
        }

        // The physics engine's own distance from a point of the world to a collider, 0 inside it.
        private static float EngineDistance(Collider collider, Vector3 worldPoint) =>
            Vector3.Distance(worldPoint, Physics.ClosestPoint(worldPoint, collider, collider.transform.position, collider.transform.rotation));

        private void AgreesWithTheEngine(Collider collider, float reach = 4f, float within = 2e-3f) {
            var built = new Built(new[] { collider }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            foreach (var point in Lattice(reach, 7)) {
                Assert.That(built.Distance(point), Is.EqualTo(EngineDistance(collider, _root.TransformPoint(point))).Within(within), $"at {point} of the frame");
            }
        }

        [Test]
        public void ABoxAtAnAngleWithUnevenScaleAgreesWithTheEngine() {
            var box = Make("box", _root, new Vector3(0.5f, -1, 0.3f), Quaternion.Euler(25, 40, 70), new Vector3(2, 0.5f, 1.5f)).AddComponent<BoxCollider>();
            box.center = new Vector3(0.2f, -0.1f, 0.4f);
            box.size = new Vector3(1.5f, 2, 0.8f);
            AgreesWithTheEngine(box);
        }

        [Test]
        public void ABoxMirroredByANegativeScaleAgreesWithTheEngine() {
            var box = Make("box", _root, new Vector3(-0.5f, 0.4f, 0), Quaternion.Euler(10, 200, 5), new Vector3(-1.5f, 1, 2)).AddComponent<BoxCollider>();
            box.center = new Vector3(0.5f, 0, 0);
            box.size = new Vector3(1, 1.2f, 0.6f);
            AgreesWithTheEngine(box);
        }

        [Test]
        public void ASphereWithUnevenScaleTakesTheLargestScale() {
            var sphere = Make("sphere", _root, new Vector3(0.5f, 0.5f, -0.5f), Quaternion.Euler(15, 60, 30), new Vector3(0.5f, 2, 1.2f)).AddComponent<SphereCollider>();
            sphere.center = new Vector3(0.3f, 0.2f, -0.1f);
            sphere.radius = 0.6f;
            AgreesWithTheEngine(sphere);
        }

        [Test]
        public void ACapsuleAlongEachAxisWithUnevenScaleAgreesWithTheEngine() {
            for (var direction = 0; direction < 3; direction++) {
                var capsule = Make("capsule", _root, new Vector3(-0.3f, 0.2f, 0.6f), Quaternion.Euler(50, -20, 15), new Vector3(1.5f, 0.7f, 2.2f)).AddComponent<CapsuleCollider>();
                capsule.direction = direction;
                capsule.center = new Vector3(0.1f, 0.3f, -0.2f);
                capsule.radius = 0.4f;
                capsule.height = 2.5f;
                AgreesWithTheEngine(capsule);
            }
        }

        [Test]
        public void ACapsuleShorterThanItsRoundEndsIsABall() {
            var capsule = Make("capsule", _root, Vector3.zero, Quaternion.Euler(0, 45, 0), Vector3.one).AddComponent<CapsuleCollider>();
            capsule.radius = 1f;
            capsule.height = 0.5f;
            AgreesWithTheEngine(capsule);
        }

        private static Mesh Sheet() {
            // Two triangles standing up at an angle, 3 by 2, with no thickness.
            var mesh = new Mesh { vertices = new[] { new Vector3(-1.5f, -1, 0), new Vector3(1.5f, -1, 0.5f), new Vector3(1.5f, 1, 0.5f), new Vector3(-1.5f, 1, 0) }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            mesh.RecalculateBounds();
            return mesh;
        }

        [Test]
        public void AMeshGoesInAsItsTrianglesInTheFrame() {
            var holder = Make("mesh", _root, new Vector3(0.4f, -0.2f, 0.1f), Quaternion.Euler(30, 75, -10), new Vector3(1.2f, 0.8f, 1.5f));
            var collider = holder.AddComponent<MeshCollider>();
            collider.sharedMesh = Sheet();
            var built = new Built(new Collider[] { collider }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.TriangleCount, Is.EqualTo(2));
            Assert.That(built.Solids, Is.EqualTo(0));

            // The engine cannot say how far a point is from such a mesh, only whether a ball touches it.
            Physics.SyncTransforms();
            var checkedPoints = 0;
            foreach (var point in Lattice(2.5f, 6)) {
                var distance = built.Distance(point);
                var world = _root.TransformPoint(point);
                if (distance > 0.05f) { Assert.That(Physics.CheckSphere(world, distance - 0.02f), Is.False, $"a ball just short of the sheet at {point}"); }
                Assert.That(Physics.CheckSphere(world, distance + 0.02f), Is.True, $"a ball just past the sheet at {point}");
                checkedPoints++;
            }
            Assert.That(checkedPoints, Is.EqualTo(216));
            Object.DestroyImmediate(collider.sharedMesh);
        }

        [Test]
        public void AConvexMeshGoesInAsABoxAroundIt() {
            // A pyramid, which collides as its hull: the box around it holds every point of it.
            var mesh = new Mesh {
                vertices = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1), new Vector3(0, 2, 0) },
                triangles = new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4, 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateBounds();
            var collider = Make("pyramid", _root, new Vector3(0.3f, 0, -0.4f), Quaternion.Euler(0, 33, 12), Vector3.one).AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
            var built = new Built(new Collider[] { collider }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Boxed, Is.EqualTo(1));
            Assert.That(built.Report.Exact, Is.EqualTo(0));
            Assert.That(built.Solids, Is.EqualTo(1));
            foreach (var point in Lattice(4f, 6)) {
                Assert.That(built.Distance(point), Is.LessThanOrEqualTo(EngineDistance(collider, _root.TransformPoint(point)) + 2e-3f), $"at {point}");
            }
            Object.DestroyImmediate(mesh);
        }

        // The smallest ball at a point of the world that touches anything in the live physics scene.
        private static float SceneDistance(Vector3 world) {
            if (Physics.CheckSphere(world, 1e-4f)) { return 0f; }
            float low = 0f, high = 32f;
            for (var step = 0; step < 26; step++) {
                var middle = (low + high) * 0.5f;
                if (Physics.CheckSphere(world, middle)) { high = middle; } else { low = middle; }
            }
            return high;
        }

        // The box along the world's axes around a solid, from its fields.
        private static Bounds Around(Solid solid) {
            var centre = new Vector3(solid.X, solid.Y, solid.Z);
            if (solid.Kind == SolidKind.Sphere) { return new Bounds(centre, Vector3.one * (2 * solid.HalfU)); }
            if (solid.Kind == SolidKind.Capsule) {
                var to = new Vector3(solid.UX, solid.UY, solid.UZ);
                var both = new Bounds(centre, Vector3.zero);
                both.Encapsulate(to);
                both.Expand(2 * solid.HalfU);
                return both;
            }
            var reach = new Vector3(
                Mathf.Abs(solid.UX) * solid.HalfU + Mathf.Abs(solid.VX) * solid.HalfV + Mathf.Abs(solid.WX) * solid.HalfW,
                Mathf.Abs(solid.UY) * solid.HalfU + Mathf.Abs(solid.VY) * solid.HalfV + Mathf.Abs(solid.WY) * solid.HalfW,
                Mathf.Abs(solid.UZ) * solid.HalfU + Mathf.Abs(solid.VZ) * solid.HalfV + Mathf.Abs(solid.WZ) * solid.HalfW);
            return new Bounds(centre, 2 * reach);
        }

        // A turned, unevenly scaled child of an unevenly scaled parent: its matrix shears. The
        // engine cannot hold a sheared box, sphere or capsule, and what it holds instead is what
        // must go in. The live scene is the judge; Physics.ClosestPoint places such a collider elsewhere.
        private void IsTheEnginesUnderShear(System.Func<GameObject, Collider> add, Vector3 parentScale) {
            var parent = Make("parent", _root, new Vector3(0.3f, -0.2f, 0.4f), Quaternion.Euler(10, 25, -15), parentScale);
            var child = Make("child", parent.transform, new Vector3(0.2f, 0.1f, -0.3f), Quaternion.Euler(35, 50, 20), new Vector3(1, 1.4f, 0.8f));
            var collider = add(child);
            Physics.SyncTransforms();

            var built = new Built(new[] { collider }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            foreach (var point in Lattice(4f, 6)) {
                Assert.That(built.Distance(point), Is.EqualTo(SceneDistance(_root.TransformPoint(point))).Within(3e-3f), $"{collider.GetType().Name} under {parentScale} at {point} of the frame");
            }

            // And in the world's own frame, the box around what went in is the box Unity gives.
            List<Solid> solids = new List<Solid>();
            ColliderShapes.Collect(new[] { collider }, Matrix4x4.identity, solids, new List<float>(), new List<int>());
            var around = Around(solids[0]);
            Assert.That(Vector3.Distance(around.min, collider.bounds.min), Is.LessThan(2e-3f), $"{collider.GetType().Name} under {parentScale}: lowest corner");
            Assert.That(Vector3.Distance(around.max, collider.bounds.max), Is.LessThan(2e-3f), $"{collider.GetType().Name} under {parentScale}: highest corner");
            Object.DestroyImmediate(parent);
        }

        [Test]
        public void ABoxUnderAShearedChainOfTransformsIsTheEngines() {
            foreach (var parentScale in new[] { new Vector3(2, 0.6f, 1.3f), new Vector3(-1.5f, 1, 3) }) {
                IsTheEnginesUnderShear(holder => {
                    var box = holder.AddComponent<BoxCollider>();
                    box.center = new Vector3(0.3f, -0.2f, 0.25f);
                    box.size = new Vector3(1.4f, 0.8f, 1.1f);
                    return box;
                }, parentScale);
            }
        }

        [Test]
        public void ASphereUnderAShearedChainOfTransformsIsTheEngines() {
            foreach (var parentScale in new[] { new Vector3(2, 0.6f, 1.3f), new Vector3(-1.5f, 1, 3) }) {
                IsTheEnginesUnderShear(holder => {
                    var sphere = holder.AddComponent<SphereCollider>();
                    sphere.center = new Vector3(0.3f, -0.2f, 0.25f);
                    sphere.radius = 0.7f;
                    return sphere;
                }, parentScale);
            }
        }

        [Test]
        public void ACapsuleUnderAShearedChainOfTransformsIsTheEngines() {
            foreach (var parentScale in new[] { new Vector3(2, 0.6f, 1.3f), new Vector3(-1.5f, 1, 3) }) {
                for (var direction = 0; direction < 3; direction++) {
                    var along = direction;
                    IsTheEnginesUnderShear(holder => {
                        var capsule = holder.AddComponent<CapsuleCollider>();
                        capsule.direction = along;
                        capsule.center = new Vector3(0.3f, -0.2f, 0.25f);
                        capsule.radius = 0.4f;
                        capsule.height = 2.2f;
                        return capsule;
                    }, parentScale);
                }
            }
        }

        [Test]
        public void AMeshUnderAShearedChainOfTransformsGoesThroughTheMatrix() {
            // The engine can hold a sheared mesh, so its corners go where the matrix puts them.
            var parent = Make("parent", _root, new Vector3(0.3f, -0.2f, 0.4f), Quaternion.Euler(10, 25, -15), new Vector3(2, 0.6f, 1.3f));
            var child = Make("child", parent.transform, new Vector3(0.2f, 0.1f, -0.3f), Quaternion.Euler(35, 50, 20), new Vector3(1, 1.4f, 0.8f));
            var collider = child.AddComponent<MeshCollider>();
            collider.sharedMesh = Sheet();
            Physics.SyncTransforms();

            var built = new Built(new Collider[] { collider }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            foreach (var point in Lattice(3.5f, 6)) {
                Assert.That(built.Distance(point), Is.EqualTo(SceneDistance(_root.TransformPoint(point))).Within(3e-3f), $"at {point} of the frame");
            }

            // collider.bounds is no judge here: for a mesh Unity gives the box around the mesh's own
            // box carried over, which is wider than the box around its corners.
            Object.DestroyImmediate(collider.sharedMesh);
        }

        [Test]
        public void AFrameThatScalesEvenlyScalesTheShapesWithIt() {
            // A frame twice the world's size: every length in it is half the world's.
            var frame = Make("frame", null, new Vector3(3, 1, -2), Quaternion.Euler(0, 40, 10), new Vector3(2, 2, 2)).transform;
            var box = Make("box", null, new Vector3(4, 2, -1), Quaternion.Euler(20, 0, 30), Vector3.one).AddComponent<BoxCollider>();
            box.size = new Vector3(2, 1, 3);
            var sphere = Make("sphere", null, new Vector3(1, 0, -4), Quaternion.identity, Vector3.one).AddComponent<SphereCollider>();
            sphere.radius = 0.8f;
            var built = new Built(new Collider[] { box, sphere }, frame.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(2));
            foreach (var point in Lattice(3f, 5)) {
                var world = frame.TransformPoint(point);
                var nearest = Mathf.Min(EngineDistance(box, world), EngineDistance(sphere, world));
                Assert.That(built.Distance(point), Is.EqualTo(nearest * 0.5f).Within(2e-3f), $"at {point} of the frame");
            }
        }

        [Test]
        public void AFrameThatShearsNeverMakesAShapeSmaller() {
            // A frame under an unevenly scaled, turned parent shears. A box then goes in as a box
            // around it, and a sphere or capsule no smaller than it is: every point of the true shape,
            // carried into the frame, is inside what went in.
            var outer = Make("outer", null, Vector3.zero, Quaternion.Euler(0, 30, 0), new Vector3(1, 2.5f, 0.6f));
            var frame = Make("frame", outer.transform, new Vector3(1, 0, 0), Quaternion.Euler(40, 0, 25), Vector3.one).transform;
            var box = Make("box", null, new Vector3(1, 0.5f, -0.5f), Quaternion.Euler(15, 60, 0), Vector3.one).AddComponent<BoxCollider>();
            box.size = new Vector3(1.2f, 0.6f, 0.9f);
            var sphere = Make("sphere", null, new Vector3(-1, 0.2f, 0.7f), Quaternion.identity, Vector3.one).AddComponent<SphereCollider>();
            sphere.radius = 0.6f;
            var capsule = Make("capsule", null, new Vector3(0.5f, -1, 1), Quaternion.Euler(70, 10, 0), Vector3.one).AddComponent<CapsuleCollider>();
            capsule.radius = 0.3f;
            capsule.height = 1.8f;

            var built = new Built(new Collider[] { box, sphere, capsule }, frame.worldToLocalMatrix);
            Assert.That(built.Report.Boxed, Is.EqualTo(1), "the box");
            Assert.That(built.Report.Exact, Is.EqualTo(2));
            var inside = 0;
            foreach (var point in Lattice(2.5f, 9)) {
                // A point of the world inside one of the three, by the engine's own measure.
                if (EngineDistance(box, point) > 0f && EngineDistance(sphere, point) > 0f && EngineDistance(capsule, point) > 0f) { continue; }
                inside++;
                Assert.That(built.Distance(frame.InverseTransformPoint(point)), Is.LessThan(1e-3f), $"the world's {point}");
            }
            Assert.That(inside, Is.GreaterThan(8));
        }

        [Test]
        public void AColliderOfAnotherKindGoesInAsABoxAroundIt() {
            var controller = Make("controller", _root, new Vector3(1, 0.5f, -1), Quaternion.identity, Vector3.one).AddComponent<CharacterController>();
            controller.radius = 0.4f;
            controller.height = 1.8f;
            Physics.SyncTransforms();
            var built = new Built(new Collider[] { controller }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Boxed, Is.EqualTo(1));
            Assert.That(built.Report.Exact, Is.EqualTo(0));
            Assert.That(built.Report.Missing, Is.EqualTo(0));
            Assert.That(built.Solids, Is.EqualTo(1));
            // The corners of the box Unity gives for it are all inside what went in.
            var bounds = controller.bounds;
            Assert.That(bounds.size.y, Is.GreaterThan(1.7f));
            for (var corner = 0; corner < 8; corner++) {
                var world = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                Assert.That(built.Distance(_root.InverseTransformPoint(world)), Is.LessThan(1e-3f));
            }

            // Not active, Unity gives no bounds for it: it is counted as missing, never passed over in silence.
            controller.gameObject.SetActive(false);
            built = new Built(new Collider[] { controller }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Missing, Is.EqualTo(1));
            Assert.That(built.Solids, Is.EqualTo(0));
        }

        [Test]
        public void AColliderThatGivesNothingIsCounted() {
            var empty = Make("empty", _root, Vector3.zero, Quaternion.identity, Vector3.one).AddComponent<MeshCollider>();
            var sphere = Make("sphere", _root, Vector3.one, Quaternion.identity, Vector3.one).AddComponent<SphereCollider>();
            var built = new Built(new Collider[] { empty, null, sphere }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Missing, Is.EqualTo(1));
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.Report.Boxed, Is.EqualTo(0));
            Assert.That(built.Solids, Is.EqualTo(1));
        }

        [Test]
        public void AnInactiveColliderStillGoesIn() {
            var box = Make("box", _root, new Vector3(1, 0, 0), Quaternion.identity, Vector3.one).AddComponent<BoxCollider>();
            box.gameObject.SetActive(false);
            var built = new Built(new Collider[] { box }, _root.worldToLocalMatrix);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.Distance(new Vector3(3, 0, 0)), Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void TheSameCollidersWriteTheSameBlockAndAGridTakesIt() {
            var box = Make("box", _root, new Vector3(1, 1, 1), Quaternion.Euler(0, 30, 0), Vector3.one).AddComponent<BoxCollider>();
            var mesh = Make("mesh", _root, new Vector3(3, 2, 2), Quaternion.identity, Vector3.one).AddComponent<MeshCollider>();
            mesh.sharedMesh = Sheet();
            Collider[] colliders = { box, mesh };
            Assert.That(ColliderShapes.TryWrite(colliders, _root.worldToLocalMatrix, out var block, out var report), Is.True);
            Assert.That(report.Exact, Is.EqualTo(2));
            Assert.That(ColliderShapes.TryWrite(colliders, _root.worldToLocalMatrix, out var again, out _), Is.True);
            Assert.That(again, Is.EqualTo(block));

            using var grid = new WallGrid(new GridShape(0, 0, 0, 0.5f, 10, 8, 8));
            Assert.That(grid.SetShapes(block), Is.True);
            Assert.That(grid.ShapeCount, Is.EqualTo(3));
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Object.DestroyImmediate(mesh.sharedMesh);
        }

        [Test]
        public void ShapesBeyondTheReachAreNotWritten() {
            var far = Make("far", null, new Vector3(5000, 0, 0), Quaternion.identity, Vector3.one).AddComponent<SphereCollider>();
            Assert.That(ColliderShapes.TryWrite(new Collider[] { far }, Matrix4x4.identity, out var block, out var report), Is.False);
            Assert.That(block, Is.Empty);
            Assert.That(report.Exact, Is.EqualTo(1));
        }
    }
}
