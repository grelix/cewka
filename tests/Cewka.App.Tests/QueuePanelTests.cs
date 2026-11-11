using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Boczny pasek kolejki i stacji: numery wierszy, wysuwanie, przypinanie, szerokość, stopka.</summary>
[Collection(UiCollection.Name)]
public sealed class QueuePanelTests
{
    /// <summary>Okno na silniku testowym z kolejką o zadanej długości.</summary>
    private static (MainWindow Window, PlayerHarness Harness) Open(
        int tracks = 0, double width = 1300, double height = 760, Action<AppSettings>? configure = null)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
            configure?.Invoke(s);
        });

        var window = new MainWindow(false, h.Player) { Width = width, Height = height };
        window.Show();

        if (tracks > 0) h.OpenPaths(Enumerable.Range(0, tracks).Select(i => h.Track("t" + i)), replace: false);

        Settle(window);
        return (window, h);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ListBox QueueList(MainWindow window) => window.FindControl<ListBox>("QueueList")!;

    /// <summary>Numer wiersza kolejki pod danym indeksem, po przewinięciu do niego.</summary>
    private static TextBlock NumberOf(MainWindow window, int index)
    {
        var list = QueueList(window);
        list.ScrollIntoView(index);
        Settle(window);

        var row = list.ContainerFromIndex(index)!;
        var expected = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == expected);
    }

    /// <summary>Szerokość napisu bez żadnego ograniczenia, tym samym krojem i stopniem.</summary>
    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontFeatures = text.FontFeatures,
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    [Fact]
    public void TrzycyfrowyNumerWierszaNieJestUciety() => Ui.Run(() =>
    {
        var (window, h) = Open(tracks: 105);

        try
        {
            Assert.Equal(105, h.Player.Queue.Count);

            var number = NumberOf(window, 104);
            var host = (Control)number.GetVisualParent()!;
            var natural = NaturalWidth(number);

            // Cały numer mieści się w kolumnie numeru i nie wychodzi z niej w lewo.
            Assert.True(natural <= host.Bounds.Width + 0.01, $"numer {natural:0.0} px, kolumna {host.Bounds.Width:0.0} px");
            Assert.True(number.Bounds.X >= -0.01, $"numer zaczyna się na {number.Bounds.X:0.0} px");
            Assert.True(number.Bounds.Width + 0.01 >= natural, $"numer ma {number.Bounds.Width:0.0} px z {natural:0.0} px");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1, "9")]
    [InlineData(2, "88")]
    [InlineData(3, "999")]
    [InlineData(4, "1000")]
    [InlineData(4, "9999")]
    public void KolumnaNumerowMiesciNajszerszyNumerOTejLiczbieCyfr(int digits, string number) => Ui.Run(() =>
    {
        var (window, h) = Open();
        using var _ = h;

        try
        {
            var text = new TextBlock { Text = number, FontSize = 10.5, Classes = { "tabular", "faint" } };
            window.FindControl<Panel>("QueueHost")!.Children.Add(text);
            Settle(window);

            Assert.True(NaturalWidth(text) <= window.QueueNumberWidth(digits), $"{number}: {NaturalWidth(text):0.0} px");
            Assert.True(window.QueueNumberWidth(digits + 1) > window.QueueNumberWidth(digits) || digits == 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void KolumnaNumerowRosnieZDlugosciaKolejkiIJestJednaDlaWszystkichWierszy() => Ui.Run(() =>
    {
        var (window, h) = Open(tracks: 99);

        try
        {
            // Wiersze na ekranie (lista jest wirtualizowana) mają jedną, wspólną szerokość kolumny.
            var widths = () => Enumerable.Range(0, h.Player.Queue.Count)
                .Select(i => QueueList(window).ContainerFromIndex(i))
                .OfType<Control>()
                .Select(row => row.GetVisualDescendants().OfType<Panel>().First(p => p.Height == 16).Bounds.Width)
                .Distinct()
                .ToArray();

            var before = Assert.Single(widths());
            Assert.Equal(window.QueueNumberWidth(2), before);

            h.AddPaths([h.Track("t99")]);
            Settle(window);

            var grown = Assert.Single(widths());
            Assert.Equal(window.QueueNumberWidth(3), grown);
            Assert.True(grown > before);
        }
        finally { window.Close(); }
    });

    // ---------- Uchwyt i podgląd ----------

    private TimeSpan _now = TimeSpan.FromSeconds(50);

    /// <summary>Okno z nieprzypiętym panelem, bez ruchu (efekty ograniczone) i z zegarem testu.</summary>
    private (MainWindow Window, PlayerHarness Harness) OpenPeek(
        int tracks = 0, double width = 1300, double height = 760, Action<AppSettings>? configure = null)
    {
        var (window, h) = Open(tracks, width, height, s =>
        {
            s.QueuePinned = false;
            s.Effects = EffectsMode.Reduced;
            configure?.Invoke(s);
        });
        window.QueueClock = () => _now;
        return (window, h);
    }

    private void Wait(MainWindow window, double seconds)
    {
        _now += TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        window.AdvanceQueueTimers();
        Settle(window);
    }

    private static Point Centre(Visual control, Visual window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Move(MainWindow window, Point point)
    {
        window.MouseMove(point);
        Settle(window);
    }

    private static void Click(MainWindow window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Key(MainWindow window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(window);
    }

    /// <summary>Punkt nad treścią okna, z dala od panelu i uchwytu.</summary>
    private static Point Content(MainWindow window) => new(120, window.Height / 2);

    /// <summary>Najeżdża na uchwyt i odczekuje zwłokę; potem kursor przechodzi nad wysunięty panel.</summary>
    private void Reveal(MainWindow window)
    {
        Move(window, Centre(window.QueueHandle, window));
        Wait(window, 0.3);
        Assert.True(window.QueuePeekShown);
        Move(window, Centre(window.QueueColumn, window));
    }

    [Fact]
    public void ZwinietyPanelToUchwytPrzyPrawejKrawedziIPelnaSzerokoscTresci() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();

        try
        {
            Assert.False(window.QueuePanelVisible);
            Assert.True(window.QueueHandle.IsEffectivelyVisible);

            var handle = window.QueueHandle.TranslatePoint(new Point(window.QueueHandle.Bounds.Width, 0), window.Body)!.Value;
            Assert.Equal(window.Body.Bounds.Width, handle.X, 0.5);
            Assert.True(window.QueueHandle.Bounds.Width >= 14);
            Assert.Equal(window.Body.Bounds.Width, window.CentreHost.Bounds.Width, 0.5);

            Assert.Equal(Localisation.Strings.Current["QueueHandleFiles"], AutomationProperties.GetName(window.QueueHandle));
            Assert.False(window.QueueHandleMark.IsVisible);

            h.Player.SetSource(SourceMode.Radio);
            Settle(window);
            Assert.Equal(Localisation.Strings.Current["QueueHandleRadio"], AutomationProperties.GetName(window.QueueHandle));
            Assert.True(window.QueueHandleMark.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NajechanieNaUchwytWysuwaPodgladNadTrescBezPrzesuwaniaJej() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek();

        try
        {
            var disc = window.DiscHost.Bounds;
            Move(window, Centre(window.QueueHandle, window));
            Wait(window, 0.29);
            Assert.False(window.QueuePanelVisible);

            Wait(window, 0.01);
            Assert.True(window.QueuePeekShown);
            Assert.True(window.QueuePanelVisible);
            Assert.Contains("queue-overlay", window.QueueColumn.Classes);
            Assert.False(window.QueueDismiss.IsVisible);

            // Treść pod podglądem nie przelicza układu, a panel stoi przy prawej krawędzi.
            Assert.Equal(disc, window.DiscHost.Bounds);
            Assert.Equal(window.Body.Bounds.Width, window.CentreHost.Bounds.Width, 0.5);
            var right = window.QueueColumn.TranslatePoint(new Point(window.QueueColumn.Bounds.Width, 0), window.Body)!.Value;
            Assert.Equal(window.Body.Bounds.Width, right.X, 0.5);

            // Kursor nad panelem trzyma go dowolnie długo.
            Move(window, Centre(window.QueueColumn, window));
            Wait(window, 30);
            Assert.True(window.QueuePanelVisible);

            // Po zjechaniu: tolerancja 0,5 s i 2 s.
            Move(window, Content(window));
            Wait(window, 2.4);
            Assert.True(window.QueuePanelVisible);
            Wait(window, 0.1);
            Assert.False(window.QueuePeekShown);
            Assert.False(window.QueuePanelVisible);
            Assert.False(window.QueueColumn.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EscChowaPodgladOdRazu() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek();

        try
        {
            Reveal(window);
            Key(window, Avalonia.Input.Key.Escape);

            Assert.False(window.QueuePeekShown);
            Assert.False(window.QueuePanelVisible);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Nakładany panel ma pod sobą to samo żywe tło co przypięta kolumna: kopię fragmentu tła okna
    /// spod panelu z tym samym przyciemnieniem i szkłem, a nie jednolitą, nieprzezroczystą płaszczyznę,
    /// która tłumiła efekty tła. Treść okna pod panelem dalej nie prześwituje.
    /// </summary>
    [Fact]
    public void PodgladMaPodSobaZyweTloJakPrzypietaKolumna() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();

        try
        {
            Reveal(window);
            Assert.True(window.QueueBackdropLayer.IsVisible);
            var brush = Assert.IsType<VisualBrush>(window.QueueBackdropCopy.Background);
            Assert.Same(window.Backdrop, brush.Visual);

            // Wycinek tła to dokładnie miejsce panelu w oknie.
            var origin = window.Body.TranslatePoint(window.QueueColumn.Bounds.TopLeft, window.Backdrop)!.Value;
            Assert.Equal(RelativeUnit.Absolute, brush.SourceRect.Unit);
            Assert.Equal(origin.X, brush.SourceRect.Rect.X, 0.5);
            Assert.Equal(origin.Y, brush.SourceRect.Rect.Y, 0.5);
            Assert.Equal(window.QueueColumn.Bounds.Width, brush.SourceRect.Rect.Width, 0.5);
            Assert.Equal(window.QueueColumn.Bounds.Height, brush.SourceRect.Rect.Height, 0.5);

            // Przypięta kolumna stoi nad prawdziwym tłem, więc kopii nie potrzebuje.
            Key(window, Avalonia.Input.Key.L);
            Settle(window);
            Assert.True(h.Player.QueuePinned);
            Assert.False(window.QueueBackdropLayer.IsVisible);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// Kopia tła pod nakładanym panelem jest żywa: po zmianie barw tła (nowy utwór, ruch plam) panel
    /// pokazuje nowe barwy, a nie klatkę z chwili wysunięcia. Sprawdzane na wyrenderowanych klatkach.
    /// </summary>
    [Fact]
    public void KopiaTlaPodPodglademNadazaZaZmianaTla() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek();

        try
        {
            window.Backdrop.Palette = [Color.FromRgb(20, 60, 200), Color.FromRgb(20, 60, 200), Color.FromRgb(20, 60, 200)];
            window.Backdrop.Advance(10);
            Reveal(window);
            Settle(window);

            var panel = window.QueueColumn.TranslatePoint(new Point(window.QueueColumn.Bounds.Width / 2, window.QueueColumn.Bounds.Height - 90), window)!.Value;
            var before = PixelAt(window, panel);

            window.Backdrop.Palette = [Color.FromRgb(230, 40, 20), Color.FromRgb(230, 40, 20), Color.FromRgb(230, 40, 20)];
            for (var i = 0; i < 8; i++)
            {
                window.Backdrop.Advance(0.25);
                Settle(window);
            }

            var after = PixelAt(window, panel);
            Assert.True(after.R > before.R + 8 && after.B + 8 < before.B,
                $"panel nie nadąża za tłem: przed {before}, po {after}");
        }
        finally { window.Close(); }
    });

    /// <summary>Barwa piksela wyrenderowanej klatki okna w punkcie w jednostkach okna.</summary>
    private static Color PixelAt(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var scale = frame.PixelSize.Width / window.Bounds.Width;
        var x = (int)(point.X * scale);
        var y = (int)(point.Y * scale);
        var value = System.Runtime.InteropServices.Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
        var bytes = BitConverter.GetBytes(value);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromRgb(bytes[0], bytes[1], bytes[2])
            : Color.FromRgb(bytes[2], bytes[1], bytes[0]);
    }

    [Fact]
    public void KlikniecieUchwytuPrzypinaPanelJakKlawiszL() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();

        try
        {
            Click(window, Centre(window.QueueHandle, window));
            Assert.True(h.Player.QueuePinned);
            Assert.True(window.QueuePanelVisible);
            Assert.False(window.QueuePeekShown);

            // W szerokim oknie przypięty panel to kolumna; jej krawędź zastępuje uchwyt.
            Assert.Equal(1, Grid.GetColumn(window.QueueColumn));
            Assert.DoesNotContain("queue-overlay", window.QueueColumn.Classes);
            Assert.False(window.QueueHandle.IsVisible);
            Assert.True(window.CentreHost.Bounds.Width < window.Bounds.Width - 200);

            // Najechanie nie zmienia przypiętego panelu, a L go odpina.
            Move(window, Content(window));
            Wait(window, 10);
            Assert.True(window.QueuePanelVisible);

            Key(window, Avalonia.Input.Key.L);
            Assert.False(h.Player.QueuePinned);
            Assert.False(window.QueuePanelVisible);
            Assert.True(window.QueueHandle.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void KlawiszLPrzypinaIUstawiaFokusNaWierszuAPonownieOddajeFokus() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek(tracks: 3);

        try
        {
            var before = window.FullScreenButton;
            Assert.True(before.Focus(NavigationMethod.Tab));

            Key(window, Avalonia.Input.Key.L);
            Assert.True(h.Player.QueuePinned);

            var focused = window.FocusManager!.GetFocusedElement() as Visual;
            var index = Math.Clamp(h.Player.CurrentIndex, 0, 2);
            Assert.Same(QueueList(window).ContainerFromIndex(index), focused);

            Key(window, Avalonia.Input.Key.L);
            Assert.False(h.Player.QueuePinned);
            Assert.Same(before, window.FocusManager!.GetFocusedElement());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BezRamkiSystemowejSkrajUchwytuZmieniaRozmiarOknaZamiastPrzypinac() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();

        try
        {
            // Okno testu nie ma ramki systemowej: ostatnie 6 px należy do krawędzi okna.
            var edge = new Point(window.Bounds.Width - 2, window.Bounds.Height / 2);
            Move(window, edge);
            Assert.Same(MainWindow.EdgeCursor, window.QueueHandle.Cursor);
            Click(window, edge);
            Assert.False(h.Player.QueuePinned);

            var inside = Centre(window.QueueHandle, window);
            Move(window, inside);
            Assert.Same(MainWindow.HandCursor, window.QueueHandle.Cursor);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WysuwaniePoKliknieciuNieReagujeNaNajechanie() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek(configure: s => s.QueueReveal = QueueReveal.Click);

        try
        {
            Move(window, Centre(window.QueueHandle, window));
            Wait(window, 5);
            Assert.False(window.QueuePanelVisible);

            Click(window, Centre(window.QueueHandle, window));
            Assert.True(h.Player.QueuePinned);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ScenaChowaPodgladAPoNiejNieWraca() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek();

        try
        {
            Reveal(window);
            window.EnterScene();
            Assert.False(window.QueuePeekShown);

            Wait(window, 5);
            window.ExitScene();
            Settle(window);
            Assert.False(window.QueuePeekShown);
            Assert.False(window.QueuePanelVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OtwarteMenuKontekstoweTrzymaPodglad() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek(tracks: 2);

        try
        {
            Reveal(window);
            window.QueueMenu.Open(QueueList(window));
            Settle(window);
            Assert.True(window.QueueMenu.IsOpen);

            Move(window, Content(window));
            Wait(window, 10);
            Assert.True(window.QueuePeekShown);

            window.QueueMenu.Close();
            Settle(window);
            Wait(window, 2.4);
            Assert.True(window.QueuePeekShown);
            Wait(window, 0.1);
            Assert.False(window.QueuePeekShown);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PodgladBezChowaniaZamykaKlikniecieObok() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek(configure: s => s.QueueHideDelay = QueueHideDelay.Never);

        try
        {
            Reveal(window);
            Move(window, Content(window));
            Wait(window, 60);
            Assert.True(window.QueuePeekShown);
            Assert.True(window.QueueDismiss.IsVisible);

            Click(window, Content(window));
            Assert.False(window.QueuePeekShown);
            Assert.False(window.QueueDismiss.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WWaskimOknieKliknieciePrzypinaNakladkeZamykanaKliknieciemObok() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek(width: 900, height: 620);

        try
        {
            Click(window, Centre(window.QueueHandle, window));
            Assert.True(h.Player.QueuePinned);
            Assert.True(window.QueueOverlayOpen);
            Assert.True(window.QueueDismiss.IsVisible);
            Assert.Contains("queue-overlay", window.QueueColumn.Classes);

            Click(window, Content(window));
            Assert.False(h.Player.QueuePinned);
            Assert.False(window.QueuePanelVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PodgladWysuwaSieZRuchemACofaGoPowrotKursora() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek(configure: s => s.Effects = EffectsMode.Full);

        try
        {
            Assert.False(((MainViewModel)window.DataContext!).ReducedEffects);
            var slide = (TranslateTransform)window.QueueColumn.RenderTransform!;

            window.QueueSlideOutTime = TimeSpan.FromHours(1);
            Reveal(window);
            Assert.NotNull(slide.Transitions);

            // Chowanie trwa chwilę: panel jest widoczny do końca ruchu.
            Move(window, Content(window));
            Wait(window, 2.5);
            Assert.False(window.QueuePeekShown);
            Assert.True(window.QueuePanelVisible);

            // Powrót kursora nad chowający się panel go zatrzymuje i cofa (wejście kursora wprost, bo
            // położenie ruchomego panelu zależy od zegara animacji).
            window.OnQueuePanelEntered();
            Assert.True(window.QueuePeekShown);
            Assert.True(window.QueuePanelVisible);
            window.Peek.PanelExited();

            Move(window, Content(window));
            Wait(window, 2.5);
            window.FinishQueueSlide();
            Assert.False(window.QueuePanelVisible);
            Assert.Equal(0, slide.X);
            Assert.Equal(1, window.QueueColumn.Opacity);
        }
        finally { window.Close(); }
    });

    // ---------- Ustawienia ----------

    [Fact]
    public void UstawieniaPaneluDzialajaOdRazuWOtwartymOknie() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();
        using var settings = new SettingsViewModel(h.Settings, h.Player);

        try
        {
            Assert.True(settings.QueueRevealChoice.Options.Single(o => Equals(o.Value, QueueReveal.Hover)).IsSelected);
            Assert.True(settings.QueueHideDelayChoice.Options.Single(o => Equals(o.Value, QueueHideDelay.TwoSeconds)).IsSelected);
            Assert.True(settings.QueueRevealDelayChoice.Options.Single(o => Equals(o.Value, QueueRevealDelay.Normal)).IsSelected);
            Assert.Equal(5, settings.QueueHideDelayChoice.Options.Count);
            Assert.True(settings.QueueHoverOptionsEnabled);

            settings.QueueHideDelayChoice.Choose(settings.QueueHideDelayChoice.Options.Single(o => Equals(o.Value, QueueHideDelay.Never)));
            settings.QueueRevealDelayChoice.Choose(settings.QueueRevealDelayChoice.Options.Single(o => Equals(o.Value, QueueRevealDelay.Long)));
            Assert.Equal(QueueHideDelay.Never, h.Settings.Current.QueueHideDelay);
            Assert.Null(window.Peek.HideDelay);
            Assert.Equal(TimeSpan.FromMilliseconds(600), window.Peek.RevealDelay);

            Move(window, Centre(window.QueueHandle, window));
            Wait(window, 0.5);
            Assert.False(window.QueuePeekShown);
            Wait(window, 0.1);
            Assert.True(window.QueuePeekShown);

            // Wysuwanie po kliknięciu wyłącza podgląd i czasy, które dotyczą tylko najechania.
            settings.QueueRevealChoice.Choose(settings.QueueRevealChoice.Options.Single(o => Equals(o.Value, QueueReveal.Click)));
            Assert.Equal(QueueReveal.Click, h.Player.QueueReveal);
            Assert.False(settings.QueueHoverOptionsEnabled);
            Assert.False(window.QueuePeekShown);
            Assert.False(window.Peek.Enabled);
        }
        finally { window.Close(); }
    });

    // ---------- Stopka ----------

    [Fact]
    public void StopkaMaPoleceniaMenuZNazwami() => Ui.Run(() =>
    {
        var (window, h) = Open();

        try
        {
            var s = Localisation.Strings.Current;
            Assert.True(window.QueueFooter.IsEffectivelyVisible);
            Assert.Equal(h.Player.SourceFilesTooltip, AutomationProperties.GetName(window.FooterFilesButton));
            Assert.Equal(h.Player.SourceRadioTooltip, AutomationProperties.GetName(window.FooterRadioButton));
            Assert.Equal(s["TooltipTogglePanel"], AutomationProperties.GetName(window.FooterPanelToggle));
            Assert.Equal(s["TooltipSettings"], AutomationProperties.GetName(window.FooterSettingsButton));
            Assert.Equal(s["TooltipQueuePin"], AutomationProperties.GetName(window.QueuePinToggle));
            Assert.True(window.QueuePinToggle.IsChecked);

            // Przełącznik źródła działa ze stopki, a panel zostaje i pokazuje stacje.
            Click(window, Centre(window.FooterRadioButton, window));
            Assert.True(h.Player.IsRadioMode);
            Assert.True(window.RadioHost.IsEffectivelyVisible);
            Assert.True(window.QueueFooter.IsEffectivelyVisible);

            // Przy wyłączonym radiu nie ma przełącznika źródła, jak w menu.
            h.Player.SetRadioEnabled(false);
            Settle(window);
            Assert.True(h.Player.IsFilesMode);
            Assert.False(window.FooterSourceSwitch.IsVisible);
        }
        finally { window.Close(); }
    });

    /// <summary>Stan podglądu do komunikatu asercji: ten test raz nie przeszedł w pełnym zestawie i nie dał się powtórzyć.</summary>
    private string PeekState(MainWindow window) =>
        $"podgląd {window.Peek.IsShown}, włączony {window.Peek.Enabled}, kursor w środku {window.Peek.PointerInside}, " +
        $"wstrzymania {window.Peek.Holds}, termin {window.Peek.NextDeadline}, zegar {_now}, panel widoczny {window.QueuePanelVisible}, " +
        $"kursor nad panelem {window.QueueColumn.IsPointerOver}, okno aktywne {window.IsActive}";

    [Fact]
    public void PrzypinkaWStopceOdpinaPanelKtoryZostajeJakoPodgladPodKursorem() => Ui.Run(() =>
    {
        var (window, h) = OpenPeek();

        try
        {
            Reveal(window);
            Click(window, Centre(window.QueuePinToggle, window));
            Assert.True(h.Player.QueuePinned, PeekState(window));
            Assert.True(window.QueuePanelVisible, PeekState(window));

            Click(window, Centre(window.QueuePinToggle, window));
            Assert.False(h.Player.QueuePinned, PeekState(window));
            Assert.True(window.QueuePeekShown, PeekState(window));

            Move(window, Content(window));
            Wait(window, 2.5);
            Assert.False(window.QueuePanelVisible, PeekState(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UstawieniaZeStopkiZamykajaPodglad() => Ui.Run(() =>
    {
        var (window, _) = OpenPeek();

        try
        {
            Reveal(window);
            Click(window, Centre(window.FooterSettingsButton, window));

            Assert.NotNull(window.OpenSettingsWindow);
            Assert.False(window.QueuePeekShown);
            window.OpenSettingsWindow!.Close();
        }
        finally { window.Close(); }
    });

    // ---------- Szerokość ----------

    private static void Drag(MainWindow window, double dx)
    {
        var start = Centre(window.QueueResizeGrip, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + dx, start.Y));
        window.MouseUp(new Point(start.X + dx, start.Y), MouseButton.Left);
        Settle(window);
    }

    [Fact]
    public void PrzeciaganieKrawedziZmieniaSzerokoscADwuklikPrzywracaDomyslna() => Ui.Run(() =>
    {
        var (window, h) = Open();

        try
        {
            Assert.Equal(280, window.QueueColumn.Bounds.Width, 0.5);
            var content = window.CentreHost.Bounds.Width;

            Drag(window, -100);
            Assert.Equal(380, h.Player.QueueWidth);
            Assert.Equal(380, window.QueueColumn.Bounds.Width, 0.5);
            Assert.Equal(content - 100, window.CentreHost.Bounds.Width, 0.5);
            Assert.Equal(380, h.Settings.Current.QueueWidth);

            // Kolumna zostawia blokowi odtwarzania jego minimum, a panel nie jest węższy niż 240 px.
            Drag(window, -2000);
            Assert.Equal(1300 - QueuePanelWidth.ContentMinimum, h.Player.QueueWidth);
            Assert.True(h.Player.QueuePinned);
            Drag(window, 2000);
            Assert.Equal(QueuePanelWidth.Minimum, h.Player.QueueWidth);

            // Dwuklik krawędzi: szerokość domyślna dla okna.
            Click(window, Centre(window.QueueResizeGrip, window));
            Click(window, Centre(window.QueueResizeGrip, window));
            Assert.Null(h.Player.QueueWidth);
            Assert.Equal(280, window.QueueColumn.Bounds.Width, 0.5);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ZapamietanaSzerokoscPonadOknoJestPrzycinanaTylkoPrzyWyswietlaniu() => Ui.Run(() =>
    {
        var (window, h) = Open(configure: s => s.QueueWidth = 900);

        try
        {
            Assert.True(h.Player.QueuePinned);
            Assert.False(window.QueueIsOverlay);
            Assert.Equal(1300 - QueuePanelWidth.ContentMinimum, window.QueueColumn.Bounds.Width, 0.5);
            Assert.Equal(900, h.Player.QueueWidth);

            // Nakładka w wąskim oknie: szerokość okna bez 80 px.
            h.Player.QueuePinned = false;
            window.Width = 800;
            Settle(window);
            h.Player.QueuePinned = true;
            Settle(window);
            Assert.True(window.QueueOverlayOpen);
            Assert.Equal(800 - QueuePanelWidth.OverlayMargin, window.QueueColumn.Bounds.Width, 0.5);
            Assert.Equal(900, h.Player.QueueWidth);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WaskiPanelChowaKolumneCzasuIPodsumowanieAPodpowiedzMaCzas() => Ui.Run(() =>
    {
        var (window, h) = Open(tracks: 2, configure: s => s.QueueWidth = 240);

        try
        {
            Assert.Contains("queue-narrow", window.QueueColumn.Classes);
            var row = QueueList(window).ContainerFromIndex(0)!;
            var duration = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-duration"));
            Assert.False(duration.IsEffectivelyVisible);
            Assert.False(window.QueueSummaryText.IsEffectivelyVisible);

            var item = h.Player.Queue[0];
            var grid = row.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("queue-row"));
            Assert.Equal(item.NarrowTooltip, ToolTip.GetTip(grid));
            Assert.Contains(item.Duration, item.NarrowTooltip);

            // Od 344 px (300 px wewnątrz) wiersz ma kolumnę czasu.
            h.Player.QueueWidth = 344;
            Settle(window);
            Assert.DoesNotContain("queue-narrow", window.QueueColumn.Classes);
            Assert.True(duration.IsEffectivelyVisible);
            Assert.True(window.QueueSummaryText.IsEffectivelyVisible);
            Assert.Equal(item.Tooltip, ToolTip.GetTip(grid));

            // Stopka mieści pięć przycisków także w najwęższym panelu.
            h.Player.QueueWidth = 240;
            Settle(window);
            foreach (var button in new Control[] { window.FooterFilesButton, window.FooterRadioButton, window.FooterPanelToggle, window.FooterSettingsButton, window.QueuePinToggle })
            {
                var left = button.TranslatePoint(default, window.QueueColumn)!.Value.X;
                Assert.True(left >= 0 && left + button.Bounds.Width <= window.QueueColumn.Bounds.Width, button.Name);
            }
        }
        finally { window.Close(); }
    });
}
