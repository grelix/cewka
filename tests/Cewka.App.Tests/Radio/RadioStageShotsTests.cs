using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Zrzuty tylko na żądanie: katalog wskazuje zmienna <c>CEWKA_ZRZUTY_RADIO</c>.</summary>
public sealed class RadioShotsFactAttribute : FactAttribute
{
    public RadioShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RadioStageShotsTests.OutputVariable)))
            Skip = $"Brak katalogu zrzutów ({RadioStageShotsTests.OutputVariable}).";
    }
}

/// <summary>Zrzuty okna głównego w trybie radia na liście wbudowanej, bez okna na ekranie i bez dźwięku.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioStageShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_RADIO";

    private static void Save(MainWindow window, string name)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(Environment.GetEnvironmentVariable(OutputVariable)!, name + ".png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Obrazy testowe wygenerowane w kodzie, w barwie zależnej od adresu; to nie są znaki żadnych stacji.</summary>
    private static RadioLogoService FakeLogos()
    {
        return new RadioLogoService(
            TestEnvironment.NewDirectory(),
            (url, _) =>
            {
                var seed = url.AbsoluteUri.Aggregate(17u, (a, c) => (a * 31) + c);
                var colour = 0xFF000000u | ((seed & 0x7F7F7Fu) + 0x303030u);
                var width = 60 + (int)(seed % 5) * 20;
                return Task.FromResult(new Cewka.Audio.Streaming.ImageFetchResult(
                    TestLogos.Png(width, 72, colour), Cewka.Audio.Streaming.ImageFetchProblem.None, 200));
            });
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Scenario(string name, Action<PlayerHarness, MainWindow> act, bool logos = false)
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(
            s =>
            {
                s.QueuePinned = true;
                s.WindowControls = WindowControlsPosition.Right;
                s.Effects = EffectsMode.Full;
                s.RadioPrivacyAcknowledged = logos;
            },
            radioCatalog: RadioCatalog.LoadBuiltIn,
            radioLogos: logos ? FakeLogos() : null);

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();

        try
        {
            h.Player.SetSource(SourceMode.Radio);
            window.FreezeAnimationsForCapture();
            act(h, window);
            Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }

    private static RadioStation Pick(PlayerHarness h, int index) => h.Player.Radio.Rows[index].Station;

    [RadioShotsFact]
    public void ListaStacji() => Ui.Run(() => Scenario("1-lista-stacji", (h, _) => { }));

    [RadioShotsFact]
    public void WynikiWyszukiwania() => Ui.Run(() => Scenario("2-wyniki-wyszukiwania", (h, _) =>
    {
        h.Player.Radio.SearchText = "rock";
    }));

    [RadioShotsFact]
    public void Ulubione() => Ui.Run(() => Scenario("3-ulubione", (h, _) =>
    {
        foreach (var index in new[] { 0, 2, 5 }) h.Player.Radio.ToggleFavourite(Pick(h, index));
        h.Player.Radio.SetView(RadioListContext.ViewFavourites);
    }));

    [RadioShotsFact]
    public void GraStacja() => Ui.Run(() => Scenario("4-gra-stacja", (h, _) =>
    {
        var radio = h.Player.Radio;
        radio.Play(Pick(h, 1));
        var source = h.Sessions[0].Source;
        radio.OnLiveState(source, LiveState.Playing);
        radio.OnLiveTitle(source, "Kasia Kowalska - Antidotum");
        h.Player.SyncPlayingState();
    }));

    [RadioShotsFact]
    public void LogoWListeIWPolu() => Ui.Run(() => Scenario("6-logo-lista-i-pole", (h, _) =>
    {
        var radio = h.Player.Radio;
        radio.Play(radio.Rows.First(r => r.Station.Logos.Count > 0).Station);
        var source = h.Sessions[0].Source;
        radio.OnLiveState(source, LiveState.Playing);
        radio.OnLiveTitle(source, "Kasia Kowalska - Antidotum");
        h.Player.SyncPlayingState();
        WaitFor(() => radio.HasStageLogo && radio.Rows.Take(6).All(r => r.HasLogo || r.Station.Logos.Count == 0));
    }, logos: true));

    [RadioShotsFact]
    public void BanerPrywatnosci() => Ui.Run(() => Scenario("7-baner-prywatnosci", (h, _) =>
    {
        Assert.True(h.Player.Radio.ShowsPrivacyBanner);
    }));

    [RadioShotsFact]
    public void MenuRozsuniete() => Ui.Run(() => Scenario("8-menu-rozsuniete", (h, window) =>
    {
        h.Player.SetEffects(EffectsMode.Reduced);
        window.SetMenuOpen(true);
    }));

    [RadioShotsFact]
    public void MenuZwiniete() => Ui.Run(() => Scenario("9-menu-zwiniete-znak-radia", (h, window) =>
    {
        window.SetMenuOpen(false);
    }));

    [RadioShotsFact]
    public void UstawieniaRadia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.Effects = EffectsMode.Full, radioLogos: FakeLogos());
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            settings.SelectSection(settings.RadioSection);
            for (var i = 0; i < 6; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(Environment.GetEnvironmentVariable(OutputVariable)!, "10-ustawienia-radio.png"), new PngBitmapEncoderOptions());
        }
        finally
        {
            window.Close();
        }
    });

    [RadioShotsFact]
    public void Blad() => Ui.Run(() => Scenario("5-blad", (h, _) =>
    {
        var radio = h.Player.Radio;
        radio.Play(Pick(h, 3));
        radio.OnLiveFailed(h.Sessions[0].Source, new LiveFailure(LiveFailureReason.Stalled));
        h.Player.SyncPlayingState();
    }));
}
