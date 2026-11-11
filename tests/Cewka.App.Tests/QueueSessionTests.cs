using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
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
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
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
        h.OpenPaths([h.Track("a")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));

        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{h.Track("b")}\n{h.Track("c")}\n");
        h.LoadPlaylist(list);

        Assert.Equal(Idle, h.Player.Title);
        Assert.Equal(-1, h.Player.CurrentIndex);
    });

    [Fact]
    public void ZaleglaZmianaUtworuPoUsunieciuGrajacegoNieOznaczaInnegoWiersza() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));

        QueueTrackChangeWithoutPumping(h, () => h.Player.MoveInQueue(2, 1));
        h.Player.RemoveFromQueue(h.Player.Queue[0]);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        AssertNothingCurrent(h);
    });

    [Fact]
    public void ZaleglaZmianaUtworuPoWczytaniuListyNieOznaczaWiersza() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a"), h.Track("b"), h.Track("c")]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));

        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{h.Track("d")}\n{h.Track("e")}\n");

        // Zastąpienie wprost: czekanie na wczytanie w tle obsłużyłoby zaległą zmianę przed nim.
        QueueTrackChangeWithoutPumping(h, () => h.Player.MoveInQueue(2, 1));
        h.Player.ReplaceWithPlaylist(PlaylistFile.Load(list));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        AssertNothingCurrent(h);
    });

    /// <summary>
    /// Wykonuje zmianę kolejki i czeka, aż zgłoszenie silnika trafi do dyspozytora, ale go nie
    /// obsługuje. Własny odbiorca jest dopisany po modelu widoku, więc jego sygnał oznacza, że
    /// zadanie modelu już czeka w kolejce.
    /// </summary>
    private static void QueueTrackChangeWithoutPumping(PlayerHarness h, Action change)
    {
        // Bez zwalniania: spóźnione zgłoszenie z puli wątków może jeszcze wywołać Set.
        var raised = new ManualResetEventSlim();
        Action<int> signal = _ => raised.Set();
        h.Engine.TrackChanged += signal;

        try
        {
            change();
            Assert.True(raised.Wait(5000));
        }
        finally
        {
            h.Engine.TrackChanged -= signal;
        }
    }

    private static void AssertNothingCurrent(PlayerHarness h)
    {
        Assert.Equal(-1, h.Engine.CurrentIndex);
        Assert.Equal(-1, h.Player.CurrentIndex);
        Assert.DoesNotContain(h.Player.Queue, row => row.IsCurrent);
        Assert.Equal(Idle, h.Player.Title);
        Assert.Equal(string.Empty, h.Player.FormatBadge);
    }

    [Fact]
    public void ZapisBezOdtwarzaniaZachowujePrzywroconaPozycje() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b"), h.Track("c")], 2, 42);

        h.RestoreQueueState();
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

        h.RestoreQueueState();

        Assert.Equal(Idle, h.Player.Title);
    });

    [Fact]
    public void BrakujacyPlikPrzedBiezacymPrzesuwaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [Path.Combine(h.Directory, "nie-ma.wav"), h.Track("b"), h.Track("c")], 2, 10);

        h.RestoreQueueState();
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

        h.RestoreQueueState();
        h.Player.SaveQueueState();

        Assert.Equal(-1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);
    });

    [Fact]
    public void WznowienieZaczynaOdZapisanejPozycji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.TrackWithTaggedLength("a"), h.TrackWithTaggedLength("b")], 1, 30);

        h.RestoreQueueState();

        // Długość z tagów decyduje, czy pozycja mieści się w utworze; bez czekania wynik
        // zależałby od tego, czy odczyt w tle zdążył.
        Assert.True(h.PumpUntil(() => h.Engine.Queue.All(entry => entry.Duration > TimeSpan.Zero)));

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
        h.RestoreQueueState();

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
        h.RestoreQueueState();

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
        h.RestoreQueueState();

        h.Player.RemoveFromQueue(h.Player.Queue[1]);
        h.Player.SaveQueueState();

        Assert.Equal(-1, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);
    });

    [Fact]
    public void WyczyszczenieKolejkiPorzucaWznowienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [h.Track("a"), h.Track("b")], 1, 10);
        h.RestoreQueueState();

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

        h.RestoreQueueState();

        Assert.Empty(h.Player.Queue);
    });

    [Fact]
    public void ZmianaKolejkiZamawiaZapisBezZamykaniaOkna() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Assert.False(h.Player.QueueSavePending);

        h.OpenPaths([h.Track("a"), h.Track("b")], replace: false);
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
        h.OpenPaths([h.Track("a")], replace: false);

        var entry = Assert.Single(h.Player.QueueAsPlaylist());
        Assert.Null(entry.Artist);
    });

    /// <summary>
    /// Otwarcie niedokończone przy zamknięciu to jawne żądanie, a nie poprzednia sesja: dokańcza się
    /// także przy wyłączonym przywracaniu, choć kolejka z poprzedniej sesji nie wraca.
    /// </summary>
    [Fact]
    public void OczekujaceOtwarcieDokanczaSieMimoWylaczonegoPrzywracania() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(settings => settings.RestoreSession = false);
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        var inFolder = h.Track(Path.Combine("album", "b"));

        QueueStateStore.Save(new QueueState
        {
            Paths = [h.Track("a")],
            CurrentIndex = 0,
            PendingOpens = [new PendingOpenState { Paths = [folder], Action = "Add" }],
        }, h.QueueFile);

        h.RestoreQueueState();

        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 1));
        Assert.Equal([inFolder], h.Engine.QueuePaths.ToArray());
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);
    });

    /// <summary>
    /// Folder przeglądany w tle w chwili zapisu trafia do otwarć oczekujących, żeby nie przepadł
    /// przy nagłym końcu programu, a po dołożeniu znika z nich przy najbliższym zapisie.
    /// </summary>
    [Fact]
    public void FolderPrzegladanyWTleJestWZapisieDoChwiliDolozenia() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        using var h = new PlayerHarness();
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        var inFolder = h.Track(Path.Combine("album", "b"));

        h.PathChecking = path =>
        {
            if (path == folder) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            _ = h.Player.AddPaths([folder]);
            h.Player.SaveQueueState();

            var during = QueueStateStore.Load(h.QueueFile)!;
            Assert.Empty(during.Paths);
            var open = Assert.Single(during.PendingOpens ?? []);
            Assert.Equal([folder], open.Paths.ToArray());
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 1));
        h.Player.SaveQueueState();

        var after = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([inFolder], after.Paths.ToArray());
        Assert.Null(after.PendingOpens);
    });

    /// <summary>
    /// Otwarcie dokańczane po starcie, gdy użytkownik zdążył już włączyć przywróconą kolejkę, tylko
    /// dopisuje pliki: nie zatrzymuje grania, nie zastępuje kolejki i nie przestawia bieżącego utworu.
    /// </summary>
    [Theory]
    [InlineData("Replace", FileOpenAction.Append)]
    [InlineData("Outside", FileOpenAction.AppendAndPlay)]
    [InlineData("Outside", FileOpenAction.ReplaceAndPlay)]
    public void DokanczaneOtwarcieNieRuszaGranejKolejki(string savedAction, FileOpenAction setting) => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        using var h = new PlayerHarness(settings => settings.FileOpenAction = setting);
        var playing = h.Track("a");
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        var inFolder = h.Track(Path.Combine("album", "b"));

        QueueStateStore.Save(new QueueState
        {
            Paths = [playing],
            CurrentIndex = 0,
            PendingOpens = [new PendingOpenState { Paths = [folder], Action = savedAction }],
        }, h.QueueFile);

        h.PathChecking = path =>
        {
            if (path == folder) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            _ = h.Player.RestoreQueueState();
            Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 1));
            h.Player.TogglePlay();
            Assert.True(h.Player.IsPlaying);
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 2));
        Assert.True(h.Player.IsPlaying);
        Assert.Equal([playing, inFolder], h.Engine.QueuePaths.ToArray());
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Player.SaveQueueState();
        Assert.Equal(0, QueueStateStore.Load(h.QueueFile)!.CurrentIndex);
    });

    private static void Save(PlayerHarness h, List<string> paths, int current, double seconds) =>
        QueueStateStore.Save(new QueueState { Paths = paths, CurrentIndex = current, PositionSeconds = seconds }, h.QueueFile);
}
