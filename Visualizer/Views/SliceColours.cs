using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// How a cell of a <see cref="WallWorld"/> is coloured: a key for each cell and a brush for each key. The slices and the
/// 3D view colour from here, so a colouring is made in one place and both show it.
/// </summary>
internal static class SliceColours {
    private static IBrush[] _ramp = [];

    /// <summary>The brush for each key a cell can have: the stored count, from 0 to the cap.</summary>
    public static IBrush[] Brushes(WallWorld world) {
        var cap = world.Cap;
        if (_ramp.Length == cap + 1) { return _ramp; }
        var ramp = new IBrush[cap + 1];
        for (var count = 0; count <= cap; count++) {
            // Hot where a shape is at hand, through yellow and green to blue; the cap, which only says "far", is a flat dark slate.
            var t = cap <= 1 ? 0 : Math.Min(1.0, count / (double)(cap - 1));
            var color = count >= cap ? 0xFF1B2735 : Palette.Hsv(0.02 + 0.56 * t, 0.78, 0.92 - 0.38 * t);
            ramp[count] = new SolidColorBrush(Palette.ToColor(color));
        }
        return _ramp = ramp;
    }

    /// <summary>The key of a cell: an index into <see cref="Brushes"/>.</summary>
    public static int KeyAt(WallWorld world, int cellX, int cellY, int cellZ) => world.CountAt(cellX, cellY, cellZ);
}
