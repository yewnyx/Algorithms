using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yewnyx.Spatial.Tests {
    public class TerrainShapesTest {
        private readonly List<Object> _made = new List<Object>();
        private Transform _root;

        // Everything in the frame, and more: for collecting with nothing left out.
        private static readonly Box Everywhere = new Box(-1000, -1000, -1000, 1000, 1000, 1000);

        [SetUp]
        public void MakeAFrameThatIsNotTheWorlds() {
            // The grid's frame: moved and turned, so that nothing passes by leaving the frame out.
            _root = Make("root", new Vector3(40, -7, 13), Quaternion.Euler(20, 130, -35)).transform;
        }

        [TearDown]
        public void DestroyWhatWasMade() {
            foreach (var made in _made) { if (made != null) { Object.DestroyImmediate(made); } }
            _made.Clear();
        }

        private GameObject Make(string name, Vector3 position, Quaternion rotation) {
            var made = new GameObject(name);
            _made.Add(made);
            made.transform.SetPositionAndRotation(position, rotation);
            return made;
        }

        // A terrain of 32 by 32 squares, a metre along x and half a metre along z, with its first
        // corner at a point of the world. The heights are given in metres for each corner of a square.
        private TerrainCollider Terrain(Func<int, int, float> height, Vector3 position) {
            var data = new TerrainData();
            _made.Add(data);
            data.heightmapResolution = 33;
            data.size = new Vector3(32, 20, 16);
            var heights = new float[33, 33];
            for (var z = 0; z < 33; z++) {
                for (var x = 0; x < 33; x++) { heights[z, x] = height(x, z) / 20f; }
            }
            data.SetHeights(0, 0, heights);
            var collider = Make("terrain", position, Quaternion.identity).AddComponent<TerrainCollider>();
            collider.terrainData = data;
            Physics.SyncTransforms();
            return collider;
        }

        // Every square a saddle of more than a metre, on a slope that differs along x and z: the two
        // ways to split a square differ by 0.6 m at its middle, and x cannot be taken for z.
        private static float Saddles(int x, int z) => 3f + ((x + z) % 2 == 0 ? 0f : 1.2f) + 0.05f * x + 0.11f * z;

        // Ground that rolls gently enough to be kept at a wide spacing.
        private static float Rolling(int x, int z) => 4f + 1.5f * Mathf.Sin(0.12f * x) * Mathf.Cos(0.1f * z);

        // The collected shapes, built, with the exact distance from a point of the frame.
        private sealed class Built {
            public readonly CollectedShapes Shapes = new CollectedShapes();
            public readonly CollectReport Report;
            private readonly Solid[] _solids;
            private readonly float[] _corners;
            private readonly int[] _triangles;
            private readonly HeightField[] _fields;
            private readonly uint[] _blocks;
            private readonly ushort[] _heights;
            private readonly Box[] _boxes;
            private readonly BoxNode[] _nodes;
            private readonly int[] _order;
            private readonly int _nodeCount;

            public Built(Collider[] colliders, Matrix4x4 worldToFrame, Box within) {
                Report = ColliderShapes.Collect(colliders, worldToFrame, within, Shapes);
                _solids = Shapes.Solids.ToArray(); _corners = Shapes.Corners.ToArray(); _triangles = Shapes.Triangles.ToArray();
                _fields = Shapes.HeightFields.ToArray(); _blocks = Shapes.HeightBlocks.ToArray(); _heights = Shapes.Heights.ToArray();
                var count = Shapes.Count;
                _boxes = new Box[count]; _nodes = new BoxNode[Yewnyx.Spatial.Shapes.NodesLength(count)]; _order = new int[count];
                Assert.That(Yewnyx.Spatial.Shapes.Build(_solids, _corners, _triangles, _fields, _blocks, _heights, _boxes, _nodes, _order,
                    new int[Yewnyx.Spatial.Shapes.WorkspaceLength(count)], out _nodeCount), Is.True);
            }

            public float Distance(Vector3 point) {
                var set = new ShapeSet(_solids, _corners, _triangles, _fields, _blocks, _heights, _boxes, new ReadOnlySpan<BoxNode>(_nodes, 0, _nodeCount), _order);
                Assert.That(Yewnyx.Spatial.Shapes.Distance(set, point.x, point.y, point.z, 100f, out var distance), Is.True);
                return distance;
            }
        }

        // The height of the engine's own surface under a point of the world, by a ray down onto the collider.
        private static float EngineHeight(TerrainCollider terrain, float x, float z) {
            Assert.That(terrain.Raycast(new Ray(new Vector3(x, 500f, z), Vector3.down), out var hit, 1000f), Is.True, $"no ground under {x}, {z}");
            return hit.point.y;
        }

        // Places over the terrain that are on no line between squares, and at no middle of one.
        private static IEnumerable<Vector2> Places(Vector3 position) {
            for (var i = 0; i < 40; i++) {
                for (var j = 0; j < 40; j++) { yield return new Vector2(position.x + 0.31f + i * 0.787f, position.z + 0.13f + j * 0.393f); }
            }
        }

        // The surface kept lies between the engine's and that much above it, at every place. The
        // frame's own lengths are the world's times scale.
        private void LiesOnTheEngines(TerrainCollider terrain, Built built, float above, float scale = 1f) {
            foreach (var place in Places(terrain.transform.position)) {
                var ground = EngineHeight(terrain, place.x, place.y);
                Assert.That(built.Distance(_root.InverseTransformPoint(new Vector3(place.x, ground - 0.01f * scale, place.y))), Is.EqualTo(0f), $"just under the ground at {place}");
                var over = built.Distance(_root.InverseTransformPoint(new Vector3(place.x, ground + (above + 0.01f) * scale, place.y)));
                Assert.That(over, Is.GreaterThan(0f), $"just over where the surface may be at {place}");
                Assert.That(over, Is.LessThanOrEqualTo(above + 0.011f), $"no farther from the surface than from the ground at {place}");
            }
        }

        [Test]
        public void ATerrainGoesInAsTheEnginesSurfaceSplitAsTheEngineSplitsIt() {
            var terrain = Terrain(Saddles, new Vector3(30, -5, 5));
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.Report.Boxed, Is.EqualTo(0));
            Assert.That(built.Shapes.HeightFields.Count, Is.EqualTo(1));
            Assert.That(built.Shapes.Solids.Count, Is.EqualTo(0));
            // Ground this rough is kept at every height, so the surface is the engine's to within a step.
            Assert.That(built.Shapes.Heights.Count, Is.EqualTo(HeightFields.HeightsLength(32, 32)));
            LiesOnTheEngines(terrain, built, 0f);
        }

        [Test]
        public void GentleGroundIsKeptCoarselyAndWithinTheBound() {
            var terrain = Terrain(Rolling, new Vector3(30, -5, 5));
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Shapes.Heights.Count, Is.LessThan(HeightFields.HeightsLength(32, 32) / 4));
            Assert.That(built.Shapes.HeightFields[0].Gap, Is.GreaterThan(0.01f));
            Assert.That(built.Shapes.HeightFields[0].Gap, Is.LessThanOrEqualTo(ColliderShapes.TerrainBound));
            LiesOnTheEngines(terrain, built, ColliderShapes.TerrainBound);
        }

        [Test]
        public void ATerrainTakesItsPlaceFromItsTransformAndNeitherTurnNorScale() {
            var terrain = Terrain(Saddles, new Vector3(30, -5, 5));
            terrain.transform.rotation = Quaternion.Euler(10, 40, 5);
            terrain.transform.localScale = new Vector3(2, 3, 0.5f);
            Physics.SyncTransforms();
            // The engine's surface is where it was: over the first corner, at the first height.
            Assert.That(EngineHeight(terrain, 30.01f, 5.01f), Is.EqualTo(-5f + 3f).Within(0.05f));
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            LiesOnTheEngines(terrain, built, 0f);
        }

        [Test]
        public void TwoTerrainsInOneSetAreEachTheEngines() {
            var rough = Terrain(Saddles, new Vector3(30, -5, 5));
            var gentle = Terrain(Rolling, new Vector3(70, -3, 9));
            var built = new Built(new Collider[] { rough, gentle }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Exact, Is.EqualTo(2));
            Assert.That(built.Shapes.HeightFields.Count, Is.EqualTo(2));
            Assert.That(built.Shapes.HeightFields[1].FirstBlock, Is.EqualTo(4));
            Assert.That(built.Shapes.HeightFields[1].FirstHeight, Is.EqualTo(HeightFields.HeightsLength(32, 32)));
            LiesOnTheEngines(rough, built, 0f);
            LiesOnTheEngines(gentle, built, ColliderShapes.TerrainBound);
        }

        [Test]
        public void AFrameThatScalesEvenlyScalesTheTerrainWithIt() {
            // A length of the frame is two of the world's: the terrain is half the size in it, and so is the bound.
            _root.localScale = Vector3.one * 2f;
            var rough = Terrain(Saddles, new Vector3(30, -5, 5));
            var built = new Built(new Collider[] { rough }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.Shapes.HeightFields[0].SizeU, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(built.Shapes.HeightFields[0].SizeW, Is.EqualTo(0.25f).Within(1e-5f));
            LiesOnTheEngines(rough, built, 0f, 2f);
            Object.DestroyImmediate(rough.gameObject);

            var gentle = Terrain(Rolling, new Vector3(30, -5, 5));
            built = new Built(new Collider[] { gentle }, _root.worldToLocalMatrix, Everywhere);
            LiesOnTheEngines(gentle, built, ColliderShapes.TerrainBound, 2f);
        }

        // The grid's counts when it is marked from the colliders' shapes, collected within a box.
        private static byte[] Marked(Collider[] colliders, GridShape shape, Box within, out CollectReport report) {
            var shapes = new CollectedShapes();
            report = ColliderShapes.Collect(colliders, Matrix4x4.identity, within, shapes);
            using var grid = new WallGrid(shape);
            Assert.That(grid.SetShapes(shapes), Is.True);
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            return grid.Wall.ToArray();
        }

        [Test]
        public void AGridUnderATerrainIsSolidWhetherOrNotTheShapesWereCollectedWithinItsBox() {
            // 24 cells of half a metre each way, from 20 m to 8 m below the terrain's own corner: all of it under the ground.
            var terrain = Terrain(Rolling, Vector3.zero);
            var shape = new GridShape(4, -20, 2, 0.5f, 24, 24, 24);
            var cut = Marked(new Collider[] { terrain }, shape, ColliderShapes.Within(shape, 1f / 128f, 128), out var report);
            Assert.That(report.Exact, Is.EqualTo(1), "the terrain is taken: what is solid under it reaches into the box");
            Assert.That(report.Outside, Is.EqualTo(0));
            Assert.That(cut.Length, Is.EqualTo(13824));
            Assert.That(cut, Is.All.EqualTo(0));
            Assert.That(Marked(new Collider[] { terrain }, shape, new Box(-1000, -1000, -1000, 1000, 1000, 1000), out _), Is.EqualTo(cut));

            // Beside the terrain's footprint and below it, nothing of it is near: the terrain is left out and the cells are open.
            var beside = new GridShape(-20, -20, 2, 0.5f, 24, 24, 24);
            var open = Marked(new Collider[] { terrain }, beside, ColliderShapes.Within(beside, 1f / 128f, 128), out report);
            Assert.That(report.Outside, Is.EqualTo(1));
            Assert.That(open, Is.All.EqualTo(128));
        }

        [Test]
        public void ShapesAtAnyAngleRoundAGridMarkItTheSameCutOrNot() {
            // Long thin boxes and capsules at every angle, on shells round a grid of 12 cells each
            // way: some reach into the box only by an end, and some lie along its faces just outside.
            var shape = new GridShape(-3, -3, -3, 0.5f, 12, 12, 12);
            var colliders = new List<Collider>();
            for (var i = 0; i < 120; i++) {
                // Directions and turns from whole numbers, so that they line up with nothing.
                var towards = new Vector3((i * 7) % 11 - 5, (i * 5) % 9 - 4, (i * 3) % 7 - 3);
                if (towards == Vector3.zero) { towards = Vector3.up; }
                var at = towards.normalized * (3.5f + (i % 8) * 0.7f);
                var made = Make("around " + i, at, Quaternion.Euler(i * 37f, i * 53f, i * 71f));
                if (i % 2 == 0) {
                    made.AddComponent<BoxCollider>().size = new Vector3(4f, 0.2f, 0.3f);
                } else {
                    var capsule = made.AddComponent<CapsuleCollider>();
                    capsule.height = 4f; capsule.radius = 0.15f; capsule.direction = i % 3;
                }
                colliders.Add(made.GetComponent<Collider>());
            }
            // A fat sphere or capsule off each face, with its centre or its axis 0.3 m outside the
            // box that is collected within and its surface 0.9 m inside it, near enough to mark cells.
            for (var face = 0; face < 6; face++) {
                var off = Vector3.zero;
                off[face / 2] = face % 2 == 0 ? 4.8f : -4.8f;
                var made = Make("fat " + face, off, Quaternion.identity);
                if (face < 3) {
                    made.AddComponent<SphereCollider>().radius = 1.2f;
                } else {
                    var capsule = made.AddComponent<CapsuleCollider>();
                    capsule.height = 3f; capsule.radius = 1.2f; capsule.direction = (face / 2 + 1) % 3;
                }
                colliders.Add(made.GetComponent<Collider>());
            }
            var within = ColliderShapes.Within(shape, 1f / 128f, 128);
            Assert.That(within.MaxX, Is.EqualTo(4.508f).Within(1e-3f));
            var cut = Marked(colliders.ToArray(), shape, within, out var report);
            Assert.That(report.Outside, Is.InRange(20, 60), "some are left out and most are not");
            Assert.That(report.Exact + report.Outside, Is.EqualTo(126));
            var all = Marked(colliders.ToArray(), shape, new Box(-1000, -1000, -1000, 1000, 1000, 1000), out report);
            Assert.That(report.Outside, Is.EqualTo(0));
            Assert.That(cut, Is.EqualTo(all));
            var below = 0;
            foreach (var held in all) { if (held < 128) { below++; } }
            Assert.That(below, Is.InRange(800, 1500), "the shapes reach many of the grid's 1,728 cells and not all");
        }

        [Test]
        public void UnderTheGroundIsSolidAllTheWayDown() {
            var terrain = Terrain(Rolling, new Vector3(30, -5, 5));
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            foreach (var depth in new[] { 1f, 30f, 300f, 800f }) {
                Assert.That(built.Distance(_root.InverseTransformPoint(new Vector3(45, -5 - depth, 12))), Is.EqualTo(0f), $"{depth} m under the terrain");
            }
            // Beside it and below its rim, the side is as near as the terrain's edge is.
            Assert.That(built.Distance(_root.InverseTransformPoint(new Vector3(28, -20, 12))), Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void TheCallThatTakesNoBoxStillTakesATerrainAsABoxAroundIt() {
            var terrain = Terrain(Rolling, new Vector3(30, -5, 5));
            List<Solid> solids = new List<Solid>();
            var report = ColliderShapes.Collect(new Collider[] { terrain }, _root.worldToLocalMatrix, solids, new List<float>(), new List<int>());
            Assert.That(report.Boxed, Is.EqualTo(1));
            Assert.That(report.Exact, Is.EqualTo(0));
            Assert.That(solids.Count, Is.EqualTo(1));
        }

        [Test]
        public void ATerrainWithAHoleGoesInAsABoxAndIsCounted() {
            var terrain = Terrain(Rolling, new Vector3(30, -5, 5));
            terrain.terrainData.SetHoles(7, 9, new[,] { { false } });
            Physics.SyncTransforms();
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Holed, Is.EqualTo(1));
            Assert.That(built.Report.Boxed, Is.EqualTo(1));
            Assert.That(built.Report.Exact, Is.EqualTo(0));
            Assert.That(built.Shapes.HeightFields.Count, Is.EqualTo(0));
            Assert.That(built.Shapes.Solids.Count, Is.EqualTo(1));
            // The box holds the ground, hole and all.
            foreach (var place in Places(terrain.transform.position)) {
                Assert.That(built.Distance(_root.InverseTransformPoint(new Vector3(place.x, -5 + 4, place.y))), Is.EqualTo(0f));
            }
        }

        [Test]
        public void ATerrainWithNoDataIsCountedAsMissing() {
            var terrain = Make("empty", Vector3.zero, Quaternion.identity).AddComponent<TerrainCollider>();
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Missing, Is.EqualTo(1));
            Assert.That(built.Shapes.Count, Is.EqualTo(0));
        }

        [Test]
        public void PaintedTreesWithCollidersAreCountedAndNotTaken() {
            var terrain = Terrain(Rolling, new Vector3(30, -5, 5));
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var bush = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _made.Add(trunk); _made.Add(bush);
            Object.DestroyImmediate(bush.GetComponent<Collider>());
            var data = terrain.terrainData;
            data.treePrototypes = new[] { new TreePrototype { prefab = trunk }, new TreePrototype { prefab = bush } };
            data.SetTreeInstances(new[] {
                new TreeInstance { prototypeIndex = 0, position = new Vector3(0.2f, 0, 0.3f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white },
                new TreeInstance { prototypeIndex = 0, position = new Vector3(0.6f, 0, 0.7f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white },
                new TreeInstance { prototypeIndex = 1, position = new Vector3(0.5f, 0, 0.5f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white },
            }, false);
            var built = new Built(new Collider[] { terrain }, _root.worldToLocalMatrix, Everywhere);
            Assert.That(built.Report.Trees, Is.EqualTo(2), "the two trunks, and not the bush");
            Assert.That(built.Report.Exact, Is.EqualTo(1));
            Assert.That(built.Shapes.Solids.Count, Is.EqualTo(0));
        }

        // Two triangles that share an edge: a square of 2 m in the plane where x is 0, cut corner to corner.
        private Mesh Sheet() {
            var mesh = new Mesh { vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 2, 2), new Vector3(0, 0, 2) }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            _made.Add(mesh);
            return mesh;
        }

        [Test]
        public void WhatLiesOutsideTheBoxIsLeftOutAndCounted() {
            // The frame is the world's, and the box is the 10 m cube at its origin.
            var within = new Box(0, 0, 0, 10, 10, 10);
            var inside = Make("inside", new Vector3(5, 5, 5), Quaternion.Euler(0, 30, 0)).AddComponent<BoxCollider>();
            var reaching = Make("reaching", new Vector3(10.4f, 5, 5), Quaternion.identity).AddComponent<SphereCollider>();
            var beyond = Make("beyond", new Vector3(10.6f, 5, 5), Quaternion.identity).AddComponent<SphereCollider>();
            var far = Make("far", new Vector3(5000, 0, 0), Quaternion.identity).AddComponent<CapsuleCollider>();
            var ball = Make("ball", new Vector3(-3, -3, -3), Quaternion.identity).AddComponent<CharacterController>();
            // A sheet tilted about its corner, which is just beyond the box: its first triangle leans back into the box and its second away from it.
            var sheet = Make("sheet", new Vector3(9, 5, 10.2f), Quaternion.Euler(-45, 0, 0)).AddComponent<MeshCollider>();
            sheet.sharedMesh = Sheet();
            var away = Make("away", new Vector3(50, 0, 0), Quaternion.identity).AddComponent<MeshCollider>();
            away.sharedMesh = Sheet();
            var ground = Terrain(Rolling, new Vector3(-20, -8, -3));
            var mountain = Terrain(Rolling, new Vector3(2000, 0, 0));
            Physics.SyncTransforms();
            Collider[] colliders = { inside, reaching, beyond, far, ball, sheet, away, ground, mountain };

            var built = new Built(colliders, Matrix4x4.identity, within);
            Assert.That(built.Report.Exact, Is.EqualTo(4), "the box, the sphere that reaches in, the sheet and the ground");
            Assert.That(built.Report.Outside, Is.EqualTo(5), "the sphere beyond, the capsule, the controller, the other sheet and the mountain");
            Assert.That(built.Report.TrianglesOutside, Is.EqualTo(1));
            Assert.That(built.Report.Boxed, Is.EqualTo(0));
            Assert.That(built.Shapes.Solids.Count, Is.EqualTo(2));
            Assert.That(built.Shapes.Triangles.Count, Is.EqualTo(3));
            Assert.That(built.Shapes.Corners.Count, Is.EqualTo(9), "only the corners of the triangle that was taken");
            Assert.That(built.Shapes.HeightFields.Count, Is.EqualTo(1));
            // The sphere that reaches in is there to be felt from inside the box.
            Assert.That(built.Distance(new Vector3(9.5f, 5, 5)), Is.EqualTo(0.4f).Within(1e-4f));

            // Cut at the box the shapes can be stored. With nothing left out, the far ones refuse the block.
            Assert.That(ColliderShapes.TryWrite(colliders, Matrix4x4.identity, within, out var block, out var report), Is.True);
            Assert.That(report.Outside, Is.EqualTo(5));
            Assert.That(Shapes.TryRead(block, out var read), Is.True);
            Assert.That(read.Count, Is.EqualTo(4));
            Assert.That(ColliderShapes.TryWrite(colliders, Matrix4x4.identity, new Box(-1e6f, -1e6f, -1e6f, 1e6f, 1e6f, 1e6f), out _, out _), Is.False);
            Assert.That(ColliderShapes.TryWrite(colliders, Matrix4x4.identity, out _, out _), Is.False);
        }

        [Test]
        public void TheBoxToCollectWithinForAGridIsTheGridGrownByWhatMarkingLooksWithinAndACell() {
            var within = ColliderShapes.Within(new GridShape(1, 2, 3, 0.5f, 10, 8, 6), 1f / 128f, 128);
            var grow = 1f + 1f / 128f + 0.5f;
            Assert.That(new[] { within.MinX, within.MinY, within.MinZ, within.MaxX, within.MaxY, within.MaxZ },
                Is.EqualTo(new[] { 1 - grow, 2 - grow, 3 - grow, 6 + grow, 6 + grow, 6 + grow }).Within(1e-5f));
        }

        [Test]
        public void TheSameTerrainWritesTheSameBlockAndAGridTakesIt() {
            var terrain = Terrain(Rolling, new Vector3(-4, -3, -2));
            var post = Make("post", new Vector3(3, 3, 3), Quaternion.identity).AddComponent<CapsuleCollider>();
            Collider[] colliders = { terrain, post };
            var shape = new GridShape(0, 0, 0, 0.5f, 16, 12, 16);
            var within = ColliderShapes.Within(shape, 1f / 128f, 128);
            Assert.That(ColliderShapes.TryWrite(colliders, Matrix4x4.identity, within, out var block, out var report), Is.True);
            Assert.That(report.Exact, Is.EqualTo(2));
            Assert.That(ColliderShapes.TryWrite(colliders, Matrix4x4.identity, within, out var again, out _), Is.True);
            Assert.That(again, Is.EqualTo(block));

            using var grid = new WallGrid(shape);
            Assert.That(grid.SetShapes(block), Is.True);
            Assert.That(grid.ShapeCount, Is.EqualTo(2));
            var bytes = grid.ByteCount;
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            // The ground here is between 2.5 and 5.5 m above the terrain's corner, which is 3 m under the grid's.
            Assert.That(grid.TryRoom(new Unity.Mathematics.float3(6, 0.1f, 6), out var room), Is.True);
            Assert.That(room, Is.EqualTo(0f), "under the ground");
            Assert.That(grid.TryRoom(new Unity.Mathematics.float3(6, 5f, 6), out room), Is.True);
            Assert.That(room, Is.GreaterThan(0.9f), "well above it");
            Assert.That(grid.TryDistance(new Unity.Mathematics.float3(6, 0.1f, 6), 10f, out var distance), Is.True);
            Assert.That(distance, Is.EqualTo(0f));
            var fromBlock = grid.Wall.ToArray();

            // The shapes as they were collected give the same grid as the block they write.
            var shapes = new CollectedShapes();
            ColliderShapes.Collect(colliders, Matrix4x4.identity, within, shapes);
            Assert.That(grid.SetShapes(shapes), Is.True);
            Assert.That(grid.ByteCount, Is.EqualTo(bytes));
            grid.ScheduleMark();
            Assert.That(grid.IsMarked, Is.True);
            Assert.That(grid.Wall.ToArray(), Is.EqualTo(fromBlock));
        }
    }
}
