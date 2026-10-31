using System.Runtime.InteropServices;

namespace Cewka.Platform;

/// <summary>
/// Odczytuje język interfejsu ustawiony w systemie.
///
/// <para>Pułapka: program jest budowany z <c>InvariantGlobalization=true</c>, bez biblioteki
/// ICU. W tym trybie <c>CultureInfo.CurrentUICulture</c> jest zawsze kulturą niezmienną, więc
/// odczyt przez <c>CultureInfo</c> nie dopasowałby żadnego języka. Stąd odczyt wprost od
/// systemu: w Windowsie z listy preferowanych języków wyświetlania (nie z formatu regionalnego,
/// który często jest inny niż język interfejsu), w Linuksie ze zmiennych środowiskowych według
/// reguł gettext.</para>
/// </summary>
public static class SystemLanguage
{
    /// <summary>
    /// Dwuliterowe kody języków systemu w kolejności preferencji, na przykład <c>en</c>,
    /// <c>pl</c>. Pusta lista, gdy nie da się ich ustalić.
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

    /// <summary>Pierwszy z <see cref="Candidates"/> albo <c>null</c>.</summary>
    public static string? TwoLetterCode() => Candidates().FirstOrDefault();

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
                Add(result, ParseLocale(entry));
        }

        Add(result, ParseLocale(locale));
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
            Add(result, ParseLocale(entry));

        return result;
    }

    private static void Add(List<string> result, string? code)
    {
        if (code is not null && !result.Contains(code)) result.Add(code);
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
