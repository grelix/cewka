using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zapis stanu kolejki w tle: najnowszy wygrywa, zapis przy zamknięciu nie zostaje nadpisany.</summary>
[Collection(UiCollection.Name)]
public sealed class QueueStateWriterTests
{
    private readonly string _path = Path.Combine(TestEnvironment.NewDirectory(), "queue.json");

    [Fact]
    public async Task ZKilkuZamowionychZapisowZostajeNajnowszy()
    {
        var writer = new QueueStateWriter(_path);

        Task last = Task.CompletedTask;
        for (var i = 1; i <= 20; i++)
            last = writer.SaveInBackground(State(i));
        await last;

        Assert.Equal(20, QueueStateStore.Load(_path)!.CurrentIndex);
    }

    [Fact]
    public async Task ZapisNatychmiastowyNieZostajeNadpisanyStarszymZTla()
    {
        var writer = new QueueStateWriter(_path);

        var background = writer.SaveInBackground(State(1));
        writer.SaveNow(State(2));
        await background;

        Assert.Equal(2, QueueStateStore.Load(_path)!.CurrentIndex);
    }

    [Fact]
    public void ZapisZamowionyPrzezZegarIdzieWTle() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a"), h.Track("b")], replace: false);

        var saving = h.Player.SaveQueueStateInBackground();

        Assert.False(h.Player.QueueSavePending);
        Assert.True(saving.Wait(10000));
        Assert.Equal(2, QueueStateStore.Load(h.QueueFile)!.Paths.Count);
    });

    private static QueueState State(int index) => new() { Paths = ["a.wav"], CurrentIndex = index };
}
