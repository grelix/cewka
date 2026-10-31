using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Własne kontrolki: klawiatura, kółko, dwuklik, czytnik ekranu i pierścień fokusu.</summary>
[Collection(UiCollection.Name)]
public sealed class ControlInputTests
{
    private static Window Host(Control content)
    {
        var window = new Window { Width = 300, Height = 240, Content = new StackPanel { Margin = new Thickness(20), Children = { content } } };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Centre(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [Fact]
    public void FaderObslugujeKlawiszeIZostawiaKombinacjeOknu()
    {
        Ui.Run(() =>
        {
            var fader = new EqFader { Height = 116 };
            var window = Host(fader);
            var unhandled = new List<Key>();
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => unhandled.Add(e.Key), handledEventsToo: false);

            try
            {
                Assert.True(fader.Focusable);
                Assert.True(fader.Focus(NavigationMethod.Tab));

                Key(window, Avalonia.Input.Key.Up);
                Assert.Equal(0.5, fader.Value);

                Key(window, Avalonia.Input.Key.PageUp);
                Assert.Equal(3.5, fader.Value);

                Key(window, Avalonia.Input.Key.End);
                Assert.Equal(12, fader.Value);

                Key(window, Avalonia.Input.Key.Home);
                Assert.Equal(-12, fader.Value);

                Key(window, Avalonia.Input.Key.Delete);
                Assert.Equal(0, fader.Value);
                Assert.Empty(unhandled);

                // Ctrl+strzałka należy do okna: następny utwór.
                Key(window, Avalonia.Input.Key.Right, RawInputModifiers.Control);
                Assert.Equal(0, fader.Value);
                Assert.Contains(Avalonia.Input.Key.Right, unhandled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void GladzikNaFaderzeSumujeDrobneKroki()
    {
        Ui.Run(() =>
        {
            var fader = new EqFader { Height = 116 };
            var window = Host(fader);

            try
            {
                var centre = Centre(fader, window);

                for (var i = 0; i < 9; i++) window.MouseWheel(centre, new Vector(0, 0.1));
                Assert.Equal(0, fader.Value);

                window.MouseWheel(centre, new Vector(0, 0.15));
                Assert.Equal(0.5, fader.Value);

                // Zwykła mysz: jeden ząbek to jeden krok.
                window.MouseWheel(centre, new Vector(0, -1));
                window.MouseWheel(centre, new Vector(0, -1));
                Assert.Equal(-0.5, fader.Value);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DwuklikNaFaderzeUstawiaZero()
    {
        Ui.Run(() =>
        {
            var fader = new EqFader { Height = 116, Value = 6 };
            var window = Host(fader);

            try
            {
                var point = Centre(fader, window) + new Point(0, -40);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.NotEqual(0, fader.Value);

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Equal(0, fader.Value);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SuwakZglaszaPrzeciaganiePrzedZmianaWartosci()
    {
        Ui.Run(() =>
        {
            var slider = new TrackSlider { Width = 200, Height = 16 };
            var window = Host(slider);
            var events = new List<string>();

            slider.Scrubbing += (_, _) => events.Add("scrub");
            slider.Committed += (_, _) => events.Add("commit");
            slider.PropertyChanged += (_, e) => { if (e.Property == TrackSlider.ValueProperty) events.Add("value"); };

            try
            {
                var left = slider.TranslatePoint(new Point(40, 8), window)!.Value;
                var right = slider.TranslatePoint(new Point(160, 8), window)!.Value;

                window.MouseDown(left, MouseButton.Left);
                Assert.Equal(["scrub", "value"], events);

                window.MouseMove(right);
                window.MouseUp(right, MouseButton.Left);
                Assert.Equal("commit", events[^1]);
                Assert.Single(events, e => e == "commit");
                Assert.Equal(0.8, slider.Value, 2);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SuwakPostepuPrzewijaDopieroPoPuszczeniu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            h.Player.OpenPaths([h.Track("a", 100)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying));

            var slider = new TrackSlider { Width = 200, Height = 16, DataContext = h.Player };
            slider.Bind(TrackSlider.ValueProperty, new Avalonia.Data.Binding(nameof(h.Player.Progress)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            slider.Scrubbing += (_, _) => h.Player.BeginScrub();
            slider.Committed += (_, value) => h.Player.CommitScrub(value);
            var window = Host(slider);

            try
            {
                var start = slider.TranslatePoint(new Point(40, 8), window)!.Value;
                var end = slider.TranslatePoint(new Point(160, 8), window)!.Value;

                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end);
                h.PumpUntil(() => false, 100);
                Assert.True(h.Engine.Position < TimeSpan.FromSeconds(10), $"{h.Engine.Position}");

                window.MouseUp(end, MouseButton.Left);
                Assert.True(h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(70)), $"{h.Engine.Position}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SuwakZKrokiemKlawiaturyPrzyjmujeFokusIStrzalki()
    {
        Ui.Run(() =>
        {
            var plain = new TrackSlider { Width = 200, Height = 16 };
            var slider = new TrackSlider { Width = 200, Height = 16, KeyboardStep = 0.1, Step = 0.1 };
            var window = Host(new StackPanel { Children = { plain, slider } });
            var commits = 0;
            slider.Committed += (_, _) => commits++;

            try
            {
                Assert.False(plain.Focusable);
                Assert.True(slider.Focus(NavigationMethod.Tab));

                Key(window, Avalonia.Input.Key.Right);
                Key(window, Avalonia.Input.Key.Right);
                Assert.Equal(0.2, slider.Value, 6);
                Assert.Equal(2, commits);

                Key(window, Avalonia.Input.Key.End);
                Assert.Equal(1, slider.Value);

                Key(window, Avalonia.Input.Key.Left, RawInputModifiers.Control);
                Assert.Equal(1, slider.Value);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KolkoNadSuwakiemGlosnosciZmieniaOKrok()
    {
        Ui.Run(() =>
        {
            var slider = new TrackSlider { Width = 200, Height = 16, WheelStep = 0.05, Value = 0.5 };
            var window = Host(slider);

            try
            {
                window.MouseWheel(Centre(slider, window), new Vector(0, 1));
                Assert.Equal(0.55, slider.Value, 6);

                window.MouseWheel(Centre(slider, window), new Vector(0, -2));
                Assert.Equal(0.45, slider.Value, 6);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CzytnikEkranuWidziStanIWartosci()
    {
        Ui.Run(() =>
        {
            var glass = new GlassSwitch();
            var slider = new TrackSlider { Width = 200, Height = 16, Value = 0.45 };
            var fader = new EqFader { Height = 116, Value = -3 };
            AutomationProperties.SetName(glass, "Korektor");
            var window = Host(new StackPanel { Children = { glass, slider, fader } });

            try
            {
                var switchPeer = ControlAutomationPeer.CreatePeerForElement(glass);
                Assert.Equal(AutomationControlType.CheckBox, switchPeer.GetAutomationControlType());
                Assert.Equal("Korektor", switchPeer.GetName());
                var toggle = Assert.IsAssignableFrom<IToggleProvider>(switchPeer);
                Assert.Equal(ToggleState.Off, toggle.ToggleState);
                toggle.Toggle();
                Assert.True(glass.IsChecked);
                Assert.Equal(ToggleState.On, toggle.ToggleState);

                var range = Assert.IsAssignableFrom<IRangeValueProvider>(ControlAutomationPeer.CreatePeerForElement(slider));
                Assert.Equal(45, range.Value, 6);
                range.SetValue(60);
                Assert.Equal(0.6, slider.Value, 6);

                var faderPeer = ControlAutomationPeer.CreatePeerForElement(fader);
                Assert.Equal(AutomationControlType.Slider, faderPeer.GetAutomationControlType());
                var gain = Assert.IsAssignableFrom<IRangeValueProvider>(faderPeer);
                Assert.Equal(-3, gain.Value);
                Assert.Equal(-12, gain.Minimum);
                gain.SetValue(2);
                Assert.Equal(2, fader.Value);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void FokusZKlawiaturyRysujePierscien()
    {
        Ui.Run(() =>
        {
            var button = new Button { Content = "A", Width = 60, Height = 30 };
            var other = new Button { Content = "B", Width = 60, Height = 30 };
            var window = Host(new StackPanel { Children = { button, other } });

            try
            {
                button.Focus(NavigationMethod.Tab);
                Settle(window);

                var layer = AdornerLayer.GetAdornerLayer(button)!;
                var ring = layer.Children.OfType<FocusRing>().Single();
                Assert.Same(button, AdornerLayer.GetAdornedElement(ring));
                Assert.True(window.TryFindResource("FocusRingBrush", window.ActualThemeVariant, out var brush));
                Assert.Same(brush, ring.Brush);
                Assert.True(ring.Bounds.Width >= button.Bounds.Width + 2 * (FocusRing.Gap + FocusRing.Thickness) - 0.5);

                // Kliknięcie myszą nie pokazuje pierścienia.
                other.Focus(NavigationMethod.Pointer);
                button.Focus(NavigationMethod.Pointer);
                Settle(window);
                Assert.Empty(layer.Children.OfType<FocusRing>());
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PierscienFokusuMaKontrast3do1DlaKazdegoAkcentu(bool dark)
    {
        for (var hue = 0; hue < 360; hue += 10)
        {
            var accent = AccentColour.From([HslColor.ToRgb(hue, 0.9, 0.5)], dark);
            var ring = Contrast.FocusRing(accent, dark);
            var surface = dark ? Contrast.DarkFocusSurface : Colors.White;

            Assert.True(Contrast.Ratio(ring, surface) >= Contrast.FocusRingRatio - 0.01,
                $"odcień {hue}: {Contrast.Ratio(ring, surface):F2}:1");
        }
    }

    [Fact]
    public void PowierzchniaFokusuZgodnaZTokenami()
    {
        Ui.Run(() =>
        {
            Color Token(string key) =>
                Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) && value is ISolidColorBrush brush
                    ? brush.Color
                    : throw new InvalidOperationException(key);

            var grey = Colors.Black;
            for (var v = 0; v <= 255; v++)
            {
                grey = Color.FromRgb((byte)v, (byte)v, (byte)v);
                if (Contrast.RelativeLuminance(grey) >= Contrast.DarkBackdropCeiling) break;
            }

            var surface = Contrast.Over(Token("Glass2Brush"), Contrast.Over(Token("ScrimBrush"), grey));
            Assert.True(Contrast.RelativeLuminance(surface) <= Contrast.RelativeLuminance(Contrast.DarkFocusSurface) + 0.002,
                $"{surface}");

            // Akcent domyślny spełnia próg sam, więc tokeny pierścienia mogą mieć jego barwę.
            Assert.Equal(AccentColour.Default(true), Contrast.FocusRing(AccentColour.Default(true), true));
            Assert.Equal(AccentColour.Default(false), Contrast.FocusRing(AccentColour.Default(false), false));
        });
    }
}
