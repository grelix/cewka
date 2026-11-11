using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Tests.SceneInput;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Karta „Następny” i stan „Koniec kolejki” w scenie pełnoekranowej.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneNextCardTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public void Dispose() => Window.Close();
    }

    private static void Render()
    {
        for (var i = 0; i < 2; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Rig Open(
        SceneVisual visual = SceneVisual.Disc,
        EffectsMode effects = EffectsMode.Full,
        double[]? lengths = null,
        Random? random = null)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = effects;
            s.SceneVisual = visual;
        }, random: random);
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h };
        window.Show();

        lengths ??= [60, 60, 60];
        h.OpenPaths(lengths.Select((seconds, i) => h.Track("x" + i, seconds)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));

        h.Player.ReadLargeCover = (_, _) => MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Red))!.Display;

        window.EnterScene();
        Render();
        return rig;
    }

    /// <summary>Przewija do sekundy i daje scenie klatki na przeliczenie.</summary>
    private static void To(Rig rig, double seconds)
    {
        SceneTests.SeekTo(rig.Harness, seconds);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.NextUpCoverTask.IsCompleted));
        Render();
    }

    private static double Target(Visual visual) =>
        visual.GetBaseValue(Visual.OpacityProperty) is { HasValue: true } target ? target.Value : visual.Opacity;

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void GoTo(Rig rig, int index)
    {
        rig.Player.PlayItem(rig.Player.Queue[index]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == index && rig.Player.IsPlaying));
    }

    private static void Hidden(Rig rig)
    {
        Assert.False(rig.Player.HasNextUp);
        Assert.False(rig.Scene.NextCard.IsVisible);
    }

    // ---------- 1. Pojawianie się i treść ----------

    [Fact]
    public void KartaPojawiaSiePietnascieSekundPrzedKoncem() => Ui.Run(() =>
    {
        using var rig = Open();

        To(rig, 30);
        Hidden(rig);

        To(rig, 44);
        Hidden(rig);

        To(rig, 46);
        Assert.True(rig.Player.HasNextUp);
        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Equal(rig.Player.Queue[1].Title, rig.Scene.NextCardTitle.Text);
        Assert.Equal(Strings.Current["SceneUpNext"], rig.Scene.NextCardLabel.Text);
        Assert.Equal(1, Target(rig.Scene.NextCard));
    });

    [Fact]
    public void KartaMaOkladke8ProcentWysokosciWPrawymDolnymRogu() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);

        var scene = rig.Scene;
        Assert.InRange(scene.NextCardCover.Height / scene.Plane.Height, 0.075, 0.085);
        Assert.NotNull(scene.NextCardCover.GetVisualDescendants().OfType<Image>().Single().Source);

        var bounds = scene.NextCard.Bounds;
        Assert.True(bounds.Left > scene.Plane.Bounds.Width / 2, "karta w prawej połowie");
        Assert.True(bounds.Top > scene.Plane.Bounds.Height / 2, "karta w dolnej połowie");
        Assert.True(bounds.Right <= scene.Plane.Bounds.Width && bounds.Bottom <= scene.Plane.Bounds.Height);
    });

    [Fact]
    public void KartaPokazujeFaktycznieNastepnyWpisPrzyLosowejKolejnosci() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60, 60, 60, 60, 60], random: new Random(11));
        rig.Player.ShuffleEnabled = true;
        To(rig, 50);

        var predicted = rig.Harness.Engine.NextIndex;
        Assert.InRange(predicted, 0, 5);
        Assert.NotEqual(rig.Player.CurrentIndex, predicted);
        Assert.Equal(rig.Player.Queue[predicted].Title, rig.Scene.NextCardTitle.Text);

        rig.Player.Next();
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex != 0 && !rig.Player.HasNextUp));
        Assert.Equal(predicted, rig.Player.CurrentIndex);
    });

    [Fact]
    public void WPolskimJezykuEtykietaToNastepny()
    {
        var previous = Strings.Current.CurrentCode;
        try
        {
            Strings.Current.SetLanguage("pl");
            Assert.Equal("Następny", Strings.Current["SceneUpNext"]);
            Assert.Equal("Koniec kolejki", Strings.Current["SceneQueueEnd"]);
            Assert.Equal("Odtwórz od początku", Strings.Current["SceneRestartQueue"]);
        }
        finally
        {
            Strings.Current.SetLanguage(previous);
        }
    }

    // ---------- 2. Ustawienie ----------

    [Fact]
    public void UstawienieWylaczaKarteOdRazu() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        Assert.True(rig.Scene.NextCard.IsVisible);

        rig.Player.SceneShowNext = false;
        Hidden(rig);

        rig.Player.RefreshForCapture();
        Hidden(rig);

        rig.Player.SceneShowNext = true;
        Assert.True(rig.Player.HasNextUp);
        Assert.True(rig.Scene.NextCard.IsVisible);
    });

    // ---------- 3. Brak karty ----------

    [Fact]
    public void BezNastepnegoWpisuKartyNie() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60]);
        GoTo(rig, 1);
        To(rig, 50);

        Hidden(rig);
    });

    [Fact]
    public void PrzyPowtarzaniuUtworuKartyNie() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Player.CycleRepeat();
        rig.Player.CycleRepeat();
        Assert.True(rig.Player.RepeatTrack);
        To(rig, 50);

        Hidden(rig);
    });

    [Fact]
    public void PrzyPowtarzaniuKolejkiNaOstatnimUtworzeNastepnymJestPierwszy() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60]);
        rig.Player.CycleRepeat();
        Assert.True(rig.Player.RepeatQueue);
        GoTo(rig, 1);
        To(rig, 50);

        Assert.True(rig.Player.HasNextUp);
        Assert.Equal(rig.Player.Queue[0].Title, rig.Scene.NextCardTitle.Text);
    });

    [Fact]
    public void JedenKrotkiUtworBezNastepnegoNieMaKarty() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [10]);
        rig.Player.RefreshForCapture();
        Render();

        Hidden(rig);
    });

    [Fact]
    public void KrotkiUtworZNastepnymPokazujeKarte() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [10, 10]);
        Assert.True(rig.Harness.PumpUntil(() =>
        {
            rig.Player.RefreshForCapture();
            return rig.Player.HasNextUp;
        }));

        Assert.Equal(rig.Player.Queue[1].Title, rig.Scene.NextCardTitle.Text);
    });

    // ---------- 4. Znikanie ----------

    [Fact]
    public void KartaZnikaOdRazuPoZmianieUtworu() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        Assert.True(rig.Scene.NextCard.IsVisible);

        rig.Player.Next();
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 1 && !rig.Player.HasNextUp));
        Dispatcher.UIThread.RunJobs();

        Assert.False(rig.Scene.NextCard.IsVisible);
    });

    [Fact]
    public void KartaZnikaPoPrzewinieciuWstecz() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        Assert.True(rig.Scene.NextCard.IsVisible);

        To(rig, 10);
        Hidden(rig);
    });

    [Fact]
    public void ZmianaKolejnosciPokazujeNowyNastepny() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        var before = rig.Scene.NextCardTitle.Text;
        Assert.Equal(rig.Player.Queue[1].Title, before);

        rig.Player.MoveInQueue(2, 1);
        Render();

        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Equal(rig.Player.Queue[1].Title, rig.Scene.NextCardTitle.Text);
        Assert.NotEqual(before, rig.Scene.NextCardTitle.Text);
    });

    // ---------- 5. Wizualizacje i tryb ograniczony ----------

    [Theory]
    [InlineData(SceneVisual.Disc)]
    [InlineData(SceneVisual.Cover)]
    [InlineData(SceneVisual.Night)]
    public void KartaDzialaWeWszystkichWizualizacjach(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual);
        To(rig, 50);

        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Equal(rig.Player.Queue[1].Title, rig.Scene.NextCardTitle.Text);

        // W Nocy jasność jak bloku tekstu, w pozostałych pełna.
        Assert.Equal(visual == SceneVisual.Night ? Target(rig.Scene.NightBlock) : 1, Target(rig.Scene.NextCard));
        if (visual == SceneVisual.Night) Assert.Equal(0.5, Target(rig.Scene.NextCard));
    });

    [Fact]
    public void ZmianaNaNocPrzygasaWidocznaKarte() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        Assert.Equal(1, Target(rig.Scene.NextCard));

        rig.Player.SceneVisual = SceneVisual.Night;
        Render();
        Assert.Equal(0.5, Target(rig.Scene.NextCard));

        rig.Player.SceneVisual = SceneVisual.Disc;
        Render();
        Assert.Equal(1, Target(rig.Scene.NextCard));
    });

    [Fact]
    public void WTrybieOgraniczonymKartaPojawiaSieSkokowo() => Ui.Run(() =>
    {
        using var rig = Open(effects: EffectsMode.Reduced);
        To(rig, 50);

        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Null(rig.Scene.NextCard.Transitions);
        Assert.Equal(1, rig.Scene.NextCard.Opacity);
    });

    [Fact]
    public void WTrybiePelnymKartaPrzenikaPrzyPojawieniu() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 30);
        Assert.Null(rig.Scene.NextCard.Transitions);

        rig.Player.RefreshForCapture();
        SceneTests.SeekTo(rig.Harness, 50);
        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Equal(1, Target(rig.Scene.NextCard));
    });

    // ---------- 6. Pauza to zero pracy ----------

    [Fact]
    public void WPauzieKartaNieMaZmianStanuANiPracy() => Ui.Run(() =>
    {
        using var rig = Open();
        To(rig, 50);
        rig.Player.Pause();
        Assert.True(rig.Harness.PumpUntil(() => !rig.Player.IsPlaying));
        Render();
        Assert.True(rig.Scene.NextCard.IsVisible);

        // Ruch po pauzie wygasa w prawdziwym czasie; sprawdzenie dotyczy stanu po jego końcu. Bez
        // unieważnienia środowisko bez okna nie dostarcza zamówionej klatki, więc pętla by nie skończyła.
        var backdrop = rig.Scene.Backdrop;
        var stopped = Ui.WaitUntil(() =>
        {
            rig.Scene.InvalidateVisual();
            backdrop.InvalidateVisual();
            Render();
            return !rig.Scene.Spinner.IsDrawing && !backdrop.IsDrawing;
        }, 5000);
        Assert.True(stopped, $"płyta {rig.Scene.Spinner.IsDrawing}, tło {backdrop.IsDrawing}, aktywne {backdrop.IsActive}");

        var notifications = new List<string>();
        rig.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName?.StartsWith("NextUp", StringComparison.Ordinal) == true || e.PropertyName == nameof(MainViewModel.HasNextUp))
                notifications.Add(e.PropertyName);
        };

        for (var i = 0; i < 40; i++)
        {
            rig.Player.RefreshForCapture();
            rig.Harness.PumpPeriods(2);
            Render();
        }

        Assert.Empty(notifications);
        Assert.True(rig.Scene.NextCard.IsVisible);
        Assert.Null(rig.Scene.NextCard.Transitions);
        Assert.Equal(Target(rig.Scene.NextCard), rig.Scene.NextCard.Opacity);
        Assert.False(rig.Scene.Spinner.IsDrawing);
        Assert.False(rig.Scene.Backdrop.IsDrawing);
    });

    // ---------- 7. Koniec kolejki ----------

    /// <summary>Zatrzymane słupki i linia nie zostają pod napisem o końcu kolejki; Horyzont ma pod nim miejsce.</summary>
    [Theory]
    [InlineData(SceneVisual.Spectrum, false)]
    [InlineData(SceneVisual.Waveform, false)]
    [InlineData(SceneVisual.Horizon, true)]
    public void KoniecKolejkiNieNakladaSieNaWizualizacje(SceneVisual visual, bool stageVisible) => Ui.Run(() =>
    {
        using var rig = OpenAtQueueEnd();
        rig.Player.SceneVisual = visual;
        Render();

        Assert.Equal(stageVisible, rig.Scene.AudioStage.IsVisible);

        var plane = rig.Scene.Plane;
        var block = rig.Scene.QueueEndBlock;
        var blockTop = block.TranslatePoint(default, plane)!.Value;
        Assert.Equal(160, blockTop.X, 0.5);
        if (stageVisible)
        {
            var stage = rig.Scene.AudioStage;
            Assert.True(stage.TranslatePoint(new Point(0, stage.Bounds.Height), plane)!.Value.Y <= blockTop.Y);
        }

        rig.Player.SceneVisual = SceneVisual.Disc;
        Render();
        Assert.Equal(rig.Scene.IsMirrored ? 120 : 920, block.TranslatePoint(default, plane)!.Value.X, 0.5);
    });

    private static Rig OpenAtQueueEnd()
    {
        var rig = Open(lengths: [60, 60]);
        GoTo(rig, 1);
        SceneTests.SeekTo(rig.Harness, 58);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.QueueEnded && !rig.Player.IsPlaying));
        Render();
        return rig;
    }

    [Fact]
    public void KoniecKolejkiDzialaJakPauzaZNapisemIPrzyciskiem() => Ui.Run(() =>
    {
        using var rig = OpenAtQueueEnd();
        var scene = rig.Scene;

        Assert.False(rig.Player.IsPlaying);
        Assert.True(scene.QueueEndBlock.IsVisible);
        Assert.Equal(Strings.Current["SceneQueueEnd"], scene.QueueEndText.Text);
        Assert.True(scene.RestartButton.IsEffectivelyVisible);
        Assert.True(scene.Chrome.IsShown);
        Assert.False(scene.NextCard.IsVisible);
    });

    [Fact]
    public void PrzyciskOdtwarzaKolejkeOdPierwszegoWpisuMysza() => Ui.Run(() =>
    {
        using var rig = OpenAtQueueEnd();
        var scene = rig.Scene;

        // Ruch ponad próg w pustej części pokazuje kursor; pierwszy klik inaczej tylko go odsłania.
        var empty = rig.Window.Bounds.Height * 0.05;
        rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4, empty));
        rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4 + 40, empty));
        Dispatcher.UIThread.RunJobs();

        var center = scene.RestartButton.TranslatePoint(
            new Point(scene.RestartButton.Bounds.Width / 2, scene.RestartButton.Bounds.Height / 2), rig.Window)!.Value;
        rig.Window.MouseMove(center);
        rig.Window.MouseDown(center, MouseButton.Left);
        rig.Window.MouseUp(center, MouseButton.Left);

        Assert.True(rig.Harness.PumpUntil(() => rig.Player.IsPlaying && rig.Player.CurrentIndex == 0));
        Render();
        Assert.False(rig.Player.QueueEnded);
        Assert.False(scene.QueueEndBlock.IsVisible);
    });

    [Fact]
    public void PrzyciskOdtwarzaKolejkeOdPierwszegoWpisuKlawiatura() => Ui.Run(() =>
    {
        using var rig = OpenAtQueueEnd();
        var scene = rig.Scene;

        // Pierwsza strzałka odsłania warstwę na Odtwórz, druga schodzi do przycisku.
        Press(rig.Window, Key.Down);
        Press(rig.Window, Key.Down);
        Assert.True(scene.RestartButton.IsFocused);

        Press(rig.Window, Key.Enter);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.IsPlaying && rig.Player.CurrentIndex == 0));
        Render();
        Assert.False(scene.QueueEndBlock.IsVisible);
    });

    [Fact]
    public void NapisIPrzyciskZnikajaPoRuszeniuOdtwarzania() => Ui.Run(() =>
    {
        using var rig = OpenAtQueueEnd();
        Assert.True(rig.Scene.QueueEndBlock.IsVisible);

        rig.Player.TogglePlay();
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.IsPlaying));
        Render();

        Assert.False(rig.Player.QueueEnded);
        Assert.False(rig.Scene.QueueEndBlock.IsVisible);
        Assert.False(rig.Scene.RestartButton.IsEffectivelyVisible);
    });

    [Fact]
    public void PrzedKoncemKolejkiNapisuNieMa() => Ui.Run(() =>
    {
        using var rig = Open();

        Assert.False(rig.Player.QueueEnded);
        Assert.False(rig.Scene.QueueEndBlock.IsVisible);
    });
}
