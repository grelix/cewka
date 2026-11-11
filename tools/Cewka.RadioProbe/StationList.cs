using System.Text.Json;

namespace Cewka.RadioProbe;

internal sealed record ProbeStation(
    string Id, string? Name, string? Country, IReadOnlyList<string> StreamUrls, IReadOnlyList<string> LogoUrls);

/// <summary>Lista stacji z pliku podanego w argumencie. Narzędzie nie zawiera żadnych adresów.</summary>
internal static class StationList
{
    /// <summary>Plik: cały katalog (kraje[].stacje[]), jeden kraj (stacje[]) albo lista. Katalog: wszystkie pliki JSON.</summary>
    public static IReadOnlyList<ProbeStation> Load(string path)
    {
        string[] files;
        if (Directory.Exists(path))
            files = Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal).ToArray();
        else if (File.Exists(path))
            files = [path];
        else
            throw new InvalidDataException($"Nie ma pliku ani katalogu: {path}");

        var seen = new HashSet<string>();
        var stations = new List<ProbeStation>();
        foreach (var file in files)
        {
            foreach (var station in ReadFile(file))
            {
                if (seen.Add(station.Id))
                    stations.Add(station);
            }
        }

        return stations;
    }

    public static IReadOnlyList<ProbeStation> Select(IReadOnlyList<ProbeStation> stations, string? country, int? limit)
    {
        IEnumerable<ProbeStation> selected = stations;
        if (country is not null)
            selected = selected.Where(s => string.Equals(s.Country, country, StringComparison.OrdinalIgnoreCase));
        if (limit is { } count)
            selected = selected.Take(count);
        return selected.ToList();
    }

    private static List<ProbeStation> ReadFile(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            var stations = new List<ProbeStation>();
            ReadRoot(document.RootElement, file, stations);
            return stations;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{file}: niepoprawny JSON: {e.Message}", e);
        }
    }

    private static void ReadRoot(JsonElement root, string file, List<ProbeStation> stations)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            ReadStations(root, null, file, stations);
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kraje", out var countries)
                 && countries.ValueKind == JsonValueKind.Array)
        {
            foreach (var country in countries.EnumerateArray())
            {
                if (country.ValueKind == JsonValueKind.Object && country.TryGetProperty("stacje", out var list))
                    ReadStations(list, Text(country, "kraj"), file, stations);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("stacje", out var list))
        {
            ReadStations(list, Text(root, "kraj"), file, stations);
        }
        else
        {
            throw new InvalidDataException($"{file}: brak listy stacji");
        }
    }

    /// <param name="country">Kraj z obiektu nadrzędnego, gdy stacja nie podaje własnego.</param>
    private static void ReadStations(JsonElement list, string? country, string file, List<ProbeStation> stations)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{file}: „stacje” nie jest listą");

        foreach (var station in list.EnumerateArray())
        {
            if (Text(station, "id") is not { Length: > 0 } id)
                throw new InvalidDataException($"{file}: stacja bez identyfikatora");

            stations.Add(new ProbeStation(id, Text(station, "nazwa"), Text(station, "kraj") ?? country,
                Urls(station, "strumienie"), Urls(station, "logo")));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Lista obiektów z polem url, lista adresów, pojedynczy obiekt albo adres; null to brak.</summary>
    private static List<string> Urls(JsonElement station, string name)
    {
        var urls = new List<string>();
        if (station.ValueKind != JsonValueKind.Object || !station.TryGetProperty(name, out var value))
            return urls;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AddUrl(item, urls);
        }
        else
        {
            AddUrl(value, urls);
        }

        return urls;
    }

    private static void AddUrl(JsonElement item, List<string> urls)
    {
        var url = item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "url");
        if (!string.IsNullOrEmpty(url))
            urls.Add(url);
    }
}
