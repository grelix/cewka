using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.Platform;

namespace Cewka.App.Views;

/// <summary>
/// The settings window: everything the player remembers between runs, in one place.
/// <para>
/// Frameless like the main window, and deliberately not modal — a device or a theme is chosen
/// by listening and looking, which means the music has to keep playing and the window behind
/// has to stay usable while the choice is made. Z tego samego powodu okno przyjmuje skróty
/// odtwarzania i staje obok okna głównego, a nie na nim.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Grab band along the window border for resizing, as in the main window.</summary>
    private const double ResizeMargin = 6;

    /// <summary>Odstęp od okna głównego przy ustawianiu obok niego.</summary>
    private const double Gap = 12;

    /// <summary>Zapas na niewidoczną ramkę systemową przy sprawdzaniu zapamiętanego położenia.</summary>
    private const double FrameTolerance = 8;

    private SettingsViewModel? _viewModel;

    /// <summary>Ramka systemowa w Windows, jak w oknie głównym.</summary>
    private readonly bool _systemFrame;

    /// <summary>
    /// Required by the XAML runtime loader. The window is always created with a view model
    /// from the player; without one it simply shows its own chrome and empty sections.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        ApplyControlsPosition();

        _systemFrame = OperatingSystem.IsWindows();
        if (_systemFrame)
        {
            WindowDecorations = WindowDecorations.BorderOnly;
            WindowDecorationProperties.SetElementRole(TitleBar, WindowDecorationsElementRole.TitleBar);
            Shell.Classes.Add("square");
        }

        // Wlasny pasek przewijania, ten sam co przy kolejce; domyslny z motywu Fluent
        // jest szerszy i nie nalezy do jezyka wizualnego reszty programu.
        ContentScrollBar.Target = ContentArea;
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Pozycja przycisków okna jest jednym z ustawień w tym oknie, więc musi się w nim
        // przestawić natychmiast — inaczej jedyne miejsce, gdzie tej zmiany nie widać, byłoby
        // to, w którym się jej dokonuje.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CurrentWindowControls)) ApplyControlsPosition();
        };
    }

    /// <summary>Obsługa skrótów odtwarzania z okna głównego; zwraca prawdę, gdy klawisz był skrótem.</summary>
    public Func<KeyEventArgs, bool>? PlaybackShortcuts { get; init; }

    private void ApplyControlsPosition()
    {
        var position = App.Settings.Current.WindowControls;
        var onRight = position == WindowControlsPosition.Right;

        ControlsLeft.Position = position;
        ControlsRight.Position = position;

        ControlsLeft.IsVisible = !onRight;
        ControlsRight.IsVisible = onRight;
    }

    // ================= Położenie =================

    /// <summary>
    /// Ustawia okno przed pokazaniem. Zapamiętane położenie wraca, jeśli okno mieści się na
    /// którymś z ekranów. Inaczej okno staje obok głównego, gdy obok jest miejsce w obszarze
    /// roboczym, a gdy go nie ma — przy krawędzi ekranu po stronie, gdzie wolnego miejsca jest więcej.
    /// </summary>
    public void PlaceBeside(Window owner)
    {
        if ((Screens.ScreenFromWindow(owner) ?? Screens.Primary) is not { } screen)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }

        var scale = screen.Scaling;

        // Na małym ekranie (np. 1366×768 przy 125%) okno o wysokości z projektu wystawałoby
        // poza obszar roboczy i nigdy nie wróciłoby w zapamiętane miejsce.
        Width = FitLength(Width, MinWidth, screen.WorkingArea.Width / scale);
        Height = FitLength(Height, MinHeight, screen.WorkingArea.Height / scale);

        var size = new PixelSize((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
        var saved = App.Settings.Current.SettingsWindow;

        if (saved is not null
            && FitsOn(new PixelRect(new PixelPoint(saved.X, saved.Y), size),
                      Screens.All.Select(s => s.WorkingArea), (int)(FrameTolerance * scale)))
        {
            Position = new PixelPoint(saved.X, saved.Y);
            return;
        }

        var ownerRect = new PixelRect(owner.Position, PixelSize.FromSize(owner.ClientSize, owner.RenderScaling));
        Position = Beside(ownerRect, size, screen.WorkingArea, (int)(Gap * scale));
    }

    /// <summary>Długość okna przycięta do dostępnego miejsca, ale nie poniżej minimum.</summary>
    internal static double FitLength(double length, double minimum, double available) =>
        Math.Max(minimum, Math.Min(length, available));

    /// <summary>
    /// Okno leży na którymś z obszarów roboczych. Kilka pikseli zapasu obejmuje niewidoczną ramkę
    /// systemową i okno przyciągnięte do krawędzi, które zaczyna się tuż poza obszarem.
    /// </summary>
    internal static bool FitsOn(PixelRect window, IEnumerable<PixelRect> areas, int tolerance) =>
        areas.Any(area => new PixelRect(area.X - tolerance, area.Y - tolerance,
                                        area.Width + 2 * tolerance, area.Height + 2 * tolerance).Contains(window));

    /// <summary>Położenie obok prostokąta okna głównego w obrębie obszaru roboczego.</summary>
    internal static PixelPoint Beside(PixelRect owner, PixelSize size, PixelRect area, int gap)
    {
        var y = Math.Clamp(owner.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));

        var right = owner.Right + gap;
        if (right + size.Width <= area.Right) return new PixelPoint(right, y);

        var left = owner.X - gap - size.Width;
        if (left >= area.X) return new PixelPoint(left, y);

        var roomRight = area.Right - owner.Right;
        var roomLeft = owner.X - area.X;
        var x = roomRight >= roomLeft ? area.Right - size.Width : area.X;

        return new PixelPoint(Math.Max(area.X, x), y);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        App.Settings.Current.SettingsWindow = new WindowGeometry
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        };
        App.Settings.Touch();

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Klawisze, których nie zużyła kontrolka z fokusem, sterują odtwarzaniem jak w oknie głównym.
        PlaybackShortcuts?.Invoke(e);
    }

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsSection section }) Select(section);
    }

    /// <summary>Pokazuje wskazaną sekcję, np. Dźwięk po poleceniu „Wybierz urządzenie”.</summary>
    internal void ShowSection(Func<SettingsViewModel, SettingsSection> pick)
    {
        if (_viewModel is not null) Select(pick(_viewModel));
    }

    private void Select(SettingsSection section)
    {
        _viewModel?.SelectSection(section);

        // Przewiniecie nalezy do porzuconej zakladki; bez tego kolejna otwieralaby sie
        // w polowie, w miejscu, ktorego uzytkownik nigdy nie wskazal.
        ContentArea.Offset = default;
    }

    /// <summary>
    /// Wybor jezyka ma postac listy, a nie paska segmentow: przy szesciu pozycjach segmenty
    /// nie mieszcza sie w jednym wierszu. Grupa pod spodem pozostaje ta sama.
    /// </summary>
    private void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SegmentOption option }) _viewModel?.Language.Choose(option);
    }

    private void OnPaletteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PaletteOption option }) _viewModel?.SelectPalette(option);
    }

    private void OnRefreshDevices(object? sender, RoutedEventArgs e) => _viewModel?.RefreshDevices();

    private void OnDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceOption device }) _viewModel?.ChooseDevice(device);
    }

    private void OnOpenRepository(object? sender, RoutedEventArgs e) =>
        WebLink.Open(_viewModel?.RepositoryUrl);

    private void OnOpenReleases(object? sender, RoutedEventArgs e) =>
        WebLink.Open(_viewModel?.ReleasesUrl);

    private void OnOpenIssues(object? sender, RoutedEventArgs e) =>
        WebLink.Open(_viewModel?.IssuesUrl);

    /// <summary>
    /// Sprawdzenie idzie bez oczekiwania na wynik: żądanie sieciowe nie może zatrzymać okna,
    /// a stan i tak dociera przez wiązania.
    /// </summary>
    private void OnCheckForUpdates(object? sender, RoutedEventArgs e) =>
        _ = _viewModel?.CheckForUpdatesNowAsync();

    private void OnRegisterAssociations(object? sender, RoutedEventArgs e) => _viewModel?.RegisterAssociations();

    private void OnRemoveAssociations(object? sender, RoutedEventArgs e) => _viewModel?.RemoveAssociations();

    // ================= Sterowanie oknem bez ramki systemowej =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _systemFrame) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);

        if (WindowState == WindowState.Normal && TryGetResizeEdge(position) is { } edge)
        {
            BeginResizeDrag(edge, e);
            return;
        }

        if (position.Y <= TitleBar.Bounds.Height) BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_systemFrame) return;

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
