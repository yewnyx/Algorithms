using System;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Diffing;

// Helpers shared by the diff algorithms.
internal static class Hunks {
    // Null-safe, and for value types the null checks fold away.
    public static bool Equal<T>(T a, T b) where T : IEquatable<T> => a is null ? b is null : a.Equals(b);

    // Appends a hunk, merging it into the previous one when nothing matches between them.
    public static void Emit(Span<Hunk> hunks, ref int count, int aStart, int aLength, int bStart, int bLength) {
        if (count > 0) {
            var (original, revised) = hunks[count - 1];
            if (original.Offset + original.Size == aStart && revised.Offset + revised.Size == bStart) {
                hunks[count - 1] = new Hunk(
                    new Segment(original.Offset, original.Size + aLength),
                    new Segment(revised.Offset, revised.Size + bLength));
                return;
            }
        }
        hunks[count++] = new Hunk(new Segment(aStart, aLength), new Segment(bStart, bLength));
    }
}
