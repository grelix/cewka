using System.Reflection;
using Cewka.App.Services;
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
}
