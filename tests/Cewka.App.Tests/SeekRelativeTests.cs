using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Przewinięcie względne: przesunięcie z zewnątrz (panel multimediów) może być dowolną liczbą,
/// a wyjątek na wątku interfejsu kończy program.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SeekRelativeTests
{
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(922337203685.0)]
    [InlineData(922337203685477.0)]
    public void SkrajnePrzesuniecieWprzodDochodziDoKonca(double seconds) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a", 60), h.Track("b", 60)]);
        Assert.True(h.PumpUntil(() => h.Player.Title == "a"));
        h.Player.SeekRelative(5);
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position >= TimeSpan.FromSeconds(4)));

        h.Player.SeekRelative(seconds);

        Assert.True(h.PumpUntil(() => h.Engine.CurrentIndex == 1 || h.Engine.Position >= TimeSpan.FromSeconds(59)),
            $"wpis {h.Engine.CurrentIndex}, pozycja {h.Engine.Position}");
    });

    [Theory]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MinValue)]
    [InlineData(-922337203685477.0)]
    public void SkrajnePrzesuniecieWsteczDochodziDoPoczatku(double seconds) => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a", 60)]);
        Assert.True(h.PumpUntil(() => h.Player.Title == "a"));
        h.Player.SeekRelative(20);
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position >= TimeSpan.FromSeconds(19)));

        h.Player.SeekRelative(seconds);

        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position < TimeSpan.FromSeconds(5)), $"pozycja {h.Engine.Position}");
    });

    [Fact]
    public void NieLiczbaNieZmieniaPozycji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.OpenPaths([h.Track("a", 60)]);
        Assert.True(h.PumpUntil(() => h.Player.Title == "a"));
        h.Player.SeekRelative(20);
        Assert.True(h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position >= TimeSpan.FromSeconds(19)));

        h.Player.SeekRelative(double.NaN);

        Assert.False(h.PumpUntil(() => h.Engine.Position < TimeSpan.FromSeconds(19) || h.Engine.Position > TimeSpan.FromSeconds(50), 300));
    });
}
