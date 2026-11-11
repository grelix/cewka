using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.Tests.SceneInput;
using Cewka.App.Views;
using Cewka.Platform;
using Xunit;
using AvaloniaHeadlessPlatform = Avalonia.Headless.AvaloniaHeadlessPlatform;
using Shapes = Avalonia.Controls.Shapes;

namespace Cewka.App.Tests;

/// <summary>Scena pełnoekranowa w oknie bez ekranu: wejście, klawisze, warstwa, pauza, blokada.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneTests
{
    private sealed class FakeWakeLock : IDisplayWakeLock
    {
        public bool IsHeld { get; private set; }

        public bool Disposed { get; private set; }

        public void Acquire() => IsHeld = true;

        public void Release() => IsHeld = false;

        public void Dispose()
        {
            Disposed = true;
            IsHeld = false;
        }
    }

    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public required FakeWakeLock WakeLock { get; init; }

        public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(1000);

        public SceneView Scene => Window.Scene;

        public void Wait(double seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Scene.AdvanceTimers();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    /// <summary>
    /// Okno 640×360, poniżej najmniejszego dozwolonego, albo podanej wielkości, na silniku testowym,
    /// z atrapą blokady i zegarem sceny testu. Kadr ma te same proporcje, a klatka rysuje się programowo,
    /// więc mniejsze okno oszczędza czas tam, gdzie liczą się tylko jednostki kadru. Test z pikselami
    /// wpisanymi na stałe albo z układem samego okna podaje rozmiar sam.
    /// </summary>
    private static Rig Open(int tracks = 2, bool play = true, Action<AppSettings>? configure = null, bool maximised = false,
        int width = 640, int height = 360)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Full;
            configure?.Invoke(s);
        });

        var fake = new FakeWakeLock();
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = width, Height = height, WakeLockFactory = () => fake };
        var rig = new Rig { Window = window, Harness = h, WakeLock = fake };
        window.Scene.Clock = () => rig.Now;
        window.Show();

        if (maximised) window.WindowState = WindowState.Maximized;

        if (tracks > 0)
        {
            h.OpenPaths(Enumerable.Range(0, tracks).Select(i => h.Track("s" + i)));
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero));
            if (!play) h.Player.Pause();
        }

        Settle(window);
        return rig;
    }

    /// <summary>
    /// Cztery wymuszone klatki: zmiana zatwierdzona w jednej rysuje się w następnej, a reakcja na
    /// rysowanie (zgłoszenie sondy GPU, przeliczenie najechania po przesunięciu treści) zmienia drzewo
    /// znowu i potrzebuje kolejnych dwóch. Zegar okna sam tyka tylko raz na sekundę
    /// (<see cref="TestApp.BuildAvaloniaApp"/>), więc brakujących klatek nie dołoży.
    /// </summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Klatki kompozytora, dopóki scena po przenikaniu nie ukryje treści okna.</summary>
    private static bool WaitCovered(Rig rig, int milliseconds = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (rig.Window.WindowContent.IsVisible)
        {
            if (DateTime.UtcNow > end) return false;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        return true;
    }

    /// <summary>
    /// Jedna klatka kompozytora na żądanie testu. Klatki nie powstają same między wywołaniami
    /// pomocników, więc test sam ustala, ile czasu mija między nimi.
    /// </summary>
    private static void NextFrame(Window window)
    {
        var drawn = false;
        window.RequestAnimationFrame(_ => drawn = true);

        var end = DateTime.UtcNow.AddSeconds(3);
        while (!drawn)
        {
            Assert.True(DateTime.UtcNow < end, "klatka nie nadeszła");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (!drawn) Thread.Sleep(1);
        }
    }

    /// <summary>Czas przenikania sceny, z którego scena liczy też zakrycie okna.</summary>
    private static TimeSpan SceneFade(SceneView scene) =>
        scene.Transitions!.OfType<DoubleTransition>().Single(t => t.Property == Visual.OpacityProperty).Duration;

    private static Point Centre(Visual control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    /// <summary>Trącenie myszy w pustej części sceny, z dala od warstwy: ruch ponad próg.</summary>
    private static void Nudge(Rig rig)
    {
        var y = rig.Window.Bounds.Height * 0.05;
        rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4, y));
        rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4 + 40, y));
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Scene.Chrome.IsShown);
    }

    /// <summary>Element ma pierścień fokusu, czyli fokus z klawiatury.</summary>
    private static bool HasRing(Control control) => control.Classes.Contains(":focus-visible");

    /// <summary>Krycie docelowe; bieżące jest w trakcie przenikania. Nieustawione to krycie domyślne.</summary>
    private static double Target(Visual visual) =>
        visual.GetBaseValue(Visual.OpacityProperty) is { HasValue: true } target ? target.Value : visual.Opacity;

    /// <summary>Dwuklik z prawdziwego naciśnięcia; okno bez ekranu nie liczy drugiego kliknięcia jako podwójnego.</summary>
    private static void DoubleClick(Window window, Control target)
    {
        PointerPressedEventArgs? press = null;
        void Capture(object? sender, PointerPressedEventArgs e) => press = e;

        window.AddHandler(InputElement.PointerPressedEvent, Capture, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        var point = Centre(target, window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.RemoveHandler(InputElement.PointerPressedEvent, Capture);

        target.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, press!));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Zdarzenie wejścia w tych testach rysuje najwyżej kilka klatek, także przy kręcącej się płycie.
    /// Pomocniki okna bez ekranu rysują wtedy około 30 na każde zdarzenie.
    /// </summary>
    [Fact]
    public void ZdarzenieWejsciaPrzyKrecacejSiePlycieRysujeKilkaKlatek()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.True(rig.Scene.Spinner.IsDrawing);

            var frames = 0;
            var counting = true;
            void Count(TimeSpan now)
            {
                if (!counting) return;
                frames++;
                rig.Window.RequestAnimationFrame(Count);
            }

            rig.Window.RequestAnimationFrame(Count);
            rig.Window.MouseMove(new Point(10, 10));
            counting = false;

            Assert.InRange(frames, 1, 6);
        });
    }

    // ---------- 1. Wejście i wyjście ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F11OtwieraIZamykaSceneAStanOknaWraca(bool maximised)
    {
        Ui.Run(() =>
        {
            using var rig = Open(maximised: maximised);
            var expected = maximised ? WindowState.Maximized : WindowState.Normal;

            Press(rig.Window, Key.F11);
            Assert.True(rig.Window.IsSceneOpen);
            Assert.Equal(WindowState.FullScreen, rig.Window.WindowState);
            Assert.True(rig.Scene.IsVisible);
            Assert.True(WaitCovered(rig));
            Assert.False(rig.Window.Backdrop.IsVisible);

            Press(rig.Window, Key.F11);
            Assert.False(rig.Window.IsSceneOpen);
            Assert.Equal(expected, rig.Window.WindowState);
            Assert.False(rig.Scene.IsVisible);
            Assert.True(rig.Window.WindowContent.IsVisible);
            Assert.True(rig.Window.Backdrop.IsVisible);
        });
    }

    [Fact]
    public void WejscieDoScenyPrzenikaNadWidocznaTrescOkna()
    {
        Ui.Run(() =>
        {
            // Najmniejsze dozwolone okno, które pokazuje płytę: klatka musi być krótsza niż przenikanie.
            using var rig = Open(width: 760, height: 520);
            Settle(rig.Window);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            rig.Window.EnterScene();

            // Scena przenika nad treścią okna: treść i tło są widoczne, ale ich pętle stoją.
            Assert.True(rig.Scene.IsVisible);
            Assert.Equal(1, Target(rig.Scene));
            Assert.True(rig.Window.WindowContent.IsVisible);
            Assert.True(rig.Window.Backdrop.IsVisible);
            Assert.True(rig.Window.Scrim.IsVisible);

            RunFrames(rig.Window, 100);
            Assert.True(rig.Window.WindowContent.IsVisible);
            Assert.InRange(rig.Scene.Opacity, 0.01, 0.99);
            Assert.False(rig.Window.IsRotating);
            Assert.False(rig.Window.Backdrop.IsDrawing);

            // Treść znika dopiero pod całkiem zakrytą sceną.
            Assert.True(WaitCovered(rig));
            Assert.True(watch.ElapsedMilliseconds >= 280, $"po {watch.ElapsedMilliseconds} ms");
            Assert.True(rig.Scene.Opacity > 0.9, $"krycie {rig.Scene.Opacity}");
            Assert.False(rig.Window.Backdrop.IsVisible);
            Assert.False(rig.Window.Scrim.IsVisible);

            // Wyjście jest natychmiastowe, a pętle okna ruszają.
            rig.Window.ExitScene();
            Assert.True(rig.Window.WindowContent.IsVisible);
            Assert.False(rig.Scene.IsVisible);
            RunFrames(rig.Window, 100);
            Assert.True(rig.Window.IsRotating);
        });
    }

    /// <summary>
    /// Wyjście przed zakryciem wznawia pętle okna, a ponowne wejście unieważnia zakrycie
    /// zamówione przy poprzednim: treść okna zostaje, choć termin starego zgłoszenia już minął.
    /// </summary>
    [Fact]
    public void WyjscieIPonowneWejscieWTrakciePrzenikaniaNieZostawiaOknaWstrzymanego()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            Settle(rig.Window);

            // Wyjście przed zakryciem: treść była cały czas widoczna, pętle i tak ruszają.
            rig.Window.EnterScene();
            RunFrames(rig.Window, 100);
            rig.Window.ExitScene();
            RunFrames(rig.Window, 100);
            Assert.True(rig.Window.IsRotating);
            Assert.True(rig.Window.WindowContent.IsVisible);

            // Zakrycie z poprzedniego wejścia nie ukrywa treści na początku nowego przenikania.
            // Przenikanie liczy się w klatkach, a tych do wyjścia była jedna: przerwa dłuższa niż
            // przenikanie sprawia, że termin starego zgłoszenia przypada na pierwszą klatkę
            // nowego wejścia, a termin nowego jeszcze nie.
            rig.Window.EnterScene();
            NextFrame(rig.Window);
            Thread.Sleep(SceneFade(rig.Scene) + TimeSpan.FromMilliseconds(100));
            rig.Window.ExitScene();

            // Klatka pełnoekranowej sceny rysuje się programowo nawet kilkaset milisekund (w Linuksie
            // pierwsza już przy wejściu), więc do następnej klatki może minąć całe nowe przenikanie.
            // Błędem jest dopiero zakrycie wcześniejsze niż przenikanie liczone od nowego wejścia;
            // przenikanie liczy się w czasie klatek, więc i tu mierzy je zegar rzeczywisty.
            var entered = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan? covered = null;
            void OnCovered() => covered ??= entered.Elapsed;
            rig.Scene.Covered += OnCovered;
            try
            {
                rig.Window.EnterScene();
                NextFrame(rig.Window);
                Assert.True(rig.Window.WindowContent.IsVisible || covered >= SceneFade(rig.Scene), $"zakrycie po {covered}");
            }
            finally
            {
                rig.Scene.Covered -= OnCovered;
            }

            Assert.True(WaitCovered(rig));
        });
    }

    [Fact]
    public void ScenaOtwieraSiePrzyciskiemWPaskuIDwuklikiemWPlyte()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);

            rig.Window.FullScreenButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(rig.Window.IsSceneOpen);
            rig.Window.ExitScene();

            DoubleClick(rig.Window, rig.Window.DiscHost);
            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>Kolejka otwarta, fokus z klawiatury na drugim wierszu.</summary>
    private static Control FocusQueueRow(Rig rig)
    {
        rig.Harness.Player.QueuePinned = true;
        Settle(rig.Window);

        var row = rig.Window.QueueList.ContainerFromIndex(1)!;
        rig.Window.QueueList.SelectedIndex = 1;
        Assert.True(row.Focus(NavigationMethod.Directional));
        Settle(rig.Window);
        Assert.Contains(":focus-visible", row.Classes);
        return row;
    }

    [Fact]
    public void FokusPoWyjsciuKlawiszemWracaDoWierszaKolejkiZPierscieniem()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3, width: 1280, height: 720);
            var row = FocusQueueRow(rig);

            Press(rig.Window, Key.F11);
            Assert.True(rig.Window.IsSceneOpen);
            Press(rig.Window, Key.Escape);
            Assert.False(rig.Window.IsSceneOpen);

            Assert.Same(row, Focused(rig.Window));
            Assert.Contains(":focus-visible", row.Classes);

            // Enter znów należy do kolejki, a nie otwiera sceny.
            Press(rig.Window, Key.Enter);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    [Fact]
    public void PoWejsciuMyszaFokusNieTrafiaNaPelnyEkran()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3, width: 1280, height: 720);
            var row = FocusQueueRow(rig);

            // Kliknięcie przycisku w pasku tytułu daje mu fokus, zanim otworzy scenę.
            var button = rig.Window.FullScreenButton;
            button.Focus(NavigationMethod.Pointer);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(rig.Window.IsSceneOpen);

            rig.Scene.ExitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(rig.Window.IsSceneOpen);
            Assert.Same(row, Focused(rig.Window));

            // Bez wcześniejszego fokusu w oknie fokus i tak nie zostaje na przycisku.
            rig.Harness.Player.QueuePinned = false;
            Settle(rig.Window);
            button.Focus(NavigationMethod.Pointer);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            rig.Window.ExitScene();
            Assert.NotSame(button, Focused(rig.Window));

            Press(rig.Window, Key.Enter);
            Assert.False(rig.Window.IsSceneOpen);

            // Dwuklik w płytę: fokus też nie trafia na przycisk.
            DoubleClick(rig.Window, rig.Window.DiscHost);
            Assert.True(rig.Window.IsSceneOpen);
            rig.Window.ExitScene();
            Assert.NotSame(button, Focused(rig.Window));
        });
    }

    [Fact]
    public void DwuklikWPustaCzescScenyIPrzyciskZamykajaScene()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Dwuklik w przycisk niczego nie zamyka, w pustą część sceny zamyka scenę.
            rig.Scene.Chrome.RevealByPointer();
            DoubleClickAt(rig, Centre(rig.Scene.NextButton, rig.Window));
            Assert.True(rig.Window.IsSceneOpen);

            DoubleClickAt(rig, Empty(rig));
            Assert.False(rig.Window.IsSceneOpen);

            rig.Window.EnterScene();
            rig.Scene.ExitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    // ---------- 2. Esc i Backspace ----------

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Back)]
    public void EscIBackspaceZamykajaScenePodczasRegulacjiPaska(Key key)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            // ↑ odsłania warstwę z fokusem na Odtwórz/Wstrzymaj, kolejne ↑ idzie do paska postępu.
            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.Seek, Focused(rig.Window));

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Scene.Chrome.IsAdjusting);
            Assert.Contains("adjusting", rig.Scene.Seek.Classes);

            Press(rig.Window, key);
            Assert.False(rig.Window.IsSceneOpen);
            Assert.False(rig.Scene.Chrome.IsAdjusting);
            Assert.DoesNotContain("adjusting", rig.Scene.Seek.Classes);
        });
    }

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Back)]
    public void EscIBackspaceZamykajaSceneZFokusemKlawiaturyWRzedzie(Key key)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);

            Press(rig.Window, key);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    [Fact]
    public void KoniecRegulacjiZdejmujeWyrozniniePaskaPoZmianieGlosnosci()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Enter);
            Press(rig.Window, Key.Add);
            Assert.Contains("adjusting", rig.Scene.Seek.Classes);

            Press(rig.Window, Key.Enter);
            Assert.False(rig.Scene.Chrome.IsAdjusting);
            Assert.DoesNotContain("adjusting", rig.Scene.Seek.Classes);
        });
    }

    /// <summary>Fokus na pasku postępu i regulacja strzałkami: ↑, ↑, Enter.</summary>
    private static void StartAdjusting(Rig rig)
    {
        Press(rig.Window, Key.Up);
        Press(rig.Window, Key.Up);
        Press(rig.Window, Key.Enter);
        Assert.True(rig.Scene.Chrome.IsAdjusting);
    }

    [Fact]
    public void StrzalkaZPaskaWRegulacjiKonczyRegulacjeIStrzalkiZnowPrzenosza()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);
            StartAdjusting(rig);

            Press(rig.Window, Key.Down);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));
            Assert.False(rig.Scene.Chrome.IsAdjusting);
            Assert.DoesNotContain("adjusting", rig.Scene.Seek.Classes);

            var position = player.Progress;
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
            Assert.Equal(position, player.Progress);

            // Enter naciska przycisk z fokusem, zamiast kończyć niewidoczną regulację.
            Press(rig.Window, Key.Enter);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));
        });
    }

    /// <summary>
    /// Pasek bez regulacji nie przewija: Home, End, ← i → przy fokusie na pasku zostawiają
    /// położenie, a po Enterze (regulacja) Home i End przenoszą na początek i koniec utworu.
    /// </summary>
    [Fact]
    public void HomeIEndPrzewijajaPasekTylkoWRegulacji()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 1, play: false);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            SeekTo(rig.Harness, 30);
            Settle(rig.Window);
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(seek, Focused(rig.Window));
            Assert.False(rig.Scene.Chrome.IsAdjusting);
            Assert.Equal(0.5, seek.Value, 3);

            foreach (var key in new[] { Key.Home, Key.End, Key.Left, Key.Right })
            {
                Press(rig.Window, key);
                Assert.Equal(0.5, seek.Value, 3);
                Assert.Equal(0.5, player.Progress, 3);
                Assert.Same(seek, Focused(rig.Window));
            }

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Scene.Chrome.IsAdjusting);

            Press(rig.Window, Key.Home);
            Assert.Equal(0, seek.Value);
            Press(rig.Window, Key.End);
            Assert.Equal(1, seek.Value);
        });
    }

    [Fact]
    public void TabZPaskaWRegulacjiKonczyRegulacje()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            StartAdjusting(rig);

            Press(rig.Window, Key.Tab);
            Assert.NotSame(rig.Scene.Seek, Focused(rig.Window));
            Assert.False(rig.Scene.Chrome.IsAdjusting);
        });
    }

    [Fact]
    public void KlikMyszaWInnyPrzyciskPodczasRegulacjiPozwalaWarstwieZgasnac()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            StartAdjusting(rig);

            rig.Scene.PlayButton.Focus(NavigationMethod.Pointer);
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Scene.Chrome.IsAdjusting);

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    [Fact]
    public void EscZamykaSceneOdslonietaMysza()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            rig.Window.EnterScene();
            Settle(rig.Window);

            rig.Window.MouseMove(new Point(300, 300));
            rig.Window.MouseMove(new Point(400, 360));
            Assert.True(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Escape);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    // ---------- 3. Licznik, kursor, próg ruchu, pierwszy klik ----------

    [Fact]
    public void WarstwaIKursorGasnaRazemPoCzterechSekundach()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.True(rig.Scene.IsCursorHidden);

            // Ruch poniżej ośmiu jednostek (przy 720 px wysokości to ok. 5 px) nic nie odsłania.
            rig.Window.MouseMove(new Point(300, 300));
            rig.Window.MouseMove(new Point(303, 302));
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            rig.Window.MouseMove(new Point(330, 300));
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.IsCursorHidden);
            Assert.Equal(1, Target(rig.Scene.TransportRow));

            rig.Wait(4.1);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.Equal(0, Target(rig.Scene.TransportRow));
            Assert.False(rig.Scene.TransportRow.IsHitTestVisible);
        });
    }

    /// <summary>
    /// Fokus z klawiatury nie trzyma warstwy: po odsłonięciu klawiszem i czterech sekundach bez
    /// klawiszy warstwa gaśnie, a fokus przechodzi na korzeń sceny. Kolejny klawisz odsłania ją
    /// znowu i przywraca fokus na ostatnio użyty element, a nie na Odtwórz/Wstrzymaj.
    /// </summary>
    [Fact]
    public void WarstwaOdslonietaKlawiszemGasniePoCzterechSekundachBezKlawiszy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            Press(rig.Window, Key.Left);
            Assert.Same(rig.Scene.PreviousButton, Focused(rig.Window));

            rig.Wait(3.9);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(0.2);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.Same(rig.Scene, Focused(rig.Window));
            Assert.Equal(0, Target(rig.Scene.TransportRow));
            Assert.True(rig.Scene.IsCursorHidden);

            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.Same(rig.Scene.PreviousButton, Focused(rig.Window));
        });
    }

    /// <summary>
    /// Pilot przy telewizorze: fokus na „Następny” i Enter co kilka sekund. Pierwszy Enter po
    /// zgaszeniu warstwy tylko ją odsłania, z fokusem dalej na „Następny”, a drugi przechodzi do
    /// następnego utworu zamiast wstrzymać odtwarzanie.
    /// </summary>
    [Fact]
    public void EnterPoPrzerwieOdslaniaWarstweZFokusemNaNastepnymADrugiPrzechodziDalej()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            var clicks = 0;
            rig.Scene.NextButton.Click += (_, _) => clicks++;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            // Pierwszy Enter nie naciska „Następny”, a silnik po stałej liczbie okresów gra ten sam utwór.
            Assert.Equal(0, clicks);
            rig.Harness.PumpPeriods(50);
            Assert.Equal(0, player.CurrentIndex);
            Assert.True(player.IsPlaying);

            Press(rig.Window, Key.Enter);
            Assert.Equal(1, clicks);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1), $"utwór {player.CurrentIndex}");
            Assert.True(player.IsPlaying);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
        });
    }

    /// <summary>
    /// Fokus na Powtarzaj, warstwa gaśnie. Strzałka w prawo przy ukrytej warstwie dalej przewija
    /// utwór i niczego nie odsłania, a Tab odsłania warstwę z fokusem z powrotem na Powtarzaj.
    /// </summary>
    [Fact]
    public void TabPoZgaszeniuWarstwyWracaNaPowtarzaj()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Right);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.RepeatButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            var position = player.Progress;
            Press(rig.Window, Key.Right);

            // Odświeżenie wprost: zegar okna nie ma gwarancji, że zdąży przy obciążonym dyspozytorze.
            var moved = rig.Harness.PumpUntil(() =>
            {
                player.RefreshForCapture();
                return player.Progress > position;
            });
            Assert.True(moved, $"postęp {player.Progress} z {position}, pozycja {rig.Harness.Engine.Position}, "
                + $"długość {rig.Harness.Engine.Duration}, przewijanie {rig.Harness.Engine.IsSeeking}");
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));

            Press(rig.Window, Key.Tab);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene.RepeatButton, Focused(rig.Window));
        });
    }

    /// <summary>Nowe wejście do sceny zaczyna od Odtwórz/Wstrzymaj, bez fokusu zapamiętanego przy poprzednim.</summary>
    [Fact]
    public void PonowneWejscieDoScenyZaczynaFokusOdOdtworzWstrzymaj()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            rig.Window.ExitScene();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));
        });
    }

    /// <summary>
    /// Odsłonięcie myszą zostawia fokus na korzeniu sceny, a następna strzałka w górę przywraca
    /// element, który miał fokus przy zgaszeniu warstwy.
    /// </summary>
    [Fact]
    public void OdslonieciemMyszaNiePrzenosiFokusuAStrzalkaWracaNaOstatniElement()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Left);
            Press(rig.Window, Key.Left);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            rig.Window.MouseMove(new Point(300, 300));
            rig.Window.MouseMove(new Point(400, 360));
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));

            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
        });
    }

    /// <summary>
    /// Trącenie myszy przy warstwie odsłoniętej klawiszem nie zdejmuje trybu klawiatury: fokus
    /// z pierścieniem zostaje na „Następny”, a Enter naciska ten przycisk, zamiast przenosić fokus.
    /// </summary>
    [Fact]
    public void PoTracieniuMyszyEnterNaciskaNastepny()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            Nudge(rig);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
            Assert.True(HasRing(rig.Scene.NextButton));

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1), $"utwór {player.CurrentIndex}");
            Assert.True(player.IsPlaying);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
        });
    }

    /// <summary>Spacja na Losowo po trąceniu myszy przełącza Losowo, a odtwarzanie trwa dalej.</summary>
    [Fact]
    public void PoTracieniuMyszySpacjaNaLosowoNieWstrzymujeOdtwarzania()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Left);
            Press(rig.Window, Key.Left);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));

            Nudge(rig);
            var shuffle = player.ShuffleEnabled;
            Press(rig.Window, Key.Space);
            Assert.NotEqual(shuffle, player.ShuffleEnabled);
            Assert.True(player.IsPlaying);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
        });
    }

    /// <summary>Strzałka po trąceniu myszy przenosi fokus od bieżącego elementu, a nie od Odtwórz/Wstrzymaj.</summary>
    [Fact]
    public void PoTracieniuMyszyStrzalkaPrzenosiFokusOdBiezacegoElementu()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            Nudge(rig);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.RepeatButton, Focused(rig.Window));
            Assert.True(HasRing(rig.Scene.RepeatButton));
        });
    }

    /// <summary>
    /// Klik myszą w przycisk warstwy zdejmuje pierścień i tryb klawiatury, także po trąceniu:
    /// w przycisk z fokusem z klawiatury i w inny przycisk.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KlikMyszaWPrzyciskNieZostawiaPierscienia(bool focusedButton)
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            Assert.True(HasRing(rig.Scene.NextButton));

            Nudge(rig);
            var player = rig.Harness.Player;
            var shuffle = player.ShuffleEnabled;
            var target = focusedButton ? (Button)rig.Scene.NextButton : rig.Scene.ShuffleButton;
            var point = Centre(target, rig.Window);
            rig.Window.MouseMove(point);
            rig.Window.MouseDown(point, MouseButton.Left);
            rig.Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // Klik trafia w przycisk.
            if (focusedButton) Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1), $"utwór {player.CurrentIndex}");
            else Assert.NotEqual(shuffle, player.ShuffleEnabled);

            Assert.Same(target, Focused(rig.Window));
            Assert.False(HasRing(rig.Scene.ShuffleButton));
            Assert.False(HasRing(rig.Scene.NextButton));
            Assert.False(rig.Scene.Chrome.HasKeyboardFocus);
        });
    }

    /// <summary>
    /// Klawisz odsłaniający przywraca fokus najpierw na element warstwy, który ma go teraz: po
    /// zgaszeniu warstwy z fokusem na „Następny”, odsłonięciu myszą i kliknięciu w Losowo strzałka
    /// zostawia fokus na Losowo, już z pierścieniem, a nie wraca na „Następny”.
    /// </summary>
    [Fact]
    public void KlawiszPoKliknieciuWLosowoZostawiaFokusNaLosowo()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));

            Nudge(rig);
            var shuffle = Centre(rig.Scene.ShuffleButton, rig.Window);
            rig.Window.MouseMove(shuffle);
            rig.Window.MouseDown(shuffle, MouseButton.Left);
            rig.Window.MouseUp(shuffle, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
            Assert.False(HasRing(rig.Scene.ShuffleButton));

            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
            Assert.True(HasRing(rig.Scene.ShuffleButton));
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
        });
    }

    /// <summary>Każdy klawisz liczy czas od nowa, więc klawisze naciskane co trzy sekundy trzymają warstwę.</summary>
    [Fact]
    public void KlawiszeCoTrzySekundyTrzymajaWarstwe()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            for (var i = 0; i < 5; i++)
            {
                rig.Wait(3);
                Press(rig.Window, i % 2 == 0 ? Key.Right : Key.Left);
                Assert.True(rig.Scene.Chrome.IsShown, $"po {3 * (i + 1)} s");
            }

            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));
        });
    }

    [Fact]
    public void KolkoMyszyOdslaniaWarstweIKursorILiczyCzasOdNowa()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Kursor stoi w pustej części sceny; samo kółko, bez ruchu.
            var point = new Point(300, 300);
            rig.Window.MouseMove(point);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            rig.Window.MouseWheel(point, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.IsCursorHidden);

            rig.Wait(3);
            rig.Window.MouseWheel(point, new Vector(0, -1));
            rig.Wait(3);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(1.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            // Esc zamyka scenę także po odsłonięciu warstwy kółkiem.
            rig.Window.MouseWheel(point, new Vector(0, 1));
            Press(rig.Window, Key.Escape);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Suwak postępu przeciągnięty nad pustą część sceny trzyma warstwę, a puszczony tam, gdy mysz
    /// potem stoi, pozwala jej zgasnąć po zwykłym czasie.
    /// </summary>
    [Fact]
    public void SuwakPuszczonyPozaWarstwaPozwalaJejZgasnac()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            var start = seek.TranslatePoint(new Point(seek.Bounds.Width * 0.3, seek.Bounds.Height / 2), rig.Window)!.Value;
            var empty = new Point(rig.Window.Bounds.Width / 2, rig.Window.Bounds.Height * 0.3);

            // Pierwsze zdarzenie wskaźnika w scenie tylko zapamiętuje położenie; odsłania dopiero ruch.
            rig.Window.MouseMove(empty);
            rig.Window.MouseMove(start);
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);

            // Przeciąganie trzyma warstwę dłużej niż zwykły czas.
            rig.Window.MouseDown(start, MouseButton.Left);
            rig.Window.MouseMove(empty);
            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Window.MouseUp(empty, MouseButton.Left);
            Settle(rig.Window);

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Przeciąga suwak od 30% do 45% szerokości, naciska klawisz, a potem rusza myszą daleko w prawo
    /// i puszcza przycisk. Wynik: wartość suwaka przy klawiszu, po puszczeniu i element, który
    /// przechwytywał mysz przy ruchu po klawiszu.
    /// </summary>
    private static (double AtKey, double AfterRelease, IInputElement? Captured) DragAndPress(Rig rig, TrackSlider slider, Key key)
    {
        var window = rig.Window;
        Point At(double fraction) =>
            slider.TranslatePoint(new Point(slider.Bounds.Width * fraction, slider.Bounds.Height / 2), window)!.Value;

        window.MouseDown(At(0.3), MouseButton.Left);
        window.MouseMove(At(0.45));
        Dispatcher.UIThread.RunJobs();
        var atKey = slider.Value;

        Press(window, key);

        IInputElement? captured = null;
        void Watch(object? sender, PointerEventArgs e) => captured = e.Pointer.Captured;
        window.AddHandler(InputElement.PointerMovedEvent, Watch, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            var far = new Point(window.Bounds.Width * 0.9, window.Bounds.Height * 0.5);
            window.MouseMove(far);
            window.MouseUp(far, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.RemoveHandler(InputElement.PointerMovedEvent, Watch);
        }

        return (atKey, slider.Value, captured);
    }

    /// <summary>
    /// Esc albo F11 w trakcie przeciągania paska sceny zamyka scenę i kończy przeciąganie: ruch
    /// i puszczenie po wyjściu nie przesuwają paska, a mysz wraca do okna.
    /// </summary>
    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.F11)]
    public void WyjscieWTrakciePrzeciaganiaPaskaScenyKonczyPrzeciaganie(Key key)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            MoveTo(rig, Centre(seek, rig.Window));
            Assert.True(rig.Scene.Chrome.IsShown);

            var (atKey, afterRelease, captured) = DragAndPress(rig, seek, key);
            Assert.False(rig.Window.IsSceneOpen);
            Assert.NotSame(seek, captured);
            Assert.Equal(atKey, afterRelease, 6);
        });
    }

    /// <summary>
    /// F11 w trakcie przeciągania paska przewijania kolejki otwiera scenę i zwalnia przechwycenie:
    /// ruch i puszczenie nad sceną nie trafiają już do kontrolki pod nią. Korektor jest w osobnym oknie.
    /// </summary>
    [Fact]
    public void WejscieDoScenyWTrakciePrzeciaganiaPaskaKolejkiZwalniaPrzechwycenie()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 80, width: 1280, height: 720, configure: s => s.QueuePinned = true);
            var window = rig.Window;
            Control control = window.QueueScrollBar;

            IPointer? pointer = null;
            void Remember(object? sender, PointerPressedEventArgs e) => pointer ??= e.Pointer;
            window.AddHandler(InputElement.PointerPressedEvent, Remember, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            var start = Centre(control, window);
            try
            {
                window.MouseDown(start, MouseButton.Left);
            }
            finally
            {
                window.RemoveHandler(InputElement.PointerPressedEvent, Remember);
            }

            window.MouseMove(start + new Point(0, 8));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(control, pointer?.Captured);

            Press(window, Key.F11);
            Assert.True(window.IsSceneOpen);
            Assert.Null(pointer!.Captured);

            window.MouseUp(start, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>
    /// F11 w trakcie przeciągania wiersza kolejki przerywa przeciąganie: ani puszczenie nad sceną,
    /// ani po wyjściu klik innym przyciskiem na wierszu kolejki nie przenoszą utworu.
    /// </summary>
    [Theory]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void WejscieDoScenyWTrakciePrzeciaganiaWKolejcePrzerywaPrzeciaganie(MouseButton button)
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 5, width: 1280, height: 720, configure: s => s.QueuePinned = true);
            var window = rig.Window;
            var player = rig.Harness.Player;
            var order = player.Queue.ToArray();
            Control Row(int index) => window.QueueList.ContainerFromIndex(index)!;

            var start = Centre(Row(0), window);
            var target = Centre(Row(2), window);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 10), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            Press(window, Key.F11);
            Assert.True(window.IsSceneOpen);
            window.MouseUp(new Point(300, 200), MouseButton.Left);
            Press(window, Key.Escape);
            Assert.False(window.IsSceneOpen);
            Settle(window);

            window.MouseDown(target, button);
            window.MouseUp(target, button);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(order, player.Queue.ToArray());
        });
    }

    /// <summary>F11 w trakcie przeciągania paska postępu okna otwiera scenę i kończy przeciąganie.</summary>
    [Fact]
    public void WejscieDoScenyWTrakciePrzeciaganiaPaskaOknaKonczyPrzeciaganie()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            var seek = rig.Window.Seek;

            var (atKey, afterRelease, captured) = DragAndPress(rig, seek, Key.F11);
            Assert.True(rig.Window.IsSceneOpen);
            Assert.NotSame(seek, captured);
            Assert.Equal(atKey, afterRelease, 6);
        });
    }

    /// <summary>
    /// Kółko pod nieruchomym kursorem, który stoi tam, gdzie po odsłonięciu wyrośnie przycisk:
    /// najechanie przelicza się dopiero przy następnej klatce, a warstwa i tak nie gaśnie.
    /// </summary>
    [Fact]
    public void KolkoPodKursoremNadPrzyciskiemNieGasiWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = rig.Scene.FindControl<Button>("NextButton")!;
            var point = next.TranslatePoint(new Point(next.Bounds.Width / 2, next.Bounds.Height / 2), rig.Window)!.Value;

            rig.Window.MouseMove(point);
            Assert.False(rig.Scene.Chrome.IsShown);

            rig.Window.MouseWheel(point, new Vector(0, -1));
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Kursor stoi nad „Następny” bez ruchu, a warstwę odsłania klawisz: najechanie, które
    /// zgłasza się przy następnej klatce, nie trzyma warstwy, bo od odsłonięcia wskaźnik się nie ruszył.
    /// </summary>
    [Fact]
    public void NieruchomyKursorNadRzedemNieTrzymaWarstwyOdslonietejKlawiszem()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Pierwsze zdarzenie wskaźnika tylko zapamiętuje położenie.
            rig.Window.MouseMove(Centre(rig.Scene.NextButton, rig.Window));
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Up);
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>Odsłonięcie klawiszem i pauzą zostawia kursor ukryty; pokazuje go dopiero ruch wskaźnika.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OdslonieciKlawiszemIPauzaNiePokazujeKursora(bool pause)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.True(rig.Scene.IsCursorHidden);

            if (pause)
            {
                rig.Harness.Player.Pause();
                Dispatcher.UIThread.RunJobs();
            }
            else
            {
                Press(rig.Window, Key.Up);
            }

            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            Nudge(rig);
            Assert.False(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>Pauza przy nieruchomym kursorze nad rzędem: warstwa gaśnie po zwykłym czasie pauzy.</summary>
    [Fact]
    public void PoPauzieNieruchomyKursorNadRzedemNieTrzymaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            rig.Window.MouseMove(Centre(rig.Scene.NextButton, rig.Window));
            rig.Harness.Player.Pause();
            Dispatcher.UIThread.RunJobs();
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(29);
            Assert.True(rig.Scene.Chrome.IsShown);
            rig.Wait(1.5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>Po odsłonięciu klawiszem rzeczywisty ruch wskaźnika nad rząd trzyma warstwę jak zwykle.</summary>
    [Fact]
    public void NajechanieZRuchemPoOdslonieciuKlawiszemTrzymaWarstwe()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4, rig.Window.Bounds.Height * 0.05));
            rig.Window.MouseMove(Centre(rig.Scene.NextButton, rig.Window));
            Settle(rig.Window);
            Assert.False(rig.Scene.IsCursorHidden);

            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>Utwory z podanymi tytułami w tagach; gra pierwszy.</summary>
    private static Rig OpenWithTitles(params string[] titles)
    {
        var rig = Open(tracks: 0);
        var h = rig.Harness;
        h.OpenPaths(titles.Select((title, i) => h.TaggedTrack("t" + i, title, "Daniel Bloom", "Lovely Fear")).ToList());
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Player.Title == titles[0]), $"tytuł „{h.Player.Title}”");
        return rig;
    }

    /// <summary>Ruch wskaźnika ponad próg: z górnej, pustej części sceny do podanego punktu.</summary>
    private static void MoveTo(Rig rig, Point point)
    {
        rig.Window.MouseMove(new Point(rig.Window.Bounds.Width * 0.4, rig.Window.Bounds.Height * 0.05));
        rig.Window.MouseMove(point);
        Settle(rig.Window);
    }

    /// <summary>
    /// Kursor po ruchu stoi przy górnym brzegu „Następny” i klika. Tytuł następnego utworu zajmuje
    /// dwa wiersze, więc rząd zjeżdża w dół spod nieruchomego kursora, a warstwa gaśnie po zwykłym czasie.
    /// </summary>
    [Fact]
    public void WarstwaGasniePoZjechaniuRzeduSpodNieruchomegoKursora()
    {
        Ui.Run(() =>
        {
            using var rig = OpenWithTitles("Katarakta", LongTitle);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = rig.Scene.NextButton;
            var top = next.TranslatePoint(new Point(next.Bounds.Width / 2, 3), rig.Window)!.Value;
            MoveTo(rig, top);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(next.IsPointerOver);

            rig.Window.MouseDown(top, MouseButton.Left);
            rig.Window.MouseUp(top, MouseButton.Left);
            Assert.True(rig.Harness.PumpUntil(() => player.Title == LongTitle), $"tytuł „{player.Title}”");
            Settle(rig.Window);
            Assert.False(rig.Scene.TransportRow.IsPointerOver);

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Kursor po ruchu stoi tuż nad rzędem i zostaje tam, gdy warstwa gaśnie. Strzałka odsłania ją
    /// bez kursora, a krótszy tytuł następnego utworu wsuwa rząd pod nieruchomy kursor: warstwa
    /// gaśnie po zwykłym czasie.
    /// </summary>
    [Fact]
    public void RzadWsunietyPodNieruchomyKursorNieTrzymaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = OpenWithTitles(LongTitle, "Katarakta");
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = rig.Scene.NextButton;
            var above = next.TranslatePoint(new Point(next.Bounds.Width / 2, -12), rig.Window)!.Value;
            MoveTo(rig, above);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.TransportRow.IsPointerOver);

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.PageDown);
            Assert.True(rig.Harness.PumpUntil(() => player.Title == "Katarakta"), $"tytuł „{player.Title}”");
            Settle(rig.Window);
            Assert.True(new Rect(next.Bounds.Size).Contains(rig.Window.TranslatePoint(above, next)!.Value), "rząd nie wjechał pod kursor");

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Klawisz po ruchu myszy ukrywa kursor, a nieruchomy kursor nad rzędem przestaje trzymać
    /// warstwę; ruch myszy przywraca jedno i drugie.
    /// </summary>
    [Fact]
    public void KlawiszPoMyszyUkrywaKursorANajechanieNieTrzymaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = Centre(rig.Scene.NextButton, rig.Window);
            MoveTo(rig, next);
            Assert.False(rig.Scene.IsCursorHidden);
            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Up);
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.False(rig.Scene.NextButton.IsPointerOver);

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);

            rig.Window.MouseMove(next + new Point(12, 0));
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.IsCursorHidden);

            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Sam modyfikator, klawisz spoza sceny i klawisz głośności nie chowają kursora nad „Następny”:
    /// chowa go tylko klawisz, który obsługuje scena albo element warstwy.
    /// </summary>
    [Theory]
    [InlineData(Key.LeftCtrl, RawInputModifiers.Control)]
    [InlineData(Key.LeftShift, RawInputModifiers.Shift)]
    [InlineData(Key.LWin, RawInputModifiers.Meta)]
    [InlineData(Key.PrintScreen, RawInputModifiers.None)]
    [InlineData(Key.VolumeUp, RawInputModifiers.None)]
    [InlineData(Key.VolumeMute, RawInputModifiers.None)]
    public void ModyfikatorIKlawiszSpozaScenyNieChowajaKursora(Key key, RawInputModifiers modifiers)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            MoveTo(rig, Centre(rig.Scene.NextButton, rig.Window));
            Assert.False(rig.Scene.IsCursorHidden);

            Press(rig.Window, key, modifiers);
            Assert.False(rig.Scene.IsCursorHidden);

            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Rząd przycisków jest wyrównany do lewej, więc kursor w pustej części kolumny na prawo od niego
    /// nie jest nad warstwą i jej nie trzyma.
    /// </summary>
    [Fact]
    public void KursorWKolumnieObokRzeduNieTrzymaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var row = rig.Scene.TransportRow;
            var beside = row.TranslatePoint(new Point(row.Bounds.Width + 120, row.Bounds.Height / 2), rig.Window)!.Value;
            var column = rig.Scene.InfoColumn;
            Assert.True(new Rect(column.Bounds.Size).Contains(rig.Window.TranslatePoint(beside, column)!.Value), "punkt poza kolumną");

            MoveTo(rig, beside);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>Kursor po ruchu w odstępie między przyciskami rzędu jest nad warstwą i ją trzyma.</summary>
    [Fact]
    public void KursorMiedzyPrzyciskamiRzeduTrzymaWarstwe()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var previous = rig.Scene.PreviousButton;
            var gap = previous.TranslatePoint(new Point(previous.Bounds.Width + 12, previous.Bounds.Height / 2), rig.Window)!.Value;
            MoveTo(rig, gap);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(previous.IsPointerOver);
            Assert.False(rig.Scene.PlayButton.IsPointerOver);

            rig.Wait(5);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>Punkt w pustej części sceny: w kadrze na lewo od płyty, z dala od kolumny i wyjścia.</summary>
    private static Point Empty(Rig rig) => rig.Scene.Plane.TranslatePoint(new Point(60, 540), rig.Window)!.Value;

    private static void Click(Rig rig, Point point, MouseButton button = MouseButton.Left)
    {
        rig.Window.MouseDown(point, button);
        rig.Window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Dwuklik w punkcie: zwykłe kliknięcie, a potem naciśnięcie z licznikiem 2 i puszczenie na
    /// elemencie pod wskaźnikiem. Okno bez ekranu liczy kliknięcia według czasu rzeczywistego,
    /// a klatka sceny trwa nieraz dłużej niż czas dwukliku, więc drugie kliknięcie powstaje jawnie.
    /// </summary>
    private static void DoubleClickAt(Rig rig, Point point, MouseButton button = MouseButton.Left) =>
        PressAgain(rig, point, ClickCapturing(rig, point, button), button);

    /// <summary>Zwykłe kliknięcie; wynik to jego naciśnięcie, od którego liczy się dwuklik.</summary>
    private static PointerPressedEventArgs ClickCapturing(Rig rig, Point point, MouseButton button = MouseButton.Left)
    {
        var window = rig.Window;
        PointerPressedEventArgs? first = null;
        void Capture(object? sender, PointerPressedEventArgs e) => first ??= e;

        window.AddHandler(InputElement.PointerPressedEvent, Capture, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            Click(rig, point, button);
        }
        finally
        {
            window.RemoveHandler(InputElement.PointerPressedEvent, Capture);
        }

        return first!;
    }

    /// <summary>Drugie naciśnięcie dwukliku zaczętego kliknięciem <paramref name="first"/>, z puszczeniem.</summary>
    private static void PressAgain(Rig rig, Point point, PointerPressedEventArgs first, MouseButton button = MouseButton.Left)
    {
        var window = rig.Window;
        var right = button == MouseButton.Right;
        var properties = new PointerPointProperties(
            right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
            right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed);
        var source = Assert.IsAssignableFrom<Avalonia.Interactivity.Interactive>(window.InputHitTest(point));
        var pointer = first.Pointer;
        source.RaiseEvent(new PointerPressedEventArgs(source, pointer, window, point, first.Timestamp + 1,
            properties, KeyModifiers.None, clickCount: 2));

        var released = pointer.Captured as Avalonia.Interactivity.Interactive ?? source;
        released.RaiseEvent(new PointerReleasedEventArgs(released, pointer, window, point, first.Timestamp + 2,
            new PointerPointProperties(RawInputModifiers.None,
                right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, button));
        pointer.Capture(null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Dwuklik prawym przyciskiem w pustą część sceny jej nie zamyka.</summary>
    [Fact]
    public void PrawyDwuklikWPustaCzescNieZamykaSceny()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Nudge(rig);

            var empty = Empty(rig);
            rig.Window.MouseMove(empty);
            DoubleClickAt(rig, empty, MouseButton.Right);
            Assert.True(rig.Window.IsSceneOpen);

            // Lewy dwuklik obok zamyka.
            var left = empty + new Point(20, 0);
            rig.Window.MouseMove(left);
            DoubleClickAt(rig, left);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Prawy, a zaraz po nim lewy klik w pustą część: licznik kliknięć nie patrzy na przycisk i daje
    /// lewemu 2, ale scenę zamykają tylko dwa lewe naciśnięcia.
    /// </summary>
    [Fact]
    public void PrawyIZarazLewyKlikWPustaCzescNieZamykajaSceny()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Nudge(rig);

            var empty = Empty(rig);
            rig.Window.MouseMove(empty);
            PressAgain(rig, empty, ClickCapturing(rig, empty, MouseButton.Right), MouseButton.Left);
            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Dwuklik, którego pierwsze naciśnięcie tylko odsłania warstwę (albo pokazuje kursor przy
    /// warstwie odsłoniętej strzałką): w części warstwy drugie naciśnięcie jest połykane, więc nie
    /// naciska przycisku pod kursorem, a w pustej części zamyka scenę jak każdy dwuklik.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DrugieNacisniecieDwuklikuPoOdslonieciuJestPolykaneTylkoWCzesciWarstwy(bool key, bool empty)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            if (key)
            {
                Press(rig.Window, Key.Up);
                Settle(rig.Window);
                Assert.True(rig.Scene.IsCursorHidden);
            }

            var point = empty ? Empty(rig) : Centre(rig.Scene.PlayButton, rig.Window);
            DoubleClickAt(rig, point);

            if (empty)
            {
                Assert.False(rig.Window.IsSceneOpen);
                return;
            }

            Assert.True(rig.Window.IsSceneOpen);
            Assert.True(player.IsPlaying);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Szybki dwuklik w „Następny”: po pierwszym kliknięciu tytuł następnego utworu zajmuje dwa
    /// wiersze i rząd zjeżdża o 36 spod kursora, więc drugie naciśnięcie trafia w pustą część
    /// kolumny. Dwuklik zaczęty w części warstwy sceny nie zamyka.
    /// </summary>
    [Fact]
    public void DwuklikZaczetyWPrzyciskuNieZamykaScenyPoZjechaniuRzedu()
    {
        Ui.Run(() =>
        {
            using var rig = OpenWithTitles("Katarakta", LongTitle);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = rig.Scene.NextButton;
            var top = next.TranslatePoint(new Point(next.Bounds.Width / 2, 3), rig.Window)!.Value;
            MoveTo(rig, top);
            var before = next.TranslatePoint(default, rig.Scene.Plane)!.Value.Y;

            var first = ClickCapturing(rig, top);
            Assert.True(rig.Harness.PumpUntil(() => player.Title == LongTitle), $"tytuł „{player.Title}”");
            Settle(rig.Window);
            // Kolumna stoi na środku, więc drugi wiersz tytułu przesuwa rząd o pół wiersza.
            Assert.Equal(before + rig.Scene.SceneTitle.LineHeight / 2, next.TranslatePoint(default, rig.Scene.Plane)!.Value.Y, 1);
            var hit = Assert.IsAssignableFrom<Visual>(rig.Window.InputHitTest(top));
            Assert.False(hit == rig.Scene.TransportRow || rig.Scene.TransportRow.IsVisualAncestorOf(hit));

            PressAgain(rig, top, first);
            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>Dwuklik w odstęp między przyciskami rzędu jest dwuklikiem w warstwę, nie w pustą część.</summary>
    [Fact]
    public void DwuklikWOdstepRzeduNieZamykaSceny()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var previous = rig.Scene.PreviousButton;
            var gap = previous.TranslatePoint(new Point(previous.Bounds.Width + 12, previous.Bounds.Height / 2), rig.Window)!.Value;
            MoveTo(rig, gap);
            DoubleClickAt(rig, gap);

            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Element warstwy zapamiętuje się przy każdym fokusie, także z myszy: po kliknięciu w Losowo
    /// i w pustą część sceny strzałka po zgaszeniu warstwy wraca na Losowo, a nie na „Następny”,
    /// który miał fokus z klawiatury przy wcześniejszym zgaszeniu.
    /// </summary>
    [Fact]
    public void StrzalkaWracaNaLosowoKliknieteMyszaPrzedKliknieciemObok()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            Nudge(rig);
            var shuffle = Centre(rig.Scene.ShuffleButton, rig.Window);
            rig.Window.MouseMove(shuffle);
            Click(rig, shuffle);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));

            var empty = Empty(rig);
            rig.Window.MouseMove(empty);
            Click(rig, empty);
            Assert.Same(rig.Scene, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
            Assert.True(HasRing(rig.Scene.ShuffleButton));
        });
    }

    /// <summary>
    /// Zdarzenie wskaźnika w tym samym miejscu okna po zmianie wielkości kadru wypada gdzie indziej
    /// w jednostkach kadru, ale nie jest ruchem: pierwsze zdarzenie po zmianie skali tylko
    /// zapamiętuje położenie.
    /// </summary>
    [Fact]
    public void ZmianaRozmiaruKadruMiedzyZdarzeniamiBezRuchuNieOdslaniaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var point = new Point(300, 300);
            rig.Window.MouseMove(point);
            Assert.False(rig.Scene.Chrome.IsShown);

            var scale = rig.Scene.Scale;
            rig.Window.Width = 1920;
            rig.Window.Height = 1080;
            Settle(rig.Window);
            Assert.NotEqual(scale, rig.Scene.Scale, 3);

            rig.Window.MouseMove(point);
            Assert.False(rig.Scene.Chrome.IsShown);

            // Ruch po zmianie skali dalej odsłania warstwę.
            rig.Window.MouseMove(point + new Point(40, 0));
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Klik myszą w pasek postępu w regulacji kończy regulację: uchwyt wraca do 20, a fokus zostaje
    /// na pasku, już bez pierścienia.
    /// </summary>
    [Fact]
    public void KlikMyszaWPasekWRegulacjiKonczyRegulacjeBezPierscienia()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            StartAdjusting(rig);
            Settle(rig.Window);
            Assert.Equal(28, seek.ThumbDiameter);
            Assert.True(HasRing(seek));

            Nudge(rig);
            var point = Centre(seek, rig.Window);
            rig.Window.MouseMove(point);
            Click(rig, point);
            Settle(rig.Window);

            Assert.False(rig.Scene.Chrome.IsAdjusting);
            Assert.Equal(20, seek.ThumbDiameter);
            Assert.Same(seek, Focused(rig.Window));
            Assert.False(HasRing(seek));
        });
    }

    /// <summary>
    /// Stuknięcie jak z ekranu dotykowego albo pióra. Okno bez ekranu nie ma dotyku, więc naciśnięcie
    /// i puszczenie powstają jawnie, jak w Avalonii: w elemencie pod palcem, który od razu
    /// przechwytuje wskaźnik, a puszczenie trafia tam, gdzie jest przechwycenie.
    /// </summary>
    private static void Tap(Rig rig, Point point, PointerType type = PointerType.Touch)
    {
        var window = rig.Window;
        var pointer = new Pointer(Pointer.GetNextFreeId(), type, isPrimary: true);
        var source = Assert.IsAssignableFrom<Avalonia.Interactivity.Interactive>(window.InputHitTest(point));
        pointer.Capture((IInputElement)source);
        source.RaiseEvent(new PointerPressedEventArgs(source, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

        var released = pointer.Captured as Avalonia.Interactivity.Interactive ?? source;
        released.RaiseEvent(new PointerReleasedEventArgs(released, pointer, window, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Warstwa odsłonięta klawiszem, kursor myszy ukryty: stuknięcie w „Następny” naciska go od razu
    /// i nie pokazuje kursora myszy. Klik myszą bez ruchu dalej niczego nie naciska.
    /// </summary>
    [Theory]
    [InlineData(PointerType.Touch)]
    [InlineData(PointerType.Pen)]
    public void StukniecieWPrzyciskPrzyWarstwieOdslonietejKlawiszemNaciskaGoBezKursora(PointerType type)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Settle(rig.Window);
            Assert.True(rig.Scene.IsCursorHidden);

            var title = player.Title;
            Tap(rig, Centre(rig.Scene.NextButton, rig.Window), type);
            Assert.True(rig.Harness.PumpUntil(() => player.Title != title), $"tytuł „{player.Title}”");
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            Click(rig, Centre(rig.Scene.PlayButton, rig.Window));
            Assert.True(player.IsPlaying);
            Assert.False(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Przy ukrytej warstwie stuknięcie tylko ją odsłania, bez kursora myszy; następne stuknięcie
    /// naciska przycisk.
    /// </summary>
    [Fact]
    public void StukniecieDotykiemPrzyUkrytejWarstwieTylkoJaOdslaniaBezKursora()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var play = Centre(rig.Scene.PlayButton, rig.Window);
            Tap(rig, play);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(player.IsPlaying);
            Assert.True(rig.Scene.IsCursorHidden);

            Tap(rig, play);
            Assert.False(player.IsPlaying);
            Assert.True(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Dotyk w przycisk z fokusem z klawiatury nie odbija fokusu przez korzeń sceny: odbicie dotyczy
    /// tylko myszy. Okno bez ekranu nie ma dotyku, więc naciśnięcie powstaje jawnie.
    /// </summary>
    [Fact]
    public void DotykNieOdbijaFokusuPrzezKorzen()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Right);
            var next = rig.Scene.NextButton;
            Assert.True(HasRing(next));
            Nudge(rig);

            var focusedRoot = false;
            void Watch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            {
                if (ReferenceEquals(e.Source, rig.Scene)) focusedRoot = true;
            }

            rig.Scene.AddHandler(InputElement.GotFocusEvent, Watch, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            try
            {
                var touch = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
                var point = Centre(next, rig.Window);
                var press = new PointerPressedEventArgs(next, touch, rig.Window, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None);
                next.RaiseEvent(press);
                Dispatcher.UIThread.RunJobs();
                touch.Capture(null);
            }
            finally
            {
                rig.Scene.RemoveHandler(InputElement.GotFocusEvent, Watch);
            }

            Assert.False(focusedRoot, "fokus przeszedł przez korzeń sceny");
        });
    }

    /// <summary>
    /// Wyjście gasi warstwę bez przenikania, ale nie zostawia przejść rzędu i wyjścia jako wartości
    /// lokalnej: po ponownym wejściu pochodzą ze stylu, a pasek ma z powrotem swoje.
    /// </summary>
    [Fact]
    public void PoWyjsciuPrzejsciaRzeduIWyjsciaPochodzaZeStylu()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var scene = rig.Scene;
            var seekTransitions = scene.Seek.Transitions;
            rig.Window.EnterScene();
            Settle(rig.Window);
            Nudge(rig);

            rig.Window.ExitScene();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Assert.NotNull(seekTransitions);
            Assert.Same(seekTransitions, scene.Seek.Transitions);
            Assert.Equal(Avalonia.Data.BindingPriority.LocalValue, scene.Seek.GetDiagnostic(Animatable.TransitionsProperty).Priority);
            foreach (var part in new Control[] { scene.TransportRow, scene.ExitButton })
            {
                Assert.NotEqual(Avalonia.Data.BindingPriority.LocalValue, part.GetDiagnostic(Animatable.TransitionsProperty).Priority);
                AssertFade(part.Transitions, Visual.OpacityProperty);
            }
        });
    }

    /// <summary>
    /// Wyjście przyciskiem Zamknij przy widocznej warstwie i ponowne wejście: rząd, wyjście i uchwyt
    /// paska mają od razu krycie 0, bez przenikania od krycia sprzed wyjścia.
    /// </summary>
    [Fact]
    public void PonowneWejsciePoWyjsciuZWidocznaWarstwaNieMigaCzesciami()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var scene = rig.Scene;
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Rząd i wyjście przenikają w czasie klatek.
            Nudge(rig);
            RunFrames(rig.Window, 300);
            Assert.Equal(1, scene.TransportRow.Opacity);
            Assert.Equal(1, scene.ExitButton.Opacity);

            var exit = Centre(scene.ExitButton, rig.Window);
            rig.Window.MouseMove(exit);
            Click(rig, exit);
            Assert.False(rig.Window.IsSceneOpen);

            rig.Window.EnterScene();
            Assert.Equal(0, scene.TransportRow.Opacity);
            Assert.Equal(0, scene.ExitButton.Opacity);
            Assert.Equal(0, scene.Seek.ThumbOpacity);

            Settle(rig.Window);
            Assert.Equal(0, scene.TransportRow.Opacity);
            Assert.Equal(0, scene.ExitButton.Opacity);
            Assert.Equal(0, scene.Seek.ThumbOpacity);
        });
    }

    [Fact]
    public void PierwszyKlikPrzyUkrytejWarstwieTylkoJaOdslania()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var play = Centre(rig.Scene.PlayButton, rig.Window);
            var playing = rig.Harness.Player.IsPlaying;

            rig.Window.MouseDown(play, MouseButton.Left);
            rig.Window.MouseUp(play, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Equal(playing, rig.Harness.Player.IsPlaying);

            // Drugi, osobny klik trafia już w przycisk. Kilka pikseli obok, bo w tym samym miejscu
            // okno policzyłoby go jako drugie naciśnięcie dwukliku.
            var again = play + new Point(6, 0);
            rig.Window.MouseDown(again, MouseButton.Left);
            rig.Window.MouseUp(again, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(playing, rig.Harness.Player.IsPlaying);
        });
    }

    /// <summary>
    /// Warstwa odsłonięta strzałką, kursor ukryty: klik w pasek postępu bez ruchu wskaźnika tylko
    /// pokazuje kursor, a położenie i fokus zostają.
    /// </summary>
    [Fact]
    public void KlikPrzyUkrytymKursorzeWPasekTylkoPokazujeKursor()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            var seek = rig.Scene.Seek;
            var value = seek.Value;
            var point = Centre(seek, rig.Window);
            rig.Window.MouseDown(point, MouseButton.Left);
            rig.Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(value, seek.Value);
            Assert.False(rig.Scene.IsCursorHidden);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
        });
    }

    /// <summary>
    /// Pauza odsłania warstwę bez kursora: przycisk wyjścia pod nieruchomym kursorem nie jest
    /// podświetlony, a klik w niego bez ruchu wskaźnika sceny nie zamyka.
    /// </summary>
    [Fact]
    public void KlikPrzyUkrytymKursorzeWZamknijPoPauzieNieZamykaSceny()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var exit = Centre(rig.Scene.ExitButton, rig.Window);
            rig.Window.MouseMove(exit);

            rig.Harness.Player.Pause();
            Dispatcher.UIThread.RunJobs();
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.False(rig.Scene.ExitButton.IsPointerOver);

            rig.Window.MouseDown(exit, MouseButton.Left);
            rig.Window.MouseUp(exit, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.True(rig.Window.IsSceneOpen);
            Assert.False(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Nieruchomy kursor nad paskiem postępu, warstwę odsłania strzałka: element pod wskaźnikiem
    /// nie jest paskiem, więc kursor zostaje ukryty zamiast stać się dłonią, a pasek nie ma najechania.
    /// </summary>
    [Fact]
    public void NadPaskiemPoStrzalceKursorZostajeUkryty()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            var point = Centre(seek, rig.Window);
            rig.Window.MouseMove(point);

            Press(rig.Window, Key.Up);
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            var under = Assert.IsAssignableFrom<InputElement>(rig.Window.InputHitTest(point));
            Assert.NotSame(seek, under);
            Assert.False(seek.IsVisualAncestorOf(under));
            Assert.Same(rig.Scene.Cursor, under.Cursor);
            Assert.False(seek.IsPointerOver);
        });
    }

    /// <summary>Przycisk sceny ma tło najechania (bez przenikania, które dzieje się w klatkach).</summary>
    private static bool Highlighted(Button button) =>
        button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter")
            .GetBaseValue(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty) is { HasValue: true } background
        && background.Value is ISolidColorBrush { Color.A: > 0 };

    /// <summary>
    /// W pauzie scena nie rysuje klatek, więc Avalonia nie przelicza najechania. Kursor stoi bez
    /// ruchu nad „Następny” od chwili, gdy warstwa była ukryta; pauza odsłania ją bez kursora,
    /// a klik albo kółko go pokazuje. Przycisk pod pokazanym kursorem trzyma warstwę od razu.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WPauzieKlikAlboKolkoNadPrzyciskiemTrzymaWarstweBezKlatki(bool wheel)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = Centre(rig.Scene.NextButton, rig.Window);
            rig.Window.MouseMove(next);
            player.Pause();
            Dispatcher.UIThread.RunJobs();
            Settle(rig.Window);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            var title = player.Title;
            if (wheel) rig.Window.MouseWheel(next, new Vector(0, -1));
            else Click(rig, next);
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Scene.IsCursorHidden);
            Assert.Equal(title, player.Title);

            rig.Wait(SceneChrome.PausedHideDelay.TotalSeconds + 1);
            Assert.True(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Kursor po ruchu nad punktem i jeszcze jeden drobny ruch: pierwszy pokazuje kursor, a przy
    /// drugim Avalonia przelicza najechanie już z częściami warstwy przyjmującymi wskaźnik.
    /// </summary>
    private static void Hover(Rig rig, Point point)
    {
        MoveTo(rig, point);
        rig.Window.MouseMove(point + new Point(1, 0));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Klawisz zdarzeniami wprost na elemencie z fokusem, bez klatki. Pomocniki okna bez ekranu
    /// rysują klatkę przy każdym klawiszu, a w niej Avalonia przelicza najechanie.
    /// </summary>
    private static void PressWithoutFrame(Rig rig, Key key)
    {
        var target = Focused(rig.Window) as Avalonia.Interactivity.Interactive ?? rig.Window;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// W pauzie scena nie rysuje klatek, a klawisz bez widocznej zmiany (M) też żadnej nie wywoła.
    /// Chowa kursor, a z nim dłoń nad paskiem, tło najechania przycisku i otwartą podpowiedź
    /// wyjścia, choć Avalonia nie musiała przeliczyć najechania. Czy zdążyła, zależy od zegara
    /// renderu, więc test sprawdza tylko skutek, a nie stan <c>IsPointerOver</c> po klawiszu.
    /// </summary>
    [Fact]
    public void WPauzieKlawiszChowaDlonPodswietlenieIPodpowiedzBezKlatki()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            rig.Harness.Player.Pause();
            Dispatcher.UIThread.RunJobs();
            Settle(rig.Window);

            var seek = rig.Scene.Seek;
            Hover(rig, Centre(seek, rig.Window));
            Assert.True(seek.IsPointerOver);
            Assert.NotSame(rig.Scene.Cursor, seek.Cursor);
            PressWithoutFrame(rig, Key.M);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.Same(rig.Scene.Cursor, seek.Cursor);

            var next = rig.Scene.NextButton;
            Hover(rig, Centre(next, rig.Window));
            Assert.True(Highlighted(next));
            PressWithoutFrame(rig, Key.M);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.False(Highlighted(next));

            var exit = rig.Scene.ExitButton;
            Hover(rig, Centre(exit, rig.Window));
            ToolTip.SetIsOpen(exit, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ToolTip.GetIsOpen(exit));
            PressWithoutFrame(rig, Key.M);
            Assert.True(rig.Scene.IsCursorHidden);
            Assert.False(ToolTip.GetIsOpen(exit));
        });
    }

    /// <summary>
    /// Wskaźnik wychodzi z okna znad przycisku: ruch poza oknem nie trafia do sceny, więc ostatnie
    /// położenie nad przyciskiem nie może trzymać warstwy. Gaśnie po zwykłym czasie.
    /// </summary>
    [Fact]
    public void WyjscieWskaznikaZOknaZnadPrzyciskuNieTrzymaWarstwy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var next = rig.Scene.NextButton;
            Hover(rig, Centre(next, rig.Window));
            Assert.True(next.IsPointerOver);
            rig.Wait(rig.Scene.Chrome.HideAfter.TotalSeconds + 1);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Window.MouseMove(new Point(-20, -20));
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Scene.IsPointerOver);
            rig.Wait(rig.Scene.Chrome.HideAfter.TotalSeconds + 1);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    /// <summary>
    /// Najechanie przy ukrytym kursorze myszy (pióro, nieruchoma mysz) nie daje przyciskowi tła: ani
    /// tła najechania sceny, ani tła z motywu Fluent, z którym wygrywa przezroczyste tło bazowe sceny.
    /// </summary>
    [Fact]
    public void NajechanieBezWidocznegoKursoraNieDajeTlaPrzycisku()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.IsCursorHidden);

            var next = rig.Scene.NextButton;
            ((IPseudoClasses)next.Classes).Set(":pointerover", true);
            Settle(rig.Window);
            Assert.False(Highlighted(next));
        });
    }

    /// <summary>
    /// Kursor stoi nad przyciskiem dłużej niż licznik, potem zjeżdża na pustą część sceny:
    /// warstwę gasi sam DispatcherTimer sceny, bez innych zdarzeń. Czas sceny przesuwa test, bo
    /// jedna klatka okna bez ekranu może trwać dłużej niż skrócony licznik; zegar dyspozytora tyka
    /// naprawdę, w przerwach <c>await</c>.
    /// </summary>
    [Fact]
    public void ZegarScenyGasiWarstwePoZejsciuKursoraZPrzyciskuPoDlugimPostoju()
    {
        Ui.RunAsync(async () =>
        {
            using var rig = Open(width: 1280, height: 720);
            var hide = TimeSpan.FromSeconds(0.3);
            rig.Scene.Chrome.HideAfter = hide;
            rig.Window.EnterScene();
            Settle(rig.Window);

            rig.Window.MouseMove(new Point(300, 300));
            rig.Window.MouseMove(new Point(340, 300));
            Assert.True(rig.Scene.Chrome.IsShown);

            // Kursor stoi nad przyciskiem dłużej niż licznik: zegar tyka i staje.
            rig.Window.MouseMove(Centre(rig.Scene.NextButton, rig.Window));
            rig.Now += hide * 3;
            await Task.Delay(700);
            Assert.True(rig.Scene.Chrome.IsShown);

            // Zjazd na pustą część sceny: warstwa gaśnie po zwykłym czasie, bez innych zdarzeń.
            rig.Window.MouseMove(new Point(300, 200));
            rig.Now += hide + TimeSpan.FromSeconds(0.1);
            Assert.True(await Ui.UntilAsync(() => !rig.Scene.Chrome.IsShown, 2000), "warstwa nie zgasła");
            Assert.True(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Wznowienie przestawia DispatcherTimer sceny z czasu pauzy na zwykły. Czas sceny przesuwa
    /// test, a zegar dyspozytora tyka naprawdę; przy liczniku pauzy nie tyknąłby w czasie testu.
    /// </summary>
    [Fact]
    public void ZegarScenyPoWznowieniuLiczyZwyklyCzasZamiastCzasuPauzy()
    {
        Ui.RunAsync(async () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            var hide = TimeSpan.FromSeconds(0.3);
            rig.Scene.Chrome.HideAfter = hide;
            rig.Scene.Chrome.PausedHideAfter = TimeSpan.FromSeconds(10);
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Space);
            Assert.False(player.IsPlaying);
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Now += TimeSpan.FromSeconds(0.2);
            Press(rig.Window, Key.Space);
            Assert.True(rig.Harness.PumpUntil(() => player.IsPlaying));

            rig.Now += hide + TimeSpan.FromSeconds(0.1);
            Assert.True(await Ui.UntilAsync(() => !rig.Scene.Chrome.IsShown, 2000), "warstwa czekała na czas pauzy");
        });
    }

    // ---------- 4. Pauza bez klatek ----------

    [Fact]
    public void WPauzieScenaPoSekundzieNieZamawiaKlatek()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            var sceneRequests = 0;
            var otherRequests = 0;

            // Okna z wcześniejszych testów mogą mieć jeszcze zamówione klatki; liczy się tylko to okno.
            void Count(Visual owner)
            {
                if (!ReferenceEquals(TopLevel.GetTopLevel(owner), rig.Window)) return;
                if (rig.Scene.IsVisualAncestorOf(owner)) sceneRequests++;
                else otherRequests++;
            }

            void Run(TimeSpan duration)
            {
                var end = DateTime.UtcNow + duration;
                do
                {
                    rig.Scene.InvalidateVisual();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(15);
                } while (DateTime.UtcNow < end);
            }

            RenderLoop.FrameRequested += Count;

            try
            {
                rig.Window.EnterScene();
                Run(TimeSpan.FromMilliseconds(200));
                Assert.True(sceneRequests > 0, "scena przy graniu nie zamawiała klatek");

                // Okno pod sceną niczego nie liczy (punkt 7 planu).
                Assert.Equal(0, otherRequests);
                Assert.False(rig.Window.IsRotating);
                Assert.False(rig.Window.Backdrop.IsDrawing);

                rig.Harness.Player.Pause();
                Dispatcher.UIThread.RunJobs();
                Run(TimeSpan.FromMilliseconds(1200));

                sceneRequests = 0;
                Run(TimeSpan.FromMilliseconds(300));
                Assert.Equal(0, sceneRequests);
                Assert.False(rig.Scene.Spinner.IsDrawing);
                Assert.False(rig.Scene.Backdrop.IsDrawing);
                Assert.Equal(0, otherRequests);
            }
            finally
            {
                RenderLoop.FrameRequested -= Count;
            }
        });
    }

    [Fact]
    public void PauzaOdslaniaWarstweAPoTrzydziestuSekundachJaChowa()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.False(rig.Scene.Chrome.IsShown);

            rig.Harness.Player.Pause();
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Scene.Chrome.IsShown);

            rig.Wait(29);
            Assert.True(rig.Scene.Chrome.IsShown);
            rig.Wait(1.5);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    // ---------- 5. Blokada wygaszacza ----------

    [Fact]
    public void WylaczenieUstawieniaZwalniaBlokadeWOtwartejScenieIWlaczenieJaPrzywraca()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;

            rig.Window.EnterScene();
            Assert.True(rig.WakeLock.IsHeld);

            player.KeepScreenAwake = false;
            Assert.False(rig.WakeLock.IsHeld);

            player.KeepScreenAwake = true;
            Assert.True(rig.WakeLock.IsHeld);
        });
    }

    [Fact]
    public void BlokadaTylkoGdyScenaGraIZwalnianaWKazdymInnymStanie()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            Assert.False(rig.WakeLock.IsHeld);

            rig.Window.EnterScene();
            Assert.True(rig.WakeLock.IsHeld);

            player.Pause();
            Assert.False(rig.WakeLock.IsHeld);

            player.TogglePlay();
            Assert.True(rig.Harness.PumpUntil(() => player.IsPlaying));
            Assert.True(rig.WakeLock.IsHeld);

            rig.Window.ExitScene();
            Assert.False(rig.WakeLock.IsHeld);

            // Minimalizacja zamyka scenę i zwalnia blokadę.
            rig.Window.EnterScene();
            Assert.True(rig.WakeLock.IsHeld);
            rig.Window.WindowState = WindowState.Minimized;
            Assert.False(rig.WakeLock.IsHeld);
            Assert.False(rig.Window.IsSceneOpen);

            rig.Window.WindowState = WindowState.Normal;
            rig.Window.EnterScene();
            Assert.True(rig.WakeLock.IsHeld);
            rig.Window.Close();
            Assert.False(rig.WakeLock.IsHeld);
            Assert.True(rig.WakeLock.Disposed);
        });
    }

    [Fact]
    public void KoniecKolejkiZwalniaBlokade()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0);
            var h = rig.Harness;

            h.OpenPaths([h.Track("krotki", seconds: 0.4)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero));

            rig.Window.EnterScene();
            Assert.True(rig.WakeLock.IsHeld);

            Assert.True(h.PumpUntil(() => !h.Player.IsPlaying));
            Assert.False(rig.WakeLock.IsHeld);
            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    // ---------- 6. Tryb ograniczony ----------

    [Fact]
    public void WTrybieOgraniczonymPlytaStoiATloJestOgraniczone()
    {
        Ui.Run(() =>
        {
            using var rig = Open(configure: s => s.Effects = EffectsMode.Reduced);
            rig.Window.EnterScene();

            for (var i = 0; i < 10; i++)
            {
                rig.Scene.InvalidateVisual();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(rig.Harness.Player.IsPlaying);
            Assert.False(rig.Scene.Spinner.IsRunning);
            Assert.Equal(0, rig.Scene.Disc.Angle);
            Assert.True(rig.Scene.Backdrop.IsReduced);
            Assert.False(rig.Scene.Backdrop.IsLooping);
        });
    }

    /// <summary>Klatki kompozytora przez podany czas, z prawdziwym upływem między nimi.</summary>
    private static void RunFrames(Window window, int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        } while (DateTime.UtcNow < end);
    }

    [Fact]
    public void TrybOgraniczonyWlaczonyPrzyZamknietejScenieStawiaJejPlyteProstoOdRazu()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var spinner = rig.Scene.Spinner;
            rig.Window.EnterScene();

            // Treść sceny dołącza do okna przy pierwszym układzie po wejściu i dopiero wtedy płyta
            // się kręci; płyta poza oknem nie znika razem ze sceną.
            Settle(rig.Window);
            Assert.Same(rig.Window, TopLevel.GetTopLevel(rig.Scene.Disc));
            for (var i = 0; i < 4; i++) spinner.Advance(0.25);
            Assert.True(spinner.Angle > 0);
            rig.Window.ExitScene();

            rig.Harness.Player.SetEffects(EffectsMode.Reduced);
            Assert.Equal(0, spinner.Angle);
            Assert.Equal(0, rig.Scene.Disc.Angle);
            Assert.False(spinner.IsRunning);

            // Po wejściu płyta stoi od pierwszej klatki.
            var angles = new List<double>();
            rig.Window.EnterScene();
            for (var i = 0; i < 10; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                angles.Add(rig.Scene.Disc.Angle);
            }

            Assert.All(angles, angle => Assert.Equal(0, angle));
        });
    }

    [Fact]
    public void WyjscieZeScenyWTrakciePowrotuDoPionuStawiaPlyteProsto()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var spinner = rig.Scene.Spinner;
            rig.Window.EnterScene();
            for (var i = 0; i < 4; i++) spinner.Advance(0.25);

            // Przy widocznej płycie powrót do pionu jest płynny.
            rig.Harness.Player.SetEffects(EffectsMode.Reduced);
            spinner.Advance(0.1);
            Assert.True(spinner.Angle > 0);

            rig.Window.ExitScene();
            Assert.Equal(0, spinner.Angle);

            rig.Window.EnterScene();
            for (var i = 0; i < 10; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, rig.Scene.Disc.Angle);
            }
        });
    }

    /// <summary>
    /// Pierwsze wejście do sceny przy grającej muzyce: płyta kręci się na klatkach kompozytora.
    /// Zawartość ukrytej sceny dołącza do okna dopiero przy pierwszym układzie, a pętla ruszona
    /// wcześniej rezygnowała i nic jej potem nie budziło.
    /// </summary>
    [Fact]
    public void PlytaScenyKreciSieOdPierwszegoWejscia()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            RunFrames(rig.Window, 150);
            Assert.True(rig.Window.Disc.Angle > 0, "płyta okna się nie kręciła");

            rig.Window.EnterScene();
            RunFrames(rig.Window, 400);

            Assert.True(rig.Scene.Disc.Angle > 0, "płyta sceny się nie kręciła");
            Assert.True(rig.Scene.Spinner.IsDrawing);
        });
    }

    [Fact]
    public void TrybOgraniczonyWlaczonyWScenieStawiaPlyteOknaProstoPrzedWyjsciem()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            RunFrames(rig.Window, 150);
            Assert.True(rig.Window.Disc.Angle > 0, "płyta okna się nie kręciła");

            rig.Window.EnterScene();
            RunFrames(rig.Window, 400);
            rig.Harness.Player.SetEffects(EffectsMode.Reduced);
            Assert.Equal(0, rig.Window.Disc.Angle);

            rig.Window.ExitScene();
            RunFrames(rig.Window, 100);
            Assert.Equal(0, rig.Window.Disc.Angle);
            Assert.False(rig.Window.IsRotating);
        });
    }

    /// <summary>
    /// Powrót płyty okna do pionu zaczęty przy widocznej płycie i przerwany wejściem do sceny nie
    /// dogrywa się po wyjściu: wstrzymana pętla nie zatrzymuje się, tylko czeka, a po odsłonięciu
    /// płyta stoi prosto od razu.
    /// </summary>
    [Fact]
    public void WyjscieZeScenyPoPrzerwanymPowrocieDoPionuStawiaPlyteOknaProsto()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            var spinner = rig.Window.Spinner;
            for (var i = 0; i < 4; i++) spinner.Advance(0.25);
            Assert.True(rig.Window.IsRotating);

            rig.Harness.Player.SetEffects(EffectsMode.Reduced);
            rig.Window.EnterScene();

            // Jedna klatka (najwyżej 0,25 s z 0,45 s powrotu) i pętla czeka na odsłonięcie.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Window.IsRotating);
            Assert.True(rig.Window.Disc.Angle > 0, "powrót do pionu skończył się przed wyjściem");

            rig.Window.ExitScene();
            Assert.Equal(0, rig.Window.Disc.Angle);

            for (var i = 0; i < 10; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, rig.Window.Disc.Angle);
            }

            Assert.False(spinner.IsRunning);
        });
    }

    // ---------- 8. Tytuł ----------

    [Fact]
    public void TytulZostajeWidocznyPoOsmiuSekundachIPoZmianieUtworu()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            rig.Window.EnterScene();
            Settle(rig.Window);

            Assert.Equal(1, Target(rig.Scene.TitleBlock));
            rig.Wait(8.1);
            Assert.Equal(1, Target(rig.Scene.TitleBlock));
            Assert.Null(rig.Scene.Chrome.NextDeadline);

            rig.Harness.Player.Next();
            Assert.True(rig.Harness.PumpUntil(() => rig.Harness.Player.CurrentIndex == 1));
            rig.Wait(30);
            Assert.Equal(1, Target(rig.Scene.TitleBlock));
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    // ---------- 9. Klawisze ----------

    [Fact]
    public void KlawiszeScenySterujaOdtwarzaniemAQLTNicNieRobia()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var panel = player.PanelOpen;
            var queue = player.QueuePinned;
            var theme = App.Theme.Mode;
            Press(rig.Window, Key.L);
            Press(rig.Window, Key.T);
            Assert.Equal(panel, player.PanelOpen);
            Assert.Equal(queue, player.QueuePinned);
            Assert.Equal(theme, App.Theme.Mode);
            Assert.True(rig.Window.IsSceneOpen);

            // Głośność plusem i minusem, nie strzałkami, bez wskaźnika na planszy.
            player.Volume = 0.5;
            Press(rig.Window, Key.Add);
            Assert.Equal(0.55, player.Volume, 3);
            Press(rig.Window, Key.OemMinus);
            Assert.Equal(0.5, player.Volume, 3);
            Assert.Null(rig.Scene.FindControl<Control>("VolumeBadge"));
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Null(rig.Scene.Chrome.NextDeadline);

            Press(rig.Window, Key.PageDown);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));
            Press(rig.Window, Key.Right, RawInputModifiers.Control);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 2));
            Press(rig.Window, Key.PageUp);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));

            var muted = player.IsMuted;
            Press(rig.Window, Key.M);
            Assert.NotEqual(muted, player.IsMuted);

            var shuffle = player.ShuffleEnabled;
            Press(rig.Window, Key.S);
            Assert.NotEqual(shuffle, player.ShuffleEnabled);

            var repeat = player.RepeatEnabled;
            Press(rig.Window, Key.R);
            Assert.NotEqual(repeat, player.RepeatEnabled);

            // Spacja przy ukrytej warstwie przełącza odtwarzanie, a warstwa się nie odsłania z fokusem.
            var playing = player.IsPlaying;
            Press(rig.Window, Key.Space);
            Assert.NotEqual(playing, player.IsPlaying);
            Assert.False(rig.Scene.Chrome.HasKeyboardFocus);
        });
    }

    [Fact]
    public void StrzalkiPrzyWidocznejWarstwiePrzenoszaFokusAEnterAktywuje()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
            Press(rig.Window, Key.Right);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.RepeatButton, Focused(rig.Window));

            Press(rig.Window, Key.Left);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));

            // Enter na przycisku z fokusem go naciska.
            Press(rig.Window, Key.Enter);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.ExitButton, Focused(rig.Window));
            Press(rig.Window, Key.Down);
            Press(rig.Window, Key.Down);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            var playing = player.IsPlaying;
            Press(rig.Window, Key.Space);
            Assert.NotEqual(playing, player.IsPlaying);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Enter);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Spacja i Enter z fokusem z klawiatury na Losowo, Poprzedni i Powtarzaj naciskają ten przycisk:
    /// jego skutek widać, a odtwarzanie się nie przełącza.
    /// </summary>
    [Theory]
    [InlineData("ShuffleButton", Key.Space)]
    [InlineData("ShuffleButton", Key.Enter)]
    [InlineData("PreviousButton", Key.Space)]
    [InlineData("PreviousButton", Key.Enter)]
    [InlineData("RepeatButton", Key.Space)]
    [InlineData("RepeatButton", Key.Enter)]
    public void SpacjaIEnterNaciskajaPrzyciskZFokusemZamiastPrzelaczacOdtwarzanie(string button, Key key)
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Drugi utwór od początku: „Poprzedni” przechodzi wtedy do pierwszego, a nie przewija.
            Press(rig.Window, Key.PageDown);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));

            Press(rig.Window, Key.Up);
            var target = (Button)Find(rig.Scene, button);
            Press(rig.Window, button == "RepeatButton" ? Key.Right : Key.Left);
            if (button == "ShuffleButton") Press(rig.Window, Key.Left);
            if (button == "RepeatButton") Press(rig.Window, Key.Right);
            Assert.Same(target, Focused(rig.Window));
            Assert.True(HasRing(target));

            var shuffle = player.ShuffleEnabled;
            var repeat = player.RepeatEnabled;
            var toggles = new List<bool>();
            player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Cewka.App.ViewModels.MainViewModel.IsPlaying)) toggles.Add(player.IsPlaying);
            };
            Press(rig.Window, key);

            switch (button)
            {
                case "ShuffleButton":
                    Assert.NotEqual(shuffle, player.ShuffleEnabled);
                    break;
                case "PreviousButton":
                    Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 0), $"utwór {player.CurrentIndex}");
                    break;
                default:
                    Assert.NotEqual(repeat, player.RepeatEnabled);
                    break;
            }

            // Odtwarzanie ani na chwilę się nie przełączyło: „Poprzedni” wznowiłby je po pauzie.
            Assert.Empty(toggles);
            Assert.True(player.IsPlaying);
            Assert.Same(target, Focused(rig.Window));
        });
    }

    /// <summary>
    /// Enter i Spacja z modyfikatorem nie naciskają przycisku warstwy z fokusem: przycisk nie patrzy
    /// na modyfikatory, więc scena takie klawisze połyka.
    /// </summary>
    [Theory]
    [InlineData(Key.Enter, RawInputModifiers.Shift)]
    [InlineData(Key.Enter, RawInputModifiers.Control)]
    [InlineData(Key.Space, RawInputModifiers.Shift)]
    [InlineData(Key.Space, RawInputModifiers.Control)]
    public void EnterISpacjaZModyfikatoremNieNaciskajaPrzyciskuWarstwy(Key key, RawInputModifiers modifiers)
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Tab);
            Press(rig.Window, Key.Left);
            Press(rig.Window, Key.Left);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));

            var shuffle = player.ShuffleEnabled;
            Press(rig.Window, key, modifiers);
            Assert.Equal(shuffle, player.ShuffleEnabled);
            Assert.True(player.IsPlaying);
            Assert.True(rig.Window.IsSceneOpen);
            Assert.Same(rig.Scene.ShuffleButton, Focused(rig.Window));
        });
    }

    /// <summary>Spacja z fokusem na Zamknij zamyka scenę, a odtwarzanie trwa.</summary>
    [Fact]
    public void SpacjaNaZamknijZamykaScene()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.ExitButton, Focused(rig.Window));

            Press(rig.Window, Key.Space);
            Assert.False(rig.Window.IsSceneOpen);
            Assert.True(player.IsPlaying);
        });
    }

    /// <summary>Shift+Tab idzie po warstwie wstecz: od paska przez wyjście i rząd od końca z powrotem na pasek.</summary>
    [Fact]
    public void ShiftTabIdzieOdPaskaPrzezWyjscieIRzadWstecz()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.Seek, Focused(rig.Window));

            var order = new List<IInputElement?>();
            for (var i = 0; i < 7; i++)
            {
                Press(rig.Window, Key.Tab, RawInputModifiers.Shift);
                order.Add(Focused(rig.Window));
            }

            var scene = rig.Scene;
            Assert.Equal<IInputElement?>(
                [scene.ExitButton, scene.RepeatButton, scene.NextButton, scene.PlayButton, scene.PreviousButton,
                 scene.ShuffleButton, scene.Seek],
                order);
        });
    }

    /// <summary>Przy ukrytej warstwie ↓ odsłania ją z fokusem na Odtwórz/Wstrzymaj, a Backspace zamyka scenę.</summary>
    [Fact]
    public void PrzyUkrytejWarstwieStrzalkaWDolOdslaniaZFokusemABackspaceZamyka()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Down);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));
            Assert.True(HasRing(rig.Scene.PlayButton));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Back);
            Assert.False(rig.Window.IsSceneOpen);
        });
    }

    /// <summary>
    /// Warstwa gaśnie z fokusem na pasku albo na Zamknij; klawisz, który ją odsłania, przywraca fokus
    /// na ten element, a nie na Odtwórz/Wstrzymaj.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KlawiszPoZgaszeniuWracaNaPasekIZamknij(int ups)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            for (var i = 0; i < ups; i++) Press(rig.Window, Key.Up);
            Control expected = ups == 1 ? rig.Scene.Seek : rig.Scene.ExitButton;
            Assert.Same(expected, Focused(rig.Window));

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene, Focused(rig.Window));

            Press(rig.Window, Key.Enter);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.True(rig.Window.IsSceneOpen);
            Assert.Same(expected, Focused(rig.Window));
            Assert.True(HasRing(expected));
            Assert.False(rig.Scene.Chrome.IsAdjusting);
        });
    }

    /// <summary>
    /// W pauzie fokus z klawiatury warstwy nie trzyma: gaśnie ona po 30 s, a każdy klawisz liczy
    /// te 30 s od nowa.
    /// </summary>
    [Fact]
    public void WPauzieZFokusemKlawiaturyWarstwaGasniePoTrzydziestuSekundachAKlawiszPrzedluza()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Spacja na Odtwórz/Wstrzymaj z fokusem z klawiatury.
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Space);
            Assert.False(player.IsPlaying);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            rig.Wait(29);
            Assert.True(rig.Scene.Chrome.IsShown);
            rig.Wait(1.5);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.Up);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.Same(rig.Scene.PlayButton, Focused(rig.Window));

            rig.Wait(29);
            Press(rig.Window, Key.Right);
            Assert.Same(rig.Scene.NextButton, Focused(rig.Window));
            rig.Wait(29);
            Assert.True(rig.Scene.Chrome.IsShown);
            rig.Wait(1.5);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.False(player.IsPlaying);
        });
    }

    /// <summary>Klawisze głośności w scenie zmieniają głośność i nie odsłaniają warstwy.</summary>
    [Theory]
    [InlineData(Key.OemPlus, 0.05)]
    [InlineData(Key.Add, 0.05)]
    [InlineData(Key.VolumeUp, 0.05)]
    [InlineData(Key.OemMinus, -0.05)]
    [InlineData(Key.Subtract, -0.05)]
    [InlineData(Key.VolumeDown, -0.05)]
    public void KlawiszGlosnosciZmieniaGlosnoscIWarstwyNieOdslania(Key key, double change)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            player.Volume = 0.5;
            Press(rig.Window, key);
            Assert.Equal(0.5 + change, player.Volume, 3);
            Assert.False(rig.Scene.Chrome.IsShown);
            Assert.Null(rig.Scene.Chrome.NextDeadline);
            Assert.True(rig.Scene.IsCursorHidden);
        });
    }

    /// <summary>
    /// Klawisze multimedialne „Następny” i „Poprzedni” zmieniają utwór bez odsłaniania warstwy, a pauza
    /// klawiszem multimedialnym odsłania ją jak każda pauza, z ukrytym kursorem. Klawisze i panel
    /// multimediów Windows idą przez <see cref="MainWindow.HandleMediaKey"/>, panel MPRIS w Linuksie
    /// przez <see cref="MainWindow.HandleMediaCommand"/>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KlawiszMultimedialnyNastepnyNieOdslaniaAPauzaOdslania(bool panel)
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 3);
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            if (panel) rig.Window.HandleMediaCommand(Cewka.Platform.Linux.MediaCommand.Next);
            else rig.Window.HandleMediaKey(MediaKey.Next);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1), $"utwór {player.CurrentIndex}");
            Assert.False(rig.Scene.Chrome.IsShown);

            if (panel) rig.Window.HandleMediaCommand(Cewka.Platform.Linux.MediaCommand.Previous);
            else rig.Window.HandleMediaKey(MediaKey.Previous);
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 0), $"utwór {player.CurrentIndex}");
            Assert.False(rig.Scene.Chrome.IsShown);

            if (panel) rig.Window.HandleMediaCommand(Cewka.Platform.Linux.MediaCommand.Pause);
            else rig.Window.HandleMediaKey(MediaKey.PlayPause);
            Dispatcher.UIThread.RunJobs();
            Assert.False(player.IsPlaying);
            Assert.True(rig.Scene.Chrome.IsShown);
            Assert.False(rig.Scene.Chrome.HasKeyboardFocus);
            Assert.True(rig.Scene.IsCursorHidden);
        });
    }

    // ---------- Wybór wizualizacji ----------

    [Fact]
    public void KlawiszVPrzelaczaWizualizacjeCyklicznieBezOdslaniania()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Assert.Equal(SceneVisual.Disc, player.SceneVisual);

            var caption = rig.Scene.FindControl<Border>("VisualCaption")!;
            var text = rig.Scene.FindControl<TextBlock>("VisualCaptionText")!;
            Assert.False(caption.IsVisible);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));

            Press(rig.Window, Key.V);
            Assert.Equal(SceneVisual.Cover, player.SceneVisual);
            Assert.True(caption.IsVisible);
            Assert.Equal(Strings.Current["SceneVisualCover"], text.Text);
            Assert.False(rig.Scene.Chrome.IsShown);

            Press(rig.Window, Key.V);
            Assert.Equal(SceneVisual.Night, player.SceneVisual);
            Assert.Equal(Strings.Current["SceneVisualNight"], text.Text);

            Press(rig.Window, Key.V);
            Assert.Equal(SceneVisual.Spectrum, player.SceneVisual);
            Assert.Equal(Strings.Current["SceneVisualSpectrum"], text.Text);

            Press(rig.Window, Key.V, RawInputModifiers.Shift);
            Assert.Equal(SceneVisual.Night, player.SceneVisual);
            Press(rig.Window, Key.V, RawInputModifiers.Shift);
            Assert.Equal(SceneVisual.Cover, player.SceneVisual);
            Assert.Equal(Strings.Current["SceneVisualCover"], text.Text);
            Assert.False(rig.Scene.Chrome.IsShown);
        });
    }

    [Fact]
    public void PodpisWizualizacjiGasniePoOkolo1_5SekundyIKazdyKlawiszOdliczaOdNowa()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            var caption = rig.Scene.FindControl<Border>("VisualCaption")!;

            Press(rig.Window, Key.V);
            rig.Wait(1.0);
            Assert.True(caption.IsVisible);

            // Kolejny klawisz przedłuża podpis od chwili naciśnięcia.
            Press(rig.Window, Key.V);
            rig.Wait(1.0);
            Assert.True(caption.IsVisible);

            rig.Wait(0.6);
            Assert.False(caption.IsVisible);
        });
    }

    [Fact]
    public void ZamkniecieScenyChowaPodpisWizualizacji()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            var caption = rig.Scene.FindControl<Border>("VisualCaption")!;

            Press(rig.Window, Key.V);
            Assert.True(caption.IsVisible);

            rig.Window.ExitScene();
            Assert.False(caption.IsVisible);
        });
    }

    // ---------- 10. Dostępność ----------

    [Fact]
    public void KorzenScenyMaNazweZTytulemWykonawcaIStanem()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();

            var name = AutomationProperties.GetName(rig.Scene);
            Assert.StartsWith(player.Title, name);
            Assert.EndsWith(Strings.Current["SceneStatePlaying"], name);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(rig.Scene));

            player.Pause();
            Assert.EndsWith(Strings.Current["SceneStatePaused"], AutomationProperties.GetName(rig.Scene));

            // Wystrój jest ukryty przed automatyzacją i nie łapie kursora.
            foreach (var layerName in new[] { "CoverLayer", "DimAcross", "DimBottom" })
            {
                var layer = rig.Scene.FindControl<Control>(layerName);
                Assert.NotNull(layer);
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(layer));
                Assert.False(layer.IsHitTestVisible, layerName);
            }

            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(rig.Scene.Backdrop));
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(rig.Scene.DiscHost));
        });
    }

    /// <summary>
    /// Pięć przycisków rzędu ma nazwę równą widocznemu podpisowi. Przycisk wyjścia nie ma podpisu
    /// na ekranie: nazwę podaje automatyzacji, a podpowiedź dokłada klawisz.
    /// </summary>
    [Fact]
    public void PrzyciskiScenyMajaNazwyZWidocznymiEtykietami()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            // Przycisk końca kolejki jest ukryty, dopóki kolejka się nie skończy.
            var buttons = rig.Scene.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToList();
            Assert.Equal(6, buttons.Count);

            foreach (var button in buttons.Where(button => button != rig.Scene.ExitButton))
            {
                var name = AutomationProperties.GetName(button);
                Assert.False(string.IsNullOrWhiteSpace(name), button.Name);

                var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Equal(name, label.Text);
            }

            Assert.Equal(Strings.Current["ScenePause"], AutomationProperties.GetName(rig.Scene.PlayButton));

            var exit = rig.Scene.ExitButton;
            Assert.Empty(exit.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal(Strings.Current["SceneExit"], AutomationProperties.GetName(exit));
            Assert.Equal($"{Strings.Current["SceneExit"]} · {Strings.Current["KeyEscape"]}", ToolTip.GetTip(exit));
        });
    }

    [Fact]
    public void PodpowiedzWyjsciaZmieniaJezyk()
    {
        Ui.Run(() =>
        {
            var previous = Strings.Current.CurrentCode;

            try
            {
                Strings.Current.SetLanguage("pl");
                using var rig = Open(tracks: 0);
                rig.Window.EnterScene();
                Settle(rig.Window);
                Assert.Equal("Zamknij pełny ekran · Esc", ToolTip.GetTip(rig.Scene.ExitButton));

                Strings.Current.SetLanguage("fr");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Quitter le plein écran · Échap", ToolTip.GetTip(rig.Scene.ExitButton));
            }
            finally
            {
                Strings.Current.SetLanguage(previous);
            }
        });
    }

    [Fact]
    public void TabIdzieOdPaskaPrzezRzadDoWyjsciaIWracaNaPasek()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(rig.Scene.Seek, Focused(rig.Window));

            var order = new List<IInputElement?>();
            for (var i = 0; i < 7; i++)
            {
                Press(rig.Window, Key.Tab);
                order.Add(Focused(rig.Window));
            }

            var scene = rig.Scene;
            Assert.Equal<IInputElement?>(
                [scene.ShuffleButton, scene.PreviousButton, scene.PlayButton, scene.NextButton, scene.RepeatButton,
                 scene.ExitButton, scene.Seek],
                order);
        });
    }

    [Fact]
    public void SuwakPodajeCzytnikowiPolozenieIDlugoscUtworu()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0);
            var h = rig.Harness;
            var player = h.Player;
            h.OpenPaths([h.Track("dlugi", seconds: 239)]);
            Assert.True(h.PumpUntil(() => player.IsPlaying));

            rig.Window.EnterScene();
            Settle(rig.Window);
            var seek = rig.Scene.Seek;
            Assert.Equal(Strings.Current["SeekPosition"], AutomationProperties.GetName(seek));

            // Podpowiedź mówi, że przewijanie strzałkami włącza Enter.
            Assert.Equal(Strings.Current["SeekPositionHelp"], AutomationProperties.GetHelpText(seek));
            Assert.Equal(Strings.Current["SeekPositionHelp"], ControlAutomationPeer.CreatePeerForElement(seek).GetHelpText());

            SeekTo(h, 39);
            Assert.Equal("0:39", player.Elapsed);
            Assert.Equal(string.Format(Strings.Current["SeekPositionValue"], "0:39", "3:59"), seek.ValueText);

            SeekTo(h, 65);
            Assert.Equal(string.Format(Strings.Current["SeekPositionValue"], "1:05", "3:59"), seek.ValueText);
        });
    }

    /// <summary>
    /// Ukryta scena nie liczy opisu położenia przy każdej sekundzie utworu; po wejściu opis jest aktualny.
    /// </summary>
    [Fact]
    public void OpisPolozeniaSuwakaLiczySieTylkoPrzyOtwartejScenie()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0);
            var h = rig.Harness;
            var player = h.Player;
            h.OpenPaths([h.Track("dlugi", seconds: 239)]);
            Assert.True(h.PumpUntil(() => player.IsPlaying));

            var seek = rig.Scene.Seek;
            SeekTo(h, 39);
            var closed = seek.ValueText;

            SeekTo(h, 65);
            Assert.Equal("1:05", player.Elapsed);
            Assert.Equal(closed, seek.ValueText);

            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.Equal(string.Format(Strings.Current["SeekPositionValue"], "1:05", "3:59"), seek.ValueText);

            // Po wyjściu opis znowu stoi.
            rig.Window.ExitScene();
            SeekTo(h, 100);
            Assert.Equal(string.Format(Strings.Current["SeekPositionValue"], "1:05", "3:59"), seek.ValueText);
        });
    }

    /// <summary>
    /// Pasek sceny nie dostaje położenia przy zamkniętej scenie: odświeżanie co 60 ms unieważniałoby
    /// rysowanie niewidocznej kontrolki i zgłaszało zmianę wartości czytnikowi. Po wejściu pasek
    /// pokazuje bieżące położenie od razu, a przeciągnięty w otwartej scenie przewija utwór.
    /// </summary>
    [Fact]
    public void PasekScenyDostajePolozenieTylkoPrzyOtwartejScenie()
    {
        Ui.Run(() =>
        {
            using var rig = Open(play: false, width: 1920, height: 1080);
            var player = rig.Harness.Player;
            var seek = rig.Scene.Seek;
            var start = seek.Value;
            var changes = 0;
            seek.PropertyChanged += (_, e) => { if (e.Property == TrackSlider.ValueProperty) changes++; };

            SeekTo(rig.Harness, 30);
            Assert.Equal(0.5, player.Progress, 3);
            Assert.Equal(0, changes);
            Assert.Equal(start, seek.Value);

            rig.Window.EnterScene();
            Assert.Equal(player.Progress, seek.Value);
            Settle(rig.Window);

            // Przeciąganie: czas idzie za wskaźnikiem, a utwór przewija się po puszczeniu.
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);
            Point At(double fraction) =>
                seek.TranslatePoint(new Point(10 + fraction * (seek.Bounds.Width - 20), seek.Bounds.Height / 2), rig.Window)!.Value;

            rig.Window.MouseMove(At(0.25));
            rig.Window.MouseDown(At(0.25), MouseButton.Left);
            rig.Window.MouseMove(At(0.75));
            Assert.Equal(0.75, seek.Value, 3);
            Assert.Equal("0:45", player.Elapsed);
            rig.Window.MouseUp(At(0.75), MouseButton.Left);

            Assert.True(rig.Harness.PumpUntil(() =>
            {
                player.RefreshForCapture();
                return Math.Abs(player.Progress - 0.75) < 0.01;
            }), $"położenie {player.Progress}");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(player.Progress, seek.Value, 3);

            // Po wyjściu położenie znowu do paska nie trafia.
            rig.Window.ExitScene();
            changes = 0;
            SeekTo(rig.Harness, 15);
            Assert.Equal(0, changes);
        });
    }

    /// <summary>
    /// Przewija do podanej sekundy i odświeża czasy. Przewinięcie przed poznaniem długości utworu
    /// przepada, a długość przychodzi z dekodera chwilę po starcie odtwarzania, więc najpierw na nią czeka.
    /// </summary>
    internal static void SeekTo(PlayerHarness harness, double seconds)
    {
        var player = harness.Player;
        Assert.True(harness.PumpUntil(() =>
        {
            player.RefreshForCapture();
            return player.Total != "0:00";
        }), "długość utworu nieznana");

        player.SeekToPosition(TimeSpan.FromSeconds(seconds));
        Assert.True(harness.PumpUntil(() =>
        {
            player.RefreshForCapture();
            return player.Elapsed == Cewka.App.ViewModels.MainViewModel.FormatTime(seconds);
        }), $"czas {player.Elapsed}");
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void EtykietaOdtwarzaniaINazwaScenyZmieniajaJezykRazemZOknem()
    {
        Ui.Run(() =>
        {
            var previous = Strings.Current.CurrentCode;
            Strings.Current.SetLanguage("pl");

            try
            {
                using var rig = Open(play: false);
                var polishPlay = Strings.Current["ScenePlay"];

                // Zmiana języka przed otwarciem sceny.
                Strings.Current.SetLanguage("en");
                Assert.NotEqual(polishPlay, Strings.Current["ScenePlay"]);
                rig.Window.EnterScene();
                Settle(rig.Window);
                Assert.Equal(Strings.Current["ScenePlay"], rig.Scene.PlayLabel.Text);
                Assert.Equal(Strings.Current["ScenePlay"], AutomationProperties.GetName(rig.Scene.PlayButton));

                // Zmiana języka przy otwartej scenie.
                Strings.Current.SetLanguage("pl");
                Assert.Equal(polishPlay, rig.Scene.PlayLabel.Text);
                Assert.EndsWith(Strings.Current["SceneStatePaused"], AutomationProperties.GetName(rig.Scene));
                Strings.Current.SetLanguage("en");
                Assert.EndsWith(Strings.Current["SceneStatePaused"], AutomationProperties.GetName(rig.Scene));
            }
            finally
            {
                Strings.Current.SetLanguage(previous);
            }
        });
    }

    /// <summary>
    /// Zmiana języka przy otwartej scenie przełącza od razu podpisy i nazwy dla czytnika przycisków
    /// rzędu, wyjścia i paska, a szerokość rzędu idzie za językiem. Podpis i nazwę porównuje się
    /// z tekstem języka osobno: zgodność podpisu z nazwą przepuściłaby oba w starym języku.
    /// </summary>
    [Fact]
    public void TekstyIUkladScenyZmieniajaJezykPrzyOtwartejScenie()
    {
        InLanguage("pl", () =>
        {
            using var rig = Open(width: 1920, height: 1080);
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);

            var scene = rig.Scene;
            (Control Control, string Key)[] texts =
            [
                (scene.ShuffleButton, "SceneShuffle"), (scene.PreviousButton, "ScenePrevious"),
                (scene.NextButton, "SceneNext"), (scene.RepeatButton, "SceneRepeat"),
                (scene.ExitButton, "SceneExit"), (scene.Seek, "SeekPosition"),
            ];
            var polish = texts.ToDictionary(text => text.Key, text => Strings.Current[text.Key]);

            Strings.Current.SetLanguage("fr");
            Settle(rig.Window);
            Assert.Equal("fr", Strings.Current.CurrentCode);

            foreach (var (control, key) in texts)
            {
                Assert.NotEqual(polish[key], Strings.Current[key]);
                Assert.Equal(Strings.Current[key], AutomationProperties.GetName(control));
                if (control is Button button && button != scene.ExitButton)
                    Assert.Equal(Strings.Current[key], Caption(button).Text);
            }

            Assert.Equal(Strings.Current["SeekPositionHelp"], AutomationProperties.GetHelpText(scene.Seek));

            // Szerokość rzędu z najdłuższego podpisu w nowym języku, z luzem po 4 z każdej strony.
            var widest = RowCaptions.Max(key => NaturalWidth(Strings.Current[key], Caption(scene.PlayButton)));
            var width = OnScene(scene.ShuffleButton, scene).Width;
            Assert.InRange(width, Math.Max(80, widest + 8) - 0.01, Math.Max(80, widest + 8 + 1));
        });
    }

    // ---------- 11. Zewnętrzna zmiana stanu okna ----------

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Normal)]
    public void ZewnetrznaZmianaStanuOknaZamykaScene(WindowState state)
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();

            rig.Window.WindowState = state;
            Assert.False(rig.Window.IsSceneOpen);
            Assert.True(rig.Window.WindowContent.IsVisible);
            Assert.Equal(state, rig.Window.WindowState);

            // Pełny ekran nadany z zewnątrz otwiera scenę, bo bez niej go nie ma.
            rig.Window.WindowState = WindowState.FullScreen;
            Assert.True(rig.Window.IsSceneOpen);
        });
    }

    // ---------- Barwy ----------

    /// <summary>
    /// Pod plamami leży rozmyta okładka, a plamy mają barwy planszy, bez kryjącego podkładu i bez
    /// zależności od motywu okna.
    /// </summary>
    [Fact]
    public void TloScenyToRozmytaOkladkaIPlamyZBarwOkladkiWKazdymMotywie()
    {
        Ui.Run(() =>
        {
            var before = App.Theme.Mode;

            try
            {
                App.Theme.SetMode(ThemeMode.Dark);
                Dispatcher.UIThread.RunJobs();

                using var rig = Open();
                var player = rig.Harness.Player;
                Assert.True(rig.Harness.PumpUntil(() => player.Title == "s0"));
                Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));
                rig.Window.EnterScene();
                Settle(rig.Window);

                var cover = rig.Scene.FindControl<Image>("CoverBackdrop");
                Assert.NotNull(cover);
                Assert.NotNull(player.SceneBackdrop);
                Assert.Same(player.SceneBackdrop, cover.Source);
                Assert.Equal(Stretch.UniformToFill, cover.Stretch);
                Assert.True(rig.Scene.FindControl<Control>("CoverLayer")!.ClipToBounds);

                var backdrop = rig.Scene.Backdrop;
                Assert.Null(backdrop.BaseBrush);
                Assert.Equal(player.SceneBlobs, backdrop.Palette);
                var blobs = player.SceneBlobs.ToArray();

                // Nowa okładka podmienia obie warstwy.
                player.ShowCover(Cewka.App.ViewModels.MainViewModel.DecodeCover(CoverTests.Png(64, 64, Color.Parse("#FF2050C0"))));
                Dispatcher.UIThread.RunJobs();
                Assert.Same(player.SceneBackdrop, cover.Source);
                Assert.Equal(player.SceneBlobs, backdrop.Palette);
                Assert.NotEqual(blobs, backdrop.Palette!.ToArray());

                // Jasne okno nie zmienia barw planszy.
                blobs = player.SceneBlobs.ToArray();
                App.Theme.SetMode(ThemeMode.Light);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(blobs, backdrop.Palette!.ToArray());
            }
            finally
            {
                App.Theme.SetMode(before);
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>Okładka 600 × 600 z kratą 8 × 8 nasyconych barw: najostrzejsze przejścia po rozmyciu.</summary>
    private static byte[] Checkerboard()
    {
        Color[] colours = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.White, Colors.Black, Colors.Magenta, Colors.Cyan];

        using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 600), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                context.FillRectangle(new SolidColorBrush(colours[(x * 3 + y * 5) % 8]), new Rect(x * 75, y * 75, 75, 75));
        }

        using var stream = new MemoryStream();
        target.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>
    /// Tło sceny rysuje się dwuliniowo, nie sześciennie: rozmyta okładka 96 px powiększona ok. 24 razy
    /// nie ma ostrych przejść, a próbkowanie sześcienne kosztuje przy każdym przerysowaniu pod płytą.
    /// Render tła 1920 × 1080 różni się od wzorcowego, sześciennego, średnio o mniej niż 0,3 na 255
    /// i nigdzie o więcej niż 2. Tryb LowQuality daje już schodki: średnio ok. 0,5, do 3 poziomów.
    /// </summary>
    [Fact]
    public void TloScenyRysujeSieDwuliniowoIWygladaJakPrzySzesciennym()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0, width: 1920, height: 1080);
            rig.Harness.Player.ShowCover(Cewka.App.ViewModels.MainViewModel.DecodeCover(Checkerboard()));
            rig.Window.EnterScene();
            Settle(rig.Window);

            var layer = Find(rig.Scene, "CoverLayer");
            var image = (Image)Find(rig.Scene, "CoverBackdrop");
            Assert.Equal(Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality, RenderOptions.GetBitmapInterpolationMode(image));

            var actual = ControlPixels.Render(layer, 1920, 1080);
            RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            var reference = ControlPixels.Render(layer, 1920, 1080);

            long sum = 0;
            var max = 0;
            for (var y = 0; y < 1080; y++)
            for (var x = 0; x < 1920; x++)
            {
                var a = actual[x, y];
                var b = reference[x, y];
                foreach (var d in new[] { Math.Abs(a.R - b.R), Math.Abs(a.G - b.G), Math.Abs(a.B - b.B) })
                {
                    sum += d;
                    max = Math.Max(max, d);
                }
            }

            var mean = sum / (1920.0 * 1080 * 3);
            Assert.True(mean < 0.3 && max <= 2, $"średnia różnica {mean:F3}, największa {max}");
        });
    }

    /// <summary>Piksele naklejki płyty sceny, od otworu do brzegu naklejki, z płytą stojącą prosto.</summary>
    private static List<Color> Label(VinylDisc disc)
    {
        var pixels = ControlPixels.Render(disc, 640, 640);
        var label = new List<Color>();

        for (var y = 0; y < 640; y++)
        for (var x = 0; x < 640; x++)
        {
            var r = Math.Sqrt((x + 0.5 - 320) * (x + 0.5 - 320) + (y + 0.5 - 320) * (y + 0.5 - 320));
            if (r is > 12 and < 125) label.Add(pixels[x, y]);
        }

        return label;
    }

    private static double Brightness(IEnumerable<Color> pixels) => pixels.Average(c => (c.R + c.G + c.B) / 3.0);

    /// <summary>
    /// Naklejka płyty sceny nie zależy od motywu okna: utwór bez okładki ma w jasnym oknie ten sam
    /// ciemny wariant okładki domyślnej co w ciemnym, a nie jasną spiralę płyty okna.
    /// </summary>
    [Fact]
    public void NaklejkaScenyBezOkladkiWygladaTakSamoWJasnymICiemnymOknie()
    {
        InTheme(ThemeMode.Dark, () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));
            rig.Window.FreezeAnimationsForCapture();
            rig.Window.EnterScene();
            Settle(rig.Window);
            Assert.Equal(0, rig.Scene.Disc.Angle);

            var dark = Label(rig.Scene.Disc);
            Assert.True(Brightness(dark) < 90, $"naklejka w ciemnym oknie: {Brightness(dark):F0}");

            App.Theme.SetMode(ThemeMode.Light);
            Dispatcher.UIThread.RunJobs();
            Settle(rig.Window);

            var light = Label(rig.Scene.Disc);
            var difference = dark.Zip(light, (a, b) => (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0).ToList();
            Assert.True(difference.Average() < 0.5 && difference.Max() < 8,
                $"naklejka w jasnym oknie: jasność {Brightness(light):F0}, średnia różnica {difference.Average():F1}, największa {difference.Max():F0}");
        });
    }

    /// <summary>
    /// W jasnym oknie ciemny wariant okładki domyślnej istnieje tylko przy otwartej scenie: przy
    /// zamkniętej nie powstaje ani przy pierwszym, ani przy kolejnym utworze bez okładki. Po wejściu
    /// naklejka i tło sceny są ciemne, a wyjście zwalnia ten wariant razem z tłem.
    /// </summary>
    [Fact]
    public void CiemnaNaklejkaWJasnymOknieIstniejeTylkoPrzyOtwartejScenie()
    {
        InTheme(ThemeMode.Light, () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));
            Assert.NotNull(player.Cover);
            Assert.Null(player.SceneCover);
            Assert.Null(player.SceneBackdrop);

            player.Next();
            Assert.True(rig.Harness.PumpUntil(() => player.CurrentIndex == 1));
            Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));
            Assert.Null(player.SceneCover);
            Assert.Null(player.SceneBackdrop);

            rig.Window.FreezeAnimationsForCapture();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var dark = player.SceneCover;
            var backdrop = player.SceneBackdrop;
            Assert.NotNull(dark);
            Assert.NotNull(backdrop);
            Assert.NotSame(player.Cover, dark);
            Assert.Same(dark, rig.Scene.Disc.Cover);
            Assert.Same(backdrop, ((Image)Find(rig.Scene, "CoverBackdrop")).Source);
            Assert.True(Brightness(Label(rig.Scene.Disc)) < 90, $"naklejka: {Brightness(Label(rig.Scene.Disc)):F0}");

            var bgra = CoverPalette.ReadPixels(backdrop);
            var blurred = Enumerable.Range(0, bgra.Length / 4).Average(i => (bgra[4 * i] + bgra[4 * i + 1] + bgra[4 * i + 2]) / 3.0);
            Assert.True(blurred < 90, $"tło sceny: {blurred:F0}");

            rig.Window.ExitScene();
            Assert.True(CoverTests.IsDisposed(dark));
            Assert.True(CoverTests.IsDisposed(backdrop));
            Assert.Null(player.SceneCover);
            Assert.Null(rig.Scene.Disc.Cover);
            Assert.False(CoverTests.IsDisposed(player.Cover!));
        });
    }

    /// <summary>
    /// Wejście do sceny i wyjście z niej przy okładce domyślnej w jasnym oknie rysuje od nowa tylko
    /// płytę i tło sceny. Okładka okna się nie zmienia, więc jego barwy się nie liczą: znacznik
    /// wpisany w zasób akcentu zostaje, a przeliczenie palety okna nadpisałoby go akcentem okładki.
    /// </summary>
    [Fact]
    public void WejscieIWyjscieZeScenyNiePrzeliczajaBarwOkna()
    {
        InTheme(ThemeMode.Light, () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;

            // Na starcie to Task.CompletedTask; dopiero obsłużona zmiana utworu wczytuje okładkę.
            Assert.True(rig.Harness.PumpUntil(() => !ReferenceEquals(player.CoverTask, Task.CompletedTask)));
            Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));
            Assert.NotNull(player.Cover);
            Assert.Null(player.SceneCover);

            var resources = Application.Current!.Resources;
            var accent = resources["AccentColor"];
            var marker = Color.Parse("#FF00FF");
            Assert.NotEqual<object?>(marker, accent);

            try
            {
                resources["AccentColor"] = marker;

                rig.Window.EnterScene();
                Settle(rig.Window);
                Assert.NotNull(player.SceneCover);
                Assert.NotEmpty(player.SceneBlobs);
                Assert.Equal<object?>(marker, resources["AccentColor"]);

                rig.Window.ExitScene();
                Assert.Null(player.SceneCover);
                Assert.Equal<object?>(marker, resources["AccentColor"]);
            }
            finally
            {
                resources["AccentColor"] = accent;
            }
        });
    }

    /// <summary>
    /// Plamy mają siłę plamy wzoru: kryjąca barwa plamy w środku o kryciu mniej więcej 0,16–0,26,
    /// a nie siłę plam tła okna nad ciemnym podkładem.
    /// </summary>
    [Fact]
    public void PlamyScenyMajaKrycieWzoru()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0);
            var backdrop = rig.Scene.Backdrop;

            // Plama najsilniejsza ma siłę 0,86 razy próg, najsłabsza 0,53 razy próg.
            Assert.InRange(0.86 * backdrop.MaximumStrength, 0.24, 0.28);
            Assert.InRange(0.53 * backdrop.MinimumStrength, 0.10, 0.16);
            Assert.True(backdrop.MinimumStrength < backdrop.MaximumStrength);
        });
    }

    /// <summary>Biel planszy: TextBrush z ciemnego zakresu sceny, także w jasnym oknie.</summary>
    private static readonly Color SceneWhite = Color.Parse("#FFF3F3F5");

    /// <summary>Szarość planszy: ikony i podpisy wyłączone, pierścień fokusu.</summary>
    private static readonly Color SceneGrey = Color.Parse("#FFC7C7CC");

    private static Color Solid(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>Barwy ikony, podpisu i kropki przycisku rzędu; kropka bez pędzla jest przezroczysta.</summary>
    private static (Color Icon, Color Caption, Color Dot) Look(Button button)
    {
        var icon = button.GetVisualDescendants().OfType<Shapes.Path>().First(path => path.IsEffectivelyVisible);
        var caption = button.GetVisualDescendants().OfType<TextBlock>().Single();
        var dot = button.GetVisualDescendants().OfType<Shapes.Ellipse>().Single(ellipse => ellipse.Classes.Contains("dot"));

        Assert.Equal(4, dot.Bounds.Width);
        Assert.Equal(4, dot.Bounds.Height);

        return (Solid(icon.Classes.Contains("stroke") ? icon.Stroke : icon.Fill),
                Solid(caption.Foreground),
                dot.Fill is ISolidColorBrush fill ? fill.Color : Colors.Transparent);
    }

    private static ToggleState Toggle(Control control) =>
        Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(control)).ToggleState;

    /// <summary>
    /// Test w podanym języku interfejsu, z przywróceniem poprzedniego. Układ rzędu zależy od
    /// szerokości podpisów, więc testy położeń nie mogą brać języka systemu.
    /// </summary>
    internal static void InLanguage(string code, Action test)
    {
        Ui.Run(() =>
        {
            var previous = Strings.Current.CurrentCode;

            try
            {
                Strings.Current.SetLanguage(code);
                Assert.Equal(code, Strings.Current.CurrentCode);
                test();
            }
            finally
            {
                Strings.Current.SetLanguage(previous);
            }
        });
    }

    /// <summary>Okno w podanym motywie na czas testu; motyw wraca po teście.</summary>
    private static void InTheme(ThemeMode mode, Action test)
    {
        Ui.Run(() =>
        {
            var before = App.Theme.Mode;

            try
            {
                App.Theme.SetMode(mode);
                Dispatcher.UIThread.RunJobs();
                test();
            }
            finally
            {
                App.Theme.SetMode(before);
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>
    /// Jedna barwa stanu włączonego, biel: włączony przełącznik ma białą ikonę i podpis oraz białą
    /// kropkę, wyłączony szarość planszy i bez kropki. Także w jasnym oknie i bez akcentu okładki.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void WlaczonyPrzelacznikMaBielIKropkeAWylaczonySzarosc(ThemeMode mode)
    {
        InTheme(mode, () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            player.ShowCover(Cewka.App.ViewModels.MainViewModel.DecodeCover(CoverTests.Png(64, 64, Color.Parse("#FFFF0000"))));
            player.ShuffleEnabled = true;
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);
            var scene = rig.Scene;

            AssertOn(scene.ShuffleButton);
            AssertOff(scene.PreviousButton);
            AssertOff(scene.NextButton);
            AssertOff(scene.RepeatButton);
            Assert.Equal(ToggleState.On, Toggle(scene.ShuffleButton));
            Assert.Equal(ToggleState.Off, Toggle(scene.RepeatButton));

            player.ShuffleEnabled = false;
            player.CycleRepeat();
            Dispatcher.UIThread.RunJobs();
            AssertOff(scene.ShuffleButton);
            AssertOn(scene.RepeatButton);
            Assert.Equal(ToggleState.Off, Toggle(scene.ShuffleButton));
            Assert.Equal(ToggleState.On, Toggle(scene.RepeatButton));

            // Odtwórz/Wstrzymaj: biały podpis, ciemna ikona na białym kole, bez kropki.
            var circle = scene.FindControl<Border>("PlayCircle");
            Assert.NotNull(circle);
            Assert.Equal(SceneWhite, Solid(circle.Background));
            Assert.Equal(SceneWhite, Solid(scene.PlayLabel.Foreground));
            Assert.Equal(Color.Parse("#FF1C1C1E"), Solid(scene.FindControl<Shapes.Path>("PauseIcon")!.Fill));
            Assert.Equal(0, Look(scene.PlayButton).Dot.A);

            // Ikona wyjścia w szarości planszy.
            Assert.Equal(SceneGrey, Solid(scene.ExitButton.GetVisualDescendants().OfType<Shapes.Path>().Single().Stroke));
        });

        static void AssertOn(Button button)
        {
            var (icon, caption, dot) = Look(button);
            Assert.Equal(SceneWhite, icon);
            Assert.Equal(SceneWhite, caption);
            Assert.Equal(SceneWhite, dot);
        }

        static void AssertOff(Button button)
        {
            var (icon, caption, dot) = Look(button);
            Assert.Equal(SceneGrey, icon);
            Assert.Equal(SceneGrey, caption);
            Assert.Equal(0, dot.A);
        }
    }

    /// <summary>
    /// Powtarzaj zachowuje trzy tryby aplikacji: kliknięcie przechodzi do następnego, a przełącznik
    /// jest włączony przy powtarzaniu kolejki i utworu. Przełącznik sam się nie przestawia, więc
    /// przejście z kolejki do utworu nie gasi go na chwilę.
    /// </summary>
    [Fact]
    public void PowtarzajPrzechodziPrzezTrzyTrybyIJestWlaczoneDlaKolejkiIUtworu()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);

            var repeat = Assert.IsAssignableFrom<ToggleButton>(rig.Scene.RepeatButton);
            var queueIcon = rig.Scene.FindControl<Shapes.Path>("RepeatIcon")!;
            var trackIcon = rig.Scene.FindControl<Shapes.Path>("RepeatOneIcon")!;
            var point = Centre(repeat, rig.Window);

            void Click()
            {
                rig.Window.MouseDown(point, MouseButton.Left);
                rig.Window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.False(player.RepeatEnabled);
            Assert.False(repeat.IsChecked);

            Click();
            Assert.True(player.RepeatEnabled);
            Assert.False(player.RepeatTrack);
            Assert.True(repeat.IsChecked);
            Assert.True(queueIcon.IsEffectivelyVisible);
            Assert.False(trackIcon.IsEffectivelyVisible);

            Click();
            Assert.True(player.RepeatTrack);
            Assert.True(repeat.IsChecked);
            Assert.Equal(ToggleState.On, Toggle(repeat));
            Assert.False(queueIcon.IsEffectivelyVisible);
            Assert.True(trackIcon.IsEffectivelyVisible);
            Assert.Equal(player.RepeatTooltip, AutomationProperties.GetHelpText(repeat));

            Click();
            Assert.False(player.RepeatEnabled);
            Assert.False(repeat.IsChecked);
            Assert.Equal(ToggleState.Off, Toggle(repeat));

            // Przełączenie przez automatyzację też przechodzi do następnego trybu.
            Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(repeat)).Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.True(player.RepeatEnabled);
            Assert.True(repeat.IsChecked);
        });
    }

    [Fact]
    public void OdtworzWstrzymajPokazujePodpisIIkoneStanu()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            rig.Window.EnterScene();
            Settle(rig.Window);

            var pause = rig.Scene.FindControl<Shapes.Path>("PauseIcon")!;
            var play = rig.Scene.FindControl<Shapes.Path>("PlayIcon")!;
            Assert.Equal(Strings.Current["ScenePause"], rig.Scene.PlayLabel.Text);
            Assert.True(pause.IsEffectivelyVisible);
            Assert.False(play.IsEffectivelyVisible);

            player.Pause();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Strings.Current["ScenePlay"], rig.Scene.PlayLabel.Text);
            Assert.False(pause.IsEffectivelyVisible);
            Assert.True(play.IsEffectivelyVisible);
        });
    }

    /// <summary>Pierścień leży w warstwie ozdobników okna, poza zakresem motywu sceny.</summary>
    private static FocusRing RingOf(Control control) =>
        AdornerLayer.GetAdornerLayer(control)!.Children.OfType<FocusRing>()
            .Single(ring => AdornerLayer.GetAdornedElement(ring) == control);

    /// <summary>
    /// Pierścień fokusu w scenie: 2 px w szarości planszy, w odstępie 2 od przycisku, w każdym
    /// motywie okna. Okno zostaje przy swoim pierścieniu.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void PierscienFokusuWScenieMaDwaPikseleSzarosciPlanszy(ThemeMode mode)
    {
        InTheme(mode, () =>
        {
            using var rig = Open(width: 1920, height: 1080);
            rig.Window.EnterScene();
            Settle(rig.Window);
            Press(rig.Window, Key.Up);
            Settle(rig.Window);

            var play = rig.Scene.PlayButton;
            var ring = RingOf(play);
            Assert.Equal(SceneGrey, Solid(ring.Brush));
            Assert.Equal(FocusRing.DefaultThickness, ring.RingThickness);
            Assert.Equal(play.Bounds.Width + 2 * (FocusRing.Gap + FocusRing.DefaultThickness), ring.Bounds.Width, 1);

            rig.Window.ExitScene();
            rig.Window.FullScreenButton.Focus(NavigationMethod.Tab);
            Settle(rig.Window);
            Assert.NotEqual(SceneGrey, Solid(RingOf(rig.Window.FullScreenButton).Brush));
        });
    }

    /// <summary>Barwa gradientu liniowego w punkcie 0–1 jego osi.</summary>
    private static Color At(LinearGradientBrush brush, double offset)
    {
        var stops = brush.GradientStops.OrderBy(stop => stop.Offset).ToArray();
        if (offset <= stops[0].Offset) return stops[0].Color;
        if (offset >= stops[^1].Offset) return stops[^1].Color;

        var after = Array.FindIndex(stops, stop => stop.Offset >= offset);
        var (a, b) = (stops[after - 1], stops[after]);
        var t = (offset - a.Offset) / (b.Offset - a.Offset);

        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(Mix(a.Color.A, b.Color.A), Mix(a.Color.R, b.Color.R), Mix(a.Color.G, b.Color.G), Mix(a.Color.B, b.Color.B));
    }

    /// <summary>
    /// Pierścień fokusu ma co najmniej 3:1 na najjaśniejszym tle, jakie może mieć: białej okładce
    /// pod przyciemnieniem poziomym przy lewym brzegu pierścienia, gdzie przyciemnienie jest najsłabsze.
    /// </summary>
    [Fact]
    public void PierscienFokusuMaKontrastTrzyDoJednegoNaPrzyciemnionejBieli()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0, width: 1920, height: 1080);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var scene = rig.Scene;
            var dim = Assert.IsType<LinearGradientBrush>(scene.FindControl<Border>("DimAcross")!.Background);
            var outside = FocusRing.Gap + FocusRing.DefaultThickness;

            foreach (var control in new Control[] { scene.Seek, scene.ShuffleButton, scene.PlayButton, scene.ExitButton })
            {
                var left = control.TranslatePoint(new Point(-outside, 0), scene)!.Value.X / scene.Bounds.Width;
                var background = Contrast.Over(At(dim, left), Colors.White);
                var ratio = Contrast.Ratio(SceneGrey, background);

                Assert.True(ratio >= Contrast.FocusRingRatio, $"{control.Name}: {ratio:F2} na {background}");
            }
        });
    }

    [Fact]
    public void PlytaScenyObracaSieJakPlytaOknaZWyglademWzoru()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var spinner = rig.Scene.Spinner;
            var start = spinner.Angle;
            spinner.Advance(0.1);

            // Jeden obrót na dziewięć sekund, jak płyta okna: 40° na sekundę.
            Assert.Equal((start + 4) % 360, spinner.Angle, 6);
            Assert.Equal(spinner.Angle, rig.Scene.Disc.Angle, 6);

            var disc = rig.Scene.Disc;
            Assert.Equal(0.4125, disc.CoreFraction);
            Assert.Equal(0.41875, disc.BodyFraction);
            Assert.Equal(640, disc.DesignDiameter);
            Assert.Equal(3, disc.GroovePitch);
            Assert.Equal(7.5, disc.GrooveInset);
            Assert.Equal(0.44375, disc.GrooveInnerFraction);
            Assert.Equal(new[] { 0.615625, 0.796875 }, disc.SeparatorFractions!);
            Assert.Equal(0.40625, disc.LabelFraction);
            Assert.Equal(0.021875, disc.HoleFraction);
            Assert.Null(disc.RingBrush);

            Assert.Equal(Color.Parse("#FF151517"), Solid(disc.CoreBrush));
            Assert.Equal(Color.Parse("#FF0C0C0E"), Solid(disc.BodyBrush));
            Assert.Equal(Color.Parse("#FF0F0F11"), Solid(disc.RimBrush));
            Assert.Equal(Color.Parse("#0FFFFFFF"), Solid(disc.OutlineBrush));
            Assert.Equal(Color.Parse("#0EFFFFFF"), Solid(disc.GrooveBrush));
            Assert.Equal(Color.Parse("#80000000"), Solid(disc.SeparatorBrush));
            Assert.Equal(Color.Parse("#99000000"), Solid(disc.LabelOutlineBrush));
            Assert.Equal(Color.Parse("#FF1C1C1E"), Solid(disc.HoleBrush));
            Assert.Equal(Color.Parse("#14FFFFFF"), Solid(disc.HoleOutlineBrush));
        });
    }

    /// <summary>
    /// Połysk płyty stoi nad nią, nie obraca się i jaśnieje tam, gdzie wzór: biel 9 % przy 28°
    /// i 7 % przy 208° od góry zgodnie z ruchem zegara, tylko w pierścieniu rowków.
    /// </summary>
    [Fact]
    public void PolyskPlytyStoiIJasniejePrzy28StopniachOdGoryZgodnieZZegarem()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 0);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var sheen = rig.Scene.FindControl<Shapes.Path>("DiscSheen");
            Assert.NotNull(sheen);
            Assert.False(sheen.IsHitTestVisible);
            Assert.False(rig.Scene.Disc.IsVisualAncestorOf(sheen));
            Assert.Same(rig.Scene.DiscHost, sheen.GetVisualParent());

            var pixels = ControlPixels.Render(new Shapes.Path { Fill = sheen.Fill, Data = sheen.Data }, 640, 640);

            Color On(double degrees, double radius)
            {
                var angle = degrees * Math.PI / 180;
                return pixels[(int)Math.Round(320 + radius * Math.Sin(angle)), (int)Math.Round(320 - radius * Math.Cos(angle))];
            }

            Assert.InRange(On(28, 227).A, 18, 28);
            Assert.InRange(On(208, 227).A, 13, 23);
            Assert.True(On(-28, 227).A < 3, $"{On(-28, 227)}");
            Assert.True(On(152, 227).A < 3, $"{On(152, 227)}");
            Assert.True(On(28, 120).A < 3, $"{On(28, 120)}");
            Assert.True(On(28, 319.6).A < 12, $"{On(28, 319.6)}");
        });
    }

    private static Color[] Plain(string colour) => Enumerable.Repeat(Color.Parse(colour), 4).ToArray();

    /// <summary>
    /// Barwy kolejnych utworów przy zamkniętej scenie, na końcu okładka domyślna. Ukryte plamy
    /// przyjmują je od razu, bez przenikania, także barwy ciemnego wariantu okładki domyślnej,
    /// które w jasnym oknie powstają dopiero przy wejściu do sceny.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void UkrytaScenaPrzyjmujePaleteOdRazuIPoWejsciuMaBarwyBiezacegoUtworu(ThemeMode mode)
    {
        InTheme(mode, () =>
        {
            using var rig = Open();
            var player = rig.Harness.Player;
            var backdrop = rig.Scene.Backdrop;

            player.ShowCover(Cewka.App.ViewModels.MainViewModel.DecodeCover(CoverTests.Png(64, 64, Color.Parse("#FF2050C0"))));
            player.ShowCover(Cewka.App.ViewModels.MainViewModel.DecodeCover(CoverTests.Png(64, 64, Color.Parse("#FFC05020"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(player.SceneBlobs, backdrop.ShownColours);

            player.ShowCover(null);
            Dispatcher.UIThread.RunJobs();

            rig.Window.EnterScene();
            Assert.Equal(player.SceneBlobs, backdrop.ShownColours);
            Settle(rig.Window);
            Assert.Equal(player.SceneBlobs, backdrop.ShownColours);
        });
    }

    [Fact]
    public void TloOknaPoWyjsciuZeScenyMaBarwyBiezacegoUtworu()
    {
        Ui.Run(() =>
        {
            using var rig = Open(width: 1280, height: 720);
            var backdrop = rig.Window.Backdrop;
            Settle(rig.Window);

            // Przenikanie zaczęte w oknie i przerwane wejściem do sceny kończy się bez ruchu.
            var first = Plain("#FF203040");
            backdrop.Palette = first;
            rig.Window.EnterScene();
            RunFrames(rig.Window, 400);
            rig.Window.ExitScene();
            Settle(rig.Window);
            Assert.Equal(first, backdrop.ShownColours);

            // Paleta zmieniona w czasie sceny jest od razu na miejscu po wyjściu.
            rig.Window.EnterScene();
            RunFrames(rig.Window, 400);
            var second = Plain("#FF402030");
            backdrop.Palette = second;
            rig.Window.ExitScene();
            Assert.Equal(second, backdrop.ShownColours);
        });
    }

    [Fact]
    public void ScenaOtwartaWPauziePoZmianiePaletyPoSekundzieNieZamawiaKlatek()
    {
        Ui.Run(() =>
        {
            using var rig = Open(play: false);
            var sceneRequests = 0;

            void Count(Visual owner)
            {
                if (ReferenceEquals(TopLevel.GetTopLevel(owner), rig.Window) && rig.Scene.IsVisualAncestorOf(owner))
                    sceneRequests++;
            }

            rig.Scene.Backdrop.Palette = Plain("#FF203040");
            rig.Scene.Backdrop.Palette = Plain("#FF402030");
            RenderLoop.FrameRequested += Count;

            try
            {
                rig.Window.EnterScene();
                RunFrames(rig.Window, 1200);

                sceneRequests = 0;
                RunFrames(rig.Window, 300);
                Assert.Equal(0, sceneRequests);
                Assert.False(rig.Scene.Backdrop.IsDrawing);
            }
            finally
            {
                RenderLoop.FrameRequested -= Count;
            }
        });
    }

    // ---------- Układ i skala ----------

    /// <summary>Prostokąt kontrolki w pikselach sceny, czyli ekranu.</summary>
    private static Rect OnScene(Visual visual, Visual scene)
    {
        var topLeft = visual.TranslatePoint(default, scene)!.Value;
        var bottomRight = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), scene)!.Value;
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>Prostokąty równe z dokładnością do zaokrąglenia układu.</summary>
    private static void AssertRect(Rect expected, Rect actual, string what, double tolerance = 0.51)
    {
        var equal = Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
                    && Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance;

        Assert.True(equal, $"{what}: {actual} zamiast {expected}");
    }

    private static Control Find(SceneView scene, string name)
    {
        var control = scene.FindControl<Control>(name);
        Assert.True(control is not null, $"brak {name}");
        return control;
    }

    /// <summary>Okno z utworem z tytułem, wykonawcą i albumem w tagach, jak na wzorze planszy.</summary>
    private static Rig OpenTagged(int width = 640, int height = 360, Action<AppSettings>? configure = null)
    {
        var rig = Open(tracks: 0, width: width, height: height, configure: configure);
        var h = rig.Harness;
        h.OpenPaths([h.TaggedTrack("katarakta", "Katarakta", "Daniel Bloom feat. Mela Koteluk", "Lovely Fear", seconds: 239)]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Player.HasAlbum), $"tytuł „{h.Player.Title}”");
        return rig;
    }

    /// <summary>Fala, Widmo i Horyzont biegną przez szerokość treści, a tekst nawet z dwuwierszowym tytułem stoi nad nimi.</summary>
    [Theory]
    [InlineData(SceneVisual.Spectrum, true)]
    [InlineData(SceneVisual.Waveform, false)]
    [InlineData(SceneVisual.Horizon, true)]
    public void FalaWidmoIHoryzontIdaPrzezCalaSzerokoscPodTekstem(SceneVisual visual, bool miniCover)
    {
        Ui.Run(() =>
        {
            using var rig = OpenWithTitles(LongTitle, "Katarakta");
            rig.Window.EnterScene();
            rig.Harness.Player.SceneVisual = visual;
            Settle(rig.Window);

            var plane = rig.Scene.Plane;
            var stage = OnScene(rig.Scene.AudioStage, plane);
            var column = OnScene(rig.Scene.InfoColumn, plane);

            Assert.Equal(160, stage.X, 0.5);
            Assert.Equal(1600, stage.Width, 0.5);
            Assert.True(stage.Bottom <= 1000, $"strefa kończy się na {stage.Bottom}");
            Assert.True(OnScene(rig.Scene.SceneTitle, plane).Height > 72, "tytuł miał zająć dwa wiersze");
            Assert.True(column.Bottom <= stage.Y, $"tekst do {column.Bottom}, strefa od {stage.Y}");
            Assert.Equal(1760, column.Right, 0.5);

            Assert.Equal(miniCover, rig.Scene.MiniCover.IsVisible);
            Assert.Equal(miniCover ? 360 : 160, column.X, 0.5);
            if (miniCover)
            {
                var cover = OnScene(rig.Scene.MiniCover, plane);
                Assert.Equal(160, cover.X, 0.5);
                Assert.Equal(column.Y, cover.Y, 0.5);
                Assert.True(cover.Bottom <= stage.Y);
            }

            if (visual == SceneVisual.Horizon) Assert.Equal(756, stage.Center.Y, 1);
        });
    }

    /// <summary>Wymiary planszy 1920 × 1080 jak we wzorze, w skali 1, z polskimi podpisami jak na wzorze.</summary>
    [Fact]
    public void PlanszaW1920Na1080MaUkladWzoru()
    {
        InLanguage("pl", () =>
        {
            using var rig = OpenTagged(1920, 1080);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var scene = rig.Scene;
            Assert.Equal(new Size(1920, 1080), scene.Bounds.Size);
            Assert.Equal(1, scene.Scale, 6);

            AssertRect(new Rect(160, 300, 640, 640), OnScene(scene.DiscHost, scene), "płyta");
            Assert.Equal(new Rect(0, 0, 640, 640), scene.Disc.Bounds);
            AssertRect(new Rect(100, 270, 760, 760), OnScene(Find(scene, "DiscShadow"), scene), "cień płyty");

            var column = OnScene(Find(scene, "InfoColumn"), scene);
            Assert.Equal(920, column.X, 0.5);
            Assert.Equal(880, column.Width, 0.5);
            Assert.Equal(620, column.Center.Y, 1);

            // Tekst: 64/72, 30/38, 20/26 w odstępach 8; pasek 20 i czasy 20 w odstępie 4; grupy co 40.
            var title = OnScene(scene.SceneTitle, scene);
            var artist = OnScene(Find(scene, "SceneArtist"), scene);
            var album = OnScene(Find(scene, "SceneAlbum"), scene);
            var seek = OnScene(scene.Seek, scene);
            var times = OnScene(Find(scene, "TimesRow"), scene);
            var row = OnScene(scene.TransportRow, scene);

            // Wiersz tytułu jest wyższy od planszy o tyle, ile oddaje ujemny margines: w układzie zajmuje 72.
            var titleMargin = scene.SceneTitle.Margin;
            Assert.Equal(column.Y, title.Y - titleMargin.Top, 0.5);
            Assert.Equal(72, title.Height + titleMargin.Top + titleMargin.Bottom, 0.5);
            Assert.Equal(38, artist.Height, 0.5);
            Assert.Equal(26, album.Height, 0.5);
            Assert.Equal(title.Bottom + titleMargin.Bottom + 8, artist.Y, 0.5);
            Assert.Equal(artist.Bottom + 8, album.Y, 0.5);
            AssertRect(new Rect(920, album.Bottom + 40, 880, 20), seek, "pasek");
            AssertRect(new Rect(920, seek.Bottom + 4, 880, 20), times, "czasy");
            Assert.Equal(times.Bottom + 40, row.Y, 0.5);
            Assert.Equal(902, row.X, 0.5);

            // Rząd: przyciski 80 co 104, pola ikon 44 od x = 920, podpisy w jednej linii.
            Button[] buttons = [scene.ShuffleButton, scene.PreviousButton, scene.PlayButton, scene.NextButton, scene.RepeatButton];
            var captionTop = OnScene(buttons[0].GetVisualDescendants().OfType<TextBlock>().Single(), scene).Y;

            for (var i = 0; i < buttons.Length; i++)
            {
                var button = OnScene(buttons[i], scene);
                AssertRect(new Rect(902 + i * 104, row.Y, 80, 90), button, buttons[i].Name!);

                var field = buttons[i].GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("icon-field"));
                AssertRect(new Rect(920 + i * 104, row.Y + 6, 44, 44), OnScene(field, scene), buttons[i].Name + " pole ikony");

                var caption = OnScene(buttons[i].GetVisualDescendants().OfType<TextBlock>().Single(), scene);
                Assert.Equal(captionTop, caption.Y, 0.5);
                Assert.Equal(row.Y + 56, caption.Y, 0.5);
                Assert.Equal(16, caption.Height, 0.5);
            }

            AssertRect(new Rect(902 + 2 * 104 + 12, row.Y, 56, 56), OnScene(Find(scene, "PlayCircle"), scene), "koło odtwarzania");
            AssertRect(new Rect(1920 - 48 - 36, 48, 36, 36), OnScene(scene.ExitButton, scene), "wyjście");

            // Pismo z tokenów skali typograficznej.
            Assert.Equal(64, scene.SceneTitle.FontSize);
            Assert.Equal(FontWeight.Bold, scene.SceneTitle.FontWeight);
            Assert.Equal(2, scene.SceneTitle.MaxLines);
            Assert.Equal(30, ((TextBlock)Find(scene, "SceneArtist")).FontSize);
            Assert.Equal(20, ((TextBlock)Find(scene, "SceneAlbum")).FontSize);
            Assert.Equal(Color.Parse("#FFABABB0"), Solid(((TextBlock)Find(scene, "SceneArtist")).Foreground));
            Assert.Equal(SceneGrey, Solid(((TextBlock)Find(scene, "SceneAlbum")).Foreground));
            Assert.Equal(SceneWhite, Solid(scene.SceneTitle.Foreground));
        });
    }

    /// <summary>Szerokość, jaką napis zająłby krojem i wielkością podpisu, bez przycinania.</summary>
    private static double NaturalWidth(string text, TextBlock style)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontSize = style.FontSize,
            FontFamily = style.FontFamily,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            LetterSpacing = style.LetterSpacing,
        };

        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private static TextBlock Caption(Button button) => button.GetVisualDescendants().OfType<TextBlock>().Single();

    /// <summary>Podpisy rzędu przycisków, z oboma stanami Odtwórz/Wstrzymaj.</summary>
    private static readonly string[] RowCaptions =
        ["SceneShuffle", "ScenePrevious", "ScenePlay", "ScenePause", "SceneNext", "SceneRepeat"];

    /// <summary>Najmniejszy luz podpisu z każdej strony przycisku.</summary>
    private const double CaptionSlack = 4;

    /// <summary>
    /// Podpisy rzędu mieszczą się w przyciskach w każdym języku, z luzem co najmniej 4 z każdej
    /// strony. Przyciski mają wspólną szerokość: 80 albo najdłuższy podpis z luzem, licząc oba stany
    /// Odtwórz/Wstrzymaj, a pole ikony pierwszego zostaje na linii kolumny; po polsku i angielsku 80.
    /// Zmiana stanu odtwarzania i trybu Powtarzaj niczego nie przesuwa, a rząd kończy się przed
    /// prawym brzegiem kolumny.
    /// </summary>
    [Theory]
    [MemberData(nameof(LocalisedLayoutTests.Codes), MemberType = typeof(LocalisedLayoutTests))]
    public void PodpisyPrzyciskowMieszczaSieWPrzyciskachWKazdymJezyku(string code)
    {
        Ui.Run(() =>
        {
            var previous = Strings.Current.CurrentCode;
            Strings.Current.SetLanguage(code);
            Assert.Equal(code, Strings.Current["_code"]);

            try
            {
                // Scena powstaje już w tym języku.
                using var rig = Open(width: 1920, height: 1080);
                var player = rig.Harness.Player;
                rig.Window.FreezeAnimationsForCapture();
                rig.Window.EnterScene();
                rig.Scene.Chrome.RevealByPointer();
                Settle(rig.Window);

                var scene = rig.Scene;
                Button[] buttons = [scene.ShuffleButton, scene.PreviousButton, scene.PlayButton, scene.NextButton, scene.RepeatButton];
                var style = Caption(scene.PlayButton);
                var needed = Math.Max(80, RowCaptions.Max(key => NaturalWidth(Strings.Current[key], style)) + 2 * CaptionSlack);

                Rect[] Check(string state)
                {
                    var rects = buttons.Select(button => OnScene(button, scene)).ToArray();

                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var what = $"{code} {state} {buttons[i].Name}";
                        Assert.InRange(rects[i].Width, needed - 0.01, needed + 1);
                        Assert.Equal(rects[0].Width, rects[i].Width, 0.01);

                        var caption = Caption(buttons[i]);
                        var text = OnScene(caption, scene);
                        var natural = NaturalWidth(caption.Text!, caption);
                        Assert.True(natural <= text.Width + 0.01, $"{what}: „{caption.Text}” potrzebuje {natural:F1} z {text.Width:F1}");
                        Assert.True(text.X - rects[i].X >= CaptionSlack - 0.01 && rects[i].Right - text.Right >= CaptionSlack - 0.01,
                            $"{what}: podpis {text} bez luzu {CaptionSlack} z każdej strony przycisku {rects[i]}");
                    }

                    if (code is "pl" or "en") Assert.Equal(80, rects[0].Width, 0.01);

                    var field = buttons[0].GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("icon-field"));
                    Assert.Equal(920, OnScene(field, scene).X, 0.5);
                    Assert.True(rects[^1].Right <= 1800, $"{code}: rząd kończy się na {rects[^1].Right:F1}");
                    return rects;
                }

                var playing = Check("odtwarzanie");
                Assert.Equal(Strings.Current["ScenePause"], Caption(scene.PlayButton).Text);

                player.Pause();
                Assert.True(rig.Harness.PumpUntil(() => !player.IsPlaying));
                Settle(rig.Window);
                Assert.Equal(Strings.Current["ScenePlay"], Caption(scene.PlayButton).Text);
                Assert.Equal(playing, Check("pauza"));

                for (var mode = 0; mode < 3; mode++)
                {
                    player.CycleRepeat();
                    Settle(rig.Window);
                    Assert.Equal(playing, Check($"powtarzanie {mode}"));
                }
            }
            finally
            {
                Strings.Current.SetLanguage(previous);
            }
        });
    }

    /// <summary>Zmiana języka przy otwartej scenie poszerza i zwęża przyciski rzędu od razu, razem z wysunięciem rzędu.</summary>
    [Fact]
    public void SzerokoscPrzyciskowIdzieZaZmianaJezyka()
    {
        InLanguage("pl", () =>
        {
            using var rig = Open(width: 1920, height: 1080);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var scene = rig.Scene;
            Button[] buttons = [scene.ShuffleButton, scene.PreviousButton, scene.PlayButton, scene.NextButton, scene.RepeatButton];
            var field = buttons[0].GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("icon-field"));
            Assert.All(buttons, button => Assert.Equal(80, OnScene(button, scene).Width, 0.01));

            Strings.Current.SetLanguage("ru");
            Settle(rig.Window);
            var wide = RowCaptions.Max(key => NaturalWidth(Strings.Current[key], Caption(scene.PlayButton))) + 2 * CaptionSlack;
            Assert.True(wide > 80, $"po rosyjsku {wide:F1}");
            Assert.All(buttons, button => Assert.InRange(OnScene(button, scene).Width, wide - 0.01, wide + 1));
            Assert.Equal(920, OnScene(field, scene).X, 0.5);

            Strings.Current.SetLanguage("pl");
            Settle(rig.Window);
            Assert.All(buttons, button => Assert.Equal(80, OnScene(button, scene).Width, 0.01));
            Assert.Equal(902, OnScene(scene.TransportRow, scene).X, 0.5);
        });
    }

    private const string LongTitle =
        "Bardzo długi tytuł utworu z koncertu, który na planszy nie mieści się w dwóch wierszach i musi "
        + "skończyć się wielokropkiem na końcu drugiego wiersza, zamiast zepchnąć pasek postępu";

    /// <summary>
    /// Tytuł na co najmniej trzy wiersze przy 880 jednostkach zajmuje dokładnie dwa, a drugi kończy
    /// się wielokropkiem. Wykonawca, album, pasek, czasy i rząd przycisków leżą pod nim jeden pod
    /// drugim, bez nachodzenia, i mieszczą się w kadrze 1920 × 1080, także bez albumu.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DlugiTytulZajmujeDwaWierszeZWielokropkiemIMiesciSieWKadrze(bool withAlbum)
    {
        InLanguage("pl", () =>
        {
            using var rig = Open(tracks: 0, width: 1920, height: 1080);
            var h = rig.Harness;
            h.OpenPaths([h.TaggedTrack("dlugi", LongTitle, "Daniel Bloom feat. Mela Koteluk", withAlbum ? "Lovely Fear" : "", seconds: 239)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Player.Title == LongTitle), $"tytuł „{h.Player.Title}”");
            Assert.Equal(withAlbum, h.Player.HasAlbum);
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);

            var scene = rig.Scene;
            var title = scene.SceneTitle;

            // Bez ograniczenia wierszy ten tytuł zająłby co najmniej trzy.
            using (var free = new Avalonia.Media.TextFormatting.TextLayout(LongTitle,
                       new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch), title.FontSize,
                       foreground: null, textWrapping: TextWrapping.Wrap, maxWidth: 880, lineHeight: title.LineHeight))
            {
                Assert.True(free.TextLines.Count >= 3, $"{free.TextLines.Count} wiersze bez ograniczenia");
            }

            var lines = title.TextLayout.TextLines;
            Assert.Equal(2, lines.Count);
            Assert.False(lines[0].HasCollapsed);
            Assert.True(lines[1].HasCollapsed, "drugi wiersz nie jest skrócony");
            var shown = string.Concat(lines[1].TextRuns.Select(run => run.Text.ToString()));
            Assert.EndsWith("…", shown);

            var titleRect = OnScene(title, scene);
            Assert.Equal(2 * ((TextBlock)title).LineHeight, titleRect.Height, 0.5);

            var parts = new List<(string Name, Rect Rect)> { ("tytuł", titleRect), ("wykonawca", OnScene(Find(scene, "SceneArtist"), scene)) };
            if (withAlbum) parts.Add(("album", OnScene(Find(scene, "SceneAlbum"), scene)));
            else Assert.False(Find(scene, "SceneAlbum").IsVisible);
            parts.Add(("pasek", OnScene(scene.Seek, scene)));
            parts.Add(("czasy", OnScene(Find(scene, "TimesRow"), scene)));
            parts.Add(("rząd", OnScene(scene.TransportRow, scene)));

            for (var i = 0; i < parts.Count; i++)
            {
                var (name, rect) = parts[i];
                Assert.True(rect.Y >= 0 && rect.Bottom <= 1080 && rect.X >= 900 && rect.Right <= 1800,
                    $"{name} {rect} poza kadrem albo kolumną");
                if (i > 0)
                    Assert.True(parts[i - 1].Rect.Bottom <= rect.Y, $"{parts[i - 1].Name} {parts[i - 1].Rect} nachodzi na {name} {rect}");
            }
        });
    }

    /// <summary>
    /// Środek uchwytu paska na narysowanym kadrze: średnie x jasnych pikseli nad torem i pod nim,
    /// bo w samym torze leży też wypełnienie w tej samej barwie.
    /// </summary>
    private static double ThumbCentreOnPlane(SceneView scene)
    {
        var seek = OnScene(scene.Seek, scene);
        var pixels = ControlPixels.Render(scene.Plane, 1920, 1080);
        var middle = (int)Math.Round(seek.Center.Y);
        double sum = 0;
        var count = 0;

        for (var y = middle - 16; y <= middle + 16; y++)
        {
            if (Math.Abs(y + 0.5 - seek.Center.Y) < 4) continue;

            for (var x = 880; x < 1840; x++)
            {
                if (pixels[x, y] is not { A: > 200, R: > 230, G: > 230, B: > 230 }) continue;
                sum += x + 0.5;
                count++;
            }
        }

        Assert.True(count > 0, "uchwytu nie widać");
        return sum / count;
    }

    /// <summary>
    /// Uchwyt paska stoi w torze jak suwak range w przeglądarce, także powiększony do 28 w regulacji
    /// strzałkami: przy 0 i 1 nie wychodzi poza kolumnę 920–1800, a w połowie toru jego środek stoi
    /// tam, gdzie w spoczynku.
    /// </summary>
    [Fact]
    public void UchwytPaskaWRegulacjiMiesciSieWTorzeAWPolowieNieZmieniaSrodka()
    {
        Ui.Run(() =>
        {
            using var rig = Open(tracks: 1, play: false, width: 1920, height: 1080);
            rig.Window.FreezeAnimationsForCapture();
            rig.Window.EnterScene();
            Settle(rig.Window);

            var scene = rig.Scene;
            var seek = scene.Seek;
            Assert.True(seek.ThumbInTrack);

            SeekTo(rig.Harness, 30);
            Settle(rig.Window);
            Press(rig.Window, Key.Up);
            Press(rig.Window, Key.Up);
            Assert.Same(seek, Focused(rig.Window));
            Assert.Equal(0.5, seek.Value, 3);
            Assert.Equal(20, seek.ThumbDiameter);
            var rest = ThumbCentreOnPlane(scene);
            Assert.InRange(rest, 1359.5, 1360.5);

            Press(rig.Window, Key.Enter);
            Assert.True(scene.Chrome.IsAdjusting);
            Assert.Equal(28, seek.ThumbDiameter);
            Assert.InRange(ThumbCentreOnPlane(scene), rest - 0.5, rest + 0.5);

            foreach (var (key, value) in new[] { (Key.Home, 0.0), (Key.End, 1.0) })
            {
                Press(rig.Window, key);
                Assert.Equal(value, seek.Value);
                var centre = ThumbCentreOnPlane(scene);
                Assert.True(centre - 14 >= 919.5 && centre + 14 <= 1800.5, $"przy {value}: uchwyt od {centre - 14:F1} do {centre + 14:F1}");
            }

            Press(rig.Window, Key.Enter);
            Assert.False(scene.Chrome.IsAdjusting);
            Assert.Equal(20, seek.ThumbDiameter);
        });
    }

    /// <summary>
    /// Kadr 1920 × 1080 skaluje się równomiernie o min(W/1920, H/1080) i stoi w środku ekranu; tło
    /// wypełnia cały ekran, a przycisk wyjścia trzyma się narożnika ekranu.
    /// </summary>
    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1080)]
    [InlineData(1920, 1200)]
    public void KadrSkalujeSieRownomiernieIStoiWSrodkuAWyjscieWNarozniku(int width, int height)
    {
        InLanguage("pl", () =>
        {
            using var rig = Open(tracks: 0, width: width, height: height);
            rig.Window.EnterScene();
            Settle(rig.Window);

            var scene = rig.Scene;
            Assert.Equal(new Size(width, height), scene.Bounds.Size);

            var scale = Math.Min(width / 1920.0, height / 1080.0);
            var x = (width - 1920 * scale) / 2;
            var y = (height - 1080 * scale) / 2;
            Rect Frame(double left, double top, double w, double h) => new(x + left * scale, y + top * scale, w * scale, h * scale);

            Assert.Equal(scale, scene.Scale, 6);
            AssertRect(Frame(160, 300, 640, 640), OnScene(scene.DiscHost, scene), "płyta");

            var column = OnScene(Find(scene, "InfoColumn"), scene);
            Assert.Equal(x + 920 * scale, column.X, 1);
            Assert.Equal(880 * scale, column.Width, 1);
            Assert.Equal(y + 620 * scale, column.Center.Y, 1);

            var field = scene.ShuffleButton.GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("icon-field"));
            Assert.Equal(x + 920 * scale, OnScene(field, scene).X, 1);

            AssertRect(new Rect(width - 84 * scale, 48 * scale, 36 * scale, 36 * scale), OnScene(scene.ExitButton, scene), "wyjście");

            foreach (var name in new[] { "CoverLayer", "DimAcross", "DimBottom" })
                AssertRect(new Rect(0, 0, width, height), OnScene(Find(scene, name), scene), name);
            AssertRect(new Rect(0, 0, width, height), OnScene(scene.Backdrop, scene), "plamy");
        });
    }

    /// <summary>Wszystko, co widać, ma ten sam prostokąt w aktywności i w spoczynku.</summary>
    [Fact]
    public void ZmianaStanuNiczegoNiePrzesuwa()
    {
        Ui.Run(() =>
        {
            using var rig = OpenTagged(1920, 1080, configure: s => s.Effects = EffectsMode.Reduced);
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);
            var active = Layout(rig.Scene);

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Settle(rig.Window);
            Assert.False(rig.Scene.Chrome.IsShown);
            var idle = Layout(rig.Scene);

            Assert.Equal(active.Count, idle.Count);
            Assert.True(active.Count > 40, $"{active.Count} elementów");
            foreach (var (visual, rect) in active)
                AssertRect(rect, idle[visual], $"{visual.GetType().Name} {(visual as Control)?.Name}", tolerance: 0.01);
        });
    }

    private static Dictionary<Visual, Rect> Layout(SceneView scene) =>
        scene.GetVisualDescendants().Where(visual => visual.IsEffectivelyVisible).ToDictionary(visual => visual, visual => OnScene(visual, scene));

    private static double ThumbTarget(TrackSlider slider) =>
        slider.GetBaseValue(TrackSlider.ThumbOpacityProperty) is { HasValue: true } target ? target.Value : slider.ThumbOpacity;

    private static void AssertFade(Transitions? transitions, AvaloniaProperty property)
    {
        Assert.NotNull(transitions);
        var fade = Assert.Single(transitions.OfType<DoubleTransition>(), transition => transition.Property == property);
        Assert.Equal(TimeSpan.FromMilliseconds(200), fade.Duration);
        Assert.IsType<CubicEaseOut>(fade.Easing);
    }

    /// <summary>
    /// Spoczynek chowa przez krycie tylko rząd przycisków, uchwyt paska i przycisk wyjścia, z miejscem
    /// zostawionym w układzie. Tytuł, wykonawca, album, tor paska i czasy zostają, także po ośmiu sekundach.
    /// </summary>
    [Fact]
    public void SpoczynekChowaTylkoRzadUchwytPaskaIWyjscie()
    {
        Ui.Run(() =>
        {
            using var rig = OpenTagged();
            rig.Window.EnterScene();
            rig.Scene.Chrome.RevealByPointer();
            Settle(rig.Window);

            var scene = rig.Scene;
            Control[] hiding = [scene.TransportRow, scene.ExitButton];
            Control[] staying =
            [
                scene.TitleBlock, scene.SceneTitle, Find(scene, "SceneArtist"), Find(scene, "SceneAlbum"),
                scene.Seek, Find(scene, "TimesRow"),
            ];

            foreach (var part in hiding)
            {
                Assert.Equal(1, Target(part));
                Assert.True(part.IsHitTestVisible, part.Name);
                AssertFade(part.Transitions, Visual.OpacityProperty);
            }

            Assert.Equal(1, ThumbTarget(scene.Seek));
            AssertFade(scene.Seek.Transitions, TrackSlider.ThumbOpacityProperty);

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.False(scene.Chrome.IsShown);

            foreach (var part in hiding)
            {
                Assert.Equal(0, Target(part));
                Assert.False(part.IsHitTestVisible, part.Name);
                Assert.True(part.IsVisible, part.Name);
            }

            Assert.Equal(0, ThumbTarget(scene.Seek));

            rig.Wait(5);
            foreach (var part in staying)
            {
                Assert.Equal(1, Target(part));
                Assert.True(part.IsEffectivelyVisible, part.Name);
            }

            Assert.Equal("Katarakta", scene.SceneTitle.Text);
            Assert.Equal("Daniel Bloom feat. Mela Koteluk", ((TextBlock)Find(scene, "SceneArtist")).Text);
            Assert.Equal("Lovely Fear", ((TextBlock)Find(scene, "SceneAlbum")).Text);
        });
    }

    /// <summary>
    /// Zamrożenie dla narzędzia zrzutów: płyta prosto, plamy stoją, a scena, rząd, wyjście i uchwyt
    /// paska zmieniają krycie od razu, więc zrzut nie łapie ich w połowie przejścia.
    /// </summary>
    [Fact]
    public void ZamrozenieDoZrzutuWylaczaPrzejsciaPlanszy()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.FreezeAnimationsForCapture();
            rig.Window.OpenSceneForCapture(showControls: true);
            Settle(rig.Window);

            var scene = rig.Scene;
            Assert.Null(scene.Transitions);
            Assert.Null(scene.Seek.Transitions);
            Assert.Null(scene.TransportRow.Transitions);
            Assert.Null(scene.ExitButton.Transitions);
            Assert.Equal(0, scene.Disc.Angle);
            Assert.False(scene.Backdrop.IsLooping);

            Assert.Equal(1, scene.Opacity);
            Assert.Equal(1, scene.TransportRow.Opacity);
            Assert.Equal(1, scene.Seek.ThumbOpacity);

            rig.Wait(SceneChrome.HideDelay.TotalSeconds + 0.1);
            Assert.Equal(0, scene.TransportRow.Opacity);
            Assert.Equal(0, scene.ExitButton.Opacity);
            Assert.Equal(0, scene.Seek.ThumbOpacity);
        });
    }

    [Fact]
    public void BrakWykonawcyIAlbumuChowaIchWiersze()
    {
        Ui.Run(() =>
        {
            using var rig = Open();
            rig.Window.EnterScene();
            Settle(rig.Window);

            Assert.False(rig.Harness.Player.HasArtist);
            Assert.False(Find(rig.Scene, "SceneArtist").IsVisible);
            Assert.False(Find(rig.Scene, "SceneAlbum").IsVisible);
            Assert.True(rig.Scene.SceneTitle.IsEffectivelyVisible);
        });
    }
}
