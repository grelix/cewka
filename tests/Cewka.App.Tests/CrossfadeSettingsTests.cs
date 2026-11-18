using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Ustawienia przenikania utworów: wartości domyślne, zapis, przekazanie do silnika i aktywność wierszy w oknie.</summary>
[Collection(UiCollection.Name)]
public sealed class CrossfadeSettingsTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static SettingsStore Store(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    [Fact]
    public void DomyslniePrzenikanieWylaczonePiecSekundAlbumyBezPrzenikania() => Ui.Run(() =>
    {
        var settings = new AppSettings();
        Assert.False(settings.Crossfade);
        Assert.Equal(5, settings.CrossfadeSeconds);
        Assert.True(settings.AlbumsWithoutCrossfade);

        using var h = new PlayerHarness();
        Assert.Equal(TimeSpan.Zero, h.Engine.Crossfade);
        Assert.True(h.Engine.AlbumsWithoutCrossfade);
    });

    [Theory]
    [InlineData(7, 8)]
    [InlineData(0, 2)]
    [InlineData(-40, 2)]
    [InlineData(999, 12)]
    [InlineData(int.MinValue, 2)]
    [InlineData(3, 3)]
    public void CzasSpozaWyboruDajeNajblizszy(int saved, int expected) => Ui.Run(() =>
    {
        var store = Store($$"""{ "crossfade": true, "crossfadeSeconds": {{saved}} }""");
        Assert.Equal(expected, store.Current.CrossfadeSeconds);
        Assert.True(store.Current.Crossfade);
    });

    [Fact]
    public void UstawieniaZPlikuTrafiajaDoSilnika() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Crossfade = true;
            s.CrossfadeSeconds = 3;
            s.AlbumsWithoutCrossfade = false;
        });

        Assert.Equal(TimeSpan.FromSeconds(3), h.Engine.Crossfade);
        Assert.False(h.Engine.AlbumsWithoutCrossfade);
    });

    [Fact]
    public void ZmianyWOknieUstawienTrafiajaDoSilnikaIZapisu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        Assert.Equal(["2 s", "3 s", "5 s", "8 s", "12 s"], settings.CrossfadeDuration.Options.Select(o => o.Label));
        Assert.Equal(5, settings.CrossfadeDuration.Options.Single(o => o.IsSelected).Value);

        // Czas wybrany przy wyłączonym przenikaniu zostaje zapamiętany, ale silnik nie przenika.
        settings.CrossfadeDuration.Choose(settings.CrossfadeDuration.Options.Single(o => Equals(o.Value, 12)));
        Assert.Equal(12, h.Settings.Current.CrossfadeSeconds);
        Assert.Equal(TimeSpan.Zero, h.Engine.Crossfade);

        settings.Crossfade = true;
        Assert.True(h.Settings.Current.Crossfade);
        Assert.Equal(TimeSpan.FromSeconds(12), h.Engine.Crossfade);

        settings.CrossfadeDuration.Choose(settings.CrossfadeDuration.Options.Single(o => Equals(o.Value, 2)));
        Assert.Equal(TimeSpan.FromSeconds(2), h.Engine.Crossfade);

        settings.AlbumsWithoutCrossfade = false;
        Assert.False(h.Engine.AlbumsWithoutCrossfade);
        Assert.False(h.Settings.Current.AlbumsWithoutCrossfade);

        settings.Crossfade = false;
        Assert.Equal(TimeSpan.Zero, h.Engine.Crossfade);

        var json = JsonSerializer.Serialize(h.Settings.Current.Clone());
        Assert.Contains("\"Crossfade\":false", json);
        Assert.Contains("\"CrossfadeSeconds\":2", json);
        Assert.Contains("\"AlbumsWithoutCrossfade\":false", json);
    });

    /// <summary>Czas i albumy są nieaktywne, dopóki przenikanie jest wyłączone.</summary>
    [Fact]
    public void CzasIAlbumyNieaktywnePrzyWylaczonymPrzenikaniu() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            settings.SelectSection(settings.Playback);
            Settle();

            var duration = window.FindControl<Control>("CrossfadeDurationSetting")!;
            var albums = window.FindControl<Control>("CrossfadeAlbumsSetting")!;
            Assert.True(duration.IsVisible);
            Assert.False(duration.IsEnabled);
            Assert.False(albums.IsEnabled);

            settings.Crossfade = true;
            Settle();
            Assert.True(duration.IsEnabled);
            Assert.True(albums.IsEnabled);
            Assert.True(window.FindControl<Controls.GlassSwitch>("CrossfadeSwitch")!.IsChecked);
        }
        finally { window.Close(); }
    });
}
