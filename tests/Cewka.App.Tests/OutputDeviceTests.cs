using Cewka.App.Models;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zmiana urządzenia wyjściowego i opóźnienia przy urządzeniu, które odmawia otwarcia.</summary>
[Collection(UiCollection.Name)]
public sealed class OutputDeviceTests
{
    [Fact]
    public void NieudaneOtwarcieWybranegoUrzadzeniaPrzechodziNaDomyslne() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);
        Assert.True(h.Player.IsPlaying);

        h.FailingDevice = "Zepsute";
        h.Player.SetOutputDevice("Zepsute");

        Assert.True(h.Player.HasAudioFailure);
        Assert.True(h.Engine.IsDeviceOpen);
        Assert.Null(h.Engine.PreferredDeviceName);
        Assert.True(h.Player.IsPlaying);
    });

    [Fact]
    public void NieudanaZmianaOpoznieniaZostawiaKomunikat() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.OpenPaths([h.Track("a")]);

        h.FailAll = true;
        h.Player.SetOutputLatency(OutputLatency.Low);

        Assert.True(h.Player.HasAudioFailure);
        Assert.False(h.Player.IsPlaying);
        Assert.False(h.Engine.IsDeviceOpen);
    });
}
