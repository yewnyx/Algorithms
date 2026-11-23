using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yewnyx.Spatial {
    /// <summary>What <see cref="ColliderShapes.Collect"/> did with the colliders it was given.</summary>
    public struct CollectReport {
        /// <summary>Colliders that went in as what they are: a box, a sphere, a capsule or a mesh's triangles.</summary>
        public int Exact;
        /// <summary>Colliders that went in as a box around them, because their true shape cannot be held.</summary>
        public int Boxed;
        /// <summary>
        /// Colliders that gave nothing: a mesh collider with no mesh, or a collider of an unknown kind
        /// that is not active, whose bounds Unity does not give. Shapes collected with any missing
        /// leave something out, and are not to be trusted to say where there is room.
        /// </summary>
        public int Missing;
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
    /// </remarks>
    public static class ColliderShapes {
        /// <summary>
        /// Appends each collider's shape to the lists, in the frame that <paramref name="worldToFrame"/> leads to.
        /// </summary>
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
            var report = new CollectReport();
            for (var i = 0; i < colliders.Count; i++) {
                var collider = colliders[i];
                if (collider == null) { continue; }
                var transform = collider.transform;
                switch (collider) {
                    case BoxCollider box:
                        if (_box(transform, box.center, box.size * 0.5f, worldToFrame, solids)) { report.Exact++; } else { report.Boxed++; }
                        break;
                    case SphereCollider sphere:
                        _sphere(transform, sphere, worldToFrame, solids);
                        report.Exact++;
                        break;
                    case CapsuleCollider capsule:
                        _capsule(transform, capsule, worldToFrame, solids);
                        report.Exact++;
                        break;
                    case MeshCollider mesh:
                        var shared = mesh.sharedMesh;
                        var toFrame = worldToFrame * transform.localToWorldMatrix;
                        if (shared == null) {
                            report.Missing++;
                        } else if (!mesh.convex && (shared.isReadable || Application.isEditor)) {
                            _mesh(shared, toFrame, corners, triangles);
                            report.Exact++;
                        } else {
                            _around(shared.bounds.center, shared.bounds.extents, toFrame, solids);
                            report.Boxed++;
                        }
                        break;
                    default:
                        // Only the world box is known, and Unity gives none for a collider that is not active.
                        if (collider.enabled && collider.gameObject.activeInHierarchy) {
                            _around(collider.bounds.center, collider.bounds.extents, worldToFrame, solids);
                            report.Boxed++;
                        } else {
                            report.Missing++;
                        }
                        break;
                }
            }
            return report;
        }

        /// <summary>
        /// Collects the colliders' shapes and writes them as one block of bytes to store (see
        /// <see cref="Shapes.Write"/>). Returns false if a shape may not be stored, such as one more
        /// than <see cref="Shapes.Reach"/> from the frame's origin.
        /// </summary>
        public static bool TryWrite(IReadOnlyList<Collider> colliders, Matrix4x4 worldToFrame, out byte[] block, out CollectReport report) {
            var solids = new List<Solid>();
            var corners = new List<float>();
            var triangles = new List<int>();
            report = Collect(colliders, worldToFrame, solids, corners, triangles);
            var count = solids.Count + triangles.Count / 3;
            var most = Shapes.BlockLength(solids.Count, corners.Count / 3, triangles.Count / 3);
            block = Array.Empty<byte>();
            if (most < 0) { return false; }
            var bytes = new byte[most];
            if (!Shapes.Write(solids.ToArray(), corners.ToArray(), triangles.ToArray(), new int[Shapes.WorkspaceLength(count)], bytes, out var length)) { return false; }
            Array.Resize(ref bytes, length);
            block = bytes;
            return true;
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
            Vector3 u = worldToFrame.MultiplyVector(worldU), v = worldToFrame.MultiplyVector(worldV), w = worldToFrame.MultiplyVector(worldW);
            double ux = u.x, uy = u.y, uz = u.z, vx = v.x, vy = v.y, vz = v.z, wx = w.x, wy = w.y, wz = w.z;
            double scaleU = Math.Sqrt(ux * ux + uy * uy + uz * uz), scaleV = Math.Sqrt(vx * vx + vy * vy + vz * vz), scaleW = Math.Sqrt(wx * wx + wy * wy + wz * wz);
            var square = scaleU > 0d && scaleV > 0d && scaleW > 0d;
            if (square) {
                ux /= scaleU; uy /= scaleU; uz /= scaleU;
                vx /= scaleV; vy /= scaleV; vz /= scaleV;
                wx /= scaleW; wy /= scaleW; wz /= scaleW;
                const double Square = 1e-5;
                square = Math.Abs(ux * vx + uy * vy + uz * vz) <= Square && Math.Abs(vx * wx + vy * wy + vz * wz) <= Square && Math.Abs(wx * ux + wy * uy + wz * uz) <= Square;
            }
            if (!square) {
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
            solids.Add(Solid.Box(at.x, at.y, at.z, (float)ux, (float)uy, (float)uz, (float)vx, (float)vy, (float)vz, (float)wx, (float)wy, (float)wz,
                (float)(halfU * scaleU), (float)(halfV * scaleV), (float)(halfW * scaleW)));
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

        private static void _mesh(Mesh mesh, Matrix4x4 toFrame, List<float> corners, List<int> triangles) {
            var first = corners.Count / 3;
            var vertices = mesh.vertices;
            for (var i = 0; i < vertices.Length; i++) {
                var at = toFrame.MultiplyPoint3x4(vertices[i]);
                corners.Add(at.x); corners.Add(at.y); corners.Add(at.z);
            }
            var indices = mesh.triangles;
            // Whole triangles only, and only corners that are there.
            for (var i = 0; i + 2 < indices.Length; i += 3) {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || (uint)c >= (uint)vertices.Length) { continue; }
                triangles.Add(first + a); triangles.Add(first + b); triangles.Add(first + c);
            }
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
