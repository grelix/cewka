using System.Runtime.InteropServices;

namespace Cewka.Platform;

/// <summary>
/// Odczytuje język interfejsu ustawiony w systemie.
///
/// <para>Pułapka: przy <c>InvariantGlobalization=true</c> <c>CultureInfo.CurrentUICulture</c>
/// jest zawsze kulturą niezmienną. Dlatego odczyt idzie wprost od systemu: w Windows z listy
/// preferowanych języków wyświetlania (format regionalny bywa inny), pod Linuksem ze zmiennych
/// środowiskowych według reguł gettext.</para>
/// </summary>
public static class SystemLanguage
{
    /// <summary>
    /// Kody języków systemu w kolejności preferencji, na przykład <c>en</c>, <c>pl</c>: dwuliterowe,
    /// a przed nimi odmiana z osobnym tłumaczeniem (<c>pt-br</c>, <c>zh-hans</c>), gdy wpis ją określa.
    /// Pusta lista, gdy nie da się ich ustalić.
    /// </summary>
    public static IReadOnlyList<string> Candidates()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? ParseLanguageList(FromWindows())
                : FromEnvironment(Environment.GetEnvironmentVariable);
        }
        catch
        {
            // Nieustalony język systemu jest zwyczajną sytuacją, nie awarią: program bierze
            // wtedy język zapasowy i działa dalej.
            return [];
        }
    }

    /// <summary>
    /// Kandydaci ze zmiennych środowiskowych: najpierw lista z <c>LANGUAGE</c>, potem
    /// <c>LC_ALL</c>, <c>LC_MESSAGES</c> albo <c>LANG</c>. Jak w gettext, <c>LANGUAGE</c> nie
    /// obowiązuje, gdy językiem jest „C” lub „POSIX”.
    /// </summary>
    public static IReadOnlyList<string> FromEnvironment(Func<string, string?> variable)
    {
        string? locale = null;

        foreach (var name in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            locale = variable(name);
            if (!string.IsNullOrWhiteSpace(locale)) break;
        }

        var result = new List<string>();

        if (!IsNoLanguage(locale))
        {
            foreach (var entry in (variable("LANGUAGE") ?? string.Empty).Split(':'))
                AddWithVariant(result, entry);
        }

        AddWithVariant(result, locale);
        return result;
    }

    /// <summary>
    /// Kody z listy języków Windows zapisanej jako napisy rozdzielone znakiem NUL, na przykład
    /// <c>en-US\0pl-PL\0\0</c>.
    /// </summary>
    public static IReadOnlyList<string> ParseLanguageList(string? list)
    {
        var result = new List<string>();
        if (list is null) return result;

        foreach (var entry in list.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            AddWithVariant(result, entry);

        return result;
    }

    /// <summary>Dodaje odmianę języka, jeśli wpis ją określa, a za nią sam język.</summary>
    private static void AddWithVariant(List<string> result, string? raw)
    {
        Add(result, ParseVariant(raw));
        Add(result, ParseLocale(raw));
    }

    private static void Add(List<string> result, string? code)
    {
        if (code is not null && !result.Contains(code)) result.Add(code);
    }

    /// <summary>
    /// Odmiana języka, która ma osobne tłumaczenie albo może je mieć: <c>pt-br</c> dla portugalskiego
    /// z Brazylii, <c>zh-hans</c> i <c>zh-hant</c> dla chińskiego według zapisu pisma (<c>zh-Hans-CN</c>)
    /// albo regionu (<c>zh_CN</c>, <c>zh-TW</c>). Dla pozostałych wpisów <c>null</c>.
    /// </summary>
    public static string? ParseVariant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var entry = raw.Split(':')[0].Split('.', '@')[0].Trim();
        var parts = entry.Split('-', '_');
        if (parts.Length < 2) return null;

        var language = parts[0].ToLowerInvariant();
        var rest = parts.Skip(1).Select(p => p.ToUpperInvariant()).ToArray();

        return language switch
        {
            "pt" when rest.Contains("BR") => "pt-br",
            "zh" when rest.Contains("HANS") => "zh-hans",
            "zh" when rest.Contains("HANT") => "zh-hant",
            "zh" when rest.Any(r => r is "CN" or "SG" or "MY") => "zh-hans",
            "zh" when rest.Any(r => r is "TW" or "HK" or "MO") => "zh-hant",
            _ => null,
        };
    }

    private static bool IsNoLanguage(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return false;

        var name = locale.Split('.', '@')[0].Trim();
        return name is "C" or "POSIX";
    }

    private const uint MuiLanguageName = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(
        uint flags, out uint count, [Out] char[]? buffer, ref uint size);

    private static string? FromWindows()
    {
        uint size = 0;
        if (!GetUserPreferredUILanguages(MuiLanguageName, out _, null, ref size) || size == 0) return null;

        var buffer = new char[size];
        if (!GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref size)) return null;

        return new string(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
    }

    /// <summary>
    /// Wyłuskuje sam kod języka z zapisu, jakim posługuje się system.
    /// Przyjmuje między innymi <c>pl-PL</c>, <c>pt_BR.UTF-8</c>, <c>uk:ru:en</c> oraz <c>de@euro</c>.
    /// </summary>
    public static string? ParseLocale(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Z listy po dwukropku bierze pierwszy element; dalej odcinany jest kraj, kodowanie
        // i wariant.
        var separator = raw.IndexOfAny([':', '.', '@', '-', '_']);
        var code = (separator > 0 ? raw[..separator] : raw).Trim().ToLowerInvariant();

        // „C" i „POSIX" to brak wyboru języka, nie język.
        if (code.Length != 2) return null;

        return code is "c" ? null : code;
    }
}
