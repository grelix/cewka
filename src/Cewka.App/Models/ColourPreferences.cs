namespace Cewka.App.Models;

/// <summary>
/// Przelicza wybraną intensywność na współczynniki, którymi mnożone są barwy pochodzące
/// z okładki.
///
/// <para>Dwa współczynniki, nasycenie i krycie, bo samo nasycenie daje barwy jaskrawe, ale ledwie
/// widoczne, a samo krycie szarawy nalot.</para>
/// </summary>
public static class ColourPreferences
{
    /// <summary>
    /// Mnożnik nasycenia barw wyciąganych z okładki.
    ///
    /// <para>Subtelna to połowa zalecanej; różnicy poniżej około trzydziestu procent na
    /// ruchomym tle nie widać.</para>
    /// </summary>
    public static double Saturation(ColourIntensity intensity) => intensity switch
    {
        ColourIntensity.Subtle => 0.36,
        ColourIntensity.Intense => 1.28,
        _ => 0.72,
    };

    /// <summary>
    /// Dolny i górny próg siły plam tła; każda plama waha się między nimi własnym rytmem.
    /// Wahanie jest względne, żeby najspokojniejsze ustawienie nie pulsowało najmocniej.
    /// </summary>
    public static (double Minimum, double Maximum) BackdropRange(ColourIntensity intensity)
    {
        var middle = intensity switch
        {
            ColourIntensity.Subtle => 0.39,
            ColourIntensity.Intense => 1.18,
            _ => 0.78,
        };

        return (middle * (1 - SwingFraction), middle * (1 + SwingFraction));
    }

    /// <summary>
    /// Głębokość wahania jako część siły średniej; przy dwunastu procentach widać oddech, przy
    /// dwudziestu tło zaczyna zwracać uwagę.
    /// </summary>
    private const double SwingFraction = 0.12;
}
