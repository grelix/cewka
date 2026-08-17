using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cewka.App.ViewModels;

namespace Cewka.App.Controls;

/// <summary>
/// A row of mutually exclusive buttons, used throughout the settings window.
/// <para>
/// The same shape already appears in the main window on the limiter and normalisation
/// switches, so the settings do not introduce a second visual language for the same job:
/// every value of a setting is on screen at once and one press changes it.
/// </para>
/// </summary>
public partial class SegmentBar : UserControl
{
    public static readonly StyledProperty<SegmentGroup?> GroupProperty =
        AvaloniaProperty.Register<SegmentBar, SegmentGroup?>(nameof(Group));

    public SegmentBar() => InitializeComponent();

    public SegmentGroup? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Lista przypisywana wprost, a nie wiazaniem do wlasnej wlasnosci: krocej i bez
        // przeszukiwania drzewa przy kazdym odswiezeniu.
        if (change.Property == GroupProperty && Options is not null)
            Options.ItemsSource = Group?.Options;
    }

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SegmentOption option }) Group?.Choose(option);
    }
}
