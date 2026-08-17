namespace Cewka.App.Models;

/// <summary>
/// Przelicza wybraną intensywność na współczynniki, którymi mnożone są barwy pochodzące
/// z okładki.
///
/// <para><b>Dlaczego dwa współczynniki, a nie jeden.</b> Wrażenie „mocniejszego koloru" składa
/// się z dwóch niezależnych rzeczy: jak nasycona jest sama barwa i jak dużą część okna zajmuje.
/// Podniesienie tylko nasycenia daje barwy jaskrawe, ale nadal ledwie widoczne; podniesienie
/// tylko krycia rozlewa po oknie szarawy nalot. Razem dają zmianę, którą widać.</para>
///
/// <para><b>Zalecana ma średnią równą jedności.</b> Siła plam nie jest stała — każda z nich
/// rozjaśnia się i przygasa własnym rytmem — więc intensywność wyznacza nie jedną wartość, ale
/// dolny i górny próg tego wahania. Przy ustawieniu zalecanym progi leżą symetrycznie wokół
/// jedności, czyli wokół siły, z jaką tło było projektowane: średnio wygląda tak jak dotąd,
/// a różnicę widać jako ruch, nie jako inną jasność.</para>
/// </summary>
public static class ColourPreferences
{
    /// <summary>Mnożnik nasycenia barw wyciąganych z okładki.</summary>
    public static double Saturation(ColourIntensity intensity) => intensity switch
    {
        ColourIntensity.Subtle => 0.72,
        ColourIntensity.Intense => 1.28,
        _ => 1.0,
    };

    /// <summary>
    /// Dolny i górny próg siły plam tła. Każda plama waha się między nimi niezależnie
    /// od pozostałych, więc progi opisują zakres, a nie moment.
    ///
    /// <para>Środek zakresu zachowuje wartość, jaką dane ustawienie miało, gdy siła była stała:
    /// 0,78 dla subtelnej, 1,00 dla zalecanej i 1,18 dla intensywnej. Wahanie wynosi w każdym
    /// przypadku dwanaście procent tego środka — nie tyle samo w liczbach bezwzględnych, bo
    /// wtedy przy subtelnej ten sam rozstrzał byłby względnie najgłębszy, czyli ustawienie
    /// najspokojniejsze pulsowałoby najmocniej.</para>
    /// </summary>
    public static (double Minimum, double Maximum) BackdropRange(ColourIntensity intensity)
    {
        var middle = intensity switch
        {
            ColourIntensity.Subtle => 0.78,
            ColourIntensity.Intense => 1.18,
            _ => 1.00,
        };

        return (middle * (1 - SwingFraction), middle * (1 + SwingFraction));
    }

    /// <summary>
    /// Głębokość wahania, jako część siły średniej. Dwanaście procent widać jako oddech, a nie
    /// jako migotanie: przy dwudziestu tło zaczyna zwracać na siebie uwagę, przy pięciu zmiana
    /// jest już poniżej progu, na którym cokolwiek zauważa się na spokojnym tle.
    /// </summary>
    private const double SwingFraction = 0.12;
}
