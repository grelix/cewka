using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Tests.SceneInput;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Ochrona ekranu OLED w scenie: dryf, przygaszanie stałych napisów, zmiana strony układu.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneOledTests
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

        /// <summary>Przechodzi do następnego utworu i czeka, aż model go przyjmie.</summary>
        public void NextTrack()
        {
            var index = Harness.Player.CurrentIndex;
            Harness.Player.Next();
            Assert.True(Harness.PumpUntil(() => Harness.Player.CurrentIndex != index));
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Czeka, aż warstwa sterowania schowa się po otwarciu sceny.</summary>
        public void HideControls()
        {
            Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(Scene.Chrome.IsShown);
        }

        public void Pause()
        {
            Harness.Player.Pause();
            Assert.True(Harness.PumpUntil(() => !Harness.Player.IsPlaying));
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    private static Rig Open(SceneVisual visual, EffectsMode effects = EffectsMode.Full, bool oled = true)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = effects;
            s.SceneVisual = visual;
            s.SceneOledCare = oled;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Scene.Clock = () => rig.Now;
        window.Show();
        h.OpenPaths(Enumerable.Range(0, 4).Select(i => h.Track("o" + i)));
        // Zgłoszenie utworu idzie przez pulę wątków i może przyjść po wejściu w scenę, a scena
        // wzięłaby je za zmianę utworu: czekać, aż model pokaże pierwszy utwór.
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

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    // ---------- Funkcje czyste ----------

    [Fact]
    public void DryfNieWychodziPozaAmplitudeIJestCiagly()
    {
        double maxX = 0, maxY = 0;
        var previous = OledCare.DriftAt(0);

        for (var t = 0.5; t <= 2400; t += 0.5)
        {
            var (x, y) = OledCare.DriftAt(t);
            Assert.InRange(x, -OledCare.Amplitude, OledCare.Amplitude);
            Assert.InRange(y, -OledCare.Amplitude, OledCare.Amplitude);
            maxX = Math.Max(maxX, Math.Abs(x));
            maxY = Math.Max(maxY, Math.Abs(y));

            // Krok timera to 2 s; przy tych okresach kadr nie przesuwa się o więcej niż 0,7 jednostki na krok.
            Assert.InRange(Math.Abs(x - previous.X), 0, 0.7 / 4);
            Assert.InRange(Math.Abs(y - previous.Y), 0, 0.7 / 4);
            previous = (x, y);
        }

        Assert.InRange(maxX, OledCare.Amplitude - 0.01, OledCare.Amplitude);
        Assert.InRange(maxY, OledCare.Amplitude - 0.01, OledCare.Amplitude);
    }

    [Fact]
    public void DryfPowtarzaSiePoOkresachPieciuICzterechMinut()
    {
        var start = OledCare.DriftAt(37);
        var afterX = OledCare.DriftAt(37 + 300);
        var afterY = OledCare.DriftAt(37 + 240);

        Assert.Equal(start.X, afterX.X, 6);
        Assert.Equal(start.Y, afterY.Y, 6);
        Assert.Equal((0.0, 0.0), OledCare.DriftAt(0));
    }

    [Fact]
    public void SkokiTrybuOgraniczonegoMajaAmplitudeDryfu()
    {
        var spots = new HashSet<(double, double)>();

        for (var track = -5; track < 9; track++)
        {
            var (x, y) = OledCare.JumpAt(track);
            Assert.Equal(OledCare.Amplitude, Math.Sqrt(x * x + y * y), 6);
            spots.Add((x, y));
        }

        Assert.True(spots.Count >= 3);
        Assert.NotEqual(OledCare.JumpAt(0), OledCare.JumpAt(1));
    }

    // ---------- Dryf ----------

    [Fact]
    public void DryfPrzesuwaKadrWOdtwarzaniuIStoiWPauzie() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;
        Assert.True(scene.OledTimerRunning);

        rig.Wait(30);
        var moved = scene.OledOffset;
        Assert.NotEqual((0.0, 0.0), moved);
        Assert.Equal(OledCare.DriftAt(30).X, moved.X, 6);

        var shift = Assert.IsType<TranslateTransform>(scene.Stage.RenderTransform);
        Assert.Equal(moved.X * scene.Scale, shift.X, 6);
        Assert.Equal(moved.Y * scene.Scale, shift.Y, 6);

        rig.Pause();
        Assert.False(scene.OledTimerRunning);
        rig.Wait(90);
        Assert.Equal(moved, scene.OledOffset);

        rig.Harness.Player.TogglePlay();
        Assert.True(rig.Harness.PumpUntil(() => rig.Harness.Player.IsPlaying));
        Assert.True(scene.OledTimerRunning);
        rig.Wait(30);
        Assert.Equal(OledCare.DriftAt(60).X, scene.OledOffset.X, 6);
    });

    [Fact]
    public void BezOchronyKadrStoi() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, oled: false);

        rig.Wait(120);

        Assert.Equal((0.0, 0.0), rig.Scene.OledOffset);
        Assert.False(rig.Scene.OledTimerRunning);
    });

    [Fact]
    public void WNocyDryfDzialaTakzePrzyWylaczonymUstawieniu() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night, oled: false);

        rig.Wait(60);

        Assert.Equal(OledCare.DriftAt(60).X, rig.Scene.OledOffset.X, 6);
        Assert.NotEqual(0, rig.Scene.OledOffset.X);
    });

    [Fact]
    public void ZamknieciaScenyZeruujePrzesuniecie() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        rig.Wait(30);
        Assert.NotEqual((0.0, 0.0), rig.Scene.OledOffset);

        rig.Window.ExitScene();

        Assert.Equal((0.0, 0.0), rig.Scene.OledOffset);
        Assert.False(rig.Scene.OledTimerRunning);
    });

    [Fact]
    public void WTrybieOgraniczonymNieMaDryfuATylkoSkokPrzyUtworze() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, EffectsMode.Reduced);
        var scene = rig.Scene;

        Assert.False(scene.OledTimerRunning);
        Assert.Equal(OledCare.JumpAt(0), scene.OledOffset);

        rig.Wait(120);
        Assert.Equal(OledCare.JumpAt(0), scene.OledOffset);

        rig.NextTrack();
        Assert.Equal(OledCare.JumpAt(1), scene.OledOffset);
        Assert.False(scene.OledTimerRunning);
    });

    // ---------- Przygaszanie ----------

    [Fact]
    public void NapisyPrzygasajaPoTrzechMinutachBezWejscia() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;

        rig.Wait(179);
        Assert.False(scene.IsDimmed);
        Assert.Equal(1, Target(scene.TitleBlock));

        rig.Wait(2);
        Assert.True(scene.IsDimmed);
        Assert.Equal(OledCare.DimFactor, Target(scene.TitleBlock), 6);
    });

    [Fact]
    public void WejscieIZmianaUtworuPrzywracajaNapisy() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;

        rig.Wait(181);
        Assert.True(scene.IsDimmed);

        Press(rig.Window, Key.M);
        Assert.False(scene.IsDimmed);
        Assert.Equal(1, Target(scene.TitleBlock));

        // Odliczanie zaczyna się od nowa od wejścia.
        rig.Wait(179);
        Assert.False(scene.IsDimmed);
        rig.Wait(2);
        Assert.True(scene.IsDimmed);

        rig.NextTrack();
        Assert.False(scene.IsDimmed);
        Assert.Equal(1, Target(scene.TitleBlock));
    });

    [Fact]
    public void PrzygaszanieDzialaTakzeWPauzie() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        rig.Pause();

        rig.Wait(181);

        Assert.True(rig.Scene.IsDimmed);
        Assert.False(rig.Scene.OledTimerRunning);
    });

    [Fact]
    public void BezOchronyNapisyNiePrzygasaja() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, oled: false);

        rig.Wait(900);

        Assert.False(rig.Scene.IsDimmed);
        Assert.Equal(1, Target(rig.Scene.TitleBlock));
    });

    [Fact]
    public void WylaczenieUstawieniaPrzywracaPrzygaszoneNapisy() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        rig.Wait(181);
        Assert.True(rig.Scene.IsDimmed);

        rig.Harness.Player.SceneOledCare = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(rig.Scene.IsDimmed);
        Assert.Equal(1, Target(rig.Scene.TitleBlock));
        Assert.Equal((0.0, 0.0), rig.Scene.OledOffset);
    });

    [Fact]
    public void BlokNocyPrzygasaDoSzescdziesieciuPieciuProcentJasnosci() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);
        var scene = rig.Scene;

        rig.Wait(181);
        scene.FinishNightMove();

        Assert.True(scene.IsDimmed);
        Assert.Equal(0.5 * OledCare.DimFactor, Target(scene.NightBlock), 6);
    });

    // ---------- Zmiana strony ----------

    [Theory]
    [InlineData(SceneVisual.Disc)]
    [InlineData(SceneVisual.Cover)]
    public void UkladyStrefowezamieniajaStronyCoUtwor(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual);
        var scene = rig.Scene;
        var zone = ZoneLeft(scene, visual);
        var column = scene.InfoColumn.Margin.Left;
        Assert.False(scene.IsMirrored);
        rig.HideControls();

        rig.NextTrack();
        Assert.True(scene.IsMirrored);
        Assert.Equal(1920 - zone - ZoneWidth(visual), ZoneLeft(scene, visual), 6);
        Assert.Equal(1920 - column - 880, scene.InfoColumn.Margin.Left, 6);

        rig.NextTrack();
        Assert.False(scene.IsMirrored);
        Assert.Equal(zone, ZoneLeft(scene, visual), 6);
        Assert.Equal(column, scene.InfoColumn.Margin.Left, 6);
    });

    /// <summary>Kliknięcie „Następny” nie może przenieść przycisku spod kursora na drugą stronę kadru.</summary>
    [Fact]
    public void PrzyWidocznychPrzyciskachStronaSieNieZmienia() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;
        var column = scene.InfoColumn.Margin.Left;
        Press(rig.Window, Key.Up);
        Assert.True(scene.Chrome.IsShown);

        rig.NextTrack();

        Assert.True(scene.Chrome.IsShown);
        Assert.False(scene.IsMirrored);
        Assert.Equal(column, scene.InfoColumn.Margin.Left);
    });

    [Theory]
    [InlineData(SceneVisual.Spectrum)]
    [InlineData(SceneVisual.Waveform)]
    [InlineData(SceneVisual.Horizon)]
    [InlineData(SceneVisual.Night)]
    public void UkladySzerokieINocNieZmieniajaStrony(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual);
        var scene = rig.Scene;
        var column = scene.InfoColumn.Margin;
        var stage = scene.AudioView.Margin;
        rig.HideControls();

        rig.NextTrack();
        rig.NextTrack();
        rig.NextTrack();

        Assert.False(scene.IsMirrored);
        Assert.Equal(column, scene.InfoColumn.Margin);
        Assert.Equal(stage, scene.AudioView.Margin);
    });

    [Fact]
    public void BezOchronyStronaSieNieZmienia() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc, oled: false);

        rig.NextTrack();

        Assert.False(rig.Scene.IsMirrored);
        Assert.Equal(160, rig.Scene.DiscHost.Margin.Left);
        Assert.Equal(920, rig.Scene.InfoColumn.Margin.Left);
    });

    [Fact]
    public void KartaNastepnyPrzechodziNaStroneKolumnyIWracaPoWylaczeniuUstawienia() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Disc);
        var scene = rig.Scene;
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, scene.NextCard.HorizontalAlignment);
        rig.HideControls();

        rig.NextTrack();
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, scene.NextCard.HorizontalAlignment);

        rig.Harness.Player.SceneOledCare = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(scene.IsMirrored);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, scene.NextCard.HorizontalAlignment);
        Assert.Equal(160, scene.DiscHost.Margin.Left);
    });

    private static double ZoneLeft(SceneView scene, SceneVisual visual) => visual switch
    {
        SceneVisual.Disc => scene.DiscHost.Margin.Left,
        SceneVisual.Cover => scene.CoverArtHost.Margin.Left,
        _ => scene.AudioView.Margin.Left,
    };

    private static double ZoneWidth(SceneVisual visual) => visual switch
    {
        SceneVisual.Disc => 640,
        SceneVisual.Cover => 720,
        _ => 760,
    };
}
