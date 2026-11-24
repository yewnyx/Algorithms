using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class CellCostTest {
    // 8 by 8 by 4 cells of half a metre from the origin: centres at 0.25, 0.75, 1.25 ...
    private static readonly GridShape Grid = new(0, 0, 0, 0.5f, 8, 8, 4);

    private static byte At(byte[] cost, float x, float y, float z) {
        CellCost.At(Grid, cost, x, y, z, out var value).Should().BeTrue();
        return value;
    }

    // Which cells a fill should have written: those whose centre is inside, by a plain loop.
    private static void FillsExactlyTheCentresInside(Solid solid, ShapeScene scene) {
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, solid, 32, cost, out var written).Should().BeTrue();
        written.Should().Be(cost.Count(value => value == 32)).And.BeGreaterThan(0);
        for (var z = 0; z < 4; z++) {
            for (var y = 0; y < 8; y++) {
                for (var x = 0; x < 8; x++) {
                    float cx = 0.25f + x * 0.5f, cy = 0.25f + y * 0.5f, cz = 0.25f + z * 0.5f;
                    // A turned box's axes are rounded to single, which leaves a point inside it a hair off zero in the loop.
                    var inside = scene.Exact(cx, cy, cz) < 1e-5;
                    cost[x + 8 * (y + 8 * z)].Should().Be(inside ? (byte)32 : (byte)0, $"the centre {cx}, {cy}, {cz}");
                }
            }
        }
    }

    [Fact]
    public void AFillWritesTheCellsWhoseCentresAreInside() {
        Solid[] solids = [
            Solid.Box(new Box(0.6f, 0.6f, 0.1f, 2.3f, 1.8f, 1.2f)),
            ShapeScene.TurnedBox(2, 2, 1, 1.4, 0.45, 0.6, 33, 15),
            Solid.Sphere(2.1f, 1.9f, 0.9f, 1.05f),
            Solid.Capsule(0.5f, 3.5f, 0.5f, 3.5f, 0.5f, 1.5f, 0.4f),
            Solid.Box(new Box(-5, -5, -5, 9, 9, 9)),
        ];
        foreach (var solid in solids) { FillsExactlyTheCentresInside(solid, new ShapeScene([solid])); }
    }

    [Fact]
    public void ALaterFillOverwritesAnEarlierOneWhereTheyOverlap() {
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, Solid.Box(new Box(0, 0, 0, 2, 2, 2)), 32, cost).Should().BeTrue();
        CellCost.Fill(Grid, Solid.Box(new Box(1, 1, 0, 3, 3, 2)), CellCost.Closed, cost).Should().BeTrue();
        At(cost, 0.25f, 0.25f, 0.25f).Should().Be(32, "only the first");
        At(cost, 1.25f, 1.25f, 0.25f).Should().Be(255, "both: the later one");
        At(cost, 2.75f, 2.75f, 0.25f).Should().Be(255, "only the second");
        At(cost, 3.25f, 3.25f, 0.25f).Should().Be(0, "neither");
    }

    [Fact]
    public void ASolidOutsideTheGridWritesNothing() {
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, Solid.Box(new Box(-3, -3, -3, -0.1f, 9, 9)), 32, cost).Should().BeTrue();
        CellCost.Fill(Grid, Solid.Sphere(20, 2, 1, 5), 32, cost, out var written).Should().BeTrue();
        written.Should().Be(0);
        cost.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void ASolidThinnerThanACellMayHoldNoCentre() {
        // A door a tenth of a metre thick between two rows of centres closes nothing.
        var cost = new byte[Grid.CellCount];
        CellCost.Fill(Grid, Solid.Box(new Box(0, 0.95f, 0, 4, 1.05f, 2)), CellCost.Closed, cost, out var written).Should().BeTrue();
        written.Should().Be(0);
        cost.Should().OnlyContain(value => value == 0);
        // A cell written again with what it holds already is written all the same.
        CellCost.Fill(Grid, Solid.Box(new Box(0, 0, 0, 1, 1, 1)), 0, cost, out written).Should().BeTrue();
        written.Should().Be(8);
    }

    [Fact]
    public void AnEmptyArrayMeansEveryCellIsNormal() {
        CellCost.At(Grid, [], 1, 1, 1, out var value).Should().BeTrue();
        value.Should().Be(0);
    }

    [Fact]
    public void ARefusedCallReturnsFalse() {
        var cost = new byte[Grid.CellCount];
        CellCost.At(Grid, cost, -0.1f, 1, 1, out _).Should().BeFalse("outside the grid");
        CellCost.At(Grid, cost, float.NaN, 1, 1, out _).Should().BeFalse();
        CellCost.At(Grid, new byte[Grid.CellCount - 1], 1, 1, 1, out _).Should().BeFalse("an array that is neither empty nor whole");
        CellCost.At(default, cost, 1, 1, 1, out _).Should().BeFalse();

        CellCost.Fill(Grid, Solid.Sphere(1, 1, 1, 1), 32, new byte[Grid.CellCount - 1]).Should().BeFalse("a short array");
        CellCost.Fill(default, Solid.Sphere(1, 1, 1, 1), 32, cost).Should().BeFalse();
        CellCost.Fill(Grid, Solid.Sphere(float.NaN, 1, 1, 1), 32, cost).Should().BeFalse();
        CellCost.Fill(Grid, default, 32, cost, out var written).Should().BeFalse("a box with no axes");
        written.Should().Be(0);
        cost.Should().OnlyContain(value => value == 0, "a refused fill writes nothing");
    }

    [Fact]
    public void AFillSaysHowManyClosedCellsItOpened() {
        var grid = new GridShape(0, 0, 0, 0.5f, 8, 8, 8);
        var cost = new byte[grid.CellCount];
        CellCost.Fill(grid, Solid.Box(new Box(0, 0, 0, 2, 4, 4)), CellCost.Closed, cost, out var closed, out var opened).Should().BeTrue();
        (closed, opened).Should().Be((4 * 8 * 8, 0));
        // Half of those made dear but not closed, and some open cells too: only the closed ones count as opened.
        CellCost.Fill(grid, Solid.Box(new Box(1, 0, 0, 3, 4, 4)), 40, cost, out var written, out opened).Should().BeTrue();
        (written, opened).Should().Be((4 * 8 * 8, 2 * 8 * 8));
        CellCost.Fill(grid, Solid.Box(new Box(0, 0, 0, 4, 4, 4)), CellCost.Closed, cost, out _, out opened).Should().BeTrue();
        opened.Should().Be(0, "closing opens nothing");
    }

}
