using Cewka.App.Models;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno ustawień odbija zmiany zrobione w oknie głównym.</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsTrackingTests
{
    [Fact]
    public void PrzelacznikiZOknaGlownegoWidacWUstawieniach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Player.NormalisationEnabled = !h.Player.NormalisationEnabled;
        h.Player.LimiterEnabled = !h.Player.LimiterEnabled;

        Assert.Contains(nameof(SettingsViewModel.NormalisationEnabled), raised);
        Assert.Contains(nameof(SettingsViewModel.LimiterEnabled), raised);
        Assert.Equal(h.Player.NormalisationEnabled, settings.NormalisationEnabled);
    });

    [Fact]
    public void OtwarcieUrzadzeniaOdswiezaOpisWUstawieniach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Player.OpenPaths([h.Track("a")]);

        Assert.Contains(nameof(SettingsViewModel.ActiveDeviceName), raised);
        Assert.Contains(nameof(SettingsViewModel.LatencyDescription), raised);
    });

    [Fact]
    public void MotywPrzelaczonyPozaOknemUstawienMaZnacznik() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);
        var previous = App.Theme.Mode;

        try
        {
            App.Theme.SetMode(ThemeMode.Light);
            Assert.True(settings.Theme.Options.Single(o => Equals(o.Value, ThemeMode.Light)).IsSelected);

            App.Theme.SetMode(ThemeMode.Dark);
            Assert.True(settings.Theme.Options.Single(o => Equals(o.Value, ThemeMode.Dark)).IsSelected);
        }
        finally
        {
            App.Theme.SetMode(previous);
        }
    });
}
