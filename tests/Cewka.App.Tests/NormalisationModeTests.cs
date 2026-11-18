using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Tryb normalizacji: ustawienie, jego zapis, wiersz w karcie „Przetwarzanie” i działanie na grany utwór.</summary>
[Collection(UiCollection.Name)]
public sealed class NormalisationModeTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- ustawienie ----------

    [Theory]
    [InlineData(NormalisationMode.Track, NormalisationScope.Track)]
    [InlineData(NormalisationMode.Album, NormalisationScope.Album)]
    [InlineData(NormalisationMode.Auto, NormalisationScope.Auto)]
    public void TrybOdpowiadaZakresowiWarstwyDzwieku(NormalisationMode mode, NormalisationScope expected) =>
        Assert.Equal(expected, AudioPreferences.Scope(mode));

    /// <summary>Domyślnie automatyczny, także w pliku ustawień sprzed tego pola.</summary>
    [Fact]
    public void DomyslnieAutomatycznyTakzeWStarszymPliku()
    {
        Assert.Equal(NormalisationMode.Auto, new AppSettings().NormalisationMode);
        Assert.Equal(NormalisationMode.Auto, Store("""{ "theme": "Dark", "normalisationEnabled": true }""").Current.NormalisationMode);
    }

    [Fact]
    public void TrybPrzechodziZapisIOdczyt()
    {
        var format = SettingsJsonContext.Default.AppSettings;
        var loaded = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new AppSettings { NormalisationMode = NormalisationMode.Album }, format), format);

        Assert.Equal(NormalisationMode.Album, loaded!.NormalisationMode);
    }

    /// <summary>Wartość spoza wyliczenia z ręcznie poprawionego pliku wraca do domyślnej.</summary>
    [Fact]
    public void WartoscSpozaWyliczeniaWracaDoAutomatycznego() =>
        Assert.Equal(NormalisationMode.Auto, Store("""{ "normalisationMode": 7 }""").Current.NormalisationMode);

    [Fact]
    public void ZapisanyTrybTrafiaDoUslugiGlosnosci() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.NormalisationMode = NormalisationMode.Track);
        Assert.Equal(NormalisationScope.Track, h.Loudness.Scope);
    });

    // ---------- okno ustawień ----------

    /// <summary>Wiersz stoi tuż pod „Poziom docelowy”, ma zaznaczony zapisany tryb i jest nieaktywny bez normalizacji.</summary>
    [Fact]
    public void WierszPodPoziomemDocelowymNieaktywnyBezNormalizacji() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.NormalisationMode = NormalisationMode.Album);
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();

        try
        {
            settings.SelectSection(settings.Processing);
            Settle();

            var bars = window.GetVisualDescendants().OfType<SegmentBar>().ToList();
            var mode = bars.Single(bar => ReferenceEquals(bar.Group, settings.NormalisationModeChoice));
            var target = bars.Single(bar => ReferenceEquals(bar.Group, settings.Loudness));

            var row = (Control)mode.Parent!;
            var targetRow = (Control)target.Parent!;
            var card = (Panel)row.Parent!;
            Assert.Same(card, targetRow.Parent);
            Assert.Equal(card.Children.IndexOf(targetRow) + 1, card.Children.IndexOf(row));

            Assert.True(mode.IsEffectivelyVisible);
            Assert.Equal(NormalisationMode.Album,
                settings.NormalisationModeChoice.Options.Single(option => option.IsSelected).Value);

            Assert.True(mode.IsEffectivelyEnabled);
            settings.NormalisationEnabled = false;
            Settle();
            Assert.False(mode.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    });

    // ---------- działanie ----------

    /// <summary>
    /// Zmiana trybu działa od razu na grany utwór: wzmocnienie albumu z pomiarów obu utworów
    /// zastępuje wzmocnienie utworu bez czekania na następny utwór, a powrót przywraca utworowe.
    /// </summary>
    [Fact]
    public void ZmianaTrybuDzialaOdRazuNaGranyUtwor() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.NormalisationMode = NormalisationMode.Track);
        var a = h.TaggedTrack("a", "Pierwszy", "Zespół", "Album");
        var b = h.TaggedTrack("b", "Drugi", "Zespół", "Album");

        h.Measurement = (path, _) => new LoudnessMeasurement(path == a ? -12 : -24, 0.5);
        var measured = 0;
        h.Loudness.Measured += (_, _) => Interlocked.Increment(ref measured);

        h.Loudness.RequestEnvelope(a);
        h.Loudness.RequestEnvelope(b);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref measured) == 2, TimeSpan.FromSeconds(10)), "pomiary się nie skończyły");

        h.OpenPaths([a, b]);
        Assert.True(h.PumpUntil(() => h.Engine.CurrentIndex == 0 && h.Engine.Position > TimeSpan.Zero));
        Assert.Equal(-6, h.Engine.Graph.Normalisation.TargetDecibels, 6);

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.NormalisationModeChoice.Choose(
            settings.NormalisationModeChoice.Options.Single(option => Equals(option.Value, NormalisationMode.Album)));

        var album = 10 * Math.Log10((Math.Pow(10, -1.2) + Math.Pow(10, -2.4)) / 2);
        Assert.Equal(NormalisationMode.Album, h.Settings.Current.NormalisationMode);
        Assert.Equal(NormalisationScope.Album, h.Loudness.Scope);
        Assert.Equal(-18 - album, h.Engine.Graph.Normalisation.TargetDecibels, 6);

        settings.NormalisationModeChoice.Choose(
            settings.NormalisationModeChoice.Options.Single(option => Equals(option.Value, NormalisationMode.Track)));
        Assert.Equal(-6, h.Engine.Graph.Normalisation.TargetDecibels, 6);
    });

    private static SettingsStore Store(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }
}
