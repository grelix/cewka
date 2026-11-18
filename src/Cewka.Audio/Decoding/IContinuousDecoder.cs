namespace Cewka.Audio.Decoding;

/// <summary>
/// Dekoder, którego konwersja częstotliwości może płynąć przez granicę utworów. Filtr patrzy
/// w przód, więc ostatnie ramki utworu czekają na dalsze wejście: przy przejściu bez przerwy
/// dostarcza je następny utwór, inaczej wypłukuje je <see cref="DrainTail"/>.
/// </summary>
internal interface IContinuousDecoder
{
    /// <summary>
    /// Przy końcu źródła <see cref="IAudioDecoder.Read"/> nie wydaje końcówki filtru, tylko zwraca
    /// zero; o końcówce rozstrzyga potem właściciel. Domyślnie wyłączone: końcówka wychodzi sama.
    /// </summary>
    bool HoldTail { get; set; }

    /// <summary>
    /// Przejmuje stan konwersji dekodera <paramref name="previous"/>, który doszedł do końca, tak
    /// jakby oba pliki były jednym. Udaje się tylko przed pierwszym odczytem i przy tym samym
    /// formacie źródła, układzie kanałów, celu i poziomie. Poprzedni nie wydaje już końcówki.
    /// </summary>
    bool TryContinueFrom(IAudioDecoder previous);

    /// <summary>Końcówka wstrzymana przez <see cref="HoldTail"/>; zero, gdy nic nie zostało.</summary>
    int DrainTail(Span<float> destination);
}
