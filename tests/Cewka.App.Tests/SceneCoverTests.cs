using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Wizualizacja Okładka sceny: ostra okładka, wolny ruch, przenikanie, duży dekod tylko na czas wizualizacji.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneCoverTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public List<(string Path, int Size)> LargeReads { get; } = [];

        public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(1000);

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public void Wait(double seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Scene.AdvanceTimers();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    private static Bitmap Big() => MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Red))!.Display;

    private static void Render()
    {
        for (var i = 0; i < 2; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Open(SceneVisual visual, EffectsMode effects = EffectsMode.Full, bool withCover = true, bool enter = true)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = effects;
            s.SceneVisual = visual;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Scene.Clock = () => rig.Now;
        window.Show();
        h.OpenPaths(Enumerable.Range(0, 2).Select(i => h.Track("o" + i)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));
        Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();

        h.Player.ReadLargeCover = (path, size) =>
        {
            lock (rig.LargeReads) rig.LargeReads.Add((path, size));
            return Big();
        };

        if (withCover) h.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.SteelBlue)));
        if (enter)
        {
            window.EnterScene();
            Render();
        }

        return rig;
    }

    private static double Target(Visual visual) =>
        visual.GetBaseValue(Visual.OpacityProperty) is { HasValue: true } target ? target.Value : visual.Opacity;

    [Fact]
    public void OkladkaPokazujeOstraOkladkeZTlemBezPlyty() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        var scene = rig.Scene;

        Assert.True(scene.CoverArtHost.IsVisible);
        Assert.NotNull(scene.CoverArtShown.Source);
        Assert.False(scene.DiscHost.IsVisible);
        Assert.False(scene.NightVeil.IsVisible);
        Assert.True(scene.Backdrop.IsVisible);
        Assert.True(scene.CoverLayer.IsVisible);
        Assert.Equal(1, scene.TitleBlock.Opacity);
    });

    [Fact]
    public void WarstwaPrzenikaniaMozeSiePrzerysowacPoZmianieOkladki() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, enter: false);
        rig.Player.ReadLargeCover = (_, _) => null;
        rig.Window.EnterScene();
        Render();
        var previous = rig.Scene.CoverArtShown;
        Assert.NotNull(previous.Source);

        rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.Orange)));

        // Stara warstwa jeszcze przenika, więc jej obraz musi nadal nadawać się do rysowania.
        Assert.NotSame(previous, rig.Scene.CoverArtShown);
        Assert.NotNull(previous.Source);
        previous.InvalidateVisual();
        Render();
    });

    [Fact]
    public void StarszePrzenikanieNieCzysciWarstwyNowszego() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, enter: false);
        rig.Player.ReadLargeCover = (_, _) => null;
        var releases = new List<Action>();
        rig.Scene.RunArtLater = (action, _) => releases.Add(action);
        rig.Window.EnterScene();
        Render();
        var first = rig.Scene.CoverArtShown;

        Color[] colours = [Colors.Orange, Colors.Green, Colors.Purple];
        foreach (var colour in colours)
        {
            rig.Now += TimeSpan.FromMilliseconds(250);
            rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, colour)));
        }

        // Pierwsza warstwa znów jest tylną, ale pokazuje już okładkę z drugiej zmiany, która jeszcze znika.
        Assert.Equal(3, releases.Count);
        Assert.NotSame(first, rig.Scene.CoverArtShown);
        Assert.NotNull(first.Source);

        releases[0]();
        releases[1]();
        Assert.NotNull(first.Source);

        releases[2]();
        Assert.Null(first.Source);
    });

    [Fact]
    public void OkladkaMaOkoloSzescdziesieciuProcentWysokosciKadru() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);

        Assert.InRange(rig.Scene.CoverArtFrame.Height / 1080.0, 0.60, 0.70);
        Assert.True(rig.Scene.CoverArtFrame.BoxShadow.Count > 0);
    });

    [Fact]
    public void PlytaNieWykonujePracyPodczasOkladki() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);

        Assert.False(rig.Scene.Spinner.IsDrawing);
        Assert.False(rig.Scene.DiscHost.IsEffectivelyVisible);
    });

    [Fact]
    public void WizualizacjaPlytaNieMaOkladki() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);

        Assert.False(rig.Scene.CoverArtHost.IsVisible);
        Assert.True(rig.Scene.DiscHost.IsVisible);
    });

    [Fact]
    public void ZmianaNaOkladkeWOtwartejScenieZastepujePlyte() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        rig.Player.SceneVisual = SceneVisual.Cover;
        Render();

        Assert.True(rig.Scene.CoverArtHost.IsVisible);
        Assert.False(rig.Scene.DiscHost.IsVisible);
        Assert.False(rig.Scene.Spinner.IsDrawing);
    });

    [Fact]
    public void BrakOkladkiPokazujeSpiraleCewki() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, withCover: false);

        Assert.NotNull(rig.Player.SceneCover);
        Assert.Same(rig.Player.SceneCover, rig.Scene.CoverArtShown.Source);
        Assert.Empty(rig.LargeReads);
    });

    // ----- Ruch -----

    [Fact]
    public void OkladkaPoruszaSieWolnoPodczasOdtwarzania() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        var scene = rig.Scene;

        rig.Wait(0.1);
        rig.Wait(15);

        Assert.InRange(scene.DriftScale, 1.0, 1.04);
        Assert.True(scene.DriftScale > 1.0);
        Assert.True(Math.Abs(scene.DriftX) + Math.Abs(scene.DriftY) > 0);
        Assert.InRange(Math.Abs(scene.DriftX), 0, 0.02 * scene.CoverArtFrame.Width + 1e-6);
        Assert.InRange(Math.Abs(scene.DriftY), 0, 0.02 * scene.CoverArtFrame.Height + 1e-6);
    });

    [Fact]
    public void KierunekRuchuZalezyOdSciezkiBiezacegoPliku() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);

        Assert.False(string.IsNullOrEmpty(rig.Player.CurrentFilePath));
        Assert.Equal(CoverDrift.For(rig.Player.CurrentFilePath), rig.Scene.DriftDirection);
    });

    [Fact]
    public void WPauzieOkladkaStoi() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        rig.Wait(0.1);
        rig.Wait(10);
        rig.Player.Pause();
        Assert.True(rig.Harness.PumpUntil(() => !rig.Player.IsPlaying));
        Render();
        rig.Wait(0.1);
        var (scale, x, y) = (rig.Scene.DriftScale, rig.Scene.DriftX, rig.Scene.DriftY);

        rig.Wait(20);

        Assert.Equal(scale, rig.Scene.DriftScale);
        Assert.Equal(x, rig.Scene.DriftX);
        Assert.Equal(y, rig.Scene.DriftY);
    });

    [Fact]
    public void WTrybieOgraniczonymOkladkaStoiWMiejscu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, EffectsMode.Reduced);

        rig.Wait(0.1);
        rig.Wait(20);

        Assert.Equal(1.0, rig.Scene.DriftScale);
        Assert.Equal(0.0, rig.Scene.DriftX);
        Assert.Equal(0.0, rig.Scene.DriftY);
    });

    [Fact]
    public void PoWyjsciuZeScenyRuchNieLiczyCzasu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        rig.Wait(0.1);
        rig.Wait(10);
        rig.Window.ExitScene();
        var scale = rig.Scene.DriftScale;

        rig.Wait(30);

        Assert.Equal(scale, rig.Scene.DriftScale);
    });

    [Fact]
    public void RuchOkladkiJestCzystaFunkcjaCzasuANieMuzyki() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        rig.Wait(0.1);
        rig.Wait(10);

        var expected = CoverDrift.At(CoverDrift.For(rig.Player.CurrentFilePath), 10.1);

        Assert.Equal(expected.Scale, rig.Scene.DriftScale, 6);
        Assert.Equal(expected.X * rig.Scene.CoverArtFrame.Width, rig.Scene.DriftX, 6);
        Assert.Equal(expected.Y * rig.Scene.CoverArtFrame.Height, rig.Scene.DriftY, 6);
    });

    // ----- Przenikanie -----

    [Fact]
    public void ZmianaOkladkiPrzenikaPrzez600Milisekund() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        var before = rig.Scene.CoverArtShown;
        rig.Wait(10);

        rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.OrangeRed)));
        Render();

        Assert.NotSame(before, rig.Scene.CoverArtShown);
        Assert.Equal(TimeSpan.FromMilliseconds(600), rig.Scene.CoverFadeDuration);
        Assert.Equal(1, Target(rig.Scene.CoverArtShown));
        Assert.Equal(0, Target(before));
    });

    [Fact]
    public void SzybkiePrzeskakiwanieSkracaPrzenikanieDoTrzechSetMilisekund() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        rig.Wait(10);
        rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.OrangeRed)));
        Render();

        rig.Wait(0.5);
        rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.SeaGreen)));
        Render();

        Assert.Equal(TimeSpan.FromMilliseconds(300), rig.Scene.CoverFadeDuration);
    });

    [Fact]
    public void WTrybieOgraniczonymOkladkaZmieniaSieBezPrzenikania() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, EffectsMode.Reduced);
        var before = rig.Scene.CoverArtShown;
        rig.Wait(10);

        rig.Player.ShowCover(MainViewModel.DecodeCover(CoverTests.Png(400, 400, Colors.OrangeRed)));
        Render();

        Assert.NotSame(before, rig.Scene.CoverArtShown);
        Assert.Equal(TimeSpan.Zero, rig.Scene.CoverFadeDuration);
        Assert.Equal(0, Target(before));
    });

    // ----- Duży dekod -----

    [Fact]
    public void DuzaOkladkaDekodujeSieTylkoDlaWizualizacjiOkladka() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        Assert.Empty(rig.LargeReads);
        Assert.False(rig.Player.HasLargeCover);

        rig.Player.SceneVisual = SceneVisual.Cover;
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(rig.LargeReads);
        Assert.Equal(rig.Player.CurrentFilePath, rig.LargeReads[0].Path);
        Assert.True(rig.Player.HasLargeCover);
    });

    [Fact]
    public void RozmiarDuzegoDekoduToWysokoscEkranuNieWiecejNiz1440() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, enter: false);

        rig.Player.SceneCoverSize = 2160;
        Assert.Equal(1440, rig.Player.SceneCoverSize);

        rig.Player.SceneCoverSize = 900;
        Assert.Equal(900, rig.Player.SceneCoverSize);

        rig.Window.EnterScene();
        rig.Player.SceneVisual = SceneVisual.Cover;
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));

        Assert.Equal(rig.Player.SceneCoverSize, rig.LargeReads.Single().Size);
        Assert.InRange(rig.LargeReads.Single().Size, 1, 1440);
    });

    [Fact]
    public void DuzaOkladkaZwalniaSiePoWyjsciuZeSceny() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Player.HasLargeCover);

        rig.Window.ExitScene();

        Assert.False(rig.Player.HasLargeCover);
        Assert.Same(rig.Player.SceneCover, rig.Player.SceneCoverArt);
    });

    [Fact]
    public void DuzaOkladkaZwalniaSiePoZmianieWizualizacji() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();

        rig.Player.SceneVisual = SceneVisual.Night;

        Assert.False(rig.Player.HasLargeCover);
    });

    [Fact]
    public void PonowneWejscieWOkladkeDekodujeOkladkeOdNowa() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        rig.Window.ExitScene();

        rig.Window.EnterScene();
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, rig.LargeReads.Count);
        Assert.True(rig.Player.HasLargeCover);
    });

    [Fact]
    public void DuzaOkladkaPodmieniaMalaPoZakonczeniuDekodu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover);
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();
        Render();

        Assert.NotSame(rig.Player.SceneCover, rig.Player.SceneCoverArt);
        Assert.Same(rig.Player.SceneCoverArt, rig.Scene.CoverArtShown.Source);
    });

    [Fact]
    public void BrakOkladkiNieZamawiaDuzegoDekodu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Cover, withCover: false);
        Assert.True(Ui.WaitUntil(() => rig.Player.LargeCoverTask.IsCompleted));

        Assert.Empty(rig.LargeReads);
        Assert.False(rig.Player.HasLargeCover);
    });
}
