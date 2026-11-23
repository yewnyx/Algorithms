using System;
using Yewnyx.Algorithms.Graphs;

namespace Yewnyx.Algorithms.Diffing;

/// <summary>
/// One edited region: <c>original[Original]</c> is replaced by <c>revised[Revised]</c>.
/// An insertion has an empty <see cref="Original"/>, a deletion an empty <see cref="Revised"/>;
/// an empty side's offset is where the edit happens.
/// </summary>
public readonly struct Hunk(Segment original, Segment revised) : IEquatable<Hunk> {
    /// <summary>The items of the original sequence that are replaced; empty for an insertion.</summary>
    public readonly Segment Original = original;
    /// <summary>The items of the revised sequence that replace them; empty for a deletion.</summary>
    public readonly Segment Revised = revised;

    /// <inheritdoc/>
    public bool Equals(Hunk other) => Original == other.Original && Revised == other.Revised;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Hunk other && Equals(other);
    /// <summary>Whether both hunks have equal <see cref="Original"/> and <see cref="Revised"/> segments.</summary>
    public static bool operator ==(Hunk left, Hunk right) => left.Equals(right);
    /// <summary>Whether the hunks differ in <see cref="Original"/> or <see cref="Revised"/>.</summary>
    public static bool operator !=(Hunk left, Hunk right) => !left.Equals(right);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Original, Revised);

    /// <summary>Splits the hunk into its two segments: <c>var (original, revised) = hunk;</c>.</summary>
    public void Deconstruct(out Segment original, out Segment revised) => (original, revised) = (Original, Revised);
}
