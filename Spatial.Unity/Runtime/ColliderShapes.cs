using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yewnyx.Spatial {
    /// <summary>What <see cref="ColliderShapes.Collect(IReadOnlyList{Collider}, Matrix4x4, List{Solid}, List{float}, List{int})"/> did with the colliders it was given.</summary>
    public struct CollectReport {
        /// <summary>Colliders that went in as what they are: a box, a sphere, a capsule, a mesh's triangles or a terrain's heights.</summary>
        public int Exact;
        /// <summary>Colliders that went in as a box around them, because their true shape cannot be held.</summary>
        public int Boxed;
        /// <summary>
        /// Colliders that gave nothing: a mesh collider with no mesh, a terrain collider with no
        /// terrain data, or a collider of an unknown kind that is not active, whose bounds Unity
        /// does not give. Shapes collected with any missing leave something out, and are not to be
        /// trusted to say where there is room.
        /// </summary>
        public int Missing;
        /// <summary>Colliders left out because they lie wholly outside the box that was collected within.</summary>
        public int Outside;
        /// <summary>Triangles left out, of meshes that reach into the box, because they lie wholly outside it.</summary>
        public int TrianglesOutside;
        /// <summary>
        /// Terrains with holes. A heightfield is solid all through, so such a terrain goes in as a
        /// box around it, which closes whatever its holes lead to. It is counted here and in
        /// <see cref="Boxed"/>, or in <see cref="Outside"/> where that box lies outside the box
        /// that was collected within.
        /// </summary>
        public int Holed;
        /// <summary>
        /// Trees painted on terrains whose prefabs have colliders. They collide through the terrain
        /// and are no colliders of their own, so they are not in the shapes: with any of these,
        /// the shapes leave something out.
        /// </summary>
        public int Trees;
    }

    /// <summary>The shapes that <see cref="ColliderShapes"/> collects, as lists that are added to.</summary>
    public sealed class CollectedShapes {
        /// <summary>Boxes, spheres and capsules.</summary>
        public readonly List<Solid> Solids = new List<Solid>();
        /// <summary>Triangle corners, three coordinates each.</summary>
        public readonly List<float> Corners = new List<float>();
        /// <summary>Three corner numbers per triangle.</summary>
        public readonly List<int> Triangles = new List<int>();
        /// <summary>Terrains, with their blocks and kept heights in the two lists after (see <see cref="ShapeSet"/>).</summary>
        public readonly List<HeightField> HeightFields = new List<HeightField>();
        /// <summary>The blocks of all the terrains together. Each terrain's begin at its <see cref="HeightField.FirstBlock"/>.</summary>
        public readonly List<uint> HeightBlocks = new List<uint>();
        /// <summary>The kept heights of all the terrains together. Each terrain's begin at its <see cref="HeightField.FirstHeight"/>.</summary>
        public readonly List<ushort> Heights = new List<ushort>();

        /// <summary>The number of shapes.</summary>
        public int Count => Solids.Count + Triangles.Count / 3 + HeightFields.Count;
    }

    /// <summary>
    /// Turns Unity colliders into the shapes a <see cref="WallGrid"/> takes, in a frame of the caller's.
    /// </summary>
    /// <remarks>
    /// It reads each collider's own data and never asks the physics engine, so it sees only the
    /// colliders it is given. Which colliders to pass, where the result is kept and when it is made
    /// again are the caller's.
    /// <para>
    /// A box, sphere or capsule goes in as the physics engine holds it, which under a turned child
    /// of an unevenly scaled parent is not what the transforms' matrix would make of it: the engine
    /// cannot hold a sheared box, so Unity gives it the transform's world rotation and its lossy
    /// scale, with the centre carried through the full transform. A mesh goes in through the matrix,
    /// as the engine holds a mesh.
    /// </para>
    /// <para>
    /// A collider whose shape cannot be held goes in as a box around it and is never left out, since
    /// leaving one out says there is room where there is none: a convex mesh (which collides as the
    /// hull of its points, not as its triangles), a mesh that cannot be read, and any other kind of
    /// collider. In the editor every mesh can be read; in a player only one imported with Read/Write on.
    /// </para>
    /// <para>
    /// The calls that take a box to collect within leave out what lies wholly outside it, whole
    /// colliders and a mesh's triangles one by one, and count what they left out. That is for a
    /// part of a level far larger than the part: a shape may not be stored more than
    /// <see cref="Shapes.Reach"/> from the frame's origin, and one that is refuses the whole
    /// block. Those calls also take a terrain as a heightfield (see
    /// <see cref="HeightFields.Pack"/>), where the others take it as a box around it.
    /// </para>
    /// </remarks>
    public static class ColliderShapes {
        /// <summary>
        /// How far above a terrain's ground its heightfield's surface may lie. Half of a cell of
        /// half a metre: a difference that a map of such cells does not tell apart.
        /// </summary>
        public const float TerrainBound = 0.25f;

        /// <summary>
        /// The box to collect within for a grid: the grid's own box, grown by as far as its marking
        /// looks for shapes from a cell (the cap and a unit) and a cell more. A cell just inside
        /// the grid's edge then holds its true distance to what lies just outside.
        /// </summary>
        public static Box Within(in GridShape shape, float unit, byte cap) {
            var grow = cap * unit + unit + shape.CellSize;
            return new Box(shape.OriginX - grow, shape.OriginY - grow, shape.OriginZ - grow,
                shape.OriginX + shape.CountX * shape.CellSize + grow, shape.OriginY + shape.CountY * shape.CellSize + grow, shape.OriginZ + shape.CountZ * shape.CellSize + grow);
        }

        /// <summary>
        /// Appends each collider's shape to the lists, in the frame that <paramref name="worldToFrame"/> leads to.
        /// A terrain goes in as a box around it.
        /// </summary>
        /// <param name="colliders">The colliders to take shapes from.</param>
        /// <param name="worldToFrame">
        /// From world space to the grid's frame, such as a root transform's worldToLocalMatrix. It
        /// should turn and move only. If it also scales, sizes follow it; if it shears, a box goes in
        /// as a box around it and a sphere or capsule a little larger than it is.
        /// </param>
        /// <param name="solids">Boxes, spheres and capsules are added here.</param>
        /// <param name="corners">Triangle corners are added here, three coordinates each.</param>
        /// <param name="triangles">Three corner numbers per triangle are added here.</param>
        public static CollectReport Collect(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, List<Solid> solids, List<float> corners, List<int> triangles) {
            if (colliders == null) { throw new ArgumentNullException(nameof(colliders)); }
            if (solids == null) { throw new ArgumentNullException(nameof(solids)); }
            if (corners == null) { throw new ArgumentNullException(nameof(corners)); }
            if (triangles == null) { throw new ArgumentNullException(nameof(triangles)); }
            return _collect(colliders, worldToFrame, false, default, solids, corners, triangles, null);
        }

        /// <summary>
        /// Appends the shape of each collider that reaches into a box of the frame, and leaves the
        /// others out. A terrain goes in as a heightfield.
        /// </summary>
        /// <param name="colliders">The colliders to take shapes from.</param>
        /// <param name="worldToFrame">As for the other Collect.</param>
        /// <param name="within">
        /// The box, in the frame. For a grid, give <see cref="Within"/>. Of a mesh that reaches into
        /// it, the triangles that do not are left out. A terrain goes in whole if any of what it
        /// becomes reaches into the box: its ground, and all that is solid under it.
        /// </param>
        /// <param name="shapes">What is collected is added here.</param>
        public static CollectReport Collect(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, in Box within, CollectedShapes shapes) {
            if (colliders == null) { throw new ArgumentNullException(nameof(colliders)); }
            if (shapes == null) { throw new ArgumentNullException(nameof(shapes)); }
            return _collect(colliders, worldToFrame, true, within, shapes.Solids, shapes.Corners, shapes.Triangles, shapes);
        }

        /// <summary>
        /// Collects the colliders' shapes and writes them as one block of bytes to store (see
        /// <see cref="Shapes.Write(ReadOnlySpan{Solid}, ReadOnlySpan{float}, ReadOnlySpan{int}, Span{int}, Span{byte}, out int)"/>).
        /// Returns false if a shape may not be stored, such as one more than
        /// <see cref="Shapes.Reach"/> from the frame's origin.
        /// </summary>
        public static bool TryWrite(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, out byte[] block, out CollectReport report) {
            var shapes = new CollectedShapes();
            report = Collect(colliders, worldToFrame, shapes.Solids, shapes.Corners, shapes.Triangles);
            return _write(shapes, out block);
        }

        /// <summary>
        /// Collects the shapes of the colliders that reach into a box, as the Collect that takes one
        /// does, and writes them as one block of bytes to store. Returns false if a shape that was
        /// taken may not be stored.
        /// </summary>
        public static bool TryWrite(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, in Box within, out byte[] block, out CollectReport report) {
            var shapes = new CollectedShapes();
            report = Collect(colliders, worldToFrame, within, shapes);
            return _write(shapes, out block);
        }

        private static bool _write(CollectedShapes shapes, out byte[] block) {
            block = Array.Empty<byte>();
            var most = Shapes.BlockLength(shapes.Solids.Count, shapes.Corners.Count / 3, shapes.Triangles.Count / 3,
                shapes.HeightFields.Count, shapes.HeightBlocks.Count, shapes.Heights.Count);
            if (most < 0) { return false; }
            var bytes = new byte[most];
            if (!Shapes.Write(shapes.Solids.ToArray(), shapes.Corners.ToArray(), shapes.Triangles.ToArray(),
                    shapes.HeightFields.ToArray(), shapes.HeightBlocks.ToArray(), shapes.Heights.ToArray(),
                    new int[Shapes.WorkspaceLength(shapes.Count)], bytes, out var length)) { return false; }
            Array.Resize(ref bytes, length);
            block = bytes;
            return true;
        }

        // With cut, only what reaches into the box is taken. With fields, a terrain goes in as a heightfield.
        private static CollectReport _collect(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, bool cut, in Box within,
            List<Solid> solids, List<float> corners, List<int> triangles, CollectedShapes fields) {
            var report = new CollectReport();
            for (var i = 0; i < colliders.Count; i++) {
                var collider = colliders[i];
                if (collider == null) { continue; }
                var transform = collider.transform;
                var before = solids.Count;
                // What the collider counts as if it is taken: exact, or as a box around it.
                var exact = true;
                switch (collider) {
                    case BoxCollider box:
                        exact = _box(transform, box.center, box.size * 0.5f, worldToFrame, solids);
                        break;
                    case SphereCollider sphere:
                        _sphere(transform, sphere, worldToFrame, solids);
                        break;
                    case CapsuleCollider capsule:
                        _capsule(transform, capsule, worldToFrame, solids);
                        break;
                    case MeshCollider mesh:
                        var shared = mesh.sharedMesh;
                        var toFrame = worldToFrame * transform.localToWorldMatrix;
                        if (shared == null) {
                            report.Missing++;
                            continue;
                        }
                        if (!mesh.convex && (shared.isReadable || Application.isEditor)) {
                            var left = _mesh(shared, toFrame, cut, within, corners, triangles, out var taken);
                            if (taken == 0 && left > 0) { report.Outside++; } else { report.Exact++; report.TrianglesOutside += left; }
                            continue;
                        }
                        _around(shared.bounds.center, shared.bounds.extents, toFrame, solids);
                        exact = false;
                        break;
                    case TerrainCollider terrain when fields != null:
                        var data = terrain.terrainData;
                        if (data == null) {
                            report.Missing++;
                            continue;
                        }
                        report.Trees += _trees(data);
                        if (_holed(data)) {
                            report.Holed++;
                        } else {
                            var went = _terrain(transform.position, data, worldToFrame, cut, within, fields);
                            if (went == Went.Outside) { report.Outside++; }
                            if (went == Went.In) { report.Exact++; }
                            if (went != Went.Sheared) { continue; }
                        }
                        // As a box around it: the engine's own where it gives one, and otherwise all
                        // that the terrain's size takes in from its transform's position.
                        if (collider.enabled && collider.gameObject.activeInHierarchy) {
                            _around(collider.bounds.center, collider.bounds.extents, worldToFrame, solids);
                        } else {
                            _around(data.size * 0.5f, data.size * 0.5f, worldToFrame * Matrix4x4.Translate(transform.position), solids);
                        }
                        exact = false;
                        break;
                    default:
                        if (collider is TerrainCollider painted && painted.terrainData != null) { report.Trees += _trees(painted.terrainData); }
                        // Only the world box is known, and Unity gives none for a collider that is not active.
                        if (!(collider.enabled && collider.gameObject.activeInHierarchy)) {
                            report.Missing++;
                            continue;
                        }
                        _around(collider.bounds.center, collider.bounds.extents, worldToFrame, solids);
                        exact = false;
                        break;
                }
                // One solid was added. With a box to collect within, it stays only if it reaches into it.
                if (cut && solids.Count > before && !_touches(_boxOf(solids[solids.Count - 1]), within)) {
                    solids.RemoveAt(solids.Count - 1);
                    report.Outside++;
                } else if (exact) {
                    report.Exact++;
                } else {
                    report.Boxed++;
                }
            }
            return report;
        }

        // The engine's box: the transform's rotation for its axes, its lossy scale for its size, and
        // its centre through the full transform. False if the frame shears, so that it had to go in
        // as a box around it.
        private static bool _box(Transform transform, Vector3 centre, Vector3 half, Matrix4x4 worldToFrame, List<Solid> solids) {
            var rotation = transform.rotation;
            var scale = transform.lossyScale;
            var worldCentre = transform.TransformPoint(centre);
            Vector3 worldU = rotation * Vector3.right, worldV = rotation * Vector3.up, worldW = rotation * Vector3.forward;
            double halfU = Math.Abs((double)half.x * scale.x), halfV = Math.Abs((double)half.y * scale.y), halfW = Math.Abs((double)half.z * scale.z);

            // Into the frame. A frame that only turns and moves leaves the axes at length 1; one that
            // scales lengthens them, and the box with them.
            if (!_axes(worldToFrame, worldU, worldV, worldW, out var u, out var v, out var w, out var scaleU, out var scaleV, out var scaleW)) {
                // The frame shears: the box around the engine's box, corner by corner.
                Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
                for (var corner = 0; corner < 8; corner++) {
                    var world = worldCentre + worldU * (float)((corner & 1) == 0 ? -halfU : halfU) + worldV * (float)((corner & 2) == 0 ? -halfV : halfV) + worldW * (float)((corner & 4) == 0 ? -halfW : halfW);
                    var inFrame = worldToFrame.MultiplyPoint3x4(world);
                    min = Vector3.Min(min, inFrame);
                    max = Vector3.Max(max, inFrame);
                }
                solids.Add(Solid.Box(new Box(min.x, min.y, min.z, max.x, max.y, max.z)));
                return false;
            }
            var at = worldToFrame.MultiplyPoint3x4(worldCentre);
            solids.Add(Solid.Box(at.x, at.y, at.z, u.x, u.y, u.z, v.x, v.y, v.z, w.x, w.y, w.z,
                (float)(halfU * scaleU), (float)(halfV * scaleV), (float)(halfW * scaleW)));
            return true;
        }

        // Three world axes that are square to each other, carried into the frame and brought to
        // length 1, with how much the frame lengthens each. False if the frame shears them, or
        // flattens one to nothing.
        private static bool _axes(Matrix4x4 worldToFrame, Vector3 worldU, Vector3 worldV, Vector3 worldW,
            out Vector3 u, out Vector3 v, out Vector3 w, out double scaleU, out double scaleV, out double scaleW) {
            Vector3 fu = worldToFrame.MultiplyVector(worldU), fv = worldToFrame.MultiplyVector(worldV), fw = worldToFrame.MultiplyVector(worldW);
            double ux = fu.x, uy = fu.y, uz = fu.z, vx = fv.x, vy = fv.y, vz = fv.z, wx = fw.x, wy = fw.y, wz = fw.z;
            scaleU = Math.Sqrt(ux * ux + uy * uy + uz * uz); scaleV = Math.Sqrt(vx * vx + vy * vy + vz * vz); scaleW = Math.Sqrt(wx * wx + wy * wy + wz * wz);
            u = default; v = default; w = default;
            if (!(scaleU > 0d && scaleV > 0d && scaleW > 0d)) { return false; }
            ux /= scaleU; uy /= scaleU; uz /= scaleU;
            vx /= scaleV; vy /= scaleV; vz /= scaleV;
            wx /= scaleW; wy /= scaleW; wz /= scaleW;
            const double Square = 1e-5;
            if (!(Math.Abs(ux * vx + uy * vy + uz * vz) <= Square && Math.Abs(vx * wx + vy * wy + vz * wz) <= Square && Math.Abs(wx * ux + wy * uy + wz * uz) <= Square)) { return false; }
            u = new Vector3((float)ux, (float)uy, (float)uz); v = new Vector3((float)vx, (float)vy, (float)vz); w = new Vector3((float)wx, (float)wy, (float)wz);
            return true;
        }

        // The engine scales a sphere's radius by the largest of its transform's three lossy scales.
        private static void _sphere(Transform transform, SphereCollider sphere, Matrix4x4 worldToFrame, List<Solid> solids) {
            var scale = transform.lossyScale;
            var radius = Math.Abs((double)sphere.radius) * Math.Max(Math.Abs(scale.x), Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)));
            var at = worldToFrame.MultiplyPoint3x4(transform.TransformPoint(sphere.center));
            solids.Add(Solid.Sphere(at.x, at.y, at.z, (float)(radius * _stretch(worldToFrame))));
        }

        // The engine scales a capsule's height by the lossy scale along its own axis and its radius
        // by the larger of the other two, and the height never goes below the two round ends.
        private static void _capsule(Transform transform, CapsuleCollider capsule, Matrix4x4 worldToFrame, List<Solid> solids) {
            var axis = capsule.direction;
            var scale = transform.lossyScale;
            var radius = Math.Abs((double)capsule.radius) * Math.Max(Math.Abs(scale[(axis + 1) % 3]), Math.Abs(scale[(axis + 2) % 3]));
            var half = Math.Max(0d, Math.Abs((double)capsule.height * scale[axis]) * 0.5d - radius);
            var along = transform.rotation * (axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward);
            var centre = transform.TransformPoint(capsule.center);
            Vector3 from = worldToFrame.MultiplyPoint3x4(centre - along * (float)half), to = worldToFrame.MultiplyPoint3x4(centre + along * (float)half);
            solids.Add(Solid.Capsule(from.x, from.y, from.z, to.x, to.y, to.z, (float)(radius * _stretch(worldToFrame))));
        }

        // Adds a mesh's triangles and the corners they use. With cut, a triangle that lies wholly
        // outside the box is left out, and so is a corner that no triangle taken uses. Returns how
        // many triangles were left out.
        private static int _mesh(Mesh mesh, Matrix4x4 toFrame, bool cut, in Box within, List<float> corners, List<int> triangles, out int taken) {
            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            taken = 0;
            if (!cut) {
                var first = corners.Count / 3;
                for (var i = 0; i < vertices.Length; i++) {
                    var at = toFrame.MultiplyPoint3x4(vertices[i]);
                    corners.Add(at.x); corners.Add(at.y); corners.Add(at.z);
                }
                // Whole triangles only, and only corners that are there.
                for (var i = 0; i + 2 < indices.Length; i += 3) {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || (uint)c >= (uint)vertices.Length) { continue; }
                    triangles.Add(first + a); triangles.Add(first + b); triangles.Add(first + c);
                    taken++;
                }
                return 0;
            }
            var inFrame = new Vector3[vertices.Length];
            for (var i = 0; i < vertices.Length; i++) { inFrame[i] = toFrame.MultiplyPoint3x4(vertices[i]); }
            // Where each corner went among the corners, once a triangle that is taken has used it.
            var placed = new int[vertices.Length];
            for (var i = 0; i < placed.Length; i++) { placed[i] = -1; }
            var left = 0;
            for (var i = 0; i + 2 < indices.Length; i += 3) {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || (uint)c >= (uint)vertices.Length) { continue; }
                Vector3 pa = inFrame[a], pb = inFrame[b], pc = inFrame[c];
                Vector3 min = Vector3.Min(pa, Vector3.Min(pb, pc)), max = Vector3.Max(pa, Vector3.Max(pb, pc));
                // A corner that is not a number fails every comparison, and so its triangle is taken: the block is then refused, and not made without it.
                if (min.x > within.MaxX || max.x < within.MinX || min.y > within.MaxY || max.y < within.MinY || min.z > within.MaxZ || max.z < within.MinZ) {
                    left++;
                    continue;
                }
                triangles.Add(_place(a, inFrame, placed, corners)); triangles.Add(_place(b, inFrame, placed, corners)); triangles.Add(_place(c, inFrame, placed, corners));
                taken++;
            }
            return left;
        }

        private static int _place(int corner, Vector3[] inFrame, int[] placed, List<float> corners) {
            if (placed[corner] < 0) {
                placed[corner] = corners.Count / 3;
                var at = inFrame[corner];
                corners.Add(at.x); corners.Add(at.y); corners.Add(at.z);
            }
            return placed[corner];
        }

        private enum Went { In, Outside, Sheared }

        // A terrain as a heightfield. The engine puts a terrain at its transform's position, along
        // the world's axes whatever the transform's turn and scale, and splits each square from its
        // corner at low x and low z to the one at high x and high z.
        private static Went _terrain(Vector3 position, TerrainData data, Matrix4x4 worldToFrame, bool cut, in Box within, CollectedShapes shapes) {
            if (!_axes(worldToFrame, Vector3.right, Vector3.up, Vector3.forward, out var u, out var v, out var w, out var scaleU, out var scaleV, out var scaleW)) { return Went.Sheared; }
            var size = data.size;
            var origin = worldToFrame.MultiplyPoint3x4(position);
            float lengthU = (float)(size.x * scaleU), lengthW = (float)(size.z * scaleW);
            var floor = _floor(origin, u, v, w, lengthU, lengthW);
            if (cut) {
                // Judged by all that it becomes: over its footprint, from as high as a height can
                // stand down to its floor. A grid that lies under the ground is in the solid.
                var top = (float)(size.y * scaleV + TerrainBound);
                Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
                for (var corner = 0; corner < 8; corner++) {
                    var at = origin + u * ((corner & 1) == 0 ? 0f : lengthU) + w * ((corner & 2) == 0 ? 0f : lengthW) + v * ((corner & 4) == 0 ? floor : top);
                    min = Vector3.Min(min, at);
                    max = Vector3.Max(max, at);
                }
                if (!_touches(new Box(min.x, min.y, min.z, max.x, max.y, max.z), within)) { return Went.Outside; }
            }
            var resolution = data.heightmapResolution;
            int count = resolution - 1;
            // Unity gives the heights row by row along z, from 0 to 1 of the terrain's height.
            var raw = data.GetHeights(0, 0, resolution, resolution);
            var heights = new float[resolution * resolution];
            var tall = size.y * scaleV;
            for (var z = 0; z < resolution; z++) {
                for (var x = 0; x < resolution; x++) { heights[z * resolution + x] = (float)(raw[z, x] * tall); }
            }
            var blocks = new uint[HeightFields.BlocksLength(count, count)];
            var kept = new ushort[HeightFields.HeightsLength(count, count)];
            if (blocks.Length == 0 || !HeightFields.Pack(heights, count, count, (float)(size.x / count * scaleU), (float)(size.z / count * scaleW),
                    TerrainBound, blocks, kept, out var field, out var keptCount)) { return Went.Sheared; }
            shapes.HeightFields.Add(field.Placed(origin.x, origin.y, origin.z, u.x, u.y, u.z, v.x, v.y, v.z, w.x, w.y, w.z, floor < field.Lowest ? floor : field.Lowest)
                .Stored(shapes.HeightBlocks.Count, shapes.Heights.Count));
            shapes.HeightBlocks.AddRange(blocks);
            for (var i = 0; i < keptCount; i++) { shapes.Heights.Add(kept[i]); }
            return Went.In;
        }

        // How far down a heightfield can be solid, as a height along its own V: as far as its four
        // lower corners stay a metre inside the reach on every axis of the frame.
        private static float _floor(Vector3 origin, Vector3 u, Vector3 v, Vector3 w, float lengthU, float lengthW) {
            const double Limit = Shapes.Reach - 1d;
            var floor = double.NegativeInfinity;
            for (var corner = 0; corner < 4; corner++) {
                var at = origin + u * ((corner & 1) == 0 ? 0f : lengthU) + w * ((corner & 2) == 0 ? 0f : lengthW);
                for (var axis = 0; axis < 3; axis++) {
                    double along = v[axis], from = at[axis];
                    if (Math.Abs(along) < 1e-6) { continue; }
                    // Going down along V, this axis of the frame runs out at one of its two limits.
                    var until = ((along > 0d ? -Limit : Limit) - from) / along;
                    if (until > floor) { floor = until; }
                }
            }
            return (float)floor;
        }

        private static bool _holed(TerrainData data) {
            var resolution = data.holesResolution;
            var holes = data.GetHoles(0, 0, resolution, resolution);
            for (var z = 0; z < resolution; z++) {
                for (var x = 0; x < resolution; x++) {
                    if (!holes[z, x]) { return true; }
                }
            }
            return false;
        }

        // The painted trees whose prefabs have a collider.
        private static int _trees(TerrainData data) {
            var prototypes = data.treePrototypes;
            if (prototypes == null || prototypes.Length == 0 || data.treeInstanceCount == 0) { return 0; }
            var collides = new bool[prototypes.Length];
            for (var i = 0; i < prototypes.Length; i++) {
                var prefab = prototypes[i].prefab;
                collides[i] = prefab != null && prefab.GetComponentInChildren<Collider>(true) != null;
            }
            var count = 0;
            foreach (var tree in data.treeInstances) {
                if ((uint)tree.prototypeIndex < (uint)collides.Length && collides[tree.prototypeIndex]) { count++; }
            }
            return count;
        }

        // The box along the frame's axes around a box given in another frame: its eight corners, carried over.
        private static void _around(Vector3 centre, Vector3 half, Matrix4x4 toFrame, List<Solid> solids) {
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
            for (var corner = 0; corner < 8; corner++) {
                var at = toFrame.MultiplyPoint3x4(centre + new Vector3((corner & 1) == 0 ? -half.x : half.x, (corner & 2) == 0 ? -half.y : half.y, (corner & 4) == 0 ? -half.z : half.z));
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
            solids.Add(Solid.Box(new Box(min.x, min.y, min.z, max.x, max.y, max.z)));
        }

        // The box along the frame's axes that holds a solid.
        private static Box _boxOf(in Solid solid) {
            switch (solid.Kind) {
                case SolidKind.Box:
                    float reachX = Math.Abs(solid.UX) * solid.HalfU + Math.Abs(solid.VX) * solid.HalfV + Math.Abs(solid.WX) * solid.HalfW;
                    float reachY = Math.Abs(solid.UY) * solid.HalfU + Math.Abs(solid.VY) * solid.HalfV + Math.Abs(solid.WY) * solid.HalfW;
                    float reachZ = Math.Abs(solid.UZ) * solid.HalfU + Math.Abs(solid.VZ) * solid.HalfV + Math.Abs(solid.WZ) * solid.HalfW;
                    return new Box(solid.X - reachX, solid.Y - reachY, solid.Z - reachZ, solid.X + reachX, solid.Y + reachY, solid.Z + reachZ);
                case SolidKind.Capsule:
                    return new Box(Math.Min(solid.X, solid.UX) - solid.HalfU, Math.Min(solid.Y, solid.UY) - solid.HalfU, Math.Min(solid.Z, solid.UZ) - solid.HalfU,
                        Math.Max(solid.X, solid.UX) + solid.HalfU, Math.Max(solid.Y, solid.UY) + solid.HalfU, Math.Max(solid.Z, solid.UZ) + solid.HalfU);
                default:
                    return new Box(solid.X - solid.HalfU, solid.Y - solid.HalfU, solid.Z - solid.HalfU, solid.X + solid.HalfU, solid.Y + solid.HalfU, solid.Z + solid.HalfU);
            }
        }

        // Whether two boxes meet. A side that is not a number meets everything: such a shape is
        // taken, and the block is then refused, and not made without it.
        private static bool _touches(in Box a, in Box b) =>
            !(a.MinX > b.MaxX || a.MaxX < b.MinX || a.MinY > b.MaxY || a.MaxY < b.MinY || a.MinZ > b.MaxZ || a.MaxZ < b.MinZ);

        // The most the frame lengthens anything: 1 for a frame that only turns and moves. For a frame
        // whose three axes are square to each other it is the longest of them; for one that shears it
        // is a bound, so a sphere or capsule comes out no smaller than it is.
        private static double _stretch(Matrix4x4 worldToFrame) {
            double ax = worldToFrame.m00, ay = worldToFrame.m10, az = worldToFrame.m20;
            double bx = worldToFrame.m01, by = worldToFrame.m11, bz = worldToFrame.m21;
            double cx = worldToFrame.m02, cy = worldToFrame.m12, cz = worldToFrame.m22;
            double a = ax * ax + ay * ay + az * az, b = bx * bx + by * by + bz * bz, c = cx * cx + cy * cy + cz * cz;
            var longest = Math.Max(a, Math.Max(b, c));
            var lean = Math.Max(Math.Abs(ax * bx + ay * by + az * bz), Math.Max(Math.Abs(bx * cx + by * cy + bz * cz), Math.Abs(cx * ax + cy * ay + cz * az)));
            return Math.Sqrt(lean <= 1e-5 * longest ? longest : a + b + c);
        }
    }
}
