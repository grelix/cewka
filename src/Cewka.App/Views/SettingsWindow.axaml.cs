using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.ViewModels;
using Cewka.Platform;

namespace Cewka.App.Views;

/// <summary>
/// Okno ustawień, bez ramki jak okno główne i niemodalne, bo urządzenie czy motyw wybiera się
/// słuchając i patrząc. Z tego powodu przyjmuje skróty odtwarzania. Za pierwszym razem staje na
/// środku okna głównego, potem w zapamiętanym miejscu.
/// </summary>
public partial class SettingsWindow : AuxiliaryWindow
{
    private SettingsViewModel? _viewModel;

    /// <summary>
    /// Required by the XAML runtime loader. The window is always created with a view model
    /// from the player; without one it simply shows its own chrome and empty sections.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        UseChrome(TitleBar, Shell, ControlsLeft, ControlsRight,
            () => App.Settings.Current.SettingsWindow, geometry => App.Settings.Current.SettingsWindow = geometry);

        // Własny pasek przewijania, ten sam co przy kolejce; domyślny z motywu Fluent jest
        // szerszy i odstaje od reszty programu.
        ContentScrollBar.Target = ContentArea;
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Położenie przycisków okna zmienia się w tym oknie, więc i tutaj ma się przestawić od razu.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CurrentWindowControls)) ApplyControlsPosition();
        };

        // Wyszukiwanie zna teksty stron z okna i pozycje list z modelu.
        _viewModel.Search.Entries = () => PageEntries().Concat(viewModel.ModelSearchEntries());
        _hitTimer.Tick += (_, _) => ClearHit();
        AddHandler(KeyDownEvent, OnSearchKeys, RoutingStrategies.Tunnel);
    }

    // ================= Wyszukiwanie =================

    private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private TextBlock? _hit;

    /// <summary>
    /// Hasła ze stron: nazwy ustawień i tytuły kart razem z opisem pod nimi. Strona wie, do której
    /// podkategorii należy (Tag z kluczem); teksty są w drzewie także na niewidocznych stronach.
    /// </summary>
    private IEnumerable<SettingsSearchEntry> PageEntries()
    {
        if (_viewModel is null || ContentArea.Content is not Panel pages) yield break;

        foreach (var page in pages.Children.OfType<Control>())
        {
            if (page.Tag is not string key || _viewModel.Sections.FirstOrDefault(s => s.Key == key) is not { } section)
                continue;

            foreach (var label in page.GetLogicalDescendants().OfType<TextBlock>()
                         .Where(t => t.Classes.Contains("setting-label") || t.Classes.Contains("section-title")))
            {
                if (string.IsNullOrWhiteSpace(label.Text)) continue;

                var hints = label.Parent is Panel parent
                    ? parent.Children.OfType<TextBlock>().Where(t => t.Classes.Contains("setting-hint")).Select(t => t.Text)
                    : [];
                yield return new SettingsSearchEntry(section, label.Text, string.Join(' ', hints), label);
            }
        }
    }

    /// <summary>Wynik otwiera podkategorię, przewija do ustawienia i na chwilę wyróżnia jego nazwę.</summary>
    private void OnSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SettingsSearchResult result }) return;

        ShowResult(result);
    }

    internal void ShowResult(SettingsSearchResult result)
    {
        Select(result.Section);
        if (result.Target is null) return;

        // Pozycja listy z modelu ma kontrolkę dopiero po ułożeniu pokazanej strony, więc szuka się jej po tym.
        Dispatcher.UIThread.Post(() =>
        {
            var target = result.Target as TextBlock ?? FindLabel(result.Target, result.Label);
            if (target is null) return;

            target.BringIntoView();
            ClearHit();
            _hit = target;
            target.Classes.Add("search-hit");
            _hitTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Napis z nazwą pozycji listy na pokazanej stronie: tekst wyniku w kontrolce tej pozycji.</summary>
    private TextBlock? FindLabel(object item, string label) =>
        ContentArea.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && ReferenceEquals(text.DataContext, item))
            .OrderBy(text => text.Text == label ? 0 : 1)
            .FirstOrDefault();

    /// <summary>Wyróżniona nazwa ustawienia, dla testów.</summary>
    internal TextBlock? Hit => _hit;

    private void ClearHit()
    {
        _hitTimer.Stop();
        _hit?.Classes.Remove("search-hit");
        _hit = null;
    }

    /// <summary>Ctrl+F przenosi fokus do pola wyszukiwania; Esc w polu z tekstem czyści je zamiast zamykać okno.</summary>
    private void OnSearchKeys(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SettingsSearchBox.Focus(NavigationMethod.Tab);
            SettingsSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SettingsSearchBox.IsFocused && !string.IsNullOrEmpty(SettingsSearchBox.Text))
        {
            SettingsSearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
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

        // Przewinięcie należało do poprzedniej zakładki; następna ma się otworzyć od góry.
        ContentArea.Offset = default;
    }

    /// <summary>
    /// Wybór języka ma postać listy, bo tylu pozycji nie zmieści pasek segmentów. Grupa pod
    /// spodem jest ta sama.
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

    private void OnFitEdges(object? sender, RoutedEventArgs e) => _viewModel?.FitEdges();

    private void OnRefreshScreens(object? sender, RoutedEventArgs e) => _viewModel?.RefreshScreens();

    private void OnScreenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScreenOption screen }) _viewModel?.ChooseScreen(screen);
    }

    private void OnDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceOption device }) _viewModel?.ChooseDevice(device);
    }

    private void OnRadioClearLogos(object? sender, RoutedEventArgs e) => _viewModel?.ClearRadioLogos();

    private void OnRadioClearRecent(object? sender, RoutedEventArgs e) => _viewModel?.ClearRadioRecent();

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
}
