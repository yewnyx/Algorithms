using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Yewnyx.Algorithms.Visualizer.Views;

/// <summary>
/// A BGRA pixel buffer the views draw into directly. Drawing tens of thousands of tiny squares
/// through DrawingContext is far too slow; writing pixels is not.
/// </summary>
public sealed class PixelSurface : IDisposable {
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public WriteableBitmap? Bitmap => _bitmap;
    public Span<uint> Pixels => _pixels.AsSpan(0, Width * Height);

    public bool Resize(int width, int height) {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height && _bitmap is not null) { return false; }
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Width = width;
        Height = height;
        if (_pixels.Length < width * height) { _pixels = new uint[width * height]; }
        return true;
    }

    public void Clear(uint argb) => Pixels.Fill(argb);

    public void FillRect(int x, int y, int w, int h, uint argb) {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + w);
        var y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) { return; }
        var pixels = _pixels;
        for (var yy = y0; yy < y1; yy++) {
            pixels.AsSpan(yy * Width + x0, x1 - x0).Fill(argb);
        }
    }

    /// <summary>Copies the pixel buffer into the bitmap. Call once per frame before drawing it.</summary>
    public unsafe void Upload() {
        if (_bitmap is null) { return; }
        using var fb = _bitmap.Lock();
        fixed (uint* src = _pixels) {
            var rowBytes = Width * 4;
            for (var y = 0; y < Height; y++) {
                Buffer.MemoryCopy(src + y * Width, (byte*)fb.Address + y * fb.RowBytes, rowBytes, rowBytes);
            }
        }
    }

    public void Dispose() {
        _bitmap?.Dispose();
        _bitmap = null;
        Width = Height = 0;
    }
}
