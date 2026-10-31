using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

[Collection(UiCollection.Name)]
public sealed class EffectViewModelTests
{
    /// <summary>Suwak siły stoi zawsze na stopniu skali, także po przeciągnięciu między stopnie.</summary>
    [Fact]
    public void SuwakSilyPrzeskakujeStopniamiSkali() => Ui.Run(() =>
    {
        var effect = new EffectViewModel("Crossfeed", enabled: true, strength: 0.5);
        var slider = new TrackSlider { Step = 0.1, Height = 20, TrackBrush = Brushes.Gray, FillBrush = Brushes.Blue, DataContext = effect };
        slider.Bind(TrackSlider.ValueProperty, new Binding(nameof(EffectViewModel.Strength)) { Mode = BindingMode.TwoWay });
        var window = new Window { Width = 200, Height = 20, Content = slider };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200, slider.Bounds.Width);

            window.MouseDown(new Point(104, 10), MouseButton.Left);
            window.MouseMove(new Point(136, 10));
            window.MouseUp(new Point(136, 10), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.7, effect.Strength);
            Assert.Equal(0.7, slider.Value);

            window.MouseDown(new Point(144, 10), MouseButton.Left);
            window.MouseUp(new Point(144, 10), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0.7, slider.Value);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void SuwakBezZmianyWartosciNieZamawiaZapisuUstawien() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var effect = h.Player.SoundEffects[0];
        h.Settings.SaveNow();

        effect.Strength = effect.Strength + 0.01;

        Assert.False(h.Settings.SavePending);

        effect.Strength = effect.Strength + 0.1;

        Assert.True(h.Settings.SavePending);
    });
}
