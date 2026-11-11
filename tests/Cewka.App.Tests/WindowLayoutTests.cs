using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno główne bez okna na ekranie: układ, stany okna, zapis geometrii.</summary>
[Collection(UiCollection.Name)]
public sealed class WindowLayoutTests
{
    private static MainWindow Open(double width, double height, bool queue = false)
    {
        App.Settings.Current.Window = null;
        App.Settings.Current.QueuePinned = queue;
        App.Settings.Current.WindowControls = WindowControlsPosition.Right;

        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
        }
    }

    private static MainViewModel Player(MainWindow window) => (MainViewModel)window.DataContext!;

    private static void Press(Window window, Key key) =>
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    [Fact]
    public void PrzelaczanieObszarowNieZmieniaRozmiaruOkna()
    {
        Ui.Run(() =>
        {
            var window = Open(1000, 560);

            try
            {
                foreach (var toggle in new Action[] { Player(window).TogglePanel, Player(window).ToggleQueue,
                                                     Player(window).TogglePanel, Player(window).ToggleQueue })
                {
                    toggle();
                    Settle(window);
                    Assert.Equal(1000, window.Width);
                    Assert.Equal(560, window.Height);
                }

                Assert.Equal(760, window.MinWidth);
                Assert.Equal(520, window.MinHeight);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WWaskimOknieKolejkaJestNakladkaZamykanaEsc()
    {
        Ui.Run(() =>
        {
            var window = Open(900, 620, queue: true);

            try
            {
                // Zwężone okno nie nakrywa treści kolejką bez prośby.
                Assert.True(window.QueueIsOverlay);
                Assert.False(Player(window).QueuePinned);

                Player(window).QueuePinned = true;
                Settle(window);
                Assert.True(window.QueueOverlayOpen);
                Assert.True(window.FindControl<Border>("QueueDismiss")!.IsVisible);

                Press(window, Key.Escape);
                Assert.False(Player(window).QueuePinned);
                Assert.False(window.FindControl<Border>("QueueDismiss")!.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KolejkaSchowanaPrzezZwezenieWracaPoPoszerzeniu()
    {
        Ui.Run(() =>
        {
            var window = Open(1200, 680, queue: true);

            try
            {
                Assert.False(window.QueueIsOverlay);
                Assert.True(Player(window).QueuePinned);

                window.Width = 900;
                Settle(window);
                Assert.True(window.QueueIsOverlay);
                Assert.False(Player(window).QueuePinned);

                // Zwężenie nie zmienia zamiaru zapisanego w ustawieniach, także przed zamknięciem.
                Assert.True(App.Settings.Current.QueuePinned);

                window.Width = 1200;
                Settle(window);
                Assert.False(window.QueueIsOverlay);
                Assert.True(Player(window).QueuePinned);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KolejkaZamknietaRecznieWWaskimOknieZostajeZamknieta()
    {
        Ui.Run(() =>
        {
            var window = Open(1200, 680, queue: true);

            try
            {
                window.Width = 900;
                Settle(window);

                Player(window).QueuePinned = true;
                Player(window).QueuePinned = false;
                Assert.False(App.Settings.Current.QueuePinned);

                window.Width = 1200;
                Settle(window);
                Assert.False(Player(window).QueuePinned);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ScenaWracaDoMaksymalizacjiIEscZNiejWychodzi()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);

            try
            {
                window.WindowState = WindowState.Maximized;
                window.EnterScene();
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                Press(window, Key.Escape);
                Assert.False(window.IsSceneOpen);
                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KolejkaOknaNiePrzechodziDoScenyAEscZamykaScene()
    {
        Ui.Run(() =>
        {
            var window = Open(900, 620);

            try
            {
                Player(window).QueuePinned = true;
                Settle(window);
                window.EnterScene();

                // Kolejka zostaje w oknie pod sceną, więc Esc nie ma czego zamykać przed sceną.
                Press(window, Key.Escape);
                Assert.False(window.IsSceneOpen);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(Player(window).QueuePinned);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PozaZwyklymOknemNarozinikiSaProste(bool systemFrame)
    {
        Ui.Run(() =>
        {
            App.Settings.Current.Window = null;
            var window = new MainWindow(systemFrame) { Width = 1100, Height = 600 };
            window.Show();
            Settle(window);

            try
            {
                var shell = window.FindControl<Border>("Shell")!;
                Assert.Equal(systemFrame, shell.Classes.Contains("square"));

                window.WindowState = WindowState.Maximized;
                Assert.Contains("square", shell.Classes);

                window.WindowState = WindowState.FullScreen;
                Assert.Contains("square", shell.Classes);

                window.WindowState = WindowState.Normal;
                Assert.Equal(systemFrame, shell.Classes.Contains("square"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void GeometriaZeScenyToZwykleOknoIStanSprzedNiej()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);

            try
            {
                window.Position = new PixelPoint(120, 80);
                Settle(window);

                window.WindowState = WindowState.Maximized;
                window.Width = 1900;
                window.Height = 1000;
                Settle(window);
                window.EnterScene();
                Settle(window);

                window.PersistGeometry();
                var saved = App.Settings.Current.Window!;

                Assert.Equal(1100, saved.Width);
                Assert.Equal(600, saved.Height);
                Assert.Equal(120, saved.X);
                Assert.Equal(80, saved.Y);
                Assert.True(saved.Maximized);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(2500, 1400, false)]
    [InlineData(2500, 1400, true)]
    [InlineData(1000, 700, false)]
    public void OknoZapisaneNaOdlaczonymEkranieStajeNaSrodkuGlownego(int savedWidth, int savedHeight, bool maximized)
    {
        Ui.Run(() =>
        {
            // Daleko poza każdym ekranem, jak monitor odłączony od ostatniego uruchomienia.
            App.Settings.Current.WindowControls = WindowControlsPosition.Right;
            App.Settings.Current.Window = new WindowGeometry
            {
                X = 100_000, Y = 0, Width = savedWidth, Height = savedHeight, Maximized = maximized,
            };

            var window = new MainWindow();

            try
            {
                var primary = window.Screens.Primary!;
                var area = primary.WorkingArea;
                var width = (int)(window.Width * primary.Scaling);
                var height = (int)(window.Height * primary.Scaling);
                Assert.InRange(window.Position.X, area.X + (area.Width - width) / 2 - 1, area.X + (area.Width - width) / 2 + 1);
                Assert.InRange(window.Position.Y, area.Y + (area.Height - height) / 2 - 1, area.Y + (area.Height - height) / 2 + 1);

                window.Show();
                Dispatcher.UIThread.RunJobs();
                if (maximized)
                {
                    window.WindowState = WindowState.Normal;
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.True(window.Width <= area.Width, $"{window.Width}");
                Assert.True(window.Height <= area.Height, $"{window.Height}");
                Assert.True(window.Position.X >= area.X, $"{window.Position}");
                Assert.True(window.Position.Y >= area.Y, $"{window.Position}");
                Assert.True(window.Position.X + window.Width <= area.Right, $"{window.Position}");
                Assert.True(window.Position.Y + window.Height <= area.Bottom, $"{window.Position}");
            }
            finally
            {
                window.Close();
                App.Settings.Current.Window = null;
            }
        });
    }

    [Fact]
    public void PrzyciskMaksymalizacjiPokazujePrzywrocenieINaPelnymEkranieZnika()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);

            try
            {
                var buttons = window.GetLogicalDescendants().OfType<WindowButtons>().First(b => b.IsVisible);
                var maximise = buttons.FindControl<Button>("SquareMaximise")!;

                Assert.Equal(Strings.Current["TooltipMaximise"], AutomationProperties.GetName(maximise));

                window.WindowState = WindowState.Maximized;
                Assert.Equal(Strings.Current["TooltipRestore"], AutomationProperties.GetName(maximise));
                Assert.Equal(Strings.Current["TooltipRestore"], ToolTip.GetTip(maximise));

                window.WindowState = WindowState.FullScreen;
                Assert.False(buttons.FindControl<StackPanel>("Root")!.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MaksymalizacjaWykonanaPrzezSystemNieCofaSieKliknieciem()
    {
        Ui.Run(() =>
        {
            var buttons = new WindowButtons { Position = WindowControlsPosition.Right };
            var window = new Window { Width = 300, Height = 100, Content = buttons };
            window.Show();

            try
            {
                Settle(window);
                var maximise = buttons.FindControl<Button>("SquareMaximise")!;
                var centre = maximise.TranslatePoint(new Point(maximise.Bounds.Width / 2, maximise.Bounds.Height / 2), window)!.Value;

                // Ramka systemowa może przełączyć stan sama, zanim przycisk dostanie kliknięcie.
                window.MouseDown(centre, MouseButton.Left);
                window.WindowState = WindowState.Maximized;
                window.MouseUp(centre, MouseButton.Left);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                // Bez udziału systemu kliknięcie przełącza jak zwykle.
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrzyciskiPoLewejZamknieciemNaZewnetrznejKrawedzi()
    {
        Ui.Run(() =>
        {
            var buttons = new WindowButtons { Position = WindowControlsPosition.Left };
            var group = buttons.FindControl<StackPanel>("SquareGroup")!;
            Assert.Equal("SquareClose", group.Children[0].Name);
            Assert.True(buttons.FindControl<Avalonia.Controls.Shapes.Rectangle>("SeparatorAfter")!.IsVisible);

            buttons.Position = WindowControlsPosition.Right;
            Assert.Equal("SquareClose", group.Children[^1].Name);
            Assert.True(buttons.FindControl<Avalonia.Controls.Shapes.Rectangle>("SeparatorBefore")!.IsVisible);
        });
    }

    [Fact]
    public void PrzelacznikiObszarowSaPrawdziwymiPrzelacznikamiZNazwa()
    {
        Ui.Run(() =>
        {
            var window = Open(1200, 680);

            try
            {
                Player(window).PanelOpen = true;
                var panel = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("PanelToggle")!;
                var queue = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("QueueToggle")!;

                Assert.True(panel.IsChecked);
                Assert.False(queue.IsChecked);
                Assert.Equal(Strings.Current["TooltipTogglePanel"], AutomationProperties.GetName(panel));
                Assert.Equal(Strings.Current["TooltipToggleQueue"], AutomationProperties.GetName(queue));

                Player(window).QueuePinned = true;
                Assert.True(queue.IsChecked);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Środek okna pomocniczego o rozmiarze w jednostkach, postawionego w punkcie w pikselach.</summary>
    private static (double X, double Y) CentreOf(PixelPoint place, Size size, double scaling) =>
        (place.X + Math.Ceiling(size.Width * scaling) / 2, place.Y + Math.Ceiling(size.Height * scaling) / 2);

    private static (double X, double Y) CentreOf(PixelRect rect) =>
        (rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);

    private static PixelRect Placed(PixelPoint place, Size size, double scaling) =>
        new(place, new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)));

    [Fact]
    public void OknoPomocniczeStajeNaSrodkuGlownego()
    {
        var owner = new PixelRect(100, 100, 1200, 680);
        var size = new Size(820, 640);
        var area = new PixelRect(0, 0, 2560, 1400);

        var place = SettingsWindow.CentreOver(owner, size, 1, area);

        Assert.Equal(new PixelPoint(290, 120), place);
        Assert.Equal(CentreOf(owner), CentreOf(place, size, 1));
    }

    [Theory]
    [InlineData(1800, 100, 2560 - 820, 120)]   // przy prawej krawędzi
    [InlineData(-300, 100, 0, 120)]            // częściowo poza lewą krawędzią
    [InlineData(100, 1000, 290, 1400 - 640)]   // przy dolnej krawędzi
    [InlineData(100, -200, 290, 0)]            // pasek tytułu nad obszarem roboczym
    public void OknoPomocniczePrzyKrawedziZostajeWObszarzeRoboczym(int ownerX, int ownerY, int x, int y)
    {
        var size = new Size(820, 640);
        var area = new PixelRect(0, 0, 2560, 1400);

        var place = SettingsWindow.CentreOver(new PixelRect(ownerX, ownerY, 1200, 680), size, 1, area);

        Assert.Equal(new PixelPoint(x, y), place);
        Assert.True(area.Contains(Placed(place, size, 1)), $"{place}");
    }

    [Fact]
    public void OknoPomocniczeWiekszeOdObszaruStajeWJegoLewymGornymRogu()
    {
        // Najmniejszy rozmiar okna bywa większy od obszaru roboczego małego ekranu: pasek tytułu
        // zostaje wtedy w obszarze, żeby dało się okno chwycić.
        var area = new PixelRect(0, 40, 1024, 560);

        Assert.Equal(new PixelPoint(0, 40),
            SettingsWindow.CentreOver(new PixelRect(0, 40, 1024, 560), new Size(1100, 600), 1, area));

        // Mieści się w szerokości, nie mieści w wysokości: na środku w poziomie, u góry obszaru.
        Assert.Equal(new PixelPoint(102, 40),
            SettingsWindow.CentreOver(new PixelRect(0, 40, 1024, 560), new Size(820, 600), 1, area));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void OknoPomocniczeNaSrodkuPrzySkalowaniu(double scaling)
    {
        // Okno główne 1100 × 650 jednostek, położenie i obszar w pikselach ekranu.
        var owner = new PixelRect(200, 120, (int)Math.Round(1100 * scaling), (int)Math.Round(650 * scaling));
        var area = new PixelRect(0, 0, 2560, 1400);
        var size = new Size(820, 470);

        var place = SettingsWindow.CentreOver(owner, size, scaling, area);
        var (x, y) = CentreOf(place, size, scaling);
        var (ownerX, ownerY) = CentreOf(owner);

        Assert.InRange(x, ownerX - 1, ownerX + 1);
        Assert.InRange(y, ownerY - 1, ownerY + 1);
        Assert.True(area.Contains(Placed(place, size, scaling)), $"{place}");
    }

    [Fact]
    public void OknoPomocniczeNaZmaksymalizowanymGlownymPrzy150Procentach()
    {
        // 1920 × 1080 przy 150 %: obszar roboczy 1920 × 1032 pikseli, czyli 1280 × 688 jednostek.
        var area = new PixelRect(0, 0, 1920, 1032);
        var size = new Size(900, 470);

        var place = SettingsWindow.CentreOver(area, size, 1.5, area);

        Assert.Equal(new PixelPoint((1920 - 1350) / 2, (1032 - 705) / 2), place);
    }

    [Fact]
    public void OknoPomocniczeNaDrugimEkranieZUjemnymiWspolrzednymi()
    {
        // Drugi monitor na lewo od głównego i wyżej od niego.
        var area = new PixelRect(-1920, -200, 1920, 1040);
        var size = new Size(820, 640);

        var place = SettingsWindow.CentreOver(new PixelRect(-1500, -150, 1200, 680), size, 1, area);
        Assert.Equal(new PixelPoint(-1310, -130), place);

        // Okno główne wystaje poza lewą krawędź drugiego ekranu.
        var edge = SettingsWindow.CentreOver(new PixelRect(-2300, -190, 1200, 680), size, 1, area);
        Assert.Equal(new PixelPoint(-1920, -170), edge);
        Assert.True(area.Contains(Placed(edge, size, 1)), $"{edge}");
    }

    [Fact]
    public void RamkaSystemowaPrzesuwaTrescWzgledemPolozeniaOkna()
    {
        // Windows: niewidoczna ramka do zmiany rozmiaru 7 px po bokach i u dołu, u góry żadnej,
        // a w oknie zmaksymalizowanym ramka wystaje poza ekran ze wszystkich stron.
        Assert.Equal(new PixelPoint(7, 0), SettingsWindow.FrameInset(new Size(974, 1047), new Size(960, 1040), 1));
        Assert.Equal(new PixelPoint(7, 7), SettingsWindow.FrameInset(new Size(1934, 1054), new Size(1920, 1040), 1));

        // Przy 150 % ramka ma 11 px; rozmiary w jednostkach, jak podaje je Avalonia.
        Assert.Equal(new PixelPoint(11, 0), SettingsWindow.FrameInset(new Size(1302 / 1.5, 1043 / 1.5), new Size(1280 / 1.5, 1032 / 1.5), 1.5));

        // Bez ramki systemowej (Linux, okna bez okien na pulpicie) treść zaczyna się w położeniu okna.
        Assert.Equal(new PixelPoint(0, 0), SettingsWindow.FrameInset(null, new Size(960, 1040), 1));
        Assert.Equal(new PixelPoint(0, 0), SettingsWindow.FrameInset(new Size(960, 1040), new Size(960, 1040), 1));
    }

    /// <summary>
    /// Pierwsze otwarcie Ustawień i otwarcie z położeniem zapamiętanym na odłączonym ekranie: okno
    /// na środku okna głównego, a nie obok niego.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OknoUstawienOtwieraSieNaSrodkuGlownego(bool savedOffScreen)
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);
            App.Settings.Current.SettingsWindow = savedOffScreen
                ? new WindowGeometry { X = 100_000, Y = 0, Width = 832, Height = 640 }
                : null;

            try
            {
                window.Position = new PixelPoint(300, 200);
                Settle(window);

                window.OpenSettings();
                var settings = window.OpenSettingsWindow!;
                Settle(settings);

                var owner = new PixelRect(window.Position, PixelSize.FromSize(window.ClientSize, window.RenderScaling));
                var (x, y) = CentreOf(settings.Position, new Size(settings.Width, settings.Height), settings.RenderScaling);
                var (ownerX, ownerY) = CentreOf(owner);
                Assert.InRange(x, ownerX - 1, ownerX + 1);
                Assert.InRange(y, ownerY - 1, ownerY + 1);

                settings.Close();
            }
            finally
            {
                window.Close();
                App.Settings.Current.SettingsWindow = null;
            }
        });
    }

    /// <summary>Okno przesunięte ręcznie wraca w zapamiętane miejsce, a nie na środek.</summary>
    [Fact]
    public void OknoUstawienWracaWZapamietaneMiejsce()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);
            App.Settings.Current.SettingsWindow = new WindowGeometry { X = 40, Y = 60, Width = 832, Height = 640 };

            try
            {
                window.OpenSettings();
                var settings = window.OpenSettingsWindow!;
                Settle(settings);

                Assert.Equal(new PixelPoint(40, 60), settings.Position);
                settings.Close();
            }
            finally
            {
                window.Close();
                App.Settings.Current.SettingsWindow = null;
            }
        });
    }

    [Fact]
    public void OknoUstawienPrzekazujeSkrotyOdtwarzaniaOknuGlownemu()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);

            try
            {
                window.OpenSettings();
                var settings = window.OpenSettingsWindow!;
                var muted = Player(window).IsMuted;

                Press(settings, Key.M);
                Assert.NotEqual(muted, Player(window).IsMuted);

                // Klawisz zużyty przez kontrolkę w oknie ustawień nie steruje odtwarzaniem.
                var area = settings.FindControl<ScrollViewer>("ContentArea")!;
                area.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true);
                area.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.M, Source = area });
                Assert.NotEqual(muted, Player(window).IsMuted);

                // Oba okna przyjmują te same skróty z tymi samymi modyfikatorami.
                foreach (var target in new Window[] { window, settings })
                {
                    target.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent, Key = Key.M, KeyModifiers = KeyModifiers.Control,
                    });
                    Assert.NotEqual(muted, Player(window).IsMuted);
                }

                settings.Close();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OknoUstawienMiesciSieNaNiskimEkranie()
    {
        // 1366×768 przy 125%: obszar roboczy ok. 1093×590 jednostek.
        Assert.Equal(590, SettingsWindow.FitLength(640, 520, 590));
        Assert.Equal(820, SettingsWindow.FitLength(820, 700, 1093));
        Assert.Equal(520, SettingsWindow.FitLength(640, 520, 480));

        // Po przycięciu okno staje u góry obszaru i mieści się w nim całe.
        var area = new PixelRect(0, 0, 1366, 738);
        var size = new Size(820, 590);
        var place = SettingsWindow.CentreOver(new PixelRect(100, 0, 1100, 650), size, 1.25, area);
        Assert.Equal(0, place.Y);
        Assert.True(area.Contains(Placed(place, size, 1.25)), $"{place}");
    }

    [Fact]
    public void ZapamietanePolozenieUstawienTolerujeRameSystemowa()
    {
        var areas = new[] { new PixelRect(0, 0, 1920, 1040) };

        // Okno przyciągnięte do lewej krawędzi zaczyna się kilka pikseli poza obszarem.
        Assert.True(SettingsWindow.FitsOn(new PixelRect(-7, 0, 974, 1047), areas, 8));
        Assert.False(SettingsWindow.FitsOn(new PixelRect(-200, 0, 820, 640), areas, 8));
        Assert.False(SettingsWindow.FitsOn(new PixelRect(2000, 100, 820, 640), areas, 8));
    }
}
