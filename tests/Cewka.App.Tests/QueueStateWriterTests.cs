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
        h.OpenPaths([h.Track("a"), h.Track("b")], replace: false);

        var saving = h.Player.SaveQueueStateInBackground();

        Assert.False(h.Player.QueueSavePending);
        Assert.True(saving.Wait(10000));
        Assert.Equal(2, QueueStateStore.Load(h.QueueFile)!.Paths.Count);
    });

    /// <summary>
    /// Przestawienie utworów jednego pliku z arkusza nie zmienia listy ścieżek. Zapis w tle nie może
    /// go uznać za samą zmianę miejsca odtwarzania, bo kolejka wróciłaby w starej kolejności.
    /// </summary>
    [Fact]
    public async Task PrzestawienieUtworowJednegoPlikuZapisujeListe()
    {
        var writer = new QueueStateWriter(_path);
        var first = new Cewka.Audio.Playback.SheetTrack { SheetPath = "a.cue", Number = 1, Start = 0, End = 75 };
        var second = first with { Number = 2, Start = 75, End = null };

        var state = new QueueState();
        state.SetItems([new("a.wav", first), new("a.wav", second)]);
        writer.SaveNow(state);

        var swapped = new QueueState { CurrentIndex = 1 };
        swapped.SetItems([new("a.wav", second), new("a.wav", first)]);
        await writer.SaveInBackground(swapped);

        var loaded = QueueStateStore.Load(_path)!;
        Assert.Equal([2, 1], loaded.Items().Select(item => item.Sheet!.Number));
    }

    private static QueueState State(int index) => new() { Paths = ["a.wav"], CurrentIndex = index };
}
