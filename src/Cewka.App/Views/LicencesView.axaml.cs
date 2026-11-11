using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.ViewModels;
using Cewka.Platform;

namespace Cewka.App.Views;

/// <summary>
/// Licencje składników: lista składników i szczegóły wybranego w tym samym miejscu.
/// Enter i kliknięcie otwierają szczegóły; Esc i „Wstecz” wracają do listy z fokusem na wierszu,
/// z którego szczegóły otwarto. Esc na samej liście zostaje dla okna ustawień, które się zamyka.
/// </summary>
public partial class LicencesView : UserControl
{
    /// <summary>Widok z notami dołączonymi do programu.</summary>
    public LicencesView() : this(new LicencesViewModel())
    {
    }

    public LicencesView(LicencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Lista sama zużywa Enter i Spację przy przesuwaniu zaznaczenia, więc handler widzi też zużyte.
        LicenceList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private LicencesViewModel? Model => DataContext as LicencesViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && Model is { IsDetailVisible: true })
        {
            GoBack(viaKeyboard: true);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None) return;

        // Wiersz z fokusem, a gdy fokus stoi na samej liście, zaznaczony.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var item = focused?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LicenceItem
                   ?? LicenceList.SelectedItem as LicenceItem;
        if (item is null) return;

        Show(item, viaKeyboard: true);
        e.Handled = true;
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is not LicenceItem item) return;

        Show(item, viaKeyboard: false);
        e.Handled = true;
    }

    private void OnBack(object? sender, RoutedEventArgs e) =>
        GoBack(viaKeyboard: BackButton.Classes.Contains(":focus-visible"));

    private void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectLink link }) WebLink.Open(link.Address);
    }

    private void Show(LicenceItem item, bool viaKeyboard)
    {
        if (Model is not { } model) return;

        model.Open(item);
        LicenceList.SelectedItem = item;

        // Wiersz znika razem z listą, więc fokus przechodzi na „Wstecz” po zbudowaniu szczegółów.
        FocusAfterLayout(() => BackButton, viaKeyboard);
    }

    private void GoBack(bool viaKeyboard)
    {
        if (Model is not { } model) return;

        model.Back();
        var index = IndexOf(model, model.LastOpened);

        FocusAfterLayout(() =>
        {
            if (index < 0) return null;

            LicenceList.SelectedIndex = index;
            LicenceList.ScrollIntoView(index);
            return LicenceList.ContainerFromIndex(index);
        }, viaKeyboard);
    }

    private static int IndexOf(LicencesViewModel model, LicenceItem? item)
    {
        if (item is null) return -1;

        for (var i = 0; i < model.Items.Count; i++)
        {
            if (ReferenceEquals(model.Items[i], item)) return i;
        }

        return -1;
    }

    /// <summary>
    /// Ustawia fokus dopiero po przebudowie układu: element, który przed chwilą był ukryty,
    /// nie przyjmie go od razu. Pierścień fokusu widać tylko po obsłudze z klawiatury.
    /// </summary>
    private static void FocusAfterLayout(Func<Control?> target, bool viaKeyboard) =>
        Dispatcher.UIThread.Post(
            () => target()?.Focus(viaKeyboard ? NavigationMethod.Tab : NavigationMethod.Unspecified),
            DispatcherPriority.Loaded);
}
