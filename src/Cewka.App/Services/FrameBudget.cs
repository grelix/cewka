namespace Cewka.App.Services;

/// <summary>
/// Pilnuje czasu klatek sceny i dobiera rozdzielczość bufora wewnętrznego. Po
/// <see cref="LateFramesToDegrade"/> spóźnionych klatkach w ciągu <see cref="WindowSeconds"/> sekund
/// poziom rośnie o jeden (mniejszy bufor); po <see cref="CalmSeconds"/> sekundach bez spóźnień
/// wraca o jeden w dół. Różnica progów daje histerezę, więc obraz nie przeskakuje między poziomami.
/// Stan to kilka liczb i pierścień znaczników czasu; wywołanie nie alokuje.
/// </summary>
public sealed class FrameBudget
{
    /// <summary>Klatka dłuższa od tej jest spóźniona: wolniej niż 20 kl./s przy celu 30.</summary>
    public const double LateSeconds = 0.05;

    public const int LateFramesToDegrade = 30;
    public const double WindowSeconds = 5;
    public const double CalmSeconds = 60;

    /// <summary>Najwyższy poziom obniżenia.</summary>
    public const int MaxLevel = 3;

    /// <summary>Dłuższa przerwa to wstrzymanie okna, a nie obciążenie, więc nie liczy się do niczego.</summary>
    private const double PauseSeconds = 0.5;

    private readonly double[] _late = new double[LateFramesToDegrade];
    private int _count;
    private int _oldest;
    private double _time;
    private double _calmSince;

    /// <summary>0 to pełna rozdzielczość, 1 do <see cref="MaxLevel"/> to coraz mniejszy bufor.</summary>
    public int Level { get; private set; }

    /// <summary>Mnożnik rozdzielczości bufora względem 960 x 540 dla bieżącego poziomu; 1 poniżej pierwszego obniżenia.</summary>
    public double Scale => ScaleFor(Level);

    public static double ScaleFor(int level) => level switch
    {
        <= 1 => 1.0,
        2 => 2.0 / 3,
        _ => 0.5,
    };

    /// <summary>Zgłasza czas ostatniej klatki w sekundach. Zwraca true, gdy poziom się zmienił.</summary>
    public bool Observe(double frameSeconds)
    {
        if (!double.IsFinite(frameSeconds) || frameSeconds <= 0 || frameSeconds > PauseSeconds) return false;

        _time += frameSeconds;
        var before = Level;

        if (frameSeconds > LateSeconds)
        {
            Record(_time);
            _calmSince = _time;
            if (WindowFull() && Level < MaxLevel)
            {
                Level++;
                _count = 0;
            }
        }
        else if (Level > 0 && _time - _calmSince >= CalmSeconds)
        {
            Level--;
            _calmSince = _time;
        }

        return Level != before;
    }

    public void Reset()
    {
        Level = 0;
        _count = 0;
        _oldest = 0;
        _time = 0;
        _calmSince = 0;
    }

    private void Record(double time)
    {
        // Pierścień: pełny gubi najstarszy znacznik.
        if (_count == LateFramesToDegrade)
        {
            _oldest = (_oldest + 1) % LateFramesToDegrade;
            _count--;
        }

        _late[(_oldest + _count) % LateFramesToDegrade] = time;
        _count++;
    }

    private bool WindowFull() =>
        _count == LateFramesToDegrade && _time - _late[_oldest] <= WindowSeconds;
}
