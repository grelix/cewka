using System.Text.Json;
using Cewka.App.Radio;
using Cewka.Audio.Streaming;

namespace Cewka.Tools.StationList;

/// <summary>Wynik budowy: stacje oraz ile i dlaczego odpadło.</summary>
public sealed class BuildResult
{
    public List<RadioStation> Stations { get; } = [];

    /// <summary>Stacje, które odpadły, według pierwszego powodu.</summary>
    public SortedDictionary<string, int> DroppedStations { get; } = new(StringComparer.Ordinal);

    /// <summary>Pojedyncze adresy odrzucone w stacjach, które zostały albo odpadły.</summary>
    public SortedDictionary<string, int> DroppedAddresses { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, int> DroppedLogos { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    public int Read { get; set; }

    public void Drop(string reason) => DroppedStations[reason] = DroppedStations.GetValueOrDefault(reason) + 1;
}

/// <summary>
/// Zamienia materiał rozpoznania (<c>stacje-v2/*.json</c>) w stacje programu. Materiał zostaje poza repozytorium;
/// przechodzą tylko pola potrzebne programowi, bez popularności, uwag prawnych, pochodzenia i grupy regulaminu.
/// Odpadają stacje, które nie spełniają reguł z założeń: bez Rosji, Białorusi i rosyjskiego, z listy blokad, bez
/// dopuszczonego adresu. Adresy: tylko http(s), bez elementów zakazanych, pochodzenie „oficjalny” i pokrewne, a
/// „wywnioskowany” i „niepotwierdzony” tylko jako zapas za adresem wyższego stopnia.
/// </summary>
public static class CatalogBuilder
{
    private const int Spare = 2;

    private static readonly HashSet<string> RussianLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ru", "rus", "russian", "русский", "rossiyskiy", "russkiy",
    };

    private static readonly HashSet<string> BannedCountries = new(StringComparer.OrdinalIgnoreCase) { "RU", "BY" };

    private static readonly HashSet<string> RasterFormats = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "webp", "gif" };

    private static readonly Dictionary<string, string> PolishCategories = new(StringComparer.Ordinal)
    {
        ["Przeboje"] = RadioCategories.Hits,
        ["Informacje i rozmowy"] = RadioCategories.NewsTalk,
        ["Muzyka rodzima"] = RadioCategories.NationalMusic,
        ["Rock i alternatywa"] = RadioCategories.Rock,
        ["Kultura"] = RadioCategories.Culture,
        ["Taneczna i hip-hop"] = RadioCategories.Dance,
        ["Klasyczna i jazz"] = RadioCategories.ClassicalJazz,
        ["Dawne przeboje"] = RadioCategories.Oldies,
        ["Sport"] = RadioCategories.Sports,
        ["Religia"] = RadioCategories.Religion,
        ["Dla dzieci"] = RadioCategories.Kids,
    };

    public static BuildResult Build(IEnumerable<string> countryFiles, RadioBlocklist? blocklist = null)
    {
        blocklist ??= RadioBlocklist.BuiltIn;
        var result = new BuildResult();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in countryFiles)
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var country = Str(root, "kraj")?.ToUpperInvariant() ?? string.Empty;
            if (!root.TryGetProperty("stacje", out var stations))
                continue;

            foreach (var element in stations.EnumerateArray())
            {
                result.Read++;
                var station = BuildStation(element, country, blocklist, result);
                if (station is null)
                    continue;

                if (!ids.Add(station.Id))
                {
                    result.Drop("powtórzony identyfikator");
                    continue;
                }

                result.Stations.Add(station);
            }
        }

        result.Stations.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Country, b.Country);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return result;
    }

    private static RadioStation? BuildStation(JsonElement e, string country, RadioBlocklist blocklist, BuildResult result)
    {
        var id = Str(e, "id") ?? string.Empty;
        var name = Str(e, "nazwa") ?? string.Empty;
        if (id.Length == 0 || name.Length == 0)
        {
            result.Drop("brak identyfikatora lub nazwy");
            return null;
        }

        if (BannedCountries.Contains(country))
        {
            result.Drop("kraj RU lub BY");
            return null;
        }

        var languages = Strings(e, "jezyki");
        if (languages.Any(RussianLanguages.Contains))
        {
            result.Drop("język rosyjski");
            return null;
        }

        if (e.TryGetProperty("sankcje", out var sanctions) && Str(sanctions, "wynik") != "brak trafień")
        {
            result.Drop("sankcje: wynik inny niż brak trafień");
            return null;
        }

        if (e.TryGetProperty("ocena_prawna", out var legal) && Str(legal, "wniosek") != "dopuszczona")
        {
            result.Drop("ocena prawna: wniosek inny niż dopuszczona");
            return null;
        }

        if (blocklist.IsBlockedName(name))
        {
            result.Drop("nazwa z listy blokad");
            return null;
        }

        var categories = new List<string>();
        foreach (var format in Strings(e, "format"))
        {
            if (RadioCategories.FromFormat(format) is { } key)
            {
                if (!categories.Contains(key))
                    categories.Add(key);
            }
            else
            {
                result.Warnings.Add($"{id}: nieznany format „{format}”");
            }
        }

        if (categories.Count == 0)
        {
            result.Drop("brak kategorii");
            return null;
        }

        CheckCategories(e, id, categories, result);

        var recommended = e.TryGetProperty("zalecany_strumien", out var rec) && rec.ValueKind == JsonValueKind.Number ? rec.GetInt32() : 0;
        var streams = BuildStreams(e, recommended, blocklist, result, out var newRecommended, out var anyTrusted);
        if (streams.Count == 0)
        {
            result.Drop("brak dopuszczonego adresu");
            return null;
        }

        if (!anyTrusted)
        {
            result.Drop("adresy tylko zapasowe (wywnioskowane lub niepotwierdzone)");
            return null;
        }

        var station = new RadioStation
        {
            Id = id,
            Name = name,
            Country = country,
            Scope = Str(e, "zasieg"),
            Type = Str(e, "typ"),
            Categories = categories,
            Languages = languages,
            Website = Str(e, "strona"),
            Streams = streams.Select(s => s.Stream).ToList(),
            Recommended = newRecommended,
            Titles = streams.Any(s => s.Titles),
        };

        if (e.TryGetProperty("miasto", out var city) && city.ValueKind == JsonValueKind.Object)
        {
            station.City = Str(city, "nazwa");
            var latin = Str(city, "lacinska");
            station.CityLatin = latin == station.City ? null : latin;
        }

        // Pole „region” wsadu to w większości polska notatka badawcza („Nowy Jork”); endonim jest tylko w Polsce.
        if (country == "PL")
            station.Region = Str(e, "region");

        station.Logos = BuildLogos(e, blocklist, result);
        return station;
    }

    private static void CheckCategories(JsonElement e, string id, List<string> categories, BuildResult result)
    {
        var declared = Strings(e, "kategorie").Select(c => PolishCategories.GetValueOrDefault(c)).Where(c => c is not null).ToList();
        if (!declared.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(categories.OrderBy(c => c, StringComparer.Ordinal)))
            result.Warnings.Add($"{id}: kategorie ze wsadu różnią się od wyliczonych z formatów");
    }

    private sealed record Candidate(StationStream Stream, int Tier, bool Https, int Original, bool Titles);

    private static List<Candidate> BuildStreams(
        JsonElement e, int recommended, RadioBlocklist blocklist, BuildResult result, out int newRecommended, out bool anyTrusted)
    {
        var candidates = new List<Candidate>();
        newRecommended = 0;
        anyTrusted = false;
        if (!e.TryGetProperty("strumienie", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return candidates;

        var index = -1;
        foreach (var s in streams.EnumerateArray())
        {
            index++;
            var url = Str(s, "url") ?? string.Empty;
            if (Str(s, "wynik") != "ok")
            {
                DropAddress(result, "wynik sprawdzenia inny niż ok");
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                DropAddress(result, "adres nie jest http(s)");
                continue;
            }

            if (blocklist.IsBlockedHost(uri.IdnHost))
            {
                DropAddress(result, "domena z listy blokad");
                continue;
            }

            if (ForbiddenUrlParts.Find(uri).Count > 0)
            {
                DropAddress(result, "element zakazany w adresie");
                continue;
            }

            var tier = Tier(Str(s, "pochodzenie"));
            if (tier < 0)
            {
                DropAddress(result, "pochodzenie niedopuszczone (pośrednik lub nieznane)");
                continue;
            }

            var hls = Str(s, "protokol") == "hls" || Str(s, "kodek") == "HLS";
            var stream = new StationStream
            {
                Url = url,
                Codec = Codec(Str(s, "kodek"), Str(s, "kontener_hls")),
                Kbps = s.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                Hls = hls,
            };
            var titles = s.TryGetProperty("icy_metadane", out var icy) && icy.ValueKind == JsonValueKind.True;
            candidates.Add(new Candidate(stream, tier, uri.Scheme == "https", index, titles));
        }

        anyTrusted = candidates.Any(c => c.Tier < Spare);

        // Pochodzenie, HTTPS przed HTTP, strumień ciągły przed HLS, wyższa przepływność; reszta wg wsadu.
        var ordered = candidates
            .OrderBy(c => c.Tier)
            .ThenBy(c => c.Https ? 0 : 1)
            .ThenBy(c => c.Stream.Hls ? 1 : 0)
            .ThenByDescending(c => c.Stream.Kbps)
            .ThenBy(c => c.Original)
            .ToList();

        var position = ordered.FindIndex(c => c.Original == recommended);
        newRecommended = position < 0 ? 0 : position;
        return ordered;
    }

    private static List<string> BuildLogos(JsonElement e, RadioBlocklist blocklist, BuildResult result)
    {
        var logos = new List<string>();

        // Wymóg zgody na kopiowanie treści serwisu: obraz zastępczy (decyzja z rozpoznania).
        if (e.TryGetProperty("logo_wymaga_zgody", out var consent) && consent.ValueKind != JsonValueKind.False)
        {
            if (e.TryGetProperty("logo", out var any) && any.ValueKind == JsonValueKind.Array && any.GetArrayLength() > 0)
                DropLogo(result, "wymóg zgody nadawcy");
            return logos;
        }

        if (!e.TryGetProperty("zalecane_logo", out var recommended) || recommended.ValueKind != JsonValueKind.Number
            || !e.TryGetProperty("logo", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return logos;
        }

        var items = list.EnumerateArray().ToList();
        var first = recommended.GetInt32();
        var order = new List<int> { first };
        order.AddRange(Enumerable.Range(0, items.Count).Where(i => i != first));
        foreach (var i in order)
        {
            if (i < 0 || i >= items.Count)
                continue;

            var url = Str(items[i], "url") ?? string.Empty;
            if (Str(items[i], "wynik") != "ok" || !RasterFormats.Contains(Str(items[i], "format") ?? string.Empty))
            {
                DropLogo(result, "format nierastrowy lub niesprawdzony");
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || blocklist.IsBlockedHost(uri.IdnHost))
            {
                DropLogo(result, "adres niedopuszczony (nie https lub z listy blokad)");
                continue;
            }

            if (!logos.Contains(url))
                logos.Add(url);
        }

        return logos;
    }

    /// <summary>0 – oficjalny; 1 – domena i serwer nadawcy, dostawca z oficjalnego odtwarzacza; 2 – zapas; -1 – nigdy.</summary>
    private static int Tier(string? origin) => origin switch
    {
        "oficjalny" => 0,
        "domena-nadawcy" or "serwer nadawcy" or "dostawca strumieni oficjalnego odtwarzacza" => 1,
        "wywnioskowany" or "wywnioskowany (zapas)" or "niepotwierdzony" or "zapas, niepotwierdzony" => Spare,
        _ => -1,
    };

    private static string Codec(string? codec, string? container) => codec switch
    {
        "MP3" => "mp3",
        "AAC" or "HE-AAC" => "aac",
        "OGG-VORBIS" => "ogg",
        "HLS" => container switch { "ADTS" or "TS-AAC" => "aac", "TS-MP3" => "mp3", _ => "unknown" },
        _ => "unknown",
    };

    private static void DropAddress(BuildResult result, string reason) =>
        result.DroppedAddresses[reason] = result.DroppedAddresses.GetValueOrDefault(reason) + 1;

    private static void DropLogo(BuildResult result, string reason) =>
        result.DroppedLogos[reason] = result.DroppedLogos.GetValueOrDefault(reason) + 1;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}
