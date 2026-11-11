using System.Text;
using Avalonia.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Jeden składnik innego autora z pliku <c>THIRD-PARTY-NOTICES.txt</c>: kto go napisał, pod jaką
/// licencją i z jakim pełnym tekstem licencji.
/// </summary>
/// <param name="Number">Numer sekcji, zgodny ze spisem na początku pliku.</param>
/// <param name="Name">Nazwa składnika z nagłówka sekcji.</param>
/// <param name="Packages">Pakiety, pliki lub składniki z wersjami, po jednym na wiersz.</param>
/// <param name="Projects">Adresy projektu, po jednym na wiersz.</param>
/// <param name="Copyright">Informacja o prawach autorskich, wiersze rozdzielone znakiem nowego wiersza.</param>
/// <param name="Licence">Nazwa licencji skrócona do pierwszego nawiasu, np. „MIT”. Pusta, gdy sekcja jej nie podaje.</param>
/// <param name="LicenceText">Pełny tekst licencji, z końcami wierszy zapisanymi jako <c>\n</c>.</param>
public sealed record NoticeComponent(
    int Number,
    string Name,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Projects,
    string Copyright,
    string Licence,
    string LicenceText)
{
    /// <summary>Pierwszy adres projektu albo pusty napis.</summary>
    public string Project => Projects.Count > 0 ? Projects[0] : string.Empty;
}

/// <summary>
/// Czyta noty o składnikach innych autorów dołączone do programu jako zasób.
///
/// <para>Plik składa się z nagłówka i spisu, a dalej z sekcji: linia z samych znaków „=”, wiersz
/// „N. Nazwa”, druga linia „=”, wiersze metadanych („Etykieta: wartość”, kontynuacja z wcięciem),
/// linia z samych znaków „-” i pełny tekst licencji aż do następnej sekcji. Nagłówek sekcji
/// rozpoznaje numeracja po kolei od 1: wiersz „2. Grant of License” w środku tekstu licencji
/// nie zaczyna nowej sekcji, bo nie stoi między liniami „=”.</para>
///
/// <para>Uszkodzony plik nie może zatrzymać okna ustawień, więc błąd kończy się pustą listą
/// i wpisem w dzienniku błędów.</para>
/// </summary>
public static class ThirdPartyNotices
{
    /// <summary>Adres zasobu dołączonego w <c>Cewka.App.csproj</c>.</summary>
    public const string ResourceUri = "avares://Cewka/Assets/Legal/THIRD-PARTY-NOTICES.txt";

    /// <summary>Najkrótsza linia z samych „=” albo „-”, jaką uznaje się za linię rozdzielającą.</summary>
    private const int MinimumRuleLength = 40;

    /// <summary>Wczytuje noty z zasobu programu. Przy błędzie zwraca pustą listę.</summary>
    public static IReadOnlyList<NoticeComponent> Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(ResourceUri));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się wczytać not o licencjach składników: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Dzieli tekst pliku na składniki. Końce wierszy mogą być <c>\n</c> albo <c>\r\n</c>.
    /// Sekcja bez linii „-” albo bez tekstu licencji jest pomijana; plik bez żadnej sekcji
    /// daje pustą listę.
    /// </summary>
    public static IReadOnlyList<NoticeComponent> Parse(string? text)
    {
        try
        {
            return ParseCore(text);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się odczytać not o licencjach składników: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Nazwa licencji do wiersza listy: pole „Licencja” do pierwszego nawiasu albo średnika, bo za
    /// nimi stoi wskazówka, w którym pliku pakietu leży tekst, a nie nazwa.
    /// </summary>
    public static string ShortLicenceName(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return string.Empty;

        var end = field.IndexOfAny(['(', ';']);
        var name = end >= 0 ? field[..end] : field;
        return name.Trim().TrimEnd(',', ' ');
    }

    private static List<NoticeComponent> ParseCore(string? text)
    {
        var result = new List<NoticeComponent>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Nagłówki sekcji: linia „=”, „N. Nazwa”, linia „=”, a numer o jeden większy od poprzedniego.
        var headers = new List<(int Line, int Number, string Name)>();
        for (var i = 0; i + 2 < lines.Length; i++)
        {
            if (!IsRule(lines[i], '=') || !IsRule(lines[i + 2], '=')) continue;
            if (!TryHeading(lines[i + 1], out var number, out var name) || number != headers.Count + 1) continue;

            headers.Add((i, number, name));
            i += 2;
        }

        for (var k = 0; k < headers.Count; k++)
        {
            var from = headers[k].Line + 3;
            var to = k + 1 < headers.Count ? headers[k + 1].Line : lines.Length;
            var component = ParseSection(headers[k].Number, headers[k].Name, lines, from, to);
            if (component is not null) result.Add(component);
        }

        return result;
    }

    private static NoticeComponent? ParseSection(int number, string name, string[] lines, int from, int to)
    {
        var rule = -1;
        for (var i = from; i < to; i++)
        {
            if (!IsRule(lines[i], '-')) continue;
            rule = i;
            break;
        }

        if (rule < 0) return null;

        var packages = new List<string>();
        var projects = new List<string>();
        var copyright = new List<string>();
        var licence = new StringBuilder();

        // Kontynuacja z wcięciem dopisuje się do ostatniego rozpoznanego pola; pozostałe
        // etykiety („Zastosowanie”, „Zawartość”) i zdania opisu między polami są pomijane.
        Field? current = null;
        for (var i = from; i < rule; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                current = null;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                if (current is { } continued) Append(continued, line.Trim(), continuation: true);
                continue;
            }

            var colon = line.IndexOf(':');
            current = colon > 0 ? FieldOf(line[..colon].Trim()) : null;
            if (current is { } field) Append(field, line[(colon + 1)..].Trim(), continuation: false);
        }

        var body = TrimBlankLines(lines, rule + 1, to);
        if (body.Length == 0) return null;

        return new NoticeComponent(
            number,
            name,
            packages,
            projects,
            string.Join('\n', copyright),
            ShortLicenceName(licence.ToString()),
            body);

        void Append(Field field, string value, bool continuation)
        {
            if (value.Length == 0) return;

            switch (field)
            {
                case Field.Package when continuation && packages.Count > 0:
                    // Wiersz kontynuacji dopowiada poprzedni wpis.
                    packages[^1] += " " + value;
                    break;
                case Field.Package:
                    packages.Add(value);
                    break;
                case Field.Project:
                    projects.Add(value);
                    break;
                case Field.Copyright:
                    copyright.Add(value);
                    break;
                case Field.Licence:
                    if (licence.Length > 0) licence.Append(' ');
                    licence.Append(value);
                    break;
            }
        }
    }

    private enum Field
    {
        Package,
        Project,
        Copyright,
        Licence,
    }

    private static Field? FieldOf(string label) => label.ToLowerInvariant() switch
    {
        "pakiet nuget" or "plik" or "pliki" or "składnik" => Field.Package,
        "projekt" => Field.Project,
        "prawa autorskie" or "autor" => Field.Copyright,
        "licencja" => Field.Licence,
        _ => null,
    };

    /// <summary>Tekst od <paramref name="from"/> do <paramref name="to"/> bez pustych wierszy na początku i na końcu.</summary>
    private static string TrimBlankLines(string[] lines, int from, int to)
    {
        while (from < to && string.IsNullOrWhiteSpace(lines[from])) from++;
        while (to > from && string.IsNullOrWhiteSpace(lines[to - 1])) to--;

        return string.Join('\n', lines[from..to].Select(line => line.TrimEnd()));
    }

    /// <summary>Linia złożona wyłącznie ze znaku <paramref name="mark"/>, dostatecznie długa na rozdzielacz.</summary>
    private static bool IsRule(string line, char mark)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= MinimumRuleLength && trimmed.All(c => c == mark);
    }

    /// <summary>Wiersz „N. Nazwa” z nagłówka sekcji.</summary>
    private static bool TryHeading(string line, out int number, out string name)
    {
        number = 0;
        name = string.Empty;

        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot <= 0 || !int.TryParse(line.AsSpan(0, dot), out number)) return false;

        name = line[(dot + 2)..].Trim();
        return name.Length > 0;
    }
}
