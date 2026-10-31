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
    /// <summary>One revolution every nine seconds, as in the design.</summary>
    private const double DegreesPerSecond = 360.0 / 9.0;

    /// <summary>
    /// Czas, w jakim płyta staje okładką prosto po ograniczeniu efektów: dość długi, żeby było
    /// widać zwalnianie, i dość krótki, żeby nikt na to nie czekał.
    /// </summary>
    private const double UprightReturnSeconds = 0.45;

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

    /// <summary>
    /// Najmniejsza szerokość okna, przy której kolejka stoi w kolumnie obok bloku odtwarzania.
    /// Węższe okno pokazuje ją jako panel nakładany nad treść. Wartość z pomiaru narzędziem
    /// zrzutów: od 1040 px blok odtwarzania mieści się obok kolumny z płytą w rozmiarze
    /// najmniejszego stopnia układu (228 px), a poniżej płyta musiałaby się kurczyć.
    /// </summary>
    internal const double QueueColumnMinWidth = 1040;

    /// <summary>Rozmiar projektowy okna przy pierwszym uruchomieniu, przycinany do ekranu.</summary>
    private const double DesignWidth = 1200;
    private const double DesignHeight = 680;

    /// <summary>Szerokość, którą musi mieć opis utworu, żeby rząd transportu nie wychodził poza blok.</summary>
    private const double TrackInfoMinWidth = 440;

    /// <summary>Płyta mniejsza od tego wygląda jak usterka, więc znika, oddając miejsce tekstowi.</summary>
    private const double SmallestDisc = 140;

    /// <summary>Pas dolny nie maleje poniżej tej wysokości; resztę przewija.</summary>
    private const double SmallestPanel = 150;

    private readonly MainViewModel _viewModel;
    private readonly RenderLoop _rotation;
    private TimeSpan _lastRotationFrame;
    private double _angle;
    private bool _animationsFrozen;

    /// <summary>Set while the record is coasting to a stop at the top of its revolution.</summary>
    private bool _returningUpright;
    private double _returnFrom;
    private double _returnElapsed;

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

    /// <summary>Ostatni stan Normal albo Maximized: do niego wraca okno po pełnym ekranie i minimalizacji.</summary>
    private WindowState _lastRegularState = WindowState.Normal;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    /// <summary>Kolejka jako panel nakładany nad treść (wąskie okno) zamiast kolumny.</summary>
    private bool _queueAsOverlay;

    /// <summary>
    /// Kolejkę schowało zwężenie okna, a nie użytkownik. Po poszerzeniu wraca sama, a ustawienia
    /// zachowują ją jako otwartą.
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

        _viewModel = viewModel;
        DataContext = _viewModel;

        _rotation = new RenderLoop(this, OnRotationFrame);

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

        AttachQueueInput();
        _viewModel.DeviceSelectionRequested += () => OpenSettings(settings => settings.Audio);

        PositionChanged += (_, _) => RememberNormalBounds();
        CentreHost.SizeChanged += (_, _) => ApplyResponsiveLayout();
        TrackInfo.SizeChanged += (_, _) => ApplyResponsiveLayout();

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentIndex): RevealCurrent(); break;
                case nameof(MainViewModel.QueueOpen): OnQueueOpenChanged(); break;
                case nameof(MainViewModel.PanelOpen): ApplyResponsiveLayout(); break;
                case nameof(MainViewModel.IsPlaying): UpdateRotationLoop(); break;
                case nameof(MainViewModel.ReducedEffects):
                    BeginUprightReturn();
                    UpdateRotationLoop();
                    break;
            }
        };
    }

    // ================= Cykl zycia =================

    /// <summary>
    /// Panel multimediów pulpitu i klawisze multimedialne. Wyłącza je pomiar okna, żeby okno
    /// postawione na chwilę nie zgłaszało się w panelu multimediów systemu.
    /// </summary>
    internal bool AttachDesktopIntegration { get; init; } = true;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        ApplyResponsiveLayout();
        AttachQueueScrollBar();

        // Ścieżki z wiersza poleceń mają pierwszeństwo przed kolejką z poprzedniej sesji:
        // „Otwórz za pomocą” ma zagrać wskazany plik.
        if (App.StartupPaths.Length > 0) _viewModel.OpenFromOutside(App.StartupPaths);
        else _viewModel.RestoreQueueState();

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
        _rotation.Stop();
        PersistGeometry();
        _viewModel.FinishPendingOpens();
        _viewModel.SaveQueueState();
        App.Settings.SaveNow();
        App.ReleaseSingleInstance();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
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

        ApplyWindowShape();
        RememberNormalBounds();
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
            if (WindowState != WindowState.Normal || !IsVisible) return;

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
    /// Zapisuje geometrię zwykłego okna z ostatniej chwili w stanie Normal. Zamknięcie na pełnym
    /// ekranie zapisuje się jak maksymalizacja, a w zminimalizowanym oknie liczy się stan sprzed.
    /// </summary>
    internal void PersistGeometry()
    {
        var state = WindowState;
        var maximized = state is WindowState.Maximized or WindowState.FullScreen
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

    /// <summary>Pełny ekran wraca do stanu sprzed niego: zmaksymalizowane okno zostaje zmaksymalizowane.</summary>
    internal void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
            return;
        }

        _stateBeforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = WindowState.FullScreen;
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

        ApplyQueueMode(width);

        var queueWidth = _queueAsOverlay ? Math.Min(step.Queue, width - 80) : step.Queue;
        QueueColumn.Width = queueWidth;

        var contentWidth = width - (!_queueAsOverlay && _viewModel.QueueOpen ? queueWidth : 0);

        // Pas dolny dostaje tyle, ile zostaje po bloku odtwarzania; nadmiar przewija.
        var body = height - (TitleBar.Bounds.Height > 0 ? TitleBar.Bounds.Height : 47);
        var info = TrackInfo.DesiredSize.Height > 0 ? TrackInfo.DesiredSize.Height : 300;
        PanelScroll.MaxHeight = Math.Max(SmallestPanel, body - info - 22);

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

        // Pasek głośności jako jedyny w rzędzie transportu ma swobodną szerokość, więc ustępuje
        // pierwszy, gdy rząd się nie mieści.
        VolumeSlider.Width = step.Volume;
    }

    /// <summary>
    /// Kolumna kolejki albo panel nakładany. Przejście w nakładkę chowa kolejkę: panel, który
    /// sam nakrywa treść przy zwężaniu okna, zabierałby ją bez prośby.
    /// </summary>
    private void ApplyQueueMode(double width)
    {
        var overlay = width < QueueColumnMinWidth;

        if (overlay != _queueAsOverlay)
        {
            _queueAsOverlay = overlay;

            if (overlay && _viewModel.QueueOpen)
            {
                _queueCollapsedByWidth = true;
                SetQueueOpen(false);
            }
            else if (!overlay && _queueCollapsedByWidth)
            {
                _queueCollapsedByWidth = false;
                SetQueueOpen(true);
            }

            Grid.SetColumn(QueueColumn, overlay ? 0 : 1);
            Grid.SetColumnSpan(QueueColumn, overlay ? 2 : 1);
            QueueColumn.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            QueueColumn.Classes.Set("queue-overlay", overlay);
        }

        QueueDismiss.IsVisible = overlay && _viewModel.QueueOpen;
    }

    private void SetQueueOpen(bool open)
    {
        _adjustingQueue = true;
        try { _viewModel.SetQueueOpen(open, remember: false); }
        finally { _adjustingQueue = false; }
    }

    private void OnQueueOpenChanged()
    {
        if (!_adjustingQueue) _queueCollapsedByWidth = false;

        QueueDismiss.IsVisible = _queueAsOverlay && _viewModel.QueueOpen;
        ApplyResponsiveLayout();

        // Zamknięta kolejka nie przewija się przy zmianie utworu, więc po otwarciu i ułożeniu listy pokazuje grany wiersz.
        if (_viewModel.QueueOpen) Dispatcher.UIThread.Post(RevealCurrent, DispatcherPriority.Loaded);
    }

    /// <summary>Nakładana kolejka jest otwarta.</summary>
    internal bool QueueOverlayOpen => _queueAsOverlay && _viewModel.QueueOpen;

    internal bool QueueIsOverlay => _queueAsOverlay;

    private void OnQueueDismissPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.QueueOpen = false;
        e.Handled = true;
    }

    // ================= Obrot plyty i tla =================

    /// <summary>Pętla obrotu działa tylko wtedy, gdy płyta się kręci albo dojeżdża do pionu.</summary>
    private void UpdateRotationLoop()
    {
        if (_animationsFrozen || _rotation.IsRunning || !IsVisible) return;
        if (!SpinNeeded) return;

        _lastRotationFrame = TimeSpan.Zero;
        _rotation.Start();
    }

    private bool SpinNeeded => (_viewModel.IsPlaying && !_viewModel.ReducedEffects) || _returningUpright;

    internal bool IsRotating => _rotation.IsRunning;

    private void OnRotationFrame(TimeSpan timestamp)
    {
        var delta = _lastRotationFrame == TimeSpan.Zero
            ? 0
            : (timestamp - _lastRotationFrame).TotalSeconds;
        _lastRotationFrame = timestamp;

        // A long stall (window dragged, machine asleep) must not fling the disc round.
        delta = Math.Clamp(delta, 0, 0.25);

        // W trybie ograniczonym plyta stoi: to najdrozszy element wystroju, bo kazda klatka
        // to obrot bitmapy okladki.
        if (_viewModel.IsPlaying && !_viewModel.ReducedEffects)
        {
            _returningUpright = false;
            _angle = (_angle + delta * DegreesPerSecond) % 360;
            ApplyAngle();
        }
        else if (_returningUpright)
        {
            AdvanceUprightReturn(delta);
        }

        if (!SpinNeeded) _rotation.Stop();
    }

    /// <summary>
    /// Wytraca obrót płyty tak, żeby stanęła okładką prosto; przekrzywiona nieruchoma płyta
    /// wygląda na błąd.
    /// </summary>
    private void BeginUprightReturn()
    {
        if (!_viewModel.ReducedEffects || _angle <= 0)
        {
            _returningUpright = false;
            return;
        }

        _returningUpright = true;
        _returnFrom = _angle;
        _returnElapsed = 0;
    }

    /// <summary>
    /// Jedna klatka wytracania obrotu. Wygładzanie sześcienne: płyta rusza z prędkością bliską
    /// grania i staje łagodnie.
    /// </summary>
    private void AdvanceUprightReturn(double delta)
    {
        _returnElapsed += delta;

        var progress = Math.Clamp(_returnElapsed / UprightReturnSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        // Zawsze w przód, do pełnego obrotu, żeby płyta nie cofała się do góry.
        _angle = _returnFrom + (360 - _returnFrom) * eased;

        if (progress >= 1)
        {
            _angle = 0;
            _returningUpright = false;
        }

        ApplyAngle();
    }

    /// <summary>
    /// Ustawia wszystko, co się rusza, w powtarzalnym stanie dla narzędzia zrzutów: płytę
    /// prosto, plamy tła i falę na początku cyklu. Wszystkie idą według zegara.
    /// </summary>
    public void FreezeAnimationsForCapture()
    {
        _animationsFrozen = true;
        _rotation.Stop();
        _returningUpright = false;
        _angle = 0;
        ApplyAngle();

        Backdrop?.FreezeForCapture();
        Wave?.FreezeForCapture();
    }

    private void ApplyAngle()
    {
        if (Disc is not null) Disc.Angle = _angle;
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
        if (QueueList is null || !_viewModel.QueueOpen) return;
        if (_queueBrowsedByKeyboard && QueueList.IsKeyboardFocusWithin) return;

        if (_viewModel.Queue.FirstOrDefault(item => item.IsCurrent) is { } current) QueueList.ScrollIntoView(current);
    }

    private void OnToggleRepeat(object? sender, RoutedEventArgs e) => _viewModel.CycleRepeat();

    private void OnToggleMute(object? sender, RoutedEventArgs e) => _viewModel.ToggleMute();

    private void OnResetEqualiser(object? sender, RoutedEventArgs e) => _viewModel.ResetEqualiser();

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
    /// Otwiera ustawienia albo wysuwa otwarte. Okno staje obok odtwarzacza, bo część ustawień
    /// ocenia się słuchem.
    /// </summary>
    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <param name="section">Sekcja do pokazania, np. Dźwięk po poleceniu „Wybierz urządzenie”.</param>
    internal void OpenSettings(Func<SettingsViewModel, SettingsSection>? section = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(new SettingsViewModel(App.Settings, _viewModel))
            {
                PlaybackShortcuts = HandlePlaybackShortcut,
            };

            _settingsWindow.PlaceBeside(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show(this);
        }
        else
        {
            _settingsWindow.Activate();
        }

        if (section is not null) _settingsWindow.ShowSection(section);
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

            var load = _viewModel.LoadPlaylist(path);

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

        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

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
            case MediaCommand.Play when !_viewModel.IsPlaying: _viewModel.TogglePlay(); break;
            case MediaCommand.Pause when _viewModel.IsPlaying: _viewModel.Pause(); break;
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

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
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
        if (e.Handled || e.Key is not (Key.Space or Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var focused = FocusManager?.GetFocusedElement();
        if (KeepsArrowKeys(focused) || e.Key == Key.Space && KeepsSpace(focused)) return;

        HandlePlaybackShortcut(e);
    }

    /// <summary>Puszczenie Spacji też nie dociera do przycisku, bo ten klika właśnie przy puszczeniu.</summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
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

        // Transport i głośność wspólne z oknem ustawień, z tymi samymi modyfikatorami.
        if (HandlePlaybackShortcut(e)) return;

        var modifiers = e.KeyModifiers;
        var none = modifiers == KeyModifiers.None;
        var control = modifiers == KeyModifiers.Control;

        switch (e.Key)
        {
            // --- pliki ---
            case Key.O when modifiers == (KeyModifiers.Control | KeyModifiers.Shift): OnOpenFolder(this, new RoutedEventArgs()); break;
            case Key.O when control: OnOpenFiles(this, new RoutedEventArgs()); break;

            // --- odtwarzanie ---
            case Key.S when none: _viewModel.ShuffleEnabled = !_viewModel.ShuffleEnabled; break;
            case Key.R when none: _viewModel.CycleRepeat(); break;

            // --- interfejs ---
            case Key.Q when none: _viewModel.TogglePanel(); break;
            case Key.L when none: _viewModel.ToggleQueue(); break;
            case Key.T when none: OnToggleTheme(this, new RoutedEventArgs()); break;
            case Key.F11 when none: ToggleFullScreen(); break;
            case Key.OemComma when control: OpenSettings(); break;

            // Esc najpierw zamyka nakładaną kolejkę, potem kończy pełny ekran.
            case Key.Escape when none && QueueOverlayOpen: _viewModel.QueueOpen = false; break;
            case Key.Escape when none && WindowState == WindowState.FullScreen: ToggleFullScreen(); break;

            // --- kolejka ---
            case Key.Z when control && _viewModel.CanUndoQueue: _viewModel.UndoQueue(); break;

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
        if (e.Handled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);

        if (!_systemFrame && WindowState == WindowState.Normal && TryGetResizeEdge(position) is { } edge)
        {
            BeginResizeDrag(edge, e);
            return;
        }

        if (position.Y > TitleBar.Bounds.Height) return;

        // Na pełnym ekranie pasek tytułu nie przesuwa okna; dwuklik z niego wychodzi.
        if (WindowState == WindowState.FullScreen)
        {
            if (e.ClickCount == 2) ToggleFullScreen();
            return;
        }

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
