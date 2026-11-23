using System;

namespace Yewnyx.Algorithms.Graphs;

/// <summary>A directed edge between two vertices, which are zero-based indexes.</summary>
public readonly struct Edge(int from, int to) : IEquatable<Edge>, IComparable<Edge> {
    /// <summary>The vertex the edge leaves.</summary>
    public readonly int From = from;
    /// <summary>The vertex the edge enters.</summary>
    public readonly int To = to;

    /// <inheritdoc/>
    public bool Equals(Edge other) => From == other.From && To == other.To;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Edge other && Equals(other);
    /// <summary>Whether both edges have the same <see cref="From"/> and <see cref="To"/>.</summary>
    public static bool operator ==(Edge left, Edge right) => left.Equals(right);
    /// <summary>Whether the edges differ in <see cref="From"/> or <see cref="To"/>.</summary>
    public static bool operator !=(Edge left, Edge right) => !left.Equals(right);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(From, To);

    /// <summary>Orders by <see cref="From"/>, then by <see cref="To"/>.</summary>
    public int CompareTo(Edge other) => From == other.From ? To.CompareTo(other.To) : From.CompareTo(other.From);

    /// <summary>Splits the edge into its two vertices: <c>var (from, to) = edge;</c>.</summary>
    public void Deconstruct(out int from, out int to) => (from, to) = (From, To);
    /// <summary>Makes an edge from a tuple, so <c>(0, 1)</c> can stand for 0 -> 1.</summary>
    public static implicit operator Edge((int from, int to) tuple) => new(tuple.from, tuple.to);
}