namespace Cewka.App.ViewModels;

/// <summary>
/// Pozycja z poprzedniej sesji, czekająca na pierwsze naciśnięcie odtwarzania; po starcie
/// program sam nie gra.
///
/// <para>Pułapka: każda droga, która sama uruchamia odtwarzanie (kliknięcie w kolejkę, następny,
/// poprzedni), musi wznowienie porzucić. Inaczej pierwsza pauza po takim starcie przeskakiwała
/// na zapamiętaną pozycję i grała dalej.</para>
/// </summary>
public sealed class PendingResume
{
    private int _index = -1;
    private double _seconds;

    /// <summary>Czy pozycja z poprzedniej sesji wciąż czeka.</summary>
    public bool IsArmed => _index >= 0;

    /// <summary>Numer czekającego wpisu w kolejce; −1, gdy nic nie czeka.</summary>
    public int Index => _index;

    /// <summary>Czekająca pozycja w utworze, w sekundach.</summary>
    public double Seconds => _seconds;

    /// <summary>
    /// Kolejka straciła wpis. Usunięcie czekającego utworu porzuca wznowienie; usunięcie
    /// wcześniejszego przesuwa numer, żeby nadal wskazywał ten sam plik.
    /// </summary>
    public void OnRemoved(int index)
    {
        if (!IsArmed) return;

        if (index == _index) Discard();
        else if (index < _index) _index--;
    }

    /// <summary>Wpis przeniesiono z <paramref name="from"/> na <paramref name="to"/>.</summary>
    public void OnMoved(int from, int to)
    {
        if (!IsArmed || from == to) return;

        if (from == _index) _index = to;
        else if (from < _index && to >= _index) _index--;
        else if (from > _index && to <= _index) _index++;
    }

    /// <summary>
    /// Zapamiętuje miejsce wznowienia. Numer spoza kolejki jest pomijany, bo lista plików mogła
    /// się od zapisu skurczyć.
    /// </summary>
    public void Arm(int index, double seconds, int queueLength)
    {
        if (index < 0 || index >= queueLength)
        {
            Discard();
            return;
        }

        _index = index;
        _seconds = seconds;
    }

    /// <summary>
    /// Porzuca pozycję. Woła to każda droga, która sama uruchamia odtwarzanie.
    /// </summary>
    public void Discard()
    {
        _index = -1;
        _seconds = 0;
    }

    /// <summary>
    /// Pobiera pozycję, jeśli wciąż obowiązuje. Zawsze ją porzuca, żeby odrzucone wznowienie
    /// nie wróciło przy następnym naciśnięciu.
    /// </summary>
    /// <param name="alreadyPlaying">
    /// Czy silnik już zaczął utwór; wtedy pozycja nie ma znaczenia, a wywołujący robi swoje
    /// (zwykle pauzę).
    /// </param>
    public bool TryTake(bool alreadyPlaying, out int index, out double seconds)
    {
        index = _index;
        seconds = _seconds;

        var armed = IsArmed;
        Discard();

        return armed && !alreadyPlaying;
    }
}
