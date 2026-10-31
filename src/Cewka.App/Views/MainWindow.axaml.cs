using Avalonia;
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
    /// How long the record takes to come to rest upright once the effects are cut back.
    /// <para>
    /// Long enough to read as slowing down rather than snapping, short enough that nobody waits
    /// for it. The rotation always finishes forwards, never backwards: a record that reverses
    /// to reach the top looks broken.
    /// </para>
    /// </summary>
    private const double UprightReturnSeconds = 0.45;

    /// <summary>
    /// Layout steps by window width, from a narrow window to a maximised one on a large display.
    /// Avalonia has no equivalent of CSS media queries, so the handful of affected properties
    /// is set directly. Without the upper steps the window on a 27-inch screen reads as a small
    /// interface stranded in a lot of empty space.
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
    /// Kolejkę schowało zwężenie okna, a nie użytkownik. Po poszerzeniu wraca sama, a do ustawień
    /// trafia jako otwarta.
    /// </summary>
    private bool _queueCollapsedByWidth;
    private bool _adjustingQueue;

    public MainWindow()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method
        // assigns the x:Name backing fields, and every piece of behaviour below depends
        // on them (disc rotation, queue scroll bar, responsive layout).
        InitializeComponent();

        _viewModel = new MainViewModel(App.Settings);
        DataContext = _viewModel;

        _rotation = new RenderLoop(this, OnRotationFrame);

        _systemFrame = OperatingSystem.IsWindows();
        if (_systemFrame) WindowDecorations = WindowDecorations.BorderOnly;

        ApplyWindowShape();
        RestoreGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        PositionChanged += (_, _) => RememberNormalBounds();
        CentreHost.SizeChanged += (_, _) => ApplyResponsiveLayout();
        TrackInfo.SizeChanged += (_, _) => ApplyResponsiveLayout();

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentIndex): SyncQueueSelection(); break;
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        ApplyResponsiveLayout();
        AttachQueueScrollBar();

        // Sciezki z wiersza polecen maja pierwszenstwo przed kolejka z poprzedniej sesji:
        // uruchomienie przez "Otworz za pomoca" ma zagrac wskazany plik, nie poprzedni.
        if (App.StartupPaths.Length > 0) _viewModel.OpenFromOutside(App.StartupPaths);
        else _viewModel.RestoreQueueState();

        if (QueueList is not null)
            QueueList.SelectedItem = _viewModel.Queue.FirstOrDefault(item => item.IsCurrent);

        // Bez oczekiwania na wynik: uruchomienie programu nie ma zalezec od tego, czy siec
        // odpowiada. Przy wylaczonym ustawieniu — a tak jest domyslnie — nic sie nie dzieje.
        _ = _viewModel.CheckForUpdatesIfDueAsync();

        AttachMediaPanel();

        UpdateRotationLoop();
    }

    /// <summary>
    /// Hooks the window up to the desktop's media panel: the Windows overlay, attached to this
    /// window's handle, or MPRIS on the session bus in Linux. Only possible once the window
    /// exists, because the Windows panel belongs to a window rather than to the process.
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
    /// Publishes the player on the session bus. Klawisze multimedialne obsługuje w Linuksie
    /// pulpit i przekazuje je właśnie tędy, więc osobne przechwytywanie klawiatury — jak
    /// w systemie Windows — jest tu niepotrzebne.
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
        App.BeginClosing();
        _rotation.Stop();
        PersistGeometry();

        // Kolejkę schowało tylko wąskie okno; przy następnym starcie ma wrócić, jeśli się zmieści.
        if (_queueCollapsedByWidth) App.Settings.Current.QueueOpen = true;

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
        if (ScreenForTitleBar(saved) is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;

            Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scale));
            Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scale));

            var width = (int)(Width * scale);
            var grip = (int)(100 * scale);
            var x = Math.Clamp(saved.X, area.X - width + grip, area.Right - grip);
            var y = Math.Clamp(saved.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(47 * scale)));

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

        // Pasek glosnosci to jedyny element rzedu transportu o swobodnej szerokosci, wiec on
        // ustepuje pierwszy, gdy rzad sie nie miesci.
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
        try { _viewModel.QueueOpen = open; }
        finally { _adjustingQueue = false; }
    }

    private void OnQueueOpenChanged()
    {
        if (!_adjustingQueue) _queueCollapsedByWidth = false;

        QueueDismiss.IsVisible = _queueAsOverlay && _viewModel.QueueOpen;
        ApplyResponsiveLayout();
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
    /// Starts the record coasting to rest with the cover the right way up. Cutting the effects
    /// used to leave the sleeve on its side or upside down — the one state in which a still
    /// picture of a record looks wrong.
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
    /// Carries the coast-to-rest forward by one frame. The easing is cubic on the way out, so
    /// the record leaves at close to playing speed and settles gently instead of stopping dead.
    /// </summary>
    private void AdvanceUprightReturn(double delta)
    {
        _returnElapsed += delta;

        var progress = Math.Clamp(_returnElapsed / UprightReturnSeconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        // Zawsze w przod, do najblizszego pelnego obrotu: 360 stopni to ta sama pozycja co zero,
        // wiec plyta dojezdza na gore, nie cofa sie do niej.
        _angle = _returnFrom + (360 - _returnFrom) * eased;

        if (progress >= 1)
        {
            _angle = 0;
            _returningUpright = false;
        }

        ApplyAngle();
    }

    /// <summary>
    /// Brings every moving part to a fixed, repeatable state for the snapshot tool: the record
    /// upright, the background blobs at the start of their paths, the waveform at the start of
    /// its cycle. All three run off wall-clock time, so without this two runs of the same code
    /// produced two different pictures.
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

    /// <summary>
    /// Only a selection the user made should start a track. Without this guard, following
    /// the current track with the highlight would itself restart playback.
    /// </summary>
    private bool _updatingSelection;

    private void OnQueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;
        if (QueueList?.SelectedItem is QueueItemViewModel item) _viewModel.PlayItem(item);
    }

    // ================= Przyciski =================

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => _viewModel.TogglePlay();

    // Przewijanie dzieje sie po puszczeniu przycisku; w trakcie przeciagania suwak i czas
    // podazaja za wskaznikiem, ale odtwarzanie zostaje tam, gdzie bylo.
    private void OnSeekScrubbing(object? sender, double value) => _viewModel.BeginScrub();

    private void OnSeekCommitted(object? sender, double value) => _viewModel.CommitScrub(value);

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        _viewModel.Next();
        SyncQueueSelection();
    }

    private void OnPrevious(object? sender, RoutedEventArgs e)
    {
        _viewModel.Previous();
        SyncQueueSelection();
    }

    private void SyncQueueSelection()
    {
        if (QueueList is null) return;

        _updatingSelection = true;
        try
        {
            QueueList.SelectedItem = _viewModel.Queue.FirstOrDefault(item => item.IsCurrent);
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private void OnToggleShuffle(object? sender, RoutedEventArgs e) =>
        _viewModel.ShuffleEnabled = !_viewModel.ShuffleEnabled;

    private void OnToggleRepeat(object? sender, RoutedEventArgs e) => _viewModel.CycleRepeat();

    private void OnToggleLimiter(object? sender, RoutedEventArgs e) =>
        _viewModel.LimiterEnabled = !_viewModel.LimiterEnabled;

    private void OnToggleNormalisation(object? sender, RoutedEventArgs e) =>
        _viewModel.NormalisationEnabled = !_viewModel.NormalisationEnabled;

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        var mode = App.Theme.Cycle();
        App.Settings.Current.Theme = mode;
        App.Settings.Touch();
    }

    private SettingsWindow? _settingsWindow;

    internal SettingsWindow? OpenSettingsWindow => _settingsWindow;

    /// <summary>
    /// Opens the settings, or brings them forward if they are already up. Shown beside the
    /// player rather than over it: several of the settings are judged by ear, which needs the
    /// music playing and the transport reachable.
    /// </summary>
    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(new SettingsViewModel(App.Settings, _viewModel))
        {
            PlaybackShortcuts = HandlePlaybackShortcut,
        };

        _settingsWindow.PlaceBeside(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    // ================= Dodawanie plików =================

    private async void OnOpenFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Current["OpenFiles"],
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
    /// O plikach z listy, których już nie ma, program mówi wprost, w miejscu przeznaczonym na
    /// komunikaty. Milczące pominięcie ich znaczyłoby, że wczytana kolejka jest krótsza od
    /// zapisanej, a użytkownik dowiaduje się o tym najwcześniej wtedy, gdy czegoś w niej szuka.
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
                Title = Strings.Current["OpenFolder"],
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

    /// <summary>Adds paths handed over by another copy of the application.</summary>
    public void ReceivePaths(string[] paths)
    {
        if (paths.Length == 0) return;
        _viewModel.OpenFromOutside(paths);
    }

    /// <summary>
    /// Acts on a multimedia key. „Stop" pauses rather than stopping outright: the position is
    /// then kept, and pressing play again carries on instead of starting the track over.
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

        SyncQueueSelection();
    }

    /// <summary>
    /// Acts on a request from the desktop's media panel. „Play" i „Pause" są tu osobnymi
    /// poleceniami, a nie przełącznikiem: panel, który prosi o pauzę, oczekuje pauzy również
    /// wtedy, gdy nic nie gra, a przełącznik zacząłby wtedy grać.
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

        SyncQueueSelection();
    }

    // ================= Operacje na kolejce =================

    private void OnQueueRemove(object? sender, RoutedEventArgs e)
    {
        if (QueueList?.SelectedItem is QueueItemViewModel item) _viewModel.RemoveFromQueue(item);
    }

    private void OnQueueClear(object? sender, RoutedEventArgs e) => _viewModel.ClearQueue();

    private void OnQueueMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnQueueMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (QueueList?.SelectedItem is not QueueItemViewModel item) return;

        var from = _viewModel.Queue.IndexOf(item);
        _viewModel.MoveInQueue(from, from + delta);
    }

    // ---------- Zmiana kolejności przeciąganiem ----------

    /// <summary>Distance the pointer must travel before a press counts as a drag.</summary>
    private const double DragThreshold = 6;

    private int _dragSourceIndex = -1;
    private Point _dragOrigin;
    private bool _dragging;

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (QueueList is null) return;
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;

        _dragOrigin = e.GetPosition(QueueList);
        _dragSourceIndex = IndexAt(_dragOrigin);
        _dragging = false;
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSourceIndex < 0 || QueueList is null) return;
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
        if (QueueList is not null) QueueList.Cursor = Cursor.Default;

        var source = _dragSourceIndex;
        var dragged = _dragging;

        _dragSourceIndex = -1;
        _dragging = false;

        if (!dragged || source < 0 || QueueList is null) return;

        var target = IndexAt(e.GetPosition(QueueList));
        if (target >= 0 && target != source)
        {
            _viewModel.MoveInQueue(source, target);

            // Przeciagniety wiersz zostaje zaznaczony, zeby nie zgubic go z oczu.
            _updatingSelection = true;
            try { QueueList.SelectedIndex = target; }
            finally { _updatingSelection = false; }
        }

        e.Handled = true;
    }

    /// <summary>Finds the queue row under a point, or −1 when the point is past the last one.</summary>
    private int IndexAt(Point position)
    {
        if (QueueList is null) return -1;

        for (var i = 0; i < _viewModel.Queue.Count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not Control container) continue;

            var bounds = container.Bounds;
            var top = container.TranslatePoint(new Point(0, 0), QueueList)?.Y ?? bounds.Y;

            if (position.Y >= top && position.Y <= top + bounds.Height) return i;
        }

        return -1;
    }

    // ================= Skroty klawiszowe =================

    /// <summary>
    /// Cztery zestawy uzgodnione przy planowaniu: transport, glosnosc, interfejs i pliki.
    /// Obsluga siedzi na oknie, a nie na poszczegolnych kontrolkach, zeby dzialala niezaleznie
    /// od tego, co ma wlasnie ognisko - z jednym wyjatkiem: gdy ognisko ma suwak lub fader,
    /// strzalki naleza do niego.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            // --- pliki ---
            case Key.O when control && shift: OnOpenFolder(this, new RoutedEventArgs()); break;
            case Key.O when control: OnOpenFiles(this, new RoutedEventArgs()); break;

            // --- transport ---
            case Key.Space: _viewModel.TogglePlay(); break;
            case Key.Right when control: _viewModel.Next(); break;
            case Key.Left when control: _viewModel.Previous(); break;
            case Key.Right: _viewModel.SeekRelative(_viewModel.SeekStep); break;
            case Key.Left: _viewModel.SeekRelative(-_viewModel.SeekStep); break;

            // --- glosnosc ---
            case Key.Up: _viewModel.AdjustVolume(0.05); break;
            case Key.Down: _viewModel.AdjustVolume(-0.05); break;
            case Key.M: _viewModel.ToggleMute(); break;

            // --- interfejs ---
            case Key.Q: _viewModel.TogglePanel(); break;
            case Key.L: _viewModel.ToggleQueue(); break;
            case Key.T: OnToggleTheme(this, new RoutedEventArgs()); break;
            case Key.F11: ToggleFullScreen(); break;
            case Key.OemComma when control: OpenSettings(); break;

            // Esc najpierw zamyka nakładaną kolejkę, potem kończy pełny ekran.
            case Key.Escape when QueueOverlayOpen: _viewModel.QueueOpen = false; break;
            case Key.Escape when WindowState == WindowState.FullScreen: ToggleFullScreen(); break;

            // --- kolejka ---
            case Key.Delete: OnQueueRemove(this, new RoutedEventArgs()); break;

            default: return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Skróty odtwarzania i głośności, które przyjmuje także okno ustawień: Spacja, strzałki,
    /// Ctrl+strzałki i M. Skróty interfejsu (Q, L, Delete) dotyczą tylko tego okna.
    /// </summary>
    internal bool HandlePlaybackShortcut(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        var none = modifiers == KeyModifiers.None;
        var control = modifiers == KeyModifiers.Control;

        switch (e.Key)
        {
            case Key.Space when none: _viewModel.TogglePlay(); break;
            case Key.Right when control: _viewModel.Next(); SyncQueueSelection(); break;
            case Key.Left when control: _viewModel.Previous(); SyncQueueSelection(); break;
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
