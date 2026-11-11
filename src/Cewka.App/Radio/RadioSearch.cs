namespace Cewka.App.Radio;

/// <summary>Zapytanie do listy stacji.</summary>
/// <param name="Query">Tekst z pola wyszukiwania; pusty daje całą listę.</param>
/// <param name="SelectedCountry">Kraj wybrany na liście; jego stacje idą pierwsze.</param>
/// <param name="DefaultCountry">Kraj domyślny; jego stacje idą po wybranym.</param>
/// <param name="Category">Klucz kategorii zawężający wyniki albo null.</param>
/// <param name="CountryName">Nazwa kraju w języku interfejsu (pole przeszukiwane); null, gdy nazwy nie szukamy.</param>
/// <param name="CategoryName">Nazwa kategorii w języku interfejsu (pole przeszukiwane).</param>
public sealed record RadioSearchRequest(
    string Query,
    string? SelectedCountry = null,
    string? DefaultCountry = null,
    string? Category = null,
    Func<string, string>? CountryName = null,
    Func<string, string>? CategoryName = null);

/// <summary>
/// Wyszukiwanie lokalne we wszystkich krajach, bez interfejsu. Każde słowo zapytania musi wystąpić w nazwie, aliasie
/// łacińskim, mieście, regionie, nazwie kraju lub kategorii (bez znaków diakrytycznych; „102,2” = „102.2”).
/// </summary>
public static class RadioSearch
{
    public static IReadOnlyList<RadioStation> Run(RadioCatalog catalog, RadioSearchRequest request)
    {
        var words = TextFolding.Fold(request.Query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var alternativeWords = TextFolding.FoldAlternative(request.Query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var countryText = new Dictionary<string, string>(StringComparer.Ordinal);
        var categoryText = new Dictionary<string, string>(StringComparer.Ordinal);

        string CountryText(string code)
        {
            if (request.CountryName is null)
                return string.Empty;
            if (!countryText.TryGetValue(code, out var text))
                countryText[code] = text = TextFolding.Fold(request.CountryName(code));
            return text;
        }

        string CategoryText(string key)
        {
            if (request.CategoryName is null)
                return string.Empty;
            if (!categoryText.TryGetValue(key, out var text))
                categoryText[key] = text = TextFolding.Fold(request.CategoryName(key));
            return text;
        }

        var matches = new List<(RadioStation Station, int Group, int Position)>();
        var position = 0;
        foreach (var station in catalog.Stations)
        {
            position++;
            if (request.Category is not null && !station.Categories.Contains(request.Category))
                continue;

            if (words.Length > 0)
            {
                var index = catalog.IndexOf(station);
                var extra = CountryText(station.Country) + " " + string.Join(' ', station.Categories.Select(CategoryText));
                if (!Matches(words, index, extra) && !Matches(alternativeWords, index, extra))
                    continue;
            }

            matches.Add((station, GroupOf(station, request), position));
        }

        // Kraj wybrany, potem domyślny, potem reszta; w grupie kolejność listy (zasięg, alfabet), reszta krajami alfabetycznie.
        return
        [
            .. matches
                .OrderBy(m => m.Group)
                .ThenBy(m => m.Group == 2 ? TextFolding.SortKey(CountryName(request, m.Station.Country)) : string.Empty, StringComparer.Ordinal)
                .ThenBy(m => m.Position)
                .Select(m => m.Station),
        ];
    }

    private static bool Matches(string[] words, string index, string extra)
    {
        foreach (var word in words)
        {
            if (!index.Contains(word, StringComparison.Ordinal) && !extra.Contains(word, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int GroupOf(RadioStation station, RadioSearchRequest request) =>
        station.Country == request.SelectedCountry ? 0 : station.Country == request.DefaultCountry ? 1 : 2;

    private static string CountryName(RadioSearchRequest request, string code) => request.CountryName?.Invoke(code) ?? code;
}
