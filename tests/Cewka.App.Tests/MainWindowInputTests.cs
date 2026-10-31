using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okno główne z klawiatury i myszą: skróty, kolejka, nazwy dla czytnika ekranu.</summary>
[Collection(UiCollection.Name)]
public sealed class MainWindowInputTests
{
    /// <summary>Okno na silniku testowym. Model widoku zwalnia zamknięcie okna.</summary>
    private static (MainWindow Window, PlayerHarness Harness) Open(int tracks = 0, bool panel = true, bool queue = true)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.PanelOpen = panel;
            s.QueueOpen = queue;
            s.WindowControls = WindowControlsPosition.Right;
        });

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();

        if (tracks > 0)
        {
            h.Player.OpenPaths(Enumerable.Range(0, tracks).Select(i => h.Track("t" + i)));
            h.PumpUntil(() => h.Player.IsPlaying);
            h.Player.Pause();
        }

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

    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Centre(Visual control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static ListBox Queue(MainWindow window) => window.FindControl<ListBox>("QueueList")!;

    private static Control Row(MainWindow window, int index) => Queue(window).ContainerFromIndex(index)!;

    private static void Click(Window window, Point point, MouseButton button = MouseButton.Left)
    {
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void SpacjaPoKliknieciuPrzyciskuSterujeOdtwarzaniem()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var next = window.GetVisualDescendants().OfType<Button>()
                    .First(b => AutomationProperties.GetName(b) == Strings.Current["TooltipNext"]);
                Click(window, Centre(next, window));
                Assert.True(next.IsFocused);
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));
                var index = h.Player.CurrentIndex;
                var playing = h.Player.IsPlaying;

                Key(window, Avalonia.Input.Key.Space);
                h.PumpUntil(() => false, 150);

                Assert.Equal(index, h.Player.CurrentIndex);
                Assert.NotEqual(playing, h.Player.IsPlaying);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LiteryDzialajaTylkoBezModyfikatorow()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 1);

            try
            {
                var panel = h.Player.PanelOpen;
                Key(window, Avalonia.Input.Key.Q, RawInputModifiers.Control);
                Assert.Equal(panel, h.Player.PanelOpen);
                Key(window, Avalonia.Input.Key.Q);
                Assert.NotEqual(panel, h.Player.PanelOpen);

                var shuffle = h.Player.ShuffleEnabled;
                Key(window, Avalonia.Input.Key.S);
                Assert.NotEqual(shuffle, h.Player.ShuffleEnabled);

                Assert.False(h.Player.RepeatEnabled);
                Key(window, Avalonia.Input.Key.R);
                Assert.True(h.Player.RepeatQueue);
                Key(window, Avalonia.Input.Key.R, RawInputModifiers.Alt);
                Assert.True(h.Player.RepeatQueue);

                var muted = h.Player.IsMuted;
                Key(window, Avalonia.Input.Key.M, RawInputModifiers.Shift);
                Assert.Equal(muted, h.Player.IsMuted);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KlikniecieWierszaZaznaczaADwuklikOdtwarza()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                Assert.Equal(0, h.Player.CurrentIndex);
                var list = Queue(window);

                Click(window, Centre(Row(window, 2), window));
                Assert.Equal(2, list.SelectedIndex);
                Assert.Equal(0, h.Player.CurrentIndex);

                // Strzałka w liście przesuwa zaznaczenie, nie głośność i nie utwór.
                var volume = h.Player.Volume;
                Key(window, Avalonia.Input.Key.Up);
                Assert.Equal(1, list.SelectedIndex);
                Assert.Equal(volume, h.Player.Volume);
                Assert.Equal(0, h.Player.CurrentIndex);

                // Spacja w liście steruje odtwarzaniem i nie rusza zaznaczenia.
                var playing = h.Player.IsPlaying;
                Key(window, Avalonia.Input.Key.Space);
                Assert.NotEqual(playing, h.Player.IsPlaying);
                Assert.Equal(1, list.SelectedIndex);

                Key(window, Avalonia.Input.Key.Enter);
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 1));

                // Dwuklik z prawdziwego naciśnięcia; drugie kliknięcie w oknie bez ekranu przychodzi
                // za późno, żeby system liczył je jako podwójne.
                PointerPressedEventArgs? press = null;
                window.AddHandler(InputElement.PointerPressedEvent, (_, e) => press = e, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                Click(window, Centre(Row(window, 2), window));
                Assert.Equal(1, h.Player.CurrentIndex);

                Row(window, 2).RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, press!));
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 2), $"{h.Player.CurrentIndex}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrawyPrzyciskZaznaczaWierszPodKursoremBezOdtwarzania()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var list = Queue(window);
                list.SelectedIndex = 0;

                Click(window, Centre(Row(window, 2), window), MouseButton.Right);
                h.PumpUntil(() => false, 100);

                Assert.Equal(2, list.SelectedIndex);
                Assert.Equal(0, h.Player.CurrentIndex);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DeleteUsuwaTylkoZFokusemNaLiscieICofaSieCtrlZ()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var list = Queue(window);
                list.SelectedIndex = 1;

                // Fokus poza listą: Delete nie rusza kolejki.
                window.FindControl<Button>("PlayButton")!.Focus(NavigationMethod.Tab);
                Key(window, Avalonia.Input.Key.Delete);
                Assert.Equal(3, h.Player.Queue.Count);

                Row(window, 1).Focus(NavigationMethod.Tab);
                var removed = h.Player.Queue[1].Path;
                Key(window, Avalonia.Input.Key.Delete);
                Assert.Equal(2, h.Player.Queue.Count);
                Assert.Equal(1, list.SelectedIndex);
                Assert.True(list.IsKeyboardFocusWithin);

                Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
                Assert.Equal(3, h.Player.Queue.Count);
                Assert.Equal(removed, h.Player.Queue[1].Path);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AltStrzalkaPrzenosiZaznaczonyWiersz()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var list = Queue(window);
                var first = h.Player.Queue[0];
                list.SelectedIndex = 0;
                Row(window, 0).Focus(NavigationMethod.Tab);

                Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);

                Assert.Same(first, h.Player.Queue[1]);
                Assert.Same(first, list.SelectedItem);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrzeciagniecieWierszaZmieniaKolejnosc()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var order = h.Player.Queue.ToArray();
                var start = Centre(Row(window, 0), window);
                var end = Centre(Row(window, 2), window);

                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Point(0, 10), RawInputModifiers.LeftMouseButton);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal([order[1], order[2], order[0]], h.Player.Queue);
            }
            finally { window.Close(); }
        });
    }

    private static void Drag(Window window, Point start, Point end)
    {
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 10), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Bottom(Visual control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height), window)!.Value;

    [Fact]
    public void UpuszczenieWSzczelinieTrafiaWNajblizszyWiersz()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 4);

            try
            {
                var order = h.Player.Queue.ToArray();

                // Zaznaczony wiersz ma obramowanie i jest wyższy, więc położenie wierszy liczy się po zaznaczeniu.
                Click(window, Centre(Row(window, 0), window));
                Settle(window);

                // Środek dwupikselowej szczeliny między wierszami 1 i 2; przy równej odległości wygrywa wyższy.
                Drag(window, Centre(Row(window, 0), window), Bottom(Row(window, 1), window) + new Point(0, 1));
                Assert.Equal([order[1], order[0], order[2], order[3]], h.Player.Queue);

                // Pod ostatnim wierszem, wciąż w liście: na koniec.
                Drag(window, Centre(Row(window, 0), window), Bottom(Row(window, 3), window) + new Point(0, 30));
                Assert.Equal([order[0], order[2], order[3], order[1]], h.Player.Queue);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UpuszczenieNadListaNiczegoNiePrzenosi()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 3);

            try
            {
                var order = h.Player.Queue.ToArray();
                var list = Queue(window);
                var above = list.TranslatePoint(new Point(list.Bounds.Width / 2, -6), window)!.Value;

                Drag(window, Centre(Row(window, 1), window), above);

                Assert.Equal(order, h.Player.Queue);
            }
            finally { window.Close(); }
        });
    }

    private static bool RowVisible(MainWindow window, int index)
    {
        var list = Queue(window);
        if (list.ContainerFromIndex(index) is not { } row) return false;

        var top = row.TranslatePoint(new Point(0, 0), list)!.Value.Y;
        return top >= 0 && top + row.Bounds.Height <= list.Bounds.Height;
    }

    [Fact]
    public void ListaPokazujeGranyWierszPoKliknieciuIPoOtwarciu()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 40);

            try
            {
                // Lista przeglądana z klawiatury nie przewija się sama.
                Row(window, 0).Focus(NavigationMethod.Tab);
                h.Player.PlayItem(h.Player.Queue[35]);
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 35));
                Settle(window);
                Assert.False(RowVisible(window, 35));

                // Po kliknięciu myszą fokus zostaje w liście, a zmiana utworu i tak ją przewija.
                Click(window, Centre(Row(window, 0), window));
                Assert.True(Queue(window).IsKeyboardFocusWithin);
                h.Player.PlayItem(h.Player.Queue[36]);
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 36));
                Settle(window);
                Assert.True(RowVisible(window, 36));

                h.Player.QueueOpen = false;
                Settle(window);
                h.Player.PlayItem(h.Player.Queue[2]);
                Assert.True(h.PumpUntil(() => h.Player.CurrentIndex == 2));
                h.Player.QueueOpen = true;
                Settle(window);

                Assert.True(RowVisible(window, 2));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TabPrzechodziOdBlokuOdtwarzaniaPrzezKolejkeDoPasa()
    {
        Ui.Run(() =>
        {
            var (window, _) = Open(tracks: 2);

            try
            {
                var order = new List<IInputElement>();
                window.FindControl<ToggleButton>("PanelToggle")!.Focus(NavigationMethod.Tab);

                for (var i = 0; i < 80; i++)
                {
                    Key(window, Avalonia.Input.Key.Tab);
                    var current = window.FocusManager!.GetFocusedElement();
                    if (current is null || order.Contains(current)) break;
                    order.Add(current);
                }

                int IndexOf(Func<IInputElement, bool> match) => order.FindIndex(e => match(e));

                var play = IndexOf(e => e is Button { Name: "PlayButton" });
                var queue = IndexOf(e => e is Visual v && v.FindAncestorOfType<ListBox>(includeSelf: true) is { Name: "QueueList" });
                var fader = IndexOf(e => e is EqFader);

                Assert.True(play >= 0 && queue > play && fader > queue, $"odtwarzanie {play}, kolejka {queue}, fader {fader}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KazdaKontrolkaZFokusemMaNazwe()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 2);

            try
            {
                h.Player.ShowNotice("x");
                Settle(window);

                var unnamed = window.GetVisualDescendants().OfType<InputElement>()
                    .Where(e => e.Focusable && e.IsEffectivelyVisible && e.IsEffectivelyEnabled && e is Control)
                    .Where(e => e is not ScrollViewer and not Avalonia.Controls.Presenters.ContentPresenter)
                    .Select(e => (Control)e)
                    .Where(c => string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(c).GetName()))
                    .Select(c => $"{c.GetType().Name} {c.Name}")
                    .ToList();

                Assert.True(unnamed.Count == 0, string.Join(", ", unnamed));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MenuKolejkiPodajeSkrotyTakJakListaSkrotow()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 2);

            try
            {
                var list = Queue(window);
                var menu = list.ContextMenu!;
                Click(window, Centre(Row(window, 0), window), MouseButton.Right);
                h.PumpUntil(() => false, 100);
                Assert.True(menu.IsOpen);

                using var settings = new SettingsViewModel(h.Settings, h.Player);
                string Keys(string action) => settings.Shortcuts.Single(row => row.Action == Strings.Current[action]).Keys;

                var items = menu.Items.OfType<MenuItem>().ToList();
                MenuCommandText Command(string name) => (MenuCommandText)items.Single(item => item.Name == name).Header!;

                Assert.All(items, item => Assert.Null(item.InputGesture));
                Assert.Equal(Strings.Current["KeyEnter"], Command("QueuePlayItem").Gesture);
                Assert.Equal(Keys("ShortcutQueuePlay"), Command("QueuePlayItem").Gesture);
                Assert.Equal(Keys("ShortcutQueueRemove"), Command("QueueRemoveItem").Gesture);
                Assert.Equal(Keys("ShortcutUndo"), Command("QueueUndoItem").Gesture);
                Assert.Equal(Keys("ShortcutQueueMove"), $"{Command("QueueMoveUpItem").Gesture} {Command("QueueMoveDownItem").Gesture}");
                Assert.Equal(Strings.Current["QueuePlay"], AutomationProperties.GetName(items[0]));
                Assert.Contains(items[0].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.Current["KeyEnter"]);

                // Skróty stoją w jednej kolumnie, choć polecenia mają różną długość.
                double GestureX(MenuItem item) => item.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == ((MenuCommandText)item.Header!).Gesture).TranslatePoint(default, item)!.Value.X;
                Assert.Equal(GestureX(items[0]), GestureX(items[1]), 1.0);

                menu.Close();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrzyciskIkonaMaNazweTakaJakPodpowiedz()
    {
        Ui.Run(() =>
        {
            var (window, _) = Open(tracks: 1);

            try
            {
                var different = window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("icon") && ToolTip.GetTip(b) is string)
                    .Where(b => AutomationProperties.GetName(b) != (string)ToolTip.GetTip(b)!)
                    .Select(b => $"{b.Name}: {AutomationProperties.GetName(b)} / {ToolTip.GetTip(b)}")
                    .ToList();

                Assert.True(different.Count == 0, string.Join("; ", different));
                Assert.Contains("Ctrl+O", Strings.Current["TooltipOpenFiles"]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void GlosnikWyciszaAKolkoZmieniaGlosnosc()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open(tracks: 1);

            try
            {
                h.Player.Volume = 0.5;
                var mute = window.FindControl<Button>("MuteButton")!;
                Click(window, Centre(mute, window));
                Assert.True(h.Player.IsMuted);
                Assert.Equal(Strings.Current["TooltipUnmute"], AutomationProperties.GetName(mute));

                var slider = window.FindControl<TrackSlider>("VolumeSlider")!;
                window.MouseWheel(Centre(slider, window), new Vector(0, 1));
                Assert.Equal(0.55, h.Player.Volume, 6);
                Assert.False(h.Player.IsMuted);
                Assert.Equal(h.Player.VolumeLevelText, AutomationProperties.GetName(slider));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WyzerujAktywneTylkoPrzyZmianachKorektora()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open();

            try
            {
                var reset = window.FindControl<Button>("EqResetButton")!;
                Assert.False(reset.IsEffectivelyEnabled);

                h.Player.Bands[2].Gain = 3;
                Assert.True(reset.IsEffectivelyEnabled);

                reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, h.Player.Bands[2].Gain);
                Assert.False(reset.IsEffectivelyEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AwariaWyjsciaMaPoleceniaIOglaszaSieOdRazu()
    {
        Ui.Run(() =>
        {
            var (window, h) = Open();
            h.FailAll = true;

            try
            {
                h.Player.OpenPaths([h.Track("a")]);
                Settle(window);
                Assert.True(h.Player.HasAudioFailure);

                var text = window.FindControl<TextBlock>("AudioFailureText")!;
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(text));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.FindControl<TextBlock>("NoticeText")!));

                var retry = window.FindControl<Button>("RetryAudioButton")!;
                Assert.True(retry.IsEffectivelyVisible);

                h.FailAll = false;
                retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.False(h.Player.HasAudioFailure);

                h.Player.RequestDeviceSelection();
                var settings = window.OpenSettingsWindow!;
                Assert.True(((SettingsViewModel)settings.DataContext!).Audio.IsSelected);
                settings.Close();
            }
            finally { window.Close(); }
        });
    }
}
