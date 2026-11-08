using Avalonia.Media;
using Yewnyx.Algorithms.Visualizer.Model;

namespace Yewnyx.Algorithms.Visualizer.Views;

public enum ColorMode { Island, Worker, Wave }

/// <summary>Shared colouring rules so the graph panes and the grid agree about every vertex.</summary>
public static class Palette {
    public const uint Background = 0xFF14161B;
    public const uint SingletonDim = 0xFF3A3F4A;
    public const uint SingletonLit = 0xFF9AA3B2;
    public const uint Cursor = 0xFFFFFFFF;

    private static readonly uint[] WorkerColors = [
        0xFF4FC3F7, 0xFFFF8A65, 0xFFAED581, 0xFFBA68C8, 0xFFFFD54F, 0xFF4DB6AC,
        0xFFF06292, 0xFF7986CB, 0xFFA1887F, 0xFF90A4AE, 0xFFDCE775, 0xFF4DD0E1,
        0xFFFFB74D, 0xFF9575CD, 0xFF81C784, 0xFFE57373,
    ];

    public static uint Worker(int worker) => WorkerColors[worker % WorkerColors.Length];

    public static Color ToColor(uint argb) => Color.FromUInt32(argb);

    /// <summary>Hue by golden-ratio stepping so neighbouring islands never look alike.</summary>
    public static uint Island(int island, bool lit) {
        var hue = (island * 0.61803398875) % 1.0;
        return lit ? Hsv(hue, 0.70, 0.95) : Hsv(hue, 0.55, 0.42);
    }

    public static uint Wave(int wave, int waveCount, bool lit) {
        var t = waveCount <= 1 ? 0.0 : wave / (double)(waveCount - 1);
        // Cool to warm across waves.
        var hue = 0.62 - 0.62 * t;
        return lit ? Hsv(hue, 0.75, 0.95) : Hsv(hue, 0.55, 0.42);
    }

    /// <summary>Colour of a vertex given model, scan state and colouring mode.</summary>
    public static uint Vertex(GraphModel model, ScanState? state, ColorMode mode, int v) {
        var worker = state is null ? ScanState.NotProcessed : Volatile.Read(ref state.ProcessedBy[v]);
        var lit = worker != ScanState.NotProcessed;
        var island = model.IslandOf[v];
        switch (mode) {
            case ColorMode.Worker:
                if (lit) { return Worker(worker); }
                return model.IslandSize[island] > 1 ? Island(island, false) : SingletonDim;
            case ColorMode.Wave:
                return Wave(model.WaveOf[island], model.WaveCount, lit);
            default:
                if (model.IslandSize[island] > 1) { return Island(island, lit); }
                return lit ? SingletonLit : SingletonDim;
        }
    }

    public static uint Hsv(double h, double s, double v) {
        var i = (int)Math.Floor(h * 6) % 6;
        var f = h * 6 - Math.Floor(h * 6);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        var (r, g, b) = i switch {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return 0xFF000000u | ((uint)(r * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(b * 255);
    }

    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);
}
