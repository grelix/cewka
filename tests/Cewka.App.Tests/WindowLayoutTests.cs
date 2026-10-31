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
    private static MainWindow Open(double width, double height, bool panel = false, bool queue = false)
    {
        App.Settings.Current.Window = null;
        App.Settings.Current.PanelOpen = panel;
        App.Settings.Current.QueueOpen = queue;
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
                Assert.False(Player(window).QueueOpen);

                Player(window).QueueOpen = true;
                Settle(window);
                Assert.True(window.QueueOverlayOpen);
                Assert.True(window.FindControl<Border>("QueueDismiss")!.IsVisible);

                Press(window, Key.Escape);
                Assert.False(Player(window).QueueOpen);
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
                Assert.True(Player(window).QueueOpen);

                window.Width = 900;
                Settle(window);
                Assert.True(window.QueueIsOverlay);
                Assert.False(Player(window).QueueOpen);

                // Zwężenie nie zmienia zamiaru zapisanego w ustawieniach, także przed zamknięciem.
                Assert.True(App.Settings.Current.QueueOpen);

                window.Width = 1200;
                Settle(window);
                Assert.False(window.QueueIsOverlay);
                Assert.True(Player(window).QueueOpen);
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

                Player(window).QueueOpen = true;
                Player(window).QueueOpen = false;
                Assert.False(App.Settings.Current.QueueOpen);

                window.Width = 1200;
                Settle(window);
                Assert.False(Player(window).QueueOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PokazaniePasaPrzeliczaPlyteBezZmianyRozmiaru()
    {
        Ui.Run(() =>
        {
            var window = Open(1200, 600);

            try
            {
                var disc = window.FindControl<Panel>("DiscHost")!;
                var without = disc.Width;

                Player(window).PanelOpen = true;
                Settle(window);

                Assert.True(disc.Width < without, $"płyta {disc.Width} przy pasie, {without} bez pasa");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PelnyEkranWracaDoMaksymalizacjiIEscZNiegoWychodzi()
    {
        Ui.Run(() =>
        {
            var window = Open(1100, 600);

            try
            {
                window.WindowState = WindowState.Maximized;
                window.ToggleFullScreen();
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                Press(window, Key.Escape);
                Assert.Equal(WindowState.Maximized, window.WindowState);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EscNajpierwZamykaNakladkePotemPelnyEkran()
    {
        Ui.Run(() =>
        {
            var window = Open(900, 620);

            try
            {
                window.ToggleFullScreen();
                Player(window).QueueOpen = true;
                Settle(window);

                Press(window, Key.Escape);
                Assert.False(Player(window).QueueOpen);
                Assert.Equal(WindowState.FullScreen, window.WindowState);

                Press(window, Key.Escape);
                Assert.Equal(WindowState.Normal, window.WindowState);
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
    public void GeometriaZPelnegoEkranuToZwykleOknoIMaksymalizacja()
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
                window.ToggleFullScreen();
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
    [InlineData(false)]
    [InlineData(true)]
    public void OknoZapisaneNaOdlaczonymEkranieMiesciSieNaPozostalym(bool maximized)
    {
        Ui.Run(() =>
        {
            var probe = new Window();
            var area = probe.Screens.All.First().WorkingArea;
            App.Settings.Current.WindowControls = WindowControlsPosition.Right;
            App.Settings.Current.Window = new WindowGeometry
            {
                X = area.Right, Y = 0, Width = 2500, Height = 1400, Maximized = maximized,
            };

            var window = new MainWindow();

            try
            {
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
            var window = Open(1200, 680, panel: true);

            try
            {
                var panel = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("PanelToggle")!;
                var queue = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("QueueToggle")!;

                Assert.True(panel.IsChecked);
                Assert.False(queue.IsChecked);
                Assert.Equal(Strings.Current["TooltipTogglePanel"], AutomationProperties.GetName(panel));
                Assert.Equal(Strings.Current["TooltipToggleQueue"], AutomationProperties.GetName(queue));

                Player(window).QueueOpen = true;
                Assert.True(queue.IsChecked);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OknoUstawienOtwieraSieObokGlownego()
    {
        var owner = new PixelRect(100, 100, 1200, 680);
        var size = new PixelSize(820, 640);

        // Miejsce po prawej.
        Assert.Equal(new PixelPoint(1312, 100),
            SettingsWindow.Beside(owner, size, new PixelRect(0, 0, 2560, 1400), 12));

        // Brak miejsca po prawej, jest po lewej.
        Assert.Equal(new PixelPoint(1800 - 12 - 820, 100),
            SettingsWindow.Beside(new PixelRect(1800, 100, 1200, 680), size, new PixelRect(0, 0, 3000, 1400), 12));

        // Nigdzie się nie mieści: przy krawędzi po stronie z większą ilością miejsca.
        var tight = SettingsWindow.Beside(new PixelRect(200, 50, 1200, 680), size, new PixelRect(0, 0, 1600, 900), 12);
        Assert.Equal(1600 - 820, tight.X);
        Assert.Equal(50, tight.Y);
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
        var size = new PixelSize(1025, 738);
        var place = SettingsWindow.Beside(new PixelRect(100, 0, 1100, 650), size, area, 15);
        Assert.True(area.Contains(new PixelRect(place, size)), $"{place}");
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
