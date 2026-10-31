namespace Cewka.Audio.Dsp;

/// <summary>
/// Ograniczanie dynamiki („tryb nocny”): kompresor progowy z miękkim kolanem i wzmocnieniem
/// wyrównawczym, z redukcją wspólną dla kanałów.
///
/// <para>Pułapka: stoi przed limiterem, który łapie to, co wzmocnienie wyrównawcze wypchnęłoby
/// ponad skalę.</para>
/// </summary>
public sealed class DynamicRange : IAudioProcessor
{
    private const double AttackSeconds = 0.010;
    private const double ReleaseSeconds = 0.200;

    /// <summary>Szerokość miękkiego kolana wokół progu, w decybelach.</summary>
    private const double KneeDb = 6.0;

    /// <summary>Próg przy sile zerowej i przy pełnej.</summary>
    private const double ThresholdAtZeroDb = -12;
    private const double ThresholdAtFullDb = -30;

    /// <summary>Nachylenie charakterystyki przy sile zerowej i przy pełnej.</summary>
    private const double RatioAtZero = 1.0;
    private const double RatioAtFull = 4.0;

    /// <summary>Część teoretycznego wyrównania; pełne przywróciłoby głośność szczytów.</summary>
    private const double MakeupShare = 0.6;

    /// <summary>Czas płynnego włączania, wyłączania i zmiany wzmocnienia wyrównawczego.</summary>
    private const double SwitchSeconds = 0.020;

    private int _channels = 2;
    private double _attackCoefficient;
    private double _releaseCoefficient;
    private double _switchCoefficient;
    private double _envelope = 1;

    private double _strength;
    private double _thresholdDb = ThresholdAtZeroDb;
    private double _ratio = RatioAtZero;
    private double _targetMakeup = 1;
    private double _makeup = 1;

    // Udział stopnia w wyniku, 0–1, i to, czy w poprzednim buforze stopień pracował.
    private double _wet;
    private bool _running;

    /// <summary>Po wyłączeniu stopień wygasa płynnie, a potem sygnał przechodzi bez zmiany ani jednej próbki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Siła działania w procentach, 0–100.</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            _strength = Math.Clamp(value, 0, 100);
            var fraction = _strength / 100.0;

            _thresholdDb = ThresholdAtZeroDb + (ThresholdAtFullDb - ThresholdAtZeroDb) * fraction;
            _ratio = RatioAtZero + (RatioAtFull - RatioAtZero) * fraction;

            // Wyrównanie odpowiada redukcji, jaką kompresor nałożyłby na sygnał o pełnej skali.
            var reductionAtFullScale = -_thresholdDb * (1 - 1 / _ratio);
            _targetMakeup = Math.Pow(10, reductionAtFullScale * MakeupShare / 20);
        }
    }

    private double TargetWet => Enabled && _strength > 0 ? 1 : 0;

    /// <summary>Bieżąca redukcja w decybelach, nigdy dodatnia. Do pokazania w interfejsie.</summary>
    public double GainReductionDb => 20 * Math.Log10(Math.Max(_envelope, 1e-6));

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _envelope = 1;

        _attackCoefficient = 1 - Math.Exp(-1.0 / (AttackSeconds * sampleRate));
        _releaseCoefficient = 1 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
        _switchCoefficient = 1 - Math.Exp(-1.0 / (SwitchSeconds * sampleRate));

        Strength = _strength;
        _makeup = _targetMakeup;
        _wet = TargetWet;
        _running = _wet > 0;
    }

    public void Process(Span<float> buffer, int frames)
    {
        var targetWet = TargetWet;
        if (targetWet == 0 && _wet == 0)
        {
            _running = false;
            return;
        }

        // Obwiednia zatrzymana przy wyłączeniu opisuje materiał sprzed wielu buforów.
        if (!_running) Reset();
        _running = true;

        var targetMakeup = _targetMakeup;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            var peak = 0f;
            for (var c = 0; c < _channels; c++)
            {
                var magnitude = Math.Abs(buffer[baseIndex + c]);
                if (magnitude > peak) peak = magnitude;
            }

            var desired = DesiredGain(peak);

            // Szybko w dół, wolno w górę — odwrotnie brzmiałoby jak oddychanie.
            var coefficient = desired < _envelope ? _attackCoefficient : _releaseCoefficient;
            _envelope += (desired - _envelope) * coefficient;

            // Wzmocnienie wyrównawcze sięga kilkunastu decybeli, więc i ono, i udział stopnia
            // zmieniają się płynnie; skok o tyle w jednej próbce to głośny trzask.
            _makeup += (targetMakeup - _makeup) * _switchCoefficient;
            _wet += (targetWet - _wet) * _switchCoefficient;

            var gain = (float)(1 + _wet * (_envelope * _makeup - 1));
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= gain;
        }

        if (Math.Abs(targetWet - _wet) < 1e-4) _wet = targetWet;
        if (Math.Abs(targetMakeup - _makeup) < 1e-6) _makeup = targetMakeup;
    }

    /// <summary>Wzmocnienie sprowadzające szczyt na charakterystykę, z miękkim kolanem wokół progu.</summary>
    private double DesiredGain(float peak)
    {
        if (peak <= 1e-9f) return 1;

        var levelDb = 20 * Math.Log10(peak);
        var over = levelDb - _thresholdDb;

        if (over <= -KneeDb / 2) return 1;

        double reductionDb;
        if (over >= KneeDb / 2)
        {
            reductionDb = -over * (1 - 1 / _ratio);
        }
        else
        {
            // W obrębie kolana nachylenie narasta kwadratowo, więc przejście jest gładkie
            // zarówno w wartości, jak i w pochodnej.
            var distance = over + KneeDb / 2;
            reductionDb = -(1 - 1 / _ratio) * distance * distance / (2 * KneeDb);
        }

        return Math.Pow(10, reductionDb / 20);
    }

    public void Reset() => _envelope = 1;
}
