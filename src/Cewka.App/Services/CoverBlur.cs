using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Rozmyta okładka pod plamami sceny, liczona raz na utwór. Plamy przerysowują cały ekran
/// w każdej klatce, więc efekt rozmycia na żywo liczyłby się razem z nimi; mała bitmapa
/// rozmyta z góry i powiększona przy wyświetlaniu kosztuje tylko skalowanie.
/// </summary>
public static class CoverBlur
{
    /// <summary>Szerokość rozmytej okładki w pikselach; wysokość wynika z proporcji.</summary>
    public const int Width = 96;

    /// <summary>
    /// Promienie trzech przebiegów rozmycia pudełkowego: wariancje 2 + 2 + 4 dają σ ≈ 2,8 px,
    /// blisko gaussa o σ = 64/2304 szerokości (2,7 px przy 96 px).
    /// </summary>
    private static readonly int[] Radii = [2, 2, 3];

    /// <summary>Rozmyta okładka prosto z zakodowanego obrazu; nie wymaga wątku interfejsu.</summary>
    public static WriteableBitmap FromEncoded(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded);
        using var small = Bitmap.DecodeToWidth(stream, Width, BitmapInterpolationMode.MediumQuality);

        return FromPixels(CoverPalette.ReadPixels(small), small.PixelSize);
    }

    /// <summary>
    /// Rysuje obraz w szerokości <see cref="Width"/> i zwraca jego piksele BGRA. Tylko na wątku
    /// interfejsu, bo <see cref="RenderTargetBitmap"/> wymaga środowiska graficznego.
    /// </summary>
    public static byte[] Rasterise(IImage image, out PixelSize size)
    {
        var height = Math.Max(1, (int)Math.Round(Width * image.Size.Height / Math.Max(1, image.Size.Width)));
        size = new PixelSize(Width, height);

        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            var options = new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality };
            using (context.PushRenderOptions(options))
            {
                context.DrawImage(image, new Rect(image.Size), new Rect(0, 0, size.Width, size.Height));
            }
        }

        return CoverPalette.ReadPixels(target);
    }

    /// <summary>Rozmywa kopię pikseli BGRA i zwraca ją jako bitmapę; źródło zostaje nietknięte.</summary>
    public static WriteableBitmap FromPixels(byte[] bgra, PixelSize size)
    {
        var pixels = (byte[])bgra.Clone();
        Blur(pixels, size.Width, size.Height);

        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            var row = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(pixels, y * row, frame.Address + y * frame.RowBytes, row);
        }

        return bitmap;
    }

    /// <summary>Przebiegi pudełkowe w poziomie i w pionie, w miejscu.</summary>
    private static void Blur(byte[] pixels, int width, int height)
    {
        var scratch = new byte[pixels.Length];

        foreach (var radius in Radii)
        {
            Pass(pixels, scratch, lines: height, length: width, lineStep: width * 4, step: 4, radius);
            Pass(scratch, pixels, lines: width, length: height, lineStep: 4, step: width * 4, radius);
        }
    }

    /// <summary>
    /// Średnia krocząca wzdłuż wierszy albo kolumn. Poza obrazem powtarza się skrajny piksel,
    /// inaczej brzegi ciemniałyby od pustki za krawędzią.
    /// </summary>
    private static void Pass(byte[] source, byte[] target, int lines, int length, int lineStep, int step, int radius)
    {
        var window = 2 * radius + 1;

        for (var line = 0; line < lines; line++)
        for (var channel = 0; channel < 4; channel++)
        {
            var origin = line * lineStep + channel;

            var sum = 0;
            for (var k = -radius; k <= radius; k++)
                sum += source[origin + Math.Clamp(k, 0, length - 1) * step];

            for (var i = 0; i < length; i++)
            {
                target[origin + i * step] = (byte)((sum + window / 2) / window);

                var entering = Math.Min(i + radius + 1, length - 1);
                var leaving = Math.Max(i - radius, 0);
                sum += source[origin + entering * step] - source[origin + leaving * step];
            }
        }
    }
}
