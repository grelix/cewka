namespace Cewka.Audio.Dsp;

/// <summary>
/// Ograniczanie dynamiki — „tryb nocny". Ścisza to, co głośne, i podnosi całość, przez co ciche
/// fragmenty stają się słyszalne bez podkręcania głośności w miejscach głośnych.
///
/// <para><b>Po co.</b> Nagranie o dużej rozpiętości dynamicznej — muzyka poważna, dobrze
/// zrealizowany jazz, ścieżka filmowa — w cichym pokoju wymaga ciągłego sięgania do suwaka,
/// a w hałasie po prostu ginie w tle. Ten stopień zawęża rozpiętość, zamiast kazać użytkownikowi
/// wybierać między zbyt cicho a za głośno.</para>
///
/// <para><b>Jak.</b> Zwykły kompresor progowy z miękkim kolanem i wzmocnieniem wyrównawczym.
/// Redukcja jest wspólna dla wszystkich kanałów — osobna dla każdego przesuwałaby obraz stereo
/// za każdym razem, gdy jedna strona zagra głośniej. Ta sama zasada rządzi limiterem na końcu
/// łańcucha.</para>
///
/// <para>Stopień stoi <em>przed</em> limiterem, więc limiter pozostaje ostatnim zabezpieczeniem
/// i łapie wszystko, co wzmocnienie wyrównawcze wypchnęłoby ponad skalę.</para>
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

    /// <summary>
    /// Jaka część teoretycznego wyrównania jest stosowana. Pełne przywróciłoby głośność
    /// szczytów, a chodzi o to, żeby całość była równiejsza, nie żeby była głośniejsza.
    /// </summary>
    private const double MakeupShare = 0.6;

    private int _channels = 2;
    private double _attackCoefficient;
    private double _releaseCoefficient;
    private double _envelope = 1;

    private double _strength;
    private double _thresholdDb = ThresholdAtZeroDb;
    private double _ratio = RatioAtZero;
    private float _makeup = 1;

    /// <summary>Gdy wyłączone, sygnał przechodzi bez zmiany ani jednej próbki.</summary>
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
            _makeup = (float)Math.Pow(10, reductionAtFullScale * MakeupShare / 20);
        }
    }

    /// <summary>Bieżąca redukcja w decybelach, nigdy dodatnia. Do pokazania w interfejsie.</summary>
    public double GainReductionDb => 20 * Math.Log10(Math.Max(_envelope, 1e-6));

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _envelope = 1;

        _attackCoefficient = 1 - Math.Exp(-1.0 / (AttackSeconds * sampleRate));
        _releaseCoefficient = 1 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));

        Strength = _strength;
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled || _strength <= 0) return;

        var makeup = _makeup;

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

            var gain = (float)_envelope * makeup;
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= gain;
        }
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
