using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// Korektor i efekty dźwiękowe. Ta sama kontrolka stoi w oknie „Korektor i efekty” i w Ustawieniach;
/// kontekstem danych jest model odtwarzacza, więc oba miejsca zmieniają te same ustawienia.
/// </summary>
public partial class SoundPanel : UserControl
{
    /// <summary>Karty jedna pod drugą (Ustawienia) zamiast obok siebie (okno korektora).</summary>
    public static readonly StyledProperty<bool> StackedProperty =
        AvaloniaProperty.Register<SoundPanel, bool>(nameof(Stacked));

    /// <summary>Opisy efektów pod nazwami; w oknie korektora są tylko w podpowiedziach.</summary>
    public static readonly StyledProperty<bool> ShowDescriptionsProperty =
        AvaloniaProperty.Register<SoundPanel, bool>(nameof(ShowDescriptions));

    public SoundPanel()
    {
        InitializeComponent();
        ApplyLayout();
    }

    public bool Stacked
    {
        get => GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    public bool ShowDescriptions
    {
        get => GetValue(ShowDescriptionsProperty);
        set => SetValue(ShowDescriptionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StackedProperty) ApplyLayout();
    }

    /// <summary>Obok siebie: korektor 60 %, efekty 40 % szerokości; jedna pod drugą: obie na całą szerokość.</summary>
    private void ApplyLayout()
    {
        Cards.ColumnDefinitions.Clear();
        Cards.RowDefinitions.Clear();
        if (Stacked)
        {
            Cards.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(EffectsCard, 0);
            Grid.SetRow(EffectsCard, 1);
        }
        else
        {
            Cards.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Star));
            Cards.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Star));
            Grid.SetColumn(EffectsCard, 1);
            Grid.SetRow(EffectsCard, 0);
        }
    }

    private void OnResetEqualiser(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.ResetEqualiser();
}
