using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Po zmianie języka w otwartych oknach nie zostaje żaden tekst w poprzednim języku. Wiązania
/// <c>{l:Translate}</c> odświeżają się same, ale tekst złożony w kodzie zmienia się tylko wtedy,
/// gdy model widoku zgłosi jego zmianę, a o takim zgłoszeniu łatwo zapomnieć.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LanguageSwitchSweepTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Languages", code + ".json")))!;

    /// <summary>
    /// Wzorce tekstów, które istnieją tylko w języku wyjściowym: całe wartości słownika, a przy
    /// wartościach z miejscami na dane także ich stałe części. Krótkie i wspólne dla obu języków
    /// pomijane, bo niczego nie dowodzą.
    /// </summary>
    private static List<(string Key, Regex Pattern)> StalePatterns(string from, string to)
    {
        var source = Load(from);
        var target = Load(to);
        var targetValues = target.Values.ToHashSet(StringComparer.Ordinal);
        var patterns = new List<(string, Regex)>();

        foreach (var (key, value) in source)
        {
            if (key.StartsWith('_') || value.Length < 6) continue;
            if (!target.TryGetValue(key, out var translated) || translated == value) continue;
            if (targetValues.Contains(value)) continue;

            var parts = Regex.Split(value, @"\{\d+\}").Select(Regex.Escape).ToArray();
            if (parts.Sum(part => part.Length) < 6) continue;

            patterns.Add((key, new Regex("^" + string.Join(".*", parts) + "$", RegexOptions.Singleline)));
        }

        return patterns;
    }

    private static IEnumerable<string> ShownTexts(Window window)
    {
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is not Control { IsEffectivelyVisible: true } control) continue;

            switch (control)
            {
                case TextBlock { Text: { Length: > 0 } text }: yield return text; break;
                case TextBox { PlaceholderText: string { Length: > 0 } placeholder }: yield return placeholder; break;
            }

            if (ToolTip.GetTip(control) is string { Length: > 0 } tip) yield return tip;

            // Nazwy i opisy dla czytnika ekranu też są tekstem interfejsu.
            if (Avalonia.Automation.AutomationProperties.GetName(control) is { Length: > 0 } name) yield return name;
            if (Avalonia.Automation.AutomationProperties.GetHelpText(control) is { Length: > 0 } help) yield return help;
        }
    }

    private static List<string> Stale(Window window, List<(string Key, Regex Pattern)> patterns, string where)
    {
        var found = new List<string>();

        foreach (var text in ShownTexts(window).Distinct())
        foreach (var (key, pattern) in patterns)
        {
            if (!pattern.IsMatch(text)) continue;

            found.Add($"{where}: „{text}” ({key})");
            break;
        }

        return found;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoZmianieJezykaWOknachNieZostajaTekstyPoprzedniego(bool radio) => Ui.Run(() =>
    {
        var previous = Strings.Current.CurrentCode;
        App.Settings.Current.Window = null;
        App.Settings.Current.SettingsWindow = null;
        App.Settings.Current.SoundWindow = null;

        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
            s.RadioEnabled = true;
            s.RadioPrivacyAcknowledged = true;
            s.RadioLogos = false;
            if (radio) s.Source = SourceMode.Radio;
        });
        h.Player.SetLanguage("pl");

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760, AttachDesktopIntegration = false };
        window.Show();

        try
        {
            h.OpenPaths(Enumerable.Range(0, 4).Select(i => h.Track("t" + i)), replace: false);
            Settle();

            // Komunikat chwilowy (tu: o plikach dodanych w trybie radia) znika sam po kilku sekundach
            // i nie jest tłumaczony w locie, więc nie należy do tego sprawdzianu.
            h.Player.ShowNotice(string.Empty);

            window.OpenSettings();
            h.Player.PanelOpen = true;

            // Okienko toru sygnału składa teksty w kodzie, więc też musi pójść za językiem.
            h.Player.IsSignalPathOpen = true;
            Settle();

            var settingsWindow = Assert.Single(window.OwnedWindows.OfType<SettingsWindow>());
            var settings = Assert.IsType<SettingsViewModel>(settingsWindow.DataContext);

            // Każda sekcja pokazana raz po polsku, żeby jej teksty istniały przed zmianą języka.
            foreach (var section in settings.Sections)
            {
                settings.SelectSection(section);
                Settle();
            }

            settings.Language.Choose(settings.Language.Options.Single(o => Equals(o.Value, "en")));
            Settle();
            Assert.Equal("en", Strings.Current.CurrentCode);

            var patterns = StalePatterns("pl", "en");
            var stale = new List<string>();

            foreach (var section in settings.Sections)
            {
                settings.SelectSection(section);
                Settle();
                stale.AddRange(Stale(settingsWindow, patterns, "Ustawienia/" + section.Key));
            }

            Assert.True(window.SignalPathPopup.IsOpen);
            stale.AddRange(Stale(window, patterns, "okno główne"));
            foreach (var owned in window.OwnedWindows.Where(w => w is not SettingsWindow))
                stale.AddRange(Stale(owned, patterns, owned.GetType().Name));

            // Drugie źródło pokazane dopiero po zmianie języka też nie może mieć starych tekstów.
            h.Player.SetSource(radio ? SourceMode.Files : SourceMode.Radio);
            Settle();
            stale.AddRange(Stale(window, patterns, "okno główne po zmianie źródła"));

            Assert.True(stale.Count == 0, string.Join("\n", stale.Distinct()));
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList()) owned.Close();
            window.Close();
            Strings.Current.SetLanguage(previous);
        }
    });

    /// <summary>Rozmiar pamięci podręcznej ma separator dziesiętny języka interfejsu.</summary>
    [Fact]
    public void RozmiarMaSeparatorJezykaInterfejsu() => Ui.Run(() =>
    {
        var previous = Strings.Current.CurrentCode;

        try
        {
            Strings.Current.SetLanguage("pl");
            Assert.Equal("43,4 MB", SettingsViewModel.FormatSize(45_500_000));
            Assert.Equal("1,5 KB", SettingsViewModel.FormatSize(1536));
            Assert.Equal("900 B", SettingsViewModel.FormatSize(900));

            Strings.Current.SetLanguage("en");
            Assert.Equal("43.4 MB", SettingsViewModel.FormatSize(45_500_000));
        }
        finally { Strings.Current.SetLanguage(previous); }
    });
}
