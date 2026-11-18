using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;
using Cewka.App.ViewModels;
using Cewka.Audio.Dsp;

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

    /// <summary>W Ustawieniach karta ma całą szerokość i może być wyższa: dziesięć wierszy bez przewijania.</summary>
    internal const double StackedListHeight = 320;

    public SoundPanel()
    {
        InitializeComponent();
        BandScrollBar.Target = BandScroll;

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

        FitBandList();
    }

    /// <summary>Wysokość wiersza pasma i odstęp między wierszami, jak w szablonie listy.</summary>
    internal const double BandRowHeight = 28;

    internal const double BandRowSpacing = 4;

    /// <summary>W oknie korektora lista pokazuje tyle pełnych wierszy bez przewijania.</summary>
    internal const int SideBySideRows = 6;

    /// <summary>
    /// W oknie korektora miejsce na listę pasm ma stałą wysokość sześciu wierszy, niezależnie od ich
    /// liczby: dodanie i usunięcie pasma nie zmienia wysokości okna, które dopasowuje ją do treści.
    /// Karta korektora jest wtedy wyższa od karty efektów, która rozciąga się do niej z wolnym miejscem
    /// na dole. W Ustawieniach lista rośnie z liczbą pasm do dziesięciu wierszy.
    /// </summary>
    private void FitBandList()
    {
        if (Stacked)
        {
            BandArea.Height = double.NaN;
            BandScroll.MaxHeight = StackedListHeight;
            return;
        }

        BandArea.Height = SideBySideRows * BandRowHeight + (SideBySideRows - 1) * BandRowSpacing;
        BandScroll.MaxHeight = double.PositiveInfinity;
    }
    private MainViewModel? Player => DataContext as MainViewModel;

    private void OnResetEqualiser(object? sender, RoutedEventArgs e) => Player?.ResetEqualiser();

    private void OnAddBand(object? sender, RoutedEventArgs e)
    {
        if (Player is not { } player) return;

        player.Parametric.AddBand();

        // Nowe pasmo stoi na końcu listy: widać je od razu, a fokus przechodzi do jego częstotliwości.
        Dispatcher.UIThread.Post(() =>
        {
            BandScroll.ScrollToEnd();
            if (ParametricBands.ContainerFromIndex(player.Parametric.Bands.Count - 1) is { } row)
                row.GetVisualDescendants().OfType<Controls.NumberField>().FirstOrDefault()?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnRemoveBand(object? sender, RoutedEventArgs e)
    {
        if (Player is not { } player || sender is not Control { DataContext: ParametricBandViewModel band }) return;

        var index = player.Parametric.Bands.IndexOf(band);
        player.Parametric.RemoveBand(band);

        // Fokus nie znika z usuniętym wierszem: idzie do przycisku usunięcia w wierszu, który zajął
        // jego miejsce, a przy pustej liście do „Dodaj pasmo”.
        Dispatcher.UIThread.Post(() =>
        {
            var count = player.Parametric.Bands.Count;
            if (count > 0 && ParametricBands.ContainerFromIndex(Math.Min(index, count - 1)) is { } row
                && row.GetVisualDescendants().OfType<Button>().LastOrDefault() is { } remove)
                remove.Focus();
            else
                AddBandButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnFilterType(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ParametricBandViewModel band, Tag: ParametricFilterType type })
            band.Type = type;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Player is not { } player || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Current["EqualiserImportTitle"],
                AllowMultiple = false,
                FileTypeFilter = [FileFilter()],
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) player.ImportEqualiser(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] wybór pliku korektora: {ex.Message}");
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Player is not { } player || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Current["EqualiserExportTitle"],
                SuggestedFileName = "ParametricEQ.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [FileFilter()],
            });

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) player.ExportEqualiser(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] wybór pliku korektora: {ex.Message}");
        }
    }

    private static FilePickerFileType FileFilter() => new(Strings.Current["EqualiserFileFilter"]) { Patterns = ["*.txt"] };
}
