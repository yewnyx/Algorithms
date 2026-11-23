using System;

namespace Yewnyx.Spatial {
    /// <summary>
    /// A travel field over one grid: for each cell, how far it is from where the field starts, going
    /// through cells a body can be in, as a count of <see cref="Unit"/>. The shape, the unit and the
    /// array belong together.
    /// </summary>
    /// <remarks>
    /// A cell that holds <see cref="Travel.Unreached"/> is one the field does not reach. Something
    /// with no goal of its own follows a field downhill: see <see cref="Travel.Sample"/>.
    /// </remarks>
    public readonly ref struct TravelField {
        public readonly GridShape Shape;
        /// <summary>Metres of travel per count.</summary>
        public readonly float Unit;
        /// <summary>One count per cell, along x, then y, then z.</summary>
        public readonly ReadOnlySpan<ushort> Distance;

        public TravelField(in GridShape shape, float unit, ReadOnlySpan<ushort> distance) {
            Shape = shape; Unit = unit; Distance = distance;
        }

        /// <summary>Whether the field can be used: a usable shape, a unit above zero, and at least one count per cell.</summary>
        public bool IsValid => Unit > 0f && !float.IsInfinity(Unit) && Shape.CellCount > 0 && Distance.Length >= Shape.CellCount;
    }

    /// <summary>What the cells a <see cref="TravelSeed"/> holds start at.</summary>
    public enum TravelStart {
        /// <summary>Each cell starts at its centre's distance from a point.</summary>
        FromPoint = 0,
        /// <summary>Each cell starts at how far its centre is in front of a plane, and at 0 behind it.</summary>
        FromPlane = 1,
    }

    /// <summary>
    /// Where a travel field starts: the cells whose centres are inside a solid, and the distance
    /// each of them starts at.
    /// </summary>
    /// <remarks>
    /// Starting from a point gives the cells round a goal their true distances to it. Starting from
    /// a plane makes a field lead out through an opening: the cells in front of the plane start at
    /// their distance from it, so the way down is towards it and across.
    /// A cell is started only if the straight way from its centre to the point, or square to the
    /// plane, lies through cells the field can enter; the cells of the solid that are left out are
    /// reached from the others like any cell. So a seed never starts a cell on the far side of a
    /// wall, however large its solid. Twenty-three fields of four bytes each, so it can be stored
    /// as it is.
    /// </remarks>
    public readonly struct TravelSeed {
        public readonly Solid Solid;
        public readonly TravelStart Start;
        /// <summary>The point, or a point of the plane.</summary>
        public readonly float X, Y, Z;
        /// <summary>The way the plane faces: cells on this side of it start above 0. It need not be of length 1.</summary>
        public readonly float NormalX, NormalY, NormalZ;

        private TravelSeed(in Solid solid, TravelStart start, float x, float y, float z, float normalX, float normalY, float normalZ) {
            Solid = solid; Start = start;
            X = x; Y = y; Z = z;
            NormalX = normalX; NormalY = normalY; NormalZ = normalZ;
        }

        /// <summary>The cells inside the solid each start at their distance from a point.</summary>
        public static TravelSeed FromPoint(in Solid solid, float x, float y, float z) => new TravelSeed(solid, TravelStart.FromPoint, x, y, z, 0f, 0f, 0f);

        /// <summary>
        /// The cells inside the solid each start at how far they are in front of the plane through a
        /// point that faces along a normal, and at 0 behind it.
        /// </summary>
        public static TravelSeed FromPlane(in Solid solid, float x, float y, float z, float normalX, float normalY, float normalZ) =>
            new TravelSeed(solid, TravelStart.FromPlane, x, y, z, normalX, normalY, normalZ);
    }
}
