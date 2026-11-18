using Avalonia.Controls;
using Avalonia.Interactivity;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// Okienko toru sygnału pod opisem parametrów pliku albo jakości stacji. Otwiera je kliknięcie, Enter
/// i Spacja na opisie; zamyka Esc, kliknięcie obok, ponowne użycie opisu i zniknięcie opisu.
/// </summary>
public partial class MainWindow
{
    private void AttachSignalPath()
    {
        PlaceSignalPath();

        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsRadioMode):
                    PlaceSignalPath();
                    CloseSignalPathWithoutAnchor();
                    break;
                case nameof(MainViewModel.ShowsTrackQuality):
                case nameof(MainViewModel.HasFormatBadge):
                    CloseSignalPathWithoutAnchor();
                    break;
            }
        };
    }

    /// <summary>Opis, pod którym staje okienko: w radiu za napisem „Na żywo”, przy plikach za wykonawcą.</summary>
    internal Button SignalPathAnchor => _viewModel.IsRadioMode ? LiveQualityButton : TrackQualityButton;

    private void PlaceSignalPath() => SignalPathPopup.PlacementTarget = SignalPathAnchor;

    private bool IsSignalPathButton(Button button) => button == TrackQualityButton || button == LiveQualityButton;

    private void OnSignalPathClick(object? sender, RoutedEventArgs e)
    {
        PlaceSignalPath();
        _viewModel.IsSignalPathOpen = !_viewModel.IsSignalPathOpen;
        e.Handled = true;
    }

    /// <summary>Zamyka okienko; fałsz, gdy nie było otwarte.</summary>
    internal bool CloseSignalPath()
    {
        if (!_viewModel.IsSignalPathOpen) return false;

        _viewModel.IsSignalPathOpen = false;
        return true;
    }

    /// <summary>Okienko bez opisu, pod którym stoi, wisiałoby w powietrzu.</summary>
    private void CloseSignalPathWithoutAnchor()
    {
        if (!SignalPathAnchor.IsEffectivelyVisible) CloseSignalPath();
    }
}
