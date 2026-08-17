namespace Cewka.App.ViewModels;

/// <summary>
/// The position restored from the previous session, waiting to be resumed.
///
/// <para><b>Po co osobna klasa.</b> Kolejka z poprzedniego uruchomienia jest przywracana, ale
/// odtwarzanie nie startuje samo — program, który po otwarciu od razu zaczyna hałasować, rzadko
/// jest tym, czego ktokolwiek chce. Zapamiętana pozycja czeka więc na pierwsze naciśnięcie play.</para>
///
/// <para><b>Dlaczego to wymagało wydzielenia.</b> Warunki, w których ta pozycja ma zostać zużyta,
/// a w których porzucona, były wcześniej rozsiane po modelu widoku — i okazało się, że trzy
/// wejścia uruchamiające odtwarzanie (kliknięcie w kolejkę, następny, poprzedni) o niej nie
/// wiedziały. Pierwsze naciśnięcie pauzy po takim starcie trafiało w przywracanie sesji: zamiast
/// zatrzymania następował przeskok na zapamiętaną pozycję i dalsza gra.</para>
///
/// <para>Tutaj te warunki są w jednym miejscu i dają się sprawdzić testem, bez urządzenia
/// dźwiękowego i bez interfejsu.</para>
/// </summary>
public sealed class PendingResume
{
    private int _index = -1;
    private double _seconds;

    /// <summary>True while a position from the previous session is still waiting.</summary>
    public bool IsArmed => _index >= 0;

    /// <summary>
    /// Remembers where to resume from. An index outside the queue is ignored rather than stored,
    /// because the file list may have shrunk since it was written.
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
    /// Forgets the position. Called by every route that starts playback on its own — once the
    /// listener has chosen a track, where the previous session left off no longer matters.
    /// </summary>
    public void Discard()
    {
        _index = -1;
        _seconds = 0;
    }

    /// <summary>
    /// Takes the position, if it still applies. Always leaves the instance disarmed: a resume
    /// that was declined must not come back on the next press.
    /// </summary>
    /// <param name="alreadyPlaying">
    /// Whether the engine has already begun a track. When it has, the restored position is moot
    /// and the caller should carry on with whatever it was doing — pausing, most likely.
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
