using Avalonia.Animation;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.Audio.Decoding;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Platform;
using Cewka.Platform.Linux;

namespace Cewka.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Stopnie układu według szerokości okna, od wąskiego do zmaksymalizowanego na dużym ekranie.
    /// Avalonia nie ma odpowiednika zapytań o media z CSS, więc kilka właściwości ustawia się wprost.
    /// </summary>
    private static readonly (double MinWidth, double Disc, double Title, double Queue, double Volume)[]
        LayoutSteps =
        [
            (0, 228, 32, 280, 88),
            (1080, 300, 42, 280, 88),
            (1500, 356, 50, 320, 104),
            (1900, 412, 58, 360, 120),
            (2400, 468, 64, 400, 132),
        ];

    /// <summary>Grab band along the window border for resizing, where the system gives none.</summary>
    private const double ResizeMargin = 6;

    /// <summary>Rozmiar projektowy okna przy pierwszym uruchomieniu, przycinany do ekranu.</summary>
    private const double DesignWidth = 1200;
    private const double DesignHeight = 680;

    /// <summary>Szerokość, którą musi mieć opis utworu, żeby rząd transportu nie wychodził poza blok.</summary>
    private const double TrackInfoMinWidth = 440;

    /// <summary>Pismo etykiety jakości w wierszu wykonawcy: cztery piąte wykonawcy, nie mniej niż napis „Na żywo”.</summary>
    internal static double QualityFontSize(double artistFontSize) => Math.Max(11, Math.Round(artistFontSize * 0.8, 1));

    /// <summary>Płyta mniejsza od tego wygląda jak usterka, więc znika, oddając miejsce tekstowi.</summary>
    private const double SmallestDisc = 140;

    private readonly MainViewModel _viewModel;
    private readonly DiscSpinner _spinner;

    /// <summary>
    /// W Windows okno ma systemową ramkę (bez paska tytułu): przyciąganie do krawędzi, Win+strzałki,
    /// układy przyciągania i zmianę rozmiaru daje system. W Linuksie okno jest bez dekoracji,
    /// a przesuwanie i zmianę rozmiaru obsługuje kod poniżej.
    /// </summary>
    private readonly bool _systemFrame;

    /// <summary>Położenie i rozmiar z ostatniej chwili w stanie Normal; tylko te trafiają do ustawień.</summary>
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _normalPositionKnown;

    /// <summary>Ostatni stan Normal albo Maximized: do niego wraca okno po scenie i minimalizacji.</summary>
    private WindowState _lastRegularState = WindowState.Normal;

    /// <summary>Stan okna sprzed sceny, przywracany przy wyjściu z niej.</summary>
    private WindowState _stateBeforeScene = WindowState.Normal;

    /// <summary>Wskaźnik ostatniego naciśnięcia w oknie; wejście do sceny zwalnia jego przechwycenie.</summary>
    private IPointer? _pressedPointer;

    /// <summary>Stan okna zmienia scena, a nie system; taka zmiana nie zamyka sceny.</summary>
    private bool _changingSceneState;

    private readonly SceneWakeLock _wakeLock;

    /// <summary>Okno za wąskie na kolumnę: przypięty panel kolejki jest nakładany nad treść.</summary>
    private bool _queueAsOverlay;

    /// <summary>
    /// Panel odpięło zwężenie okna, a nie użytkownik. Po poszerzeniu wraca przypięty, a ustawienia
    /// zachowują go jako przypięty.
    /// </summary>
    private bool _queueCollapsedByWidth;
    private bool _adjustingQueue;

    public MainWindow() : this(OperatingSystem.IsWindows())
    {
    }

    /// <param name="systemFrame">Ramka i narożniki systemowe (Windows) zamiast własnych.</param>
    internal MainWindow(bool systemFrame) : this(systemFrame, new MainViewModel(App.Settings))
    {
    }

    /// <param name="viewModel">Model widoku podany z zewnątrz, np. na silniku testowym.</param>
    internal MainWindow(bool systemFrame, MainViewModel viewModel)
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method
        // assigns the x:Name backing fields, and every piece of behaviour below depends
        // on them (disc rotation, queue scroll bar, responsive layout).
        InitializeComponent();
        Icon = AppIcon.Window;

        _viewModel = viewModel;
        DataContext = _viewModel;
        AttachQueuePanel();

        _spinner = new DiscSpinner(Disc);
        _wakeLock = new SceneWakeLock(() => WakeLockFactory());
        Scene.ExitRequested += ExitScene;
        Scene.Covered += () =>
        {
            if (Scene.IsOpen) ShowWindowContent(false);
        };
        AddHandler(GotFocusEvent, RememberFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        ScreenSource = () => SceneScreens.Distinguish(Screens.All.Select(ScreenInfo.From));
        WindowScreenSource = () => Screens.ScreenFromWindow(this) is { } screen
            ? SceneScreens.At(ScreenSource(), screen.Bounds) ?? ScreenInfo.From(screen)
            : null;
        Screens.Changed += OnScreensChanged;

        _systemFrame = systemFrame;
        if (_systemFrame)
        {
            WindowDecorations = WindowDecorations.BorderOnly;
            TitleBarRoles.Apply(TitleBar);
            SystemMenu.Attach(this);
        }

        ApplyWindowShape();
        RestoreGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Spacja i strzałki przed kontrolką z fokusem: inaczej przycisk kliknięty myszą
        // przechwytuje Spację i powtarza swoje działanie.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) => _pressedPointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnMenuOutsidePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        MenuTrayItems.AddHandler(Button.ClickEvent, OnMenuTrayClick, RoutingStrategies.Bubble, handledEventsToo: true);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsRadioMode) or nameof(MainViewModel.RadioAvailable)) UpdateMenuMark();
        };
        UpdateMenuMark();

        AttachQueueInput();
        AttachRadioInput();
        _viewModel.DeviceSelectionRequested += () => OpenSettings(settings => settings.Audio);

        PositionChanged += (_, _) => RememberNormalBounds();
        CentreHost.SizeChanged += (_, _) => ApplyResponsiveLayout();
        TrackInfo.SizeChanged += (_, _) => ApplyResponsiveLayout();

        // Wiersze zastąpione w trakcie przeciągania: pod tym samym numerem jest już inny utwór.
        _viewModel.Queue.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) CancelQueueDrag();
            UpdateQueueNumberWidth();
        };
        UpdateQueueNumberWidth();

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentIndex): RevealCurrent(); break;
                case nameof(MainViewModel.QueuePinned): OnQueuePinnedChanged(); break;
                case nameof(MainViewModel.QueueReveal):
                case nameof(MainViewModel.QueueHideDelay):
                case nameof(MainViewModel.QueueRevealDelay): UpdatePeekSettings(); break;
                case nameof(MainViewModel.QueueWidth): ApplyResponsiveLayout(); break;
                case nameof(MainViewModel.PanelOpen): ApplySoundWindow(); break;
                case nameof(MainViewModel.IsPlaying):
                    UpdateRotationLoop();
                    UpdateWakeLock();
                    break;
                case nameof(MainViewModel.ReducedEffects): UpdateRotationLoop(); break;
                case nameof(MainViewModel.DiscSpin): UpdateRotationLoop(); break;
                case nameof(MainViewModel.BackdropMotion): Backdrop.MotionScale = _viewModel.BackdropMotionFactor; break;
                case nameof(MainViewModel.KeepScreenAwake): UpdateWakeLock(); break;
            }
        };

        Backdrop.MotionScale = _viewModel.BackdropMotionFactor;
    }

    // ================= Cykl zycia =================

    /// <summary>
    /// Panel multimediów pulpitu i klawisze multimedialne. Wyłącza je pomiar okna, żeby okno
    /// postawione na chwilę nie zgłaszało się w panelu multimediów systemu.
    /// </summary>
    internal bool AttachDesktopIntegration { get; init; } = true;

    /// <summary>Blokada wygaszania ekranu dla sceny; testy podstawiają atrapę.</summary>
    internal Func<IDisplayWakeLock> WakeLockFactory { get; init; } =
        () => DisplayWakeLock.Create(Strings.Current["WakeLockReason"]);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        ApplyResponsiveLayout();
        AttachQueueScrollBar();

        // „Otwórz za pomocą” ma zagrać wskazany plik zamiast kolejki z poprzedniej sesji.
        if (App.StartupPaths.Length > 0)
        {
            _viewModel.OpenAtStart(App.StartupPaths);
        }
        else
        {
            _viewModel.RestoreQueueState();
            _viewModel.PlayRadioAtStart();
        }

        RevealCurrent();

        // Bez czekania na wynik, żeby start nie zależał od sieci.
        _ = _viewModel.CheckForUpdatesIfDueAsync();

        if (AttachDesktopIntegration) AttachMediaPanel();

        UpdateRotationLoop();
    }

    /// <summary>
    /// Podłącza okno do panelu multimediów pulpitu: nakładki Windows albo MPRIS pod Linuksem.
    /// Dopiero po otwarciu okna, bo panel Windows należy do okna, a nie do procesu.
    /// </summary>
    private void AttachMediaPanel()
    {
        if (OperatingSystem.IsWindows()) AttachWindowsPanel();
        else if (OperatingSystem.IsLinux()) AttachMpris();

        App.MediaPanelActive = _mediaPanel is not null;
        if (_mediaPanel is not null) _viewModel.AttachMediaPanel(_mediaPanel);

        App.ApplyMediaKeysSetting();
    }

    private void AttachWindowsPanel()
    {
        var panel = SystemMediaControls.TryCreate(TryGetPlatformHandle()?.Handle ?? nint.Zero);
        if (panel is null) return;

        panel.ButtonPressed += key => Dispatcher.UIThread.Post(() => HandleMediaKey(key));
        _mediaPanel = panel;
    }

    /// <summary>
    /// Wystawia odtwarzacz na szynie sesji. Pulpit Linuksa przekazuje tędy też klawisze
    /// multimedialne.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void AttachMpris()
    {
        var mpris = MprisService.TryStart("Cewka", "cewka");
        if (mpris is null) return;

        mpris.CommandReceived += command => Dispatcher.UIThread.Post(() => HandleMediaCommand(command));
        mpris.SeekRequested += offset => Dispatcher.UIThread.Post(() => _viewModel.SeekRelative(offset.TotalSeconds));
        mpris.PositionRequested += position => Dispatcher.UIThread.Post(() => _viewModel.SeekToPosition(position));
        mpris.VolumeRequested += volume => Dispatcher.UIThread.Post(() => _viewModel.Volume = volume);
        mpris.OpenRequested += path => Dispatcher.UIThread.Post(() => _viewModel.OpenFromOutside([path]));

        mpris.RaiseRequested += () => Dispatcher.UIThread.Post(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = _lastRegularState;
            Activate();
        });

        mpris.QuitRequested += () => Dispatcher.UIThread.Post(Close);

        _mediaPanel = mpris;
    }

    private IMediaPanel? _mediaPanel;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Termin dokańczania otwarć biegnie od tej chwili, także dla ścieżek odebranych niżej.
        _viewModel.BeginClosing();
        App.BeginClosing();
        _spinner.Stop();
        _wakeLock.Dispose();
        PersistGeometry();
        _viewModel.FinishPendingOpens();
        _viewModel.SaveQueueState();
        App.Settings.SaveNow();
        App.ReleaseSingleInstance();
        base.OnClosing(e);
    }

    private void OnScreensChanged(object? sender, EventArgs e) => HandleScreensChanged(ListScreens());

    protected override void OnClosed(EventArgs e)
    {
        Screens.Changed -= OnScreensChanged;
        _mediaPanel?.Dispose();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RememberNormalBounds();
        ApplyResponsiveLayout();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty) OnWindowStateChanged(change.GetNewValue<WindowState>());
    }

    // ================= Stan i geometria okna =================

    private void OnWindowStateChanged(WindowState state)
    {
        if (state is WindowState.Normal or WindowState.Maximized) _lastRegularState = state;

        // Pełny ekran istnieje tylko razem ze sceną: zmiana stanu przez system (minimalizacja,
        // skrót menedżera okien) zamyka scenę, a pełny ekran nadany z zewnątrz ją otwiera.
        if (!_changingSceneState)
        {
            if (Scene.IsOpen && state != WindowState.FullScreen) CloseScene(restoreState: false);
            else if (!Scene.IsOpen && state == WindowState.FullScreen) OpenScene(_lastRegularState);
        }

        ApplyWindowShape();
        RememberNormalBounds();
        UpdateWakeLock();
    }

    /// <summary>
    /// Proste narożniki bez obwódki poza zwykłym oknem, a w Windows zawsze, bo tam narożniki,
    /// obwódkę i cień rysuje system. Na pełnym ekranie pasek tytułu nie przesuwa okna.
    /// </summary>
    private void ApplyWindowShape()
    {
        var state = WindowState;
        Shell.Classes.Set("square", _systemFrame || state is WindowState.Maximized or WindowState.FullScreen);

        if (_systemFrame)
        {
            WindowDecorationProperties.SetElementRole(TitleBar, state == WindowState.FullScreen
                ? WindowDecorationsElementRole.None
                : WindowDecorationsElementRole.TitleBar);
        }
    }

    /// <summary>
    /// Zapamiętuje położenie i rozmiar zwykłego okna. Zatwierdzane po obsłużeniu bieżących
    /// komunikatów: w Windows zdarzenie przesunięcia przychodzi przed zmianą stanu, więc położenie
    /// okna maksymalizowanego wziętoby za zwykłe.
    /// </summary>
    private void RememberNormalBounds()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WindowState != WindowState.Normal || !IsVisible || _movedForScene) return;

            _normalPosition = Position;
            _normalPositionKnown = true;
            _normalSize = ClientSize;
        });
    }

    private void RestoreGeometry()
    {
        var saved = App.Settings.Current.Window;

        if (saved is null)
        {
            // Pierwsze uruchomienie: rozmiar projektowy, ale nie większy niż 90 % obszaru roboczego.
            if (WorkingArea(Screens.Primary) is { } primary)
            {
                Width = Math.Max(MinWidth, Math.Min(DesignWidth, primary.Width * 0.9));
                Height = Math.Max(MinHeight, Math.Min(DesignHeight, primary.Height * 0.9));
            }

            _normalSize = new Size(Width, Height);
            return;
        }

        Width = Math.Max(MinWidth, saved.Width);
        Height = Math.Max(MinHeight, saved.Height);

        // Ekran mógł zniknąć albo zmienić rozdzielczość od ostatniego razu. Okno trafia na ekran,
        // na którym leży największa część jego paska tytułu, i tak, żeby pasek dało się chwycić.
        // Gdy pasek nie leży na żadnym ekranie, okno staje na środku głównego.
        var titleScreen = ScreenForTitleBar(saved);
        if ((titleScreen ?? Screens.Primary ?? Screens.All.FirstOrDefault()) is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;

            Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scale));
            Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scale));

            var width = (int)(Width * scale);
            var height = (int)(Height * scale);
            var grip = (int)(100 * scale);
            var x = titleScreen is null
                ? area.X + Math.Max(0, (area.Width - width) / 2)
                : Math.Clamp(saved.X, area.X - width + grip, area.Right - grip);
            var y = titleScreen is null
                ? area.Y + Math.Max(0, (area.Height - height) / 2)
                : Math.Clamp(saved.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(47 * scale)));

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _normalPosition = Position;
            _normalPositionKnown = true;
        }

        _normalSize = new Size(Width, Height);

        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Obszar roboczy ekranu w jednostkach logicznych.</summary>
    private static Size? WorkingArea(Avalonia.Platform.Screen? screen) =>
        screen is null ? null : new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);

    private Avalonia.Platform.Screen? ScreenForTitleBar(WindowGeometry saved)
    {
        Avalonia.Platform.Screen? best = null;
        long bestArea = 0;

        foreach (var screen in Screens.All)
        {
            var strip = new PixelRect(saved.X, saved.Y,
                (int)(saved.Width * screen.Scaling), (int)(47 * screen.Scaling));
            var common = strip.Intersect(screen.WorkingArea);
            var area = (long)common.Width * common.Height;

            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
        }

        return best;
    }

    /// <summary>
    /// Zapisuje geometrię zwykłego okna z ostatniej chwili w stanie Normal. Zamknięcie w scenie
    /// zapisuje stan sprzed niej, a w zminimalizowanym oknie liczy się stan sprzed minimalizacji.
    /// </summary>
    internal void PersistGeometry()
    {
        var state = WindowState;
        var maximized = state == WindowState.Maximized
                        || (state == WindowState.FullScreen && _stateBeforeScene == WindowState.Maximized)
                        || (state == WindowState.Minimized && _lastRegularState == WindowState.Maximized);

        var previous = App.Settings.Current.Window;
        var position = _normalPositionKnown ? _normalPosition : new PixelPoint(previous?.X ?? Position.X, previous?.Y ?? Position.Y);

        App.Settings.Current.Window = new WindowGeometry
        {
            X = position.X,
            Y = position.Y,
            Width = _normalSize.Width,
            Height = _normalSize.Height,
            Maximized = maximized,
        };

        App.Settings.Touch();
    }

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ================= Scena =================

    internal bool IsSceneOpen => Scene.IsOpen;

    internal void ToggleScene()
    {
        if (Scene.IsOpen) ExitScene();
        else EnterScene();
    }

    /// <summary>Otwiera scenę na pełnym ekranie; wyjście wraca do stanu okna sprzed niej.</summary>
    internal void EnterScene() =>
        OpenScene(WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal);

    internal void ExitScene() => CloseScene(restoreState: true);

    /// <summary>Scena dla narzędzia zrzutów, z warstwą sterowania albo w spoczynku.</summary>
    public void OpenSceneForCapture(bool showControls)
    {
        EnterScene();
        if (showControls) Scene.Chrome.RevealByPointer();
    }

    /// <summary>
    /// Scena przenika nad treścią okna, także w trybie ograniczonym. Pętle okna stają od razu,
    /// a treść i tło znikają, gdy scena zakryje je w całości: niewidoczne nie liczy klatek.
    /// </summary>
    private void OpenScene(WindowState returnTo)
    {
        if (Scene.IsOpen) return;

        // Suwak okna przeciągany w chwili wejścia przestałby być widoczny, a puszczenie nad sceną
        // przewinęłoby utwór albo zmieniło głośność.
        Seek.EndDrag();
        VolumeSlider.EndDrag();

        // Każda inna przeciągana kontrolka (suwak korektora, pasek przewijania, wiersz kolejki) znika
        // pod sceną: ruch i puszczenie nad sceną nie mogą do niej trafić, a przeciąganie w kolejce
        // nie może czekać na późniejsze puszczenie.
        _pressedPointer?.Capture(null);
        CancelQueueDrag();
        EndQueueResize();

        // Podgląd panelu nie wraca po scenie; przypięty panel zostaje pod nią.
        _peek.Close();

        _stateBeforeScene = returnTo;
        RememberFocusBeforeScene();
        SuspendWindowContent(true);
        // Pełny ekran nadany z zewnątrz stoi na ekranie okna, niezależnie od zapisanego wyboru.
        _sceneScreen = WindowState == WindowState.FullScreen ? WindowScreen() : SceneScreen();
        Scene.ScreenKey = _sceneScreen?.Key;
        Scene.ScreenBounds = _sceneScreen?.Bounds;
        MoveToSceneScreen(_sceneScreen);
        Scene.Open();
        UpdatePeekSettings();
        SetStateForScene(WindowState.FullScreen);
        UpdateWakeLock();

        // Okno korektora należy do okna głównego i stałoby nad sceną; wraca po jej zamknięciu.
        _soundWindow?.Hide();
    }

    /// <summary>
    /// Przenosi okno na ekran sceny przed pełnym ekranem, bo pełny ekran zajmuje ekran, na którym stoi
    /// okno. Ze stanu zmaksymalizowanego najpierw zwykły, bo położenie zmaksymalizowanego okna jest
    /// ignorowane. Scena nie ma osobnego okna.
    /// </summary>
    private void MoveToSceneScreen(ScreenInfo? target)
    {
        _movedForScene = false;

        // Pełny ekran nadany z zewnątrz już stoi na swoim ekranie.
        if (WindowState == WindowState.FullScreen) return;

        var current = WindowScreen();
        if (target is not { } screen || current is { } here && here.Bounds == screen.Bounds) return;

        _positionBeforeScene = _stateBeforeScene == WindowState.Maximized && _normalPositionKnown ? _normalPosition : Position;
        _movedForScene = true;

        if (WindowState == WindowState.Maximized) SetStateForScene(WindowState.Normal);
        Position = screen.Bounds.Position;
    }

    /// <summary>
    /// Wyjście z pełnego ekranu: stan sprzed sceny, a po przeniesieniu także położenie. Gdy ekran sceny
    /// zniknął, okno staje na ekranie głównym w obszarze roboczym.
    /// </summary>
    private void LeaveSceneState(ScreenInfo? home)
    {
        if (!_movedForScene && home is null)
        {
            SetStateForScene(_stateBeforeScene);
            return;
        }

        SetStateForScene(WindowState.Normal);
        Position = home is { } screen
            ? SceneScreens.Centre(screen.WorkingArea, new PixelSize((int)(ClientSize.Width * screen.Scaling), (int)(ClientSize.Height * screen.Scaling)))
            : _positionBeforeScene;
        _movedForScene = false;

        if (_stateBeforeScene == WindowState.Maximized) SetStateForScene(WindowState.Maximized);
        RememberNormalBounds();
    }

    /// <summary>
    /// Zmiana ekranów w systemie. Zniknięcie ekranu sceny zamyka scenę i sprowadza okno na ekran główny;
    /// odłączenie innego ekranu nic nie zmienia.
    /// </summary>
    internal void HandleScreensChanged(IReadOnlyList<ScreenInfo> screens)
    {
        if (!Scene.IsOpen || _sceneScreen is not { } used) return;

        // Numery jednakowych monitorów zależą od położenia, więc klucz może przejść na inny monitor. Monitor
        // sceny to ten sam model: najpierw o tych samych granicach, potem o tym samym kluczu, a gdy odłączono
        // jeden z jednakowych i nie wiadomo, który został, dowolny pozostały tego modelu.
        var model = SceneScreens.Model(used.Key);
        var same = screens.Where(screen => SceneScreens.Model(screen.Key) == model).ToArray();
        if (same.Length > 0)
        {
            var current = SceneScreens.At(same, used.Bounds) ?? SceneScreens.WithKey(same, used.Key) ?? same[0];
            if (current == used) return;

            _sceneScreen = current;
            Scene.ScreenKey = current.Key;
            Scene.ScreenBounds = current.Bounds;

            // System mógł przenieść okno pełnoekranowe na inny ekran, np. główny: wraca na monitor sceny.
            if (WindowScreen() is not { } here || here.Bounds != current.Bounds)
            {
                SetStateForScene(WindowState.Normal);
                Position = current.Bounds.Position;
                SetStateForScene(WindowState.FullScreen);
            }

            return;
        }

        CloseScene(restoreState: true, home: SceneScreens.Home(screens));
        _viewModel.ShowNotice(Strings.Current["SceneScreenLost"]);
    }

    /// <summary>Ekrany podłączone w tej chwili; testy podstawiają własną listę.</summary>
    internal Func<IReadOnlyList<ScreenInfo>> ScreenSource { get; set; } = null!;

    /// <summary>Ekran, na którym stoi okno; testy podstawiają własny.</summary>
    internal Func<ScreenInfo?> WindowScreenSource { get; set; } = null!;

    private IReadOnlyList<ScreenInfo> ListScreens() => ScreenSource();

    private ScreenInfo? WindowScreen() => WindowScreenSource();

    /// <summary>Ekran, na którym stanie scena: zapisany, gdy jest podłączony, inaczej ekran okna.</summary>
    private ScreenInfo? SceneScreen() => SceneScreens.Pick(ListScreens(), _viewModel.SceneScreen, WindowScreen());

    /// <summary>Ekran, na którym scena jest otwarta; do odłączenia ekranu.</summary>
    private ScreenInfo? _sceneScreen;

    /// <summary>Okno przeniesiono na ekran sceny: położenie sprzed niej wraca przy wyjściu.</summary>
    private bool _movedForScene;

    private PixelPoint _positionBeforeScene;

    /// <summary>Most do ustawień sceny, które zależą od ekranów.</summary>
    internal SceneSettingsHost SceneHost() => new()
    {
        // Przy otwartej scenie margines należy do ekranu, na którym ona stoi; po przesunięciu numerów
        // jednakowych monitorów zapisany wybór może już wskazywać inny monitor.
        ScreenKey = () => Scene.IsOpen && _sceneScreen is { } shown ? shown.Key : SceneScreen()?.Key,
        Screens = ListScreens,
        FitEdges = FitScreenEdges,
    };

    /// <summary>
    /// Zamyka okno Ustawienia i pokazuje scenę z ramką na granicy marginesu; strzałki zmieniają margines,
    /// Enter i Esc kończą tryb ramki, a scena zostaje otwarta.
    /// </summary>
    internal void FitScreenEdges()
    {
        _settingsWindow?.Close();
        if (!Scene.IsOpen) EnterScene();
        Scene.BeginEdgeFit();
    }

    private void CloseScene(bool restoreState, ScreenInfo? home = null)
    {
        if (!Scene.IsOpen) return;

        try
        {
            Scene.Close();
            UpdatePeekSettings();
            ShowWindowContent(true);
            SuspendWindowContent(false);
            if (restoreState && WindowState == WindowState.FullScreen) LeaveSceneState(home);
            else if (_movedForScene)
            {
                // Stan zmienił system: okno wraca na swoje miejsce, o ile da się je przesunąć.
                _movedForScene = false;
                if (WindowState == WindowState.Normal) Position = _positionBeforeScene;
            }

            RestoreFocusAfterScene();
            if (_soundWindow is { IsVisible: false } sound) sound.Show(this);
        }
        finally
        {
            UpdateWakeLock();
        }
    }

    private void SetStateForScene(WindowState state)
    {
        _changingSceneState = true;
        try { WindowState = state; }
        finally { _changingSceneState = false; }
    }

    private void ShowWindowContent(bool show)
    {
        Backdrop.IsVisible = show;
        Scrim.IsVisible = show;
        WindowContent.IsVisible = show;
    }

    /// <summary>Ostatni fokus w oknie poza sceną i poza przyciskiem „Pełny ekran” klikniętym myszą.</summary>
    private IInputElement? _lastFocus;
    private bool _lastFocusVisible;

    private IInputElement? _focusBeforeScene;
    private bool _focusBeforeSceneVisible;

    private void RememberFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not IInputElement element
            || e.Source is Visual visual && (ReferenceEquals(visual, Scene) || Scene.IsVisualAncestorOf(visual)))
            return;

        // Przycisk kliknięty myszą bierze fokus po drodze; wrócić trzeba tam, gdzie był wcześniej.
        if (ReferenceEquals(element, FullScreenButton) && e.NavigationMethod == NavigationMethod.Pointer) return;

        _lastFocus = element;
        _lastFocusVisible = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
    }

    private void RememberFocusBeforeScene()
    {
        var focused = FocusManager?.GetFocusedElement();
        var visible = focused is StyledElement styled && styled.Classes.Contains(":focus-visible");

        // „Pełny ekran” z fokusem bez pierścienia dostał go od myszy: Enter po wyjściu otwierałby
        // scenę od nowa, więc fokus wraca do elementu sprzed kliknięcia.
        if (ReferenceEquals(focused, FullScreenButton) && !visible)
        {
            focused = _lastFocus;
            visible = _lastFocusVisible;
        }

        _focusBeforeScene = focused;
        _focusBeforeSceneVisible = visible;
    }

    /// <summary>Fokus wraca tam, gdzie był przed sceną, z pierścieniem, jeśli go wtedy miał.</summary>
    private void RestoreFocusAfterScene()
    {
        var target = _focusBeforeScene;
        _focusBeforeScene = null;

        if (target is InputElement { Focusable: true, IsEffectivelyVisible: true, IsEffectivelyEnabled: true } element
            && ReferenceEquals(TopLevel.GetTopLevel(element), this)
            && element.Focus(_focusBeforeSceneVisible ? NavigationMethod.Directional : NavigationMethod.Unspecified))
            return;

        // Nie ma dokąd wrócić (np. wiersz kolejki zniknął): fokus nie zostaje w ukrytej scenie.
        FocusManager?.Focus(null);
    }

    private void SuspendWindowContent(bool suspend)
    {
        RenderLoop.SetIsSuspended(Backdrop, suspend);
        RenderLoop.SetIsSuspended(WindowContent, suspend);
    }

    private void UpdateWakeLock() =>
        _wakeLock.Update(Scene.IsOpen, _viewModel.IsPlaying, WindowState == WindowState.Minimized,
            _viewModel.KeepScreenAwake);

    private void OnFullScreen(object? sender, RoutedEventArgs e) => EnterScene();

    private void OnDiscDoubleTapped(object? sender, TappedEventArgs e)
    {
        EnterScene();
        e.Handled = true;
    }

    // ================= Układ =================

    private void ApplyResponsiveLayout()
    {
        // Before the first layout pass the width is still zero; the design size is the right
        // assumption then, and the next size change corrects it if needed.
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;

        var step = LayoutSteps[0];
        foreach (var candidate in LayoutSteps)
            if (width >= candidate.MinWidth) step = candidate;

        var compact = width < LayoutSteps[1].MinWidth;

        var queueWidth = ApplyQueueMode(width, step.Queue);

        // Podgląd i nakładka leżą nad treścią, więc miejsce zabiera tylko przypięta kolumna.
        var contentWidth = width - (_queueAsColumn ? queueWidth : 0);

        var body = height - (TitleBar.Bounds.Height > 0 ? TitleBar.Bounds.Height : 47);
        var info = TrackInfo.DesiredSize.Height > 0 ? TrackInfo.DesiredSize.Height : 300;

        var centreHeight = CentreHost.Bounds.Height > 0 ? CentreHost.Bounds.Height : body;

        // Odstęp od góry i dołu rośnie z wolnym miejscem. W ciasnym oknie każdy piksel oddechu
        // odbiera miejsce treści, a przy dużej wysokości ten sam odstęp wyglądałby na skąpy.
        var room = Math.Clamp((centreHeight - info - 22) / 240, 0, 1);
        var top = 14 + room * (compact ? 12 : 24);
        var bottom = 8 + room * (compact ? 6 : 10);

        var padding = compact ? 28 : Math.Min(72, 40 + (width - 1180) * 0.05);
        var spacing = compact ? 32 : Math.Min(72, 48 + (width - 1180) * 0.03);

        // Płyta rośnie z szerokością okna, ale nie może przerosnąć wolnej wysokości ani zabrać
        // opisowi utworu miejsca na rząd transportu. Za mała znika całkiem.
        var disc = Math.Min(step.Disc, Math.Min(
            centreHeight - top - bottom,
            contentWidth - 2 * padding - spacing - TrackInfoMinWidth));
        var showDisc = disc >= SmallestDisc;

        DiscHost.IsVisible = showDisc;
        DiscHost.Width = DiscHost.Height = Math.Max(SmallestDisc, disc);

        NowPlaying.Margin = new Thickness(padding, top, padding, bottom);
        NowPlaying.ColumnSpacing = showDisc ? spacing : 0;

        // Szeroki blok jest środkowany przez granicę szerokości, a nie przez wyrównanie: przy
        // wyrównaniu do środka szerokość paska postępu zależałaby od długości tytułu.
        NowPlaying.MaxWidth = (showDisc ? DiscHost.Width + spacing : 0) + TrackInfo.MaxWidth;

        // Niskie okno: mniejszy tytuł, żeby rząd transportu nie wypadł poza blok.
        var titleSize = centreHeight < 330 ? Math.Min(step.Title, 30) : step.Title;
        TrackTitle.FontSize = titleSize;
        TrackArtist.FontSize = Math.Round(titleSize * 0.40, 1);
        TrackQuality.FontSize = QualityFontSize(TrackArtist.FontSize);

        // Pasek głośności jako jedyny w rzędzie transportu ma swobodną szerokość, więc ustępuje
        // pierwszy, gdy rząd się nie mieści.
        VolumeSlider.Width = step.Volume;
    }

    /// <summary>Ctrl+F w radiu: przypina panel stacji (także w nakładce) i ustawia fokus w polu wyszukiwania.</summary>
    private void FocusRadioSearch()
    {
        if (!_viewModel.QueuePinned) _viewModel.QueuePinned = true;
        Dispatcher.UIThread.Post(() =>
        {
            RadioSearch.Focus();
            RadioSearch.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    // ================= Obrot plyty i tla =================

    /// <summary>Pętla obrotu działa tylko wtedy, gdy płyta się kręci albo dojeżdża do pionu.</summary>
    private void UpdateRotationLoop() =>
        _spinner.Update(_viewModel.IsPlaying, _viewModel.ReducedEffects, _viewModel.DiscSpinFactor);

    /// <summary>Mnożnik obrotu płyty w oknie, dla testów.</summary>
    internal double DiscSpeed => _spinner.Speed;

    internal bool IsRotating => _spinner.IsDrawing;

    internal DiscSpinner Spinner => _spinner;

    /// <summary>
    /// Ustawia wszystko, co się rusza, w powtarzalnym stanie dla narzędzia zrzutów: płytę
    /// prosto, plamy tła i falę na początku cyklu. Wszystkie idą według zegara.
    /// </summary>
    public void FreezeAnimationsForCapture()
    {
        _spinner.Freeze();
        Backdrop?.FreezeForCapture();
        Wave?.FreezeForCapture();
        LiveDot?.FreezeForCapture();
        Scene?.FreezeForCapture();
    }

    // ================= Poswiata pod kursorem =================

    private void OnDiscPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Glow is null || !DiscHost.IsVisible) return;
        if (_viewModel.ReducedEffects) { Glow.Intensity = 0; return; }

        var position = e.GetPosition(Glow);
        var radius = Math.Min(Glow.Bounds.Width, Glow.Bounds.Height) / 2;
        var centre = new Point(Glow.Bounds.Width / 2, Glow.Bounds.Height / 2);

        var distance = Math.Sqrt(
            Math.Pow(position.X - centre.X, 2) + Math.Pow(position.Y - centre.Y, 2));

        // Full strength over the record, fading out as the pointer moves away across the block.
        var proximity = Math.Clamp(1 - Math.Max(0, distance - radius) / 340, 0, 1);

        Glow.Centre = position;
        Glow.Intensity = 0.25 + 0.75 * proximity;
    }

    private void OnDiscPointerExited(object? sender, PointerEventArgs e)
    {
        if (Glow is not null) Glow.Intensity = 0;
    }

    // ================= Kolejka =================

    private void AttachQueueScrollBar()
    {
        if (QueueList is null || QueueScrollBar is null) return;

        // The list owns a ScrollViewer inside its template; the thin indicator drives that
        // one rather than wrapping the list, which would defeat virtualisation. The template
        // is not necessarily applied yet when the window opens, so retry until it is.
        if (TryBindScrollBar()) return;

        void Retry(object? sender, EventArgs e)
        {
            if (!TryBindScrollBar()) return;
            LayoutUpdated -= Retry;
        }

        LayoutUpdated += Retry;
    }

    /// <summary>Liczba cyfr numerów, dla której policzono szerokość kolumny numerów.</summary>
    private int _queueNumberDigits;

    /// <summary>
    /// Kolumna numerów ma szerokość najdłuższego numeru w kolejce: tyle cyfr, ile ma jej długość.
    /// Liczona tylko przy zmianie liczby cyfr, cyframi równej szerokości, z zapasem 4 px.
    /// </summary>
    private void UpdateQueueNumberWidth()
    {
        var digits = Math.Max(1, _viewModel.Queue.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        if (digits == _queueNumberDigits) return;

        _queueNumberDigits = digits;
        QueueList.Resources["QueueNumberWidth"] = QueueNumberWidth(digits);
    }

    /// <summary>Szerokość kolumny numerów dla numeru o danej liczbie cyfr; co najmniej 16 px.</summary>
    internal double QueueNumberWidth(int digits)
    {
        var probe = new TextBlock
        {
            Text = new string('8', digits),
            FontSize = 10.5,
            FontFeatures = [Avalonia.Media.FontFeature.Parse("tnum")],
        };
        if (this.TryFindResource("UiFont", out var font) && font is Avalonia.Media.FontFamily family) probe.FontFamily = family;

        probe.Measure(Size.Infinity);
        return Math.Max(16, Math.Ceiling(probe.DesiredSize.Width) + 4);
    }

    private bool TryBindScrollBar()
    {
        if (QueueList is null || QueueScrollBar is null) return true;

        QueueList.ApplyTemplate();
        var viewer = QueueList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null) return false;

        QueueScrollBar.Target = viewer;
        return true;
    }


    // ================= Przyciski =================

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => _viewModel.TogglePlay();

    // Przewinięcie następuje po puszczeniu przycisku; w trakcie przeciągania za wskaźnikiem
    // idą tylko suwak i czas.
    private void OnSeekScrubbing(object? sender, double value) => _viewModel.BeginScrub();

    private void OnSeekCommitted(object? sender, double value) => _viewModel.CommitScrub(value);

    private void OnNext(object? sender, RoutedEventArgs e) => _viewModel.Next();

    private void OnPrevious(object? sender, RoutedEventArgs e) => _viewModel.Previous();

    /// <summary>
    /// Przewija listę do granego wiersza bez ruszania zaznaczenia. Nie wtedy, gdy listę
    /// przegląda się klawiaturą: tej nie przesuwa się spod ręki.
    /// </summary>
    private void RevealCurrent()
    {
        if (QueueList is null || !QueueColumn.IsVisible) return;
        if (_queueBrowsedByKeyboard && QueueList.IsKeyboardFocusWithin) return;

        if (_viewModel.Queue.FirstOrDefault(item => item.IsCurrent) is { } current) QueueList.ScrollIntoView(current);
    }

    private void OnToggleRepeat(object? sender, RoutedEventArgs e) => _viewModel.CycleRepeat();

    private void OnToggleMute(object? sender, RoutedEventArgs e) => _viewModel.ToggleMute();

    private void OnRetryAudio(object? sender, RoutedEventArgs e) => _viewModel.RetryAudio();

    private void OnChooseDevice(object? sender, RoutedEventArgs e) => _viewModel.RequestDeviceSelection();

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        var mode = App.Theme.Cycle();
        App.Settings.Current.Theme = mode;
        App.Settings.Touch();
    }

    private SettingsWindow? _settingsWindow;

    internal SettingsWindow? OpenSettingsWindow => _settingsWindow;

    /// <summary>
    /// Otwiera ustawienia albo wysuwa otwarte. Okno nie jest modalne, bo część ustawień ocenia się
    /// słuchem; za pierwszym razem staje na środku odtwarzacza, potem w zapamiętanym miejscu.
    /// </summary>
    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <param name="section">Sekcja do pokazania, np. Dźwięk po poleceniu „Wybierz urządzenie”.</param>
    internal void OpenSettings(Func<SettingsViewModel, SettingsSection>? section = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(new SettingsViewModel(App.Settings, _viewModel, SceneHost()))
            {
                PlaybackShortcuts = HandlePlaybackShortcut,
            };

            _settingsWindow.PlaceOver(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show(this);
        }
        else
        {
            _settingsWindow.Activate();
        }

        if (section is not null) _settingsWindow.ShowSection(section);
    }

    private SoundWindow? _soundWindow;

    /// <summary>Okno „Korektor i efekty”, gdy jest otwarte; dla testów.</summary>
    internal SoundWindow? OpenSoundWindow => _soundWindow;

    /// <summary>
    /// Otwiera albo zamyka okno korektora według <see cref="MainViewModel.PanelOpen"/> (przycisk w pasku
    /// tytułu i klawisz Q). Okno nie jest modalne, bo korektor ocenia się słuchem; za pierwszym razem
    /// staje na środku odtwarzacza, a podczas sceny otwiera się dopiero po jej zamknięciu.
    /// </summary>
    private void ApplySoundWindow()
    {
        if (_viewModel.PanelOpen)
        {
            if (_soundWindow is not null)
            {
                if (_soundWindow.IsVisible) _soundWindow.Activate();
                return;
            }

            _soundWindow = new SoundWindow(_viewModel) { PlaybackShortcuts = HandlePlaybackShortcut };
            _soundWindow.PlaceOver(this);
            _soundWindow.Closed += (_, _) => _soundWindow = null;
            if (!Scene.IsOpen) _soundWindow.Show(this);
            return;
        }

        _soundWindow?.Close();
    }

    // ================= Dodawanie plików =================

    private async void OnOpenFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Current["OpenFilesTitle"],
                AllowMultiple = true,
                FileTypeFilter = [BuildAudioFilter()],
            });

            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrEmpty(path))
                .Cast<string>()
                .ToList();

            if (paths.Count > 0) _ = _viewModel.AddPaths(paths);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] okno wyboru plików: {ex.Message}");
        }
    }

    private void OnOpenReleases(object? sender, RoutedEventArgs e) =>
        WebLink.Open(_viewModel.UpdateNoticeUrl);

    // ================= Listy odtwarzania =================

    /// <summary>
    /// Wczytuje listę M3U na miejsce obecnej kolejki.
    /// <para>
    /// O brakujących plikach z listy mówi komunikat, żeby krótsza kolejka nie była zaskoczeniem.
    /// </para>
    /// </summary>
    private async void OnLoadPlaylist(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Current["PlaylistLoadTitle"],
                AllowMultiple = false,
                FileTypeFilter = [BuildPlaylistFilter()],
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // Okno zamknięte w trakcie sprawdzania listy: nie ma już czego zgłaszać.
            if (await _viewModel.LoadPlaylist(path) is not { } load) return;

            if (load.Paths.Count == 0)
                _viewModel.ShowNotice(Strings.Current["PlaylistEmpty"]);
            else if (load.Missing > 0)
                _viewModel.ShowNotice(string.Format(Strings.Current["PlaylistMissing"], load.Missing));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] wczytanie listy: {ex.Message}");
            _viewModel.ShowNotice(Strings.Current["PlaylistLoadFailed"]);
        }
    }

    private async void OnSavePlaylist(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Current["PlaylistSaveTitle"],
                SuggestedFileName = Strings.Current["PlaylistDefaultName"],
                DefaultExtension = "m3u8",
                FileTypeChoices = [BuildPlaylistFilter()],
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            _viewModel.SavePlaylist(path);
            _viewModel.ShowNotice(string.Format(
                Strings.Current["PlaylistSaved"], Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] zapis listy: {ex.Message}");
            _viewModel.ShowNotice(Strings.Current["PlaylistSaveFailed"]);
        }
    }

    private static FilePickerFileType BuildPlaylistFilter() => new(Strings.Current["PlaylistFilter"])
    {
        Patterns = PlaylistFile.Extensions.Select(extension => "*" + extension).ToArray(),
    };

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.Current["OpenFolderTitle"],
                AllowMultiple = false,
            });

            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _ = _viewModel.AddPaths([path]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] okno wyboru folderu: {ex.Message}");
        }
    }

    private static FilePickerFileType BuildAudioFilter() => new(Strings.Current["AudioFilter"])
    {
        Patterns = AudioFileFormatDetector.SupportedExtensions.Select(ext => "*" + ext).ToArray(),
    };

    // ================= Przeciąganie na okno =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Przyjmujemy wyłącznie pliki; każdy inny rodzaj danych ma zostać odrzucony,
        // żeby kursor nie sugerował, że da się je upuścić.
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        NoteFileDrag(e);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        EndFileDrag();

        var items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0) return;

        var paths = items
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToList();

        if (paths.Count == 0) return;

        _viewModel.OpenFromOutside(paths);
    }

    // ================= Wywolania z innych kopii i z klawiatury multimedialnej =================

    /// <summary>
    /// Dokłada ścieżki przekazane przez inną kopię programu. Przy zamykaniu okna
    /// (<paramref name="closing"/>) foldery przegląda się od razu, przed zapisem kolejki.
    /// </summary>
    public void ReceivePaths(string[] paths, bool closing = false)
    {
        if (paths.Length == 0) return;
        _viewModel.OpenFromOutside(paths, now: closing);
    }

    /// <summary>
    /// Obsługuje klawisz multimedialny. „Stop” wstrzymuje, żeby zachować pozycję w utworze.
    /// </summary>
    public void HandleMediaKey(MediaKey key)
    {
        switch (key)
        {
            case MediaKey.PlayPause: _viewModel.TogglePlay(); break;
            case MediaKey.Next: _viewModel.Next(); break;
            case MediaKey.Previous: _viewModel.Previous(); break;
            case MediaKey.Stop: _viewModel.Pause(); break;
        }
    }

    /// <summary>
    /// Obsługuje polecenie z panelu multimediów. „Play” i „Pause” są osobne, bo przełącznik
    /// zacząłby grać na prośbę o pauzę, gdy nic nie gra.
    /// </summary>
    public void HandleMediaCommand(MediaCommand command)
    {
        switch (command)
        {
            case MediaCommand.Play: _viewModel.Play(); break;
            case MediaCommand.Pause: _viewModel.Pause(); break;
            case MediaCommand.PlayPause: _viewModel.TogglePlay(); break;
            case MediaCommand.Stop: _viewModel.Pause(); break;
            case MediaCommand.Next: _viewModel.Next(); break;
            case MediaCommand.Previous: _viewModel.Previous(); break;
        }
    }

    // ================= Operacje na kolejce =================

    /// <summary>
    /// Mysz i klawiatura listy kolejki. Naciśnięcie wiersza obsługuje sam wiersz (zaznaczenie),
    /// więc początek przeciągania łapie faza tunelowania, a resztę także zdarzenia już obsłużone.
    /// </summary>
    private void AttachQueueInput()
    {
        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);
        QueueList.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.NavigationMethod == NavigationMethod.Tab) _queueBrowsedByKeyboard = true;
        });
        QueueList.AddHandler(ContextRequestedEvent, OnQueueContextRequested, RoutingStrategies.Tunnel);
        QueueList.DoubleTapped += OnQueueDoubleTapped;
        QueueMenu.Opening += (_, _) => UpdateQueueMenu();
    }

    /// <summary>Lista dostała fokus klawiszem Tab albo zaznaczenie przesuwano strzałkami.</summary>
    private bool _queueBrowsedByKeyboard;

    private QueueItemViewModel? SelectedRow => QueueList.SelectedItem as QueueItemViewModel;

    /// <summary>Wiersz pod wskazanym elementem albo nic, gdy to puste miejsce listy.</summary>
    private static QueueItemViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as QueueItemViewModel;

    private void OnQueueDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source) is not { } row) return;

        _viewModel.PlayItem(row);
        e.Handled = true;
    }

    /// <summary>
    /// Enter, Delete i Alt+strzałki działają tylko na liście z fokusem. Spacja zostaje przy
    /// odtwarzaniu także tutaj, zamiast zaznaczać wiersz, który i tak zaznaczają strzałki.
    /// </summary>
    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        var none = e.KeyModifiers == KeyModifiers.None;
        var alt = e.KeyModifiers == KeyModifiers.Alt;

        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            _queueBrowsedByKeyboard = true;

        if (e.Key == Key.Space && none)
        {
            _viewModel.TogglePlay();
            e.Handled = true;
            return;
        }

        if (SelectedRow is not { } row) return;

        switch (e.Key)
        {
            case Key.Enter when none: _viewModel.PlayItem(row); break;
            case Key.Delete when none: RemoveSelected(); break;
            case Key.Up when alt: MoveSelected(-1); break;
            case Key.Down when alt: MoveSelected(1); break;
            default: return;
        }

        e.Handled = true;
    }

    /// <summary>Menu kontekstowe działa na wierszu pod kursorem, a nie na wcześniej zaznaczonym.</summary>
    private void OnQueueContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(QueueList, out _)) return;

        QueueList.SelectedItem = RowAt(e.Source);
    }

    private void UpdateQueueMenu()
    {
        var index = SelectedRow is { } row ? _viewModel.Queue.IndexOf(row) : -1;
        var s = Strings.Current;

        SetCommand(QueuePlayItem, s["QueuePlay"], KeyNames.Enter);
        SetCommand(QueueRemoveItem, s["QueueRemove"], KeyNames.Delete);
        SetCommand(QueueMoveUpItem, s["QueueMoveUp"], KeyNames.Chord(KeyNames.Alt, "↑"));
        SetCommand(QueueMoveDownItem, s["QueueMoveDown"], KeyNames.Chord(KeyNames.Alt, "↓"));
        SetCommand(QueueUndoItem, _viewModel.UndoQueueLabel, KeyNames.Chord(KeyNames.Ctrl, "Z"));

        QueuePlayItem.IsEnabled = index >= 0;
        QueueRemoveItem.IsEnabled = index >= 0;
        QueueMoveUpItem.IsEnabled = index > 0;
        QueueMoveDownItem.IsEnabled = index >= 0 && index < _viewModel.Queue.Count - 1;
    }

    private static void SetCommand(MenuItem item, string text, string gesture)
    {
        item.Header = new MenuCommandText(text, gesture);
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetAcceleratorKey(item, gesture);
    }

    // ================= Radio: lista stacji =================

    private void AttachRadioInput()
    {
        RadioList.AddHandler(KeyDownEvent, OnRadioKeyDown, RoutingStrategies.Tunnel);
        RadioList.AddHandler(ContextRequestedEvent, OnRadioContextRequested, RoutingStrategies.Tunnel);
        RadioList.DoubleTapped += OnRadioDoubleTapped;
        RadioMenu.Opening += (_, _) => UpdateRadioMenu();
    }

    private StationRowViewModel? RadioRow => RadioList.SelectedItem as StationRowViewModel;

    private static StationRowViewModel? RadioRowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as StationRowViewModel;

    private void OnSourceFiles(object? sender, RoutedEventArgs e) => _viewModel.SetSource(SourceMode.Files);

    private void OnSourceRadio(object? sender, RoutedEventArgs e) => _viewModel.SetSource(SourceMode.Radio);

    private void OnRadioRetry(object? sender, RoutedEventArgs e) => _viewModel.Radio.Retry();

    private void OnRadioAddToggle(object? sender, RoutedEventArgs e) => _viewModel.Radio.IsAdding = !_viewModel.Radio.IsAdding;

    private void OnRadioAddConfirm(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.Radio.AddCustom(RadioAddName.Text ?? string.Empty, RadioAddUrl.Text ?? string.Empty)) return;

        RadioAddName.Text = string.Empty;
        RadioAddUrl.Text = string.Empty;
    }

    // ---------- Menu w pasku tytułu ----------

    private static readonly TimeSpan MenuSlide = TimeSpan.FromMilliseconds(160);

    private bool _menuOpen;
    private Transitions? _menuTransitions;

    /// <summary>Menu w pasku tytułu jest rozsunięte.</summary>
    public bool IsMenuOpen => _menuOpen;

    private void OnMenuButton(object? sender, RoutedEventArgs e) => SetMenuOpen(!_menuOpen);

    /// <summary>
    /// Rozsuwa albo zwija ikony menu. Z ograniczonymi efektami zmiana następuje od razu, bez animacji.
    /// Zwinięte ikony są wyłączone, żeby nie brały fokusu ani kliknięć.
    /// </summary>
    public void SetMenuOpen(bool open)
    {
        if (_menuOpen == open && MenuTray.Opacity == (open ? 1 : 0)) return;

        _menuOpen = open;
        MenuButton.IsChecked = open;

        _menuTransitions ??=
        [
            new DoubleTransition { Property = WidthProperty, Duration = MenuSlide, Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            new DoubleTransition { Property = OpacityProperty, Duration = MenuSlide },
        ];
        MenuTray.Transitions = _viewModel.ReducedEffects ? null : _menuTransitions;

        if (open)
        {
            MenuTrayItems.IsEnabled = true;
            MenuTrayItems.Measure(Size.Infinity);
            MenuTray.Width = MenuTrayItems.DesiredSize.Width;
            MenuTray.Opacity = 1;
            MenuTray.IsHitTestVisible = true;
        }
        else
        {
            MenuTrayItems.IsEnabled = false;
            MenuTray.Width = 0;
            MenuTray.Opacity = 0;
            MenuTray.IsHitTestVisible = false;
        }

        UpdateMenuMark();
    }

    private void UpdateMenuMark() => MenuModeMark.IsVisible = _viewModel.IsRadioMode && !_menuOpen && _viewModel.RadioAvailable;

    /// <summary>Kliknięcie poza ikonami menu i jego przyciskiem zwija je jak zwykłe menu.</summary>
    private void OnMenuOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_menuOpen) return;

        if (e.Source is Visual source && (MenuTray.IsVisualAncestorOf(source) || MenuButton.IsVisualAncestorOf(source)
                                          || ReferenceEquals(source, MenuButton))) return;

        SetMenuOpen(false);
    }

    /// <summary>Użycie którejkolwiek ikony menu zwija je.</summary>
    private void OnMenuTrayClick(object? sender, RoutedEventArgs e) => SetMenuOpen(false);

    private void OnRadioPrivacyDismiss(object? sender, RoutedEventArgs e) => _viewModel.Radio.AcknowledgePrivacy();

    /// <summary>Wiersz listy trafił na ekran (lista jest wirtualizowana): dopiero wtedy prosi o logo.</summary>
    private void OnRadioRowAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: StationRowViewModel row }) row.RequestLogo();
    }

    private void OnRadioHeart(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StationRowViewModel row }) _viewModel.Radio.ToggleFavourite(row.Station);
        e.Handled = true;
    }

    private void OnRadioDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RadioRowAt(e.Source) is not { } row) return;

        PlayRadioRow(row);
        e.Handled = true;
    }

    private void PlayRadioRow(StationRowViewModel row)
    {
        if (_viewModel.Radio.Play(row.Station)) _viewModel.SyncPlayingState();
    }

    private void OnRadioKeyDown(object? sender, KeyEventArgs e)
    {
        var none = e.KeyModifiers == KeyModifiers.None;
        var alt = e.KeyModifiers == KeyModifiers.Alt;

        if (e.Key == Key.Space && none)
        {
            _viewModel.TogglePlay();
            e.Handled = true;
            return;
        }

        if (RadioRow is not { } row) return;

        switch (e.Key)
        {
            case Key.Enter when none: PlayRadioRow(row); break;
            case Key.Up when alt: _viewModel.Radio.MoveFavourite(row.Station, -1); break;
            case Key.Down when alt: _viewModel.Radio.MoveFavourite(row.Station, 1); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnRadioContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(RadioList, out _)) return;

        RadioList.SelectedItem = RadioRowAt(e.Source);
    }

    private void UpdateRadioMenu()
    {
        var s = Strings.Current;
        var row = RadioRow;
        var favourites = _viewModel.Radio.View == Radio.RadioListContext.ViewFavourites;

        SetCommand(RadioPlayItem, s["QueuePlay"], KeyNames.Enter);
        SetCommand(RadioFavouriteItem, s[row is { IsFavourite: true } ? "TooltipFavouriteRemove" : "TooltipFavouriteAdd"], string.Empty);
        SetCommand(RadioWebsiteItem, s["RadioOpenWebsite"], string.Empty);
        SetCommand(RadioMoveUpItem, s["QueueMoveUp"], KeyNames.Chord(KeyNames.Alt, "↑"));
        SetCommand(RadioMoveDownItem, s["QueueMoveDown"], KeyNames.Chord(KeyNames.Alt, "↓"));

        RadioPlayItem.IsEnabled = row is not null;
        RadioFavouriteItem.IsEnabled = row is not null;
        RadioWebsiteItem.IsVisible = row is not null && RadioViewModel.WebsiteUrl(row.Station) is not null;
        RadioMoveUpItem.IsVisible = favourites;
        RadioMoveDownItem.IsVisible = favourites;
    }

    private void OnRadioMenuPlay(object? sender, RoutedEventArgs e)
    {
        if (RadioRow is { } row) PlayRadioRow(row);
    }

    private void OnRadioMenuFavourite(object? sender, RoutedEventArgs e)
    {
        if (RadioRow is { } row) _viewModel.Radio.ToggleFavourite(row.Station);
    }

    private void OnRadioMenuWebsite(object? sender, RoutedEventArgs e)
    {
        if (RadioRow is { } row) _viewModel.Radio.OpenWebsite(row.Station, url => WebLink.Open(url));
    }

    private void OnRadioMenuMoveUp(object? sender, RoutedEventArgs e)
    {
        if (RadioRow is { } row) _viewModel.Radio.MoveFavourite(row.Station, -1);
    }

    private void OnRadioMenuMoveDown(object? sender, RoutedEventArgs e)
    {
        if (RadioRow is { } row) _viewModel.Radio.MoveFavourite(row.Station, 1);
    }

    private static FilePickerFileType BuildStationListFilter() => new(Strings.Current["RadioListFilter"])
    {
        Patterns = ["*.m3u", "*.m3u8", "*.pls"],
    };

    /// <summary>Dopisuje do ulubionych stacje z pliku M3U albo PLS.</summary>
    private async void OnRadioImport(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Current["RadioImportTitle"],
                AllowMultiple = false,
                FileTypeFilter = [BuildStationListFilter()],
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // Plik większy niż rozsądna lista stacji nie jest listą stacji.
            if (new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                _viewModel.ShowNotice(Strings.Current["RadioImportFailed"]);
                return;
            }

            _viewModel.Radio.Import(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] import stacji: {ex.Message}");
            _viewModel.ShowNotice(Strings.Current["RadioImportFailed"]);
        }
    }

    /// <summary>Zapisuje ulubione jako rozszerzone M3U.</summary>
    private async void OnRadioExport(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Current["RadioExportTitle"],
                SuggestedFileName = Strings.Current["RadioExportDefaultName"],
                DefaultExtension = "m3u8",
                FileTypeChoices = [BuildStationListFilter()],
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            await File.WriteAllTextAsync(path, _viewModel.Radio.ExportText(), new System.Text.UTF8Encoding(false));
            _viewModel.ShowNotice(Strings.Current["RadioExported"]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] eksport stacji: {ex.Message}");
            _viewModel.ShowNotice(Strings.Current["RadioExportFailed"]);
        }
    }

    private void OnQueuePlay(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row) _viewModel.PlayItem(row);
    }

    private void OnQueueRemove(object? sender, RoutedEventArgs e) => RemoveSelected();

    private void OnQueueClear(object? sender, RoutedEventArgs e) => _viewModel.ClearQueue();

    private void OnQueueUndo(object? sender, RoutedEventArgs e) => _viewModel.UndoQueue();

    private void OnQueueMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnQueueMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(1);

    /// <summary>Usuwa zaznaczony wiersz; zaznaczenie przechodzi na wiersz, który zajął jego miejsce.</summary>
    private void RemoveSelected()
    {
        if (SelectedRow is not { } row) return;

        var index = _viewModel.Queue.IndexOf(row);
        var focused = QueueList.IsKeyboardFocusWithin;

        _viewModel.RemoveFromQueue(row);
        SelectRow(Math.Min(index, _viewModel.Queue.Count - 1), focused);
    }

    private void MoveSelected(int delta)
    {
        if (SelectedRow is not { } row) return;

        var from = _viewModel.Queue.IndexOf(row);
        var to = from + delta;
        if (to < 0 || to >= _viewModel.Queue.Count) return;

        var focused = QueueList.IsKeyboardFocusWithin;
        _viewModel.MoveInQueue(from, to);
        SelectRow(to, focused);
    }

    /// <summary>Zaznacza wiersz i, jeśli lista miała fokus, przenosi go na ten wiersz.</summary>
    private void SelectRow(int index, bool focus)
    {
        if (index < 0)
        {
            QueueList.SelectedItem = null;
            if (focus) QueueList.Focus(NavigationMethod.Directional);
            return;
        }

        QueueList.SelectedIndex = index;
        QueueList.ScrollIntoView(index);
        if (!focus) return;

        QueueList.UpdateLayout();
        if (QueueList.ContainerFromIndex(index) is { } container) container.Focus(NavigationMethod.Directional);
    }

    // ---------- Zmiana kolejności przeciąganiem ----------

    /// <summary>Distance the pointer must travel before a press counts as a drag.</summary>
    private const double DragThreshold = 6;

    private int _dragSourceIndex = -1;
    private Point _dragOrigin;
    private bool _dragging;

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _queueBrowsedByKeyboard = false;
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;

        _dragOrigin = e.GetPosition(QueueList);
        _dragSourceIndex = IndexAt(_dragOrigin);
        _dragging = false;
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSourceIndex < 0) return;
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;

        if (!_dragging &&
            Math.Abs(e.GetPosition(QueueList).Y - _dragOrigin.Y) > DragThreshold)
        {
            _dragging = true;
            QueueList.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }
    }

    /// <summary>Przerywa przeciąganie w kolejce bez przenoszenia utworu, np. gdy kolejkę zakrywa scena.</summary>
    private void CancelQueueDrag()
    {
        QueueList.Cursor = Cursor.Default;
        _dragSourceIndex = -1;
        _dragging = false;
    }

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Gest zaczyna się lewym przyciskiem i tylko jego puszczenie go kończy.
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        QueueList.Cursor = Cursor.Default;

        var source = _dragSourceIndex;
        var dragged = _dragging;

        _dragSourceIndex = -1;
        _dragging = false;

        if (!dragged || source < 0) return;

        var target = DropIndexAt(e.GetPosition(QueueList));

        if (target >= 0 && target != source)
        {
            _viewModel.MoveInQueue(source, target);

            // Przeciągnięty wiersz zostaje zaznaczony, żeby nie zgubić go z oczu.
            QueueList.SelectedIndex = target;
        }

        e.Handled = true;
    }

    /// <summary>Wiersz kolejki pod punktem albo −1, gdy punkt nie trafia w żaden wiersz.</summary>
    private int IndexAt(Point position)
    {
        for (var i = 0; i < _viewModel.Queue.Count; i++)
        {
            if (RowSpan(i) is not (var top, var bottom)) continue;
            if (position.Y >= top && position.Y <= bottom) return i;
        }

        return -1;
    }

    /// <summary>
    /// Miejsce upuszczenia przeciąganego wiersza: najbliższy wiersz, także w szczelinie między
    /// wierszami i pod ostatnim. Poza listą −1, wtedy przeciąganie niczego nie przenosi.
    /// </summary>
    private int DropIndexAt(Point position)
    {
        if (!new Rect(QueueList.Bounds.Size).Contains(position)) return -1;

        var nearest = -1;
        var best = double.MaxValue;

        for (var i = 0; i < _viewModel.Queue.Count; i++)
        {
            if (RowSpan(i) is not (var top, var bottom)) continue;

            var distance = position.Y < top ? top - position.Y : Math.Max(0, position.Y - bottom);
            if (distance < best) (nearest, best) = (i, distance);
        }

        return nearest;
    }

    /// <summary>Górna i dolna krawędź wiersza w układzie listy; nic dla wiersza bez kontenera.</summary>
    private (double Top, double Bottom)? RowSpan(int index)
    {
        if (QueueList.ContainerFromIndex(index) is not Control container) return null;

        var top = container.TranslatePoint(new Point(0, 0), QueueList)?.Y ?? container.Bounds.Y;
        return (top, top + container.Bounds.Height);
    }

    // ================= Skroty klawiszowe =================

    /// <summary>
    /// Spacja i strzałki w fazie tunelowania, czyli przed przyciskiem, który dostał fokus po
    /// kliknięciu. Wyjątek: lista kolejki, fader i suwak z fokusem, które same używają strzałek,
    /// oraz przełącznik albo przycisk zmieniający stan z fokusem z klawiatury, który Spacją zmienia
    /// stan; klawisz, którego nie zużyją, i tak dochodzi potem do <see cref="OnKeyDown"/>.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _menuOpen)
        {
            SetMenuOpen(false);
            e.Handled = true;
            return;
        }

        // Scena obsługuje klawisze przed skrótami okna; czego nie weźmie, idzie do kontrolki
        // z fokusem albo do skrótów plików i ustawień w OnKeyDown.
        if (Scene.IsOpen)
        {
            if (!e.Handled && Scene.HandleKey(e)) e.Handled = true;
            return;
        }

        if (e.Handled || e.Key is not (Key.Space or Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var focused = FocusManager?.GetFocusedElement();
        if (KeepsArrowKeys(focused) || e.Key == Key.Space && KeepsSpace(focused)) return;

        HandlePlaybackShortcut(e);
    }

    /// <summary>Puszczenie Spacji też nie dociera do przycisku, bo ten klika właśnie przy puszczeniu.</summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (Scene.IsOpen)
        {
            if (e.Key == Key.Space && (e.KeyModifiers != KeyModifiers.None || !Scene.PassesSpaceToFocus)) e.Handled = true;
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !KeepsArrowKeys(focused) && !KeepsSpace(focused))
            e.Handled = true;
    }

    /// <summary>Przełącznik albo przycisk zmieniający stan sam obsługuje Spację, gdy ma fokus z klawiatury.</summary>
    private bool KeepsSpace(IInputElement? focused) =>
        focused is StyledElement element && element.Classes.Contains(":focus-visible")
        && (focused is GlassSwitch or Avalonia.Controls.Primitives.ToggleButton
            || focused is Button button && (button == ThemeButton || button == RepeatModeButton || button == MuteButton));

    private bool KeepsArrowKeys(IInputElement? focused) => focused switch
    {
        EqFader or TextBox => true,
        TrackSlider slider => slider.Focusable,
        Visual visual => ReferenceEquals(visual.FindAncestorOfType<ListBox>(includeSelf: true), QueueList),
        _ => false,
    };

    /// <summary>
    /// Skróty okna. Litery działają tylko bez modyfikatorów, żeby kombinacje systemu i pulpitu
    /// (Ctrl+Q, Alt+Spacja) trafiały tam, dokąd należą.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var modifiers = e.KeyModifiers;
        var none = modifiers == KeyModifiers.None;
        var control = modifiers == KeyModifiers.Control;

        // Pisanie w polu tekstowym (szukanie stacji, adres) nie uruchamia skrótów literowych.
        if ((none || modifiers == KeyModifiers.Shift) && FocusManager?.GetFocusedElement() is TextBox) return;

        // W scenie z okna zostają tylko polecenia otwierające pliki i ustawienia.
        if (Scene.IsOpen && !(e.Key == Key.O && (control || modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                              || e.Key == Key.OemComma && control))
            return;

        // Transport i głośność wspólne z oknem ustawień, z tymi samymi modyfikatorami.
        if (HandlePlaybackShortcut(e)) return;

        switch (e.Key)
        {
            // --- pliki ---
            case Key.O when modifiers == (KeyModifiers.Control | KeyModifiers.Shift): OnOpenFolder(this, new RoutedEventArgs()); break;
            case Key.O when control: OnOpenFiles(this, new RoutedEventArgs()); break;

            // --- odtwarzanie ---
            case Key.S when none: if (_viewModel.IsFilesMode) _viewModel.ShuffleEnabled = !_viewModel.ShuffleEnabled; break;
            case Key.R when none: if (_viewModel.IsFilesMode) _viewModel.CycleRepeat(); break;

            // --- źródło dźwięku i radio ---
            case Key.D1 when control && _viewModel.RadioAvailable: _viewModel.SetSource(SourceMode.Files); break;
            case Key.D2 when control: _viewModel.SetSource(SourceMode.Radio); break;
            case Key.F when control && _viewModel.IsRadioMode: FocusRadioSearch(); break;

            // --- interfejs ---
            case Key.Q when none: _viewModel.TogglePanel(); break;
            case Key.L when none: ToggleQueueByKey(); break;
            case Key.T when none: OnToggleTheme(this, new RoutedEventArgs()); break;
            case Key.F11 when none: EnterScene(); break;
            case Key.OemComma when control: OpenSettings(); break;

            case Key.Escape when none && CloseQueueByKey(): break;

            // --- kolejka ---
            case Key.Z when control && _viewModel.IsRadioMode && _viewModel.Radio.CanUndo: _viewModel.Radio.Undo(); break;
            case Key.Z when control && _viewModel.IsFilesMode && _viewModel.CanUndoQueue: _viewModel.UndoQueue(); break;

            default: return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Skróty odtwarzania i głośności, które przyjmuje także okno ustawień: Spacja, strzałki,
    /// Ctrl+strzałki i M. Skróty interfejsu dotyczą tylko tego okna.
    /// </summary>
    internal bool HandlePlaybackShortcut(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        var none = modifiers == KeyModifiers.None;
        var control = modifiers == KeyModifiers.Control;

        switch (e.Key)
        {
            case Key.Space when none: _viewModel.TogglePlay(); break;
            case Key.Right when control: _viewModel.Next(); break;
            case Key.Left when control: _viewModel.Previous(); break;
            case Key.Right when none: _viewModel.SeekRelative(_viewModel.SeekStep); break;
            case Key.Left when none: _viewModel.SeekRelative(-_viewModel.SeekStep); break;
            case Key.Up when none: _viewModel.AdjustVolume(0.05); break;
            case Key.Down when none: _viewModel.AdjustVolume(-0.05); break;
            case Key.M when none: _viewModel.ToggleMute(); break;
            default: return false;
        }

        e.Handled = true;
        return true;
    }
    // ================= Sterowanie oknem bez ramki systemowej =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || Scene.IsOpen) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);

        if (!_systemFrame && WindowState == WindowState.Normal && TryGetResizeEdge(position) is { } edge)
        {
            BeginResizeDrag(edge, e);
            return;
        }

        if (position.Y > TitleBar.Bounds.Height) return;

        // W Windows przesuwanie i dwuklik obsługuje system, bo pasek jest oznaczony jako pasek tytułu.
        if (_systemFrame) return;

        if (e.ClickCount == 2) ToggleMaximise();
        else BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_systemFrame) return;

        if (WindowState != WindowState.Normal)
        {
            Cursor = Cursor.Default;
            return;
        }

        Cursor = TryGetResizeEdge(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => Cursor.Default,
        };
    }

    /// <summary>Works out which border the pointer is over, for the frame drawn by the program.</summary>
    private WindowEdge? TryGetResizeEdge(Point position)
    {
        var left = position.X <= ResizeMargin;
        var right = position.X >= Bounds.Width - ResizeMargin;
        var top = position.Y <= ResizeMargin;
        var bottom = position.Y >= Bounds.Height - ResizeMargin;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }
}
