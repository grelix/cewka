using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Pozycja pliku z poprzedniej sesji czeka w wznowieniu, dopóki odtwarzanie pliku jej nie zużyje.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioResumeKeptTests
{
    private static void SaveQueue(PlayerHarness h, List<string> paths, int current, double seconds) =>
        QueueStateStore.Save(new QueueState { Paths = paths, CurrentIndex = current, PositionSeconds = seconds }, h.QueueFile);

    [Fact]
    public void PrzelaczenieNaRadioIPowrotPrzyPauzieNieKasujePozycjiZPoprzedniejSesji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        SaveQueue(h, [h.Track("a"), h.Track("b")], 1, 42);
        h.RestoreQueueState();

        h.Player.SetSource(SourceMode.Radio);
        h.Player.SetSource(SourceMode.Files);
        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.Equal(42, saved.PositionSeconds);
    });
}
