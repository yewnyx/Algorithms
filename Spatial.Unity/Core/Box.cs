#nullable enable
using System;

namespace Yewnyx.Spatial {
    /// <summary>An axis-aligned box, from its lowest corner to its highest.</summary>
    public readonly struct Box : IEquatable<Box> {
        public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ) {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public bool Equals(Box other) =>
            MinX == other.MinX && MinY == other.MinY && MinZ == other.MinZ &&
            MaxX == other.MaxX && MaxY == other.MaxY && MaxZ == other.MaxZ;

        public override bool Equals(object? obj) => obj is Box other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
        public static bool operator ==(Box a, Box b) => a.Equals(b);
        public static bool operator !=(Box a, Box b) => !a.Equals(b);
    }
}
