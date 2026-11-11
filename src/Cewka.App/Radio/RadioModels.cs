using System.Text.Json.Serialization;
using Cewka.Audio.Streaming;

namespace Cewka.App.Radio;

/// <summary>Skąd stacja pochodzi: z listy w programie, wpisana przez słuchacza albo z zaimportowanej playlisty.</summary>
public static class StationOrigin
{
    public const string Catalog = "catalog";
    public const string Typed = "typed";
    public const string Imported = "imported";
}

/// <summary>Zasięg nadawania; klucz w pliku stacji.</summary>
public static class StationScope
{
    public const string National = "national";
    public const string Online = "online";
    public const string Regional = "regional";
    public const string Local = "local";

    /// <summary>Kolejność w kraju: najpierw ogólnokrajowe i internetowe, potem regionalne i lokalne.</summary>
    public static int Rank(string? scope) => scope is National or Online ? 0 : 1;
}

/// <summary>Jeden adres strumienia stacji.</summary>
public sealed class StationStream
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Kodek: <c>mp3</c>, <c>aac</c>, <c>ogg</c> albo <c>unknown</c> (adres słuchacza: ustali się z ramek).</summary>
    public string Codec { get; set; } = "unknown";

    public int Kbps { get; set; }

    /// <summary>Lista HLS (segmenty), nie strumień ciągły.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hls { get; set; }

    [JsonIgnore]
    public LiveCodec LiveCodec => Codec switch
    {
        "mp3" => LiveCodec.Mp3,
        "aac" => LiveCodec.Aac,
        "ogg" => LiveCodec.Ogg,
        _ => LiveCodec.Unknown,
    };

    public StationStream Copy() => new() { Url = Url, Codec = Codec, Kbps = Kbps, Hls = Hls };
}

/// <summary>
/// Stacja z listy w programie albo zapisana w ulubionych i ostatnio słuchanych. Zapisana kopia jest pełna
/// (adresy, kraj, logo), więc przetrwa zmianę listy.
/// </summary>
public sealed class RadioStation
{
    /// <summary>Stały identyfikator, np. <c>pl-rmf-fm</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Kod kraju ISO 3166-1 (wielkimi literami); pusty u stacji słuchacza bez kraju.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Miasto w zapisie oryginalnym (endonim) i łacińskim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CityLatin { get; set; }

    /// <summary>Region (województwo) w zapisie oryginalnym; tylko tam, gdzie wsad ma endonim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>Klucze kategorii (<see cref="RadioCategories"/>), 1–3.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Języki nadawania, kody języków.</summary>
    public List<string> Languages { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Website { get; set; }

    /// <summary>Stacja podaje tytuł granego utworu w strumieniu.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Titles { get; set; }

    /// <summary>Adresy w kolejności z generatora: pochodzenie, HTTPS, strumień ciągły, przepływność.</summary>
    public List<StationStream> Streams { get; set; } = [];

    /// <summary>Indeks adresu zalecanego; w kolejności prób idzie pierwszy, jeśli jego kodek jest dostępny.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Recommended { get; set; }

    /// <summary>Adresy logo, zalecany pierwszy; pusta lista oznacza obraz zastępczy. Rastrowe, HTTPS.</summary>
    public List<string> Logos { get; set; } = [];

    /// <summary>Stacja ma adres logo, który program pobiera (HTTPS); bez niego stoi obraz zastępczy.</summary>
    [JsonIgnore]
    public bool HasLogoAddress => Logos.Any(IsLogoAddress);

    /// <summary>Adres logo, który program pobiera: bezwzględny, HTTPS.</summary>
    public static bool IsLogoAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    [JsonPropertyName("origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? OriginName { get; set; }

    /// <summary>Adres wpisany przez słuchacza może prowadzić do sieci lokalnej; z importu i z listy nie.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowLocalAddress { get; set; }

    [JsonIgnore]
    public string Origin => OriginName ?? StationOrigin.Catalog;

    [JsonIgnore]
    public bool IsCustom => Origin != StationOrigin.Catalog;

    /// <summary>
    /// Adresy w kolejności prób dla tego komputera: zalecany pierwszy (gdy jego kodek jest dostępny), potem
    /// pozostałe w kolejności z pliku; adresy z niedostępnym kodekiem pominięte. Nieznany kodek (adres
    /// słuchacza) wchodzi zawsze, bo ustali się z ramek.
    /// </summary>
    public IReadOnlyList<StationStream> PlayableStreams(Func<LiveCodec, bool> isPlayable)
    {
        var result = new List<StationStream>();
        if (Recommended >= 0 && Recommended < Streams.Count && Usable(Streams[Recommended], isPlayable))
            result.Add(Streams[Recommended]);

        for (var i = 0; i < Streams.Count; i++)
        {
            if (i != Recommended && Usable(Streams[i], isPlayable))
                result.Add(Streams[i]);
        }

        return result;
    }

    /// <summary>Stacja bez żadnego odtwarzalnego adresu bywa pokazana jako przygaszona.</summary>
    public bool IsPlayable(Func<LiveCodec, bool> isPlayable) => PlayableStreams(isPlayable).Count > 0;

    /// <summary>Polecenie dla silnika; null, gdy nie ma adresu do zagrania na tym komputerze.</summary>
    public LiveSource? ToLiveSource(Func<LiveCodec, bool>? isPlayable = null)
    {
        var streams = PlayableStreams(isPlayable ?? LiveSupport.IsPlayable);
        if (streams.Count == 0)
            return null;

        var urls = new List<Uri>();
        foreach (var stream in streams)
        {
            if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri))
                urls.Add(uri);
        }

        return urls.Count == 0
            ? null
            : new LiveSource(Id, Name, urls, Country.Length == 0 ? null : Country, AllowLocalAddress);
    }

    public RadioStation Copy() => new()
    {
        Id = Id,
        Name = Name,
        Country = Country,
        City = City,
        CityLatin = CityLatin,
        Region = Region,
        Scope = Scope,
        Type = Type,
        Categories = [.. Categories],
        Languages = [.. Languages],
        Website = Website,
        Titles = Titles,
        Streams = [.. Streams.Select(s => s.Copy())],
        Recommended = Recommended,
        Logos = [.. Logos],
        OriginName = OriginName,
        AllowLocalAddress = AllowLocalAddress,
    };

    private static bool Usable(StationStream stream, Func<LiveCodec, bool> isPlayable) =>
        stream.LiveCodec == LiveCodec.Unknown || isPlayable(stream.LiveCodec);
}

/// <summary>Zawartość pliku stacji w programie.</summary>
public sealed class StationCatalogFile
{
    public int Schema { get; set; } = 1;

    public List<RadioStation> Stations { get; set; } = [];

    /// <summary>Identyfikatory stacji usuniętych z listy na żądanie nadawcy; ulubione o takim identyfikatorze nie grają.</summary>
    public List<string> Withdrawn { get; set; } = [];
}
