using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno ustawień dla czytnika ekranu i lista skrótów w zakładce „O programie”.</summary>
[Collection(UiCollection.Name)]
public sealed class SettingsAccessibilityTests
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
    public void KazdaKontrolkaZFokusemWKazdejSekcjiMaNazwe()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var settings = new SettingsViewModel(h.Settings, h.Player);
            var window = new SettingsWindow(settings);
            window.Show();

            try
            {
                var unnamed = new List<string>();

                foreach (var section in settings.Sections)
                {
                    settings.SelectSection(section);
                    Settle();

                    unnamed.AddRange(window.GetVisualDescendants().OfType<InputElement>()
                        .Where(e => e.Focusable && e.IsEffectivelyVisible && e is Control and not ScrollViewer)
                        .Where(e => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement((Control)e).GetName()))
                        .Select(e => $"{section.Key}: {e.GetType().Name}"));
                }

                Assert.True(unnamed.Count == 0, string.Join(", ", unnamed.Distinct()));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OpcjaPaskaSegmentowPodajeCzyJestWybrana()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var settings = new SettingsViewModel(h.Settings, h.Player);
            var window = new SettingsWindow(settings);
            window.Show();

            try
            {
                settings.SelectSection(settings.Audio);
                Settle();

                var options = window.GetVisualDescendants().OfType<OptionButton>()
                    .Where(o => o.DataContext is SegmentOption option && settings.Quality.Options.Contains(option))
                    .ToList();
                Assert.Equal(settings.Quality.Options.Count, options.Count);

                var chosen = options.Single(o => ((SegmentOption)o.DataContext!).IsSelected);
                var other = options.First(o => o != chosen);

                var peer = ControlAutomationPeer.CreatePeerForElement(other);
                Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
                var item = Assert.IsAssignableFrom<ISelectionItemProvider>(peer);
                Assert.False(item.IsSelected);

                item.Select();
                Assert.True(((SegmentOption)other.DataContext!).IsSelected);
                Assert.True(item.IsSelected);
                Assert.False(other == chosen);
            }
            finally { window.Close(); }

        });
    }

    [Fact]
    public void ListaSkrotowZawieraKlawiszeZeSlownika()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            using var settings = new SettingsViewModel(h.Settings, h.Player);

            var rows = settings.Shortcuts;

            Assert.Equal(19, rows.Count);
            Assert.Equal(Strings.Current["KeySpace"], rows[0].Keys);
            Assert.All(rows, row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Action));
                Assert.DoesNotContain("[", row.Action);
            });
            Assert.Contains(rows, row => row.Keys == "S");
            Assert.Contains(rows, row => row.Keys == "R");
        });
    }
}
