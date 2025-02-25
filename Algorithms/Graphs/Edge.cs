using System;

namespace Yewnyx.Algorithms.Graphs;

public readonly struct Edge(int from, int to) : IEquatable<Edge>, IComparable<Edge> {
    public readonly int From = from;
    public readonly int To = to;

    public bool Equals(Edge other) => From == other.From && To == other.To;
    public override bool Equals(object? obj) => obj is Edge other && Equals(other);
    public static bool operator ==(Edge left, Edge right) => left.Equals(right);
    public static bool operator !=(Edge left, Edge right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(From, To);

    public int CompareTo(Edge other) => From == other.From ? To.CompareTo(other.To) : From.CompareTo(other.From);

    public void Deconstruct(out int from, out int to) => (from, to) = (From, To);
    public static implicit operator Edge((int from, int to) tuple) => new(tuple.from, tuple.to);
}