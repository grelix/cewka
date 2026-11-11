using Avalonia;
using Avalonia.Media;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Rozmyta okładka sceny: mała bitmapa liczona raz na utwór, gładka krawędź, brzegi bez obwódki.</summary>
[Collection(UiCollection.Name)]
public sealed class CoverBlurTests
{
    [Fact]
    public void OkladkaDostajeRozmytaWersjeSzerokosci96() => Ui.Run(() =>
    {
        // Poza wątkiem interfejsu, jak w odczycie okładki bieżącego utworu.
        var encoded = CoverTests.Png(1600, 800, Colors.SteelBlue);
        var cover = Task.Run(() => MainViewModel.DecodeCover(encoded)).GetAwaiter().GetResult();

        Assert.NotNull(cover);
        using (cover.Display)
        using (cover.Backdrop)
        {
            Assert.NotNull(cover.Backdrop);
            Assert.Equal(new PixelSize(CoverBlur.Width, CoverBlur.Width / 2), cover.Backdrop.PixelSize);
        }
    });

    [Fact]
    public void SpoznionaOkladkaZwalniaTakzeRozmyta() => Ui.Run(() =>
    {
        // Odczyt okładki modelu widoku z podstawionym plikiem: pierwszy utwór kończy się
        // dopiero po zamówieniu drugiego.
        using var firstReleased = new ManualResetEventSlim();
        var late = MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.SteelBlue));
        var current = MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.OrangeRed));
        Assert.NotNull(late?.Backdrop);
        Assert.NotNull(current?.Backdrop);

        var loader = MainViewModel.CreateCoverLoader(path =>
        {
            if (path != "pierwszy") return current;
            Assert.True(firstReleased.Wait(TimeSpan.FromSeconds(10)), "odczyt nie został zwolniony");
            return late;
        });
        var shown = new List<DecodedCover?>();

        var first = loader.LoadAsync("pierwszy", shown.Add);
        var second = loader.LoadAsync("drugi", shown.Add);
        Assert.True(Ui.WaitUntil(() => second.IsCompleted));

        firstReleased.Set();
        Assert.True(Ui.WaitUntil(() => first.IsCompleted));

        Assert.Equal(new[] { current }, shown);
        Assert.True(CoverTests.IsDisposed(late.Display));
        Assert.True(CoverTests.IsDisposed(late.Backdrop));
        Assert.False(CoverTests.IsDisposed(current.Backdrop));

        current.Discard();
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RozmycieWygladzaOstraKrawedz(bool verticalEdge) => Ui.Run(() =>
    {
        const int width = 96, height = 64;
        var length = verticalEdge ? width : height;
        var edge = length / 2;

        // Wzdłuż osi przejścia czerń do połowy, dalej biel.
        int Along(int pixel) => verticalEdge ? pixel % width : pixel / width;

        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var value = (byte)(Along(i / 4) < edge ? 0 : 255);
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (value, value, value, 255);
        }

        using var blurred = CoverBlur.FromPixels(pixels, new PixelSize(width, height));
        var result = CoverPalette.ReadPixels(blurred);

        // Promienie 2, 2 i 3 sięgają razem 7 pikseli od krawędzi: zmienia się dokładnie pas
        // od edge − 7 do edge + 6, dalej nic. Słabsze albo mocniejsze rozmycie przesuwa granice pasa.
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var position = Along(pixel);
            var value = result[pixel * 4];

            if (position < edge - 7) Assert.True(value == 0, $"pozycja {position}: {value} zamiast 0");
            else if (position > edge + 6) Assert.True(value == 255, $"pozycja {position}: {value} zamiast 255");
            else Assert.True(value is >= 1 and <= 254, $"pozycja {position}: {value} poza przejściem");
        }

        // Źródło zostaje nietknięte: z tych samych pikseli liczą się barwy plam.
        Assert.Equal(0, pixels[(verticalEdge ? edge - 1 : (edge - 1) * width) * 4]);
    });

    [Fact]
    public void JednolityObrazZostajeJednolityAzDoBrzegow() => Ui.Run(() =>
    {
        const int width = 96, height = 60;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (200, 120, 40, 255);

        using var blurred = CoverBlur.FromPixels(pixels, new PixelSize(width, height));
        var result = CoverPalette.ReadPixels(blurred);

        var first = Enumerable.Range(0, pixels.Length).FirstOrDefault(i => result[i] != pixels[i], -1);
        Assert.True(first < 0, $"piksel {first / 4} (x {first / 4 % width}, y {first / 4 / width}) ma składową {result[Math.Max(first, 0)]} zamiast {pixels[Math.Max(first, 0)]}");
    });
}
