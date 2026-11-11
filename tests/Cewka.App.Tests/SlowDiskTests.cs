using System.Collections.Concurrent;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Niedostępny dysk sieciowy: sprawdzenie ścieżki czeka kilkanaście sekund. Tu czeka na zwolnienie
/// bramki, a wątek interfejsu ma pracować dalej, bez zmiany kolejności wpisów i wznowienia.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SlowDiskTests
{
    /// <summary>Czekanie na koniec otwierania, jak w <see cref="PlayerHarness.OpenPaths"/>: pod obciążeniem pełnego zestawu sprawdzanie ścieżek w tle może trwać dłużej niż 5 s.</summary>
    private const int OpenTimeout = 20000;

    /// <summary>Górna granica czekania bramki; gdyby czekał wątek interfejsu, test skończy się porażką.</summary>
    private static readonly TimeSpan GateLimit = TimeSpan.FromSeconds(2);

    /// <summary>Bramka dłuższa niż termin zamykania z zapasem na wolną maszynę: otwarcie na pewno nie zdąży.</summary>
    private static readonly TimeSpan BeyondClosing = TimeSpan.FromSeconds(10);

    public static readonly TheoryData<string> OpenRoutes = ["OpenPaths", "AddPaths", "OpenFromOutside"];

    private static Task OpenBy(string route, PlayerHarness h, string path) => route switch
    {
        "OpenPaths" => h.Player.OpenPaths([path]),
        "AddPaths" => h.Player.AddPaths([path]),
        _ => h.Player.OpenFromOutside([path]),
    };

    /// <summary>Bramka na sprawdzanie ścieżek, zapamiętująca wątki, które na niej stanęły.</summary>
    private sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _open = new();
        private readonly ConcurrentBag<int> _threads = [];
        private readonly ConcurrentDictionary<string, bool> _seen = [];

        /// <param name="only">Jedyna zatrzymywana ścieżka; bez niej bramka zatrzymuje każdą.</param>
        public Gate(PlayerHarness h, TimeSpan? limit = null, string? only = null) => h.PathChecking = path =>
        {
            _seen[path] = true;
            if (only is not null && path != only) return;

            _threads.Add(Environment.CurrentManagedThreadId);
            _open.Wait(limit ?? GateLimit);
        };

        public void Open() => _open.Set();

        /// <summary>
        /// Czeka, aż ścieżka za bramką zostanie sprawdzona, i daje chwilę na obsłużenie wyniku.
        /// Otwarcie, które wyprzedziłoby to, co stoi na bramce, zdąży wtedy wejść do kolejki.
        /// </summary>
        public void AwaitCheckOf(PlayerHarness h, string path)
        {
            Assert.True(h.PumpUntil(() => _seen.ContainsKey(path)), $"Nie sprawdzono: {path}");
            h.PumpUntil(() => false, 200);
        }

        public void AssertHoldsOnlyBackgroundThreads(PlayerHarness h)
        {
            Assert.True(h.PumpUntil(() => !_threads.IsEmpty), "Żadna ścieżka nie została sprawdzona.");
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, _threads);
        }

        public void Dispose() => _open.Set();
    }

    [Theory]
    [MemberData(nameof(OpenRoutes))]
    public void SprawdzaniePlikuNieWstrzymujeWatkuInterfejsu(string route) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var track = h.Track("a");

        var opening = OpenBy(route, h, track);

        gate.AssertHoldsOnlyBackgroundThreads(h);
        Assert.False(opening.IsCompleted);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => opening.IsCompleted, OpenTimeout));
        Assert.Equal([track], h.Engine.QueuePaths.ToArray());
    });

    [Fact]
    public void PrzywracanieKolejkiNieWstrzymujeWatkuInterfejsu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var a = h.Track("a");
        var c = h.Track("c");
        Save(h, [a, Path.Combine(h.Directory, "nie-ma.wav"), c], 2, 10, shuffle: true);

        var restoring = h.Player.RestoreQueueState();

        gate.AssertHoldsOnlyBackgroundThreads(h);
        Assert.False(restoring.IsCompleted);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.Equal([a, c], h.Engine.QueuePaths.ToArray());
        Assert.True(h.Player.ShuffleEnabled);

        // Numer bieżącego wpisu liczy się po odfiltrowaniu brakującego pliku.
        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);
    });

    /// <summary>Zapis w trakcie sprawdzania nie może wyczyścić poprzedniej sesji, zanim wróci.</summary>
    [Fact]
    public void ZapisWTrakcieSprawdzaniaZachowujePoprzedniaKolejke() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var missing = Path.Combine(h.Directory, "nie-ma.wav");
        List<string> paths = [h.Track("a"), missing, h.Track("c")];
        Save(h, paths, 2, 10, shuffle: true, repeat: nameof(RepeatMode.Queue));

        var restoring = h.Player.RestoreQueueState();
        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(paths, saved.Paths);
        Assert.Equal(2, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());
        Assert.Null(saved.PendingOpens);

        gate.Open();
        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
    });

    /// <summary>Tryb przełączony w czasie sprawdzania należy do użytkownika; koniec przywracania go nie cofa.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrybyPrzelaczoneWTrakciePrzywracaniaZostajaPoJegoKoncu(bool savedOn) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Save(h, [h.Track("a"), h.Track("b")], 1, 10, shuffle: savedOn, repeat: (savedOn ? RepeatMode.Queue : RepeatMode.None).ToString());

        var restoring = h.Player.RestoreQueueState();

        h.Player.ShuffleEnabled = !savedOn;
        h.Player.CycleRepeat();

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.Equal(2, h.Player.Queue.Count);
        Assert.Equal(!savedOn, h.Player.ShuffleEnabled);
        Assert.Equal(savedOn ? RepeatMode.Track : RepeatMode.Queue, h.Engine.Repeat);
    });

    /// <summary>
    /// Zapis w czasie sprawdzania, także ten przy zamknięciu okna przed jego końcem, niesie tryby
    /// wybrane przez użytkownika, a kolejka czekająca na powrót zostaje w nim bez zmian.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrybyPrzelaczoneWTrakciePrzywracaniaTrafiajaDoZapisu(bool closing) => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        using var gate = new Gate(h, BeyondClosing);
        List<string> paths = [h.Track("a"), Path.Combine(h.Directory, "nie-ma.wav"), h.Track("c")];
        Save(h, paths, 2, 10);

        var restoring = h.Player.RestoreQueueState();

        h.Player.ShuffleEnabled = true;
        h.Player.CycleRepeat();

        if (closing) Close(h);
        else h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(paths, saved.Paths);
        Assert.Equal(2, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());
        Assert.Null(saved.PendingOpens);

        gate.Open();
        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));

        if (!closing) h.Dispose();
    });

    /// <summary>
    /// Otwarcie sprzed przywracania już dołożyło pliki, a przywracanie trwa: poprzednia sesja staje
    /// za nimi, a jej bieżący wpis i pozycja nie wskazują cudzego utworu.
    /// </summary>
    [Fact]
    public void ZapisZaPlikamiOtwartymiPrzedPrzywracaniem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var x = h.Track("x");
        var a = h.Track("a");
        var b = h.Track("b");
        h.OpenPaths([x], replace: false);
        Save(h, [a, b], 1, 30);

        using var gate = new Gate(h, BeyondClosing, only: a);

        var restoring = h.Player.RestoreQueueState();
        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([x, a, b], saved.Paths.ToArray());
        Assert.Equal(-1, saved.CurrentIndex);
        Assert.Equal(0, saved.PositionSeconds);

        gate.Open();
        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
    });

    /// <summary>Przycisk ma pokazywać tryb z zapisu od początku, a nie dopiero po sprawdzeniu ścieżek.</summary>
    [Fact]
    public void TrybyZZapisuWidacOdRazuPoRozpoczeciuPrzywracania() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Save(h, [h.Track("a")], 0, 10, shuffle: true, repeat: nameof(RepeatMode.Track));

        List<string?> raised = [];
        h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var restoring = h.Player.RestoreQueueState();

        Assert.False(restoring.IsCompleted);
        Assert.Empty(h.Player.Queue);
        Assert.True(h.Player.ShuffleEnabled);
        Assert.True(h.Player.RepeatTrack);
        Assert.Contains(nameof(h.Player.ShuffleEnabled), raised);
        Assert.Contains(nameof(h.Player.RepeatTrack), raised);

        gate.Open();
        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.True(h.Player.ShuffleEnabled);
        Assert.True(h.Player.RepeatTrack);
    });

    /// <summary>
    /// Losowość włącza się przy pustej kolejce, jeszcze przed dołożeniem wpisów, a kolejność ma mimo
    /// to objąć je wszystkie, od zapisanego pliku. Tak samo, gdy włączył ją użytkownik w trakcie.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PoPrzywroceniuZLosowosciaKolejnoscObejmujeWszystkieWpisy(bool fromSave) => Ui.Run(() =>
    {
        using var h = new PlayerHarness(random: new Random(20261104));
        using var gate = new Gate(h);
        var tracks = Enumerable.Range(0, 8).Select(i => h.TrackWithTaggedLength($"t{i}")).ToList();
        Save(h, tracks, 5, 30, shuffle: fromSave);

        var restoring = h.Player.RestoreQueueState();

        if (!fromSave) h.Player.ShuffleEnabled = true;
        Assert.True(h.Player.ShuffleEnabled);

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.True(h.Player.ShuffleEnabled);
        Assert.Equal(tracks, h.Engine.QueuePaths.ToList());

        // Długość z tagów decyduje, czy pozycja mieści się w utworze; bez czekania wynik
        // zależałby od tego, czy odczyt w tle zdążył.
        Assert.True(h.PumpUntil(() => h.Engine.Queue.All(entry => entry.Duration > TimeSpan.Zero)));

        h.Player.TogglePlay();

        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.CurrentIndex == 5));
        Assert.True(h.Engine.Position.TotalSeconds >= 29, $"pozycja {h.Engine.Position}");

        List<string> heard = [tracks[5]];
        for (var i = 1; i < tracks.Count; i++)
        {
            h.Player.Next();
            Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking));
            heard.Add(tracks[h.Engine.CurrentIndex]);
        }

        Assert.Equal(tracks.Order(), heard.Order());
    });

    public static readonly TheoryData<string, int> CommandsDuringRestore = new()
    {
        { "TogglePlay", 1 },
        { "Next", 0 },
        { "Previous", 0 },
    };

    private static void Run(string command, PlayerHarness h)
    {
        switch (command)
        {
            case "TogglePlay": h.Player.TogglePlay(); break;
            case "Next": h.Player.Next(); break;
            default: h.Player.Previous(); break;
        }
    }

    /// <summary>
    /// Polecenie naciśnięte, gdy kolejka dopiero wraca, wykonuje się po jej powrocie, tak samo jak
    /// naciśnięte chwilę później. Do tego czasu nie zajmuje karty dźwiękowej.
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandsDuringRestore))]
    public void PolecenieWTrakciePrzywracaniaCzekaNaKolejke(string command, int expectedIndex) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Save(h, [h.TrackWithTaggedLength("a"), h.TrackWithTaggedLength("b"), h.TrackWithTaggedLength("c")], 1, 10);

        var restoring = h.Player.RestoreQueueState();

        Run(command, h);
        Assert.Empty(h.Outputs);
        Assert.False(h.Player.IsPlaying);

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.CurrentIndex == expectedIndex),
            $"wpis {h.Engine.CurrentIndex}");
        Assert.True(h.Player.IsPlaying);

        // Tylko pierwsze odtwarzanie wznawia z zapisanej pozycji.
        if (command == "TogglePlay") Assert.True(h.Engine.Position.TotalSeconds >= 9, $"pozycja {h.Engine.Position}");
    });

    /// <summary>Z kolejki nic nie wróciło, więc nie ma czego grać ani po co zajmować karty dźwiękowej.</summary>
    [Theory]
    [InlineData("TogglePlay")]
    [InlineData("Next")]
    [InlineData("Previous")]
    public void PolecenieWTrakciePrzywracaniaNiczegoNieRobiGdyNicNieWrocilo(string command) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Save(h, [Path.Combine(h.Directory, "nie-ma.wav")], 0, 10);

        var restoring = h.Player.RestoreQueueState();

        Run(command, h);
        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);
    });

    /// <summary>Okno zamknięto przed powrotem kolejki: zamykane okno niczego już nie uruchamia.</summary>
    [Fact]
    public void PolecenieWTrakciePrzywracaniaPrzepadaPrzyZamknieciu() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var a = h.Track("a");
        Save(h, [a], 0, 10);

        var restoring = h.Player.RestoreQueueState();
        h.Player.TogglePlay();

        Close(h);

        Assert.True(Ui.WaitUntil(() => restoring.IsCompleted));
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a], saved.Paths.ToArray());
        Assert.Equal(10, saved.PositionSeconds);
    });

    /// <summary>Tryby z zapisu nie zależą od tego, czy któryś plik został.</summary>
    [Fact]
    public void TrybyWracajaTakzeGdyZadenPlikNieZostal() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Save(h, [Path.Combine(h.Directory, "nie-ma.wav")], 0, 10, shuffle: true, repeat: nameof(RepeatMode.Queue));

        h.RestoreQueueState();

        Assert.Empty(h.Player.Queue);
        Assert.True(h.Player.ShuffleEnabled);
        Assert.True(h.Player.RepeatQueue);

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());
    });

    [Fact]
    public void SondaDyskuOdrozniaPlikOdFolderuIBraku()
    {
        var folder = TestEnvironment.NewDirectory();
        var file = Path.Combine(folder, "a.wav");
        var missing = Path.Combine(folder, "nie-ma");
        File.WriteAllText(file, string.Empty);

        Assert.True(PathProbe.Disk.FileExists(file));
        Assert.False(PathProbe.Disk.FileExists(folder));
        Assert.False(PathProbe.Disk.FileExists(missing));

        Assert.True(PathProbe.Disk.DirectoryExists(folder));
        Assert.False(PathProbe.Disk.DirectoryExists(file));
        Assert.False(PathProbe.Disk.DirectoryExists(missing));
    }

    /// <summary>Program bez podanej sondy widzi prawdziwy dysk: przywrócona kolejka nie filtruje się do zera, a folder się przegląda.</summary>
    [Fact]
    public void ModelWidokuBezPodanejSondySprawdzaPrawdziwyDysk() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(realDisk: true);
        var a = h.Track("a");
        var b = h.Track("b");
        Directory.CreateDirectory(Path.Combine(h.Directory, "album"));
        var inFolder = h.Track(Path.Combine("album", "c"));
        Save(h, [a, Path.Combine(h.Directory, "nie-ma.wav"), b], 2, 10);

        h.RestoreQueueState();

        Assert.Equal([a, b], h.Engine.QueuePaths.ToArray());

        h.OpenPaths([Path.GetDirectoryName(inFolder)!], replace: false);

        Assert.Equal([a, b, inFolder], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>Zapis z pustą kolejką też niesie tryby, np. gdy zamknięcie przerwało otwarcie pustej kolejki.</summary>
    [Fact]
    public void TrybyWracajaZZapisuBezSciezek() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var x = h.Track("x");
        QueueStateStore.Save(new QueueState
        {
            Shuffle = true,
            Repeat = nameof(RepeatMode.Queue),
            PendingOpens = [new PendingOpenState { Paths = [x], Action = "Add" }],
        }, h.QueueFile);

        h.RestoreQueueState();

        Assert.True(h.Player.ShuffleEnabled);
        Assert.True(h.Player.RepeatQueue);
        Assert.Equal([x], h.Engine.QueuePaths.ToArray());

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([x], saved.Paths.ToArray());
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());
    });

    /// <summary>Przywracana kolejka była pierwsza, więc to, co przyszło w trakcie, staje za nią.</summary>
    [Theory]
    [MemberData(nameof(OpenRoutes))]
    public void PlikOtwartyWTrakciePrzywracaniaStajeZaKolejka(string route) => Ui.Run(() =>
    {
        using var h = new PlayerHarness(settings => settings.FileOpenAction = FileOpenAction.Append);
        var a = h.Track("a");
        var b = h.Track("b");
        var c = h.Track("c");
        Save(h, [a, b], 1, 10);
        using var gate = new Gate(h, BeyondClosing, only: a);

        h.Player.RestoreQueueState();
        var opening = route == "OpenPaths" ? h.Player.OpenPaths([c], replace: false) : OpenBy(route, h, c);

        gate.AwaitCheckOf(h, c);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => opening.IsCompleted, OpenTimeout));
        Assert.Equal([a, b, c], h.Engine.QueuePaths.ToArray());

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
    });

    [Fact]
    public void SzybkieOtwarciaNieZamieniajaSieKolejnoscia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var first = h.Track("a");
        var second = h.Track("b");
        using var gate = new Gate(h, BeyondClosing, only: first);

        h.Player.OpenPaths([first], replace: false);
        var later = h.Player.OpenPaths([second], replace: false);

        // Drugi plik jest już sprawdzony, a mimo to czeka za pierwszym.
        gate.AwaitCheckOf(h, second);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => later.IsCompleted, OpenTimeout));
        Assert.Equal([first, second], h.Engine.QueuePaths.ToArray());
    });

    [Fact]
    public void WyjatekPrzySprawdzaniuNiePrzerywaKolejnychOtwarc() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var broken = h.Track("a");
        var good = h.Track("b");
        h.PathChecking = path =>
        {
            if (path == broken) throw new IOException("Dysk nie odpowiada.");
        };

        var failing = h.Player.OpenPaths([broken], replace: false);
        var later = h.Player.OpenPaths([good], replace: false);

        Assert.True(h.PumpUntil(() => later.IsCompleted, OpenTimeout));
        Assert.True(failing.IsCompletedSuccessfully);
        Assert.Equal([good], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>
    /// Wyjątek przy sprawdzaniu ścieżek przywracania, zwykły albo przy zamykaniu okna: kolejka przestaje
    /// czekać, więc podpowiedź wraca, a następny zapis nie dokleja poprzedniej sesji do nowych plików.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WyjatekPrzySprawdzaniuPrzywracaniaKonczyCzekanie(bool closing) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var x = h.Track("x");
        Save(h, [a], 0, 10);
        h.PathChecking = path =>
        {
            if (path == a) throw new IOException("Dysk nie odpowiada.");
        };

        var restoring = h.Player.RestoreQueueState();

        if (closing)
        {
            h.Player.BeginClosing();
            h.Player.FinishPendingOpens();
        }
        else
        {
            Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        }

        Assert.True(h.Player.ShowsAddPrompt);
        Assert.Empty(h.Player.Queue);

        h.OpenPaths([x], replace: false);
        h.Player.SaveQueueState();
        Assert.Equal([x], QueueStateStore.Load(h.QueueFile)!.Paths.ToArray());
    });

    /// <summary>Wynik sprawdzania wraca do zamkniętego okna: nie ma go gdzie dołożyć ani czym zgłosić.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZamkniecieWTrakcieSprawdzaniaNieDajeBledu(bool restoring) => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        using var gate = new Gate(h);
        var track = h.Track("a");

        Task pending;
        if (restoring)
        {
            Save(h, [track], 0, 10);
            pending = h.Player.RestoreQueueState();
        }
        else
        {
            pending = h.Player.OpenPaths([track]);
        }

        h.Dispose();
        gate.Open();

        Assert.True(Ui.WaitUntil(() => pending.IsCompleted));
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Empty(h.Player.Queue);
        Assert.Empty(h.Outputs);
    });

    /// <summary>Pustą kolejkę z podpowiedzią dodawania pokazuje się dopiero, gdy naprawdę jest pusta.</summary>
    [Fact]
    public void PodpowiedzDodawaniaCzekaNaKoniecPrzywracania() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Assert.True(h.Player.ShowsAddPrompt);
        var shown = FollowAddPrompt(h);

        Save(h, [Path.Combine(h.Directory, "nie-ma.wav")], 0, 10);
        var restoring = h.Player.RestoreQueueState();

        var raised = 0;
        h.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(h.Player.ShowsAddPrompt)) raised++;
        };

        Assert.False(h.Player.ShowsAddPrompt);
        Assert.False(shown());

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.True(h.Player.ShowsAddPrompt);
        Assert.True(shown());
        Assert.Equal(1, raised);
        Assert.Empty(h.Player.Queue);
    });

    /// <summary>Podpowiedź znika po dołożeniu plików i wraca po wyczyszczeniu kolejki, także dla widoku śledzącego powiadomienia.</summary>
    [Fact]
    public void PodpowiedzDodawaniaZnikaPoDolozeniuPlikow() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var shown = FollowAddPrompt(h);
        Assert.True(shown());

        h.OpenPaths([h.Track("a")], replace: false);
        Assert.False(shown());

        h.Player.ClearQueue();
        Assert.True(shown());
    });

    /// <summary>Wartość podpowiedzi taka, jak widzi ją wiązanie: zmienia się wyłącznie po powiadomieniu.</summary>
    private static Func<bool> FollowAddPrompt(PlayerHarness h)
    {
        var shown = h.Player.ShowsAddPrompt;
        h.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(h.Player.ShowsAddPrompt)) shown = h.Player.ShowsAddPrompt;
        };

        return () => shown;
    }

    [Fact]
    public void WczytanieListyNieWstrzymujeWatkuInterfejsu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var track = h.Track("x");
        var list = Playlist(h, track, Path.Combine(h.Directory, "nie-ma.wav"));

        var loading = h.Player.LoadPlaylist(list);

        gate.AssertHoldsOnlyBackgroundThreads(h);
        Assert.False(loading.IsCompleted);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => loading.IsCompleted, OpenTimeout));
        Assert.Equal(1, loading.Result!.Missing);
        Assert.Equal([track], h.Engine.QueuePaths.ToArray());
        Assert.False(h.Player.IsPlaying);
    });

    /// <summary>Lista zastępuje to, co przyszło przed nią, a to, co przyszło po niej, staje za nią.</summary>
    [Fact]
    public void ListaZachowujeKolejnoscPrzybycia() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var before = h.Track("a");
        var inList = h.Track("x");
        var after = h.Track("c");
        var list = Playlist(h, inList);
        using var gate = new Gate(h, BeyondClosing, only: before);

        h.Player.OpenPaths([before], replace: false);
        var loading = h.Player.LoadPlaylist(list);
        var later = h.Player.OpenPaths([after], replace: false);

        gate.AwaitCheckOf(h, inList);
        gate.AwaitCheckOf(h, after);
        Assert.Empty(h.Player.Queue);

        gate.Open();

        Assert.True(h.PumpUntil(() => later.IsCompleted, OpenTimeout));
        Assert.True(loading.IsCompletedSuccessfully);
        Assert.Equal([inList, after], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>Błąd odczytu listy dostaje wołający; otwarcia czekające za nią dokładają się mimo to.</summary>
    [Fact]
    public void NieudanyOdczytListyNiePrzerywaKolejnychOtwarc() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var track = h.Track("a");

        var loading = h.Player.LoadPlaylist(Path.Combine(h.Directory, "nie-ma.m3u8"));
        var later = h.Player.OpenPaths([track], replace: false);

        gate.Open();

        Assert.True(h.PumpUntil(() => later.IsCompleted, OpenTimeout));
        Assert.True(loading.IsFaulted);
        Assert.Equal([track], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>
    /// Błąd urządzenia przy zastępowaniu kolejki dostaje wołający, żeby okno go zgłosiło: lista
    /// przeczytana poprawnie nie weszła do kolejki. Otwarcia czekające za nią dokładają się mimo to.
    /// </summary>
    [Fact]
    public void BladUrzadzeniaPrzyZastepowaniuKolejkiListaDostajeWolajacy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var later = h.Track("c");
        var list = Playlist(h, h.Track("x"), Path.Combine(h.Directory, "nie-ma.wav"));

        h.OpenPaths([a]);
        Assert.True(h.Player.IsPlaying);

        h.Outputs[^1].FailStop = true;

        try
        {
            var loading = h.Player.LoadPlaylist(list);

            Assert.True(h.PumpUntil(() => loading.IsCompleted, OpenTimeout));
            Assert.True(loading.IsFaulted, $"stan {loading.Status}");
            Assert.IsType<AudioException>(loading.Exception!.InnerException);
            Assert.Equal([a], h.Engine.QueuePaths.ToArray());

            var opening = h.Player.OpenPaths([later], replace: false);

            Assert.True(h.PumpUntil(() => opening.IsCompleted, OpenTimeout));
            Assert.Equal([a, later], h.Engine.QueuePaths.ToArray());
        }
        finally
        {
            h.Outputs[^1].FailStop = false;
        }
    });

    /// <summary>
    /// Lista niegotowa przy zamknięciu czeka w zapisie kolejki. Spóźniony odczyt nie ma już komu
    /// zgłosić wyniku i nie zmienia kolejki zamkniętego okna, także przed jego zwolnieniem.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZamkniecieWTrakcieWczytywaniaListyNieDajeBledu(bool disposed) => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        using var gate = new Gate(h, BeyondClosing);
        var list = Playlist(h, h.Track("x"));

        var loading = h.Player.LoadPlaylist(list);

        h.Player.BeginClosing();
        h.Player.FinishPendingOpens();
        h.Player.SaveQueueState();
        if (disposed) h.Dispose();
        gate.Open();

        Assert.True(Ui.WaitUntil(() => loading.IsCompleted));
        Assert.True(loading.IsCompletedSuccessfully);
        Assert.Null(loading.Result);
        Assert.Empty(h.Engine.QueuePaths);

        Assert.Equal([$"Playlist: {list}"], SavedOpens(h));

        if (!disposed) h.Dispose();
    });

    /// <summary>Lista gotowa w terminie zamykania wstawia się przed zapisem, a otwarcie po niej staje za nią.</summary>
    [Fact]
    public void ListaGotowaPrzedTerminemZamykaniaZastepujeKolejkeWZapisie() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var inList = h.Track("x");
        var later = h.Track("c");
        Save(h, [h.Track("a")], 0, 10);
        h.RestoreQueueState();

        var loading = h.Player.LoadPlaylist(Playlist(h, inList));
        _ = h.Player.OpenPaths([later], replace: false);

        Close(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([inList, later], saved.Paths.ToArray());
        Assert.Equal(0, saved.CurrentIndex);
        Assert.Equal(0, saved.PositionSeconds);
        Assert.Null(saved.PendingOpens);
        Assert.Empty(h.Outputs);

        Assert.True(Ui.WaitUntil(() => loading.IsCompleted));
        Assert.True(loading.IsCompletedSuccessfully);
        Assert.Null(loading.Result);
    });

    /// <summary>
    /// Zamykane okno nie gasi opisu utworu na ekranie i w panelu systemowym, tak jak przy plikach
    /// przysłanych w chwili zamykania.
    /// </summary>
    [Fact]
    public void ListaWstawianaPrzyZamykaniuNieCzysciOpisuUtworu() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var inList = h.Track("x");
        h.OpenPaths([h.TaggedTrack("a", "Tytuł", "Wykonawca", "Album")]);
        Assert.True(h.PumpUntil(() => h.Player.Artist == "Wykonawca"));

        _ = h.Player.LoadPlaylist(Playlist(h, inList));

        h.Player.BeginClosing();
        h.Player.FinishPendingOpens();

        Assert.Equal([inList], h.Engine.QueuePaths.ToArray());
        Assert.Equal("Wykonawca", h.Player.Artist);

        h.Dispose();
    });

    /// <summary>
    /// Lista nie zdążyła przed terminem, więc plik wybrany po niej też czeka, żeby jej nie wyprzedzić.
    /// Kolejka w zapisie zostaje taka, jaka była.
    /// </summary>
    [Fact]
    public void ListaNiegotowaWTerminieZamykaniaCzekaWZapisieRazemZPozniejszymOtwarciem() => Ui.Run(() =>
    {
        var h = new PlayerHarness();

        var (a, _, later, list) = CloseWithSlowPlaylist(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a], saved.Paths.ToArray());
        Assert.Equal(0, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
        Assert.Equal([$"Playlist: {list}", $"Append: {later}"], SavedOpens(h));
    });

    /// <summary>
    /// Lista z poprzedniego uruchomienia tylko dopisuje: wznowienie zostaje, nic nie gra, a późniejsze
    /// otwarcie staje za nią.
    /// </summary>
    [Fact]
    public void NastepneUruchomienieDopisujeListeZaPrzywroconaKolejka() => Ui.Run(() =>
    {
        var h = new PlayerHarness();

        var (a, inList, later, _) = CloseWithSlowPlaylist(h);

        using var next = new PlayerHarness(directory: h.Directory);
        next.RestoreQueueState();

        Assert.Equal([a, inList, later], next.Engine.QueuePaths.ToArray());
        Assert.False(next.Player.IsPlaying);
        Assert.Empty(next.Outputs);

        next.Player.SaveQueueState();
        var after = QueueStateStore.Load(next.QueueFile)!;
        Assert.Equal([a, inList, later], after.Paths.ToArray());
        Assert.Equal(0, after.CurrentIndex);
        Assert.Equal(10, after.PositionSeconds);
        Assert.Null(after.PendingOpens);
    });

    /// <summary>Plik listy zniknął między uruchomieniami: nie ma czego dopisać, a późniejsze otwarcie dokłada się normalnie.</summary>
    [Fact]
    public void ListaBezPlikuPrzyNastepnymUruchomieniuNieZatrzymujePozniejszegoOtwarcia() => Ui.Run(() =>
    {
        var h = new PlayerHarness();

        var (a, _, later, list) = CloseWithSlowPlaylist(h);
        Assert.Equal([$"Playlist: {list}", $"Append: {later}"], SavedOpens(h));
        File.Delete(list);

        using var next = new PlayerHarness(directory: h.Directory);
        next.RestoreQueueState();

        Assert.Equal([a, later], next.Engine.QueuePaths.ToArray());
        Assert.False(next.Player.IsPlaying);
        Assert.Empty(next.Outputs);

        next.Player.SaveQueueState();
        Assert.Null(QueueStateStore.Load(next.QueueFile)!.PendingOpens);
    });

    /// <summary>Program może skończyć się nagle w trakcie czytania listy, więc jest ona w każdym zapisie, aż się wstawi.</summary>
    [Fact]
    public void ZapisWTrakcieCzytaniaListyZawieraJaWOtwarciachOczekujacych() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var inList = h.Track("x");
        var list = Playlist(h, inList);

        var loading = h.Player.LoadPlaylist(list);
        h.Player.SaveQueueState();

        Assert.Empty(QueueStateStore.Load(h.QueueFile)!.Paths);
        Assert.Equal([$"Playlist: {list}"], SavedOpens(h));

        gate.Open();

        Assert.True(h.PumpUntil(() => loading.IsCompleted, OpenTimeout));
        h.Player.SaveQueueState();

        var after = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([inList], after.Paths.ToArray());
        Assert.Null(after.PendingOpens);
    });

    /// <summary>Nieczytelna lista przy zamykaniu nie ma komu zgłosić błędu ani po co wracać przy następnym uruchomieniu.</summary>
    [Fact]
    public void NieudanyOdczytListyPrzyZamykaniuNieTrafiaDoZapisu() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var track = h.Track("a");

        var loading = h.Player.LoadPlaylist(Path.Combine(h.Directory, "nie-ma.m3u8"));
        _ = h.Player.OpenPaths([track], replace: false);

        Close(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([track], saved.Paths.ToArray());
        Assert.Null(saved.PendingOpens);

        Assert.True(Ui.WaitUntil(() => loading.IsCompleted));
        Assert.True(loading.IsCompletedSuccessfully);
        Assert.Null(loading.Result);
    });

    /// <summary>Lista dokańczana po starcie znów nie zdążyła: wraca do zapisu jako lista, a nie jako zwykła ścieżka.</summary>
    [Fact]
    public void DrugieZamkniecieWTrakcieDokanczaniaListyZapisujeJaZnowu() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var a = h.Track("a");
        var inList = h.Track("x");
        var later = h.Track("c");
        var list = Playlist(h, inList);

        QueueStateStore.Save(new QueueState
        {
            Paths = [a],
            CurrentIndex = 0,
            PositionSeconds = 10,
            PendingOpens =
            [
                new PendingOpenState { Paths = [list], Action = "Playlist" },
                new PendingOpenState { Paths = [later], Action = "Add" },
            ],
        }, h.QueueFile);

        using var gate = new Gate(h, BeyondClosing, only: inList);

        _ = h.Player.RestoreQueueState();
        Close(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a], saved.Paths.ToArray());
        Assert.Equal(10, saved.PositionSeconds);
        Assert.Equal([$"Playlist: {list}", $"Append: {later}"], SavedOpens(h));
    });

    /// <summary>
    /// Użytkownik zdążył włączyć przywróconą kolejkę przed końcem czytania listy z poprzedniego
    /// uruchomienia: gra dalej, a lista tylko się dopisuje.
    /// </summary>
    [Fact]
    public void DokanczanaListaNieRuszaGranejKolejki() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var playing = h.Track("a");
        var inList = h.Track("x");
        var list = Playlist(h, inList);

        QueueStateStore.Save(new QueueState
        {
            Paths = [playing],
            CurrentIndex = 0,
            PendingOpens = [new PendingOpenState { Paths = [list], Action = "Playlist" }],
        }, h.QueueFile);

        using var gate = new Gate(h, BeyondClosing, only: inList);

        var restoring = h.Player.RestoreQueueState();

        Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 1));
        h.Player.TogglePlay();
        Assert.True(h.Player.IsPlaying);

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.True(h.Player.IsPlaying);
        Assert.Equal([playing, inList], h.Engine.QueuePaths.ToArray());
        Assert.Equal(0, h.Engine.CurrentIndex);
    });

    /// <summary>
    /// Dysk nie odpowiada przy starcie: plików nie ma razem z ich folderem, ale wrócą. Samo
    /// uruchomienie i zamknięcie nie kasuje zapisanej kolejki, bieżącego wpisu ani pozycji.
    /// </summary>
    [Fact]
    public void NiedostepnyDyskNieKasujeZapisanejKolejki() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var network = Path.Combine(h.Directory, "siec");
        var a = Path.Combine(network, "a.wav");
        var b = Path.Combine(network, "b.wav");
        Save(h, [a, b], 1, 120, shuffle: true, repeat: nameof(RepeatMode.Queue));

        h.RestoreQueueState();

        Assert.Empty(h.Player.Queue);
        Assert.True(h.Player.ShowsAddPrompt);

        Close(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a, b], saved.Paths.ToArray());
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(120, saved.PositionSeconds);
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());

        Directory.CreateDirectory(network);
        using var next = new PlayerHarness(directory: h.Directory);
        next.Track(Path.Combine("siec", "a"));
        next.Track(Path.Combine("siec", "b"));
        next.RestoreQueueState();

        Assert.Equal([a, b], next.Engine.QueuePaths.ToArray());
        Assert.True(next.Player.ShuffleEnabled);

        next.Player.SaveQueueState();
        var after = QueueStateStore.Load(next.QueueFile)!;
        Assert.Equal(1, after.CurrentIndex);
        Assert.Equal(120, after.PositionSeconds);
    });

    /// <summary>Tak samo, gdy część plików została, a zapis idzie w tle, bez zamykania okna.</summary>
    [Fact]
    public void CzesciowoNiedostepnyDyskNieKasujeWpisuIPozycji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var local = h.Track("lokalny");
        var away = Path.Combine(h.Directory, "siec", "b.wav");
        Save(h, [local, away], 1, 120);

        h.RestoreQueueState();

        Assert.Equal([local], h.Engine.QueuePaths.ToArray());

        var saving = h.Player.SaveQueueStateInBackground();
        Assert.True(h.PumpUntil(() => saving.IsCompleted, OpenTimeout));

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([local, away], saved.Paths.ToArray());
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(120, saved.PositionSeconds);
    });

    /// <summary>Kto zmienił kolejkę albo włączył to, co zostało, pracuje już z tym, co widzi na ekranie.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PracaNaKolejceBezNiedostepnychPlikowZastepujeZapis(bool play) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var local = h.Track("lokalny");
        var added = h.Track("dodany");
        Save(h, [local, Path.Combine(h.Directory, "siec", "b.wav")], 1, 120);

        h.RestoreQueueState();

        if (play)
        {
            h.Player.TogglePlay();
            Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 0));
        }
        else
        {
            h.AddPaths([added]);
        }

        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(play ? [local] : [local, added], saved.Paths.ToArray());
        if (play) Assert.Equal(0, saved.CurrentIndex);
    });

    public static readonly TheoryData<string> PathsWithoutFiles = ["nie-ma.wav", "lista.m3u8", "siec/c.wav"];

    /// <summary>
    /// Ścieżka ze startu ma pierwszeństwo przed poprzednią sesją, ale tylko gdy daje pliki. Plik,
    /// którego nie ma, lista albo niedostępny dysk nie kasują zapisanej kolejki ani pozycji.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathsWithoutFiles))]
    public void StartZeSciezkaBezPlikowPrzywracaZapisanaKolejke(string name) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        Save(h, [a, b], 1, 30, shuffle: true, repeat: nameof(RepeatMode.Queue));

        var path = Path.Combine(h.Directory, name);
        if (name.EndsWith(".m3u8")) File.WriteAllText(path, $"#EXTM3U\n{a}\n");

        var starting = h.Player.OpenAtStart([path]);

        Assert.True(h.PumpUntil(() => starting.IsCompleted, OpenTimeout));
        Assert.Equal([a, b], h.Engine.QueuePaths.ToArray());
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a, b], saved.Paths.ToArray());
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(30, saved.PositionSeconds);
        Assert.True(saved.Shuffle);
        Assert.Equal(RepeatMode.Queue, saved.ReadRepeat());
    });

    /// <summary>Otwarcie niedokończone przy poprzednim zamknięciu staje za przywróconą kolejką, a nie zamiast niej.</summary>
    [Fact]
    public void StartZeSciezkaBezPlikowDokanczaOtwarcieZaKolejka() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        var c = h.Track("c");
        QueueStateStore.Save(new QueueState
        {
            Paths = [a, b],
            CurrentIndex = 1,
            PositionSeconds = 30,
            PendingOpens = [new PendingOpenState { Paths = [c], Action = "Add" }],
        }, h.QueueFile);

        var starting = h.Player.OpenAtStart([Path.Combine(h.Directory, "nie-ma.wav")]);

        Assert.True(h.PumpUntil(() => starting.IsCompleted, OpenTimeout));
        Assert.Equal([a, b, c], h.Engine.QueuePaths.ToArray());

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(30, saved.PositionSeconds);
        Assert.Null(saved.PendingOpens);
    });

    /// <summary>Ścieżka, która daje pliki, zastępuje poprzednią kolejkę i gra, bez dokładania tamtej.</summary>
    [Fact]
    public void StartZeSciezkaDajacaPlikiZastepujePoprzedniaKolejke() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var x = h.Track("x");
        Save(h, [h.Track("a"), h.Track("b")], 1, 30);

        var starting = h.Player.OpenAtStart([x]);

        Assert.True(h.PumpUntil(() => starting.IsCompleted, OpenTimeout));
        Assert.Equal([x], h.Engine.QueuePaths.ToArray());
        Assert.True(h.Player.IsPlaying);

        h.Player.SaveQueueState();
        Assert.Equal([x], QueueStateStore.Load(h.QueueFile)!.Paths.ToArray());
    });

    /// <summary>
    /// Ścieżka ze startu nie zdążyła przed zamknięciem: poprzednia kolejka zostaje w zapisie, a ścieżka
    /// czeka w otwarciach. Dopiero następne uruchomienie rozstrzyga, czy daje pliki.
    /// </summary>
    [Fact]
    public void ZamkniecieWTrakcieSprawdzaniaSciezkiZeStartuZachowujeKolejke() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        var x = h.Track("x");
        Save(h, [a, b], 1, 30);

        using var gate = new Gate(h, BeyondClosing, only: x);

        _ = h.Player.OpenAtStart([x]);
        Close(h);

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal([a, b], saved.Paths.ToArray());
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(30, saved.PositionSeconds);
        Assert.Equal([$"Outside: {x}"], SavedOpens(h));
    });

    public static readonly TheoryData<string> ClearedRoutes = ["OpenPaths", "AddPaths", "OpenFromOutside", "Append", "Playlist"];

    /// <summary>
    /// Nadawca uznał otwarcie za przyjęte, więc zapis okresowy ma je objąć jeszcze przed końcem
    /// sprawdzania: bez tego nagły koniec programu je gubi, gdy nic nie gra.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClearedRoutes))]
    public void OtwarcieCzekajaceNaSprawdzenieZamawiaZapis(string route) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var x = h.Track("x");
        var list = Playlist(h, x);
        using var gate = new Gate(h);

        Assert.False(h.Player.QueueSavePending);

        _ = route switch
        {
            "Append" => h.Player.OpenPaths([x], replace: false),
            "Playlist" => h.Player.LoadPlaylist(list),
            _ => OpenBy(route, h, x),
        };

        Assert.True(h.Player.QueueSavePending);

        h.Player.SaveQueueState();
        Assert.Equal([route == "Playlist" ? list : x], QueueStateStore.Load(h.QueueFile)!.PendingOpens!.SelectMany(open => open.Paths));
    });

    /// <summary>Otwarcie, które nie dało plików, znika z zapisu, zamiast czekać w nim do następnej zmiany.</summary>
    [Fact]
    public void OtwarcieBezPlikowZamawiaZapisPoSprawdzeniu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);

        var opening = h.Player.AddPaths([Path.Combine(h.Directory, "nie-ma.wav")]);
        h.Player.SaveQueueState();
        Assert.False(h.Player.QueueSavePending);

        gate.Open();

        Assert.True(h.PumpUntil(() => opening.IsCompleted, OpenTimeout));
        Assert.True(h.Player.QueueSavePending);
    });

    /// <summary>
    /// Wyczyszczenie obejmuje też to, co przyszło przed nim i czeka jeszcze na sprawdzenie:
    /// spóźniony plik nie wraca do kolejki, nie gra i nie kasuje cofnięcia.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClearedRoutes))]
    public void WyczyszczenieWTrakcieSprawdzaniaZabieraCzekajaceOtwarcie(string route) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        var x = h.Track("x");
        var list = Playlist(h, x);
        h.OpenPaths([a, b], replace: false);

        using var gate = new Gate(h);

        Task opening = route switch
        {
            "Append" => h.Player.OpenPaths([x], replace: false),
            "Playlist" => h.Player.LoadPlaylist(list),
            _ => OpenBy(route, h, x),
        };

        h.Player.ClearQueue();

        Assert.Empty(h.Player.Queue);
        Assert.True(h.Player.CanUndoQueue);

        gate.Open();

        Assert.True(h.PumpUntil(() => h.Player.OpensFinished.IsCompleted && opening.IsCompleted));
        Assert.True(opening.IsCompletedSuccessfully);
        if (opening is Task<PlaylistLoad?> loading) Assert.Null(loading.Result);

        Assert.Empty(h.Player.Queue);
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Outputs);
        Assert.True(h.Player.CanUndoQueue);

        h.Player.SaveQueueState();
        Assert.Null(QueueStateStore.Load(h.QueueFile)!.PendingOpens);

        h.Player.UndoQueue();
        Assert.Equal([a, b], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>To, co przyszło po wyczyszczeniu, jest nowym żądaniem i trafia do kolejki.</summary>
    [Fact]
    public void OtwarciePoWyczyszczeniuDokladaSieNormalnie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var x = h.Track("x");
        var y = h.Track("y");
        h.OpenPaths([a], replace: false);

        using var gate = new Gate(h);

        _ = h.Player.OpenPaths([x], replace: false);
        h.Player.ClearQueue();
        var later = h.Player.OpenPaths([y], replace: false);

        gate.Open();

        Assert.True(h.PumpUntil(() => later.IsCompleted, OpenTimeout));
        Assert.Equal([y], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>
    /// Anulowane sprawdzanie nieodpowiadającego dysku nie wstrzymuje następnych otwarć: lokalny plik
    /// trafia do kolejki, choć tamto sprawdzanie nadal stoi na bramce.
    /// </summary>
    [Fact]
    public void OtwarciePoWyczyszczeniuNieCzekaNaAnulowaneSprawdzanie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var a = h.Track("a");
        var x = h.Track("x");
        var y = h.Track("y");
        h.OpenPaths([a], replace: false);

        using var gate = new Gate(h, only: x);

        _ = h.Player.OpenPaths([x], replace: false);
        gate.AwaitCheckOf(h, x);
        h.Player.ClearQueue();
        var later = h.Player.OpenPaths([y], replace: false);

        Assert.True(h.PumpUntil(() => later.IsCompleted, 1000), "otwarcie czeka na anulowane sprawdzanie");
        Assert.Equal([y], h.Engine.QueuePaths.ToArray());
    });

    /// <summary>Jawne „Play” i „Pause” w trakcie przywracania zachowują kolejność i kończą bez odtwarzania.</summary>
    [Theory]
    [InlineData("Pause")]
    [InlineData("TogglePlayPause")]
    public void PlayPotemPauzaWTrakciePrzywracaniaNieGra(string second) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        Save(h, [h.TrackWithTaggedLength("a"), h.TrackWithTaggedLength("b")], 1, 10);

        var restoring = h.Player.RestoreQueueState();

        h.Player.Play();
        if (second == "Pause") h.Player.Pause();
        else { h.Player.TogglePlay(); h.Player.Pause(); }

        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        h.PumpUntil(() => false, 300);
        Assert.False(h.Player.IsPlaying);
        Assert.NotEqual(PlaybackState.Playing, h.Engine.State);
    });

    /// <summary>Przywracanej kolejki nikt jeszcze nie widział, więc wyczyszczenie nie zabiera poprzedniej sesji.</summary>
    [Fact]
    public void WyczyszczenieWTrakciePrzywracaniaNieZabieraPoprzedniejSesji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var gate = new Gate(h);
        var a = h.Track("a");
        var b = h.Track("b");
        Save(h, [a, b], 1, 10);

        var restoring = h.Player.RestoreQueueState();

        h.Player.ClearQueue();
        gate.Open();

        Assert.True(h.PumpUntil(() => restoring.IsCompleted, OpenTimeout));
        Assert.Equal([a, b], h.Engine.QueuePaths.ToArray());

        h.Player.SaveQueueState();
        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(10, saved.PositionSeconds);
    });

    /// <summary>
    /// Zamyka okno, gdy czytanie listy z wolnego dysku trwa dłużej niż termin, a za listą czeka plik
    /// wybrany po niej. W zapisie zostaje kolejka z jednym plikiem, bieżącym, z pozycją 10 s.
    /// </summary>
    private static (string A, string InList, string Later, string List) CloseWithSlowPlaylist(PlayerHarness h)
    {
        var a = h.Track("a");
        var inList = h.Track("x");
        var later = h.Track("c");
        var list = Playlist(h, inList);
        Save(h, [a], 0, 10);
        h.RestoreQueueState();

        using var gate = new Gate(h, BeyondClosing, only: inList);

        _ = h.Player.LoadPlaylist(list);
        _ = h.Player.OpenPaths([later], replace: false);

        Close(h);

        return (a, inList, later, list);
    }

    private static IEnumerable<string> SavedOpens(PlayerHarness h) =>
        QueueStateStore.Load(h.QueueFile)!.PendingOpens?.Select(open => $"{open.Action}: {string.Join("|", open.Paths)}") ?? [];

    /// <summary>Ta sama kolejność co przy zamykaniu okna.</summary>
    private static void Close(PlayerHarness h)
    {
        h.Player.BeginClosing();
        h.Player.FinishPendingOpens();
        h.Player.SaveQueueState();
        h.Dispose();
    }

    private static string Playlist(PlayerHarness h, params string[] tracks)
    {
        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, "#EXTM3U\n" + string.Concat(tracks.Select(track => track + "\n")));
        return list;
    }

    private static void Save(PlayerHarness h, List<string> paths, int current, double seconds, bool shuffle = false, string? repeat = null) =>
        QueueStateStore.Save(
            new QueueState { Paths = paths, CurrentIndex = current, PositionSeconds = seconds, Shuffle = shuffle, Repeat = repeat },
            h.QueueFile);
}
