using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Zapis kolejki w trakcie grania: lista ścieżek trafia na dysk tylko wtedy, gdy się zmieniła,
/// a miejsce odtwarzania do małego pliku obok. Zapis przy zamknięciu jest zawsze pełny, więc
/// plik pozycji liczy się tylko po nagłym końcu programu.
/// </summary>
public sealed class QueuePositionFileTests
{
    private static string NewQueueFile() => Path.Combine(TestEnvironment.NewDirectory(), "queue.json");

    private static QueueState State(int index, double seconds, params string[] paths) => new()
    {
        Paths = [.. paths],
        CurrentIndex = index,
        PositionSeconds = seconds,
        Repeat = "None",
    };

    private static readonly string[] Three = ["a.flac", "b.flac", "c.flac"];

    [Fact]
    public async Task ZapisWTleBezZmianyListyNieRuszaPlikuKolejki()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);

        await writer.SaveInBackground(State(0, 5, Three));
        var before = File.ReadAllBytes(path);

        await writer.SaveInBackground(State(1, 42.5, Three));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(File.Exists(QueueStateStore.PositionFile(path)));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(Three, loaded.Paths);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(42.5, loaded.PositionSeconds);
    }

    [Fact]
    public async Task ZmianaListyZapisujeCalyPlik()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);

        await writer.SaveInBackground(State(0, 5, Three));
        await writer.SaveInBackground(State(1, 30, Three));
        await writer.SaveInBackground(State(0, 7, "a.flac", "c.flac"));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(["a.flac", "c.flac"], loaded.Paths);
        Assert.Equal(0, loaded.CurrentIndex);
        Assert.Equal(7, loaded.PositionSeconds);
    }

    /// <summary>Tryby odtwarzania należą do listy: ich zmiana też musi trafić do pliku kolejki.</summary>
    [Fact]
    public async Task ZmianaLosowosciAlboPowtarzaniaZapisujeCalyPlik()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);
        await writer.SaveInBackground(State(0, 5, Three));

        var shuffled = State(0, 6, Three);
        shuffled.Shuffle = true;
        await writer.SaveInBackground(shuffled);
        Assert.True(QueueStateStore.Load(path)!.Shuffle);

        var repeating = State(0, 7, Three);
        repeating.Shuffle = true;
        repeating.Repeat = "Queue";
        await writer.SaveInBackground(repeating);
        Assert.Equal("Queue", QueueStateStore.Load(path)!.Repeat);

        var pending = State(0, 8, Three);
        pending.Shuffle = true;
        pending.Repeat = "Queue";
        pending.PendingOpens = [new PendingOpenState { Paths = ["d.flac"], Action = "Append" }];
        await writer.SaveInBackground(pending);
        Assert.Equal(["d.flac"], QueueStateStore.Load(path)!.PendingOpens!.Single().Paths);
    }

    /// <summary>Po zwykłym zamknięciu plik kolejki jest kompletny także bez pliku pozycji.</summary>
    [Fact]
    public async Task ZapisPrzyZamknieciuJestPelny()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);

        await writer.SaveInBackground(State(0, 5, Three));
        await writer.SaveInBackground(State(1, 30, Three));
        writer.SaveNow(State(2, 99, Three));

        File.Delete(QueueStateStore.PositionFile(path));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(2, loaded.CurrentIndex);
        Assert.Equal(99, loaded.PositionSeconds);
    }

    /// <summary>Plik pozycji z poprzedniej listy nie może przestawić nowej.</summary>
    [Fact]
    public async Task PozycjaZInnejListyJestPomijana()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);
        await writer.SaveInBackground(State(0, 5, Three));
        await writer.SaveInBackground(State(2, 120, Three));
        var stale = File.ReadAllBytes(QueueStateStore.PositionFile(path));

        new QueueStateWriter(path).SaveNow(State(0, 3, "x.flac", "y.flac", "z.flac"));
        File.WriteAllBytes(QueueStateStore.PositionFile(path), stale);

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(0, loaded.CurrentIndex);
        Assert.Equal(3, loaded.PositionSeconds);
    }

    [Theory]
    [InlineData("to nie jest JSON")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"revision":"x"}""")]
    public async Task UszkodzonyPlikPozycjiJestPomijany(string content)
    {
        var path = NewQueueFile();
        await new QueueStateWriter(path).SaveInBackground(State(1, 12, Three));
        File.WriteAllText(QueueStateStore.PositionFile(path), content);

        var loaded = QueueStateStore.Load(path, out var unreadable)!;

        Assert.False(unreadable);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(12, loaded.PositionSeconds);
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!), file => file.Contains(".bad-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, 10.0, 1, 12.0)]
    [InlineData(-2, 10.0, 1, 12.0)]
    [InlineData(2, -5.0, 2, 0.0)]
    [InlineData(2, 1e300, 2, 0.0)]
    [InlineData(-1, 10.0, -1, 10.0)]
    public async Task PozycjaSpozaZakresuJestOdrzucanaAlboPrzycinana(int index, double seconds, int expectedIndex, double expectedSeconds)
    {
        var path = NewQueueFile();
        await new QueueStateWriter(path).SaveInBackground(State(1, 12, Three));
        var revision = QueueStateStore.Load(path)!.Revision;
        File.WriteAllText(
            QueueStateStore.PositionFile(path),
            FormattableString.Invariant($$"""{"revision":{{revision}},"currentIndex":{{index}},"positionSeconds":{{seconds}}}"""));

        var loaded = QueueStateStore.Load(path)!;

        Assert.Equal(expectedIndex, loaded.CurrentIndex);
        Assert.Equal(expectedSeconds, loaded.PositionSeconds);
    }

    /// <summary>Plik kolejki sprzed tej zmiany nie ma numeru zapisu i żaden plik pozycji do niego nie pasuje.</summary>
    [Fact]
    public void PlikKolejkiBezNumeruZapisuNiePrzyjmujePozycji()
    {
        var path = NewQueueFile();
        File.WriteAllText(path, """{"paths":["a.flac","b.flac"],"currentIndex":0,"positionSeconds":4,"shuffle":false,"repeat":"None"}""");
        File.WriteAllText(QueueStateStore.PositionFile(path), """{"revision":0,"currentIndex":1,"positionSeconds":50}""");

        var loaded = QueueStateStore.Load(path)!;

        Assert.Equal(0, loaded.CurrentIndex);
        Assert.Equal(4, loaded.PositionSeconds);
    }

    /// <summary>Lista, której nie udało się zapisać, nie uchodzi za zapisaną: następny zapis znów jest pełny.</summary>
    [Fact]
    public async Task NieudanyZapisListyJestPonawianyWCalosci()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);

        Directory.CreateDirectory(path);
        await writer.SaveInBackground(State(0, 5, Three));
        Directory.Delete(path);

        await writer.SaveInBackground(State(1, 9, Three));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(Three, loaded.Paths);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(9, loaded.PositionSeconds);
    }

    /// <summary>Plik, którego przy starcie nie dało się przeczytać, zostaje nietknięty razem z plikiem pozycji.</summary>
    [Fact]
    public async Task ZachowanyPlikNieDostajePlikuPozycji()
    {
        var path = NewQueueFile();
        File.WriteAllText(path, "zawartość, której nie wolno nadpisać");
        var writer = new QueueStateWriter(path);
        writer.KeepExistingFile();

        await writer.SaveInBackground(State(0, 5, Three));
        writer.SaveNow(State(1, 6, Three));

        Assert.Equal("zawartość, której nie wolno nadpisać", File.ReadAllText(path));
        Assert.False(File.Exists(QueueStateStore.PositionFile(path)));
    }

    /// <summary>
    /// Dwie kopie programu (wyłączona jedna kopia) piszą te same pliki. Plik pozycji jednej nie
    /// może unieważnić stanu drugiej: kopia, która zastaje cudzy plik kolejki, zapisuje swój stan
    /// w całości, więc na dysku zawsze leży spójny stan tej, która pisała ostatnia.
    /// </summary>
    [Fact]
    public async Task DwieKopieZostawiajaSpojnyStanOstatniejPiszacej()
    {
        var path = NewQueueFile();
        var first = new QueueStateWriter(path);
        var second = new QueueStateWriter(path);
        string[] other = ["x.flac", "y.flac"];

        await first.SaveInBackground(State(0, 5, Three));
        await second.SaveInBackground(State(0, 7, other));
        await second.SaveInBackground(State(1, 60, other));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(other, loaded.Paths);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(60, loaded.PositionSeconds);

        await first.SaveInBackground(State(2, 90, Three));

        loaded = QueueStateStore.Load(path)!;
        Assert.Equal(Three, loaded.Paths);
        Assert.Equal(2, loaded.CurrentIndex);
        Assert.Equal(90, loaded.PositionSeconds);

        await second.SaveInBackground(State(1, 75, other));

        loaded = QueueStateStore.Load(path)!;
        Assert.Equal(other, loaded.Paths);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(75, loaded.PositionSeconds);
    }

    /// <summary>Plik kolejki usunięty w trakcie sesji wraca przy najbliższym zapisie w tle.</summary>
    [Fact]
    public async Task UsunietyPlikKolejkiWracaPrzyZapisieWTle()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);
        await writer.SaveInBackground(State(0, 5, Three));

        File.Delete(path);
        await writer.SaveInBackground(State(1, 20, Three));

        var loaded = QueueStateStore.Load(path)!;
        Assert.Equal(Three, loaded.Paths);
        Assert.Equal(1, loaded.CurrentIndex);
        Assert.Equal(20, loaded.PositionSeconds);
    }

    /// <summary>Numer zapisu czytany z początku pliku zgadza się z numerem z pełnego odczytu.</summary>
    [Fact]
    public async Task NumerZapisuZPoczatkuPlikuZgadzaSieZPelnymOdczytem()
    {
        var path = NewQueueFile();
        Assert.Equal(0, QueueStateStore.ReadRevision(path));

        await new QueueStateWriter(path).SaveInBackground(State(0, 5, Three));

        var revision = QueueStateStore.Load(path)!.Revision;
        Assert.NotEqual(0, revision);
        Assert.Equal(revision, QueueStateStore.ReadRevision(path));

        File.WriteAllText(path, """{"paths":["a.flac"],"currentIndex":0,"positionSeconds":4,"revision":12345}""");
        Assert.Equal(0, QueueStateStore.ReadRevision(path));

        File.WriteAllText(path, """{"revision":123""");
        Assert.Equal(0, QueueStateStore.ReadRevision(path));
    }

    /// <summary>Duża kolejka: kolejne zapisy pozycji nie rosną z liczbą utworów.</summary>
    [Fact]
    public async Task ZapisPozycjiPrzyDuzejKolejceJestMaly()
    {
        var path = NewQueueFile();
        var writer = new QueueStateWriter(path);
        var paths = Enumerable.Range(0, 20_000).Select(i => $@"D:\Muzyka\Album {i / 12:0000}\{i % 12 + 1:00} - Utwór {i}.flac").ToArray();

        await writer.SaveInBackground(State(0, 5, paths));
        var listWritten = File.GetLastWriteTimeUtc(path);
        var listSize = new FileInfo(path).Length;

        for (var i = 1; i <= 5; i++) await writer.SaveInBackground(State(i, i * 30, paths));

        Assert.Equal(listWritten, File.GetLastWriteTimeUtc(path));
        Assert.Equal(listSize, new FileInfo(path).Length);
        Assert.True(new FileInfo(QueueStateStore.PositionFile(path)).Length < 200);
        Assert.Equal(5, QueueStateStore.Load(path)!.CurrentIndex);
    }
}

/// <summary>Ta sama droga przez program: zapis w tle w trakcie grania, nagły koniec, następne uruchomienie.</summary>
[Collection(UiCollection.Name)]
public sealed class QueuePositionSessionTests
{
    [Fact]
    public void PoNaglymKoncuKolejkaWznawiaOdUtworuZPlikuPozycji() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        string[] tracks;
        byte[] list;

        using (var h = new PlayerHarness(directory: directory))
        {
            tracks = [h.Track("a"), h.Track("b"), h.Track("c")];
            h.OpenPaths(tracks);
            Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
            h.Player.SaveQueueStateInBackground().Wait(5000);
            list = File.ReadAllBytes(h.QueueFile);

            h.Player.PlayItem(h.Player.Queue[2]);
            Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 2));
            h.Player.SaveQueueStateInBackground().Wait(5000);

            // Bez zapisu przy zamknięciu: tak kończy program zabity przez system.
            Assert.Equal(list, File.ReadAllBytes(h.QueueFile));
        }

        Assert.Equal(list, File.ReadAllBytes(Path.Combine(directory, "queue.json")));
        Assert.Equal(2, QueueStateStore.Load(Path.Combine(directory, "queue.json"))!.CurrentIndex);

        using var again = new PlayerHarness(directory: directory);
        again.RestoreQueueState();
        again.Player.TogglePlay();

        Assert.True(again.PumpUntil(() => again.Engine.CurrentIndex == 2));
    });

    /// <summary>Zwykłe zamknięcie zostawia plik kolejki kompletny: plik pozycji nie jest do niczego potrzebny.</summary>
    [Fact]
    public void PoZwyklymZamknieciuPlikKolejkiWystarczaSam() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();

        using (var h = new PlayerHarness(directory: directory))
        {
            h.OpenPaths([h.Track("a"), h.Track("b")]);
            Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
            h.Player.SaveQueueStateInBackground().Wait(5000);
            h.Player.PlayItem(h.Player.Queue[1]);
            Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));
            h.Player.SaveQueueStateInBackground().Wait(5000);
            h.Player.SaveQueueState();
        }

        var file = Path.Combine(directory, "queue.json");
        File.Delete(QueueStateStore.PositionFile(file));

        Assert.Equal(1, QueueStateStore.Load(file)!.CurrentIndex);
    });
}
