using Cewka.App.Models;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

[Collection(UiCollection.Name)]
public sealed class MotionTests
{
    [Fact]
    public void TrybAutomatycznyOgraniczaRuchNaProsbeSystemu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness(s => s.Effects = EffectsMode.Auto);

            h.Player.AutomaticReduction = () => true;
            h.Player.UpdateEffects();
            Assert.True(h.Player.ReducedEffects);

            h.Player.AutomaticReduction = () => false;
            h.Player.UpdateEffects();
            Assert.False(h.Player.ReducedEffects);
        });
    }

    [Fact]
    public void TrybyJawneNiezalezneOdSystemu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness(s => s.Effects = EffectsMode.Full);
            h.Player.AutomaticReduction = () => true;

            h.Player.UpdateEffects();
            Assert.False(h.Player.ReducedEffects);

            h.Player.SetEffects(EffectsMode.Reduced);
            Assert.True(h.Player.ReducedEffects);
        });
    }

    [Fact]
    public void OdczytUstawieniaAnimacjiSystemuNieZawodzi()
    {
        var first = SystemAnimations.Enabled;
        Assert.Equal(first, SystemAnimations.Enabled);
    }
}
