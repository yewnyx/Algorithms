using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>What the cells of a slice are coloured by.</summary>
public enum SliceColour { WallDistance, Regions }

/// <summary>
/// How a cell of a <see cref="WallWorld"/> is coloured: a key for each cell and a brush for each key, for each way of
/// colouring. The slices and the 3D view colour from here, so a colouring is made in one place and both show it.
/// </summary>
internal static class SliceColours {
    // Keys for colouring by region: 1 to 254 are regions, then open but unreached, then closed.
    public const int Unreached = 255, Closed = 256, RegionKeys = 257;

    private static IBrush[] _ramp = [];
    private static IBrush[]? _regions;

    /// <summary>The brush for each key a cell can have in a colouring.</summary>
    public static IBrush[] Brushes(WallWorld world, SliceColour colour) => colour == SliceColour.Regions ? RegionBrushes() : Ramp(world.Cap);

    /// <summary>The key of a cell in a colouring: an index into <see cref="Brushes"/>.</summary>
    public static int KeyAt(WallWorld world, SliceColour colour, int cellX, int cellY, int cellZ) =>
        colour == SliceColour.Regions ? world.RegionKey(cellX, cellY, cellZ) : world.CountAt(cellX, cellY, cellZ);

    // The stored count, from 0 to the cap.
    private static IBrush[] Ramp(int cap) {
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

    private static IBrush[] RegionBrushes() {
        if (_regions is { } held) { return held; }
        var brushes = new IBrush[RegionKeys];
        for (var region = 1; region < Unreached; region++) { brushes[region] = new SolidColorBrush(Palette.ToColor(Palette.Island(region - 1, true))); }
        brushes[0] = brushes[Closed] = new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x35));
        brushes[Unreached] = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x62));
        return _regions = brushes;
    }
}
