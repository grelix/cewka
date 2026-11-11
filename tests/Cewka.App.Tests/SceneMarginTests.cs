using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Tests.SceneInput;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Margines ekranu sceny: skala kadru, zapis dla ekranu oraz tryb ramki „Dopasuj krawędzie”.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneMarginTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public SceneMargin Margin => Player.GetSceneMargin(Scene.ScreenKey);

        public void Dispose() => Window.Close();
    }

    private static Rig Open()
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = EffectsMode.Full;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Show();
        h.OpenPaths(Enumerable.Range(0, 2).Select(i => h.Track("m" + i)));
        // Zgłoszenie utworu przychodzi przez pulę wątków; scena wzięłaby spóźnione za zmianę utworu.
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero
            && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));
        window.EnterScene();
        Render();
        return rig;
    }

    private static void Render()
    {
        for (var i = 0; i < 2; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    // ---------- Funkcje czyste ----------

    [Fact]
    public void MarginesToDwaIPolProcentaAPiecProcentZKazdejStrony()
    {
        Assert.Equal(0, ScreenMargin.Fraction(SceneMargin.None));
        Assert.Equal(0.025, ScreenMargin.Fraction(SceneMargin.Small));
        Assert.Equal(0.05, ScreenMargin.Fraction(SceneMargin.Large));
        Assert.Equal(1.0, ScreenMargin.ScaleFactor(SceneMargin.None), 9);
        Assert.Equal(0.95, ScreenMargin.ScaleFactor(SceneMargin.Small), 9);
        Assert.Equal(0.90, ScreenMargin.ScaleFactor(SceneMargin.Large), 9);
    }

    [Fact]
    public void DomyslnyMarginesToDwaIPolProcenta()
    {
        Assert.Equal(SceneMargin.Small, ScreenMargin.StandardDefault);
        Assert.Empty(new AppSettings().SceneMargins);
    }

    [Fact]
    public void KrokZatrzymujeSiePrzyBrakuIPieciuProcentach()
    {
        Assert.Equal(SceneMargin.Small, ScreenMargin.Step(SceneMargin.None, 1));
        Assert.Equal(SceneMargin.Large, ScreenMargin.Step(SceneMargin.Small, 1));
        Assert.Equal(SceneMargin.Large, ScreenMargin.Step(SceneMargin.Large, 1));
        Assert.Equal(SceneMargin.Small, ScreenMargin.Step(SceneMargin.Large, -1));
        Assert.Equal(SceneMargin.None, ScreenMargin.Step(SceneMargin.None, -1));
    }

    [Fact]
    public void MarginesEkranuBezWpisuJestDomyslny()
    {
        var margins = new Dictionary<string, SceneMargin> { ["a|1920x1080"] = SceneMargin.Large };

        Assert.Equal(SceneMargin.Large, ScreenMargin.For(margins, "a|1920x1080"));
        Assert.Equal(ScreenMargin.Default, ScreenMargin.For(margins, "b|1920x1080"));
        Assert.Equal(ScreenMargin.Default, ScreenMargin.For(margins, null));
        Assert.Equal(ScreenMargin.Default, ScreenMargin.For(null, "a|1920x1080"));
    }

    [Fact]
    public void KluczEkranuZawieraNazweIRozdzielczoscBezPolozenia()
    {
        var key = SceneScreens.KeyOf("HDMI-1", new PixelRect(0, 0, 3840, 2160));

        Assert.Equal("HDMI-1|3840x2160", key);
        Assert.Equal(key, SceneScreens.KeyOf(" HDMI-1 ", new PixelRect(1920, 100, 3840, 2160)));
        Assert.NotEqual(key, SceneScreens.KeyOf("HDMI-1", new PixelRect(0, 0, 1920, 1080)));
        Assert.NotEqual(key, SceneScreens.KeyOf("HDMI-2", new PixelRect(0, 0, 3840, 2160)));
        Assert.Equal("|1920x1080", SceneScreens.KeyOf(null, new PixelRect(0, 0, 1920, 1080)));
    }

    // ---------- Ustawienia ----------

    [Fact]
    public void KopiaMarginesowNieDzieliSlownika()
    {
        var source = new AppSettings();
        source.SceneMargins["a|1x1"] = SceneMargin.Large;

        var clone = source.Clone();
        clone.SceneMargins["a|1x1"] = SceneMargin.None;
        clone.SceneMargins["b|2x2"] = SceneMargin.Small;

        Assert.Equal(SceneMargin.Large, source.SceneMargins["a|1x1"]);
        Assert.Single(source.SceneMargins);
    }

    [Fact]
    public void MarginesyWracajaZPlikuUstawien()
    {
        var source = new AppSettings();
        source.SceneMargins["TV|3840x2160"] = SceneMargin.Large;
        source.SceneMargins["|1920x1080"] = SceneMargin.None;

        var json = JsonSerializer.Serialize(source, SettingsJsonContext.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(source.SceneMargins, loaded.SceneMargins);
        Assert.Contains("\"Large\"", json);
    }

    [Fact]
    public void ZapisMarginesuDotyczyTylkoJednegoEkranu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var changed = new List<string?>();
        h.Player.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Player.SetSceneMargin("tv|3840x2160", SceneMargin.Large);

        Assert.Equal(SceneMargin.Large, h.Player.GetSceneMargin("tv|3840x2160"));
        Assert.Equal(ScreenMargin.Default, h.Player.GetSceneMargin("monitor|1920x1080"));
        Assert.Contains(nameof(MainViewModel.SceneMargins), changed);

        changed.Clear();
        h.Player.SetSceneMargin("tv|3840x2160", SceneMargin.Large);
        Assert.Empty(changed);
    });

    // ---------- Skala kadru ----------

    [Fact]
    public void MarginesZmniejszaSkaleKadruIOdsuwaWyjscieOdKrawedzi() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;
        var full = scene.Scale;
        Assert.Equal(0, scene.ExitStage.Margin.Top);
        Assert.Equal(0, scene.ExitStage.Margin.Right);

        rig.Player.SetSceneMargin(scene.ScreenKey, SceneMargin.Small);
        Render();
        Assert.Equal(full * 0.95, scene.Scale, 6);
        Assert.Equal(360 * 0.025, scene.ExitStage.Margin.Top, 6);
        Assert.Equal(640 * 0.025, scene.ExitStage.Margin.Right, 6);

        rig.Player.SetSceneMargin(scene.ScreenKey, SceneMargin.Large);
        Render();
        Assert.Equal(full * 0.90, scene.Scale, 6);

        // Ta sama skala obejmuje przycisk wyjścia.
        var exit = Assert.IsType<Avalonia.Media.ScaleTransform>(scene.ExitStage.LayoutTransform);
        Assert.Equal(scene.Scale, exit.ScaleX, 9);

        rig.Player.SetSceneMargin(scene.ScreenKey, SceneMargin.None);
        Render();
        Assert.Equal(full, scene.Scale, 6);
    });

    [Fact]
    public void MarginesInnegoEkranuNieZmieniaSkali() => Ui.Run(() =>
    {
        using var rig = Open();
        var scale = rig.Scene.Scale;

        rig.Player.SetSceneMargin("inny|1x1", SceneMargin.Large);
        Render();

        Assert.Equal(scale, rig.Scene.Scale, 9);
    });

    // ---------- Dopasuj krawędzie ----------

    [Fact]
    public void RamkaPokazujeGranicePolozeniemWedlugMarginesu() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;
        Assert.False(scene.EdgeFrame.IsVisible);

        rig.Window.FitScreenEdges();
        Render();

        Assert.True(scene.IsEdgeFitting);
        Assert.True(scene.IsOpen);
        Assert.True(scene.EdgeFrame.IsVisible);
        Assert.Equal(new Thickness(0), scene.EdgeFrame.Margin);

        rig.Player.SetSceneMargin(scene.ScreenKey, SceneMargin.Small);
        Render();
        Assert.Equal(new Thickness(640 * 0.025, 360 * 0.025), scene.EdgeFrame.Margin);
        Assert.False(string.IsNullOrWhiteSpace(scene.EdgeFitValue.Text));
        Assert.False(string.IsNullOrWhiteSpace(scene.EdgeFitHint.Text));
    });

    [Fact]
    public void StrzalkiZmieniajaMarginesIZapisujaGoOdRazu() => Ui.Run(() =>
    {
        using var rig = Open();
        var scene = rig.Scene;
        rig.Window.FitScreenEdges();
        Render();
        Assert.Equal(SceneMargin.None, rig.Margin);

        Press(rig.Window, Key.Right);
        Assert.Equal(SceneMargin.Small, rig.Margin);
        Assert.Equal(SceneMargin.Small, rig.Harness.Settings.Current.SceneMargins.GetValueOrDefault(scene.ScreenKey ?? string.Empty, SceneMargin.None));
        Assert.True(scene.IsEdgeFitting);

        Press(rig.Window, Key.Right);
        Assert.Equal(SceneMargin.Large, rig.Margin);
        Press(rig.Window, Key.Right);
        Assert.Equal(SceneMargin.Large, rig.Margin);

        Press(rig.Window, Key.Left);
        Assert.Equal(SceneMargin.Small, rig.Margin);
        Assert.True(scene.IsOpen);
    });

    [Fact]
    public void StrzalkiWRamceNieCofajaUtworu() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.FitScreenEdges();
        Render();
        var position = rig.Player.Progress;

        Press(rig.Window, Key.Right);
        Press(rig.Window, Key.Left);

        Assert.Equal(position, rig.Player.Progress, 3);
    });

    [Fact]
    public void EnterKonczyRamkeAScenaZostajeOtwarta() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.FitScreenEdges();
        Render();

        Press(rig.Window, Key.Enter);

        Assert.False(rig.Scene.IsEdgeFitting);
        Assert.False(rig.Scene.EdgeFrame.IsVisible);
        Assert.True(rig.Scene.IsOpen);
        Assert.True(rig.Player.IsPlaying);
    });

    [Fact]
    public void EscDziałaWarstwowoNajpierwRamkaPotemScena() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.FitScreenEdges();
        Render();

        Press(rig.Window, Key.Escape);
        Assert.False(rig.Scene.IsEdgeFitting);
        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(WindowState.FullScreen, rig.Window.WindowState);

        Press(rig.Window, Key.Escape);
        Assert.False(rig.Scene.IsOpen);
    });

    [Fact]
    public void ZamknieciaScenyKonczyRamke() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.FitScreenEdges();
        Render();

        rig.Window.ExitScene();

        Assert.False(rig.Scene.IsEdgeFitting);
        Assert.False(rig.Scene.EdgeFrame.IsVisible);
    });

    [Fact]
    public void PrzyciskWUstawieniachZamykaOknoIOtwieraSceneZRamka() => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        window.Show();
        try
        {
            window.OpenSettings();
            Assert.NotNull(window.OpenSettingsWindow);
            Assert.False(window.Scene.IsOpen);

            var settings = Assert.IsType<SettingsViewModel>(window.OpenSettingsWindow!.DataContext);
            settings.FitEdges();
            Render();

            Assert.Null(window.OpenSettingsWindow);
            Assert.True(window.Scene.IsOpen);
            Assert.True(window.Scene.IsEdgeFitting);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void MarginesZUstawienZapisujeSieDlaEkranuSceny() => Ui.Run(() =>
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player, new SceneSettingsHost { ScreenKey = () => "tv|3840x2160" });

        var large = settings.SceneMarginChoice.Options.Single(option => Equals(option.Value, SceneMargin.Large));
        settings.SceneMarginChoice.Choose(large);

        Assert.Equal(SceneMargin.Large, h.Player.GetSceneMargin("tv|3840x2160"));
        Assert.Equal(ScreenMargin.Default, h.Player.GetSceneMargin("inny|1x1"));
    });
}
