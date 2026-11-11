using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.ViewModels;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests;

/// <summary>Rząd okładek jako kontrolka ogólna: bez kolejki, plików i silnika.</summary>
[Collection(UiCollection.Name)]
public sealed class CoverRowTests
{
    private sealed class Stand : IDisposable
    {
        public required Window Window { get; init; }

        public required CoverRow Row { get; init; }

        public int Loads;

        public void Dispose() => Window.Close();

        public CoverRowItem Item(string id, string title = "Stacja", string subtitle = "", string? duration = null) =>
            new(id, title, subtitle, duration, () =>
            {
                Interlocked.Increment(ref Loads);
                return MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Blue))!.Display;
            });

        public List<CoverRowItem> Items(int count) => Enumerable.Range(0, count).Select(i => Item("s" + i, "Stacja " + i)).ToList();
    }

    private static Stand Make()
    {
        var row = new CoverRow { Width = 1000, EdgePadding = 20, TileSize = 100, TileSpacing = 20, FooterHeight = 10 };
        var window = new Window { Width = 1000, Height = 400, Content = row };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Stand { Window = window, Row = row };
    }

    private static void Settle(CoverRow row)
    {
        Assert.True(Ui.WaitUntil(() => !row.IsLoading, 5000));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void ElementyBezCzasuIPodtytuluMajaKrotszyPodpis() => Ui.Run(() =>
    {
        using var stand = Make();

        stand.Row.Open([stand.Item("a", "Radio", duration: null), stand.Item("b", "Muzyka", "Kraków", "3:41")], 0);
        Assert.Equal("Radio", stand.Row.Caption.Text);

        stand.Row.MoveFocusBy(1);
        Assert.Equal("Muzyka · Kraków · 3:41", stand.Row.Caption.Text);
        Assert.Equal("Muzyka, Kraków, 3:41", AutomationProperties.GetName(stand.Row.Tiles.Single(tile => tile.Index == 1)));
    });

    [Fact]
    public void BezBiezacegoFokusStajeNaPierwszymINicNieJestZagrane() => Ui.Run(() =>
    {
        using var stand = Make();

        stand.Row.Open(stand.Items(4), -1);

        Assert.Equal(0, stand.Row.FocusIndex);
        Assert.DoesNotContain(stand.Row.Tiles, tile => tile.Classes.Contains("played") || tile.Dot.IsVisible);
    });

    [Fact]
    public void FokusZostajeNaTymSamymElemencieAGdyZniknalNaTymSamymMiejscu() => Ui.Run(() =>
    {
        using var stand = Make();
        var items = stand.Items(5);
        stand.Row.Open(items, 0);
        stand.Row.MoveFocusTo(3);

        stand.Row.SetItems(items.AsEnumerable().Reverse().ToList(), 4);
        Assert.Equal("s3", stand.Row.FocusedItem!.Id);
        Assert.Equal(1, stand.Row.FocusIndex);

        stand.Row.SetItems(items.Where(item => (string)item.Id != "s3").ToList(), 0);
        Assert.Equal(1, stand.Row.FocusIndex);
        Assert.Equal("s1", stand.Row.FocusedItem!.Id);

        stand.Row.MoveFocusTo(3);
        stand.Row.SetItems(items.Take(2).ToList(), 0);
        Assert.Equal(1, stand.Row.FocusIndex);

        stand.Row.SetItems([], -1);
        Assert.Null(stand.Row.FocusedItem);
        Assert.Empty(stand.Row.Tiles);
    });

    [Fact]
    public void ElementZTymSamymIdZachowujeOkladkeIObraz() => Ui.Run(() =>
    {
        using var stand = Make();
        var items = stand.Items(3);
        stand.Row.Open(items, 0);
        Settle(stand.Row);
        var tile = stand.Row.Tiles.Single(t => t.Index == 1);
        var image = tile.Picture.Source;
        var loads = Volatile.Read(ref stand.Loads);

        stand.Row.SetItems(items.AsEnumerable().Reverse().ToList(), 2);
        Settle(stand.Row);

        Assert.Same(tile, stand.Row.Tiles.Single(t => t.Index == 1));
        Assert.Same(image, tile.Picture.Source);
        Assert.Equal(loads, Volatile.Read(ref stand.Loads));
    });

    [Fact]
    public void IstniejaTylkoWidoczneIPoKilkaZKazdejStrony() => Ui.Run(() =>
    {
        using var stand = Make();

        stand.Row.Open(stand.Items(5000), 2500);
        Dispatcher.UIThread.RunJobs();

        var pitch = stand.Row.TileSize + stand.Row.TileSpacing;
        var visible = (int)Math.Ceiling(stand.Row.Width / pitch) + 1;
        Assert.InRange(stand.Row.Tiles.Count, visible, visible + 2 * CoverRow.Overscan);
        Assert.Contains(stand.Row.Tiles, tile => tile.Index == 2500);

        Settle(stand.Row);
        Assert.Equal(stand.Row.Tiles.Count, Volatile.Read(ref stand.Loads));
    });

    /// <summary>
    /// Odczyty czekają na zwolnienie, więc test widzi kolejność przydziału, a nie kolejność, w jakiej
    /// wątki puli zdążą się zgłosić: wątek wywłaszczony między przydziałem a zgłoszeniem zgłosiłby się po
    /// odczytach przydzielonych później.
    /// </summary>
    [Fact]
    public void RzadCzytaOdNajblizszychFokusowi() => Ui.Run(() =>
    {
        using var stand = Make();
        using var gate = new SemaphoreSlim(0);
        var order = new List<int>();
        var items = Enumerable.Range(0, 30).Select(i => new CoverRowItem("x" + i, "x", "", null, () =>
        {
            lock (order) order.Add(i);
            gate.Wait(TimeSpan.FromSeconds(10));
            return null;
        })).ToList();

        int[] Started()
        {
            lock (order) return [.. order];
        }

        try
        {
            stand.Row.Open(items, 15);

            // Czytane są po dwa naraz: najpierw fokus i jeden sąsiad, a trzeci dopiero po zwolnieniu miejsca.
            Assert.True(Ui.WaitUntil(() => Started().Length >= 2), "nie zaczęły się dwa odczyty");
            Dispatcher.UIThread.RunJobs();
            var first = Started();
            Assert.Equal(2, first.Length);
            Assert.Contains(15, first);
            Assert.All(first, index => Assert.InRange(index, 14, 16));

            gate.Release();
            Assert.True(Ui.WaitUntil(() => Started().Length >= 3), "po zwolnieniu miejsca nie zaczął się trzeci odczyt");
            Assert.Equal([14, 15, 16], Started().Take(3).Order());
        }
        finally
        {
            gate.Release(items.Count);
        }

        Settle(stand.Row);
    });

    [Fact]
    public void BladOdczytuPokazujeObrazZastepczyINieZatrzymujeReszty() => Ui.Run(() =>
    {
        using var stand = Make();
        var created = 0;
        stand.Row.PlaceholderFactory = () =>
        {
            created++;
            return MainViewModel.DecodeCover(CoverTests.Png(8, 8, Colors.Green))!.Display;
        };

        stand.Row.Open(
        [
            new CoverRowItem("zly", "Zły", "", null, () => throw new IOException("brak")),
            new CoverRowItem("pusty", "Pusty", "", null, () => null),
            stand.Item("dobry"),
        ], 0);
        Settle(stand.Row);

        var bad = stand.Row.Tiles.Single(tile => tile.Index == 0).Picture.Source;
        Assert.NotNull(bad);
        Assert.Same(bad, stand.Row.Tiles.Single(tile => tile.Index == 1).Picture.Source);
        Assert.NotSame(bad, stand.Row.Tiles.Single(tile => tile.Index == 2).Picture.Source);
        Assert.Equal(1, created);

        var placeholder = (Bitmap)bad;
        stand.Row.Close();
        Assert.Throws<ObjectDisposedException>(() => placeholder.PixelSize);
    });

    [Fact]
    public void ZamknietyRzadIgnorujeZmianyIDaSieOtworzycPonownie() => Ui.Run(() =>
    {
        using var stand = Make();
        stand.Row.Open(stand.Items(3), 0);
        stand.Row.Close();

        stand.Row.SetItems(stand.Items(9), 0);
        stand.Row.MoveFocusBy(1);
        Assert.Empty(stand.Row.Tiles);
        Assert.False(stand.Row.IsVisible);

        stand.Row.Open(stand.Items(2), 1);
        Assert.Equal(1, stand.Row.FocusIndex);
        Assert.True(stand.Row.IsVisible);
        Assert.NotEmpty(stand.Row.GetVisualDescendants().OfType<CoverRowTile>());
    });
}
