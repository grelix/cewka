using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Układ okien w każdym języku, mierzony na prawdziwym rysowaniu. Limity znaków w
/// <see cref="LanguageFileTests"/> są przybliżeniem; tu sprawdza się, czy tekst faktycznie
/// mieści się tam, gdzie stoi, przy najwęższym dozwolonym oknie.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LocalisedLayoutTests
{
    /// <summary>
    /// Języki z katalogu plików. Nie z <see cref="Strings"/>: ta klasa wczytuje polski przy
    /// pierwszym użyciu i przed uruchomieniem Avalonii zostałaby z pustym słownikiem.
    /// </summary>
    public static TheoryData<string> Codes => new(
        System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Languages"), "*.json")
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Order());

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Wykonuje działanie w danym języku i przywraca poprzedni.</summary>
    private static void InLanguage(string code, Action action)
    {
        Ui.Run(() =>
        {
            var previous = Strings.Current.CurrentCode;
            Strings.Current.SetLanguage(code);

            // Słownik naprawdę wczytany: brakujący polski dałby wszędzie [Klucz] i test niczego by nie mierzył.
            Assert.Equal(code, Strings.Current["_code"]);

            try { action(); }
            finally { Strings.Current.SetLanguage(previous); }
        });
    }

    /// <summary>Szerokość, jaką tekst zająłby bez przycinania.</summary>
    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontSize = text.FontSize,
            FontFamily = text.FontFamily,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            LetterSpacing = text.LetterSpacing,
        };

        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void PaskiSegmentowIPrzycinaneNapisyMieszczaSieWNajwezszymOknieUstawien(string code)
    {
        InLanguage(code, () =>
        {
            using var h = new PlayerHarness();
            var settings = new SettingsViewModel(h.Settings, h.Player);
            var window = new SettingsWindow(settings) { Width = 700, Height = 640 };
            window.Show();

            try
            {
                var problems = new List<string>();

                foreach (var section in settings.Sections)
                {
                    settings.SelectSection(section);
                    Settle();

                    foreach (var bar in window.GetVisualDescendants().OfType<SegmentBar>().Where(b => b.IsEffectivelyVisible))
                    {
                        var buttons = bar.GetVisualDescendants().OfType<OptionButton>().ToList();
                        if (buttons.Count == 0) continue;

                        // Obramowanie 1 + 3 px po obu stronach, odstęp 4 px między przyciskami.
                        var needed = buttons.Sum(b => b.DesiredSize.Width) + 4 * (buttons.Count - 1) + 8;

                        if (needed > bar.Bounds.Width + 0.5)
                            problems.Add($"{section.Key}: pasek {string.Join("/", buttons.Select(Label))} potrzebuje {needed:F0} px z {bar.Bounds.Width:F0}");
                    }

                    foreach (var text in window.GetVisualDescendants().OfType<TextBlock>()
                                 .Where(t => t.TextTrimming != TextTrimming.None && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                    {
                        var natural = NaturalWidth(text);

                        if (natural > text.Bounds.Width + 0.5)
                            problems.Add($"{section.Key}: napis \"{text.Text}\" potrzebuje {natural:F0} px z {text.Bounds.Width:F0}");
                    }
                }

                Assert.True(problems.Count == 0, $"{code}: {string.Join("; ", problems.Distinct())}");
            }
            finally { window.Close(); }
        });
    }

    private static string Label(OptionButton button) =>
        button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "?";

    [Theory]
    [MemberData(nameof(Codes))]
    public void PodpisPreampuNieOdbieraMiejscaPasmomKorektoraWNajwezszymOknie(string code)
    {
        InLanguage(code, () =>
        {
            App.Settings.Current.Window = null;
            using var h = new PlayerHarness(s =>
            {
                s.PanelOpen = true;
                s.QueueOpen = false;
                s.WindowControls = WindowControlsPosition.Right;
            });

            var window = new MainWindow(false, h.Player) { Width = 760, Height = 760 };
            window.Show();

            try
            {
                Settle();

                var faders = window.GetVisualDescendants().OfType<EqFader>().Where(f => f.IsEffectivelyVisible).ToList();
                Assert.Equal(11, faders.Count);

                var narrow = faders.Where(f => f.Bounds.Width < 26 - 0.5).Select(f => $"{f.Bounds.Width:F1}").ToArray();
                Assert.True(narrow.Length == 0, $"{code}: suwaki węższe niż 26 px: {string.Join(", ", narrow)}");

                var caption = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == Strings.Current["PreampName"]);
                Assert.True(caption.Bounds.Width > 0, $"{code}: podpis preampu nie ma szerokości");

                // Podpis zawija się tylko na granicy wyrazów: najdłuższy wyraz musi się zmieścić w całości.
                var longestWord = (caption.Text ?? "").Split(' ', '-').Max(word => word.Length);
                Assert.True(
                    caption.Bounds.Width >= NaturalWidth(new TextBlock
                    {
                        Text = (caption.Text ?? "").Split(' ').OrderByDescending(word => word.Length).First(),
                        FontSize = caption.FontSize,
                        FontFamily = caption.FontFamily,
                    }) - 0.5,
                    $"{code}: wyraz „{caption.Text}” ({longestWord} znaków) łamie się w środku");
            }
            finally { window.Close(); }
        });
    }
}
