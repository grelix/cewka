using System.Diagnostics;

namespace Cewka.Platform;

/// <summary>
/// Otwiera adres w przeglądarce ustawionej w systemie.
///
/// <para>W Windowsie i w Linuksie robi to ta sama droga: <see cref="ProcessStartInfo"/>
/// z <c>UseShellExecute</c>, które w Linuksie sprowadza się do <c>xdg-open</c>. Program nie
/// osadza żadnej przeglądarki i nie pobiera niczego sam — oddaje adres pulpitowi.</para>
/// </summary>
public static class WebLink
{
    /// <summary>
    /// Otwiera adres, jeśli jest adresem http albo https. Zwraca informację o powodzeniu.
    ///
    /// <para><b>Dlaczego sprawdzenie schematu.</b> Wartość idzie do powłoki systemu, która
    /// wykonałaby również <c>file:</c> czy nazwę programu. Adresy pochodzą tutaj z metadanych
    /// zestawu i z odpowiedzi serwisu GitHuba, czyli z dwóch miejsc, których program nie
    /// kontroluje w całości — a to wystarczający powód, żeby nie podawać powłoce niczego,
    /// czego się wcześniej nie obejrzało.</para>
    /// </summary>
    public static bool Open(string? address)
    {
        if (!IsSafe(address)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(address!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się otworzyć adresu: {ex.Message}");
            return false;
        }
    }

    /// <summary>Adres bezwzględny o schemacie http albo https.</summary>
    public static bool IsSafe(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
