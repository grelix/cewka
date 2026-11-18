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
        var target = ShellTarget(address);
        if (target is null) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się otworzyć adresu: {ex.Message}");
            return false;
        }
    }

    /// <summary>Tekst przekazywany powłoce albo <c>null</c>, gdy adresu nie wolno otworzyć.</summary>
    // Postać znormalizowana: cudzysłów i spacje z obcego adresu nie trafiają dosłownie
    // do wiersza poleceń przeglądarki.
    public static string? ShellTarget(string? address) =>
        IsSafe(address) ? new Uri(address!).AbsoluteUri : null;

    /// <summary>Adres bezwzględny o schemacie http albo https.</summary>
    public static bool IsSafe(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
