using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cewka.App.ViewModels;

namespace Cewka.App.Controls;

/// <summary>
/// Rząd wzajemnie wykluczających się przycisków w oknie ustawień.
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

        // Lista przypisywana wprost, bez wiązania, żeby nie przeszukiwać drzewa przy każdym
        // odświeżeniu.
        if (change.Property == GroupProperty && Options is not null)
            Options.ItemsSource = Group?.Options;
    }

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SegmentOption option }) Group?.Choose(option);
    }
}
