using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Zapis kolejki w trybie radia nosi pozycję pliku, nie czas słuchania stacji.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioFilePlaceSaveTests
{
    [Fact]
    public void ZapisWRadiuZachowujePozycjePlikuNieCzasSluchaniaStacji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a", 300), h.Track("b", 300)]);
        h.Player.PlayItem(h.Player.Queue[1]);
        Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));
        h.Engine.PlayIndex(1, TimeSpan.FromSeconds(100));
        Assert.True(h.PumpUntil(() => h.Engine.Position.TotalSeconds >= 99));
        var expected = h.Engine.Position.TotalSeconds;

        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(h.Player.Radio.Rows.First().Station);
        Assert.True(h.Engine.IsLive);
        for (var i = 0; i < 30; i++) h.PumpUntil(() => false, 20);
        h.Player.SaveQueueState();

        var saved = QueueStateStore.Load(h.QueueFile)!;
        Assert.Equal(1, saved.CurrentIndex);
        Assert.InRange(saved.PositionSeconds, expected - 1, expected + 1);
    });
}
