using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno ustawień: kategorie główne z podkategoriami w pasku bocznym.</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsStructureTests
{
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void KategorieGlowneMajaPodkategorieWUstalonejKolejnosci() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        var layout = settings.Categories.Select(c => $"{c.Key}: {string.Join(", ", c.Sections.Select(s => s.Key))}");
        Assert.Equal(
        [
            "SectionGeneral: SectionAppearance, SectionLanguage, SectionSystem",
            "SectionAudio: SectionAudioOutput, SectionEqualiser, SectionPlayback",
            "SectionScene: SectionFullScreen, SectionSceneVisuals, SectionSceneTv",
            "SectionRadio: SectionRadioStations, SectionRadioLogos",
            "SectionAbout: SectionAboutInfo, SectionLicences",
        ], layout);

        // Płaska lista to podkategorie po kolei, każda w jednej kategorii, a na starcie wybrany jest wygląd.
        Assert.Equal(settings.Categories.SelectMany(c => c.Sections), settings.Sections);
        Assert.All(settings.Sections, s => Assert.Contains(s, s.Category!.Sections));
        Assert.Equal([settings.Appearance], settings.Sections.Where(s => s.IsSelected));
    });

    /// <summary>Każdy język ma małą flagę kraju macierzystego; „zgodnie z systemem” zostaje bez flagi.</summary>
    [Fact]
    public void KazdyJezykMaFlageAZgodnieZSystememNie() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        var options = settings.Language.Options;
        Assert.Equal("auto", options[0].Value);
        Assert.False(options[0].HasIcon);
        Assert.All(options.Skip(1), option =>
        {
            var flag = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(option.Icon);
            Assert.Equal(new Avalonia.PixelSize(64, 48), flag.PixelSize);
        });
        Assert.Equal(Localisation.Strings.Available.Count, options.Count - 1);
    });

    /// <summary>Pasek boczny ma nagłówek każdej kategorii i przycisk każdej podkategorii; wybór pokazuje jedną stronę.</summary>
    [Fact]
    public void PasekBocznyPokazujeKategorieIPodkategorieAWyborJednaStrone() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            Settle();
            var sidebar = window.FindControl<ItemsControl>("SidebarList")!;
            var headings = sidebar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("sidebar-heading"))
                .Select(t => t.Text).ToList();
            Assert.Equal(settings.Categories.Select(c => c.Label), headings);

            var buttons = sidebar.GetVisualDescendants().OfType<OptionButton>().ToList();
            Assert.Equal(settings.Sections.Count, buttons.Count);

            var content = window.FindControl<ScrollViewer>("ContentArea")!;
            var pages = content.GetVisualDescendants().OfType<Panel>()
                .Where(p => p.Parent is Panel holder && holder.Parent == content && p.Tag is string).ToList();
            Assert.Equal(settings.Sections.Count, pages.Count);

            foreach (var section in settings.Sections)
            {
                settings.SelectSection(section);
                Settle();
                Assert.Single(pages, p => p.IsVisible);
                Assert.True(section.IsSelected);
            }
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Kategoria główna wyraźnie odróżnia się od podkategorii: większe, pogrubione pismo w pełnej barwie
    /// tekstu, a podkategorie są wcięte względem nagłówka. Nagłówek nie jest ucinany w żadnym języku.
    /// </summary>
    [Fact]
    public void KategorieGlowneOdrozniajaSieOdPodkategoriiWKazdymJezyku() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        var previous = Localisation.Strings.Current.CurrentCode;
        window.Show();
        try
        {
            foreach (var code in Localisation.Strings.Available.Select(l => l.Code))
            {
                Localisation.Strings.Current.SetLanguage(code);
                Settle();
                var sidebar = window.FindControl<ItemsControl>("SidebarList")!;
                var headings = sidebar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("sidebar-heading")).ToList();
                var buttons = sidebar.GetVisualDescendants().OfType<OptionButton>().ToList();
                var label = buttons[0].GetVisualDescendants().OfType<TextBlock>().First();

                foreach (var heading in headings)
                {
                    Assert.True(heading.FontSize > label.FontSize, $"{code}: {heading.Text}");
                    Assert.True(heading.FontWeight >= Avalonia.Media.FontWeight.Bold, $"{code}: {heading.Text}");
                    Assert.True(window.TryFindResource("TextBrush", window.ActualThemeVariant, out var text));
                    Assert.Equal(text, heading.Foreground);

                    // Nagłówek mieści się w całości: zmierzony bez ograniczenia nie jest szerszy niż jego miejsce.
                    var probe = new TextBlock { Text = heading.Text, FontSize = heading.FontSize, FontWeight = heading.FontWeight, FontFamily = heading.FontFamily };
                    probe.Measure(Avalonia.Size.Infinity);
                    Assert.True(probe.DesiredSize.Width <= heading.Bounds.Width + 0.5, $"{code}: {heading.Text} ucięty");
                }

                // Nazwy podkategorii zaczynają się wyraźnie na prawo od nagłówka kategorii.
                var headingLeft = Avalonia.VisualExtensions.TranslatePoint(headings[0], default, sidebar)!.Value.X;
                var labelLeft = Avalonia.VisualExtensions.TranslatePoint(label, default, sidebar)!.Value.X;
                Assert.True(labelLeft >= headingLeft + 6, $"{code}: podkategorie bez wcięcia ({labelLeft:0} wobec {headingLeft:0})");
            }
        }
        finally
        {
            Localisation.Strings.Current.SetLanguage(previous);
            window.Close();
        }
    });
}
