using Cewka.App.Models;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Głośność, wyciszenie i zerowanie korektora w modelu widoku okna głównego.</summary>
[Collection(UiCollection.Name)]
public sealed class VolumeTests
{
    [Fact]
    public void SuwakPrzechodziPrzezKrzywaPercepcyjna() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();

        h.Player.Volume = 0.5;

        Assert.Equal(VolumeCurve.ToGain(0.5), h.Engine.Graph.Volume.Target, precision: 5);
        Assert.Equal(0.5, h.Player.Volume);
        Assert.Equal(0.5, h.Settings.Current.Volume);
    });

    [Fact]
    public void WyciszenieNieZmieniaPoziomu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.Volume = 0.8;

        h.Player.ToggleMute();

        Assert.True(h.Player.IsMuted);
        Assert.Equal(0, h.Engine.Graph.Volume.Target);
        Assert.Equal(0.8, h.Player.Volume);
        Assert.True(h.Settings.Current.IsMuted);

        h.Player.ToggleMute();

        Assert.False(h.Player.IsMuted);
        Assert.Equal(VolumeCurve.ToGain(0.8), h.Engine.Graph.Volume.Target, precision: 5);
    });

    [Fact]
    public void RuchSuwakaPrzywracaDzwiek() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.ToggleMute();

        h.Player.AdjustVolume(0.05);

        Assert.False(h.Player.IsMuted);
        Assert.True(h.Engine.Graph.Volume.Target > 0);
    });

    [Fact]
    public void WyciszeniePrzetrwaUruchomienie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.IsMuted = true;
            s.Volume = 0.6;
        });

        Assert.True(h.Player.IsMuted);
        Assert.Equal(0, h.Engine.Graph.Volume.Target);
        Assert.Equal(0.6, h.Player.Volume);
    });

    [Fact]
    public void PoziomWProcentachWNazwie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        h.Player.Volume = 0.45;

        Assert.Contains("45", h.Player.VolumeLevelText);
    });

    [Fact]
    public void WyzerowanieKorektoraDostepneTylkoPrzyZmianach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        Assert.False(h.Player.CanResetEqualiser);

        h.Player.Bands[3].Gain = 4;
        h.Player.Preamp.Gain = -2;
        Assert.True(h.Player.CanResetEqualiser);

        h.Player.ResetEqualiser();

        Assert.False(h.Player.CanResetEqualiser);
        Assert.All(h.Player.Bands, band => Assert.Equal(0, band.Gain));
        Assert.Equal(0, h.Player.Preamp.Gain);
        Assert.All(h.Settings.Current.EqualiserGains, gain => Assert.Equal(0, gain));
        Assert.Equal(0, h.Settings.Current.Preamp);
    });
}
