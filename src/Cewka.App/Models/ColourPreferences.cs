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
/// <para><b>Skala przesunięta w dół w wersji 0.8.0.</b> Tło okazało się w praktyce mocniejsze,
/// niż potrzeba: ustawienie zalecane przytłaczało treść, a subtelne było tym, którego ludzie
/// używali. Dawna subtelna stała się więc nową zalecaną, a subtelna zjechała o połowę niżej.
/// Intensywna została tam, gdzie była — kto ją wybrał, wybrał ją świadomie.</para>
///
/// <para>Znaczy to, że jedność przestała być wartością wyróżnioną. Progi ustawienia zalecanego
/// leżą teraz wokół 0,78, a nie wokół jedności; „tak, jak program był projektowany" odpowiada
/// dziś ustawieniu o jeden stopień mocniejszemu niż domyślne.</para>
/// </summary>
public static class ColourPreferences
{
    /// <summary>
    /// Mnożnik nasycenia barw wyciąganych z okładki.
    ///
    /// <para>Subtelna jest dokładnie o połowę słabsza od zalecanej — nie „trochę słabsza".
    /// Różnicy poniżej mniej więcej trzydziestu procent nie widać na tle, po którym i tak wędrują
    /// plamy, więc stopień, który niczym się nie różni, byłby stopniem bez powodu.</para>
    /// </summary>
    public static double Saturation(ColourIntensity intensity) => intensity switch
    {
        ColourIntensity.Subtle => 0.36,
        ColourIntensity.Intense => 1.28,
        _ => 0.72,
    };

    /// <summary>
    /// Dolny i górny próg siły plam tła. Każda plama waha się między nimi niezależnie
    /// od pozostałych, więc progi opisują zakres, a nie moment.
    ///
    /// <para>Środki zakresów: 0,39 dla subtelnej, 0,78 dla zalecanej i 1,18 dla intensywnej.
    /// Zalecana przejęła wartość dawnej subtelnej, a subtelna zeszła o połowę niżej. Wahanie
    /// wynosi w każdym przypadku dwanaście procent tego środka — nie tyle samo w liczbach
    /// bezwzględnych, bo wtedy przy subtelnej ten sam rozstrzał byłby względnie najgłębszy,
    /// czyli ustawienie najspokojniejsze pulsowałoby najmocniej.</para>
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
    /// Głębokość wahania, jako część siły średniej. Dwanaście procent widać jako oddech, a nie
    /// jako migotanie: przy dwudziestu tło zaczyna zwracać na siebie uwagę, przy pięciu zmiana
    /// jest już poniżej progu, na którym cokolwiek zauważa się na spokojnym tle.
    /// </summary>
    private const double SwingFraction = 0.12;
}
