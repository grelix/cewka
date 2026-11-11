namespace Cewka.App.Services;

/// <summary>Kierunek przesunięcia okładki: wersor w kadrze.</summary>
public readonly record struct CoverDriftPath(double X, double Y);

/// <summary>
/// Bardzo wolny ruch okładki w scenie: skala 1,00–1,04 i przesunięcie do 2% rozmiaru w cyklu 60 s.
/// Funkcja czasu, bez stanu; kierunek przesunięcia wynika ze ścieżki pliku, więc ten sam utwór
/// porusza się zawsze tak samo, a kolejne utwory różnie. Start jest w skali 1 bez przesunięcia.
/// </summary>
public static class CoverDrift
{
    /// <summary>Okres pełnego cyklu ruchu.</summary>
    public static readonly TimeSpan Cycle = TimeSpan.FromSeconds(60);

    /// <summary>Największy przyrost skali ponad 1.</summary>
    public const double MaxScaleGain = 0.04;

    /// <summary>Największe przesunięcie jako ułamek rozmiaru okładki.</summary>
    public const double MaxShift = 0.02;

    private const int Directions = 12;

    /// <summary>Kierunek dla ścieżki; stały między uruchomieniami (bez losowego skrótu napisów).</summary>
    public static CoverDriftPath For(string? path)
    {
        // FNV-1a: stabilny, w przeciwieństwie do string.GetHashCode.
        var hash = 2166136261u;
        foreach (var c in path ?? string.Empty)
            hash = (hash ^ c) * 16777619u;

        var angle = hash % Directions * (2 * Math.PI / Directions);
        return new CoverDriftPath(Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>Skala i przesunięcie (ułamek rozmiaru) po <paramref name="seconds"/> sekundach ruchu.</summary>
    public static (double Scale, double X, double Y) At(CoverDriftPath direction, double seconds)
    {
        var phase = 2 * Math.PI * seconds / Cycle.TotalSeconds;
        var scale = 1 + MaxScaleGain * (0.5 - 0.5 * Math.Cos(phase));
        var shift = MaxShift * Math.Sin(phase);
        return (scale, direction.X * shift, direction.Y * shift);
    }
}
