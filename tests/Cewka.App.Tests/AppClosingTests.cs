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
    /// czeka na zwolnienie roli; plik przysłany po folderze i tak trafia do zapisanej kolejki.
    /// </summary>
    [Fact]
    public void WolnePrzegladanieFolderuNieWstrzymujeZamykaniaPonadTermin() => Ui.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        var h = new PlayerHarness();

        var track = h.Track("a");
        var folder = Path.Combine(h.Directory, "album");
        Directory.CreateDirectory(folder);
        File.Copy(track, Path.Combine(folder, "b.wav"));

        MainViewModel.FolderExpanding = path =>
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

                var closing = Stopwatch.StartNew();
                window.Close();
                closing.Stop();

                Assert.True(closing.Elapsed < SingleInstance.ClosingGrace + TimeSpan.FromSeconds(1.5),
                    $"Zamykanie trwało {closing.Elapsed.TotalSeconds:0.0} s.");
                Assert.Equal([track], QueueStateStore.Load(h.QueueFile)?.Paths.ToArray() ?? []);
            });
        }
        finally
        {
            MainViewModel.FolderExpanding = null;
            release.Set();
        }
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
            QueueStateStore.Save(new QueueState { Paths = [existing], CurrentIndex = 0 }, h.QueueFile);

        WithClosingWindow(h, (address, window) =>
        {
            Assert.Equal(existingQueue ? 1 : 0, h.Player.Queue.Count);
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
        });
    });

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
