using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cewka.App.Tests;

/// <summary>
/// Kontrolka narysowana poza ekranem prawdziwym rysowaniem Skia, bez okna. Piksele w barwach
/// niepomnożonych przez krycie, więc półprzezroczysty piksel ma barwę tego, co go zakryło.
/// </summary>
internal sealed class ControlPixels
{
    private readonly byte[] _bgra;
    private readonly int _stride;

    private ControlPixels(byte[] bgra, int stride, int width, int height)
    {
        _bgra = bgra;
        _stride = stride;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public Color this[int x, int y]
    {
        get
        {
            var i = y * _stride + x * 4;
            return Color.FromArgb(_bgra[i + 3], _bgra[i + 2], _bgra[i + 1], _bgra[i]);
        }
    }

    /// <summary>Piksele BGRA bez dopełnienia wierszy, na czarnym tle (krycie wymnożone), gotowe dla <c>FrameLuminance</c>.</summary>
    public byte[] PackedBgraOverBlack()
    {
        var packed = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var source = y * _stride + x * 4;
                var target = (y * Width + x) * 4;
                var alpha = _bgra[source + 3];
                packed[target] = (byte)(_bgra[source] * alpha / 255);
                packed[target + 1] = (byte)(_bgra[source + 1] * alpha / 255);
                packed[target + 2] = (byte)(_bgra[source + 2] * alpha / 255);
                packed[target + 3] = 255;
            }
        }

        return packed;
    }

    /// <summary>Rozkłada kontrolkę na zadany rozmiar i rysuje ją w skali 1:1.</summary>
    public static ControlPixels Render(Control control, int width, int height)
    {
        control.Width = width;
        control.Height = height;
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));

        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        target.Render(control);

        using var readable = new WriteableBitmap(target.PixelSize, target.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var buffer = readable.Lock();
        target.CopyPixels(buffer);

        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return new ControlPixels(bytes, buffer.RowBytes, width, height);
    }
}
