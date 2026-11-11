namespace Cewka.App.Radio;

/// <summary>
/// Jedenaście kategorii stacji. Tu są tylko klucze; nazwy w językach interfejsu to klucze plików języków.
/// Kategorie wynikają z formatów stacji we wsadzie (jedna stacja ma 1–3 formaty, więc 1–3 kategorie).
/// </summary>
public static class RadioCategories
{
    public const string Hits = "hits";
    public const string NewsTalk = "news-talk";
    public const string NationalMusic = "national-music";
    public const string Rock = "rock";
    public const string Culture = "culture";
    public const string Dance = "dance";
    public const string ClassicalJazz = "classical-jazz";
    public const string Oldies = "oldies";
    public const string Sports = "sports";
    public const string Religion = "religion";
    public const string Kids = "kids";

    /// <summary>Kolejność wyświetlania w rzędzie przycisków.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Hits, NewsTalk, NationalMusic, Rock, Culture, Dance, ClassicalJazz, Oldies, Sports, Religion, Kids,
    ];

    private static readonly Dictionary<string, string> ByFormat = new(StringComparer.Ordinal)
    {
        ["hits"] = Hits, ["adult-contemporary"] = Hits, ["country"] = Hits,
        ["news"] = NewsTalk, ["talk"] = NewsTalk, ["full-service"] = NewsTalk,
        ["national-popular"] = NationalMusic,
        ["rock"] = Rock, ["alternative"] = Rock,
        ["culture"] = Culture,
        ["dance"] = Dance, ["hip-hop"] = Dance,
        ["classical"] = ClassicalJazz, ["jazz"] = ClassicalJazz,
        ["oldies"] = Oldies,
        ["sports"] = Sports,
        ["religious"] = Religion,
        ["kids"] = Kids,
    };

    /// <summary>Klucz kategorii dla formatu ze wsadu; null dla formatu nieznanego.</summary>
    public static string? FromFormat(string format) => ByFormat.GetValueOrDefault(format);

    public static bool IsKnown(string key) => All.Contains(key);
}
