using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cewka.App.Services;

/// <summary>
/// Wyciąga z okładki kilka barw, z których powstaje ruchome tło.
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
    /// Mnożnik z ustawienia intensywności barw; jedność zostawia wynik bez zmian.
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
    /// <see cref="Count"/> barw plam sceny z próbki pikseli BGRA: dominujące barwy okładki
    /// w kolejności udziału, razem z szarościami, czernią i bielą, rozjaśnione do łagodnych
    /// pasteli. Okładka czarno-biała, także z szumem barwnym JPEG, daje szarości, kolorowa swoje
    /// odcienie. Piksele przezroczyste się nie liczą, półprzezroczyste ważą tyle, ile kryją.
    /// Barwy są kryjące; przezroczystość daje dopiero siła plam. Gdy barw jest mniej, powtarzają
    /// się po kolei.
    /// </summary>
    public static Color[] SceneBlobs(byte[]? bgra)
    {
        if (bgra is not { Length: >= 4 }) return SceneFallback();

        const int levels = 5;
        var weights = new long[levels * levels * levels];
        var sums = new (long R, long G, long B)[levels * levels * levels];
        long total = 0;

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];

            // Piksel przezroczysty nie ma barwy, półprzezroczysty waży tyle, ile kryje.
            if (a == 0) continue;

            // Składowe są przemnożone przez krycie; przegródkę wyznacza barwa właściwa.
            var bucket = (Unpremultiply(r, a) * levels / 256) * levels * levels
                       + (Unpremultiply(g, a) * levels / 256) * levels
                       + (Unpremultiply(b, a) * levels / 256);

            // Suma składowych przemnożonych razy 255, podzielona przez sumę kryć, daje średnią
            // barw właściwych ważoną kryciem.
            weights[bucket] += a;
            total += a;
            sums[bucket] = (sums[bucket].R + r * 255L, sums[bucket].G + g * 255L, sums[bucket].B + b * 255L);
        }

        if (total == 0) return SceneFallback();

        // Ten sam próg udziału co w palecie okna: kilka barwnych pikseli na czarno-białej okładce
        // nie barwi plam. Najliczniejsza przegródka zostaje zawsze.
        var least = Math.Max(1, total * MinimumSharePercent / 100);

        var found = Enumerable.Range(0, weights.Length)
            .Where(index => weights[index] > 0)
            .OrderByDescending(index => weights[index])
            .Where((index, rank) => rank == 0 || weights[index] >= least)
            .Take(Count)
            .Select(index => Soften(Mean(sums[index], weights[index])))
            .ToArray();

        var result = new Color[Count];
        for (var i = 0; i < Count; i++)
            result[i] = found[i % found.Length];

        return result;
    }

    /// <summary>Najwyższe nasycenie plam sceny; wyżej plamy przestają być łagodne.</summary>
    private const double SceneBlobSaturation = 0.6;

    /// <summary>
    /// Barwa wstęgi Zorzy z plamy sceny. Plamy są jasnymi pastelami do tła, a światło wstęg mieszane z nich
    /// szarzeje, więc odcień zostaje, nasycenie wraca do pełnego zakresu, a jasność schodzi do połowy skali.
    /// Plama szara daje szarą wstęgę.
    /// </summary>
    public static Color Ribbon(Color blob)
    {
        var hsl = blob.ToHsl();
        return HslColor.ToRgb(hsl.H, Math.Min(1, hsl.S / SceneBlobSaturation) * RibbonSaturation, RibbonLightness);
    }

    private const double RibbonSaturation = 0.85;
    private const double RibbonLightness = 0.5;

    /// <summary>
    /// Zostawia odcień, nasycenie przycina do pułapu, a jasność przenosi w pasmo 0,82–0,94
    /// z zachowaniem kolejności: czerń daje jasną szarość, biel prawie biel. Szarość zostaje szara.
    /// </summary>
    private static Color Soften(Color colour)
    {
        var hsl = colour.ToHsl();

        // Nasycenie HSL przy skrajnych jasnościach rośnie z niczego: (0, 0, 1) ma nasycenie pełne.
        // O barwie decyduje więc rozpiętość składowych: poniżej progu szarości plama jest szara,
        // a do jego dwukrotności nasycenie narasta bez skoku.
        var colourful = Math.Clamp((Chroma(colour) - GreyChroma) / (double)GreyChroma, 0, 1);

        return HslColor.ToRgb(hsl.H, Math.Min(hsl.S, SceneBlobSaturation) * colourful, 0.82 + 0.12 * hsl.L);
    }

    /// <summary>Składowa bez przemnożenia przez krycie <paramref name="alpha"/> (1–255).</summary>
    private static int Unpremultiply(int component, int alpha) =>
        Math.Min(255, (component * 255 + alpha / 2) / alpha);

    /// <summary>Średnia przegródki zaokrąglona do najbliższej wartości, nie w dół.</summary>
    private static Color Mean((long R, long G, long B) sum, long count)
    {
        // Przycięcie chroni przed pikselami, których składowa przekracza krycie.
        byte Component(long value) => (byte)Math.Min(255, (value + count / 2) / count);

        return Color.FromRgb(Component(sum.R), Component(sum.G), Component(sum.B));
    }

    /// <summary>Jasne szarości dla okładki bez próbki, jak plamy wzoru przy okładce czarno-białej.</summary>
    private static Color[] SceneFallback() =>
    [
        Color.FromRgb(0xF0, 0xF0, 0xF4),
        Color.FromRgb(0xCE, 0xCE, 0xD6),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xE0, 0xE0, 0xE6),
    ];

    /// <summary>
    /// Próbka pikseli BGRA z obrazu już zdekodowanego (logo stacji); wymaga wątku interfejsu.
    /// Piksele przezroczyste się nie liczą w <see cref="FromPixels"/>.
    /// </summary>
    public static byte[]? SampleImage(IImage image)
    {
        try
        {
            return Rasterise(image, out _);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Zmniejsza zakodowany obraz wprost przy dekodowaniu i zwraca jego piksele w kolejności
    /// BGRA. Nie wymaga wątku interfejsu, więc okładka nie dekoduje się drugi raz, żeby z niej
    /// wyczytać barwy.
    /// </summary>
    public static byte[] Sample(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded);
        using var small = Bitmap.DecodeToWidth(stream, SampleSize, BitmapInterpolationMode.MediumQuality);

        return ReadPixels(small);
    }

    /// <summary>Piksele obrazu w kolejności BGRA, wiersz za wierszem bez dopełnień.</summary>
    internal static unsafe byte[] ReadPixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];

        fixed (byte* pointer = buffer)
        {
            bitmap.CopyPixels(new PixelRect(size), (IntPtr)pointer, buffer.Length, stride);
        }

        // Dekoder oddaje format natywny platformy; paleta liczy w BGRA.
        if (bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888)
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
    /// Dzieli piksele na zgrubną siatkę RGB i zwraca najliczniejsze przegródki. Prawie czarne
    /// i prawie białe są pomijane, bo ma je niemal każda okładka i tło byłoby zawsze szare.
    /// </summary>
    private static Color[] Reduce(byte[] pixels, bool darkTheme, double saturation)
    {
        const int levels = 5;
        var counts = new int[levels * levels * levels];
        var sums = new (long R, long G, long B)[levels * levels * levels];
        long coloured = 0, chromaSum = 0, lumaSum = 0, opaque = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Przezroczyste tło logo nie jest częścią barw.
            if (pixels[i + 3] < 64) continue;

            opaque++;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];

            var maximum = Math.Max(r, Math.Max(g, b));
            var minimum = Math.Min(r, Math.Min(g, b));
            lumaSum += (r * 299 + g * 587 + b * 114) / 1000;

            // Pomijamy skrajne jasności i barwy prawie szare. Szarość ma w HSL odcień zerowy,
            // czyli czerwień, a podbicie nasycenia zrobiłoby z czarno-białej okładki różowe tło.
            if (maximum < 28 || minimum > 232 || maximum - minimum < GreyChroma) continue;

            coloured++;
            chromaSum += maximum - minimum;

            var bucket = (r * levels / 256) * levels * levels
                       + (g * levels / 256) * levels
                       + (b * levels / 256);

            counts[bucket]++;
            sums[bucket] = (sums[bucket].R + r, sums[bucket].G + g, sums[bucket].B + b);
        }

        // Barwa musi być wyraźna: obraz szary z artefaktami kompresji ma barwne piksele, ale mało
        // ich i ledwo widać, więc głosują za szarością z jasności obrazu, nie za odcieniem.
        var total = Math.Max(1, opaque);
        var meanChroma = coloured == 0 ? 0 : chromaSum / coloured;
        var enoughColour = coloured * 100 >= (long)total * NeutralSharePercent;
        if (coloured == 0 || meanChroma < VisibleChroma || (!enoughColour && meanChroma < VividChroma))
            return Neutral(lumaSum / (double)total / 255.0, darkTheme);

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

    /// <summary>Udział barwnych pikseli w obrazie, poniżej którego paleta jest szara, w procentach.</summary>
    private const int NeutralSharePercent = 10;

    /// <summary>Średnia rozpiętość barwnych pikseli, poniżej której barwa jest prawie niewidoczna.</summary>
    private const int VisibleChroma = 32;

    /// <summary>Średnia rozpiętość, od której mały, ale wyraźny akcent barwny zostaje.</summary>
    private const int VividChroma = 90;

    /// <summary>
    /// Cztery szarości z jasności obrazu, w paśmie jasności motywu jak barwy okładki.
    /// </summary>
    private static Color[] Neutral(double luma, bool darkTheme)
    {
        var (low, high) = darkTheme ? (0.18, 0.48) : (0.55, 0.78);
        var baseLightness = darkTheme ? luma * 0.55 + 0.16 : luma * 0.28 + 0.56;
        double[] offsets = [0, -0.06, 0.05, -0.10];

        return offsets
            .Select(offset => Contrast.LimitForBackdrop(
                HslColor.ToRgb(0, 0, Math.Clamp(baseLightness + offset, low, high)), darkTheme))
            .ToArray();
    }

    /// <summary>Najmniejszy udział kubełka w obrazie, w procentach.</summary>
    private const int MinimumSharePercent = 1;

    private static Color Average((long R, long G, long B) sum, int count) =>
        Color.FromRgb((byte)(sum.R / count), (byte)(sum.G / count), (byte)(sum.B / count));

    private static bool IsGrey(Color colour) => Chroma(colour) < GreyChroma;

    /// <summary>Rozpiętość składowych barwy, 0–255; zero dla szarości.</summary>
    private static int Chroma(Color colour) =>
        Math.Max(colour.R, Math.Max(colour.G, colour.B)) - Math.Min(colour.R, Math.Min(colour.G, colour.B));

    /// <summary>
    /// Zachowuje odcień z okładki, resztę ustawia na nowo. Nasycenie rośnie, bo uśrednianie
    /// w przegródce ciągnie barwy ku szarości; jasność trafia w pasmo motywu: barwy głębokie pod
    /// jasnym tekstem, pastele pod ciemnym.
    /// </summary>
    private static Color Enrich(Color colour, bool darkTheme, double intensity)
    {
        var hsl = colour.ToHsl();

        // W motywie jasnym górna granica jasności 0,78, bo jaśniejsze pastele zlewają się z tłem;
        // dolna 0,55 zostawia zapas kontrastu dla ciemnego tekstu. Intensywność mnoży nasycenie
        // i jego pułap, inaczej przy wyraźnych okładkach nie dawałaby nic; 0,95 to twardy hamulec.
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
