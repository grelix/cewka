using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cewka.App.Models;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// The settings window: everything the player remembers between runs, in one place.
/// <para>
/// Frameless like the main window, and deliberately not modal — a device or a theme is chosen
/// by listening and looking, which means the music has to keep playing and the window behind
/// has to stay usable while the choice is made.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Grab band along the window border for resizing, as in the main window.</summary>
    private const double ResizeMargin = 6;

    private SettingsViewModel? _viewModel;

    /// <summary>
    /// Required by the XAML runtime loader. The window is always created with a view model
    /// from the player; without one it simply shows its own chrome and empty sections.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        ApplyControlsPosition();

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

    private void ApplyControlsPosition()
    {
        var position = App.Settings.Current.WindowControls;
        var onRight = position == WindowControlsPosition.Right;

        ControlsLeft.Position = position;
        ControlsRight.Position = position;

        ControlsLeft.IsVisible = !onRight;
        ControlsRight.IsVisible = onRight;
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

        if (e.Key != Key.Escape) return;

        Close();
        e.Handled = true;
    }

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingsSection section }) return;

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

    private void OnRefreshDevices(object? sender, RoutedEventArgs e) => _viewModel?.RefreshDevices();

    private void OnDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceOption device }) _viewModel?.ChooseDevice(device);
    }

    private void OnRegisterAssociations(object? sender, RoutedEventArgs e) => _viewModel?.RegisterAssociations();

    private void OnRemoveAssociations(object? sender, RoutedEventArgs e) => _viewModel?.RemoveAssociations();

    // ================= Sterowanie oknem =================

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

        if (position.Y <= TitleBar.Bounds.Height) BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

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

    private WindowEdge? TryGetResizeEdge(Avalonia.Point position)
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
