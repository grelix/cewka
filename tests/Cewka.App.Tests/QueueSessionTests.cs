using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Bieżący utwór i zapamiętana pozycja przy edycji kolejki, wczytaniu listy i przywróceniu sesji.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class QueueSessionTests
{
    private static string Idle => Strings.Current["NothingPlaying"];

    [Fact]
    public void UsuniecieGrajacegoUtworuCzysciTytulIZaczynaOdNastepnego() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        h.Player.PlayItem(h.Player.Queue[1]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));

        h.Player.RemoveFromQueue(h.Player.Queue[1]);

        Assert.Equal(Idle, h.Player.Title);
        Assert.Equal(string.Empty, h.Player.ArtistLine);
        Assert.Equal(-1, h.Player.CurrentIndex);
        Assert.False(h.Player.IsPlaying);

        h.Player.TogglePlay();
        Assert.Equal(1, h.Engine.CurrentIndex);
    });

    [Fact]
    public void WczytanieListyCzysciTytulPoprzedniegoUtworu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));

        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{h.Track("b")}\n{h.Track("c")}\n");
        h.Player.LoadPlaylist(list);

        Assert.Equal(Idle, h.Player.Title);
        Assert.Equal(-1, h.Player.CurrentIndex);
    });

    [Fact]
    public void ZapisBezOdtwarzaniaZachowujePrzywroconaPozycje() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b"), h.Track("c")], 2, 42);

        h.Player.RestoreQueueState();
        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(2, saved.CurrentIndex);
        Assert.Equal(42, saved.PositionSeconds);
    });

    [Fact]
    public void PrzywrocenieBezUtworuPokazujeStanBezczynny() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b")], 1, 5);

        h.Player.RestoreQueueState();

        Assert.Equal(Idle, h.Player.Title);
    });

    [Fact]
    public void BrakujacyPlikPrzedBiezacymPrzesuwaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [Path.Combine(h.Directory, "nie-ma.wav"), h.Track("b"), h.Track("c")], 2, 10);

        h.Player.RestoreQueueState();
        h.Player.SaveQueueState();

        Assert.Equal(1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);

        h.Player.TogglePlay();
        Assert.Equal(1, h.Engine.CurrentIndex);
    });

    [Fact]
    public void BrakujacyBiezacyPlikPorzucaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), Path.Combine(h.Directory, "nie-ma.wav"), h.Track("c")], 1, 10);

        h.Player.RestoreQueueState();
        h.Player.SaveQueueState();

        Assert.Equal(-1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);
    });

    [Fact]
    public void WznowienieZaczynaOdZapisanejPozycji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b")], 1, 30);

        h.Player.RestoreQueueState();
        h.Player.TogglePlay();

        Assert.True(h.Player.IsPlaying);
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.CurrentIndex == 1));
        Assert.True(h.Engine.Position.TotalSeconds >= 29, $"pozycja {h.Engine.Position}");
    });

    [Fact]
    public void NieudaneOtwarcieUrzadzeniaNieZuzywaWznowienia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.TrackWithTaggedLength("a"), h.TrackWithTaggedLength("b")], 1, 30);
        h.Player.RestoreQueueState();

        // Długość z tagów decyduje, czy pozycja mieści się w utworze; bez czekania wynik
        // zależałby od tego, czy odczyt w tle zdążył.
        Assert.True(h.PumpUntil(() => h.Engine.Queue.All(entry => entry.Duration > TimeSpan.Zero)));

        h.FailAll = true;
        h.Player.TogglePlay();
        Assert.False(h.Player.IsPlaying);

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(30, saved.PositionSeconds);

        h.FailAll = false;
        h.Player.RetryAudio();
        Assert.True(h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds >= 29),
            $"wpis {h.Engine.CurrentIndex}, pozycja {h.Engine.Position}");
    });

    [Fact]
    public void EdycjaKolejkiPrzesuwaUzbrojoneWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var c = h.Track("c");
        Save(h, [h.Track("a"), h.Track("b"), c], 2, 10);
        h.Player.RestoreQueueState();

        h.Player.RemoveFromQueue(h.Player.Queue[0]);
        h.Player.SaveQueueState();
        Assert.Equal(1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);

        h.Player.MoveInQueue(1, 0);
        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(0, saved.CurrentIndex);
        Assert.Equal(c, saved.Paths[0]);
    });

    [Fact]
    public void UsuniecieUzbrojonegoUtworuPorzucaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b")], 1, 10);
        h.Player.RestoreQueueState();

        h.Player.RemoveFromQueue(h.Player.Queue[1]);
        h.Player.SaveQueueState();

        Assert.Equal(-1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);
    });

    [Fact]
    public void WyczyszczenieKolejkiPorzucaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b")], 1, 10);
        h.Player.RestoreQueueState();

        h.Player.ClearQueue();
        h.Player.TogglePlay();

        Assert.False(h.Player.IsPlaying);
        Assert.False(h.Engine.IsDeviceOpen);
    });

    [Fact]
    public void PlikKolejkiBezListySciezekNieWywracaStartu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        File.WriteAllText(h.QueueFile, """{"paths": null, "currentIndex": 0}""");

        h.Player.RestoreQueueState();

        Assert.Empty(h.Player.Queue);
    });

    [Fact]
    public void ZmianaKolejkiZamawiaZapisBezZamykaniaOkna() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Assert.False(h.Player.QueueSavePending);

        h.Player.OpenPaths([h.Track("a"), h.Track("b")], replace: false);
        Assert.True(h.Player.QueueSavePending);

        h.Player.SaveQueueState();
        Assert.False(h.Player.QueueSavePending);
        Assert.Equal(2, QueueStateStore.Load(h.QueueFile)!.Paths.Count);

        h.Player.CycleRepeat();
        Assert.True(h.Player.QueueSavePending);
    });

    [Fact]
    public void ListaZKolejkiNieWpisujeZastepczegoWykonawcy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")], replace: false);

        var entry = Assert.Single(h.Player.QueueAsPlaylist());
        Assert.Null(entry.Artist);
    });

    private static void Save(PlayerHarness h, List<string> paths, int current, double seconds) =>
        QueueStateStore.Save(new QueueState { Paths = paths, CurrentIndex = current, PositionSeconds = seconds }, h.QueueFile);
}
