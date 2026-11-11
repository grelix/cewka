namespace Cewka.App.Services;

/// <summary>
/// Wspólny ogranicznik błysków sceny (WCAG 2.3.1) dla wszystkich wizualizacji. Dostaje średnią
/// luminancję klatki (0 do 1, <see cref="FrameLuminance"/>) i zwraca luminancję, jaką wolno
/// pokazać. Zasady:
/// <list type="bullet">
/// <item>narastanie nie szybsze niż pełna skala w 80 ms, opadanie nie szybsze niż w 250 ms;</item>
/// <item>zmiana o co najmniej 10% w kierunku przeciwnym do poprzedniej to przejście; w dowolnej
/// sekundzie dozwolone są najwyżej 3. Czwartego nie wolno zacząć: wartość zostaje tuż przed progiem,
/// co w praktyce wydłuża opadanie obwiedni, aż najstarsze przejście zestarzeje się o sekundę.</item>
/// </list>
/// Stan to kilka liczb; wywołanie nie alokuje. Wywołuje go wyłącznie wątek rysowania.
/// </summary>
public sealed class FlashLimiter
{
    public const double RiseSeconds = 0.08;
    public const double FallSeconds = 0.25;

    /// <summary>Zmiana luminancji liczona jako przejście.</summary>
    public const double Threshold = 0.10;

    public const int MaxTransitions = 3;
    public const double WindowSeconds = 1.0;

    // Obraz przekracza próg o klatkę lub dwie inaczej niż szacunek, więc miejsce wraca z zapasem ponad okno.
    private const double CreditSeconds = WindowSeconds + 0.1;

    // Tuż pod progiem, żeby pomiar z histerezą 10% nigdy nie policzył wstrzymanej zmiany.
    private const double HeldExcursion = Threshold * 0.98;

    private readonly double[] _transitions = new double[MaxTransitions];
    private int _count;
    private int _oldest;
    private double _time;
    private double _value;
    private double _extreme;
    private int _direction;
    private bool _started;

    /// <summary>Ostatnia zwrócona luminancja.</summary>
    public double Value => _value;

    /// <summary>
    /// Przetwarza jedną klatkę. <paramref name="dt"/> to czas od poprzedniej klatki w sekundach;
    /// pierwsza klatka przechodzi bez zmian.
    /// </summary>
    public double Limit(double luminance, double dt)
    {
        if (!double.IsFinite(luminance)) luminance = 0;
        luminance = Math.Clamp(luminance, 0, 1);
        dt = Math.Clamp(double.IsFinite(dt) ? dt : 0, 0, 1);
        _time += dt;

        if (!_started)
        {
            _started = true;
            _value = _extreme = luminance;
            return _value;
        }

        var next = _value;
        if (luminance > _value) next = Math.Min(luminance, _value + dt / RiseSeconds);
        else if (luminance < _value) next = Math.Max(luminance, _value - dt / FallSeconds);

        next = RegisterMovement(next);
        _value = next;
        return _value;
    }

    /// <summary>Mnożnik, przez który trzeba przemnożyć klatkę, żeby jej luminancja stała się dozwolona.</summary>
    public double Gain(double measured, double limited) => measured > 1e-4 ? Math.Min(1, limited / measured) : 1;

    public void Reset()
    {
        _count = 0;
        _oldest = 0;
        _time = 0;
        _value = _extreme = 0;
        _direction = 0;
        _started = false;
    }

    private double RegisterMovement(double next)
    {
        var step = next - _value;

        if (_direction == 0)
        {
            if (Math.Abs(next - _extreme) < Threshold) return next;
            return Turn(next, Math.Sign(next - _extreme));
        }

        // Dalszy ruch w tym samym kierunku przesuwa skrajną wartość.
        if (step * _direction > 0 && (next - _extreme) * _direction > 0) _extreme = next;

        if ((_extreme - next) * _direction < Threshold) return next;

        // Odwrócenie o co najmniej próg: nowe przejście, o ile mieści się w limicie.
        if (!CanTransition())
            return _extreme - _direction * HeldExcursion;

        return Turn(next, -_direction);
    }

    private double Turn(double next, int direction)
    {
        Record();
        _direction = direction;
        _extreme = next;
        return next;
    }

    private bool CanTransition()
    {
        // Zwalnia miejsca po przejściach starszych niż okno.
        while (_count > 0 && _time - _transitions[_oldest] >= CreditSeconds)
        {
            _oldest = (_oldest + 1) % MaxTransitions;
            _count--;
        }

        return _count < MaxTransitions;
    }

    private void Record()
    {
        if (!CanTransition()) return;

        _transitions[(_oldest + _count) % MaxTransitions] = _time;
        _count++;
    }
}

/// <summary>Pomiar średniej luminancji względnej klatki (bufor wewnętrzny, zwykle 64×36).</summary>
public static class FrameLuminance
{
    /// <summary>Szerokość i wysokość bufora pomiarowego.</summary>
    public const int Width = 64;
    public const int Height = 36;

    private static readonly float[] Linear = BuildTable();

    private static float[] BuildTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            table[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }

        return table;
    }

    /// <summary>
    /// Średnia luminancja względna (0 czerń, 1 biel) pikseli BGRA, 4 bajty na piksel.
    /// Alfa jest pomijana: bufor jest nieprzezroczysty.
    /// </summary>
    public static double Measure(ReadOnlySpan<byte> bgra)
    {
        var pixels = bgra.Length / 4;
        if (pixels == 0) return 0;

        double sum = 0;
        for (var i = 0; i < pixels; i++)
        {
            var b = Linear[bgra[i * 4]];
            var g = Linear[bgra[i * 4 + 1]];
            var r = Linear[bgra[i * 4 + 2]];
            sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        return sum / pixels;
    }
}
