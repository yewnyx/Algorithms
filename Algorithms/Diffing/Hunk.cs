using System;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// One edited region: <c>original[Original]</c> is replaced by <c>revised[Revised]</c>.
/// An insertion has an empty <see cref="Original"/>, a deletion an empty <see cref="Revised"/>;
/// an empty side's offset is where the edit happens.
/// </summary>
public readonly struct Hunk(Segment original, Segment revised) : IEquatable<Hunk> {
    public readonly Segment Original = original;
    public readonly Segment Revised = revised;

    public bool Equals(Hunk other) => Original == other.Original && Revised == other.Revised;
    public override bool Equals(object? obj) => obj is Hunk other && Equals(other);
    public static bool operator ==(Hunk left, Hunk right) => left.Equals(right);
    public static bool operator !=(Hunk left, Hunk right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(Original, Revised);

    public void Deconstruct(out Segment original, out Segment revised) => (original, revised) = (Original, Revised);
}
