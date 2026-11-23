using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class SizeClassTest {
    // Half-metre cells, counts of 1/128 m, and a cap of 128 counts: 1 m.
    private static readonly GridShape Grid = new(0, 0, 0, 0.5f, 8, 8, 8);
    private static WallMap Map(float unit = 1f / 128f, byte cap = 128) => new(Grid, unit, cap, default);

    [Fact]
    public void AClassHoldsWhatItsRadiusNeedsAndIsPromised() {
        SizeClass.TryMake(0.51f, Map(), out var size).Should().BeTrue("the map need not be filled");
        size.Radius.Should().Be(0.51f);
        // Between two open centres half a metre apart: sqrt(0.51^2 - 0.25^2).
        ((double)size.Guarantee).Should().BeApproximately(0.4445, 1e-4);
        // A sample of a leg: sqrt(0.51^2 + (0.5 / 8)^2), and the slack.
        ((double)size.LegNeed).Should().BeApproximately(0.5138 + Shapes.Slack, 1e-4);
        // The least counts that prove 0.51, sqrt(0.51^2 + 0.25^2) = 0.568 and sqrt(0.51^2 + 0.125) = 0.621.
        size.Cell.Should().Be(66);
        size.Plane.Should().Be(73);
        size.Space.Should().Be(80);
        ((double)size.Cell / 128).Should().BeGreaterThanOrEqualTo(0.51);
        ((double)(size.Cell - 1) / 128).Should().BeLessThan(0.51);
        // And the cap that lets a leg's sample be proven in open space: sqrt(0.5143^2 + 0.125) = 0.624.
        size.Cap.Should().Be(80);
        size.Fits(Map()).Should().BeTrue();
    }

    [Fact]
    public void TheNeedsOfOtherRadiiMatchThePlansTable() {
        SizeClass.TryMake(0.33f, Map(), out var small).Should().BeTrue();
        ((double)small.Guarantee).Should().BeApproximately(0.2154, 1e-4);
        // For this class the leg's sample in open space needs a count more of the cap than its
        // diagonal step does: sqrt(0.3363^2 + 0.125) = 0.488, against 0.484.
        small.Space.Should().Be(62);
        small.Cap.Should().Be(63);
        SizeClass.TryMake(0.33f, Map(cap: 63), out _).Should().BeTrue();
        SizeClass.TryMake(0.33f, Map(cap: 62), out _).Should().BeFalse("enough for the diagonal step, not for a leg in open space");
        // The room at a point is what the cells prove less the slack, so the cap covers the slack
        // as well. For a radius of 0.301 that tips it one count: 61, where the leg alone needs 60.
        SizeClass.TryMake(0.301f, Map(), out var tipped).Should().BeTrue();
        tipped.Space.Should().Be(60);
        tipped.Cap.Should().Be(61);
        SizeClass.TryMake(0.301f, Map(cap: 60), out _).Should().BeFalse();
        ((double)small.Plane / 128).Should().BeInRange(0.414, 0.414 + 1.0 / 128);
        ((double)small.Space / 128).Should().BeInRange(0.484, 0.484 + 1.0 / 128);
        SizeClass.TryMake(0.58f, Map(), out var large).Should().BeTrue();
        ((double)large.Guarantee).Should().BeApproximately(0.5233, 1e-4);
        ((double)large.Plane / 128).Should().BeInRange(0.6316, 0.6316 + 1.0 / 128);
        ((double)large.Space / 128).Should().BeInRange(0.6794, 0.6794 + 1.0 / 128);
    }

    [Fact]
    public void ARadiusOfHalfACellOrLessIsRefused() {
        // Its guarantee would be nothing: a sheet could lie between two of its open cells.
        SizeClass.TryMake(0.25f, Map(), out var size).Should().BeFalse("exactly half a cell");
        size.Fits(Map()).Should().BeFalse("a class that was refused fits no map");
        SizeClass.TryMake(0.2f, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(0f, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(-1f, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(float.NaN, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(float.PositiveInfinity, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(0.2501f, Map(), out _).Should().BeTrue("just above half a cell");
    }

    [Fact]
    public void AClassThatNeedsMoreThanTheCapCanSayIsRefused() {
        // The 0.51 m class needs a cap of 80 counts at 1/128 m.
        SizeClass.TryMake(0.51f, Map(cap: 80), out _).Should().BeTrue();
        SizeClass.TryMake(0.51f, Map(cap: 79), out _).Should().BeFalse("one count short");
        // A large body on a map capped at 1 m: it would be told it fits where it may not.
        SizeClass.TryMake(1.57f, Map(), out _).Should().BeFalse();
        SizeClass.TryMake(1.57f, Map(cap: 255), out var large).Should().BeTrue();
        large.Cap.Should().BeGreaterThan(128);
        // A map that cannot be used.
        SizeClass.TryMake(0.51f, Map(unit: 0f), out _).Should().BeFalse();
        SizeClass.TryMake(0.51f, Map(cap: 0), out _).Should().BeFalse();
        SizeClass.TryMake(0.51f, new WallMap(default, 1f / 128f, 128, default), out _).Should().BeFalse("no cell size");
    }

    [Fact]
    public void AClassFitsOnlyAMapLikeTheOneItWasMadeFor() {
        SizeClass.TryMake(0.51f, Map(), out var size).Should().BeTrue();
        size.Fits(Map()).Should().BeTrue();
        size.Fits(Map(cap: 255)).Should().BeTrue("a higher cap is enough");
        size.Fits(Map(cap: 79)).Should().BeFalse("a lower cap than the class needs");
        size.Fits(Map(unit: 1f / 64f)).Should().BeFalse("another unit: its counts would mean other distances");
        size.Fits(new WallMap(new GridShape(0, 0, 0, 0.25f, 8, 8, 8), 1f / 128f, 128, default)).Should().BeFalse("another cell size: its guarantee would not hold");
        default(SizeClass).Fits(Map()).Should().BeFalse();
    }
}
