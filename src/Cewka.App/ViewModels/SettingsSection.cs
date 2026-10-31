using Cewka.App.Localisation;

namespace Cewka.App.ViewModels;

/// <summary>
/// Zakładka okna ustawień. Nazwa trzymana jako klucz, bo język zmienia się w tym samym oknie.
/// </summary>
public sealed class SettingsSection(string key) : ObservableObject
{
    public string Key => key;

    public string Label => Strings.Current[key];

    private bool _isSelected;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    internal void RefreshLabel() => Raise(nameof(Label));
}
