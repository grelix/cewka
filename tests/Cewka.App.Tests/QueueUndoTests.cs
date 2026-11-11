using Cewka.App.Localisation;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Cofanie jednej zmiany kolejki: usunięcia, wyczyszczenia i zastąpienia listą.</summary>
[Collection(UiCollection.Name)]
public sealed class QueueUndoTests
{
    private static string[] Paths(PlayerHarness h) => h.Player.Queue.Select(row => row.Path).ToArray();

    [Fact]
    public void CofniecieUsunieciaWstawiaWierszNaDawneMiejsce() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
        var before = Paths(h);

        h.Player.RemoveFromQueue(h.Player.Queue[1]);
        Assert.True(h.Player.CanUndoQueue);
        Assert.Equal(Strings.Current["QueueUndoRemove"], h.Player.UndoQueueLabel);

        h.Player.UndoQueue();

        Assert.Equal(before, Paths(h));
        Assert.Equal([1, 2, 3], h.Player.Queue.Select(row => row.Number));
        Assert.False(h.Player.CanUndoQueue);
        Assert.True(h.Player.IsPlaying);
        Assert.Equal(0, h.Player.CurrentIndex);
    });

    /// <summary>
    /// Tagi odczytane w tle, gdy usuniętego wiersza nie było w kolejce, trafiają na niego po
    /// cofnięciu: wraca ten sam wiersz, a odczyt nie miał go wtedy gdzie wpisać.
    /// </summary>
    [Fact]
    public void CofniecieUsunieciaPrzynosiTagiOdczytaneWCzasieUsuniecia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var b = h.Track("b");

        // Odczyt tagów w tle czeka przy wierszu „b”, aż wiersz zniknie z kolejki. Bez tego szybki
        // odczyt zdąża przed usunięciem i test nie sprawdza tego, co obiecuje.
        using var removed = new ManualResetEventSlim();
        h.Player.BeforeBackgroundMetadataRead = entry =>
        {
            if (entry.Path == b) removed.Wait(TimeSpan.FromSeconds(10));
        };

        h.OpenPaths([h.Track("a"), b, h.Track("c")]);
        var row = h.Player.Queue[1];
        var entry = row.Entry!;
        Assert.Equal(0, row.DurationSeconds);

        h.Player.RemoveFromQueue(row);
        removed.Set();
        Assert.True(h.PumpUntil(() => entry.Metadata is not null));
        h.PumpUntil(() => false, 200);
        Assert.Equal(0, row.DurationSeconds);

        h.Player.UndoQueue();

        // Czas z tagów pliku testowego to ułamek sekundy, ale nie zero jak przed odczytem.
        Assert.Same(row, h.Player.Queue[1]);
        Assert.True(row.DurationSeconds > 0);
    });

    [Fact]
    public void CofniecieUsunieciaGrajacegoCzekaNaOdtworzenieTegoUtworu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        h.Player.PlayItem(h.Player.Queue[1]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));

        h.Player.RemoveFromQueue(h.Player.Queue[1]);
        h.Player.UndoQueue();

        Assert.False(h.Player.IsPlaying);
        h.Player.TogglePlay();
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.EndsWith("b.wav", h.Engine.Queue[1].Path);
    });

    [Fact]
    public void CofniecieWyczyszczeniaPrzywracaKolejkeIMiejsce() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        h.Player.PlayItem(h.Player.Queue[2]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 2));
        var before = Paths(h);

        h.Player.ClearQueue();
        Assert.Empty(h.Player.Queue);
        Assert.Equal(Strings.Current["QueueUndoClear"], h.Player.UndoQueueLabel);

        h.Player.UndoQueue();

        Assert.Equal(before, Paths(h));
        Assert.False(h.Player.IsPlaying);
        h.Player.TogglePlay();
        Assert.Equal(2, h.Engine.CurrentIndex);
    });

    [Fact]
    public void CofniecieWczytaniaListyPrzywracaPoprzedniaKolejke() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b")]);
        var before = Paths(h);

        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{h.Track("x")}\n");
        h.LoadPlaylist(list);
        Assert.Single(h.Player.Queue);
        Assert.Equal(Strings.Current["QueueUndoReplace"], h.Player.UndoQueueLabel);

        h.Player.UndoQueue();

        Assert.Equal(before, Paths(h));
    });

    [Fact]
    public void WczytanieListyDoPustejKolejkiNieDajeCofniecia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{h.Track("x")}\n");

        h.LoadPlaylist(list);

        Assert.False(h.Player.CanUndoQueue);
    });

    [Fact]
    public void KolejnaZmianaKolejkiUniewaznaCofniecie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);

        h.Player.RemoveFromQueue(h.Player.Queue[0]);
        h.Player.MoveInQueue(0, 1);
        Assert.False(h.Player.CanUndoQueue);

        h.Player.RemoveFromQueue(h.Player.Queue[0]);
        h.AddPaths([h.Track("d")]);
        Assert.False(h.Player.CanUndoQueue);

        var before = Paths(h);
        h.Player.UndoQueue();
        Assert.Equal(before, Paths(h));
    });

    [Fact]
    public void PodpowiedziPrzyciskowZaleznaOdStanu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Assert.Equal(Strings.Current["TooltipPlay"], h.Player.PlayPauseTooltip);
        Assert.Equal(Strings.Current["TooltipMute"], h.Player.MuteTooltip);

        var raised = new List<string?>();
        h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying));
        Assert.Equal(Strings.Current["TooltipPause"], h.Player.PlayPauseTooltip);
        Assert.Contains("PlayPauseTooltip", raised);

        h.Player.ToggleMute();
        Assert.Equal(Strings.Current["TooltipUnmute"], h.Player.MuteTooltip);
        Assert.Contains("MuteTooltip", raised);
    });
}
