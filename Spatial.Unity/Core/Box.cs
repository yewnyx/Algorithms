#nullable enable
using System;

namespace Yewnyx.Spatial {
    /// <summary>An axis-aligned box, from its lowest corner to its highest.</summary>
    public readonly struct Box : IEquatable<Box> {
        /// <summary>The lowest corner: the least coordinate along each axis.</summary>
        public readonly float MinX, MinY, MinZ;
        /// <summary>The highest corner: the greatest coordinate along each axis.</summary>
        public readonly float MaxX, MaxY, MaxZ;

        /// <summary>Makes a box from its lowest and highest corners. Nothing is checked; the queries say how they read a NaN coordinate.</summary>
        public Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ) {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        /// <summary>Whether all six coordinates are equal, compared with ==: a box with a NaN coordinate is not equal to itself.</summary>
        public bool Equals(Box other) =>
            MinX == other.MinX && MinY == other.MinY && MinZ == other.MinZ &&
            MaxX == other.MaxX && MaxY == other.MaxY && MaxZ == other.MaxZ;

        /// <summary>Whether <paramref name="obj"/> is a <see cref="Box"/> that <see cref="Equals(Box)"/> accepts.</summary>
        public override bool Equals(object? obj) => obj is Box other && Equals(other);
        /// <summary>A hash of the six coordinates.</summary>
        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
        /// <summary>The same as <see cref="Equals(Box)"/>.</summary>
        public static bool operator ==(Box a, Box b) => a.Equals(b);
        /// <summary>The opposite of <see cref="Equals(Box)"/>.</summary>
        public static bool operator !=(Box a, Box b) => !a.Equals(b);
    }
}
