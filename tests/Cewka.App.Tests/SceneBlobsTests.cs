using Avalonia.Media;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Barwy plam sceny: dominujące barwy okładki razem z szarościami, jasne i łagodne; okładka
/// czarno-biała daje szarości, kolorowa swoje odcienie.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SceneBlobsTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color MidGrey = Color.FromRgb(128, 128, 128);
    private static readonly Color Blue = Color.FromRgb(30, 90, 200);
    private static readonly Color Red = Color.FromRgb(200, 60, 40);

    [Fact]
    public void CzarnoBialaOkladkaDajeJasneSzarosciWKolejnosciUdzialu()
    {
        var blobs = CoverPalette.SceneBlobs(Pixels((Black, 60), (White, 30), (MidGrey, 10)));

        Assert.Equal(CoverPalette.Count, blobs.Length);
        Assert.All(blobs, AssertLightGrey);

        // Czerń ma największy udział i daje najciemniejszą z jasnych szarości, biel najjaśniejszą.
        Assert.True(blobs[0].R < blobs[2].R && blobs[2].R < blobs[1].R, string.Join(", ", blobs));
        Assert.Equal(blobs[0], blobs[3]);
    }

    [Fact]
    public void KolorowaOkladkaZachowujeOdcienieIPowtarzaBrakujace()
    {
        var blobs = CoverPalette.SceneBlobs(Pixels((Black, 50), (Blue, 30), (Red, 20)));

        Assert.Equal(CoverPalette.Count, blobs.Length);
        Assert.All(blobs, AssertLight);

        AssertLightGrey(blobs[0]);
        AssertHue(Blue, blobs[1]);
        AssertHue(Red, blobs[2]);
        Assert.Equal(blobs[0], blobs[3]);

        // Barwa zostaje, ale łagodna: pastel, nie jaskrawa plama.
        Assert.InRange(blobs[1].ToHsl().S, 0.2, 0.7);
    }

    [Fact]
    public void JednaBarwaWypelniaWszystkiePlamy()
    {
        var blobs = CoverPalette.SceneBlobs(Pixels((Blue, 100)));

        Assert.Equal(CoverPalette.Count, blobs.Length);
        Assert.All(blobs, blob => Assert.Equal(blobs[0], blob));
        AssertHue(Blue, blobs[0]);
        AssertLight(blobs[0]);
    }

    [Fact]
    public void KilkaBarwnychPikseliNaCzarnoBialejOkladceNieBarwiPlam()
    {
        var pixels = Pixels((Black, 50), (White, 50));
        for (var i = 0; i < 12; i++)
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2]) = ((byte)255, (byte)0, (byte)0);

        Assert.All(CoverPalette.SceneBlobs(pixels), AssertLightGrey);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(254, 255, 254)]
    [InlineData(250, 245, 255)]
    [InlineData(128, 128, 140)]
    public void PrawieSzaraBarwaDajeSzarosc(byte r, byte g, byte b)
    {
        // W HSL czerń z jedną jednostką błękitu ma pełne nasycenie; plama ma być szara.
        Assert.All(CoverPalette.SceneBlobs(Pixels((Color.FromRgb(r, g, b), 100))), AssertLightGrey);
    }

    [Fact]
    public void ZabarwionePikseleWCzerniIBieliNieBarwiaPlam()
    {
        const int half = 48 * 48 / 2;
        var pixels = Pixels((Black, 50), (White, 50));

        // Po 29 pikseli (1,26 %) wpada do przegródek czerni i bieli i przesuwa ich średnie
        // o jednostkę od szarości.
        Paint(pixels, 0, 29, Color.FromRgb(40, 0, 0));
        Paint(pixels, half, 29, Color.FromRgb(255, 215, 255));

        Assert.All(CoverPalette.SceneBlobs(pixels), AssertLightGrey);
    }

    [Fact]
    public void SzumBarwnyCzarnoBialejOkladkiDajeSzarosci()
    {
        // Szum JPEG z lekkim przesunięciem ku błękitowi, jak po podpróbkowaniu barw.
        var random = new Random(20261101);
        var pixels = Pixels((Black, 50), (White, 50));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var black = pixels[i + 2] == 0;
            pixels[i] = (byte)(black ? random.Next(0, 9) : random.Next(247, 256));
            pixels[i + 1] = (byte)(black ? random.Next(0, 5) : random.Next(251, 256));
            pixels[i + 2] = (byte)(black ? random.Next(0, 5) : random.Next(251, 256));
        }

        Assert.All(CoverPalette.SceneBlobs(pixels), AssertLightGrey);
    }

    [Fact]
    public void PrzezroczystePikseleNieLiczaSieDoCzerni()
    {
        const int count = 48 * 48;
        var pixels = Pixels((Blue, 100));
        Paint(pixels, 0, count * 60 / 100, Blue, alpha: 0);

        var blobs = CoverPalette.SceneBlobs(pixels);

        Assert.All(blobs, blob => AssertHue(Blue, blob));
        Assert.All(blobs, AssertLight);
    }

    [Fact]
    public void CalkiemPrzezroczystyObrazDajePlamyZastepcze()
    {
        var pixels = Pixels((Blue, 100));
        Paint(pixels, 0, 48 * 48, Blue, alpha: 0);

        Assert.Equal(CoverPalette.SceneBlobs(null), CoverPalette.SceneBlobs(pixels));
    }

    [Fact]
    public void PolprzezroczystePikseleLiczaSieWedlugKrycia()
    {
        const int count = 48 * 48;
        var pixels = Pixels((Blue, 40), (Red, 60));

        // Czerwień zajmuje więcej pikseli, ale kryje w jednej czwartej, więc waży mniej niż błękit;
        // jej barwa jest ta sama co czerwieni kryjącej, nie przyciemniona przez krycie.
        Paint(pixels, count * 40 / 100, count - count * 40 / 100, Red, alpha: 64);

        var blobs = CoverPalette.SceneBlobs(pixels);

        AssertHue(Blue, blobs[0]);
        Assert.Equal(CoverPalette.SceneBlobs(Pixels((Red, 100)))[0], blobs[1]);
    }

    [Fact]
    public void BezPikseliPlamySaJasnymiSzarosciami()
    {
        foreach (var pixels in new[] { null, Array.Empty<byte>() })
        {
            var blobs = CoverPalette.SceneBlobs(pixels);

            Assert.Equal(CoverPalette.Count, blobs.Length);
            Assert.All(blobs, blob =>
            {
                Assert.Equal(255, blob.A);
                Assert.InRange(Math.Max(blob.R, Math.Max(blob.G, blob.B)) - Math.Min(blob.R, Math.Min(blob.G, blob.B)), 0, 10);
                Assert.True(blob.ToHsl().L >= 0.8, blob.ToString());
            });
        }
    }

    [Fact]
    public void ModelWidokuPodajeBarwyPlamZOkladki() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var raised = new List<string?>();
        h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(200, 200, Blue)));

        Assert.Equal(CoverPalette.Count, h.Player.SceneBlobs.Count);
        Assert.All(h.Player.SceneBlobs, blob => AssertHue(Blue, blob));
        Assert.Contains(nameof(MainViewModel.SceneBlobs), raised);

        h.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(200, 200, MidGrey)));

        Assert.All(h.Player.SceneBlobs, AssertLightGrey);
    });

    [Fact]
    public void BezProbkiPlamyBioraBarwyZRozmytejOkladki() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var cover = MainViewModel.DecodeCover(CoverTests.Png(200, 200, Blue));
        Assert.NotNull(cover?.Backdrop);

        h.Player.ShowCover(cover with { Pixels = null });

        Assert.All(h.Player.SceneBlobs, blob => AssertHue(Blue, blob));
    });

    [Fact]
    public void OkladkaDomyslnaDajeBarwyPlamZeSwojegoObrazu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        // W jasnym oknie obraz plam powstaje dopiero przy otwartej scenie.
        h.Player.IsSceneOpen = true;

        Assert.Equal(CoverPalette.Count, h.Player.SceneBlobs.Count);
        Assert.All(h.Player.SceneBlobs, AssertLight);
        Assert.NotEqual(CoverPalette.SceneBlobs(null), h.Player.SceneBlobs);
    });

    /// <summary>Piksele BGRA 48 × 48 z barw o podanych udziałach w procentach, po kolei.</summary>
    private static byte[] Pixels(params (Color Colour, int Percent)[] parts)
    {
        const int count = 48 * 48;
        var pixels = new byte[count * 4];
        var index = 0;

        for (var part = 0; part < parts.Length; part++)
        {
            var (colour, percent) = parts[part];
            var end = part == parts.Length - 1 ? count : index + count * percent / 100;
            for (; index < end; index++)
                (pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2], pixels[index * 4 + 3]) =
                    (colour.B, colour.G, colour.R, (byte)255);
        }

        return pixels;
    }

    /// <summary>
    /// Zamalowuje barwą <paramref name="count"/> pikseli od <paramref name="start"/>; składowe
    /// przemnożone przez krycie, jak w bitmapach Skii.
    /// </summary>
    private static void Paint(byte[] pixels, int start, int count, Color colour, byte alpha = 255)
    {
        static byte Premultiply(byte component, byte alpha) => (byte)((component * alpha + 127) / 255);

        for (var index = start; index < start + count; index++)
            (pixels[index * 4], pixels[index * 4 + 1], pixels[index * 4 + 2], pixels[index * 4 + 3]) =
                (Premultiply(colour.B, alpha), Premultiply(colour.G, alpha), Premultiply(colour.R, alpha), alpha);
    }

    private static void AssertLight(Color colour)
    {
        Assert.Equal(255, colour.A);
        Assert.InRange(colour.ToHsl().L, 0.8, 0.96);
    }

    private static void AssertLightGrey(Color colour)
    {
        AssertLight(colour);
        Assert.InRange(Math.Max(colour.R, Math.Max(colour.G, colour.B)) - Math.Min(colour.R, Math.Min(colour.G, colour.B)), 0, 3);
    }

    private static void AssertHue(Color expected, Color actual)
    {
        var difference = Math.Abs(expected.ToHsl().H - actual.ToHsl().H) % 360;
        Assert.True(Math.Min(difference, 360 - difference) <= 4, $"odcień {actual.ToHsl().H:F1} zamiast {expected.ToHsl().H:F1} ({actual})");
    }
}
