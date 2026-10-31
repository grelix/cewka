using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okładka: dekodowanie w zmniejszonej rozdzielczości, paleta z próbki, brak pracy przy przewijaniu.</summary>
[Collection(UiCollection.Name)]
public sealed class CoverTests
{
    [Fact]
    public void DuzaOkladkaDekodowanaWRozdzielczosciPlyty() => Ui.Run(() =>
    {
        var cover = MainViewModel.DecodeCover(Png(1600, 1600, Colors.SteelBlue));

        Assert.NotNull(cover);
        using (cover.Display)
        {
            Assert.True(cover.Display.PixelSize.Width <= 640, $"szerokość {cover.Display.PixelSize.Width}");
            Assert.NotNull(cover.Pixels);
            Assert.True(cover.Pixels.Length <= 48 * 48 * 4);
        }
    });

    [Fact]
    public void UszkodzonaOkladkaNieWywracaOdtwarzania() =>
        Assert.Null(MainViewModel.DecodeCover([1, 2, 3, 4, 5]));

    [Fact]
    public void PaletaZProbkiOddajeBarweOkladki() => Ui.Run(() =>
    {
        var pixels = CoverPalette.Sample(Png(200, 200, Color.FromRgb(30, 90, 200)));

        var palette = CoverPalette.FromPixels(pixels, darkTheme: true);

        Assert.Equal(CoverPalette.Count, palette.Length);
        Assert.InRange(palette[0].ToHsl().H, 200, 240);
    });

    [Fact]
    public void SzaraOkladkaDajeNeutralnaPalete() => Ui.Run(() =>
    {
        var grey = new byte[48 * 48 * 4];
        for (var i = 0; i < grey.Length; i += 4)
        {
            grey[i] = grey[i + 1] = grey[i + 2] = 128;
            grey[i + 3] = 255;
        }

        Assert.Equal(CoverPalette.Extract(null, darkTheme: true), CoverPalette.FromPixels(grey, darkTheme: true));
    });

    [Fact]
    public void PrzewiniecieNieRysujeOkladkiOdNowa() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.Title == "a"));
        Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted));
        var cover = h.Player.Cover;
        var title = h.Player.Title;

        h.Player.SeekRelative(10);
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking));
        Ui.WaitUntil(() => false, 200);

        Assert.Same(cover, h.Player.Cover);
        Assert.Equal(title, h.Player.Title);
    });

    private static byte[] Png(int width, int height, Color colour)
    {
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
            context.FillRectangle(new SolidColorBrush(colour), new Rect(0, 0, width, height));

        using var stream = new MemoryStream();
#pragma warning disable CS0618 // zapis PNG z domyślnymi ustawieniami wystarcza do testu
        target.Save(stream);
#pragma warning restore CS0618
        return stream.ToArray();
    }
}
