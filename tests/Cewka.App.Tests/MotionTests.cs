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
    public void OdczytWTleOddajeOstatniaZnanaWartoscBezCzekania()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var queued = new List<Action>();
        bool? answer = false;
        var reads = 0;

        var value = new PolledValue(true, () => { reads++; return answer; }, TimeSpan.FromSeconds(5),
                                    () => now, queued.Add);

        // Pierwsze pytanie zleca odczyt i od razu oddaje wartość początkową.
        Assert.True(value.Value);
        Assert.Single(queued);

        // Odczyt w toku nie zleca drugiego.
        Assert.True(value.Value);
        Assert.Single(queued);

        queued[0]();
        Assert.False(value.Value);
        Assert.Single(queued);

        // Kolejny odczyt dopiero po upływie odstępu.
        now = now.AddSeconds(5);
        answer = null;
        Assert.False(value.Value);
        Assert.Equal(2, queued.Count);

        // Brak odpowiedzi zostawia ostatnią znaną wartość.
        queued[1]();
        Assert.False(value.Value);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void BladOdczytuZostawiaWartoscIPozwalaPonowic()
    {
        var now = DateTime.UtcNow;
        var queued = new List<Action>();
        var value = new PolledValue(false, () => throw new DllNotFoundException(), TimeSpan.FromSeconds(5),
                                    () => now, queued.Add);

        Assert.False(value.Value);
        queued[0]();
        Assert.False(value.Value);

        now = now.AddSeconds(6);
        _ = value.Value;
        Assert.Equal(2, queued.Count);
    }
}
