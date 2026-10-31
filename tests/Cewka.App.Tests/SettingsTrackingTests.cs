using Cewka.App.Localisation;
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

    /// <summary>Uwaga o tłumaczeniu maszynowym nie dotyczy polskiego, w którym interfejs powstał.</summary>
    [Fact]
    public void UwagaOTlumaczeniuMaszynowymNieDotyczyPolskiego() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var previous = Strings.Current.CurrentCode;

        try
        {
            Strings.Current.SetLanguage("pl");
            Assert.False(settings.ShowTranslationNote);

            Strings.Current.SetLanguage("de");
            Assert.True(settings.ShowTranslationNote);
            Assert.Contains(nameof(SettingsViewModel.ShowTranslationNote), raised);
        }
        finally
        {
            Strings.Current.SetLanguage(previous);
        }
    });

    /// <summary>Poziomy głośności mają prawdziwy minus, jak liczby w podpowiedzi pod paskiem.</summary>
    [Fact]
    public void EtykietyPoziomowGlosnosciMajaPrawdziwyMinus() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        var labels = settings.Loudness.Options.Select(option => option.Label).ToArray();

        Assert.Equal(["−23 LUFS", "−18 LUFS", "−14 LUFS"], labels);
    });
}
