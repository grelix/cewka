using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Wizualizacja Noc sceny: czerń, przygaszony blok, przenosiny co 2 minuty, zero pracy tła.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneNightTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(1000);

        public SceneView Scene => Window.Scene;

        public void Wait(double seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Scene.AdvanceTimers();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    private static Rig Open(SceneVisual visual, EffectsMode effects = EffectsMode.Full)
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
        h.OpenPaths(Enumerable.Range(0, 2).Select(i => h.Track("n" + i)));
        // Zgłoszenie utworu przychodzi przez pulę wątków; scena wzięłaby spóźnione za zmianę utworu.
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero
            && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));
        window.EnterScene();
        for (var i = 0; i < 2; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return rig;
    }

    private static double Target(Visual visual) =>
        visual.GetBaseValue(Visual.OpacityProperty) is { HasValue: true } target ? target.Value : visual.Opacity;

    [Fact]
    public void NocJestCzarnaBezTlaIPlytyZBlokiemTekstu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);
        var scene = rig.Scene;

        Assert.True(scene.NightVeil.IsVisible);
        Assert.True(scene.NightBlock.IsVisible);
        Assert.False(scene.CoverLayer.IsVisible);
        Assert.False(scene.Backdrop.IsVisible);
        Assert.False(scene.DiscHost.IsVisible);
        Assert.False(scene.DimAcross.IsVisible);
        Assert.Equal(0, scene.TitleBlock.Opacity);
        Assert.InRange(Target(scene.NightBlock), 0.4, 0.55);

        // Warstwa sterowania zostaje.
        Assert.True(scene.TransportRow.IsVisible);
        Assert.True(scene.Seek.IsVisible);
    });

    [Fact]
    public void WNocyPasekPostepuWidacTylkoRazemZWarstwaSterowania() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);
        var scene = rig.Scene;
        rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);

        Assert.False(scene.Chrome.IsShown);
        Assert.Equal(0, scene.ProgressBlock.Opacity);

        scene.Chrome.RevealByPointer();
        Dispatcher.UIThread.RunJobs();
        Assert.True(scene.Chrome.IsShown);
        Assert.Equal(1, scene.ProgressBlock.Opacity);

        rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
        Assert.Equal(0, scene.ProgressBlock.Opacity);

        // Płyta pokazuje pasek także w spoczynku.
        rig.Harness.Player.SceneVisual = SceneVisual.Disc;
        Dispatcher.UIThread.RunJobs();
        Assert.False(scene.Chrome.IsShown);
        Assert.Equal(1, scene.ProgressBlock.Opacity);
    });

    [Fact]
    public void NocNieKręciPłytyANiePrzerysowujeTła() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);

        Assert.False(rig.Scene.Spinner.IsDrawing);
        Assert.False(rig.Scene.Backdrop.IsEffectivelyVisible);
    });

    [Fact]
    public void WizualizacjaPłytaPokazujeTłoIPłytęBezBlokuNocy() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;

        Assert.False(scene.NightVeil.IsVisible);
        Assert.False(scene.NightBlock.IsVisible);
        Assert.True(scene.DiscHost.IsVisible);
        Assert.Equal(1, scene.TitleBlock.Opacity);
    });

    [Fact]
    public void ZmianaNaNocWOtwartejScenieUkrywaPłytęIZatrzymujeJą() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        rig.Harness.Player.SceneVisual = SceneVisual.Night;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(rig.Scene.NightBlock.IsVisible);
        Assert.False(rig.Scene.DiscHost.IsVisible);
        Assert.False(rig.Scene.Spinner.IsDrawing);
    });

    [Fact]
    public void BlokNocyPrzenosiSiePoDwóchMinutachZPrzenikaniem() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);
        var scene = rig.Scene;
        var start = scene.NightBlock.Margin;

        rig.Wait(119);
        Assert.Equal(start, scene.NightBlock.Margin);

        rig.Wait(2);
        Assert.Equal(0, Target(scene.NightBlock));

        scene.FinishNightMove();
        Assert.NotEqual(start, scene.NightBlock.Margin);
        Assert.InRange(Target(scene.NightBlock), 0.4, 0.55);
    });

    [Fact]
    public void WTrybieOgraniczonymBlokNocyPrzenosiSieBezPrzenikania() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night, EffectsMode.Reduced);
        var scene = rig.Scene;
        var start = scene.NightBlock.Margin;

        rig.Wait(121);

        Assert.NotEqual(start, scene.NightBlock.Margin);
        Assert.InRange(Target(scene.NightBlock), 0.4, 0.55);
    });

    [Fact]
    public void NocNieLiczyPrzenosinPoWyjsciuZeSceny() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);
        rig.Window.ExitScene();
        var start = rig.Scene.NightBlock.Margin;

        rig.Wait(300);

        Assert.Equal(start, rig.Scene.NightBlock.Margin);
    });
}
