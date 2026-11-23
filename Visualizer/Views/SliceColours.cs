using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>What the cells of a slice are coloured by. Routes colours as regions do, with the route drawn over it.</summary>
public enum SliceColour { WallDistance, Regions, Travel, Routes }

/// <summary>
/// How a cell of a <see cref="WallWorld"/> is coloured: a key for each cell and a brush for each key, for each way of
/// colouring. The slices and the 3D view colour from here, so a colouring is made in one place and both show it.
/// </summary>
internal static class SliceColours {
    // Keys for colouring by region: 1 to 254 are regions, then open but unreached, then closed.
    public const int Unreached = 255, Closed = 256, RegionKeys = 257;
    // Keys for colouring by travel: 0 to 47 are bands of distance, then open but not reached by the field, then not open.
    public const int Bands = 48, FieldUnreached = 48, FieldClosed = 49, TravelKeys = 50;

    private static IBrush[] _ramp = [];
    private static IBrush[]? _regions, _travel;

    /// <summary>The brush for each key a cell can have in a colouring.</summary>
    public static IBrush[] Brushes(WallWorld world, SliceColour colour) => colour switch {
        SliceColour.Regions or SliceColour.Routes => RegionBrushes(),
        SliceColour.Travel => TravelBrushes(),
        _ => Ramp(world.Cap),
    };

    /// <summary>The key of a cell in a colouring: an index into <see cref="Brushes"/>.</summary>
    public static int KeyAt(WallWorld world, SliceColour colour, int cellX, int cellY, int cellZ) => colour switch {
        SliceColour.Regions or SliceColour.Routes => world.RegionKey(cellX, cellY, cellZ),
        SliceColour.Travel => world.TravelKey(cellX, cellY, cellZ),
        _ => world.CountAt(cellX, cellY, cellZ),
    };

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

    private static IBrush[] TravelBrushes() {
        if (_travel is { } held) { return held; }
        var brushes = new IBrush[TravelKeys];
        for (var band = 0; band < Bands; band++) {
            // Near the goal bright yellow, far purple; every other band a little darker, so the bands read as contours.
            var t = band / (double)(Bands - 1);
            var color = Palette.Hsv(0.14 + 0.62 * t, 0.78, (0.98 - 0.42 * t) * (band % 2 == 0 ? 1.0 : 0.88));
            brushes[band] = new SolidColorBrush(Palette.ToColor(color));
        }
        brushes[FieldUnreached] = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x62));
        brushes[FieldClosed] = new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x35));
        return _travel = brushes;
    }
}
