using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Skróty i widoczność kolumny stacji w prawdziwym oknie (bez ekranu i dźwięku).</summary>
[Collection(UiCollection.Name)]
public sealed class RadioWindowInputTests
{
    private static (MainWindow Window, PlayerHarness Harness) Open()
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
        });

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();
        Settle();
        return (window, h);
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Settle();
    }

    [Fact]
    public void CtrlDwaOtwieraListeStacjiWMiejscuKolejkiACtrlJedenPrzywracaKolejke() => Ui.Run(() =>
    {
        var (window, h) = Open();
        using var _ = h;

        Assert.True(window.FindControl<Control>("QueueContent")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("RadioHost")!.IsEffectivelyVisible);

        Key(window, Avalonia.Input.Key.D2, RawInputModifiers.Control);

        Assert.True(h.Player.IsRadioMode);
        Assert.True(window.FindControl<Control>("RadioHost")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("QueueContent")!.IsEffectivelyVisible);
        Assert.NotEmpty(window.FindControl<ListBox>("RadioList")!.ItemsSource!.Cast<object>());

        Key(window, Avalonia.Input.Key.D1, RawInputModifiers.Control);

        Assert.True(h.Player.IsFilesMode);
        Assert.True(window.FindControl<Control>("QueueContent")!.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void PisanieWPoluSzukaniaNieUruchamiaSkrotowLiterowych() => Ui.Run(() =>
    {
        var (window, h) = Open();
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        Settle();

        var search = window.FindControl<TextBox>("RadioSearch")!;
        search.Focus();
        Settle();
        Assert.True(search.IsFocused);

        // L zamykałoby kolejkę, S włączałoby losowanie, M wyciszałoby.
        foreach (var key in new[] { Avalonia.Input.Key.L, Avalonia.Input.Key.S, Avalonia.Input.Key.M, Avalonia.Input.Key.Q })
            Key(window, key);

        Assert.True(h.Player.QueuePinned);
        Assert.False(h.Player.ShuffleEnabled);
        Assert.False(h.Player.IsMuted);
        window.Close();
    });

    [Fact]
    public void CtrlFWRadiuOtwieraKolumneIUstawiaFokusWPoluSzukania() => Ui.Run(() =>
    {
        var (window, h) = Open();
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        h.Player.QueuePinned = false;
        Settle();

        Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control);
        h.PumpUntil(() => window.FindControl<TextBox>("RadioSearch")!.IsFocused);

        Assert.True(h.Player.QueuePinned);
        Assert.True(window.FindControl<TextBox>("RadioSearch")!.IsFocused);
        window.Close();
    });

    [Fact]
    public void LosowoIPowtarzanieWRadiuSaNieaktywnePrzyZachowanymMiejscu() => Ui.Run(() =>
    {
        var (window, h) = Open();
        using var _ = h;
        var shuffle = window.FindControl<Control>("ShuffleToggle")!;
        var width = shuffle.Bounds.Width;

        h.Player.SetSource(SourceMode.Radio);
        Settle();
        Key(window, Avalonia.Input.Key.S);
        Key(window, Avalonia.Input.Key.R);

        Assert.False(h.Player.ShuffleEnabled);
        Assert.False(h.Player.RepeatEnabled);
        Assert.Equal(0, shuffle.Opacity);
        Assert.False(shuffle.IsHitTestVisible);
        Assert.Equal(width, shuffle.Bounds.Width);
        window.Close();
    });
}
