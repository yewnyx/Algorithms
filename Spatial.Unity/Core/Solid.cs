namespace Yewnyx.Spatial {
    /// <summary>What a <see cref="Solid"/> is.</summary>
    public enum SolidKind {
        Box = 0,
        Sphere = 1,
        Capsule = 2,
    }

    /// <summary>
    /// A solid shape in a grid's frame: a box at any angle, a sphere or a capsule. A point inside it
    /// is at distance 0.
    /// </summary>
    /// <remarks>
    /// Sixteen fields of four bytes each, so it can be stored as it is. Make one with
    /// <see cref="Box(in Yewnyx.Spatial.Box)"/>, <see cref="Sphere"/> or <see cref="Capsule"/>; the
    /// fields are for reading one back, to draw it for instance, and what each holds depends on
    /// <see cref="Kind"/>. A solid whose kind is not known, or that holds a number that is not
    /// finite, never reads as farther than it is.
    /// </remarks>
    public readonly struct Solid {
        public readonly SolidKind Kind;
        /// <summary>A box's centre, a sphere's centre, or a capsule's first end.</summary>
        public readonly float X, Y, Z;
        /// <summary>A box's first axis, of length 1. A capsule's second end. Not used by a sphere.</summary>
        public readonly float UX, UY, UZ;
        /// <summary>A box's second and third axes, each of length 1 and square to the others. Not used by a sphere or a capsule.</summary>
        public readonly float VX, VY, VZ, WX, WY, WZ;
        /// <summary>A box's half extent along U. A sphere's or a capsule's radius.</summary>
        public readonly float HalfU;
        /// <summary>A box's half extents along V and W. Not used by a sphere or a capsule.</summary>
        public readonly float HalfV, HalfW;

        private Solid(SolidKind kind, float x, float y, float z, float ux, float uy, float uz, float vx, float vy, float vz,
            float wx, float wy, float wz, float halfU, float halfV, float halfW) {
            Kind = kind;
            X = x; Y = y; Z = z;
            UX = ux; UY = uy; UZ = uz; VX = vx; VY = vy; VZ = vz; WX = wx; WY = wy; WZ = wz;
            HalfU = halfU; HalfV = halfV; HalfW = halfW;
        }

        /// <summary>
        /// A box from its centre, its three axes and its half extents along them. The axes must each
        /// have length 1 and be square to each other. The box is the space between three pairs of
        /// planes, each pair square to one axis as given, so axes that are a little off give a box
        /// that much different from the one meant; axes far off are refused when the box is stored.
        /// </summary>
        public static Solid Box(float centreX, float centreY, float centreZ, float ux, float uy, float uz, float vx, float vy, float vz,
            float wx, float wy, float wz, float halfU, float halfV, float halfW) =>
            new Solid(SolidKind.Box, centreX, centreY, centreZ, ux, uy, uz, vx, vy, vz, wx, wy, wz, halfU, halfV, halfW);

        /// <summary>A box along the frame's own axes.</summary>
        public static Solid Box(in Box box) =>
            new Solid(SolidKind.Box, (float)((float)(box.MinX + box.MaxX) * 0.5f), (float)((float)(box.MinY + box.MaxY) * 0.5f), (float)((float)(box.MinZ + box.MaxZ) * 0.5f),
                1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f,
                (float)((float)(box.MaxX - box.MinX) * 0.5f), (float)((float)(box.MaxY - box.MinY) * 0.5f), (float)((float)(box.MaxZ - box.MinZ) * 0.5f));

        public static Solid Sphere(float centreX, float centreY, float centreZ, float radius) =>
            new Solid(SolidKind.Sphere, centreX, centreY, centreZ, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, radius, 0f, 0f);

        /// <summary>A capsule: every point within <paramref name="radius"/> of the segment between two ends.</summary>
        public static Solid Capsule(float fromX, float fromY, float fromZ, float toX, float toY, float toZ, float radius) =>
            new Solid(SolidKind.Capsule, fromX, fromY, fromZ, toX, toY, toZ, 0f, 0f, 0f, 0f, 0f, 0f, radius, 0f, 0f);
    }
}
