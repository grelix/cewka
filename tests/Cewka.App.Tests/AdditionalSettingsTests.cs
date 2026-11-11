using System.Text.Json;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>
/// Ustawienia dodane po etapie 7: miękka pauza, tempo obrotu płyty i ruchu tła, opadanie widma,
/// długość listy ostatnich stacji i włączanie ostatniej stacji przy starcie.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AdditionalSettingsTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static SettingsStore Store(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    /// <summary>Katalog ze stanem radia: <paramref name="count"/> ostatnich stacji, ostatnia to „pl-b”.</summary>
    private static string RadioDirectory(int count)
    {
        var directory = TestEnvironment.NewDirectory();
        var state = new RadioState();
        for (var i = 0; i < count; i++)
            state.PushRecent(Tests.Radio.RadioTestData.Station("x-" + i, "Stacja " + i, "PL"), RecentLimits.Max);
        state.PushRecent(PlayerHarness.DefaultRadioCatalog().Find("pl-b")!, RecentLimits.Max);
        RadioStateStore.Save(state, Path.Combine(directory, "radio.json"));
        return directory;
    }

    // ---------- Wartości domyślne i zapis ----------

    [Fact]
    public void DomyslnieWszystkoDzialaJakDotad()
    {
        var settings = new AppSettings();

        Assert.False(settings.SoftPause);
        Assert.Equal(DiscSpin.Normal, settings.DiscSpin);
        Assert.Equal(BackdropMotion.Normal, settings.BackdropMotion);
        Assert.Equal(SpectrumResponse.Normal, settings.SpectrumResponse);
        Assert.Equal(10, settings.RadioRecentLimit);
        Assert.False(settings.RadioPlayAtStart);
    }

    [Theory]
    [InlineData(7, 5)]
    [InlineData(0, 5)]
    [InlineData(15, 10)]
    [InlineData(999, 20)]
    [InlineData(20, 20)]
    public void DlugoscListyOstatnichSpozaWyboruDajeNajblizsza(int saved, int expected) => Ui.Run(() =>
    {
        var store = Store($$"""{ "radioRecentLimit": {{saved}} }""");
        Assert.Equal(expected, store.Current.RadioRecentLimit);
    });

    [Fact]
    public void WartosciSpozaWyliczenDajaZwykleTempo() => Ui.Run(() =>
    {
        var store = Store("""{ "discSpin": 99, "backdropMotion": 7, "spectrumResponse": -1 }""");

        Assert.Equal(DiscSpin.Normal, store.Current.DiscSpin);
        Assert.Equal(BackdropMotion.Normal, store.Current.BackdropMotion);
        Assert.Equal(SpectrumResponse.Normal, store.Current.SpectrumResponse);
    });

    // ---------- Miękka pauza ----------

    [Fact]
    public void MiekkaPauzaZUstawienTrafiaDoSilnika() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SoftPause = true);
        Assert.Equal(MainViewModel.SoftPauseDuration, h.Engine.SoftPause);

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.SoftPause = false;

        Assert.Equal(TimeSpan.Zero, h.Engine.SoftPause);
        Assert.False(h.Settings.Current.SoftPause);

        settings.SoftPause = true;
        Assert.Equal(MainViewModel.SoftPauseDuration, h.Engine.SoftPause);
        Assert.True(h.Settings.Current.SoftPause);
    });

    [Fact]
    public void MiekkaPauzaWPrzyciskuOdtwarzaniaPauzujeOdRazuWWidoku() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SoftPause = true);
        h.OpenPaths([h.Track("a", 30)]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));

        h.Player.TogglePlay();

        Assert.False(h.Player.IsPlaying);
        Assert.Equal(Cewka.Audio.Playback.PlaybackState.Paused, h.Engine.State);
    });

    // ---------- Obrót płyty ----------

    [Fact]
    public void TempoObrotuMnozyKatAPlytaStojacaWracaDoPionu() => Ui.Run(() =>
    {
        var disc = new VinylDisc();
        var spinner = new DiscSpinner(disc);

        spinner.Update(playing: true, reduced: false, speed: 9.0 / 15.0);
        spinner.Advance(0.25);
        spinner.Advance(0.25);

        // 15 sekund na obrót: 24° na sekundę.
        Assert.InRange(disc.Angle, 11.99, 12.01);

        spinner.Update(playing: true, reduced: false, speed: 9.0 / 5.0);
        spinner.Advance(0.25);
        Assert.InRange(disc.Angle, 29.99, 30.01);

        spinner.Update(playing: true, reduced: false, speed: 0);
        for (var i = 0; i < 10; i++) spinner.Advance(0.1);
        Assert.Equal(0, disc.Angle);

        spinner.Advance(0.2);
        Assert.Equal(0, disc.Angle);
    });

    [Theory]
    [InlineData(DiscSpin.Slow, 9.0 / 15.0)]
    [InlineData(DiscSpin.Normal, 1.0)]
    [InlineData(DiscSpin.Fast, 9.0 / 5.0)]
    [InlineData(DiscSpin.Still, 0.0)]
    public void ObrotPlytyZUstawienTrafiaDoOkna(DiscSpin spin, double expected) => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness();
        var window = new MainWindow(false, h.Player) { Width = 1000, Height = 640 };
        window.Show();
        try
        {
            h.OpenPaths([h.Track("a", 30)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
            Settle();

            var settings = new SettingsViewModel(h.Settings, h.Player);
            var option = settings.DiscSpinChoice.Options.Single(o => Equals(o.Value, spin));
            settings.DiscSpinChoice.Choose(option);
            Settle();

            Assert.Equal(spin, h.Settings.Current.DiscSpin);
            Assert.Equal(expected, window.DiscSpeed, 6);
        }
        finally { window.Close(); }
    });

    // ---------- Ruch tła i widmo w scenie ----------

    [Fact]
    public void RuchTlaIOpadanieWidmaTrafiajaDoOknaISceny() => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.SceneOledCare = false;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        window.Show();
        try
        {
            h.OpenPaths([h.Track("a", 30)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
            window.EnterScene();
            Settle();

            var scene = window.Scene;
            Assert.Equal(1, window.Backdrop.MotionScale);
            Assert.Equal(1, scene.Backdrop.MotionScale);
            Assert.Equal(1, scene.AudioStage.Spectrum.FallScale);

            h.Player.BackdropMotion = BackdropMotion.Slow;
            h.Player.SpectrumResponse = SpectrumResponse.Smooth;
            Settle();

            Assert.Equal(0.5, window.Backdrop.MotionScale);
            Assert.Equal(0.5, scene.Backdrop.MotionScale);
            Assert.Equal(0.5, scene.Particles.MotionScale);
            Assert.Equal(1.8, scene.AudioStage.Spectrum.FallScale);
            Assert.Equal(1.8, scene.DiscRingView.Spectrum.FallScale);
            Assert.Equal(1.8, scene.AuroraStage.Spectrum.FallScale);

            h.Player.SpectrumResponse = SpectrumResponse.Fast;
            Settle();
            Assert.Equal(0.5, scene.AudioStage.Spectrum.FallScale);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WolniejszeOpadanieZostawiaWyzszeSlupki()
    {
        var normal = new SpectrumBars();
        var smooth = new SpectrumBars { FallScale = 1.8 };
        var loud = Enumerable.Repeat(0.5f, SpectrumBars.Count).ToArray();
        var silent = new float[SpectrumBars.Count];

        for (var i = 0; i < 20; i++)
        {
            normal.Update(loud, 0.02);
            smooth.Update(loud, 0.02);
        }

        normal.Update(silent, 0.1);
        smooth.Update(silent, 0.1);

        Assert.True(smooth.Bars[10] > normal.Bars[10], $"{smooth.Bars[10]} > {normal.Bars[10]}");
        Assert.True(smooth.Peaks[10] >= normal.Peaks[10]);
    }

    [Fact]
    public void WolnyRuchDrobinekPrzesuwaJeOPolowe() => Ui.Run(() =>
    {
        var normal = new SceneParticles { Count = 8 };
        var slow = new SceneParticles { Count = 8, MotionScale = 0.5 };
        normal.Step(0);
        slow.Step(0);
        var start = normal.Positions.ToArray();
        Assert.Equal(start, slow.Positions.ToArray());

        normal.Step(0.04);
        slow.Step(0.08);

        Assert.Equal(normal.Positions.ToArray(), slow.Positions.ToArray());
    });

    // ---------- Radio ----------

    [Fact]
    public void ListaOstatnichMaDlugoscZUstawien()
    {
        var state = new RadioState();
        for (var i = 0; i < 8; i++)
            state.PushRecent(Tests.Radio.RadioTestData.Station("s" + i, "S" + i, "PL"), limit: 5);

        Assert.Equal(5, state.Recent.Count);
        Assert.Equal("s7", state.Recent[0].Id);
        Assert.False(state.TrimRecent(5));
        Assert.True(state.TrimRecent(3));
        Assert.Equal(["s7", "s6", "s5"], state.Recent.Select(r => r.Id));
    }

    [Fact]
    public void PlikStanuPrzechowujeDwadziesciaOstatnich()
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "radio.json");
        var state = new RadioState();
        for (var i = 0; i < 25; i++)
            state.PushRecent(Tests.Radio.RadioTestData.Station("s" + i, "S" + i, "PL"), RecentLimits.Max);
        RadioStateStore.Save(state, path);

        Assert.Equal(20, RadioStateStore.Load(path).Recent.Count);
    }

    [Fact]
    public void ListaOstatnichPoWczytaniuMaDlugoscZUstawien() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Source = SourceMode.Radio;
            s.RadioRecentLimit = 5;
        }, RadioDirectory(12));

        Assert.Equal(5, h.Player.Radio.State.Recent.Count);
        Assert.Equal("pl-b", h.Player.Radio.State.Recent[0].Id);
    });

    [Fact]
    public void KrotszaListaOstatnichSkracaZapisanyStanADluzszaGoNieOdtwarza() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Source = SourceMode.Radio;
            s.RadioRecentLimit = 20;
        }, RadioDirectory(12));
        Assert.Equal(13, h.Player.Radio.State.Recent.Count);

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.RadioRecentLimitChoice.Choose(settings.RadioRecentLimitChoice.Options.Single(o => Equals(o.Value, 5)));

        Assert.Equal(5, h.Settings.Current.RadioRecentLimit);
        Assert.Equal(5, h.Player.Radio.State.Recent.Count);
        Assert.Equal(5, RadioStateStore.Load(h.RadioFile).Recent.Count);

        h.Player.Radio.RecentLimit = 20;
        Assert.Equal(5, h.Player.Radio.State.Recent.Count);

        Assert.True(h.Player.Radio.Play(PlayerHarness.DefaultRadioCatalog().Find("pl-a")!));
        Assert.Equal(6, h.Player.Radio.State.Recent.Count);
    });

    [Fact]
    public void StartWRadiuZUstawieniemWlaczaOstatniaStacje() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Source = SourceMode.Radio;
            s.RadioPlayAtStart = true;
        }, RadioDirectory(0));

        h.Player.PlayRadioAtStart();

        Assert.True(h.Player.IsPlaying);
        Assert.True(h.Engine.IsLive);
        Assert.Single(h.Sessions);
        Assert.Equal("pl-b", h.Player.Radio.CurrentStation?.Id);
    });

    [Fact]
    public void OknoPoStarcieWRadiuWlaczaOstatniaStacje() => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.Source = SourceMode.Radio;
            s.RadioPlayAtStart = true;
        }, RadioDirectory(0));
        var window = new MainWindow(false, h.Player) { Width = 1000, Height = 640 };
        window.Show();
        try
        {
            Settle();
            Assert.Single(h.Sessions);
            Assert.True(h.Player.IsPlaying);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(SourceMode.Radio, false)]
    [InlineData(SourceMode.Files, true)]
    public void BezUstawieniaAlboWPlikachStartNieWlaczaStacji(SourceMode source, bool playAtStart) => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Source = source;
            s.RadioPlayAtStart = playAtStart;
        }, RadioDirectory(0));

        h.Player.PlayRadioAtStart();

        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Sessions);
    });

    [Fact]
    public void UstawieniaRadiaZapisujaSie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);

        settings.RadioPlayAtStart = true;

        Assert.True(h.Settings.Current.RadioPlayAtStart);
        var json = JsonSerializer.Serialize(h.Settings.Current.Clone());
        Assert.Contains("RadioPlayAtStart", json);
    });
}
