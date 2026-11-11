using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Suwaki wyłączonego efektu i korektora wyglądają na drugorzędne w obu motywach.</summary>
[Collection(UiCollection.Name)]
public sealed class InactiveControlsTests
{
    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Color Colour(IBrush? brush) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuwakWylaczonegoEfektuMaBarweEtykietyAWlaczonegoAkcent(bool dark)
    {
        Ui.Run(() =>
        {
            App.Settings.Current.Window = null;
            var h = new PlayerHarness(s => s.WindowControls = WindowControlsPosition.Right);
            var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
            window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Show();

            try
            {
                // Korektor i efekty są w osobnym oknie.
                h.Player.PanelOpen = true;
                var sound = window.OpenSoundWindow!;
                sound.RequestedThemeVariant = window.RequestedThemeVariant;

                var effects = h.Player.SoundEffects;
                effects[0].Enabled = false;
                effects[1].Enabled = true;
                h.Player.EqualiserEnabled = false;
                Settle();

                Assert.True(sound.TryFindResource("AccentBrush", sound.ActualThemeVariant, out var accent));
                var sliders = sound.GetVisualDescendants().OfType<TrackSlider>().ToList();

                foreach (var effect in effects.Take(2))
                {
                    var slider = sliders.Single(s => ReferenceEquals(s.DataContext, effect));
                    var label = slider.GetVisualAncestors().OfType<StackPanel>().First().GetVisualDescendants().OfType<TextBlock>()
                        .First(t => t.Text == effect.Label);

                    var expected = effect.Enabled ? Colour((IBrush)accent!) : Colour(label.Foreground);
                    Assert.Equal(expected, Colour(slider.FillBrush));
                    Assert.True(slider.IsEffectivelyEnabled && slider.IsHitTestVisible && slider.Focusable);
                }

                var faders = sound.GetVisualDescendants().OfType<EqFader>().ToList();
                var preamp = faders.Single(f => AutomationProperties.GetName(f) == Strings.Current["PreampName"]);
                var band = faders.First(f => f != preamp);
                Assert.Equal(Colour(band.FillBrush), Colour(preamp.FillBrush));

                h.Player.EqualiserEnabled = true;
                Settle();
                Assert.Equal(Colour((IBrush)accent!), Colour(preamp.FillBrush));
            }
            finally { window.Close(); }
        });
    }
}
