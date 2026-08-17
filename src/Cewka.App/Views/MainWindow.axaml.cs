using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    /// Breakpoints for the layout. Avalonia has no equivalent of CSS media queries, and
    /// driving this through style classes proved unreliable at start-up, when the window
    /// bounds are still zero. Setting the handful of affected properties directly is both
    /// deterministic and easy to follow.
    /// </summary>
    /// <summary>
    /// Layout steps, from a narrow window to a maximised one on a large display. Without the
    /// upper steps the record and the title stay the size they were designed at, and the
    /// window on a 27-inch screen reads as a small interface stranded in a lot of empty space.
    /// </summary>
    private static readonly (double MinWidth, double Disc, double Title, double EqColumn)[] LayoutSteps =
    [
        (0, 228, 32, 468),
        (1080, 300, 42, 524),
        (1500, 356, 50, 600),
        (1900, 412, 58, 680),
        (2400, 468, 64, 760),
    ];

    /// <summary>Grab band along the window border for resizing.</summary>
    private const double ResizeMargin = 6;

    /// <summary>
    /// Smallest window height that still fits everything, with and without the panel.
    ///
    /// <para>Musi zależeć od stanu panelu, bo panel korektora i kolejki ma własną wysokość
    /// naturalną i nie da się jej skurczyć. Przy jednej, wspólnej wartości można było zwinąć
    /// panel, zmniejszyć okno do minimum, a potem panel rozwinąć — i wtedy nie było już miejsca
    /// na blok odtwarzania, który wychodził poza swój obszar i nachodził na panel.</para>
    ///
    /// <para>Wartości wynikają z pomiaru: pasek tytułu 47, panel około 255, a blok odtwarzania
    /// z najmniejszą płytą i ściśniętymi marginesami około 300.</para>
    /// </summary>
    private const double MinHeightWithPanel = 620;
    private const double MinHeightWithoutPanel = 360;

    private readonly MainViewModel _viewModel;
    private readonly RenderLoop _rotation;
    private TimeSpan _lastRotationFrame;
    private double _angle;

    /// <summary>
    /// Height the window had before expanding the panel forced it taller, and the height that
    /// was forced. Both are needed: the first is where to go back to, the second is how to tell
    /// whether the user has resized the window since.
    /// </summary>
    private double? _heightBeforePanel;
    private double _forcedHeight;

    /// <summary>Set while the record is coasting to a stop at the top of its revolution.</summary>
    private bool _returningUpright;
    private double _returnFrom;
    private double _returnElapsed;

    public MainWindow()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method
        // assigns the x:Name backing fields, and every piece of behaviour below depends
        // on them (disc rotation, queue scroll bar, responsive layout).
        InitializeComponent();

        _viewModel = new MainViewModel(App.Settings);
        DataContext = _viewModel;

        _rotation = new RenderLoop(this, OnRotationFrame);

        // Ograniczenie wysokosci przed odtworzeniem geometrii: zapamietany rozmiar sprawdzany
        // jest wzgledem MinHeight, a to zalezy od tego, czy panel jest rozwiniety.
        ApplyHeightConstraint();
        RestoreGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentIndex)) SyncQueueSelection();
            else if (e.PropertyName == nameof(MainViewModel.PanelOpen)) ApplyHeightConstraint();
            else if (e.PropertyName == nameof(MainViewModel.ReducedEffects)) BeginUprightReturn();
        };
    }

    /// <summary>
    /// Keeps the window tall enough for what it currently shows. Rozwinięcie panelu w oknie
    /// niższym niż jego zawartość podnosi okno do wysokości minimalnej — inaczej nie ma
    /// fizycznie miejsca, w którym blok odtwarzania mógłby się zmieścić.
    ///
    /// <para>Podniesienie jest odwracalne: wysokość sprzed rozwinięcia wraca po schowaniu
    /// panelu. Bez tego okno rosło w jedną stronę — każde pokazanie korektora zostawiało je
    /// wyższym na stałe, choć powód, dla którego urosło, już nie istniał.</para>
    /// </summary>
    private void ApplyHeightConstraint()
    {
        var minimum = _viewModel.PanelOpen ? MinHeightWithPanel : MinHeightWithoutPanel;
        MinHeight = minimum;

        if (WindowState != WindowState.Normal) return;

        if (Height < minimum)
        {
            _heightBeforePanel = Height;
            _forcedHeight = minimum;
            Height = minimum;
            return;
        }

        if (_viewModel.PanelOpen || _heightBeforePanel is not { } wanted) return;

        // Przywracamy tylko wtedy, gdy od naszego podniesienia nikt nie chwycił krawędzi okna.
        // Porównanie z zapamiętaną wysokością wystarcza i nie zależy od kolejności zdarzeń
        // rozmiaru: jeśli użytkownik ustawił okno sam, cofanie tego byłoby odebraniem mu decyzji.
        if (Math.Abs(Height - _forcedHeight) < 0.5) Height = Math.Max(wanted, minimum);

        _heightBeforePanel = null;
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

        _rotation.Start();
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

        mpris.RaiseRequested += () => Dispatcher.UIThread.Post(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });

        mpris.QuitRequested += () => Dispatcher.UIThread.Post(Close);

        _mediaPanel = mpris;
    }

    private IMediaPanel? _mediaPanel;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _rotation.Stop();
        PersistGeometry();
        _viewModel.SaveQueueState();
        App.Settings.SaveNow();
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
        ApplyResponsiveLayout();
    }

    // ================= Geometria okna =================

    private void RestoreGeometry()
    {
        var saved = App.Settings.Current.Window;
        if (saved is null) return;

        if (saved.Width >= MinWidth && saved.Height >= MinHeight)
        {
            Width = saved.Width;
            Height = saved.Height;
        }

        // A monitor may have been unplugged since the last run; only restore the
        // position if it still lands on a screen that exists.
        var point = new PixelPoint(saved.X, saved.Y);
        if (Screens.ScreenFromPoint(point) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = point;
        }

        if (saved.Maximized) WindowState = WindowState.Maximized;

        // Wysokość zapamiętana chwilę wcześniej przez ApplyHeightConstraint dotyczyła rozmiaru
        // projektowego, a nie rozmiaru z poprzedniego uruchomienia. Po odtworzeniu geometrii
        // nie ma już do czego wracać: to, co widzi użytkownik, jest jego własnym wyborem.
        _heightBeforePanel = null;
    }

    private void PersistGeometry()
    {
        var maximized = WindowState == WindowState.Maximized;

        App.Settings.Current.Window = new WindowGeometry
        {
            // While maximized the current bounds describe the screen, not the window
            // the user actually sized, so keep whatever was stored before.
            X = maximized ? App.Settings.Current.Window?.X ?? Position.X : Position.X,
            Y = maximized ? App.Settings.Current.Window?.Y ?? Position.Y : Position.Y,
            Width = maximized ? App.Settings.Current.Window?.Width ?? Width : Width,
            Height = maximized ? App.Settings.Current.Window?.Height ?? Height : Height,
            Maximized = maximized,
        };

        App.Settings.Touch();
    }

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

        if (DiscHost is not null)
        {
            // Plyta rosnie z szerokoscia, ale nie moze przerosnac wysokosci dostepnej
            // po odjeciu paska tytulu, panelu i marginesow.
            var vertical = Math.Max(200, height - 47 - (_viewModel.PanelOpen ? 300 : 90) - 120);
            var size = Math.Min(step.Disc, vertical);

            DiscHost.Width = size;
            DiscHost.Height = size;
        }

        if (TrackTitle is not null) TrackTitle.FontSize = step.Title;

        if (TrackArtist is not null) TrackArtist.FontSize = Math.Round(step.Title * 0.40, 1);

        if (NowPlaying is not null)
        {
            var padding = compact ? 28 : Math.Min(72, 40 + (width - 1180) * 0.05);

            // Odstep od gory i dolu rosnie z wysokoscia okna, zamiast byc staly. W oknie
            // sciscietym do minimum kazdy niepotrzebny piksel oddechu odbiera miejsce trescii,
            // a przy duzej wysokosci ten sam odstep wygladalby na skapy.
            var room = Math.Clamp((height - MinHeightWithPanel) / 240, 0, 1);
            var top = 14 + room * (compact ? 12 : 24);
            var bottom = 8 + room * (compact ? 6 : 10);

            NowPlaying.Margin = new Thickness(padding, top, padding, bottom);
            NowPlaying.ColumnSpacing = compact ? 32 : Math.Min(72, 48 + (width - 1180) * 0.03);
        }

        // Prawa kolumna rozciaga sie swobodnie do granicy, powyzej ktorej pasek postepu
        // przestaje byc czytelny; wtedy caly blok jest srodkowany zamiast rozciagany.
        if (NowPlaying is not null)
            NowPlaying.HorizontalAlignment = width > 1700
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Stretch;

        // x:Name na ColumnDefinition nie tworzy pola, wiec kolumna wskazywana jest indeksem.
        if (PanelGrid?.ColumnDefinitions.Count > 0)
            PanelGrid.ColumnDefinitions[0].Width = new GridLength(step.EqColumn);

        // Wyzsze okno pokazuje wiecej kolejki, zamiast zostawiac pusty pas.
        //
        // Dolna granica zeszla ze 176 na 168, bo naglowek kolejki zrownal wysokosc z naglowkiem
        // korektora i przez to urosl o 16 px. Bez tej korekty przy dlugiej kolejce ta kolumna
        // stalaby sie wyzsza od kolumny korektora i caly panel potrzebowalby 8 px wiecej - czyli
        // zmierzona wysokosc minimalna okna (MinHeightWithPanel) przestalaby byc prawdziwa.
        if (QueueHost is not null) QueueHost.MaxHeight = Math.Clamp(height * 0.26, 168, 420);
    }

    // ================= Obrot plyty i tla =================

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
    }

    /// <summary>
    /// Starts the record coasting to rest with the cover the right way up.
    /// <para>
    /// Cutting the effects used to leave the rotation wherever it happened to be, so the sleeve
    /// ended up on its side or upside down and stayed there — the one state in which a still
    /// picture of a record looks wrong.
    /// </para>
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
    /// Brings every moving part to a fixed, repeatable state: the record upright, the background
    /// blobs at the start of their paths, the waveform at the start of its cycle.
    /// <para>
    /// This is for the snapshot tool. All three animations run off wall-clock time, so two runs
    /// of the same code produced two different pictures — which made the snapshots useless for
    /// the one job they exist to do, namely comparing one version of the interface against the
    /// next. Photographs of a paused player, so nothing here invents motion that isn't there.
    /// </para>
    /// </summary>
    public void FreezeAnimationsForCapture()
    {
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
        if (Backdrop?.RenderTransform is RotateTransform rotate) rotate.Angle = _angle;
    }

    // ================= Poswiata pod kursorem =================

    private void OnDiscPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Glow is null || DiscHost is null) return;
        if (_viewModel.ReducedEffects) { Glow.Intensity = 0; return; }

        var position = e.GetPosition(Glow);
        var radius = Math.Min(Glow.Bounds.Width, Glow.Bounds.Height) / 2;
        var centre = new Point(Glow.Bounds.Width / 2, Glow.Bounds.Height / 2);

        var distance = Math.Sqrt(
            Math.Pow(position.X - centre.X, 2) + Math.Pow(position.Y - centre.Y, 2));

        // Full strength over the record, fading out as the pointer moves away.
        var proximity = Math.Clamp(1 - (distance - radius) / 340, 0, 1);

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

    private void OnTogglePanel(object? sender, RoutedEventArgs e) => _viewModel.TogglePanel();

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

    /// <summary>
    /// Opens the settings, or brings them forward if they are already up. Shown beside the
    /// player rather than over it: several of the settings are judged by ear, which needs the
    /// music playing and the transport reachable.
    /// </summary>
    private void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(new SettingsViewModel(App.Settings, _viewModel));
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

            if (paths.Count > 0) _viewModel.OpenPaths(paths, replace: _viewModel.IsQueueEmpty);
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
            if (!string.IsNullOrEmpty(path)) _viewModel.OpenPaths([path], replace: _viewModel.IsQueueEmpty);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] okno wyboru folderu: {ex.Message}");
        }
    }

    private static FilePickerFileType BuildAudioFilter() => new("Pliki dźwiękowe")
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

    // ================= Sterowanie oknem =================

    /// <summary>
    /// Minimise, maximise and close live in <see cref="Controls.WindowButtons"/>, which acts
    /// on the window directly. Only the double-click on the title bar is handled here.
    /// </summary>
    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

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
            case Key.T: OnToggleTheme(this, new RoutedEventArgs()); break;
            case Key.F11: ToggleFullScreen(); break;

            // --- kolejka ---
            case Key.Delete: OnQueueRemove(this, new RoutedEventArgs()); break;

            default: return;
        }

        e.Handled = true;
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);

        if (WindowState == WindowState.Normal && TryGetResizeEdge(position) is { } edge)
        {
            BeginResizeDrag(edge, e);
            return;
        }

        // Dragging anywhere on the empty part of the title bar moves the window.
        if (TitleBar is not null && position.Y <= TitleBar.Bounds.Height)
        {
            if (e.ClickCount == 2) ToggleMaximise();
            else BeginMoveDrag(e);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

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

    /// <summary>
    /// Works out which border the pointer is over. Without system decorations the window
    /// has no resize frame of its own, so one is provided here.
    /// </summary>
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
