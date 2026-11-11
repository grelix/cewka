namespace Cewka.App.Models;

/// <summary>
/// Granice szerokości panelu kolejki i stacji. Szerokość wybrana przeciąganiem krawędzi jest zapisywana
/// bez zmian, a do granic bieżącego okna przycina ją dopiero wyświetlanie: po powrocie na większy
/// monitor panel wraca do wybranej szerokości.
/// </summary>
public static class QueuePanelWidth
{
    /// <summary>Najwęższy panel, w którym wiersz mieści numer, tytuł i stopkę z pięcioma przyciskami.</summary>
    public const double Minimum = 240;

    /// <summary>Najszerszy panel w kolumnie; węższe okno ogranicza go dodatkowo do połowy swojej szerokości.</summary>
    public const double ColumnMaximum = 560;

    /// <summary>Pas treści, który nakładka zostawia odsłonięty przy lewej krawędzi okna.</summary>
    public const double OverlayMargin = 80;

    /// <summary>
    /// Najmniejsza szerokość bloku odtwarzania obok kolumny. Wartość z pomiaru narzędziem zrzutów: blok
    /// mieści płytę najmniejszego stopnia układu (228 px) obok kolumny o domyślnej szerokości 280 px przy
    /// oknie szerokim na 1040 px. Węższe okno pokazuje przypięty panel jako nakładkę.
    /// </summary>
    public const double ContentMinimum = 760;

    /// <summary>Największa szerokość panelu w oknie o danej szerokości.</summary>
    public static double Maximum(double window, bool column) =>
        Math.Max(Minimum, column ? Math.Min(ColumnMaximum, window * 0.5) : window - OverlayMargin);

    /// <summary>Szerokość do wyświetlenia: wybrana albo domyślna, przycięta do granic okna.</summary>
    public static double Shown(double? chosen, double fallback, double window, bool column) =>
        Math.Clamp(chosen ?? fallback, Minimum, Maximum(window, column));

    /// <summary>
    /// Przypięty panel mieści się w kolumnie: obok zostaje <see cref="ContentMinimum"/> na blok odtwarzania.
    /// Próg liczy się z węższej z szerokości wybranej i domyślnej, więc szerszy wybór nie odbiera kolumny
    /// wcześniej niż dotąd, tylko zwęża się razem z oknem (<see cref="Column"/>).
    /// </summary>
    public static bool FitsColumn(double? chosen, double fallback, double window) =>
        window >= ContentMinimum + Math.Min(Shown(chosen, fallback, window, column: true), fallback);

    /// <summary>Szerokość kolumny: wybrana albo domyślna, zwężona tak, żeby blok odtwarzania miał swoje minimum.</summary>
    public static double Column(double? chosen, double fallback, double window) =>
        Math.Min(Shown(chosen, fallback, window, column: true), ColumnDragMaximum(window));

    /// <summary>Największa szerokość kolumny przy przeciąganiu krawędzi.</summary>
    public static double ColumnDragMaximum(double window) =>
        Math.Max(Minimum, Math.Min(Maximum(window, column: true), window - ContentMinimum));
}

/// <summary>Czasy wysuwania i chowania panelu kolejki.</summary>
public static class QueuePanelTimes
{
    /// <summary>
    /// Kursor, który wyjechał z panelu na krócej, nie uruchamia chowania (tolerancja chybionego ruchu).
    /// Ustawiony czas chowania liczy się dopiero po niej.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    /// <summary>Wsunięcie panelu.</summary>
    public static readonly TimeSpan SlideIn = TimeSpan.FromMilliseconds(180);

    /// <summary>Wysunięcie panelu przy chowaniu.</summary>
    public static readonly TimeSpan SlideOut = TimeSpan.FromMilliseconds(160);

    public static TimeSpan RevealDelay(QueueRevealDelay delay) => delay switch
    {
        QueueRevealDelay.Short => TimeSpan.FromMilliseconds(150),
        QueueRevealDelay.Long => TimeSpan.FromMilliseconds(600),
        _ => TimeSpan.FromMilliseconds(300),
    };

    /// <summary>Czas chowania po tolerancji; <c>null</c>, gdy panel nie chowa się sam.</summary>
    public static TimeSpan? HideDelay(QueueHideDelay delay) => delay switch
    {
        QueueHideDelay.HalfSecond => TimeSpan.FromMilliseconds(500),
        QueueHideDelay.OneSecond => TimeSpan.FromSeconds(1),
        QueueHideDelay.FourSeconds => TimeSpan.FromSeconds(4),
        QueueHideDelay.Never => null,
        _ => TimeSpan.FromSeconds(2),
    };
}
