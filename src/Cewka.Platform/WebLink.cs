using System.Diagnostics;

namespace Cewka.Platform;

/// <summary>
/// Otwiera adres w przeglądarce ustawionej w systemie (<c>UseShellExecute</c>, pod Linuksem
/// <c>xdg-open</c>).
/// </summary>
public static class WebLink
{
    /// <summary>
    /// Otwiera adres, jeśli jest adresem http albo https. Zwraca informację o powodzeniu.
    /// Powłoka wykonałaby też <c>file:</c> czy nazwę programu, a adresy przychodzą między innymi
    /// z odpowiedzi serwisu GitHuba.
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
