using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Tests.SceneInput;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests.Radio;

/// <summary>Scena przy grającej stacji: nazwa i tytuł z metadanych, napis „Na żywo”, bez osi czasu, płyty, Losowo i Powtarzaj.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneRadioTests
{
    private const string LogoRed = "https://logo.example.org/czerwone.png";

    internal sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public RadioViewModel Radio => Harness.Player.Radio;

        public void Dispose() => Window.Close();

        public RadioStation Station(string id) => Radio.Rows.First(r => r.Station.Id == id).Station;

        public void Live(LiveState state, string? title = null)
        {
            var source = Harness.Sessions[^1].Source;
            Radio.OnLiveState(source, state);
            if (title is not null) Radio.OnLiveTitle(source, title);
            Harness.Player.SyncPlayingState();
            Render();
        }
    }

    private static void Render()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static RadioCatalog Catalog()
    {
        RadioStation Make(string id, string name, string? logo)
        {
            var station = RadioTestData.Station(id, name, "PL");
            if (logo is not null) station.Logos = [logo];
            return station;
        }

        return new RadioCatalog(
        [
            Make("a", "Alfa FM", LogoRed),
            Make("b", "Beta FM", null),
            Make("c", "Gamma FM", null),
        ], null);
    }

    /// <summary>Scena otwarta przy stacji „Alfa FM”, która już gra.</summary>
    private static Rig Open(SceneVisual visual = SceneVisual.Disc, bool play = true, bool logos = true, bool gpu = false)
    {
        App.Settings.Current.Window = null;
        var logoService = new RadioLogoService(
            TestEnvironment.NewDirectory(),
            (_, _) => Task.FromResult(new ImageFetchResult(TestLogos.Png(64, 64, 0xFFCC2222), ImageFetchProblem.None, 200)));
        var h = new PlayerHarness(
            s =>
            {
                s.QueuePinned = false;
                s.Effects = EffectsMode.Full;
                s.SceneVisual = visual;
                s.RadioPrivacyAcknowledged = true;
            },
            radioCatalog: Catalog,
            radioLogos: logos ? logoService : null);
        if (gpu)
        {
            // Zorza wymaga GPU i zasilania sieciowego; w testach bez okna jednego i drugiego nie ma.
            h.Player.BatteryProbe = () => false;
            h.Player.RefreshBattery();
            h.Player.PinGpu(true);
        }

        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Show();

        h.Player.SetSource(SourceMode.Radio);
        if (play)
        {
            rig.Radio.Play(rig.Station("a"));
            rig.Live(LiveState.Playing);
        }

        window.EnterScene();
        Render();
        return rig;
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void BezTytuluSceneMaNazweStacjiINapisNaZywo() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.Equal(rig.Radio.StationName, rig.Scene.SceneTitle.Text);
        Assert.NotEqual(Strings.Current["NoTrack"], rig.Scene.SceneTitle.Text);
        Assert.False(rig.Scene.SceneArtist.IsVisible);
        Assert.True(rig.Scene.LiveCaption.IsVisible);
        Assert.Equal(Strings.Current["RadioLive"], rig.Scene.LiveCaption.Text);
    });

    [Fact]
    public void TytulZMetadanychZastepujeUtwor() => Ui.Run(() =>
    {
        using var rig = Open();

        rig.Live(LiveState.Playing, "Kasia Kowalska - Antidotum");

        Assert.Equal("Antidotum", rig.Scene.SceneTitle.Text);
        Assert.True(rig.Scene.SceneArtist.IsVisible);
        Assert.Contains("Kasia Kowalska", rig.Scene.SceneArtist.Text);
        Assert.Contains(rig.Radio.StationName, rig.Scene.SceneArtist.Text);
        Assert.False(rig.Scene.SceneAlbum.IsVisible);
    });

    [Fact]
    public void StanLaczeniaJestWidocznyZamiastNapisuNaZywo() => Ui.Run(() =>
    {
        using var rig = Open();

        rig.Live(LiveState.Buffering);

        Assert.Equal(rig.Radio.StateText, rig.Scene.LiveCaption.Text);
        Assert.NotEqual(Strings.Current["RadioLive"], rig.Scene.LiveCaption.Text);
    });

    [Fact]
    public void BezOsiCzasuLosowoIPowtarzaj() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.Equal(0, rig.Scene.Seek.Opacity);
        Assert.False(rig.Scene.Seek.IsEnabled);
        Assert.False(rig.Scene.TimesRow.IsVisible);
        Assert.Equal(0, rig.Scene.ShuffleButton.Opacity);
        Assert.False(rig.Scene.ShuffleButton.IsEnabled);
        Assert.Equal(0, rig.Scene.RepeatButton.Opacity);
        Assert.False(rig.Scene.RepeatButton.IsEnabled);
        Assert.True(rig.Scene.ShuffleButton.IsVisible);
    });

    [Fact]
    public void KlawiszeSIRNicNieRobiaWRadiu() => Ui.Run(() =>
    {
        using var rig = Open();
        var shuffle = rig.Player.ShuffleEnabled;
        var repeat = rig.Player.RepeatEnabled;

        Press(rig.Window, Key.S);
        Press(rig.Window, Key.R);

        Assert.Equal(shuffle, rig.Player.ShuffleEnabled);
        Assert.Equal(repeat, rig.Player.RepeatEnabled);
    });

    [Fact]
    public void PrzyciskStopMaKwadratIPodpisZatrzymaj() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.Equal(Strings.Current["SceneStop"], rig.Scene.PlayLabel.Text);
        Assert.True(rig.Scene.StopIcon.IsEffectivelyVisible);
        Assert.False(rig.Scene.PauseIcon.IsEffectivelyVisible);
    });

    [Fact]
    public void WsteczIDalejZmieniajaStacje() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Radio.StepDelay = TimeSpan.Zero;

        rig.Scene.NextButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Render();
        Assert.Equal("Beta FM", rig.Radio.CurrentStation!.Name);

        rig.Scene.PreviousButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Render();
        Assert.Equal("Alfa FM", rig.Radio.CurrentStation!.Name);
    });

    [Fact]
    public void BezPlytyLogoStoiWRamce() => Ui.RunAsync(async () =>
    {
        using var rig = Open();

        Assert.True(await Ui.UntilAsync(() => rig.Radio.HasStageLogo));
        Render();

        Assert.False(rig.Scene.DiscHost.IsVisible);
        Assert.False(rig.Scene.CoverArtHost.IsVisible);
        Assert.True(rig.Scene.RadioStageHost.IsEffectivelyVisible);
        Assert.True(rig.Scene.RadioStageLogo.IsEffectivelyVisible);
        Assert.False(rig.Scene.RadioStagePlaceholder.IsVisible);
    });

    /// <summary>
    /// Mała okładka przy tytule w Widmie i Zorzy idzie za logo stacji: pojawia się po pobraniu logo, po przejściu
    /// do stacji bez logo znika, po powrocie wraca. Wcześniej zostawało logo poprzedniej stacji, a logo pobrane
    /// po wejściu w scenę nie pojawiało się wcale. Horyzontu w radiu nie ma (stacja nie ma końca).
    /// </summary>
    [Theory]
    [InlineData(SceneVisual.Spectrum)]
    [InlineData(SceneVisual.Aurora)]
    public void MalaOkladkaIdzieZaLogoPoZmianieStacji(SceneVisual visual) => Ui.RunAsync(async () =>
    {
        using var rig = Open(visual, gpu: true);
        rig.Radio.StepDelay = TimeSpan.Zero;
        Assert.Equal(visual, rig.Player.ActiveSceneVisual);

        Assert.True(await Ui.UntilAsync(() => rig.Radio.HasStageLogo));
        Render();
        Assert.True(rig.Scene.MiniCover.IsVisible);
        Assert.Same(rig.Radio.StageLogo, rig.Scene.MiniCoverImage.Source);

        rig.Scene.NextButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Render();
        Assert.Equal("Beta FM", rig.Radio.CurrentStation!.Name);
        Assert.False(rig.Scene.MiniCover.IsVisible, "zostało logo poprzedniej stacji");
        Assert.Null(rig.Scene.MiniCoverImage.Source);

        rig.Scene.PreviousButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(await Ui.UntilAsync(() => rig.Radio.HasStageLogo));
        Render();
        Assert.True(rig.Scene.MiniCover.IsVisible);
        Assert.Same(rig.Radio.StageLogo, rig.Scene.MiniCoverImage.Source);
    });

    [Fact]
    public void StacjaBezLogoMaWRamceIkoneRadia() => Ui.Run(() =>
    {
        using var rig = Open(logos: false);

        Assert.True(rig.Scene.RadioStageHost.IsEffectivelyVisible);
        Assert.False(rig.Scene.RadioStageLogo.IsVisible);
        Assert.True(rig.Scene.RadioStagePlaceholder.IsVisible);
    });

    [Fact]
    public void KartaNastepnyIKoniecKolejkiSaUkryte() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.False(rig.Scene.NextCard.IsVisible);
        Assert.False(rig.Scene.QueueEndBlock.IsVisible);
    });

    [Fact]
    public void RzadPodQPokazujeStacjeZListy() => Ui.Run(() =>
    {
        using var rig = Open();

        Press(rig.Window, Key.Q);
        Render();

        Assert.True(rig.Scene.IsQueueRowOpen);
        Assert.Equal(["Alfa FM", "Beta FM", "Gamma FM"], rig.Scene.QueueRow.Items.Select(i => i.Title.Split(' ')[0] + " " + i.Title.Split(' ')[1]));
        Assert.Equal(0, rig.Scene.QueueRow.CurrentIndex);

        // Kraj stoi już przy nazwie stacji.
        Assert.All(rig.Scene.QueueRow.Items, item => Assert.Empty(item.Subtitle));
    });

    [Fact]
    public void OtwartyRzadNieBudujeSieOdNowaBezZmianyStacji() => Ui.RunAsync(async () =>
    {
        using var rig = Open();
        rig.Radio.StepDelay = TimeSpan.Zero;
        Press(rig.Window, Key.Q);
        Render();

        // Odświeżanie idzie co 60 ms, więc to kilka okazji do zbędnej przebudowy.
        await Task.Delay(100);
        Render();
        var revision = rig.Player.QueueRowRevision;

        await Task.Delay(300);
        Render();
        Assert.Equal(revision, rig.Player.QueueRowRevision);

        rig.Player.PlayQueueRowItem(rig.Scene.QueueRow.Items[2].Id);

        Assert.True(await Ui.UntilAsync(() => rig.Player.QueueRowRevision != revision));
        Render();
        Assert.Equal(2, rig.Scene.QueueRow.CurrentIndex);
    });

    [Fact]
    public void EnterWRzedzieWlaczaStacje() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Radio.StepDelay = TimeSpan.Zero;
        Press(rig.Window, Key.Q);
        rig.Scene.QueueRow.MoveFocusTo(2);

        rig.Player.PlayQueueRowItem(rig.Scene.QueueRow.Items[2].Id);
        Render();

        Assert.Equal("Gamma FM", rig.Radio.CurrentStation!.Name);
    });

    [Fact]
    public void TloSceneMaBarwyLogoStacji() => Ui.RunAsync(async () =>
    {
        using var rig = Open();

        Assert.True(await Ui.UntilAsync(() => rig.Radio.HasStageLogo));
        Render();

        Assert.Contains(rig.Player.SceneBlobs, c => c.ToHsl().H is < 25 or > 335);
    });

    private static bool Models_IsAudio(SceneVisual visual) => ViewModels.MainViewModel.IsAudioVisual(visual);

    [Theory]
    [InlineData(SceneVisual.Disc)]
    [InlineData(SceneVisual.Cover)]
    [InlineData(SceneVisual.Night)]
    [InlineData(SceneVisual.Spectrum)]
    [InlineData(SceneVisual.Waveform)]
    public void WizualizacjeDzialajaWRadiu(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual);

        Assert.Equal(visual, rig.Player.SceneVisual);
        var audio = Models_IsAudio(visual);
        Assert.Equal(audio, rig.Scene.AudioView.IsVisible);
        Assert.Equal(audio, rig.Scene.AudioView.IsRunning);
        Assert.Equal(visual is SceneVisual.Disc or SceneVisual.Cover, rig.Scene.RadioStageHost.IsVisible);
        Assert.Equal(visual == SceneVisual.Night, rig.Scene.NightBlock.IsVisible);
        Assert.Equal(rig.Radio.StationName, rig.Scene.NightTitle.Text);
    });

    [Fact]
    public void HoryzontJestNiedostepnyWRadiuAWybranyDajePlyteIBezObwiedni() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Horizon);

        // Zapamiętany wybór zostaje, a rysowana jest Płyta.
        Assert.Equal(SceneVisual.Horizon, rig.Player.SceneVisual);
        Assert.Equal(SceneVisual.Disc, rig.Player.ActiveSceneVisual);
        Assert.Null(rig.Player.SceneEnvelope);
        Assert.False(rig.Scene.AudioView.IsVisible);
        Assert.Equal(Controls.SceneAudioMode.None, rig.Scene.AudioView.Mode);
        Assert.True(rig.Scene.RadioStageHost.IsVisible);

        // V pomija Horyzont w obie strony.
        rig.Player.SceneVisual = SceneVisual.Waveform;
        rig.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, rig.Player.SceneVisual);
        rig.Player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Waveform, rig.Player.SceneVisual);
    });

    [Fact]
    public void PowrotDoPlikowPrzywracaScenePlikow() => Ui.Run(() =>
    {
        using var rig = Open();

        rig.Player.SetSource(SourceMode.Files);
        Render();

        Assert.True(rig.Scene.DiscHost.IsVisible);
        Assert.False(rig.Scene.RadioStageHost.IsVisible);
        Assert.True(rig.Scene.Seek.IsEnabled);
        Assert.Equal(1, rig.Scene.Seek.Opacity);
        Assert.True(rig.Scene.TimesRow.IsVisible);
        Assert.False(rig.Scene.LiveCaption.IsVisible);
        Assert.True(rig.Scene.ShuffleButton.IsEnabled);
        Assert.Equal(1, rig.Scene.RepeatButton.Opacity);
        Assert.Equal(Strings.Current["ScenePlay"], rig.Scene.PlayLabel.Text);
    });
}
