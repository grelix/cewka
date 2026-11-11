using Cewka.Audio.Streaming;

namespace Cewka.App.Radio;

/// <summary>Wynik sprawdzenia adresu stacji.</summary>
public enum AddressVerdict
{
    Allowed,

    /// <summary>Nie jest adresem bezwzględnym.</summary>
    Invalid,

    /// <summary>Tylko http i https.</summary>
    UnsupportedScheme,

    /// <summary>Domena z listy blokad: Rosja, Białoruś (domeny krajowe) albo podmiot z wykazu.</summary>
    Blocked,
}

/// <summary>
/// Lista blokad adresów i nazw: domeny krajowe Rosji i Białorusi (.ru, .su, .by, .рф, .бел) oraz domeny i nazwy
/// podmiotów z wykazu sankcji. Dopasowanie po etykietach domeny, nie po fragmencie tekstu: „notrt.com” nie jest
/// „rt.com”, a stacja „MDR Sputnik” nie jest „Sputnik”. Dotyczy adresów słuchacza i odpowiedzi katalogu; lista stacji
/// w programie przechodzi to samo sprawdzenie w narzędziu katalogu i w teście.
/// </summary>
public sealed class RadioBlocklist
{
    private static readonly string[] CountryDomains = ["ru", "su", "by", "xn--p1ai", "xn--90ais"];

    // Podmioty z załącznika XV rozporządzenia 833/2014 o domenach spoza .ru i .by; pełnego wykazu ACM nie porównywano.
    private static readonly string[] BuiltInDomains = ["rt.com", "sputniknews.com", "sputnikglobe.com"];

    private static readonly string[] BuiltInNames = ["rt", "rt deutsch", "rt france", "rt arabic", "sputnik", "radyo sputnik"];

    private readonly string[] _domains;
    private readonly HashSet<string> _names;

    public RadioBlocklist(IEnumerable<string>? domains = null, IEnumerable<string>? names = null)
    {
        _domains = [.. (domains ?? []).Select(NormaliseDomain).Where(d => d.Length > 0).Distinct()];
        _names = [.. (names ?? []).Select(TextFolding.Fold).Where(n => n.Length > 0)];
    }

    /// <summary>Lista wbudowana w program; później można ją zastąpić podpisaną listą z serwera projektu.</summary>
    public static RadioBlocklist BuiltIn { get; } = new(BuiltInDomains, BuiltInNames);

    /// <summary>Lista obowiązująca w programie.</summary>
    public static RadioBlocklist Current { get; set; } = BuiltIn;

    public IReadOnlyList<string> Domains => _domains;

    /// <summary>Do podłączenia jako <c>PlaybackEngine.LiveUrlGuard</c>: sprawdza adres początkowy i każde przekierowanie.</summary>
    public static LiveFailureReason? UrlGuard(Uri url) => Current.IsBlockedHost(url.IdnHost) ? LiveFailureReason.UrlBlocked : null;

    public bool IsBlockedHost(string host)
    {
        var name = NormaliseDomain(host);
        if (name.Length == 0)
            return false;

        var labels = name.Split('.');
        if (labels.Length > 1 && CountryDomains.Contains(labels[^1]))
            return true;

        foreach (var domain in _domains)
        {
            if (name == domain || name.EndsWith("." + domain, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Nazwa stacji równa nazwie z listy (cała nazwa, nie fragment).</summary>
    public bool IsBlockedName(string name) => _names.Contains(TextFolding.Fold(name));

    /// <summary>Adres wpisany lub zaimportowany: bezwzględny, http(s), nie z listy blokad.</summary>
    public AddressVerdict CheckAddress(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri))
            return AddressVerdict.Invalid;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return AddressVerdict.UnsupportedScheme;
        return IsBlockedHost(uri.IdnHost) ? AddressVerdict.Blocked : AddressVerdict.Allowed;
    }

    /// <summary>
    /// Powód, dla którego zapisana stacja się nie odtworzy (ulubiona zostaje na liście, ale nie gra): blokada nazwy
    /// albo wszystkich jej adresów. <see cref="AddressVerdict.Allowed"/>, gdy gra.
    /// </summary>
    public AddressVerdict CheckStation(RadioStation station)
    {
        if (IsBlockedName(station.Name))
            return AddressVerdict.Blocked;
        if (station.Streams.Count == 0)
            return AddressVerdict.Invalid;

        var verdicts = station.Streams.Select(s => CheckAddress(s.Url)).ToList();
        return verdicts.Contains(AddressVerdict.Allowed) ? AddressVerdict.Allowed : verdicts[0];
    }

    private static string NormaliseDomain(string? host) => (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
}
