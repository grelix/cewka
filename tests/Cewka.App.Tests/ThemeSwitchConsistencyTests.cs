using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Po zmianie motywu każde okno wygląda tak samo jak otwarte od razu w nowym motywie. Obraz
/// zapamiętany w chwili rysowania (kopia tła, bitmapa, pędzel zbudowany w kodzie) nie idzie za
/// motywem sam, a pojedynczy element w barwach poprzedniego motywu łatwo przeoczyć.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ThemeSwitchConsistencyTests
{
    /// <summary>Okno w zadanym stanie i okna pomocnicze, które razem z nim powstały.</summary>
    private sealed class Stage(MainWindow window, PlayerHarness harness)
    {
        public MainWindow Window { get; } = window;

        public PlayerHarness Harness { get; } = harness;

        public IEnumerable<Window> All() => [Window, .. Window.OwnedWindows];

        public void Close()
        {
            foreach (var owned in Window.OwnedWindows.ToList()) owned.Close();
            Window.Close();
        }
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Stage Open(string state)
    {
        App.Settings.Current.Window = null;
        App.Settings.Current.SettingsWindow = null;
        App.Settings.Current.SoundWindow = null;

        var h = new PlayerHarness(s =>
        {
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Reduced;
            s.QueuePinned = state is not ("schowana" or "waskie");
            s.RadioEnabled = true;
            s.RadioPrivacyAcknowledged = true;
            s.RadioLogos = false;
            if (state == "radio") s.Source = SourceMode.Radio;
        });

        var window = new MainWindow(false, h.Player) { Width = state == "waskie" ? 620 : 1300, Height = 760, AttachDesktopIntegration = false };
        window.Show();

        if (state != "radio") h.OpenPaths(Enumerable.Range(0, 6).Select(i => h.Track("t" + i)), replace: false);
        Settle();

        switch (state)
        {
            case "menu": window.SetMenuOpen(true); break;
            case "ustawienia": window.OpenSettings(); break;
            case "korektor": h.Player.PanelOpen = true; break;
            case "tor": h.Player.IsSignalPathOpen = true; break;
            case "waskie": h.Player.QueuePinned = true; break;
        }

        Settle();
        window.FreezeAnimationsForCapture();
        Settle();
        return new Stage(window, h);
    }

    /// <summary>Barwy okna w rzadkiej siatce punktów.</summary>
    private static (int Width, int Height, uint[] Pixels) Sample(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();

        const int step = 6;
        var width = frame.PixelSize.Width / step;
        var height = frame.PixelSize.Height / step;
        var pixels = new uint[width * height];
        var rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buffer.Address, y * step * buffer.RowBytes + x * step * 4);
            var a = value & 0xFF;
            var b = (value >> 8) & 0xFF;
            var c = (value >> 16) & 0xFF;
            pixels[y * width + x] = rgba ? (a << 16) | (b << 8) | c : (c << 16) | (b << 8) | a;
        }

        return (width, height, pixels);
    }

    private static int Difference(uint a, uint b)
    {
        var red = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        var green = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
        var blue = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
        return Math.Max(red, Math.Max(green, blue));
    }

    /// <summary>Opis miejsc, w których dwa obrazy różnią się wyraźnie; pusty, gdy są zgodne.</summary>
    private static string Compare((int Width, int Height, uint[] Pixels) switched, (int Width, int Height, uint[] Pixels) fresh)
    {
        if (switched.Width != fresh.Width || switched.Height != fresh.Height)
            return $"rozmiar {switched.Width}x{switched.Height} zamiast {fresh.Width}x{fresh.Height}";

        var different = 0;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        var worst = 0;
        var example = string.Empty;

        for (var y = 0; y < fresh.Height; y++)
        for (var x = 0; x < fresh.Width; x++)
        {
            var d = Difference(switched.Pixels[y * fresh.Width + x], fresh.Pixels[y * fresh.Width + x]);
            if (d <= 24) continue;

            different++;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);

            if (d > worst)
            {
                worst = d;
                example = $"#{switched.Pixels[y * fresh.Width + x]:X6} zamiast #{fresh.Pixels[y * fresh.Width + x]:X6} w ({x * 6},{y * 6})";
            }
        }

        // Pojedyncze punkty na krawędziach liter i ikon mogą wypaść inaczej; plama nie.
        if (different <= fresh.Pixels.Length / 400) return string.Empty;

        return $"{different} z {fresh.Pixels.Length} punktów, obszar ({left * 6},{top * 6})–({right * 6},{bottom * 6}), np. {example}";
    }

    [Theory]
    [InlineData("przypieta")]
    [InlineData("schowana")]
    [InlineData("waskie")]
    [InlineData("radio")]
    [InlineData("menu")]
    [InlineData("ustawienia")]
    [InlineData("korektor")]
    [InlineData("tor")]
    public void PoZmianieMotywuOknoWygladaJakOtwarteWNowymMotywie(string state) => Ui.Run(() =>
    {
        var modeBefore = App.Theme.Mode;
        var settingBefore = App.Settings.Current.Theme;
        var other = App.Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark;

        try
        {
            var switched = Open(state);
            List<(string Name, (int, int, uint[]) Frame)> after;
            try
            {
                // Jak przycisk motywu: tryb i zapisane ustawienie zmieniają się razem.
                App.Theme.SetMode(other);
                App.Settings.Current.Theme = other;
                Settle();
                switched.Window.Backdrop.Advance(0.25);
                for (var i = 0; i < 8; i++)
                {
                    switched.Window.Backdrop.Advance(0.25);
                    Settle();
                }

                switched.Window.FreezeAnimationsForCapture();
                Settle();
                after = switched.All().Select(w => (w.GetType().Name, Sample(w))).ToList();
            }
            finally { switched.Close(); }

            var fresh = Open(state);
            List<(string Name, (int, int, uint[]) Frame)> reference;
            try
            {
                reference = fresh.All().Select(w => (w.GetType().Name, Sample(w))).ToList();
            }
            finally { fresh.Close(); }

            Assert.Equal(reference.Select(r => r.Name), after.Select(a => a.Name));

            var problems = after.Zip(reference, (a, r) => (a.Name, Problem: Compare(a.Frame, r.Frame)))
                .Where(p => p.Problem.Length > 0)
                .Select(p => $"{p.Name}: {p.Problem}")
                .ToList();

            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
        finally
        {
            App.Theme.SetMode(modeBefore);
            App.Settings.Current.Theme = settingBefore;
        }
    });
}
