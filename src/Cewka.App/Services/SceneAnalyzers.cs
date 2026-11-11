namespace Cewka.App.Services;

/// <summary>
/// Słupki widma z balistyką i zatrzymanymi szczytami. Wzrost od zera do pełnej skali trwa 40 ms,
/// opadanie 300 ms, szczyt stoi 0,8 s, potem opada. Zakres dB normalizuje się powoli, żeby ciche
/// nagrania nie były martwe: góra zakresu podąża za najgłośniejszym pasmem (rośnie w 0,5 s,
/// opada w 12 s), dół leży 60 dB niżej.
/// </summary>
public sealed class SpectrumBars
{
    public const int Count = 48;
    public const double RiseSeconds = 0.04;
    public const double FallSeconds = 0.3;
    public const double PeakHoldSeconds = 0.8;
    public const double PeakFallSeconds = 1.5;
    public const double RangeDb = 60;
    public const double MinTopDb = -40;

    private readonly float[] _bars = new float[Count];
    private readonly float[] _peaks = new float[Count];
    private readonly double[] _hold = new double[Count];
    private double _topDb = MinTopDb;

    /// <summary>Mnożnik czasu opadania słupków i szczytów z ustawień; większy to wolniejsze, płynniejsze opadanie.</summary>
    public double FallScale { get; set; } = 1;

    public ReadOnlySpan<float> Bars => _bars;

    public ReadOnlySpan<float> Peaks => _peaks;

    /// <summary>Bieżąca góra zakresu w dBFS.</summary>
    public double TopDb => _topDb;

    public void Reset()
    {
        Array.Clear(_bars);
        Array.Clear(_peaks);
        Array.Clear(_hold);
        _topDb = MinTopDb;
    }

    public static double ToDb(float amplitude) => amplitude > 1e-6f ? 20 * Math.Log10(amplitude) : -120;

    /// <summary>Podnosi słupki o podany mnożnik, najwyżej do pełnej wysokości; wydłuża w ten sposób opadanie.</summary>
    public void Lift(float factor)
    {
        for (var i = 0; i < Count; i++) _bars[i] = Math.Min(1f, _bars[i] * factor);
    }

    public void Update(ReadOnlySpan<float> amplitudes, double dt)
    {
        dt = Math.Clamp(double.IsFinite(dt) ? dt : 0, 0, 1);

        var loudest = -120.0;
        for (var i = 0; i < Count && i < amplitudes.Length; i++)
            loudest = Math.Max(loudest, ToDb(float.IsFinite(amplitudes[i]) ? amplitudes[i] : 0));

        var goal = Math.Clamp(loudest, MinTopDb, 0);
        if (goal > _topDb) _topDb = Math.Min(goal, _topDb + dt * RangeDb / 0.5);
        else _topDb = Math.Max(goal, _topDb - dt * RangeDb / 12);

        var bottom = _topDb - RangeDb;
        for (var i = 0; i < Count; i++)
        {
            var amplitude = i < amplitudes.Length && float.IsFinite(amplitudes[i]) ? amplitudes[i] : 0;
            var target = (float)Math.Clamp((ToDb(amplitude) - bottom) / RangeDb, 0, 1);

            var value = _bars[i];
            if (target > value) value = Math.Min(target, value + (float)(dt / RiseSeconds));
            else value = Math.Max(target, value - (float)(dt / (FallSeconds * FallScale)));
            _bars[i] = value;

            if (value >= _peaks[i])
            {
                _peaks[i] = value;
                _hold[i] = PeakHoldSeconds;
            }
            else if (_hold[i] > 0) _hold[i] -= dt;
            else _peaks[i] = Math.Max(value, _peaks[i] - (float)(dt / (PeakFallSeconds * FallScale)));
        }
    }
}

/// <summary>Wyzwalanie oscyloskopu na rosnącym przejściu przez zero i wolna regulacja wzmocnienia.</summary>
public sealed class ScopeTrigger
{
    public const double MinGain = 0.5;
    public const double MaxGain = 8;

    private double _gain = 1;

    /// <summary>Bieżące wzmocnienie pionowe.</summary>
    public double Gain => _gain;

    public void Reset() => _gain = 1;

    /// <summary>
    /// Pozycja (ułamkowa) pierwszego rosnącego przejścia przez zero w pierwszych
    /// <paramref name="searchLength"/> próbkach; 0, gdy go brak. Histereza odrzuca szum przy zerze.
    /// </summary>
    public static double Find(ReadOnlySpan<float> samples, int searchLength)
    {
        searchLength = Math.Min(searchLength, samples.Length - 1);
        var armed = false;
        for (var i = 0; i < searchLength; i++)
        {
            var value = samples[i];
            if (value < -0.01f) armed = true;
            else if (armed && value >= 0 && i > 0)
            {
                var before = samples[i - 1];
                var span = value - before;
                return span > 1e-9f ? i - 1 + -before / span : i;
            }
        }

        return 0;
    }

    /// <summary>Próbka przebiegu w ułamkowej pozycji, z interpolacją liniową.</summary>
    public static float Sample(ReadOnlySpan<float> samples, double position)
    {
        var index = (int)position;
        if (index < 0) return 0;
        if (index >= samples.Length - 1) return samples.Length > 0 ? samples[^1] : 0;
        var t = (float)(position - index);
        return samples[index] + (samples[index + 1] - samples[index]) * t;
    }

    /// <summary>Wolno dostraja wzmocnienie tak, żeby szczyt zajmował około 80% wysokości.</summary>
    public double Adapt(float peak, double dt)
    {
        dt = Math.Clamp(double.IsFinite(dt) ? dt : 0, 0, 1);
        var goal = peak > 0.005f ? Math.Clamp(0.8 / peak, MinGain, MaxGain) : _gain;
        var rate = goal < _gain ? 1.5 : 0.4;
        _gain += (goal - _gain) * (1 - Math.Exp(-dt * rate));
        return _gain;
    }
}
