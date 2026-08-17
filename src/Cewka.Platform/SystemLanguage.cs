using System.Runtime.InteropServices;

namespace Cewka.Platform;

/// <summary>
/// Odczytuje język interfejsu ustawiony w systemie.
///
/// <para><b>Dlaczego nie przez CultureInfo.</b> Program jest budowany z
/// <c>InvariantGlobalization=true</c> — bez biblioteki ICU, dzięki czemu pakiety dla Linuksa nie
/// mają od niej zależności. W tym trybie <c>CultureInfo.CurrentUICulture</c> jest zawsze kulturą
/// niezmienną: jej <c>TwoLetterISOLanguageName</c> to „iv", a <c>InstalledUICulture.Name</c> jest
/// puste. Sprawdzone pomiarem na Windowsie z polskim interfejsem. Ustawienie „język systemowy"
/// oparte na tym odczycie nie mogło zadziałać nigdy i dla nikogo — zawsze wypadało z niego
/// dopasowanie do niczego, a więc język zapasowy.</para>
///
/// <para>Stąd odczyt wprost od systemu: w Windowsie z ustawień regionalnych użytkownika,
/// w Linuksie ze zmiennych środowiskowych, w kolejności przyjętej przez gettext.</para>
/// </summary>
public static class SystemLanguage
{
    /// <summary>
    /// Dwuliterowy kod języka systemu, na przykład <c>pl</c>, albo <c>null</c>, gdy nie da się
    /// go ustalić.
    /// </summary>
    public static string? TwoLetterCode()
    {
        try
        {
            var raw = OperatingSystem.IsWindows() ? FromWindows() : FromEnvironment();
            return ParseLocale(raw);
        }
        catch
        {
            // Nieustalony język systemu jest zwyczajną sytuacją, nie awarią: program bierze
            // wtedy język zapasowy i działa dalej.
            return null;
        }
    }

    /// <summary>Zmienne środowiskowe w kolejności, w jakiej czyta je gettext.</summary>
    private static string? FromEnvironment()
    {
        foreach (var name in (string[])["LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG"])
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private const int LocaleNameMaxLength = 85;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetUserDefaultLocaleName([Out] char[] name, int size);

    private static string? FromWindows()
    {
        var buffer = new char[LocaleNameMaxLength];
        var length = GetUserDefaultLocaleName(buffer, buffer.Length);

        // Zwracana długość obejmuje znak kończący; zero znaczy, że wywołanie się nie udało.
        return length > 1 ? new string(buffer, 0, length - 1) : null;
    }

    /// <summary>
    /// Wyłuskuje sam kod języka z zapisu, jakim posługuje się system.
    /// Przyjmuje między innymi <c>pl-PL</c>, <c>pt_BR.UTF-8</c>, <c>uk:ru:en</c> oraz <c>de@euro</c>.
    ///
    /// <para>Jawnie dostępna, bo jest to jedyna część tego odczytu, którą da się sprawdzić testem:
    /// pozostałe pytają system operacyjny, a jego odpowiedź zależy od maszyny, na której test
    /// właśnie działa.</para>
    /// </summary>
    public static string? ParseLocale(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // LANGUAGE może zawierać listę języków po dwukropku, od najbardziej pożądanego;
        // dalej odcinany jest kraj, kodowanie i wariant.
        var separator = raw.IndexOfAny([':', '.', '@', '-', '_']);
        var code = (separator > 0 ? raw[..separator] : raw).Trim().ToLowerInvariant();

        // „C" i „POSIX" to brak wyboru języka, nie język.
        if (code.Length != 2) return null;

        return code is "c" ? null : code;
    }
}
