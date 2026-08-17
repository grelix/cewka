using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cewka.App.Models;

namespace Cewka.App.Controls;

/// <summary>
/// The minimise, maximise and close buttons, in whichever form the settings ask for.
/// <para>
/// Kept as one control rather than three copies in the window so that the placement setting
/// only has to move a single element between the left and right ends of the title bar.
/// </para>
/// </summary>
public partial class WindowButtons : UserControl
{
    public static readonly StyledProperty<WindowControlsPosition> PositionProperty =
        AvaloniaProperty.Register<WindowButtons, WindowControlsPosition>(nameof(Position));

    /// <summary>
    /// Shows the close button alone. Used by the settings window, which has a fixed shape:
    /// a maximise button that stretches a column of switches across a wide screen would be
    /// an invitation to do something the window has no use for.
    /// </summary>
    public static readonly StyledProperty<bool> CloseOnlyProperty =
        AvaloniaProperty.Register<WindowButtons, bool>(nameof(CloseOnly));

    public WindowButtons()
    {
        InitializeComponent();
        ApplyPosition();
    }

    public WindowControlsPosition Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool CloseOnly
    {
        get => GetValue(CloseOnlyProperty);
        set => SetValue(CloseOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PositionProperty || change.Property == CloseOnlyProperty) ApplyPosition();
    }

    private void ApplyPosition()
    {
        var traffic = Position == WindowControlsPosition.MacOs;

        if (SquareGroup is not null) SquareGroup.IsVisible = !traffic;
        if (TrafficGroup is not null) TrafficGroup.IsVisible = traffic;

        if (SquareMinimise is not null) SquareMinimise.IsVisible = !CloseOnly;
        if (SquareMaximise is not null) SquareMaximise.IsVisible = !CloseOnly;
        if (TrafficMinimise is not null) TrafficMinimise.IsVisible = !CloseOnly;
        if (TrafficZoom is not null) TrafficZoom.IsVisible = !CloseOnly;
    }

    // Avalonia 12 usunela interfejsy korzenia wizualnego; TopLevel.GetTopLevel to nastepca.
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private void OnMinimise(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window) window.WindowState = WindowState.Minimized;
    }

    private void OnToggleMaximise(object? sender, RoutedEventArgs e)
    {
        if (Host is not { } window) return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Host?.Close();
}
