using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Tests.SceneInput;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using SkiaSharp;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests.Radio;

/// <summary>Zrzuty tylko na żądanie: katalog wskazuje zmienna <c>CEWKA_ZRZUTY_RE</c>.</summary>
public sealed class RadioSceneShotsFactAttribute : FactAttribute
{
    public RadioSceneShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SceneRadioShotsTests.OutputVariable)))
            Skip = $"Brak katalogu zrzutów ({SceneRadioShotsTests.OutputVariable}).";
    }
}

/// <summary>Zrzuty Sceny i okna w trybie radia oraz okładki czarno-białej; logo to obrazy testowe wygenerowane w kodzie.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneRadioShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_RE";

    private const string LogoUrl = "https://logo.example.org/a.png";

    private static void Save(MainWindow window, string name)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        frame.Save(Path.Combine(Environment.GetEnvironmentVariable(OutputVariable)!, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static RadioCatalog Catalog()
    {
        RadioStation Make(string id, string name, string country, string? logo)
        {
            var station = RadioTestData.Station(id, name, country);
            if (logo is not null) station.Logos = [logo];
            return station;
        }

        return new RadioCatalog(
        [
            Make("a", "Radio Przykład", "PL", LogoUrl),
            Make("b", "Fala Testowa", "PL", "https://logo.example.org/b.png"),
            Make("c", "Studio Próbne", "PL", null),
            Make("d", "Eter Wzorcowy", "PL", "https://logo.example.org/d.png"),
        ], null);
    }

    private static RadioLogoService Logos() => new(
        TestEnvironment.NewDirectory(),
        (url, _) =>
        {
            var colour = url.AbsoluteUri.Contains("/b.") ? 0xFF1E8C5Au : url.AbsoluteUri.Contains("/d.") ? 0xFFC87814u : 0xFF2850B4u;
            return Task.FromResult(new ImageFetchResult(TestLogos.Png(240, 240, colour), ImageFetchProblem.None, 200));
        });

    private static async Task Radio(string name, bool title, bool row, bool sceneOpen, int width = 1920, int height = 1080)
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(
            s =>
            {
                s.QueuePinned = false;
                s.WindowControls = WindowControlsPosition.Right;
                s.Effects = EffectsMode.Full;
                s.SceneVisual = SceneVisual.Disc;
                s.RadioPrivacyAcknowledged = true;
            },
            radioCatalog: Catalog,
            radioLogos: Logos());
        var window = new MainWindow(false, h.Player) { Width = width, Height = height };
        window.Show();

        try
        {
            h.Player.SetSource(SourceMode.Radio);
            var radio = h.Player.Radio;
            radio.Play(radio.Rows.First(r => r.Station.Id == "a").Station);
            var source = h.Sessions[^1].Source;
            radio.OnLiveState(source, LiveState.Playing);
            if (title) radio.OnLiveTitle(source, "Wykonawca Przykładowy - Utwór próbny");
            h.Player.SyncPlayingState();
            Assert.True(await Ui.UntilAsync(() => radio.HasStageLogo, 5000));
            Dispatcher.UIThread.RunJobs();

            window.FreezeAnimationsForCapture();
            if (sceneOpen)
            {
                window.OpenSceneForCapture(showControls: true);
                if (row)
                {
                    window.KeyPress(Key.Q, RawInputModifiers.None, PhysicalKey.None, null);
                    window.KeyRelease(Key.Q, RawInputModifiers.None, PhysicalKey.None, null);
                    Dispatcher.UIThread.RunJobs();
                    h.PumpUntil(() => false, 800);
                }
            }

            Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }

    [RadioSceneShotsFact]
    public void ScenaZLogoStacji() => Ui.RunAsync(() => Radio("1-scena-z-logo", title: true, row: false, sceneOpen: true));

    [RadioSceneShotsFact]
    public void ScenaBezTytulu() => Ui.RunAsync(() => Radio("2-scena-bez-tytulu", title: false, row: false, sceneOpen: true));

    [RadioSceneShotsFact]
    public void ScenaZRzedemQ() => Ui.RunAsync(() => Radio("3-scena-z-rzedem-q", title: true, row: true, sceneOpen: true));

    [RadioSceneShotsFact]
    public void OknoZPaletaZLogo() => Ui.RunAsync(() => Radio("4-okno-z-paleta-logo", title: true, row: false, sceneOpen: false, width: 1300, height: 760));

    [RadioSceneShotsFact]
    public void OknoZCzarnoBialaOkladka() => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Full;
        });
        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();

        try
        {
            h.Player.OpenPaths([h.TaggedTrack("bw", "Utwór próbny", "Wykonawca przykładowy", "Album przykładowy", seconds: 120)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero));
            Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted));

            h.Player.ShowCover(MainViewModel.DecodeCover(GreyJpeg()));
            window.FreezeAnimationsForCapture();
            Save(window, "5-okno-czarno-biala-okladka");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Szary gradient z szumem barwnym zakodowany do JPEG z niską jakością, jak okładki z artefaktami.</summary>
    private static byte[] GreyJpeg()
    {
        var random = new Random(20261108);
        using var source = new SKBitmap(600, 600);
        for (var y = 0; y < 600; y++)
        for (var x = 0; x < 600; x++)
        {
            var grey = (x + y) * 255 / 1198;
            var noise = random.Next(-28, 29);
            source.SetPixel(x, y, new SKColor((byte)Math.Clamp(grey + noise, 0, 255), (byte)Math.Clamp(grey, 0, 255), (byte)Math.Clamp(grey - noise, 0, 255)));
        }

        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 20);
        return data.ToArray();
    }
}
