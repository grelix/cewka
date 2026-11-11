using System.Collections.ObjectModel;
using Cewka.App.Radio;

namespace Cewka.App.ViewModels;

/// <summary>Hasło wyszukiwania ustawień: nazwa ustawienia, tekst do dopasowania i miejsce w oknie.</summary>
/// <param name="Target">Element strony, do którego okno przewija po wybraniu wyniku; null — początek strony.</param>
public sealed record SettingsSearchEntry(SettingsSection Section, string Label, string Text, object? Target = null);

/// <summary>Wynik wyszukiwania w pasku bocznym: nazwa ustawienia i ścieżka „Kategoria › Podkategoria”.</summary>
public sealed class SettingsSearchResult(SettingsSearchEntry entry)
{
    public SettingsSection Section => entry.Section;

    public string Label => entry.Label;

    public string Path => $"{entry.Section.Category?.Label} › {entry.Section.Label}";

    public object? Target => entry.Target;
}

/// <summary>
/// Wyszukiwanie w ustawieniach jak w macOS i Windows: każde słowo zapytania musi wystąpić w nazwie,
/// opisie albo nazwie podkategorii, bez względu na wielkość liter i znaki diakrytyczne. Wyniki stoją
/// w kolejności podkategorii, bez powtórzeń.
/// </summary>
public sealed class SettingsSearch : ObservableObject
{
    /// <summary>Najwięcej wyników na liście; dłuższa lista nie pomaga znaleźć ustawienia.</summary>
    public const int MaxResults = 40;

    private readonly IReadOnlyList<SettingsSection> _sections;
    private string _text = string.Empty;

    public SettingsSearch(IReadOnlyList<SettingsSection> sections) => _sections = sections;

    /// <summary>Hasła z okna (teksty stron); okno podaje je po wczytaniu, model dokłada pozycje list.</summary>
    public Func<IEnumerable<SettingsSearchEntry>> Entries { get; set; } = () => [];

    public ObservableCollection<SettingsSearchResult> Results { get; } = [];

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? string.Empty)) return;

            Run();
            Raise(nameof(IsActive));
        }
    }

    /// <summary>Zapytanie nie jest puste: pasek boczny pokazuje wyniki zamiast kategorii.</summary>
    public bool IsActive => _text.Trim().Length > 0;

    public bool HasNoResults => IsActive && Results.Count == 0;

    /// <summary>Szuka od nowa, np. po zmianie języka, gdy zmieniły się teksty.</summary>
    public void Run()
    {
        Results.Clear();
        var words = TextFolding.Fold(_text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            var order = _sections.Select((section, index) => (section, index)).ToDictionary(p => p.section, p => p.index);
            var seen = new HashSet<(SettingsSection, string)>();
            var matches = Entries()
                .Concat(_sections.Select(section => new SettingsSearchEntry(section, section.Label, section.Category?.Label ?? string.Empty)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Label))
                .Where(entry =>
                {
                    var haystack = TextFolding.Fold(
                        $"{entry.Label} {entry.Text} {entry.Section.Label} {entry.Section.Category?.Label}");
                    return words.All(word => haystack.Contains(word, StringComparison.Ordinal));
                })
                .OrderBy(entry => order.GetValueOrDefault(entry.Section, int.MaxValue))
                // Nazwa podkategorii przed jej ustawieniami.
                .ThenBy(entry => entry.Label == entry.Section.Label ? 0 : 1);

            foreach (var entry in matches)
            {
                if (!seen.Add((entry.Section, entry.Label))) continue;

                Results.Add(new SettingsSearchResult(entry));
                if (Results.Count == MaxResults) break;
            }
        }

        Raise(nameof(HasNoResults));
    }

    /// <summary>
    /// Po zmianie języka teksty haseł są inne, więc otwarte wyniki liczą się od nowa — dopiero po tym, jak
    /// kontrolki i pozycje list dostaną nowe teksty, bo powiadomienie o języku przychodzi najpierw tutaj.
    /// </summary>
    internal void RefreshLabels()
    {
        if (IsActive) Avalonia.Threading.Dispatcher.UIThread.Post(Run, Avalonia.Threading.DispatcherPriority.Background);
    }
}
