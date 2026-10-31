using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

public sealed class EffectViewModelTests
{
    /// <summary>Suwak w położeniu pośrednim ma wrócić na stopień, nawet gdy wartość się nie zmieniła.</summary>
    [Fact]
    public void ZaokraglenieDoTejSamejWartosciCofaSuwak()
    {
        var effect = new EffectViewModel("Crossfeed", enabled: true, strength: 0.5);
        var raised = new List<string?>();
        effect.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        effect.Strength = 0.52;

        Assert.Equal(0.5, effect.Strength);
        Assert.Contains(nameof(EffectViewModel.Strength), raised);
    }
}
