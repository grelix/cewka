using Avalonia.Media;
using Cewka.App.Services;
using SkiaSharp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okładki czarno-białe, także z szumem barwnym kompresji, dają szare tło, nie barwne.</summary>
[Collection(UiCollection.Name)]
public sealed class CoverPaletteNeutralTests
{
    private const int Size = 48;

    private static void AssertNeutral(Color[] palette)
    {
        Assert.Equal(CoverPalette.Count, palette.Length);
        foreach (var colour in palette)
        {
            var chroma = Math.Max(colour.R, Math.Max(colour.G, colour.B)) - Math.Min(colour.R, Math.Min(colour.G, colour.B));
            Assert.True(chroma <= 3, $"barwa {colour} nie jest szara");
        }
    }

    private static byte[] Gradient(Func<int, int, (int R, int G, int B)> pixel)
    {
        var bgra = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var (r, g, b) = pixel(x, y);
            var i = (y * Size + x) * 4;
            bgra[i] = (byte)Math.Clamp(b, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(g, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(r, 0, 255);
            bgra[i + 3] = 255;
        }

        return bgra;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SzumBarwnyOMalejAmplitudzieDajeSzarePaleta(bool dark) => Ui.Run(() =>
    {
        var random = new Random(20261105);
        var pixels = Gradient((x, _) =>
        {
            var grey = 40 + x * 4;
            return (grey + random.Next(-22, 23), grey + random.Next(-22, 23), grey + random.Next(-22, 23));
        });

        AssertNeutral(CoverPalette.FromPixels(pixels, dark));
    });

    [Fact]
    public void BlokoweZabarwienieJpegNaSzarymGradiencieDajeSzarePaleta() => Ui.Run(() =>
    {
        var random = new Random(20261106);
        var tints = new (int R, int B)[(Size / 8) * (Size / 8)];
        for (var i = 0; i < tints.Length; i++)
            tints[i] = (random.Next(-14, 15), random.Next(-14, 15));

        var pixels = Gradient((x, y) =>
        {
            var tint = tints[(y / 8) * (Size / 8) + x / 8];
            var grey = 30 + (x + y) * 2;
            return (grey + tint.R, grey, grey + tint.B);
        });

        AssertNeutral(CoverPalette.FromPixels(pixels, darkTheme: true));
    });

    [Fact]
    public void PrawdziwyJpegZSzaregoGradientuZSzumemDajeSzarePaleta() => Ui.Run(() =>
    {
        var random = new Random(20261107);
        using var source = new SKBitmap(400, 400);
        for (var y = 0; y < 400; y++)
        for (var x = 0; x < 400; x++)
        {
            var grey = (x + y) * 255 / 798;
            var noise = random.Next(-30, 31);
            source.SetPixel(x, y, new SKColor((byte)Math.Clamp(grey + noise, 0, 255), (byte)Math.Clamp(grey, 0, 255), (byte)Math.Clamp(grey - noise, 0, 255)));
        }

        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 25);

        AssertNeutral(CoverPalette.FromPixels(CoverPalette.Sample(data.ToArray()), darkTheme: false));
    });

    [Fact]
    public void JasnaSzaraOkladkaDajeJasniejszeTloNizCiemna() => Ui.Run(() =>
    {
        var bright = CoverPalette.FromPixels(Gradient((_, _) => (230, 230, 230)), darkTheme: true);
        var dim = CoverPalette.FromPixels(Gradient((_, _) => (40, 40, 40)), darkTheme: true);

        Assert.True(bright[0].ToHsl().L > dim[0].ToHsl().L);
    });

    [Fact]
    public void MalyWyraznyAkcentNaSzarejOkladceZostajeBarwa() => Ui.Run(() =>
    {
        var pixels = Gradient((x, y) => x < 6 && y < 6 ? (220, 30, 30) : (128, 128, 128));

        var palette = CoverPalette.FromPixels(pixels, darkTheme: true);

        Assert.True(palette[0].ToHsl().H is < 20 or > 340);
    });

    [Fact]
    public void KolorowaOkladkaDajeTeSameBarwyCoDotad() => Ui.Run(() =>
    {
        var pixels = Gradient((x, _) => x < 24 ? (30, 90, 200) : (200, 60, 40));

        var palette = CoverPalette.FromPixels(pixels, darkTheme: true);

        Assert.Contains(palette, c => c.ToHsl().H is > 200 and < 240);
        Assert.Contains(palette, c => c.ToHsl().H is < 20 or > 340);
    });
}
