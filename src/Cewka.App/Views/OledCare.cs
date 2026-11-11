namespace Cewka.App.Views;

/// <summary>
/// Ochrona ekranu OLED w scenie: czyste funkcje dryfu i stałe przygaszania. Położenie zależy
/// wyłącznie od czasu ruchu, więc jest ciągłe, powtarzalne i nie wymaga alokacji w kroku.
/// </summary>
internal static class OledCare
{
    /// <summary>Amplituda dryfu w jednostkach płótna 1920 × 1080.</summary>
    public const double Amplitude = 12;

    /// <summary>Okres dryfu w poziomie, w sekundach (5 minut).</summary>
    public const double PeriodX = 300;

    /// <summary>Okres dryfu w pionie, w sekundach (4 minuty).</summary>
    public const double PeriodY = 240;

    /// <summary>Co ile krok dryfu przesuwa obraz; przy tak powolnym ruchu częściej nie ma sensu.</summary>
    public static readonly TimeSpan DriftStep = TimeSpan.FromSeconds(2);

    /// <summary>Bez wejścia użytkownika tyle czasu mija, zanim stałe napisy przygasną.</summary>
    public static readonly TimeSpan DimAfter = TimeSpan.FromMinutes(3);

    /// <summary>Krycie przygaszonych napisów względem zwykłego.</summary>
    public const double DimFactor = 0.65;

    /// <summary>Stałe położenia trybu ograniczonego, każde o tej samej amplitudzie co dryf.</summary>
    private static readonly (double X, double Y)[] Jumps =
        [(Amplitude, 0), (0, Amplitude), (-Amplitude, 0), (0, -Amplitude)];

    /// <summary>Przesunięcie obrazu po <paramref name="seconds"/> sekundach ruchu.</summary>
    public static (double X, double Y) DriftAt(double seconds) =>
        (Amplitude * Math.Sin(2 * Math.PI * seconds / PeriodX),
         Amplitude * Math.Sin(2 * Math.PI * seconds / PeriodY));

    /// <summary>Położenie trybu ograniczonego dla kolejnego utworu; numer liczy się cyklicznie.</summary>
    public static (double X, double Y) JumpAt(int track) => Jumps[((track % Jumps.Length) + Jumps.Length) % Jumps.Length];
}
