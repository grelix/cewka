using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Stan pokazywany w pasku tytułu i okno ustawień.</summary>
[Collection(UiCollection.Name)]
public sealed class TitleBarStateTests
{
    [Fact]
    public void KropkaPrzetwarzaniaTylkoPrzySchowanymPasie()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness(s =>
            {
                s.EqualiserEnabled = false;
                s.LimiterEnabled = false;
                s.NormalisationEnabled = false;
            });

            Assert.False(h.Player.ShowProcessingDot);

            h.Player.SoundEffects[0].Enabled = true;
            Assert.True(h.Player.ShowProcessingDot);

            h.Player.PanelOpen = true;
            Assert.False(h.Player.ShowProcessingDot);
        });
    }

    [Fact]
    public void KropkaPrzetwarzaniaGasiePrzyUstawieniachDomyslnych()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();

            Assert.True(h.Player.EqualiserEnabled);
            Assert.True(h.Player.LimiterEnabled);
            Assert.True(h.Player.NormalisationEnabled);
            Assert.False(h.Player.ShowProcessingDot);
        });
    }

    [Fact]
    public void KorektorZapalaKropkeTylkoZeZmienionaKrzywa()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var raised = new List<string?>();
            h.Player.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            h.Player.Bands[3].Gain = 4;
            Assert.True(h.Player.ShowProcessingDot);
            Assert.Contains(nameof(MainViewModel.ShowProcessingDot), raised);

            h.Player.EqualiserEnabled = false;
            Assert.False(h.Player.ShowProcessingDot);

            h.Player.EqualiserEnabled = true;
            h.Player.ResetEqualiser();
            Assert.False(h.Player.ShowProcessingDot);

            h.Player.Preamp.Gain = -3;
            Assert.True(h.Player.ShowProcessingDot);
        });
    }

    [Fact]
    public void WlaczonyPrzelacznikPodKursoremZostajeWAkcencie()
    {
        Ui.Run(() =>
        {
            var glyph = new Avalonia.Controls.Shapes.Path { Classes = { "stroke" }, Width = 16, Height = 16 };
            var toggle = new Avalonia.Controls.Primitives.ToggleButton
            {
                Classes = { "icon" }, IsChecked = true, Width = 40, Height = 40, Content = glyph,
            };
            var window = new Window { Width = 100, Height = 100, Content = toggle };
            window.Show();

            try
            {
                // Trafienie kursorem korzysta z wyrenderowanej sceny.
                Tick(2);
                window.MouseMove(new Point(20, 50));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(toggle.IsPointerOver);

                Assert.True(window.TryFindResource("AccentBrush", window.ActualThemeVariant, out var accent));
                Assert.Same(accent, glyph.Stroke);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrzyciskMotywuPokazujeTrybBiezacy()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var before = App.Theme.Mode;

            try
            {
                App.Theme.SetMode(ThemeMode.Dark);
                Assert.True(h.Player.ThemeIsDark);
                Assert.Equal(Strings.Current["TooltipThemeDark"], h.Player.ThemeTooltip);

                App.Theme.SetMode(ThemeMode.System);
                Assert.True(h.Player.ThemeIsSystem);
                Assert.Equal(Strings.Current["TooltipThemeSystem"], h.Player.ThemeTooltip);
            }
            finally
            {
                App.Theme.SetMode(before);
            }
        });
    }

    [Fact]
    public void OknoUstawienBezZakladkiEfektowIZTytulemSystemowym()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var settings = new SettingsViewModel(h.Settings, h.Player);

            Assert.DoesNotContain(settings.Sections, section => section.Key == "Effects");

            var window = new SettingsWindow(settings);
            Assert.Equal(Strings.Current["SettingsWindowTitle"], window.Title);
            settings.Dispose();
        });
    }

    private static void Tick(int count)
    {
        for (var i = 0; i < count; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
