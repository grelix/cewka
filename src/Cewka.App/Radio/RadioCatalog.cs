using System.Text.Json;
using Avalonia.Platform;

namespace Cewka.App.Radio;

/// <summary>Kraj z liczbą stacji; nazwę dobiera wywołujący z plików języków.</summary>
public sealed record CountryEntry(string Code, string Name, int Count);

/// <summary>Kategoria z liczbą stacji w wybranym zakresie.</summary>
public sealed record CategoryEntry(string Key, int Count);

/// <summary>
/// Lista stacji w programie (<c>Radio/stations.json</c>): kraje, kategorie, stacje i klucze wyszukiwania.
/// Stacje w kraju: najpierw ogólnokrajowe i internetowe, potem regionalne i lokalne, w grupie alfabetycznie.
/// </summary>
public sealed class RadioCatalog
{
    private static readonly Dictionary<string, string[]> LanguageCountries = new(StringComparer.Ordinal)
    {
        ["pl"] = ["PL"], ["cs"] = ["CZ"], ["de"] = ["DE"], ["el"] = ["GR"], ["en"] = ["GB", "US"], ["es"] = ["ES"],
        ["fr"] = ["FR"], ["hu"] = ["HU"], ["id"] = ["ID"], ["it"] = ["IT"], ["nl"] = ["NL"], ["pt"] = ["PT", "BR"],
        ["pt-br"] = ["BR", "PT"], ["ro"] = ["RO"], ["ru"] = ["GB"], ["tr"] = ["TR"], ["uk"] = ["UA"], ["vi"] = ["VN"],
    };

    private readonly Dictionary<string, RadioStation> _byId;
    private readonly Dictionary<string, List<RadioStation>> _byCountry;
    private readonly Dictionary<RadioStation, string> _index;
    private readonly HashSet<string> _repeatedNames;

    private readonly HashSet<string> _withdrawn;

    public RadioCatalog(IEnumerable<RadioStation> stations, IEnumerable<string>? withdrawn = null)
    {
        _withdrawn = [.. withdrawn ?? []];
        // W kraju najpierw stacje z logo, potem bez; wyszukiwanie dziedziczy tę kolejność przez pozycję w katalogu.
        Stations = [.. stations.OrderBy(s => s.Country, StringComparer.Ordinal)
            .ThenBy(s => s.HasLogoAddress ? 0 : 1)
            .ThenBy(s => StationScope.Rank(s.Scope))
            .ThenBy(s => TextFolding.SortKey(s.Name), StringComparer.Ordinal)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
        _byId = Stations.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _byCountry = Stations.GroupBy(s => s.Country, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _index = Stations.ToDictionary(s => s, BuildIndex);
        _repeatedNames = [.. Stations.GroupBy(s => TextFolding.Fold(StripFrequency(s.Name)))
            .Where(g => g.Select(s => s.Country).Distinct().Count() > 1).Select(g => g.Key)];
    }

    public IReadOnlyList<RadioStation> Stations { get; }

    /// <summary>Kody krajów mające stacje, alfabetycznie po kodzie.</summary>
    public IReadOnlyList<string> CountryCodes => [.. _byCountry.Keys.OrderBy(c => c, StringComparer.Ordinal)];

    public static RadioCatalog Load(Stream stream)
    {
        var file = JsonSerializer.Deserialize(stream, RadioJsonContext.Default.StationCatalogFile)
            ?? throw new InvalidDataException("Plik stacji jest pusty.");
        return new RadioCatalog(file.Stations, file.Withdrawn);
    }

    public static RadioCatalog LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Lista wbudowana w program (zasób aplikacji); wymaga zainicjowanego środowiska Avalonii.</summary>
    public static RadioCatalog LoadBuiltIn()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Cewka/Radio/stations.json"));
        return Load(stream);
    }

    /// <summary>Stacja usunięta z listy na żądanie nadawcy (decyzja 12.2); zostaje w ulubionych, ale nie gra.</summary>
    public bool IsWithdrawn(string id) => _withdrawn.Contains(id);

    public RadioStation? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<RadioStation> InCountry(string code) =>
        _byCountry.TryGetValue(code, out var list) ? list : [];

    /// <summary>Stacje kraju (albo wszystkich, gdy <paramref name="country"/> to null) z kategorii; kolejność jak na liście.</summary>
    public IReadOnlyList<RadioStation> List(string? country, string? category = null) =>
        [.. (country is null ? Stations : InCountry(country)).Where(s => category is null || s.Categories.Contains(category))];

    /// <summary>
    /// Kraje do przycisku wyboru: kraj domyślny pierwszy, potem pozostałe alfabetycznie według nazw
    /// w języku interfejsu (klucz bez znaków diakrytycznych). „Ulubione” i „Ostatnio słuchane” dokłada interfejs.
    /// </summary>
    public IReadOnlyList<CountryEntry> Countries(Func<string, string> displayName, string? defaultCountry)
    {
        var entries = _byCountry.Select(p => new CountryEntry(p.Key, displayName(p.Key), p.Value.Count)).ToList();
        return
        [
            .. entries.Where(e => e.Code == defaultCountry),
            .. entries.Where(e => e.Code != defaultCountry)
                .OrderBy(e => TextFolding.SortKey(e.Name), StringComparer.Ordinal).ThenBy(e => e.Code, StringComparer.Ordinal),
        ];
    }

    /// <summary>Kategorie obecne w kraju (albo we wszystkich krajach), w stałej kolejności, bez progu liczebności.</summary>
    public IReadOnlyList<CategoryEntry> Categories(string? country = null)
    {
        var source = country is null ? Stations : InCountry(country);
        return
        [
            .. RadioCategories.All
                .Select(key => new CategoryEntry(key, source.Count(s => s.Categories.Contains(key))))
                .Where(e => e.Count > 0),
        ];
    }

    /// <summary>
    /// Kraj startowy: (1) ostatni wybór, jeśli kraj jest na liście; (2) lokalizacja domowa, jeśli ma stacje w języku
    /// interfejsu; (3) kraj przypisany do języka (interfejs rosyjski otwiera Wielką Brytanię).
    /// </summary>
    public string? DefaultCountry(string uiLanguage, string? homeRegion = null, string? lastChoice = null)
    {
        if (lastChoice is not null && _byCountry.ContainsKey(lastChoice))
            return lastChoice;

        var language = uiLanguage.Split('-', '_')[0].ToLowerInvariant();
        var home = homeRegion?.Trim().ToUpperInvariant();
        if (language != "ru" && home is { Length: 2 } && InCountry(home).Count(s => s.Languages.Contains(language)) >= 3)
            return home;

        if (LanguageCountries.TryGetValue(language, out var countries))
            return countries.FirstOrDefault(_byCountry.ContainsKey);

        return null;
    }

    /// <summary>
    /// Nazwa do listy, wyników, panelu systemu i eksportu: kraj w nawiasie zawsze, gdy stacja ma kraj
    /// (decyzja 11.3); <paramref name="countryName"/> podaje nazwę kraju w języku interfejsu.
    /// </summary>
    public string DisplayName(RadioStation station, Func<string, string> countryName) =>
        station.Country.Length > 0 ? $"{station.Name} ({countryName(station.Country)})" : station.Name;

    public bool IsNameRepeated(RadioStation station) => _repeatedNames.Contains(TextFolding.Fold(StripFrequency(station.Name)));

    internal string IndexOf(RadioStation station) =>
        _index.TryGetValue(station, out var text) ? text : BuildIndex(station);

    private static string BuildIndex(RadioStation station)
    {
        var parts = new List<string>
        {
            station.Name, TextFolding.FoldAlternative(station.Name), station.City ?? string.Empty,
            station.CityLatin ?? string.Empty, station.Region ?? string.Empty,
        };
        return string.Join(' ', parts.Select(TextFolding.Fold).Where(p => p.Length > 0).Distinct());
    }

    private static string StripFrequency(string name)
    {
        // „Radio Zet 107.5” i „Radio Zet” to ta sama nazwa; częstotliwość liczy się osobno.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[^1].Length > 0 && char.IsAsciiDigit(words[^1][0]) && words[^1].All(c => char.IsAsciiDigit(c) || c is '.' or ','))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }
}
