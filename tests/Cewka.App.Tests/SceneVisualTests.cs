using Cewka.App.Models;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wybór wizualizacji pełnego ekranu w modelu widoku: cykl w obie strony i zapis.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneVisualTests
{
    [Fact]
    public void CyklPrzechodziPrzezWszystkieWizualizacjeWObieStrony() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var player = h.Player;
        Assert.Equal(SceneVisual.Disc, player.SceneVisual);

        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Cover, player.SceneVisual);
        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Night, player.SceneVisual);
        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Spectrum, player.SceneVisual);
        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Waveform, player.SceneVisual);
        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Horizon, player.SceneVisual);
        player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, player.SceneVisual);

        player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Horizon, player.SceneVisual);
    });

    [Fact]
    public void ZmianaWizualizacjiPowiadamiaWidok() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var changed = new List<string?>();
        h.Player.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Player.CycleSceneVisual(1);

        Assert.Contains(nameof(h.Player.SceneVisual), changed);
    });

    /// <summary>Wskaźniki wycofano: V przechodzi z Zorzy prosto do Płyty, a ustawienia ich nie oferują.</summary>
    [Fact]
    public void WskaznikowNieMaWCykluAniWUstawieniach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.Effects = EffectsMode.Full);
        h.Player.BatteryProbe = () => false;
        h.Player.RefreshBattery();
        h.Player.PinGpu(true);
        var settings = new Cewka.App.ViewModels.SettingsViewModel(h.Settings, h.Player);

        h.Player.SceneVisual = SceneVisual.Aurora;
        h.Player.CycleSceneVisual(1);
        Assert.Equal(SceneVisual.Disc, h.Player.SceneVisual);
        h.Player.CycleSceneVisual(-1);
        Assert.Equal(SceneVisual.Aurora, h.Player.SceneVisual);

        Assert.False(h.Player.IsVisualUsable(SceneVisual.Meters));
        var offered = settings.SceneVisualChoice.Options.Concat(settings.SceneVisualReactiveChoice.Options);
        Assert.DoesNotContain(offered, o => Equals(o.Value, SceneVisual.Meters));
        settings.Dispose();
    });
}
