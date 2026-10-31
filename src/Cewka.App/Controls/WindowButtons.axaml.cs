using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cewka.App.Localisation;
using Cewka.App.Models;

namespace Cewka.App.Controls;

/// <summary>
/// The minimise, maximise and close buttons, in whichever form the settings ask for.
/// <para>
/// Kept as one control rather than three copies in the window so that the placement setting
/// only has to move a single element between the left and right ends of the title bar.
/// </para>
/// <para>
/// Przy układzie po lewej zamknięcie stoi na zewnętrznej krawędzi, jak w GNOME i macOS,
/// a kreska oddziela grupę od poleceń paska. Na pełnym ekranie przyciski znikają.
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

    private Window? _host;

    public WindowButtons()
    {
        InitializeComponent();

        // W systemie Windows przycisk maksymalizacji zgłasza się jako systemowy, co włącza
        // podpowiedź układów przyciągania po najechaniu.
        if (OperatingSystem.IsWindows())
            WindowDecorationProperties.SetElementRole(SquareMaximise, WindowDecorationsElementRole.MaximizeButton);

        ApplyPosition();
        ApplyWindowState();
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _host = TopLevel.GetTopLevel(this) as Window;
        if (_host is not null) _host.PropertyChanged += OnHostPropertyChanged;
        Strings.Current.PropertyChanged += OnLanguageChanged;
        ApplyWindowState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_host is not null) _host.PropertyChanged -= OnHostPropertyChanged;
        Strings.Current.PropertyChanged -= OnLanguageChanged;
        _host = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) ApplyWindowState();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyWindowState();

    private void ApplyPosition()
    {
        var traffic = Position == WindowControlsPosition.MacOs;
        var onLeft = Position != WindowControlsPosition.Right;

        SquareGroup.IsVisible = !traffic;
        TrafficGroup.IsVisible = traffic;

        SquareMinimise.IsVisible = !CloseOnly;
        SquareMaximise.IsVisible = !CloseOnly;
        TrafficMinimise.IsVisible = !CloseOnly;
        TrafficZoom.IsVisible = !CloseOnly;

        // Zamknięcie zawsze na zewnętrznej krawędzi paska.
        SquareGroup.Children.Clear();
        if (onLeft) SquareGroup.Children.AddRange([SquareClose, SquareMinimise, SquareMaximise]);
        else SquareGroup.Children.AddRange([SquareMinimise, SquareMaximise, SquareClose]);

        SeparatorBefore.IsVisible = !CloseOnly && !onLeft;
        SeparatorAfter.IsVisible = !CloseOnly && onLeft;
    }

    /// <summary>Ikona i podpowiedź maksymalizacji idą za stanem okna; na pełnym ekranie grupa znika.</summary>
    private void ApplyWindowState()
    {
        var state = _host?.WindowState ?? WindowState.Normal;
        var enlarged = state is WindowState.Maximized or WindowState.FullScreen;

        Root.IsVisible = state != WindowState.FullScreen;

        var tip = Strings.Current[enlarged ? "TooltipRestore" : "TooltipMaximise"];
        foreach (var button in new Control[] { SquareMaximise, TrafficZoom })
        {
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, tip);
        }

        if (this.TryFindResource(enlarged ? "IconRestoreStroke" : "IconMaximiseStroke", out var glyph)
            && glyph is Avalonia.Media.Geometry geometry)
            MaximiseGlyph.Data = geometry;
    }

    // Avalonia 12 usunela interfejsy korzenia wizualnego; TopLevel.GetTopLevel to nastepca.
    private Window? Host => _host ?? TopLevel.GetTopLevel(this) as Window;

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
