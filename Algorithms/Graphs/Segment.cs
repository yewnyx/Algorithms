using System;

namespace Yewnyx.Algorithms.Graphs;

public readonly struct Segment(int offset, int size) : IEquatable<Segment> {
    public readonly int Offset = offset;
    public readonly int Size = size;

    public bool Equals(Segment other) => Offset == other.Offset && Size == other.Size;
    public override bool Equals(object? obj) => obj is Segment other && Equals(other);
    public static bool operator ==(Segment left, Segment right) => left.Equals(right);
    public static bool operator !=(Segment left, Segment right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(Offset, Size);

    public void Deconstruct(out int offset, out int size) => (offset, size) = (Offset, Size);
    public static implicit operator Segment((int offset, int size) tuple) => new(tuple.offset, tuple.size);
    public static implicit operator Range(Segment segment) => new(segment.Offset, segment.Offset + segment.Size);
}