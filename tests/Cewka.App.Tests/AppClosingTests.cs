using System.Reflection;
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
}
