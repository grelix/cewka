using Cewka.App.Localisation;

namespace Cewka.App.ViewModels;

/// <summary>
/// Podkategoria okna ustawień, pozycja paska bocznego. Nazwa trzymana jako klucz, bo język zmienia się
/// w tym samym oknie.
/// </summary>
public sealed class SettingsSection(string key) : ObservableObject
{
    public string Key => key;

    public string Label => Strings.Current[key];

    private bool _isSelected;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Kategoria główna, do której należy podkategoria; ustawia ją <see cref="SettingsCategory"/>.</summary>
    public SettingsCategory? Category { get; internal set; }

    internal void RefreshLabel() => Raise(nameof(Label));
}

/// <summary>
/// Kategoria główna okna ustawień: nagłówek w pasku bocznym nad swoimi podkategoriami. Sama nie ma treści,
/// więc nie da się jej wybrać.
/// </summary>
public sealed class SettingsCategory : ObservableObject
{
    public SettingsCategory(string key, params SettingsSection[] sections)
    {
        Key = key;
        Sections = sections;
        foreach (var section in sections) section.Category = this;
    }

    public string Key { get; }

    public string Label => Strings.Current[Key];

    public IReadOnlyList<SettingsSection> Sections { get; }

    internal void RefreshLabel()
    {
        Raise(nameof(Label));
        foreach (var section in Sections) section.RefreshLabel();
    }
}
