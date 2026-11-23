namespace Yewnyx.Spatial {
    /// <summary>
    /// Ground as a shape: heights over a rectangle of squares, and everything under them solid down
    /// to a floor. A point under the surface is at distance 0.
    /// </summary>
    /// <remarks>
    /// The rectangle lies in the heightfield's own frame, as a turned box does: U and W run along
    /// its two sides and V is up. Square (i, j) spans <see cref="SizeU"/> along U from i and
    /// <see cref="SizeW"/> along W from j, and is two flat triangles, split from its corner at low
    /// U and low W to the one at high U and high W.
    /// The heights are kept in blocks of up to 16 by 16 squares, each block at every height or at
    /// every 2nd, 4th or 8th, whichever <see cref="HeightFields.Pack"/> found close enough. So the
    /// surface kept is not the one given: it is nowhere below it, and nowhere more than
    /// <see cref="Gap"/> above it. Where two blocks meet, their surfaces can differ by a step; the
    /// solid is the blocks' solids together.
    /// Twenty-three fields of four bytes each, so it can be stored as it is. Make one with
    /// <see cref="HeightFields.Pack"/>, then <see cref="Placed"/> and <see cref="Stored"/>; the fields
    /// are for reading one back, to draw it for instance.
    /// </remarks>
    public readonly struct HeightField {
        /// <summary>The corner of the rectangle where the squares are counted from, at height 0.</summary>
        public readonly float X, Y, Z;
        /// <summary>The axis the squares are counted along first, of length 1.</summary>
        public readonly float UX, UY, UZ;
        /// <summary>The axis that heights are measured along, of length 1 and square to the others.</summary>
        public readonly float VX, VY, VZ;
        /// <summary>The axis the squares are counted along second, of length 1 and square to the others.</summary>
        public readonly float WX, WY, WZ;
        /// <summary>A square's size along U and along W.</summary>
        public readonly float SizeU, SizeW;
        /// <summary>How many squares there are along U and along W.</summary>
        public readonly int CountU, CountW;
        /// <summary>A kept height h, from 0 to 65535, stands Lowest + Step * h above the origin along V.</summary>
        public readonly float Lowest, Step;
        /// <summary>How far down the solid reaches, as a height along V. Never above <see cref="Lowest"/>.</summary>
        public readonly float Floor;
        /// <summary>The most the surface kept lies above the heights it was made from.</summary>
        public readonly float Gap;
        /// <summary>Must be 0. It is kept for what a heightfield may come to hold, and one with a bit set is refused.</summary>
        public readonly int Flags;
        /// <summary>Where this heightfield's blocks and kept heights begin among the set's.</summary>
        public readonly int FirstBlock, FirstHeight;

        internal HeightField(float x, float y, float z, float ux, float uy, float uz, float vx, float vy, float vz, float wx, float wy, float wz,
            float sizeU, float sizeW, int countU, int countW, float lowest, float step, float floor, float gap, int flags, int firstBlock, int firstHeight) {
            X = x; Y = y; Z = z;
            UX = ux; UY = uy; UZ = uz; VX = vx; VY = vy; VZ = vz; WX = wx; WY = wy; WZ = wz;
            SizeU = sizeU; SizeW = sizeW; CountU = countU; CountW = countW;
            Lowest = lowest; Step = step; Floor = floor; Gap = gap; Flags = flags;
            FirstBlock = firstBlock; FirstHeight = firstHeight;
        }

        /// <summary>
        /// The same heights somewhere else: with its first corner at a point, its three axes as
        /// given, and solid down to <paramref name="floor"/>, a height along V that is no higher
        /// than <see cref="Lowest"/>. The axes must each have length 1 and be square to each other,
        /// as a box's must.
        /// </summary>
        public HeightField Placed(float x, float y, float z, float ux, float uy, float uz, float vx, float vy, float vz, float wx, float wy, float wz, float floor) =>
            new HeightField(x, y, z, ux, uy, uz, vx, vy, vz, wx, wy, wz, SizeU, SizeW, CountU, CountW, Lowest, Step, floor, Gap, Flags, FirstBlock, FirstHeight);

        /// <summary>
        /// The same heightfield with its blocks and kept heights at other places in a set's spans:
        /// where they were copied to when several heightfields are kept together.
        /// </summary>
        public HeightField Stored(int firstBlock, int firstHeight) =>
            new HeightField(X, Y, Z, UX, UY, UZ, VX, VY, VZ, WX, WY, WZ, SizeU, SizeW, CountU, CountW, Lowest, Step, Floor, Gap, Flags, firstBlock, firstHeight);
    }
}
