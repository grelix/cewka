using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Liczby w kroju interfejsu, z cyframi o stałej szerokości, bez kroju o stałej szerokości.</summary>
[Collection(UiCollection.Name)]
public sealed class TypographyTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void MainWindowTexts(Action<List<TextBlock>> check)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
        });
        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();

        try
        {
            h.OpenPaths(Enumerable.Range(0, 3).Select(i => h.Track("t" + i)));
            h.PumpUntil(() => h.Player.IsPlaying);
            h.Player.Pause();
            Settle();

            // Teksty okna korektora i efektów też należą do okna odtwarzacza.
            h.Player.PanelOpen = true;
            Settle();

            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Concat(window.OpenSoundWindow!.GetVisualDescendants().OfType<TextBlock>()).ToList();
            check(texts);
        }
        finally { window.Close(); }
    }

    private static bool IsMonospace(TextBlock text) =>
        text.FontFamily.FamilyNames.Any(n => n.Contains("Mono", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ZadenTekstOknaNieUzywaKrojuOStalejSzerokosci()
    {
        Ui.Run(() =>
        {
            MainWindowTexts(texts =>
                Assert.Empty(texts.Where(IsMonospace).Select(t => t.Text)));

            using var h = new PlayerHarness();
            var settings = new SettingsViewModel(h.Settings, h.Player);
            var window = new SettingsWindow(settings);
            window.Show();

            try
            {
                foreach (var section in settings.Sections)
                {
                    settings.SelectSection(section);
                    Settle();
                    Assert.Empty(window.GetVisualDescendants().OfType<TextBlock>().Where(IsMonospace).Select(t => t.Text));
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LiczbyMajaCyfryOStalejSzerokosci()
    {
        Ui.Run(() =>
        {
            MainWindowTexts(texts =>
            {
                var tabular = texts.Where(t => t.Classes.Contains("tabular")).ToList();
                Assert.NotEmpty(tabular);

                foreach (var text in tabular)
                {
                    text.Text = "11:11";
                    text.Measure(Size.Infinity);
                    var ones = text.DesiredSize.Width;
                    text.Text = "00:00";
                    text.Measure(Size.Infinity);

                    Assert.Equal(ones, text.DesiredSize.Width, 3);
                }
            });
        });
    }
}
