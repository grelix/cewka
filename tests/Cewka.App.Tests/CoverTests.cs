using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Okładka: dekodowanie w zmniejszonej rozdzielczości, paleta z próbki, rozmyte tło sceny,
/// zwalnianie obrazów, brak pracy przy przewijaniu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CoverTests
{
    [Fact]
    public void DuzaOkladkaDekodowanaWRozdzielczosciPlyty() => Ui.Run(() =>
    {
        var cover = MainViewModel.DecodeCover(Png(1600, 1600, Colors.SteelBlue));

        Assert.NotNull(cover);
        try
        {
            Assert.True(cover.Display.PixelSize.Width <= 640, $"szerokość {cover.Display.PixelSize.Width}");
            Assert.NotNull(cover.Pixels);
            Assert.True(cover.Pixels.Length <= 48 * 48 * 4);
        }
        finally
        {
            cover.Discard();
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

        Assert.All(CoverPalette.FromPixels(grey, darkTheme: true), c => Assert.True(c.R == c.G && c.G == c.B, $"{c}"));
    });

    [Fact]
    public void KilkaBarwnychPikseliNaSzarejOkladceNieBarwiPalety() => Ui.Run(() =>
    {
        var pixels = Grey(48 * 48);
        for (var i = 0; i < 12; i++)
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2]) = ((byte)0, (byte)0, (byte)255);

        Assert.Equal(CoverPalette.Extract(null, darkTheme: true), CoverPalette.FromPixels(pixels, darkTheme: true));
    });

    [Fact]
    public void SzumBarwnyNaSzarejOkladceNieBarwiPalety() => Ui.Run(() =>
    {
        var random = new Random(20261031);
        var pixels = Grey(48 * 48);
        for (var i = 0; i < pixels.Length; i += 4)
        for (var channel = 0; channel < 3; channel++)
            pixels[i + channel] = (byte)(128 + random.Next(-12, 13));

        Assert.All(CoverPalette.FromPixels(pixels, darkTheme: false), c => Assert.True(Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 3, $"{c}"));
    });

    private static byte[] Grey(int count)
    {
        var pixels = new byte[count * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    [Fact]
    public void PrzewiniecieNieRysujeOkladkiOdNowa() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a")]);
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

    [Fact]
    public void PokazanaOkladkaDajeRozmyteTloSceny() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var raised = new List<string?>();
        h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var cover = MainViewModel.DecodeCover(Png(400, 400, Colors.SteelBlue));
        h.Player.ShowCover(cover);

        Assert.NotNull(cover?.Backdrop);
        Assert.Same(cover.Backdrop, h.Player.SceneBackdrop);
        Assert.Contains(nameof(MainViewModel.SceneBackdrop), raised);
    });

    [Fact]
    public void NowaOkladkaZwalniaPoprzednieTloSceny() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        // Otwarta scena ma tło także przy okładce domyślnej, w każdym motywie okna.
        h.Player.IsSceneOpen = true;
        h.Player.ShowCover(MainViewModel.DecodeCover(Png(400, 400, Colors.SteelBlue)));
        var first = h.Player.SceneBackdrop;
        Assert.NotNull(first);

        h.Player.ShowCover(MainViewModel.DecodeCover(Png(400, 400, Colors.OrangeRed)));
        var second = h.Player.SceneBackdrop;

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(IsDisposed(first));
        Assert.False(IsDisposed(second));

        // Powrót do okładki domyślnej też zwalnia rozmytą okładkę utworu.
        h.Player.ShowCover(null);

        Assert.True(IsDisposed(second));
        Assert.NotNull(h.Player.SceneBackdrop);
    });

    [Fact]
    public void ZamkniecieZwalniaTloSceny() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        h.Player.ShowCover(MainViewModel.DecodeCover(Png(400, 400, Colors.SteelBlue)));
        var backdrop = h.Player.SceneBackdrop;
        Assert.NotNull(backdrop);

        h.Dispose();

        Assert.True(IsDisposed(backdrop));
    });

    [Fact]
    public void OkladkaDomyslnaDajeCiemneRozmyteTloTakzeWJasnymMotywie() => Ui.Run(() =>
    {
        var before = App.Theme.Mode;
        try
        {
            App.Theme.SetMode(ThemeMode.Light);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var h = new PlayerHarness();

            // Ciemne tło powstaje z ciemnego wariantu spirali, a ten dopiero przy otwartej scenie.
            Assert.Null(h.Player.SceneBackdrop);
            h.Player.IsSceneOpen = true;

            var backdrop = h.Player.SceneBackdrop;
            Assert.NotNull(backdrop);
            Assert.Equal(CoverBlur.Width, backdrop.PixelSize.Width);

            // Okładka domyślna w oknie jest jasna, scena jest zawsze ciemna.
            Assert.True(Brightness(h.Player.Cover!) > 150, $"okładka w oknie: {Brightness(h.Player.Cover!):F0}");
            Assert.True(Brightness(backdrop) < 90, $"tło sceny: {Brightness(backdrop):F0}");
        }
        finally
        {
            App.Theme.SetMode(before);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>
    /// Płyta sceny dostaje tę samą bitmapę co płyta okna, a przy okładce domyślnej w jasnym motywie
    /// jej ciemny wariant. Ten wariant powstaje tylko przy otwartej scenie i zwalnia się przy jej
    /// zamknięciu, przy zmianie okładki i przy zamknięciu programu, jak okładka okna.
    /// </summary>
    [Fact]
    public void NaklejkaScenyToOkladkaOknaAlboCiemnyWariantOkladkiDomyslnej() => Ui.Run(() =>
    {
        var before = App.Theme.Mode;
        try
        {
            App.Theme.SetMode(ThemeMode.Dark);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var h = new PlayerHarness();
            var player = h.Player;

            // Ciemne okno: płyta sceny to okładka okna, przy zamkniętej i otwartej scenie.
            Assert.NotNull(player.Cover);
            Assert.Same(player.Cover, player.SceneCover);
            player.IsSceneOpen = true;
            Assert.Same(player.Cover, player.SceneCover);
            player.IsSceneOpen = false;
            Assert.Same(player.Cover, player.SceneCover);
            Assert.False(IsDisposed(player.Cover));
            var darkWindow = player.Cover;

            // Jasne okno przy zamkniętej scenie: okładka okna jasna, ciemnego wariantu nie ma.
            App.Theme.SetMode(ThemeMode.Light);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var light = player.Cover!;
            Assert.True(IsDisposed(darkWindow));
            Assert.Null(player.SceneCover);
            Assert.Null(player.SceneBackdrop);

            // Otwarcie sceny rysuje ciemny wariant w tym samym rozmiarze; okładka okna zostaje.
            player.IsSceneOpen = true;
            var dark = player.SceneCover!;
            Assert.Same(light, player.Cover);
            Assert.NotSame(light, dark);
            Assert.Equal(light.PixelSize, dark.PixelSize);
            Assert.True(Brightness(light) > 150, $"okładka w oknie: {Brightness(light):F0}");
            Assert.True(Brightness(dark) < 90, $"płyta sceny: {Brightness(dark):F0}");
            var backdrop = player.SceneBackdrop!;
            Assert.True(Brightness(backdrop) < 90, $"tło sceny: {Brightness(backdrop):F0}");

            // Zamknięcie sceny zwalnia ciemny wariant i tło z niego, okładka okna zostaje.
            player.IsSceneOpen = false;
            Assert.True(IsDisposed(dark));
            Assert.True(IsDisposed(backdrop));
            Assert.Null(player.SceneCover);
            Assert.Same(light, player.Cover);
            Assert.False(IsDisposed(light));

            // Prawdziwa okładka: ta sama bitmapa na obu płytach, oba warianty domyślnej zwolnione.
            player.IsSceneOpen = true;
            dark = player.SceneCover!;
            player.ShowCover(MainViewModel.DecodeCover(Png(400, 400, Colors.SteelBlue)));
            var real = player.Cover!;
            Assert.Same(real, player.SceneCover);
            Assert.True(IsDisposed(light));
            Assert.True(IsDisposed(dark));

            // Prawdziwa okładka przy zamykaniu sceny zostaje.
            player.IsSceneOpen = false;
            Assert.Same(real, player.SceneCover);
            Assert.False(IsDisposed(real));
            player.IsSceneOpen = true;

            player.ShowCover(null);
            Assert.True(IsDisposed(real));
            var again = player.SceneCover!;
            Assert.NotSame(player.Cover, again);

            h.Dispose();
            Assert.True(IsDisposed(again));
            Assert.True(IsDisposed(player.Cover!));
        }
        finally
        {
            App.Theme.SetMode(before);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>Średnia składowych barwnych wszystkich pikseli, 0–255.</summary>
    private static double Brightness(Bitmap bitmap)
    {
        var pixels = CoverPalette.ReadPixels(bitmap);
        long sum = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];

        return sum / (pixels.Length / 4 * 3.0);
    }

    /// <summary>Zwolniona bitmapa nie oddaje już rozmiaru.</summary>
    internal static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    internal static byte[] Png(int width, int height, Color colour)
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
