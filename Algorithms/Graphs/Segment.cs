using System;

namespace Yewnyx.Algorithms.Graphs;

/// <summary>
/// A slice of an array. <c>array.AsSpan(segment)</c> is a view (no copy); <c>array[segment]</c> copies.
/// </summary>
public readonly struct Segment(int offset, int size) : IEquatable<Segment> {
    /// <summary>The index of the first item.</summary>
    public readonly int Offset = offset;
    /// <summary>The number of items; 0 for an empty slice.</summary>
    public readonly int Size = size;

    /// <inheritdoc/>
    public bool Equals(Segment other) => Offset == other.Offset && Size == other.Size;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Segment other && Equals(other);
    /// <summary>Whether both segments have the same <see cref="Offset"/> and <see cref="Size"/>.</summary>
    public static bool operator ==(Segment left, Segment right) => left.Equals(right);
    /// <summary>Whether the segments differ in <see cref="Offset"/> or <see cref="Size"/>.</summary>
    public static bool operator !=(Segment left, Segment right) => !left.Equals(right);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Offset, Size);

    /// <summary>Splits the segment into its offset and size: <c>var (offset, size) = segment;</c>.</summary>
    public void Deconstruct(out int offset, out int size) => (offset, size) = (Offset, Size);
    /// <summary>Makes a segment from a tuple, so <c>(2, 3)</c> can stand for items 2 to 4.</summary>
    public static implicit operator Segment((int offset, int size) tuple) => new(tuple.offset, tuple.size);
    /// <summary>
    /// Makes the range of the segment's items, which is what lets <c>array.AsSpan(segment)</c> (a view) and
    /// <c>array[segment]</c> (a copy) work.
    /// </summary>
    public static implicit operator Range(Segment segment) => new(segment.Offset, segment.Offset + segment.Size);
}