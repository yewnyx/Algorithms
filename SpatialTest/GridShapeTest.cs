using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class GridShapeTest {
    // 4 by 3 by 2 cells of half a metre, with its lowest corner at (10, 20, 30).
    private static readonly GridShape Grid = new(10, 20, 30, 0.5f, 4, 3, 2);

    [Fact]
    public void AShapeKnowsItsCellsAndItsBounds() {
        Grid.CellCount.Should().Be(24);
        Grid.LayerLength.Should().Be(12);
        Grid.Bounds.Should().Be(new Box(10, 20, 30, 12, 21.5f, 31));
    }

    [Fact]
    public void APointGivesTheCentreOfItsCell() {
        Grid.TryCentre(10.1f, 20.9f, 30.6f, out var x, out var y, out var z).Should().BeTrue();
        (x, y, z).Should().Be((10.25f, 20.75f, 30.75f));
        Grid.TryCentre(10, 20, 30, out x, out y, out z).Should().BeTrue("the lowest corner is in the first cell");
        (x, y, z).Should().Be((10.25f, 20.25f, 30.25f));
        Grid.TryCentre(11.5f, 21, 30.5f, out x, out y, out z).Should().BeTrue("a point on a face between two cells is in the higher one");
        (x, y, z).Should().Be((11.75f, 21.25f, 30.75f));
    }

    [Fact]
    public void APointOutsideTheGridHasNoCell() {
        Grid.TryCentre(9.99f, 20.5f, 30.5f, out _, out _, out _).Should().BeFalse();
        Grid.TryCentre(12, 20.5f, 30.5f, out _, out _, out _).Should().BeFalse("the upper faces are outside");
        Grid.TryCentre(11, 21.5f, 30.5f, out _, out _, out _).Should().BeFalse();
        Grid.TryCentre(11, 20.5f, 31, out _, out _, out _).Should().BeFalse();
        Grid.TryCentre(float.NaN, 20.5f, 30.5f, out _, out _, out _).Should().BeFalse();
        Grid.TryCentre(11, float.PositiveInfinity, 30.5f, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void AShapeThatCannotBeUsedHasNoCells() {
        new GridShape(0, 0, 0, 0f, 4, 3, 2).CellCount.Should().Be(0, "a cell size of zero");
        new GridShape(0, 0, 0, -0.5f, 4, 3, 2).CellCount.Should().Be(0);
        new GridShape(0, 0, 0, float.NaN, 4, 3, 2).CellCount.Should().Be(0);
        new GridShape(0, 0, 0, float.PositiveInfinity, 4, 3, 2).CellCount.Should().Be(0);
        new GridShape(float.NaN, 0, 0, 0.5f, 4, 3, 2).CellCount.Should().Be(0);
        new GridShape(0, 0, 0, 0.5f, 4, 0, 2).CellCount.Should().Be(0, "an axis with no cells");
        new GridShape(0, 0, 0, 0.5f, 4, 3, -2).CellCount.Should().Be(0);
        new GridShape(0, 0, 0, 0.5f, 2000, 2000, 2000).CellCount.Should().Be(0, "more cells than an int holds");
        new GridShape(0, 0, 0, 0.5f, 1290, 1290, 1290).CellCount.Should().Be(1290 * 1290 * 1290, "just under what an int holds");
        default(GridShape).CellCount.Should().Be(0);
        default(GridShape).LayerLength.Should().Be(0);
        default(GridShape).TryCentre(0, 0, 0, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ACallerCanWalkAnArrayItOwns() {
        // One value per cell runs along x, then y, then z.
        Grid.IndexOf(0, 0, 0).Should().Be(0);
        Grid.IndexOf(3, 0, 0).Should().Be(3);
        Grid.IndexOf(0, 1, 0).Should().Be(4);
        Grid.IndexOf(0, 0, 1).Should().Be(12);
        Grid.IndexOf(3, 2, 1).Should().Be(23);
        Grid.IndexOf(4, 0, 0).Should().Be(-1, "no such cell");
        Grid.IndexOf(0, -1, 0).Should().Be(-1);
        Grid.IndexOf(0, 0, 2).Should().Be(-1);
        default(GridShape).IndexOf(0, 0, 0).Should().Be(-1);

        Grid.CentreOf(3, 2, 1, out var x, out var y, out var z);
        (x, y, z).Should().Be((11.75f, 21.25f, 30.75f));
        // The same centre a point in that cell gives.
        Grid.TryCentre(11.9f, 21.4f, 30.6f, out var px, out var py, out var pz).Should().BeTrue();
        (px, py, pz).Should().Be((x, y, z));
    }

    [Fact]
    public void AGridOfOneCellHoldsItsOwnPoints() {
        var one = new GridShape(-1, -1, -1, 2f, 1, 1, 1);
        one.CellCount.Should().Be(1);
        one.TryCentre(0.9f, -0.9f, 0, out var x, out var y, out var z).Should().BeTrue();
        (x, y, z).Should().Be((0f, 0f, 0f));
    }
}
