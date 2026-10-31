using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cewka.App.Services;

/// <summary>
/// Pulls a handful of representative colours out of an album cover, for the animated
/// background to be built from.
/// <para>
/// The previous approach blurred the cover itself into a large bitmap. This produces the
/// same impression — the window takes on the colour of whatever is playing — from four
/// colours instead of a 192-pixel bitmap, and unlike a still image it can move.
/// </para>
/// </summary>
public static class CoverPalette
{
    /// <summary>Working resolution. Colour, not detail, is what is being measured.</summary>
    private const int SampleSize = 48;

    /// <summary>Rozpiętość składowych, poniżej której piksel uznaje się za szary.</summary>
    private const int GreyChroma = 16;

    /// <summary>How many colours the background is built from.</summary>
    public const int Count = 4;

    /// <summary>
    /// Returns <see cref="Count"/> colours ordered from most to least prominent. Falls back
    /// to a neutral set when the image cannot be read.
    /// </summary>
    /// <param name="saturation">
    /// Multiplier from the user's colour-intensity setting. One leaves the result exactly as it
    /// was before the setting existed.
    /// </param>
    public static Color[] Extract(IImage? cover, bool darkTheme, double saturation = 1.0)
    {
        if (cover is null) return Fallback(darkTheme);

        try
        {
            return FromPixels(Rasterise(cover, out _), darkTheme, saturation);
        }
        catch
        {
            return Fallback(darkTheme);
        }
    }

    /// <summary>
    /// Barwy z próbki pikseli BGRA, pobranej raz przez <see cref="Sample"/>. Zmiana motywu
    /// i intensywności liczy paletę od nowa z tej samej próbki, bez dekodowania okładki.
    /// </summary>
    public static Color[] FromPixels(byte[] bgra, bool darkTheme, double saturation = 1.0)
    {
        var colours = Reduce(bgra, darkTheme, saturation);
        return colours.Length >= Count ? colours : Pad(colours, darkTheme);
    }

    /// <summary>
    /// Zmniejsza zakodowany obraz wprost przy dekodowaniu i zwraca jego piksele w kolejności
    /// BGRA. Nie wymaga wątku interfejsu, więc okładka nie dekoduje się drugi raz, żeby z niej
    /// wyczytać barwy.
    /// </summary>
    public static unsafe byte[] Sample(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded);
        using var small = Bitmap.DecodeToWidth(stream, SampleSize, BitmapInterpolationMode.MediumQuality);

        var size = small.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];

        fixed (byte* pointer = buffer)
        {
            small.CopyPixels(new PixelRect(size), (IntPtr)pointer, buffer.Length, stride);
        }

        // Dekoder oddaje format natywny platformy; paleta liczy w BGRA.
        if (small.Format == Avalonia.Platform.PixelFormat.Rgba8888)
            for (var i = 0; i < buffer.Length; i += 4)
                (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);

        return buffer;
    }

    private static unsafe byte[] Rasterise(IImage cover, out int stride)
    {
        stride = SampleSize * 4;

        using var target = new RenderTargetBitmap(new PixelSize(SampleSize, SampleSize), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            var options = new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality };
            using (context.PushRenderOptions(options))
            {
                context.DrawImage(cover, new Rect(cover.Size), new Rect(0, 0, SampleSize, SampleSize));
            }
        }

        var buffer = new byte[stride * SampleSize];
        fixed (byte* pointer = buffer)
        {
            target.CopyPixels(new PixelRect(0, 0, SampleSize, SampleSize), (IntPtr)pointer, buffer.Length, stride);
        }

        return buffer;
    }

    /// <summary>
    /// Buckets the pixels into a coarse RGB grid and returns the busiest buckets.
    /// <para>
    /// Near-black and near-white are skipped: almost every cover has plenty of both, and a
    /// background built from them would be the same grey wash for every album — which is
    /// precisely the problem this replaces.
    /// </para>
    /// </summary>
    private static Color[] Reduce(byte[] pixels, bool darkTheme, double saturation)
    {
        const int levels = 5;
        var counts = new int[levels * levels * levels];
        var sums = new (long R, long G, long B)[levels * levels * levels];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];

            var maximum = Math.Max(r, Math.Max(g, b));
            var minimum = Math.Min(r, Math.Min(g, b));

            // Pomijamy skrajne jasności i barwy prawie szare. Szarość ma w HSL odcień zerowy,
            // czyli czerwień, a podbicie nasycenia zrobiłoby z czarno-białej okładki różowe tło.
            if (maximum < 28 || minimum > 232 || maximum - minimum < GreyChroma) continue;

            var bucket = (r * levels / 256) * levels * levels
                       + (g * levels / 256) * levels
                       + (b * levels / 256);

            counts[bucket]++;
            sums[bucket] = (sums[bucket].R + r, sums[bucket].G + g, sums[bucket].B + b);
        }

        // Kilka barwnych pikseli na czarno-białej okładce (mały napis, szum JPEG) nie może
        // zabarwić całego tła, więc kubełek musi mieć udział w całym obrazie, a jego średnia
        // nie może być szara: szum wokół szarości uśrednia się z powrotem do szarości.
        var least = Math.Max(1, pixels.Length / 4 * MinimumSharePercent / 100);

        return Enumerable.Range(0, counts.Length)
            .Where(index => counts[index] >= least)
            .Select(index => (Count: counts[index], Colour: Average(sums[index], counts[index])))
            .Where(bucket => !IsGrey(bucket.Colour))
            .OrderByDescending(bucket => bucket.Count)
            .Take(Count)
            .Select(bucket => Enrich(bucket.Colour, darkTheme, saturation))
            .ToArray();
    }

    /// <summary>Najmniejszy udział kubełka w obrazie, w procentach.</summary>
    private const int MinimumSharePercent = 1;

    private static Color Average((long R, long G, long B) sum, int count) =>
        Color.FromRgb((byte)(sum.R / count), (byte)(sum.G / count), (byte)(sum.B / count));

    private static bool IsGrey(Color colour) =>
        Math.Max(colour.R, Math.Max(colour.G, colour.B)) - Math.Min(colour.R, Math.Min(colour.G, colour.B)) < GreyChroma;

    /// <summary>
    /// Keeps the hue the cover gave and rewrites the rest.
    /// <para>
    /// Saturation is lifted because averaging inside a bucket pulls colours towards grey.
    /// Lightness is moved into a band that suits the theme: deep colours behind light text,
    /// pastels behind dark text. Without this the same palette would either disappear on a
    /// light background or swallow the text on it.
    /// </para>
    /// </summary>
    private static Color Enrich(Color colour, bool darkTheme, double intensity)
    {
        var hsl = colour.ToHsl();

        // Motyw jasny dostaje mocniejsze nasycenie i niższy zakres jasności niż wcześniej:
        // pastele rozjaśnione do 0,86 zlewały się z tłem pulpitu i barwa okładki ledwie się
        // przebijała. Dolna granica 0,55 zostawia jeszcze zapas kontrastu dla ciemnego tekstu.
        //
        // Ustawienie intensywności mnoży zarówno nasycenie, jak i jego górną granicę. Mnożenie
        // samej wartości przy nieruchomej granicy nie dałoby nic przy ustawieniu intensywnym:
        // barwy z wyraźnych okładek już dziś dobijają do tego pułapu. Twarde 0,95 zostaje jako
        // ostateczny hamulec — powyżej barwa przestaje wyglądać na wziętą ze zdjęcia.
        var ceiling = Math.Min(0.95, (darkTheme ? 0.85 : 0.74) * intensity);
        var saturation = Math.Clamp((hsl.S * 1.5 + 0.10) * intensity, 0, ceiling);
        var lightness = darkTheme
            ? Math.Clamp(hsl.L * 0.55 + 0.16, 0.18, 0.48)
            : Math.Clamp(hsl.L * 0.28 + 0.56, 0.55, 0.78);

        // Pasmo HSL nie wystarcza: żółć i limonka przy tej samej jasności HSL są dla oka znacznie
        // jaśniejsze niż błękit. Ostateczną granicę wyznacza luminancja względna, do której
        // dobrany jest kolor tekstu drugorzędnego.
        return Contrast.LimitForBackdrop(HslColor.ToRgb(hsl.H, saturation, lightness), darkTheme);
    }

    private static Color[] Pad(Color[] colours, bool darkTheme)
    {
        var fallback = Fallback(darkTheme);
        var result = new Color[Count];

        for (var i = 0; i < Count; i++)
            result[i] = i < colours.Length ? colours[i] : fallback[i];

        return result;
    }

    /// <summary>Neutral, theme-appropriate colours for covers that yield nothing usable.</summary>
    private static Color[] Fallback(bool darkTheme) => darkTheme
        ?
        [
            Color.FromRgb(0x3A, 0x4A, 0x66),
            Color.FromRgb(0x24, 0x2C, 0x3E),
            Color.FromRgb(0x4A, 0x3C, 0x58),
            Color.FromRgb(0x1E, 0x28, 0x32),
        ]
        :
        [
            Color.FromRgb(0x9F, 0xBC, 0xE4),
            Color.FromRgb(0xC2, 0xCA, 0xDF),
            Color.FromRgb(0xD5, 0xBE, 0xDD),
            Color.FromRgb(0xAE, 0xCA, 0xD3),
        ];
}
