using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Tests.SceneInput;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Rząd okładek kolejki pod klawiszem Q w scenie pełnoekranowej.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneQueueRowTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public TimeSpan Now { get; set; }

        /// <summary>Każdy obraz wczytany na potrzeby rzędu.</summary>
        public ConcurrentBag<Bitmap> Loaded { get; } = [];

        public SceneView Scene => Window.Scene;

        public CoverRow Row => Scene.QueueRow;

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

    private static Bitmap TestBitmap() => MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Red))!.Display;

    private static Rig Open(
        SceneVisual visual = SceneVisual.Disc,
        EffectsMode effects = EffectsMode.Reduced,
        double[]? lengths = null,
        Random? random = null,
        bool enter = true)
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
        window.Scene.Clock = () => rig.Now;
        window.Show();

        lengths ??= [60, 60, 60, 60];
        h.OpenPaths(lengths.Select((seconds, i) => h.Track("q" + i, seconds)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero && !string.IsNullOrEmpty(h.Player.CurrentFilePath)));

        h.Player.ReadLargeCover = (_, _) =>
        {
            var bitmap = TestBitmap();
            rig.Loaded.Add(bitmap);
            return bitmap;
        };

        if (enter)
        {
            window.EnterScene();
            Render();
        }

        return rig;
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void OpenRow(Rig rig)
    {
        Press(rig.Window, Key.Q);
        Assert.True(rig.Scene.IsQueueRowOpen);
        Render();
    }

    /// <summary>Tło skończyło czytać obrazy; bez tego test zamawiałby klatki w trakcie wczytywania.</summary>
    private static void Loaded(Rig rig)
    {
        Assert.True(rig.Harness.PumpUntil(() => !rig.Row.IsLoading));
        Render();
    }

    /// <summary>Zgłoszenie zmiany z silnika dochodzi przy odświeżeniu modelu.</summary>
    private static void Sync(Rig rig)
    {
        rig.Player.RefreshForCapture();
        rig.Harness.PumpPeriods(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static double Target(TranslateTransform transform) =>
        transform.GetBaseValue(TranslateTransform.YProperty) is { HasValue: true } target ? target.Value : transform.Y;

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static CoverRowTile Tile(Rig rig, int index) => rig.Row.Tiles.Single(tile => tile.Index == index);

    private static CoverRowTile FocusedTile(Rig rig) => Tile(rig, rig.Row.FocusIndex);

    private static Point Centre(Visual control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>Przerysowanie wszystkich obrazów sceny; zwolniony obraz w <see cref="Image"/> rzuciłby tu wyjątek.</summary>
    private static void Redraw(Rig rig)
    {
        foreach (var image in rig.Scene.GetVisualDescendants().OfType<Image>()) image.InvalidateVisual();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static int EngineIndex(Rig rig, CoverRowItem item) =>
        rig.Harness.Engine.Queue.ToList().IndexOf((QueueEntry)item.Id);

    // ---------- 1. Otwieranie i przesuwanie ----------

    [Theory]
    [InlineData(SceneVisual.Disc)]
    [InlineData(SceneVisual.Cover)]
    [InlineData(SceneVisual.Night)]
    public void QOtwieraIZamykaRzadWKazdejWizualizacji(SceneVisual visual) => Ui.Run(() =>
    {
        using var rig = Open(visual);
        Assert.False(rig.Row.IsVisible);

        Press(rig.Window, Key.Q);
        Assert.True(rig.Scene.IsQueueRowOpen);
        Assert.True(rig.Row.IsVisible);
        Assert.Equal(-SceneView.QueueRowLift, Target(rig.Scene.BodyMove));
        Assert.Equal(0, Target(rig.Scene.RowMove));
        Assert.True(rig.Row.Tiles.Count > 0);

        Press(rig.Window, Key.Q);
        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.False(rig.Row.IsVisible);
        Assert.Equal(0, Target(rig.Scene.BodyMove));
        Assert.Empty(rig.Row.Tiles);
        Assert.True(rig.Window.IsSceneOpen);
    });

    [Fact]
    public void QNieRuszaPaneluAniKolejkiOkna() => Ui.Run(() =>
    {
        using var rig = Open();
        var panel = rig.Player.PanelOpen;
        var queue = rig.Player.QueuePinned;

        Press(rig.Window, Key.Q);

        Assert.Equal(panel, rig.Player.PanelOpen);
        Assert.Equal(queue, rig.Player.QueuePinned);
    });

    [Fact]
    public void PrzyPelnychAnimacjachBlokUnosiSiePlynnieARzadWjezdza() => Ui.Run(() =>
    {
        using var rig = Open(effects: EffectsMode.Full);

        void WaitForMoves(double body)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (rig.Scene.BodyMove.Y != body || rig.Scene.RowMove.Y != (body == 0 ? rig.Scene.RowMove.Y : 0))
            {
                Assert.True(DateTime.UtcNow < end, "przesuwanie się nie skończyło");
                Render();
                Thread.Sleep(15);
            }
        }

        // Pierwsze otwarcie buduje rząd, więc ruch mierzy się przy kolejnym.
        Press(rig.Window, Key.Q);
        WaitForMoves(-SceneView.QueueRowLift);
        Press(rig.Window, Key.Q);
        WaitForMoves(0);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Press(rig.Window, Key.Q);
        var body = rig.Scene.BodyMove.Y;
        var row = rig.Scene.RowMove.Y;
        var elapsed = watch.Elapsed;

        Assert.NotNull(rig.Scene.BodyMove.Transitions);
        Assert.NotNull(rig.Scene.RowMove.Transitions);
        Assert.Equal(-SceneView.QueueRowLift, Target(rig.Scene.BodyMove));

        // Wolna maszyna może obsłużyć klawisz dłużej niż trwa ruch; stan w trakcie ruchu da się wtedy tylko pominąć.
        if (elapsed < SceneView.QueueRowSlide)
        {
            Assert.True(body > -SceneView.QueueRowLift, "blok od razu na miejscu");
            Assert.True(row > 0, "rząd od razu na miejscu");
        }

        WaitForMoves(-SceneView.QueueRowLift);
    });

    [Fact]
    public void PrzyOgraniczonychAnimacjachObaPrzesuwajaSieSkokowo() => Ui.Run(() =>
    {
        using var rig = Open(effects: EffectsMode.Reduced);

        Press(rig.Window, Key.Q);

        Assert.Null(rig.Scene.BodyMove.Transitions);
        Assert.Null(rig.Scene.RowMove.Transitions);
        Assert.Equal(-SceneView.QueueRowLift, rig.Scene.BodyMove.Y);
        Assert.Equal(0, rig.Scene.RowMove.Y);

        Press(rig.Window, Key.Q);

        Assert.Equal(0, rig.Scene.BodyMove.Y);
    });

    [Fact]
    public void BlokNocyUnosiSieTylkoZDolnegoMiejsca() => Ui.Run(() =>
    {
        using var rig = Open(SceneVisual.Night);

        Press(rig.Window, Key.Q);
        Assert.Equal(0, rig.Scene.NightMove.Y);
        Render();
        Assert.True(rig.Scene.NightBlock.TranslatePoint(default, rig.Scene.Plane)!.Value.Y >= 0);

        // Miejsca: dwa górne, potem dolne.
        rig.Scene.FinishNightMove();
        rig.Scene.FinishNightMove();
        Render();
        Assert.Equal(-SceneView.QueueRowLift, rig.Scene.NightMove.Y);

        var block = new Rect(rig.Scene.NightBlock.TranslatePoint(default, rig.Scene.Plane)!.Value, rig.Scene.NightBlock.Bounds.Size);
        var row = new Rect(rig.Row.TranslatePoint(default, rig.Scene.Plane)!.Value, rig.Row.Bounds.Size);
        Assert.False(block.Intersects(row), "blok Nocy nachodzi na rząd");

        rig.Scene.FinishNightMove();
        Assert.Equal(0, rig.Scene.NightMove.Y);
    });

    // ---------- 2. Okładki ----------

    [Fact]
    public void OkladkiSaKwadratoweOkolo16ProcentWysokosciZagranePrzygaszoneBiezacyZKropka() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Player.PlayItem(rig.Player.Queue[2]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 2));
        OpenRow(rig);

        var current = Tile(rig, 2);
        var size = current.GetVisualDescendants().OfType<Image>().Single().Parent as Border;
        Assert.NotNull(size);
        Assert.Equal(size.Bounds.Width, size.Bounds.Height, 3);
        Assert.InRange(size.Bounds.Height / rig.Scene.Plane.Height, 0.15, 0.17);

        Assert.Contains("played", Tile(rig, 0).Classes);
        Assert.Contains("played", Tile(rig, 1).Classes);
        Assert.DoesNotContain("played", current.Classes);
        Assert.DoesNotContain("played", Tile(rig, 3).Classes);
        Assert.True(Tile(rig, 0).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("cover-tile-art")).Opacity < 1);
        Assert.Equal(1, current.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("cover-tile-art")).Opacity);

        Assert.Equal([2], rig.Row.Tiles.Where(tile => tile.Dot.IsVisible).Select(tile => tile.Index));

        Assert.True(Tile(rig, 0).Bounds.X < current.Bounds.X && current.Bounds.X < Tile(rig, 3).Bounds.X);

        // Fokus po otwarciu na bieżącym.
        Assert.Equal(2, rig.Row.FocusIndex);
        Assert.Same(current, Focused(rig.Window));
        Assert.Contains("focused", current.Classes);
    });

    [Fact]
    public void BrakOkladkiPokazujeSpirale() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Player.ReadLargeCover = (_, _) => null;
        OpenRow(rig);
        Loaded(rig);

        var sources = rig.Row.Tiles.Select(tile => tile.Picture.Source).ToList();
        Assert.All(sources, Assert.NotNull);
        Assert.Single(sources.Distinct());
        Redraw(rig);
    });

    [Fact]
    public void OkladkaZPlikuTrafiaDoOkladkiRzedu() => Ui.Run(() =>
    {
        using var rig = Open();
        var heights = new ConcurrentBag<int>();
        var read = rig.Player.ReadLargeCover;
        rig.Player.ReadLargeCover = (path, height) =>
        {
            heights.Add(height);
            return read(path, height);
        };

        OpenRow(rig);
        Loaded(rig);

        Assert.All(rig.Row.Tiles, tile => Assert.Contains(tile.Picture.Source as Bitmap, rig.Loaded));
        Assert.NotEmpty(heights);
        Assert.All(heights, height => Assert.InRange(height, 1, 256));
    });

    [Fact]
    public void KazdaOkladkaMaNazweDostepnosciABiezacaMowiZeGra() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(rig.Row)));
        Assert.All(rig.Row.Tiles, tile =>
            Assert.Contains(tile.Item.Title, Avalonia.Automation.AutomationProperties.GetName(tile)));

        var current = Avalonia.Automation.AutomationProperties.GetName(Tile(rig, 0));
        Assert.StartsWith(string.Format(Strings.Current["QueueNowPlaying"], Tile(rig, 0).Item.Title), current);
    });

    // ---------- 3. Kolejność i odświeżanie ----------

    [Fact]
    public void BezLosowaniaRzadIdzieWKolejnosciListy() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Assert.Equal(rig.Harness.Engine.Queue, rig.Row.Items.Select(item => (QueueEntry)item.Id));
        Assert.Equal(rig.Player.CurrentIndex, rig.Row.CurrentIndex);
    });

    [Fact]
    public void PrzyLosowaniuRzadPokazujeKolejnoscSilnikaANastepnyZgadzaSieZSilnikiem() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60, 60, 60, 60, 60, 60, 60], random: new Random(5));
        rig.Player.ShuffleEnabled = true;
        OpenRow(rig);

        var order = rig.Harness.Engine.PlaybackOrder;
        Assert.Equal(order.Entries, rig.Row.Items.Select(item => (QueueEntry)item.Id));
        Assert.Equal(order.CurrentPosition, rig.Row.CurrentIndex);

        var next = rig.Row.Items[rig.Row.CurrentIndex + 1];
        Assert.Equal(rig.Harness.Engine.NextIndex, EngineIndex(rig, next));
    });

    [Fact]
    public void PrzelaczenieLosowaniaOdswiezaRzadIZachowujeFokusNaWpisie() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60, 60, 60, 60, 60, 60, 60], random: new Random(5));
        OpenRow(rig);
        Press(rig.Window, Key.Right);
        Press(rig.Window, Key.Right);
        var focused = rig.Row.FocusedItem!.Id;

        Press(rig.Window, Key.S);
        Render();

        Assert.True(rig.Player.ShuffleEnabled);
        Assert.Equal(rig.Harness.Engine.PlaybackOrder.Entries, rig.Row.Items.Select(item => (QueueEntry)item.Id));
        Assert.Same(focused, rig.Row.FocusedItem!.Id);
        Assert.Same(FocusedTile(rig), Focused(rig.Window));
        Assert.True(rig.Scene.IsQueueRowOpen);
    });

    [Fact]
    public void ZmianaBiezacegoPrzesuwaKropkeAFokusZostajeNaWpisie() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Press(rig.Window, Key.End);
        var focused = rig.Row.FocusedItem!.Id;

        rig.Player.Next();
        Assert.True(rig.Harness.PumpUntil(() => rig.Row.CurrentIndex == 1));
        Render();

        Assert.Same(focused, rig.Row.FocusedItem!.Id);
        Assert.Equal([1], rig.Row.Tiles.Where(tile => tile.Dot.IsVisible).Select(tile => tile.Index));
        Assert.Contains("played", Tile(rig, 0).Classes);
    });

    [Fact]
    public void DodanieDoKolejkiPrzyOtwartymRzedzieDokladaWpisyIZachowujeFokus() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Press(rig.Window, Key.Right);
        var focused = rig.Row.FocusedItem!.Id;

        rig.Harness.AddPaths([rig.Harness.Track("dodany")]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rig.Harness.PumpUntil(() => rig.Row.Items.Count == 5));
        Assert.Same(focused, rig.Row.FocusedItem!.Id);
    });

    [Fact]
    public void KoniecRundyPrzyPowtarzaniuZLosowaniemZmieniaRzadDopieroPrzyPrzejsciu() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60, 60, 60], random: new Random(9));
        rig.Player.ShuffleEnabled = true;
        rig.Harness.Engine.Repeat = RepeatMode.Queue;

        for (var i = 0; i < 10 && rig.Harness.Engine.PlaybackOrder.CurrentPosition < 3; i++)
        {
            var next = rig.Harness.Engine.PlaybackOrder;
            var target = rig.Harness.Engine.Queue.ToList().IndexOf(next.Entries[next.CurrentPosition + 1]);
            rig.Harness.Engine.PlayIndex(target);
            Assert.True(rig.Harness.PumpUntil(() => rig.Harness.Engine.CurrentIndex == target));
        }

        OpenRow(rig);
        Assert.Equal(3, rig.Row.CurrentIndex);

        // Sam odczyt następnego wpisu niczego nie przestawia.
        var predicted = rig.Harness.Engine.NextIndex;
        Sync(rig);
        Assert.Equal(3, rig.Row.CurrentIndex);

        // Dopiero przejście zaczyna nową rundę: zapowiedziany wpis jest jej początkiem.
        rig.Player.Next();
        Assert.True(rig.Harness.PumpUntil(() => rig.Harness.Engine.CurrentIndex == predicted));
        Sync(rig);
        Assert.True(rig.Harness.PumpUntil(() => rig.Row.CurrentIndex == 0));
        Assert.Equal(predicted, EngineIndex(rig, rig.Row.Items[0]));
    });

    // ---------- 4. Podpis ----------

    [Fact]
    public void PodpisJestJedenPodOkladkaZFokusemIMaTytulWykonawceICzas() => Ui.Run(() =>
    {
        using var rig = Open(enter: false);
        var h = rig.Harness;
        h.OpenPaths([h.TaggedTrack("tag0", "Pierwszy", "Zespół", "Album"), h.TrackWithTaggedLength("tag1", 221)]);
        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 2 && h.Player.Queue[1].DurationSeconds > 0));
        rig.Window.EnterScene();
        Render();
        OpenRow(rig);

        Assert.StartsWith("Pierwszy · Zespół", rig.Row.Caption.Text);

        Press(rig.Window, Key.Right);
        Assert.EndsWith("3:41", rig.Row.Caption.Text);

        Assert.Equal(1, rig.Row.Caption.MaxLines);
        Assert.Equal(TextTrimming.CharacterEllipsis, rig.Row.Caption.TextTrimming);
        Assert.All(rig.Row.Tiles, tile => Assert.DoesNotContain(tile.GetVisualDescendants().OfType<TextBlock>(), text => !string.IsNullOrEmpty(text.Text)));
    });

    [Fact]
    public void ZnacznikiWczytaneWTrakcieOdswiezajaPodpis() => Ui.Run(() =>
    {
        using var rig = Open(enter: false);
        var h = rig.Harness;
        h.OpenPaths([h.TaggedTrack("meta0", "Znacznik", "Zespół", "Album")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
        rig.Window.EnterScene();
        Render();

        Press(rig.Window, Key.Q);
        Assert.True(h.PumpUntil(() =>
        {
            rig.Player.RefreshForCapture();
            return rig.Row.Caption.Text?.StartsWith("Znacznik", StringComparison.Ordinal) == true;
        }));
    });

    // ---------- 5. Klawisze i mysz ----------

    [Fact]
    public void StrzalkiPrzesuwajaFokusBezPrzewijaniaUtworuIBezZawijania() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Harness.PumpUntil(() => rig.Harness.Engine.Position > TimeSpan.Zero);
        OpenRow(rig);
        var position = rig.Player.Progress;

        Press(rig.Window, Key.Left);
        Assert.Equal(0, rig.Row.FocusIndex);

        Press(rig.Window, Key.Right);
        Press(rig.Window, Key.Right);
        Assert.Equal(2, rig.Row.FocusIndex);
        Assert.Same(FocusedTile(rig), Focused(rig.Window));
        Assert.Contains("focused", FocusedTile(rig).Classes);
        Assert.Single(rig.Row.Tiles, tile => tile.Classes.Contains("focused"));

        Assert.InRange(rig.Player.Progress, position - 0.01, position + 0.01);
    });

    [Fact]
    public void HomeIEndIdaNaKranceListy() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Press(rig.Window, Key.End);
        Assert.Equal(3, rig.Row.FocusIndex);

        Press(rig.Window, Key.Home);
        Assert.Equal(0, rig.Row.FocusIndex);
    });

    [Fact]
    public void StrzalkiPionoweITabZostawiajaFokusWRzedzie() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        var tile = FocusedTile(rig);

        Press(rig.Window, Key.Up);
        Press(rig.Window, Key.Down);
        Press(rig.Window, Key.Tab);

        Assert.Same(tile, Focused(rig.Window));
    });

    [Fact]
    public void BackspaceTezZamykaNajpierwRzad() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Press(rig.Window, Key.Back);
        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.True(rig.Window.IsSceneOpen);
    });

    [Fact]
    public void OtwarcieRzeduKonczyRegulacjePaska() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Scene.Chrome.SetAdjusting(true);

        OpenRow(rig);

        Assert.False(rig.Scene.Chrome.IsAdjusting);
    });

    [Fact]
    public void EnterOdtwarzaWskazanyARzadZostajeOtwarty() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Press(rig.Window, Key.Right);
        Press(rig.Window, Key.Right);

        Press(rig.Window, Key.Enter);

        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 2));
        Assert.True(rig.Scene.IsQueueRowOpen);
        Assert.Equal(2, rig.Row.FocusIndex);
        Assert.True(rig.Harness.PumpUntil(() => rig.Row.CurrentIndex == 2));
    });

    [Fact]
    public void EscZamykaRzadADopieroNastepnyScene() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Press(rig.Window, Key.Escape);
        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.True(rig.Window.IsSceneOpen);
        Assert.Same(rig.Scene, Focused(rig.Window));

        Press(rig.Window, Key.Escape);
        Assert.False(rig.Window.IsSceneOpen);
    });

    [Fact]
    public void WyjscieZeSceny_F11_ZamykaTakzeRzad() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Loaded(rig);
        var images = rig.Row.Tiles.Select(tile => tile.Picture).ToList();

        Press(rig.Window, Key.F11);

        Assert.False(rig.Window.IsSceneOpen);
        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.False(rig.Player.QueueRowActive);
        Assert.All(images, image => Assert.Null(image.Source));
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));
    });

    [Fact]
    public void SpacjaIKlawiszeSpozaRzeduDzialajaJakZawsze() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);

        Press(rig.Window, Key.Space);
        Assert.True(rig.Harness.PumpUntil(() => !rig.Player.IsPlaying));
        Assert.Equal(0, rig.Row.FocusIndex);
        Assert.Equal(0, rig.Player.CurrentIndex);

        var visual = rig.Player.SceneVisual;
        Press(rig.Window, Key.V);
        Assert.NotEqual(visual, rig.Player.SceneVisual);
        Assert.True(rig.Scene.IsQueueRowOpen);

        Press(rig.Window, Key.Right, RawInputModifiers.Control);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 1));
        Assert.True(rig.Scene.IsQueueRowOpen);
    });

    [Fact]
    public void KlikniecieOkladkiOdtwarzaWpis() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Render();

        var point = Centre(Tile(rig, 2), rig.Window);

        // Ruch ponad próg pokazuje kursor: przy ukrytym mysz niczego nie naciska na ślepo.
        rig.Window.MouseMove(point - new Point(0, 60));
        rig.Window.MouseMove(point);
        Assert.True(rig.Scene.Chrome.IsCursorShown);

        rig.Window.MouseDown(point, MouseButton.Left);
        rig.Window.MouseUp(point, MouseButton.Left);

        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 2));
        Assert.Equal(2, rig.Row.FocusIndex);
        Assert.True(rig.Scene.IsQueueRowOpen);
        Assert.True(rig.Window.IsSceneOpen);
    });

    [Fact]
    public void KlikPrzyUkrytymKursorzeTylkoOdslaniaKursor() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Render();
        Assert.False(rig.Scene.Chrome.IsCursorShown);

        var point = Centre(Tile(rig, 2), rig.Window);
        rig.Window.MouseDown(point, MouseButton.Left);
        rig.Window.MouseUp(point, MouseButton.Left);

        Assert.True(rig.Scene.Chrome.IsCursorShown);
        rig.Harness.PumpPeriods(5);
        Assert.Equal(0, rig.Player.CurrentIndex);
    });

    [Fact]
    public void PrzewijaniePrzyPelnychAnimacjachJestPlynne_AOgraniczonychSkokowe() => Ui.Run(() =>
    {
        using (var full = Open(effects: EffectsMode.Full, lengths: Enumerable.Repeat(60.0, 40).ToArray()))
        {
            OpenRow(full);
            Assert.True(full.Row.IsAnimated);
            for (var i = 0; i < 8; i++) Press(full.Window, Key.Right);

            Assert.NotNull(full.Row.ScrollTransform.Transitions);
            Assert.Equal(-full.Row.ScrollOffset, full.Row.ScrollTransform.GetBaseValue(TranslateTransform.XProperty).Value, 3);

            // Daleki skok nie przelatuje przez okładki, których nie ma.
            Press(full.Window, Key.End);
            Assert.Null(full.Row.ScrollTransform.Transitions);
            Assert.Equal(-full.Row.ScrollOffset, full.Row.ScrollTransform.X, 3);
        }

        using var reduced = Open(effects: EffectsMode.Reduced, lengths: Enumerable.Repeat(60.0, 40).ToArray());
        OpenRow(reduced);
        Assert.False(reduced.Row.IsAnimated);
        for (var i = 0; i < 8; i++) Press(reduced.Window, Key.Right);

        Assert.Null(reduced.Row.ScrollTransform.Transitions);
        Assert.True(reduced.Row.ScrollOffset > 0);
        Assert.Equal(-reduced.Row.ScrollOffset, reduced.Row.ScrollTransform.X, 3);
    });

    // ---------- 6. Wirtualizacja ----------

    [Fact]
    public void Kolejka5000PozycjiMaMaloOkladekIOtwarcieNieCzytaWszystkichPlikow() => Ui.Run(() =>
    {
        using var rig = Open(enter: false);
        var h = rig.Harness;

        // Ten sam plik wiele razy: liczy się liczba wpisów, a nie plików na dysku testu.
        var path = h.Track("wiele");
        h.OpenPaths(Enumerable.Repeat(path, 5000));
        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 5000 && h.Player.IsPlaying, 20000));

        var reads = 0;
        h.Player.ReadLargeCover = (_, _) =>
        {
            Interlocked.Increment(ref reads);
            var bitmap = TestBitmap();
            rig.Loaded.Add(bitmap);
            return bitmap;
        };

        rig.Window.EnterScene();
        Render();
        OpenRow(rig);
        Loaded(rig);

        int Covers() => rig.Scene.GetVisualDescendants().OfType<CoverRowTile>().Count();

        Assert.Equal(5000, rig.Row.Items.Count);
        Assert.InRange(Covers(), 1, 30);
        Assert.InRange(Volatile.Read(ref reads), 1, 30);

        // Na końcu i po skoku z powrotem nadal garść okładek, a stare obrazy są już zwolnione.
        Press(rig.Window, Key.End);
        Loaded(rig);
        Assert.InRange(Covers(), 1, 30);
        Assert.Equal(4999, rig.Row.FocusIndex);

        Press(rig.Window, Key.Home);
        Loaded(rig);
        Assert.InRange(Covers(), 1, 30);
        Assert.InRange(Volatile.Read(ref reads), 1, 90);

        var alive = rig.Loaded.Count(bitmap => !IsDisposed(bitmap));
        Assert.InRange(alive, 1, 30);

        Press(rig.Window, Key.Q);
        Assert.Equal(0, Covers());
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));
    });

    // ---------- 7. Zwalnianie obrazów ----------

    [Fact]
    public void PoPrzewinieciuIZamknieciuRzeduPrzerysowanieNieRzucaWyjatku() => Ui.Run(() =>
    {
        using var rig = Open(lengths: Enumerable.Repeat(60.0, 40).ToArray());
        OpenRow(rig);
        Loaded(rig);
        Redraw(rig);

        // Szybkie przewijanie: część okładek znika, zanim zdąży się wczytać.
        for (var i = 0; i < 30; i++)
        {
            Press(rig.Window, Key.Right);
            if (i % 5 == 0) Redraw(rig);
        }

        Press(rig.Window, Key.End);
        Redraw(rig);
        Press(rig.Window, Key.Home);
        Redraw(rig);
        Loaded(rig);
        Redraw(rig);

        Assert.All(rig.Row.Tiles, tile =>
        {
            if (tile.Picture.Source is Bitmap bitmap) Assert.False(IsDisposed(bitmap));
        });

        var images = rig.Row.Tiles.Select(tile => tile.Picture).ToList();
        Press(rig.Window, Key.Escape);
        Redraw(rig);

        Assert.All(images, image => Assert.Null(image.Source));
        Assert.True(rig.Harness.PumpUntil(() => !rig.Row.IsLoading));
        Redraw(rig);
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));
    });

    [Fact]
    public void OdczytKonczacySiePoZamknieciuRzeduZwalniaWynikBezDotykaniaOkladki() => Ui.Run(() =>
    {
        using var rig = Open(lengths: Enumerable.Repeat(60.0, 20).ToArray());
        using var gate = new ManualResetEventSlim();
        var produced = new ConcurrentBag<Bitmap>();
        var started = 0;

        rig.Player.ReadLargeCover = (_, _) =>
        {
            Interlocked.Increment(ref started);
            gate.Wait();
            var bitmap = TestBitmap();
            produced.Add(bitmap);
            return bitmap;
        };

        OpenRow(rig);
        Assert.True(rig.Harness.PumpUntil(() => Volatile.Read(ref started) == 2));

        // Klatki w trakcie wczytywania: obrazu jeszcze nie ma, a okładki muszą się dać narysować.
        Redraw(rig);
        Press(rig.Window, Key.End);
        Redraw(rig);

        var images = rig.Row.Tiles.Select(tile => tile.Picture).ToList();
        Press(rig.Window, Key.Q);
        Redraw(rig);

        gate.Set();
        Assert.True(rig.Harness.PumpUntil(() => !rig.Row.IsLoading));
        Redraw(rig);

        Assert.Equal(2, produced.Count);
        Assert.All(produced, bitmap => Assert.True(IsDisposed(bitmap)));
        Assert.All(images, image => Assert.Null(image.Source));
        Assert.Equal(2, Volatile.Read(ref started));
    });

    [Fact]
    public void ZamkniecieOknaPrzyOtwartymRzedzieOdpinaIZwalniaObrazy() => Ui.Run(() =>
    {
        var rig = Open();
        OpenRow(rig);
        Loaded(rig);
        var images = rig.Row.Tiles.Select(tile => tile.Picture).ToList();
        Assert.All(images, image => Assert.NotNull(image.Source));

        rig.Window.Close();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.All(images, image => Assert.Null(image.Source));
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));
    });

    [Fact]
    public void PonowneOtwarcieWczytujeObrazyOdNowa() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Loaded(rig);

        Press(rig.Window, Key.Q);
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));

        OpenRow(rig);
        Loaded(rig);
        Assert.All(rig.Row.Tiles, tile => Assert.False(IsDisposed(Assert.IsAssignableFrom<Bitmap>(tile.Picture.Source))));
        Redraw(rig);
    });

    // ---------- 8. Radio i źródło na żywo ----------

    [Fact]
    public void PrzyZrodleNaZywoQNicNieOtwieraAOtwartyRzadSieZamyka() => Ui.Run(() =>
    {
        using var rig = Open();
        var engine = rig.Harness.Engine;

        OpenRow(rig);
        engine.PlayLive(new LiveSource("s1", "Stacja", [new Uri("http://127.0.0.1:1/s1")]));
        Sync(rig);

        Assert.True(rig.Harness.PumpUntil(() =>
        {
            rig.Player.RefreshForCapture();
            return !rig.Scene.IsQueueRowOpen;
        }));
        Assert.False(rig.Row.IsVisible);

        Press(rig.Window, Key.Q);
        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.False(rig.Row.IsVisible);
        Assert.True(rig.Window.IsSceneOpen);

        engine.StopLive();
    });

    // ---------- 9. Karta „Następny” i koniec kolejki ----------

    [Fact]
    public void KartaNastepnyZnikaPrzyOtwartymRzedzieIWracaGdyNadalJejCzas() => Ui.Run(() =>
    {
        using var rig = Open();
        SceneTests.SeekTo(rig.Harness, 50);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.NextUpCoverTask.IsCompleted && rig.Player.HasNextUp));
        Render();
        Assert.True(rig.Scene.NextCard.IsVisible);

        OpenRow(rig);
        Assert.False(rig.Scene.NextCard.IsVisible);

        Press(rig.Window, Key.Q);
        Assert.True(rig.Scene.NextCard.IsVisible);
    });

    [Fact]
    public void PoZamknieciuRzeduKartaNieWracaGdyMinalJejCzas() => Ui.Run(() =>
    {
        using var rig = Open();
        SceneTests.SeekTo(rig.Harness, 50);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.NextUpCoverTask.IsCompleted && rig.Player.HasNextUp));
        Render();

        OpenRow(rig);
        SceneTests.SeekTo(rig.Harness, 10);
        Assert.True(rig.Harness.PumpUntil(() => !rig.Player.HasNextUp));

        Press(rig.Window, Key.Q);
        Assert.False(rig.Scene.NextCard.IsVisible);
    });

    [Fact]
    public void KoniecKolejkiZPrzyciskiemNieNachodziNaRzad() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60, 60]);
        rig.Player.PlayItem(rig.Player.Queue[1]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.CurrentIndex == 1));
        SceneTests.SeekTo(rig.Harness, 58);
        Assert.True(rig.Harness.PumpUntil(() => rig.Player.QueueEnded && !rig.Player.IsPlaying));
        Render();
        Assert.True(rig.Scene.QueueEndBlock.IsVisible);

        OpenRow(rig);
        Render();

        var plane = rig.Scene.Plane;
        var block = new Rect(rig.Scene.QueueEndBlock.TranslatePoint(default, plane)!.Value, rig.Scene.QueueEndBlock.Bounds.Size);
        var row = new Rect(rig.Row.TranslatePoint(default, plane)!.Value, rig.Row.Bounds.Size);
        var column = new Rect(rig.Scene.InfoColumn.TranslatePoint(default, plane)!.Value, rig.Scene.InfoColumn.Bounds.Size);

        Assert.True(block.Bottom <= row.Top, $"napis kończy się na {block.Bottom}, rząd zaczyna na {row.Top}");
        Assert.False(block.Intersects(column), "napis nachodzi na kolumnę");
        Assert.True(row.Bottom <= plane.Bounds.Height);
        Assert.True(column.Top >= 0);
    });

    // ---------- 10. Pauza i warstwa sterowania ----------

    [Fact]
    public void WarstwaNieGasnieIRzadZostajePrzezCalyCzasOtwarcia() => Ui.Run(() =>
    {
        using var rig = Open();
        Assert.False(rig.Scene.Chrome.IsShown);

        OpenRow(rig);
        Assert.True(rig.Scene.Chrome.IsShown);
        Assert.Null(rig.Scene.Chrome.NextDeadline);

        rig.Now += TimeSpan.FromHours(1);
        rig.Scene.AdvanceTimers();
        Dispatcher.UIThread.RunJobs();

        Assert.True(rig.Scene.Chrome.IsShown);
        Assert.True(rig.Scene.IsQueueRowOpen);
        Assert.True(rig.Row.IsVisible);

        // Po zamknięciu licznik biegnie od początku i warstwa gaśnie jak zwykle.
        Press(rig.Window, Key.Q);
        Assert.True(rig.Scene.Chrome.IsShown);
        Assert.NotNull(rig.Scene.Chrome.NextDeadline);

        rig.Now += SceneChrome.HideDelay + TimeSpan.FromSeconds(1);
        rig.Scene.AdvanceTimers();
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Scene.Chrome.IsShown);
    });

    [Fact]
    public void OtwartyRzadWPauzieNieZamawiaKlatekIMaOdliczaniaBezPracy() => Ui.Run(() =>
    {
        using var rig = Open(effects: EffectsMode.Full);
        rig.Player.Pause();
        Assert.True(rig.Harness.PumpUntil(() => !rig.Player.IsPlaying));

        OpenRow(rig);
        Loaded(rig);

        // Do końca wjazdu i wczytywania, potem już cisza.
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Scene.BodyMove.Y != -SceneView.QueueRowLift || rig.Scene.RowMove.Y != 0)
        {
            Assert.True(DateTime.UtcNow < end, "przesuwanie się nie skończyło");
            Render();
            Thread.Sleep(15);
        }

        var requests = 0;
        var revisions = 0;

        void Count(Visual owner)
        {
            if (ReferenceEquals(TopLevel.GetTopLevel(owner), rig.Window)) requests++;
        }

        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.QueueRowRevision)) revisions++;
        }

        RenderLoop.FrameRequested += Count;
        rig.Player.PropertyChanged += Changed;

        try
        {
            for (var i = 0; i < 30; i++)
            {
                rig.Player.RefreshForCapture();
                rig.Now += TimeSpan.FromSeconds(1);
                rig.Scene.AdvanceTimers();
                Render();
            }

            Assert.Equal(0, requests);
            Assert.Equal(0, revisions);
        }
        finally
        {
            RenderLoop.FrameRequested -= Count;
            rig.Player.PropertyChanged -= Changed;
        }

        Assert.Null(rig.Scene.Chrome.NextDeadline);
        Assert.True(rig.Scene.Chrome.IsShown);
        Assert.False(rig.Row.IsLoading);
        Assert.False(rig.Scene.Spinner.IsDrawing);
        Assert.False(rig.Scene.Backdrop.IsDrawing);
    });

    // ---------- 11. Przypadki graniczne ----------

    [Fact]
    public void PustaKolejkaQNicNieOtwiera() => Ui.Run(() =>
    {
        using var rig = Open(enter: false);
        rig.Player.ClearQueue();
        Dispatcher.UIThread.RunJobs();
        rig.Window.EnterScene();
        Render();

        Press(rig.Window, Key.Q);

        Assert.False(rig.Scene.IsQueueRowOpen);
        Assert.False(rig.Row.IsVisible);
        Assert.Equal(0, rig.Scene.BodyMove.Y);
    });

    [Fact]
    public void WyczyszczenieKolejkiPrzyOtwartymRzedzieGoZamyka() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Loaded(rig);

        rig.Player.ClearQueue();
        Assert.True(rig.Harness.PumpUntil(() => !rig.Scene.IsQueueRowOpen));

        Assert.False(rig.Row.IsVisible);
        Assert.Equal(0, rig.Scene.BodyMove.Y);
        Assert.NotSame(rig.Row, Focused(rig.Window));
        Assert.All(rig.Loaded, bitmap => Assert.True(IsDisposed(bitmap)));
        Redraw(rig);
    });

    [Fact]
    public void JedenWpisStrzalkiNicNieRobiaAEscZamyka() => Ui.Run(() =>
    {
        using var rig = Open(lengths: [60]);
        OpenRow(rig);

        Assert.Single(rig.Row.Items);
        Assert.Equal(0, rig.Row.CurrentIndex);

        Press(rig.Window, Key.Right);
        Press(rig.Window, Key.Left);
        Press(rig.Window, Key.End);
        Assert.Equal(0, rig.Row.FocusIndex);
        Assert.Equal(0, rig.Row.ScrollOffset);

        Press(rig.Window, Key.Escape);
        Assert.False(rig.Scene.IsQueueRowOpen);
    });

    [Fact]
    public void UsuniecieBiezacegoPrzyOtwartymRzedzieZostawiaRzadBezKropki() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Loaded(rig);

        rig.Player.RemoveFromQueue(rig.Player.Queue[0]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Row.Items.Count == 3));
        Loaded(rig);
        Redraw(rig);

        Assert.Equal(-1, rig.Row.CurrentIndex);
        Assert.Equal(0, rig.Row.FocusIndex);
        Assert.DoesNotContain(rig.Row.Tiles, tile => tile.Dot.IsVisible);
        Assert.DoesNotContain(rig.Row.Tiles, tile => tile.Classes.Contains("played"));
        Assert.Same(FocusedTile(rig), Focused(rig.Window));
        Assert.True(rig.Scene.IsQueueRowOpen);
    });

    [Fact]
    public void UsuniecieWpisuZFokusemPrzenosiFokusNaToSamoMiejsce() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Press(rig.Window, Key.End);
        var previous = rig.Row.Items[2].Id;

        rig.Player.RemoveFromQueue(rig.Player.Queue[3]);
        Assert.True(rig.Harness.PumpUntil(() => rig.Row.Items.Count == 3));

        Assert.Equal(2, rig.Row.FocusIndex);
        Assert.Same(previous, rig.Row.FocusedItem!.Id);
        Assert.Same(FocusedTile(rig), Focused(rig.Window));
    });

    [Fact]
    public void KolejkaZmienionaWTleWSilnikuOdswiezaRzadPrzyOdswiezeniuModelu() => Ui.Run(() =>
    {
        using var rig = Open();
        OpenRow(rig);
        Press(rig.Window, Key.Right);
        var focused = rig.Row.FocusedItem!.Id;
        var engine = rig.Harness.Engine;

        // Zmiana z innego wątku, której model widoku nie zlecił.
        Task.Run(() => engine.Move(3, 0)).Wait();

        Assert.True(rig.Harness.PumpUntil(() =>
        {
            rig.Player.RefreshForCapture();
            return ReferenceEquals(rig.Row.Items[0].Id, engine.Queue[0]);
        }));

        Assert.Same(focused, rig.Row.FocusedItem!.Id);
        Assert.Equal(engine.CurrentIndex, rig.Row.CurrentIndex);

        // Wpis, którego model jeszcze nie ma, po prostu nie pojawia się w rzędzie.
        Task.Run(() => engine.Enqueue([rig.Harness.Track("w-tle")])).Wait();
        rig.Player.RefreshForCapture();
        rig.Harness.PumpPeriods(3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, rig.Row.Items.Count);
        Redraw(rig);
    });
}
