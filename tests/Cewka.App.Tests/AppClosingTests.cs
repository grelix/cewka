using System.Diagnostics;
using System.Reflection;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Pojedyncza instancja w czasie zamykania okna. Stan aplikacji jest statyczny, więc test idzie
/// w kolekcji bez równoległości.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppClosingTests
{
    /// <summary>
    /// Ścieżka przysłana w trakcie zapisu kolejki trafiłaby do okna, które już jej nie przyjmie.
    /// Nadawca ma dostać odmowę i sam przejąć rolę po zamknięciu.
    /// </summary>
    [Fact]
    public void ZamykaneOknoNiePrzyjmujeSciezekOdNastepnejKopii()
    {
        var directory = TestEnvironment.NewDirectory();
        var address = new InstanceAddress(
            "Cewka.test." + Guid.NewGuid().ToString("N"),
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));

        App.Instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(App.Instance);

        try
        {
            App.BeginClosing();

            Assert.False(SingleInstance.TryHandOff(address, ["/muzyka/utwor.mp3"]));
        }
        finally
        {
            App.ReleaseSingleInstance();
            typeof(App).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        }
    }

    /// <summary>
    /// Ścieżka odebrana tuż przed zamknięciem czeka jeszcze w dyspozytorze, gdy okno zapisuje
    /// kolejkę. Nadawca uznał przekazanie za udane, więc plik ma trafić do zapisanej kolejki.
    /// </summary>
    [Fact]
    public void SciezkaOdebranaPrzedZamknieciemTrafiaDoZapisanejKolejki() => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var address = new InstanceAddress(
            "Cewka.test." + Guid.NewGuid().ToString("N"),
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));

        var mainWindow = typeof(App).GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = mainWindow.GetValue(null);

        var h = new PlayerHarness();
        var window = new MainWindow(false, h.Player);
        window.Show();
        mainWindow.SetValue(null, window);

        App.Instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(App.Instance);

        try
        {
            typeof(App).GetMethod("AttachSingleInstance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Avalonia.Application.Current, null);

            var track = h.Track("a");
            Assert.True(SingleInstance.TryHandOff(address, [track]));

            // Odbiorca zdąży przekazać ścieżkę do dyspozytora, który wykona ją dopiero po zamknięciu.
            Thread.Sleep(200);
            window.Close();

            Assert.Equal([track], QueueStateStore.Load(h.QueueFile)!.Paths.ToArray());
        }
        finally
        {
            if (window.IsVisible) window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            App.ReleaseSingleInstance();
            typeof(App).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
            mainWindow.SetValue(null, previous);
        }
    });

    /// <summary>
    /// Folder przekazany tuż przed zamknięciem albo plik przekazany, gdy wcześniejsze otwieranie
    /// jeszcze trwa, nie mogą czekać na przeglądanie w tle, które skończy się po zapisie kolejki.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SciezkaWymagajacaPrzegladaniaTrafiaDoZapisanejKolejki(bool earlierOpenPending) => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var address = new InstanceAddress(
            "Cewka.test." + Guid.NewGuid().ToString("N"),
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));

        var mainWindow = typeof(App).GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = mainWindow.GetValue(null);

        var h = new PlayerHarness();
        var window = new MainWindow(false, h.Player);
        window.Show();
        mainWindow.SetValue(null, window);

        App.Instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(App.Instance);

        try
        {
            typeof(App).GetMethod("AttachSingleInstance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Avalonia.Application.Current, null);

            var track = h.Track("a");
            var folder = Path.Combine(h.Directory, "album");
            Directory.CreateDirectory(folder);
            var inFolder = Path.Combine(folder, "b.wav");
            File.Copy(track, inFolder);

            string expected;
            if (earlierOpenPending)
            {
                // Dokończenie wcześniejszego otwierania czeka w dyspozytorze, którego nikt nie pompuje.
                _ = h.Player.OpenFromOutside([folder]);
                expected = track;
                Assert.True(SingleInstance.TryHandOff(address, [track]));
            }
            else
            {
                expected = inFolder;
                Assert.True(SingleInstance.TryHandOff(address, [folder]));
            }

            Thread.Sleep(200);
            window.Close();

            var saved = QueueStateStore.Load(h.QueueFile)?.Paths.ToArray() ?? [];
            Assert.Contains(expected, saved);

            // Wcześniej otwierany folder też trafia do kolejki i stoi przed plikiem przysłanym później.
            if (earlierOpenPending) Assert.Equal([inFolder, track], saved);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            App.ReleaseSingleInstance();
            typeof(App).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
            mainWindow.SetValue(null, previous);
        }
    });

    /// <summary>
    /// Folder od innej kopii odebrany przez dyspozytor przed zamknięciem: przeglądanie w tle już
    /// ruszyło, a jego dokończenie czeka w dyspozytorze, którego nikt nie pompuje. Nadawca zakończył
    /// pracę, więc pliki z folderu mają trafić do zapisanej kolejki — przed plikiem upuszczonym później.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderOdebranyPrzedZamknieciemTrafiaDoZapisanejKolejki(bool laterFile) => Ui.Run(() =>
    {
        var directory = TestEnvironment.NewDirectory();
        var address = new InstanceAddress(
            "Cewka.test." + Guid.NewGuid().ToString("N"),
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));

        var mainWindow = typeof(App).GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = mainWindow.GetValue(null);

        var h = new PlayerHarness();
        var window = new MainWindow(false, h.Player);
        window.Show();
        mainWindow.SetValue(null, window);

        App.Instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(App.Instance);

        try
        {
            typeof(App).GetMethod("AttachSingleInstance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Avalonia.Application.Current, null);

            var track = h.Track("a");
            var folder = Path.Combine(h.Directory, "album");
            Directory.CreateDirectory(folder);
            var inFolder = Path.Combine(folder, "b.wav");
            File.Copy(track, inFolder);

            Assert.True(SingleInstance.TryHandOff(address, [folder]));
            Thread.Sleep(200);

            // Dyspozytor wykonuje odbiór jak w działającym programie: przeglądanie rusza w tle.
            typeof(App).GetMethod("OnPathsHandedOver", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

            // Upuszczony plik czeka za folderem.
            if (laterFile) _ = h.Player.OpenFromOutside([track]);

            window.Close();

            var saved = QueueStateStore.Load(h.QueueFile)?.Paths.ToArray() ?? [];
            Assert.Equal(laterFile ? [inFolder, track] : [inFolder], saved);

            // Dokończenie w tle, wykonane już po zamknięciu, niczego nie dokłada drugi raz.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(laterFile ? 2 : 1, h.Player.Queue.Count);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            App.ReleaseSingleInstance();
            typeof(App).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
            mainWindow.SetValue(null, previous);
        }
    });

    /// <summary>
    /// Folder przysłany przed zamknięciem przegląda się dłużej niż termin zamykania. Zamykanie
    /// nie czeka na niego dłużej niż <see cref="SingleInstance.ClosingGrace"/>, bo następna kopia
    /// czeka na zwolnienie roli, ale nadawca uznał folder za przyjęty, więc otwarcie nie przepada:
    /// zapisuje się razem z plikiem przysłanym po nim, a następne uruchomienie dokłada oba
    /// w kolejności przybycia, bez odtwarzania.
    /// </summary>
    [Fact]
    public void WolnePrzegladanieFolderuDokanczaSiePrzyNastepnymUruchomieniu() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();

        var track = h.Track("a");
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        var inFolder = h.Track(Path.Combine("album", "b"));

        h.PathChecking = path =>
        {
            if (path == folder) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            WithClosingWindow(h, (address, window) =>
            {
                Assert.True(SingleInstance.TryHandOff(address, [folder]));
                Assert.True(SingleInstance.TryHandOff(address, [track]));
                Thread.Sleep(200);

                var released = CloseUntilRoleReleased(window);

                Assert.True(released < SingleInstance.ClosingGrace + TimeSpan.FromSeconds(1.5),
                    $"Zamykanie trwało {released.TotalSeconds:0.0} s.");

                // Plik przysłany po folderze czeka za nim, żeby go nie wyprzedzić w kolejce.
                var saved = QueueStateStore.Load(h.QueueFile)!;
                Assert.Empty(saved.Paths);
                Assert.Equal([folder, track], saved.PendingOpens?.Select(open => string.Join("|", open.Paths)) ?? []);
            });
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        using var next = new PlayerHarness(directory: h.Directory);
        next.Track("a");
        next.Track(Path.Combine("album", "b"));

        next.Player.RestoreQueueState();

        Assert.True(next.PumpUntil(() => next.Player.Queue.Count == 2), $"w kolejce {next.Player.Queue.Count}");
        Assert.Equal([inFolder, track], next.Engine.QueuePaths.ToArray());
        Assert.False(next.Player.IsPlaying);
        Assert.Empty(next.Outputs);

        // Dokończone otwarcia znikają z zapisu.
        next.Player.SaveQueueState();
        var after = QueueStateStore.Load(next.QueueFile)!;
        Assert.Equal([inFolder, track], after.Paths.ToArray());
        Assert.Null(after.PendingOpens);
    });

    /// <summary>
    /// Plik na niedostępnym dysku sieciowym, przysłany w chwili zamykania: samo sprawdzenie, czy
    /// istnieje, potrafi czekać kilkanaście sekund. Zamykanie nie czeka na nie dłużej niż
    /// <see cref="SingleInstance.ClosingGrace"/>, a otwarcie dokańcza się przy następnym uruchomieniu.
    /// </summary>
    [Fact]
    public void PlikNaWolnymDyskuNieWstrzymujeZamykania() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();
        var track = h.Track("a");

        h.PathChecking = path =>
        {
            if (path == track) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            WithClosingWindow(h, (address, window) =>
            {
                Assert.True(SingleInstance.TryHandOff(address, [track]));
                Thread.Sleep(200);

                var released = CloseUntilRoleReleased(window);

                Assert.True(released < SingleInstance.ClosingGrace + TimeSpan.FromSeconds(1.5),
                    $"Zamykanie trwało {released.TotalSeconds:0.0} s.");

                var saved = QueueStateStore.Load(h.QueueFile)!;
                Assert.Empty(saved.Paths);
                Assert.Equal([track], saved.PendingOpens?.SelectMany(open => open.Paths) ?? []);
            });
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        using var next = new PlayerHarness(directory: h.Directory);
        next.Track("a");
        next.Player.RestoreQueueState();

        Assert.True(next.PumpUntil(() => next.Player.Queue.Count == 1), $"w kolejce {next.Player.Queue.Count}");
        Assert.Equal([track], next.Engine.QueuePaths.ToArray());
    });

    /// <summary>
    /// Kolejka z poprzedniej sesji sprawdzana jeszcze na niedostępnym dysku w chwili zamykania okna:
    /// zamykanie nie czeka na nią dłużej niż <see cref="SingleInstance.ClosingGrace"/>, a jej zapis zostaje
    /// bez zmian, z bieżącym wpisem i pozycją. Plik przysłany w tym czasie czeka za nią i dokłada się
    /// przy następnym uruchomieniu.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrzywracanieNaWolnymDyskuNiePrzepadaPrzyZamknieciu(bool fileDuringClosing) => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();

        var a = h.Track("a");
        var b = h.Track("b");
        var sent = h.Track("c");
        QueueStateStore.Save(new QueueState { Paths = [a, b], CurrentIndex = 1, PositionSeconds = 30, Shuffle = true }, h.QueueFile);

        h.PathChecking = path =>
        {
            if (path == a) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            WithClosingWindow(h, (address, window) =>
            {
                Assert.Empty(h.Player.Queue);

                if (fileDuringClosing)
                {
                    Assert.True(SingleInstance.TryHandOff(address, [sent]));
                    Thread.Sleep(200);
                }

                var released = CloseUntilRoleReleased(window);

                Assert.True(released < SingleInstance.ClosingGrace + TimeSpan.FromSeconds(1.5),
                    $"Zamykanie trwało {released.TotalSeconds:0.0} s.");

                var saved = QueueStateStore.Load(h.QueueFile)!;
                Assert.Equal([a, b], saved.Paths.ToArray());
                Assert.Equal(1, saved.CurrentIndex);
                Assert.Equal(30, saved.PositionSeconds);
                Assert.True(saved.Shuffle);
                Assert.Equal(fileDuringClosing ? [sent] : [], saved.PendingOpens?.SelectMany(open => open.Paths) ?? []);
            });
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        // Spóźniony wynik sprawdzania nie zmienia już niczego w zamkniętym oknie.
        Assert.True(h.PumpUntil(() => h.Player.OpensFinished.IsCompleted));
        Assert.Empty(h.Player.Queue);

        using var next = new PlayerHarness(directory: h.Directory);
        next.Track("a");
        next.Track("b");
        next.Track("c");
        next.RestoreQueueState();

        Assert.Equal(fileDuringClosing ? [a, b, sent] : [a, b], next.Engine.QueuePaths.ToArray());
        Assert.Empty(next.Outputs);

        next.Player.SaveQueueState();
        var after = QueueStateStore.Load(next.QueueFile)!;
        Assert.Equal(1, after.CurrentIndex);
        Assert.Equal(30, after.PositionSeconds);
        Assert.Null(after.PendingOpens);
    });

    /// <summary>
    /// Lista z niedostępnego dysku, a po niej plik od innej kopii, przy zamykaniu okna: zamykanie nie
    /// czeka dłużej niż <see cref="SingleInstance.ClosingGrace"/>, oba otwarcia trafiają do zapisu
    /// w kolejności przybycia, a następne uruchomienie dopisuje je za kolejką, bez odtwarzania.
    /// </summary>
    [Fact]
    public void ListaNaWolnymDyskuNiePrzepadaPrzyZamknieciuOkna() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();

        var a = h.Track("a");
        var inList = h.Track("x");
        var sent = h.Track("c");
        var list = Path.Combine(h.Directory, "lista.m3u8");
        File.WriteAllText(list, $"#EXTM3U\n{inList}\n");
        QueueStateStore.Save(new QueueState { Paths = [a], CurrentIndex = 0, PositionSeconds = 30 }, h.QueueFile);

        h.PathChecking = path =>
        {
            if (path == inList) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            WithClosingWindow(h, (address, window) =>
            {
                Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 1));

                var loading = h.Player.LoadPlaylist(list);

                Assert.True(SingleInstance.TryHandOff(address, [sent]));
                Thread.Sleep(200);

                var released = CloseUntilRoleReleased(window);

                Assert.True(released < SingleInstance.ClosingGrace + TimeSpan.FromSeconds(1.5),
                    $"Zamykanie trwało {released.TotalSeconds:0.0} s.");

                var saved = QueueStateStore.Load(h.QueueFile)!;
                Assert.Equal([a], saved.Paths.ToArray());
                Assert.Equal(30, saved.PositionSeconds);
                Assert.Equal(
                    [$"Playlist: {list}", $"Outside: {sent}"],
                    saved.PendingOpens?.Select(open => $"{open.Action}: {string.Join("|", open.Paths)}") ?? []);

                release.Set();
                Assert.True(Ui.WaitUntil(() => loading.IsCompleted));
                Assert.Null(loading.Result);
            });
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }

        using var next = new PlayerHarness(directory: h.Directory);
        next.RestoreQueueState();

        Assert.Equal([a, inList, sent], next.Engine.QueuePaths.ToArray());
        Assert.False(next.Player.IsPlaying);
        Assert.Empty(next.Outputs);
    });

    /// <summary>
    /// Sprawdzenie skończone przed terminem: kolejka wraca przy zamykaniu i zapisuje się po
    /// odfiltrowaniu, tak jak po zwykłym przywróceniu.
    /// </summary>
    [Fact]
    public void PrzywracanieSkonczoneWTerminieZapisujeOdfiltrowanaKolejke() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        QueueStateStore.Save(new QueueState { Paths = [Path.Combine(h.Directory, "nie-ma.wav"), a, b], CurrentIndex = 2, PositionSeconds = 30 }, h.QueueFile);

        WithClosingWindow(h, (_, window) =>
        {
            window.Close();

            var saved = QueueStateStore.Load(h.QueueFile)!;
            Assert.Equal([a, b], saved.Paths.ToArray());
            Assert.Equal(1, saved.CurrentIndex);
            Assert.Equal(30, saved.PositionSeconds);
        });
    });

    /// <summary>
    /// Folder przejrzany przed terminem trafia do zapisanej kolejki i nie zostaje w otwarciach
    /// oczekujących, więc następne uruchomienie nie dokłada go drugi raz.
    /// </summary>
    [Fact]
    public void FolderPrzejrzanyWTerminieNieDublujeSiePrzyNastepnymUruchomieniu() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        var inFolder = h.Track(Path.Combine("album", "b"));

        WithClosingWindow(h, (address, window) =>
        {
            Assert.True(SingleInstance.TryHandOff(address, [folder]));
            Thread.Sleep(200);

            // Przeglądanie rusza w tle jeszcze przed zamknięciem, jak w działającym programie.
            typeof(App).GetMethod("OnPathsHandedOver", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            window.Close();

            var saved = QueueStateStore.Load(h.QueueFile)!;
            Assert.Equal([inFolder], saved.Paths.ToArray());
            Assert.Null(saved.PendingOpens);
        });

        using var next = new PlayerHarness(directory: h.Directory);
        next.Track(Path.Combine("album", "b"));
        next.Player.RestoreQueueState();

        // Czas na ewentualne spóźnione dołożenie z tła.
        next.PumpUntil(() => false, 300);
        Assert.Equal([inFolder], next.Engine.QueuePaths.ToArray());
    });

    /// <summary>
    /// Pliki dokładane przy zamykaniu — przysłane w trakcie albo z folderu przeglądanego jeszcze
    /// w tle — nie uruchamiają odtwarzania ani nie otwierają urządzenia. Zapisana kolejka i bieżący
    /// wpis są takie, jakie ustawiłaby wybrana akcja otwierania plików.
    /// </summary>
    [Theory]
    [InlineData(FileOpenAction.Append, false, false)]
    [InlineData(FileOpenAction.Append, true, false)]
    [InlineData(FileOpenAction.AppendAndPlay, true, false)]
    [InlineData(FileOpenAction.AppendAndPlay, false, true)]
    [InlineData(FileOpenAction.ReplaceAndPlay, true, false)]
    [InlineData(FileOpenAction.ReplaceAndPlay, true, true)]
    public void PlikiDokladanePrzyZamykaniuNieUruchamiajaOdtwarzania(FileOpenAction action, bool existingQueue, bool viaFolder) => Ui.Run(() =>
    {
        var h = new PlayerHarness(settings => settings.FileOpenAction = action);

        var existing = h.Track("x");
        var track = h.Track("a");
        var sent = track;

        if (viaFolder)
        {
            var folder = Path.Combine(h.Directory, "album");
            Directory.CreateDirectory(folder);
            sent = folder;
            track = h.Track(Path.Combine("album", "b"));
        }

        // Kolejka z poprzedniej sesji wraca przy otwarciu okna bez odtwarzania.
        if (existingQueue)
            QueueStateStore.Save(new QueueState { Paths = [existing], CurrentIndex = 0, PositionSeconds = 42 }, h.QueueFile);

        WithClosingWindow(h, (address, window) =>
        {
            Assert.True(h.PumpUntil(() => h.Player.Queue.Count == (existingQueue ? 1 : 0)));
            Assert.True(SingleInstance.TryHandOff(address, [sent]));
            Thread.Sleep(200);

            // Folder zaczyna się przeglądać w tle jeszcze przed zamknięciem, jak w działającym programie.
            if (viaFolder)
                typeof(App).GetMethod("OnPathsHandedOver", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

            var started = false;
            h.Player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsPlaying)) started |= h.Player.IsPlaying;
            };

            window.Close();

            Assert.False(started, "Odtwarzanie ruszyło przy zamykaniu.");
            Assert.False(h.Player.IsPlaying);
            Assert.Empty(h.Outputs);

            var expected = action == FileOpenAction.ReplaceAndPlay || !existingQueue
                ? new[] { track }
                : [existing, track];
            var expectedIndex = action == FileOpenAction.AppendAndPlay ? expected.Length - 1 : 0;

            var saved = QueueStateStore.Load(h.QueueFile)!;
            Assert.Equal(expected, saved.Paths.ToArray());
            Assert.Equal(expectedIndex, saved.CurrentIndex);

            // Nowy bieżący utwór jeszcze nie grał, więc zacznie od początku; samo dopisanie
            // zostawia pozycję poprzedniego.
            var keepsPosition = existingQueue && action == FileOpenAction.Append;
            Assert.Equal(keepsPosition ? 42 : 0, saved.PositionSeconds);
        });
    });

    /// <summary>Okno otwarte ze ścieżką, która nie daje plików, wraca do kolejki z poprzedniej sesji i jej nie kasuje.</summary>
    [Fact]
    public void OknoOtwarteZeSciezkaBezPlikowNieKasujeZapisanejKolejki() => Ui.Run(() =>
    {
        var h = new PlayerHarness();
        var a = h.Track("a");
        var b = h.Track("b");
        QueueStateStore.Save(new QueueState { Paths = [a, b], CurrentIndex = 1, PositionSeconds = 30 }, h.QueueFile);

        var startupPaths = typeof(App).GetProperty(nameof(App.StartupPaths))!;
        startupPaths.SetValue(null, new[] { Path.Combine(h.Directory, "nie-ma.wav") });

        try
        {
            WithClosingWindow(h, (_, window) =>
            {
                Assert.True(h.PumpUntil(() => h.Player.Queue.Count == 2));

                window.Close();

                var saved = QueueStateStore.Load(h.QueueFile)!;
                Assert.Equal([a, b], saved.Paths.ToArray());
                Assert.Equal(1, saved.CurrentIndex);
                Assert.Equal(30, saved.PositionSeconds);
            });
        }
        finally
        {
            startupPaths.SetValue(null, Array.Empty<string>());
        }
    });

    /// <summary>Okno nie pokazuje podpowiedzi dodawania muzyki nad kolejką, która dopiero wraca z poprzedniej sesji.</summary>
    [Fact]
    public void PodpowiedzDodawaniaWOknieCzekaNaKoniecPrzywracania() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();
        var missing = Path.Combine(h.Directory, "nie-ma.wav");
        QueueStateStore.Save(new QueueState { Paths = [missing], CurrentIndex = 0, PositionSeconds = 10 }, h.QueueFile);

        h.PathChecking = path =>
        {
            if (path == missing) release.Wait(TimeSpan.FromSeconds(10));
        };

        try
        {
            WithClosingWindow(h, (_, window) =>
            {
                var prompt = window.AddPrompt;
                Assert.False(prompt.IsVisible);

                release.Set();

                Assert.True(h.PumpUntil(() => h.Player.OpensFinished.IsCompleted));
                Assert.True(prompt.IsVisible);
            });
        }
        finally
        {
            h.PathChecking = null;
            release.Set();
        }
    });

    /// <summary>
    /// Zamyka okno i podaje czas do oddania roli działającej kopii, bo na to czeka następna kopia.
    /// Zwalnianie modelu widoku po zamknięciu czeka z limitem na wątki o niskim priorytecie,
    /// które pod obciążeniem głodują, i nie należy już do tego terminu.
    /// </summary>
    private static TimeSpan CloseUntilRoleReleased(MainWindow window)
    {
        var closing = Stopwatch.StartNew();
        var released = TimeSpan.MaxValue;
        window.Closing += (_, _) => released = closing.Elapsed;

        window.Close();

        return released;
    }

    /// <summary>Okno z nasłuchem pojedynczej instancji; po zakończeniu przywraca stan aplikacji.</summary>
    private static void WithClosingWindow(PlayerHarness h, Action<InstanceAddress, MainWindow> body)
    {
        var directory = TestEnvironment.NewDirectory();
        var address = new InstanceAddress(
            "Cewka.test." + Guid.NewGuid().ToString("N"),
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));

        var mainWindow = typeof(App).GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = mainWindow.GetValue(null);

        var window = new MainWindow(false, h.Player);
        window.Show();
        mainWindow.SetValue(null, window);

        App.Instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(App.Instance);

        try
        {
            typeof(App).GetMethod("AttachSingleInstance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Avalonia.Application.Current, null);

            body(address, window);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            App.ReleaseSingleInstance();
            typeof(App).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
            mainWindow.SetValue(null, previous);
        }
    }
}
