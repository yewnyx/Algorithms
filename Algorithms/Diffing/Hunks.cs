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

    // Moves each pure insertion or deletion as late as it can go without changing the result, and
    // merges hunks that end up touching. Works from the last hunk back, so each hunk's limit (the next
    // hunk) is already final. Returns the new count; the hunks stay at the front of the span.
    public static int SlideLate<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<Hunk> hunks, int count)
        where T : IEquatable<T> {
        var write = count;
        for (var read = count - 1; read >= 0; read--) {
            var (original, revised) = hunks[read];
            while (true) {
                // The run of matches after this hunk ends where the next hunk starts.
                var limit = write < count ? hunks[write].Original.Offset : a.Length;
                if (original.Size == 0) {
                    // An insertion moves past a match when it starts with that match's item.
                    while (original.Offset < limit && Equal(b[revised.Offset], b[revised.Offset + revised.Size])) {
                        original = new Segment(original.Offset + 1, 0);
                        revised = new Segment(revised.Offset + 1, revised.Size);
                    }
                } else if (revised.Size == 0) {
                    while (original.Offset + original.Size < limit &&
                           Equal(a[original.Offset], a[original.Offset + original.Size])) {
                        original = new Segment(original.Offset + 1, original.Size);
                        revised = new Segment(revised.Offset + 1, 0);
                    }
                }

                if (write == count) { break; }
                var (nextOriginal, nextRevised) = hunks[write];
                if (original.Offset + original.Size != nextOriginal.Offset) { break; }
                original = new Segment(original.Offset, original.Size + nextOriginal.Size);
                revised = new Segment(revised.Offset, revised.Size + nextRevised.Size);
                write++;
            }
            hunks[--write] = new Hunk(original, revised);
        }

        var kept = count - write;
        if (write > 0) { hunks.Slice(write, kept).CopyTo(hunks); }
        return kept;
    }
}
