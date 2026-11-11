using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Zmiana języka przy otwartych oknach. Teksty z <c>{l:Translate}</c> przechodzą na nowy język
/// bez ponownego otwierania okna: treść przycisku, podpowiedź, tytuł okna i nazwa dla czytnika
/// ekranu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LanguageSwitchTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Wykonuje test na wątku interfejsu i przywraca język sprzed testu.</summary>
    private static void KeepingLanguage(Action action) => Ui.Run(() =>
    {
        var previous = Strings.Current.CurrentCode;
        try { action(); }
        finally { Strings.Current.SetLanguage(previous); }
    });

    /// <summary>Nazwa, jaką dostaje czytnik ekranu.</summary>
    private static string? SpokenName(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetName();

    /// <summary>Przycisk, którego treścią jest dany tekst w bieżącym języku.</summary>
    private static Button ButtonWithContent(Window window, string key) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, Strings.Current[key]));

    /// <summary>Tekst klucza ma się zmienić, inaczej test niczego by nie sprawdzał.</summary>
    private static void AssertTranslated(string polish, string key) =>
        Assert.NotEqual(polish, Strings.Current[key]);

    [Fact]
    public void OknoGlowneZmieniaJezykTresciPodpowiedziINazwPrzyciskow() => KeepingLanguage(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetLanguage("pl");

        var window = new MainWindow(false, h.Player);
        window.Show();

        try
        {
            Settle();

            var settingsButton = window.FindControl<Button>("SettingsButton")!;
            var fullScreen = window.FindControl<Button>("FullScreenButton")!;
            var openFiles = ButtonWithContent(window, "OpenFiles");

            var polishSettings = Strings.Current["TooltipSettings"];
            var polishFullScreen = Strings.Current["TooltipFullScreen"];
            var polishOpenFiles = Strings.Current["OpenFiles"];
            Assert.Equal(polishSettings, ToolTip.GetTip(settingsButton));
            Assert.Equal(polishSettings, AutomationProperties.GetName(settingsButton));

            // Ta sama droga, którą idzie wybór języka w oknie ustawień.
            h.Player.SetLanguage("fr");
            Settle();

            Assert.Equal("fr", Strings.Current.CurrentCode);
            AssertTranslated(polishSettings, "TooltipSettings");
            AssertTranslated(polishFullScreen, "TooltipFullScreen");
            AssertTranslated(polishOpenFiles, "OpenFiles");

            Assert.Equal(Strings.Current["OpenFiles"], openFiles.Content);
            Assert.Equal(Strings.Current["TooltipSettings"], ToolTip.GetTip(settingsButton));
            Assert.Equal(Strings.Current["TooltipSettings"], AutomationProperties.GetName(settingsButton));
            Assert.Equal(Strings.Current["TooltipSettings"], SpokenName(settingsButton));
            Assert.Equal(Strings.Current["TooltipFullScreen"], ToolTip.GetTip(fullScreen));
            Assert.Equal(Strings.Current["TooltipFullScreen"], SpokenName(fullScreen));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OknoUstawienZmieniaJezykTytuluTresciPodpowiedziINazw() => KeepingLanguage(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetLanguage("pl");

        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();

        try
        {
            settings.SelectSection(settings.Audio);
            Settle();

            var refresh = ButtonWithContent(window, "DeviceRefresh");
            var normalisation = window.GetVisualDescendants().OfType<GlassSwitch>()
                .Single(s => AutomationProperties.GetName(s) == Strings.Current["OptionNormalisation"]);
            var close = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "SquareClose" && b.IsEffectivelyVisible);

            var polishTitle = Strings.Current["SettingsWindowTitle"];
            var polishRefresh = Strings.Current["DeviceRefresh"];
            var polishNormalisation = Strings.Current["OptionNormalisation"];
            var polishClose = Strings.Current["TooltipClose"];
            Assert.Equal(polishTitle, window.Title);
            Assert.Equal(polishClose, ToolTip.GetTip(close));

            // Wybór języka tak, jak robi to użytkownik: przyciskiem paska języków.
            settings.Language.Choose(settings.Language.Options.Single(o => Equals(o.Value, "fr")));
            Settle();

            Assert.Equal("fr", Strings.Current.CurrentCode);
            AssertTranslated(polishTitle, "SettingsWindowTitle");
            AssertTranslated(polishRefresh, "DeviceRefresh");
            AssertTranslated(polishNormalisation, "OptionNormalisation");
            AssertTranslated(polishClose, "TooltipClose");

            Assert.Equal(Strings.Current["SettingsWindowTitle"], window.Title);
            Assert.Equal(Strings.Current["DeviceRefresh"], refresh.Content);
            Assert.Equal(Strings.Current["OptionNormalisation"], AutomationProperties.GetName(normalisation));
            Assert.Equal(Strings.Current["OptionNormalisation"], SpokenName(normalisation));
            Assert.Equal(Strings.Current["TooltipClose"], ToolTip.GetTip(close));
            Assert.Equal(Strings.Current["TooltipClose"], SpokenName(close));
        }
        finally { window.Close(); }
    });

    /// <summary>Dwa przyciski „Odśwież listę” w różnych sekcjach mówią czytnikowi, którą listę odświeżają.</summary>
    [Fact]
    public void PrzyciskiOdswiezaniaMajaRozneNazwyDlaCzytnika() => KeepingLanguage(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetLanguage("pl");
        var window = new SettingsWindow(new SettingsViewModel(h.Settings, h.Player));
        window.Show();

        try
        {
            Settle();
            var names = window.GetVisualDescendants().OfType<Button>()
                .Where(b => Equals(b.Content, Strings.Current["DeviceRefresh"]))
                .Select(SpokenName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(
                new[] { Strings.Current["DeviceRefreshName"], Strings.Current["ScreenRefreshName"] }.OrderBy(n => n, StringComparer.Ordinal),
                names);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Okno główne i otwarte obok niego, niemodalne okno ustawień: język wybrany w ustawieniach
    /// przełącza od razu treść przycisków, podpowiedzi i nazwy dla czytnika w oknie głównym.
    /// </summary>
    [Fact]
    public void JezykWybranyWUstawieniachZmieniaTekstyOtwartegoOknaGlownego() => KeepingLanguage(() =>
    {
        using var h = new PlayerHarness();
        h.Player.SetLanguage("pl");

        var window = new MainWindow(false, h.Player);
        window.Show();

        try
        {
            Settle();

            var settingsButton = window.FindControl<Button>("SettingsButton")!;
            var fullScreen = window.FindControl<Button>("FullScreenButton")!;
            var openFiles = ButtonWithContent(window, "OpenFiles");

            var polishSettings = Strings.Current["TooltipSettings"];
            var polishFullScreen = Strings.Current["TooltipFullScreen"];
            var polishOpenFiles = Strings.Current["OpenFiles"];

            // Ustawienia otwierane tak jak przyciskiem: okno niemodalne, należące do okna głównego.
            window.OpenSettings();
            Settle();
            var settingsWindow = Assert.Single(window.OwnedWindows.OfType<SettingsWindow>());
            Assert.True(settingsWindow.IsVisible);
            var settings = Assert.IsType<SettingsViewModel>(settingsWindow.DataContext);

            settings.Language.Choose(settings.Language.Options.Single(o => Equals(o.Value, "fr")));
            Settle();

            Assert.Equal("fr", Strings.Current.CurrentCode);
            AssertTranslated(polishSettings, "TooltipSettings");
            AssertTranslated(polishFullScreen, "TooltipFullScreen");
            AssertTranslated(polishOpenFiles, "OpenFiles");

            Assert.Equal(Strings.Current["OpenFiles"], openFiles.Content);
            Assert.Equal(Strings.Current["TooltipSettings"], ToolTip.GetTip(settingsButton));
            Assert.Equal(Strings.Current["TooltipSettings"], AutomationProperties.GetName(settingsButton));
            Assert.Equal(Strings.Current["TooltipFullScreen"], ToolTip.GetTip(fullScreen));
            Assert.Equal(Strings.Current["TooltipFullScreen"], AutomationProperties.GetName(fullScreen));
            Assert.Equal(Strings.Current["TooltipFullScreen"], SpokenName(fullScreen));
        }
        finally { window.Close(); }
    });
}
