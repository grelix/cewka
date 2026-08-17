using Cewka.App.Localisation;

namespace Cewka.App.ViewModels;

/// <summary>
/// One tab of the settings window.
///
/// <para>Nazwa trzymana jest jako klucz, nie jako gotowy tekst: język zmienia się w tym samym
/// oknie, w którym stoją zakładki, więc etykieta przepisana raz zostałaby w języku, w jakim ją
/// przepisano — i to dokładnie tam, gdzie zmiany dokonano.</para>
/// </summary>
public sealed class SettingsSection(string key) : ObservableObject
{
    public string Key => key;

    public string Label => Strings.Current[key];

    private bool _isSelected;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    internal void RefreshLabel() => Raise(nameof(Label));
}
