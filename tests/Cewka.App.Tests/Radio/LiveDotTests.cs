using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;

namespace Cewka.App.Tests.Radio;

/// <summary>
/// Czerwona kropka przed napisem „Na żywo” w oknie i w scenie: widoczna razem z napisem, pulsuje powoli przy graniu,
/// stoi przy ograniczonych efektach i w pauzie, nie liczy klatek, gdy jej nie widać, barwa z tokenu motywu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LiveDotTests
{
    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public MainViewModel Player => Harness.Player;

        public RadioViewModel Radio => Harness.Player.Radio;

        public LiveDot WindowDot => Window.LiveDot;

        public TextBlock WindowCaption => Window.LiveCaption;

        public LiveDot SceneDot => Window.Scene.LiveDot;

        public TextBlock SceneCaption => Window.Scene.LiveCaption;

        public LiveSource Source => Harness.Sessions[^1].Source;

        public void Live(LiveState state)
        {
            Radio.OnLiveState(Source, state);
            Player.SyncPlayingState();
            Render();
        }

        public void Fail()
        {
            Radio.OnLiveFailed(Source, new LiveFailure(LiveFailureReason.Stalled));
            Render();
        }

        public void Dispose() => Window.Close();
    }

    private static void Render()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Okno ze stacją, która już gra, przy pełnych efektach.</summary>
    private static Rig Open()
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.Effects = EffectsMode.Full;
            s.SceneVisual = SceneVisual.Disc;
            s.RadioPrivacyAcknowledged = true;
        });

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        var rig = new Rig { Window = window, Harness = h };
        window.Show();

        h.Player.SetSource(SourceMode.Radio);
        rig.Radio.SelectedCountry = rig.Radio.Countries.First(c => c.Key == "PL");
        rig.Radio.Play(rig.Radio.Rows.First(r => r.Station.Id == "pl-a").Station);
        rig.Live(LiveState.Playing);
        return rig;
    }

    /// <summary>Faza od zera: ograniczenie efektów i powrót ustawiają kropkę na pełne krycie.</summary>
    private static void RestartPulse(Rig rig)
    {
        rig.Player.SetEffects(EffectsMode.Reduced);
        rig.Player.SetEffects(EffectsMode.Full);
    }

    /// <summary>Kropka tuż przed napisem, w tym samym wierszu, z odstępem jak między słowami i środkiem na środku wiersza.</summary>
    private static void AssertBefore(LiveDot dot, TextBlock caption)
    {
        Assert.Same(dot.Parent, caption.Parent);
        Assert.Equal(caption.FontSize, dot.FontSize);

        // Średnica 0,5–0,6 wysokości x pisma (Noto Sans: 0,536 em).
        Assert.InRange(dot.Diameter / caption.FontSize, 0.5 * 0.536 - 1e-9, 0.6 * 0.536 + 1e-9);

        Assert.True(dot.Bounds.Right <= caption.Bounds.Left + 0.01, $"kropka kończy się na {dot.Bounds.Right}, napis zaczyna na {caption.Bounds.Left}");
        // Zaokrąglanie układu do pikseli dokłada do odstępu najwyżej piksel.
        Assert.Equal(dot.Gap, caption.Bounds.Left - (dot.Bounds.Left + dot.Diameter), 1.0);
        Assert.Equal(caption.Bounds.Center.Y, dot.Bounds.Top + dot.Bounds.Height / 2, 0.75);
    }

    private static Color Token(string key, ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException(key);

    private static Color GreyWithLuminance(double luminance)
    {
        for (var v = 0; v <= 255; v++)
        {
            var grey = Color.FromRgb((byte)v, (byte)v, (byte)v);
            if (Contrast.RelativeLuminance(grey) >= luminance) return grey;
        }

        return Colors.White;
    }

    // ---------- Widoczność i położenie ----------

    [Fact]
    public void WOknieKropkaStoiPrzedNapisemNaZywoIZnikaRazemZNim() => Ui.Run(() =>
    {
        using var rig = Open();
        var dot = rig.WindowDot;
        var caption = rig.WindowCaption;

        Assert.Equal(Strings.Current["RadioLive"], caption.Text);
        Assert.True(caption.IsEffectivelyVisible);
        Assert.True(dot.IsEffectivelyVisible);
        AssertBefore(dot, caption);

        // Okno pokazuje „Na żywo” także przy łączeniu, więc kropka zostaje.
        rig.Live(LiveState.Buffering);
        Assert.True(caption.IsEffectivelyVisible);
        Assert.True(dot.IsEffectivelyVisible);

        rig.Fail();
        Assert.False(caption.IsEffectivelyVisible);
        Assert.False(dot.IsEffectivelyVisible);

        rig.Radio.Retry();
        rig.Live(LiveState.Playing);
        Assert.True(caption.IsEffectivelyVisible);
        Assert.True(dot.IsEffectivelyVisible);

        rig.Player.SetSource(SourceMode.Files);
        Render();
        Assert.False(caption.IsEffectivelyVisible);
        Assert.False(dot.IsEffectivelyVisible);
    });

    [Fact]
    public void WScenieKropkaStoiTylkoPrzyNapisieNaZywo() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.EnterScene();
        Render();
        var dot = rig.SceneDot;
        var caption = rig.SceneCaption;

        Assert.Equal(Strings.Current["RadioLive"], caption.Text);
        Assert.True(dot.IsVisible);
        Assert.True(dot.IsEffectivelyVisible);
        AssertBefore(dot, caption);

        // Stan łączenia i błąd zastępują napis w tym samym wierszu; kropka znika.
        rig.Live(LiveState.Buffering);
        Assert.True(caption.IsEffectivelyVisible);
        Assert.Equal(rig.Radio.StateText, caption.Text);
        Assert.False(dot.IsVisible);

        rig.Live(LiveState.Playing);
        Assert.True(dot.IsVisible);
        AssertBefore(dot, caption);

        rig.Fail();
        Assert.Equal(rig.Radio.ErrorText, caption.Text);
        Assert.False(dot.IsVisible);

        rig.Player.SetSource(SourceMode.Files);
        Render();
        Assert.False(caption.IsVisible);
        Assert.False(dot.IsEffectivelyVisible);
    });

    [Fact]
    public void KropkaWScenieMaWielkoscNapisuPrzyKtorymStoi() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.EnterScene();
        Render();
        var dot = rig.SceneDot;
        var caption = rig.SceneCaption;
        var size = caption.FontSize;

        Assert.Equal(size * LiveDot.DiameterPerEm, dot.Diameter, 6);

        // Wielkość tekstu sceny nie rozjeżdża kropki i napisu.
        rig.Player.SceneTextSize = SceneTextSize.ExtraLarge;
        Render();
        Assert.Equal(caption.FontSize * LiveDot.DiameterPerEm, dot.Diameter, 6);
        AssertBefore(dot, caption);

        // Większe pismo napisu: większa kropka i odstęp, nadal przed napisem i na środku wiersza.
        var width = dot.Bounds.Width;
        caption.FontSize = size * 2;
        Render();
        Assert.Equal(size * 2 * LiveDot.DiameterPerEm, dot.Diameter, 6);
        Assert.True(dot.Bounds.Width > width * 1.7, $"{width} -> {dot.Bounds.Width}");
        AssertBefore(dot, caption);
    });

    // ---------- Pulsowanie ----------

    [Fact]
    public void KrzywaPulsowaniaIdzieLagodnieOdPelnegoKryciaDoTrzydziestuPieciuSetnychIZPowrotem() => Ui.Run(() =>
    {
        Assert.Equal(1, LiveDot.OpacityAt(0), 9);
        Assert.Equal(0.35, LiveDot.OpacityAt(1), 9);
        Assert.Equal(1, LiveDot.OpacityAt(2), 9);
        Assert.Equal(LiveDot.OpacityAt(0.4), LiveDot.OpacityAt(1.6), 9);

        // Bez skoków: przy 100 klatkach na sekundę krycie zmienia się najwyżej o setną.
        for (var t = 0.0; t < 2; t += 0.01)
        {
            var step = Math.Abs(LiveDot.OpacityAt(t + 0.01) - LiveDot.OpacityAt(t));
            Assert.True(step <= 0.011, $"{t:F2} s: {step:F4}");
            Assert.InRange(LiveDot.OpacityAt(t), 0.35 - 1e-9, 1 + 1e-9);
        }
    });

    [Fact]
    public void KropkaWOknieIWSceniePulsujeWCykluDwochSekundBezZmianyRozmiaru() => Ui.Run(() =>
    {
        using var rig = Open();

        foreach (var scene in new[] { false, true })
        {
            if (scene)
            {
                rig.Window.EnterScene();
                Render();
            }

            var dot = scene ? rig.SceneDot : rig.WindowDot;
            Assert.True(dot.IsPulsing);

            RestartPulse(rig);
            Assert.True(dot.IsPulsing);
            Assert.Equal(1, dot.Opacity);
            var bounds = dot.Bounds;

            var lowest = 1.0;
            for (var i = 1; i <= 40; i++)
            {
                var before = dot.Opacity;
                dot.Advance(0.05);
                Assert.True(Math.Abs(dot.Opacity - before) <= 0.06, $"klatka {i}: {before:F3} -> {dot.Opacity:F3}");
                lowest = Math.Min(lowest, dot.Opacity);
                if (i == 10) Assert.Equal(0.675, dot.Opacity, 3);
                if (i == 20) Assert.Equal(0.35, dot.Opacity, 3);
            }

            Assert.Equal(0.35, lowest, 3);
            Assert.Equal(1, dot.Opacity, 3);
            Render();
            Assert.Equal(bounds, dot.Bounds);
        }
    });

    [Fact]
    public void PrzyOgraniczonychEfektachIWPauzieKropkaSwieciStale() => Ui.Run(() =>
    {
        using var rig = Open();
        var dot = rig.WindowDot;
        RestartPulse(rig);
        dot.Advance(0.25);
        dot.Advance(0.25);
        Assert.True(dot.Opacity < 0.9, $"{dot.Opacity}");

        rig.Player.SetEffects(EffectsMode.Reduced);
        Render();
        Assert.True(dot.IsEffectivelyVisible);
        Assert.False(dot.IsPulsing);
        Assert.Equal(1, dot.Opacity);
        dot.Advance(0.25);
        Assert.Equal(1, dot.Opacity);

        rig.Window.EnterScene();
        Render();
        Assert.True(rig.SceneDot.IsEffectivelyVisible);
        Assert.False(rig.SceneDot.IsPulsing);
        Assert.Equal(1, rig.SceneDot.Opacity);
        rig.Window.ExitScene();
        Render();

        rig.Player.SetEffects(EffectsMode.Full);
        Render();
        Assert.True(dot.IsPulsing);

        // Pauza, czyli w radiu rozłączenie: napis i kropka zostają, kropka stoi.
        rig.Player.TogglePlay();
        Render();
        Assert.False(rig.Player.IsPlaying);
        Assert.True(dot.IsEffectivelyVisible);
        Assert.False(dot.IsPulsing);
        Assert.Equal(1, dot.Opacity);
    });

    [Fact]
    public void KropkaNieLiczyKlatekGdyJejNieWidac() => Ui.Run(() =>
    {
        using var rig = Open();
        var dot = rig.WindowDot;
        var sceneDot = rig.SceneDot;
        Assert.True(dot.IsPulsing);

        // Scena przykrywa okno: kropka okna czeka, kropka sceny pulsuje.
        rig.Window.EnterScene();
        Render();
        Assert.False(dot.IsPulsing);
        Assert.True(sceneDot.IsPulsing);

        rig.Live(LiveState.Buffering);
        Assert.False(sceneDot.IsPulsing);
        rig.Live(LiveState.Playing);
        Assert.True(sceneDot.IsPulsing);

        rig.Window.ExitScene();
        Render();
        Assert.False(sceneDot.IsPulsing);
        Assert.True(dot.IsPulsing);

        rig.Window.WindowState = WindowState.Minimized;
        Render();
        Assert.False(dot.IsPulsing);
        rig.Window.WindowState = WindowState.Normal;
        Render();
        Assert.True(dot.IsPulsing);

        rig.Fail();
        Assert.False(dot.IsPulsing);
    });

    // ---------- Barwa i dostępność ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BarwaKropkiToCzerwienZKontrastemTrzyDoJednegoNaTleMotywu(bool dark) => Ui.Run(() =>
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var red = Token("LiveDotBrush", variant);
        var scrim = Token("ScrimBrush", variant);

        // Najgorsza plama tła motywu pod przyciemnieniem i drugi skraj jasności (czerń albo biel).
        var limit = dark ? Contrast.DarkBackdropCeiling : Contrast.LightBackdropFloor;
        var worst = Contrast.Over(scrim, GreyWithLuminance(limit));
        var other = Contrast.Over(scrim, dark ? Colors.Black : Colors.White);

        Assert.True(Contrast.Ratio(red, worst) >= 3, $"motyw {(dark ? "ciemny" : "jasny")}: {Contrast.Ratio(red, worst):F2}:1");
        Assert.True(Contrast.Ratio(red, other) >= 3, $"motyw {(dark ? "ciemny" : "jasny")}: {Contrast.Ratio(red, other):F2}:1");
        Assert.True(red.ToHsl().H is < 15 or > 345, $"{red}");
        Assert.Equal(255, red.A);
    });

    [Fact]
    public void KropkaBierzeBarweZTokenuMotywuOknaAWScenieZawszeZCiemnego() => Ui.Run(() =>
    {
        using var rig = Open();
        Color Fill(LiveDot dot) => Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color;

        rig.Window.RequestedThemeVariant = ThemeVariant.Light;
        Render();
        Assert.Equal(Token("LiveDotBrush", ThemeVariant.Light), Fill(rig.WindowDot));

        rig.Window.RequestedThemeVariant = ThemeVariant.Dark;
        Render();
        Assert.Equal(Token("LiveDotBrush", ThemeVariant.Dark), Fill(rig.WindowDot));
        Assert.NotEqual(Token("LiveDotBrush", ThemeVariant.Light), Token("LiveDotBrush", ThemeVariant.Dark));

        rig.Window.RequestedThemeVariant = ThemeVariant.Light;
        rig.Window.EnterScene();
        Render();
        Assert.Equal(Token("LiveDotBrush", ThemeVariant.Dark), Fill(rig.SceneDot));
    });

    [Fact]
    public void KropkaJestUkrytaPrzedCzytnikiemANapisMaTeSamaNazwe() => Ui.Run(() =>
    {
        using var rig = Open();
        rig.Window.EnterScene();
        Render();

        foreach (var dot in new[] { rig.WindowDot, rig.SceneDot })
        {
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(dot));
            Assert.False(ControlAutomationPeer.CreatePeerForElement(dot).IsControlElement());
            Assert.False(dot.Focusable);
            Assert.False(dot.IsHitTestVisible);
        }

        Assert.Equal(Strings.Current["RadioLive"], ControlAutomationPeer.CreatePeerForElement(rig.WindowCaption).GetName());
        Assert.Equal(Strings.Current["RadioLive"], ControlAutomationPeer.CreatePeerForElement(rig.SceneCaption).GetName());
    });
}
