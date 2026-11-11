using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Cewka.App.Radio;

/// <summary>Widok listy stacji z poprzedniego uruchomienia.</summary>
public sealed class RadioListContext
{
    public const string ViewCountry = "country";
    public const string ViewAll = "all";
    public const string ViewFavourites = "favourites";
    public const string ViewRecent = "recent";

    public string View { get; set; } = ViewCountry;

    /// <summary>Ostatnio wybrany kraj; pusty, gdy użytkownik jeszcze nie wybierał.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; set; }

    /// <summary>Klucz kategorii albo null dla „Wszystkie”.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }
}

/// <summary>
/// Stan radia z <c>radio.json</c>: ulubione (pełne kopie, ręczna kolejność), ostatnio słuchane, ostatnia stacja
/// i widok listy. Zmiany idą przez metody, które pilnują duplikatów i limitu.
/// </summary>
/// <summary>Długości listy ostatnich stacji do wyboru w ustawieniach.</summary>
public static class RecentLimits
{
    public static readonly int[] Options = [5, 10, 20];

    public const int Default = 10;

    /// <summary>Najdłuższa lista; tyle zapisuje plik stanu niezależnie od wybranej długości.</summary>
    public static int Max => Options[^1];

    /// <summary>Najbliższa długość z listy; przy remisie krótsza.</summary>
    public static int Nearest(int value) => Options.MinBy(option => Math.Abs(option - value));
}

public sealed class RadioState
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public List<RadioStation> Favourites { get; set; } = [];

    /// <summary>Od najnowszej.</summary>
    public List<RadioStation> Recent { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastStationId { get; set; }

    public RadioListContext Context { get; set; } = new();

    /// <summary>
    /// Plik był nieczytelny albo zapisała go nowsza wersja programu: stan jest tylko do odczytu, a zapis go nie nadpisze.
    /// </summary>
    [JsonIgnore]
    public bool ReadOnly { get; set; }

    public bool IsFavourite(string id) => Favourites.Any(f => f.Id == id);

    /// <summary>Dodaje na koniec; false, gdy stacja już jest.</summary>
    public bool AddFavourite(RadioStation station)
    {
        if (IsFavourite(station.Id))
            return false;

        Favourites.Add(station.Copy());
        return true;
    }

    /// <summary>Usuwa bez pytania; zwraca indeks i kopię do cofnięcia (Ctrl+Z) albo null, gdy stacji nie było.</summary>
    public (int Index, RadioStation Station)? RemoveFavourite(string id)
    {
        var index = Favourites.FindIndex(f => f.Id == id);
        if (index < 0)
            return null;

        var station = Favourites[index];
        Favourites.RemoveAt(index);
        return (index, station);
    }

    /// <summary>Cofnięcie usunięcia: wstawia na dawne miejsce (albo na koniec, gdy lista się skróciła).</summary>
    public bool RestoreFavourite(int index, RadioStation station)
    {
        if (IsFavourite(station.Id))
            return false;

        Favourites.Insert(Math.Clamp(index, 0, Favourites.Count), station);
        return true;
    }

    /// <summary>Ręczna kolejność: przesuwa z jednego miejsca na drugie (przeciąganie, Alt+↑/↓).</summary>
    public bool MoveFavourite(int from, int to)
    {
        if (from < 0 || from >= Favourites.Count || to < 0 || to >= Favourites.Count)
            return false;

        if (from == to)
            return true;

        var station = Favourites[from];
        Favourites.RemoveAt(from);
        Favourites.Insert(to, station);
        return true;
    }

    /// <summary>
    /// Zapamiętuje odtworzoną stację: na początek, bez powtórzeń, najwyżej <paramref name="limit"/>;
    /// ustawia też ostatnią stację.
    /// </summary>
    public void PushRecent(RadioStation station, int limit = RecentLimits.Default)
    {
        Recent.RemoveAll(r => r.Id == station.Id);
        Recent.Insert(0, station.Copy());
        TrimRecent(limit);

        LastStationId = station.Id;
    }

    /// <summary>Skraca listę ostatnich do <paramref name="limit"/> najnowszych; zwraca, czy coś ubyło.</summary>
    public bool TrimRecent(int limit)
    {
        limit = Math.Max(1, limit);
        if (Recent.Count <= limit) return false;

        Recent.RemoveRange(limit, Recent.Count - limit);
        return true;
    }

    public void ClearRecent() => Recent.Clear();

    /// <summary>Szuka stacji w ulubionych i ostatnich; do odtworzenia ostatniej stacji z poprzedniego uruchomienia.</summary>
    public RadioStation? Find(string id) =>
        Favourites.FirstOrDefault(f => f.Id == id) ?? Recent.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Stacja ze wskazanego adresu: wpisana ręcznie (<paramref name="typed"/>: może prowadzić do sieci lokalnej)
    /// albo zaimportowana. Identyfikator wynika z adresu, więc ten sam adres daje tę samą stację.
    /// </summary>
    public static RadioStation CreateCustom(string name, string url, string? country, bool typed, string? logoUrl = null)
    {
        var trimmed = url.Trim();
        var station = new RadioStation
        {
            Id = CustomId(trimmed),
            Name = name.Trim().Length == 0 ? trimmed : name.Trim(),
            Country = (country ?? string.Empty).Trim().ToUpperInvariant(),
            Streams = [new StationStream { Url = trimmed }],
            OriginName = typed ? StationOrigin.Typed : StationOrigin.Imported,
            AllowLocalAddress = typed,
        };
        if (!string.IsNullOrWhiteSpace(logoUrl))
            station.Logos.Add(logoUrl.Trim());
        return station;
    }

    /// <summary>Stacja z adresu wpisanego ręcznie: odmowa (blokada, zły schemat), zanim powstanie.</summary>
    public static (RadioStation? Station, AddressVerdict Verdict) TryCreateTyped(
        string name, string url, string? country = null, string? logoUrl = null, RadioBlocklist? blocklist = null)
    {
        blocklist ??= RadioBlocklist.Current;
        var verdict = blocklist.CheckAddress(url);
        if (verdict == AddressVerdict.Allowed && blocklist.IsBlockedName(name))
            verdict = AddressVerdict.Blocked;

        return verdict == AddressVerdict.Allowed
            ? (CreateCustom(name, url, country, typed: true, logoUrl), verdict)
            : (null, verdict);
    }

    public static string CustomId(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return "user-" + Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }
}
