namespace Cewka.Audio.Dsp;

/// <summary>
/// Ogranicznik z miękkim kolanem i wyprzedzeniem na końcu toru. Korektor potrafi dodać do 12 dB
/// w kilku pasmach naraz, a nagrania i tak sięgają pełnej skali.
/// <para>
/// Wzmocnienie próbki to minimum wymaganego wzmocnienia z okna wyprzedzenia, wygładzone średnią
/// ruchomą tej samej długości, więc zejście kończy się dokładnie, gdy szczyt wychodzi z linii
/// opóźnienia. Redukcja jest wspólna dla kanałów, żeby nie przesuwać obrazu stereo.
/// </para>
/// <para>
/// Pułapka: linia opóźnienia działa także po wyłączeniu. Opóźnienie toru jest przez to stałe
/// i przełączenie niczego nie gubi ani nie powtarza.
/// </para>
/// </summary>
public sealed class Limiter : IAudioProcessor
{
    private const double LookaheadSeconds = 0.005;
    private const double ReleaseSeconds = 0.100;
    private const double SwitchSeconds = 0.010;

    /// <summary>A little below full scale, leaving room for inter-sample peaks.</summary>
    private const double ThresholdDb = -1.0;

    /// <summary>Width of the soft knee, in decibels around the threshold.</summary>
    private const double KneeDb = 4.0;

    /// <summary>Poniżej tego szczytu wzmocnienie wynosi jeden; porównanie oszczędza logarytm.</summary>
    private static readonly float KneeStart = (float)Math.Pow(10, (ThresholdDb - KneeDb / 2) / 20);

    private float[] _delay = [];
    private int _delayFrames;
    private int _cursor;
    private int _channels = 2;

    // Minimum przesuwne z okna długości wyprzedzenia plus jedna próbka: kolejka monotoniczna
    // w buforze kołowym, wartości rosnące od czoła.
    private double[] _minimumValues = [];
    private long[] _minimumStamps = [];
    private int _minimumHead;
    private int _minimumCount;
    private long _stamp;

    // Średnia ruchoma minimum, indeksowana tym samym kursorem co linia opóźnienia.
    private double[] _box = [];
    private double _boxSum;

    private double _envelope = 1;
    private double _releaseCoefficient;
    private double _switchCoefficient;
    private double _wet = 1;
    private float _lastGain = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Current gain reduction in decibels, for display. Never positive.</summary>
    public double GainReductionDb => 20 * Math.Log10(Math.Max(_lastGain, 1e-6f));

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _delayFrames = Math.Max(1, (int)(sampleRate * LookaheadSeconds));
        _delay = new float[_delayFrames * channels];
        _box = new double[_delayFrames];
        _minimumValues = new double[_delayFrames + 2];
        _minimumStamps = new long[_delayFrames + 2];

        _releaseCoefficient = 1 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
        _switchCoefficient = 1 - Math.Exp(-1.0 / (SwitchSeconds * sampleRate));
        _wet = Enabled ? 1 : 0;

        Reset();
    }

    public void Process(Span<float> buffer, int frames)
    {
        var targetWet = Enabled ? 1.0 : 0.0;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            // Peak across channels of the sample that is about to enter the delay line.
            var peak = 0f;
            for (var c = 0; c < _channels; c++)
            {
                var magnitude = Math.Abs(buffer[baseIndex + c]);
                if (magnitude > peak) peak = magnitude;
            }

            var held = PushMinimum(DesiredGain(peak));

            _boxSum += held - _box[_cursor];
            _box[_cursor] = held;
            var boxed = _boxSum / _delayFrames;

            // Spadek natychmiast, bo średnia już rozłożyła go na czas wyprzedzenia; powrót wolno.
            _envelope = boxed < _envelope ? boxed : _envelope + (boxed - _envelope) * _releaseCoefficient;

            _wet += (targetWet - _wet) * _switchCoefficient;
            var gain = (float)(1 + _wet * (_envelope - 1));
            var delayIndex = _cursor * _channels;

            for (var c = 0; c < _channels; c++)
            {
                var delayed = _delay[delayIndex + c];
                _delay[delayIndex + c] = buffer[baseIndex + c];
                buffer[baseIndex + c] = delayed * gain;
            }

            _lastGain = gain;

            _cursor++;
            if (_cursor >= _delayFrames)
            {
                _cursor = 0;

                // Suma bieżąca liczona przyrostowo zbiera błąd zaokrągleń; raz na okno liczy się ją od nowa.
                _boxSum = 0;
                foreach (var value in _box) _boxSum += value;
            }
        }

        if (Math.Abs(targetWet - _wet) < 1e-4) _wet = targetWet;

        // Even when off, nothing may leave the chain outside the representable range.
        Clamp(buffer, frames * _channels);
    }

    /// <summary>Dokłada wartość do okna i zwraca minimum z ostatnich <c>_delayFrames + 1</c> próbek.</summary>
    private double PushMinimum(double value)
    {
        var capacity = _minimumValues.Length;

        while (_minimumCount > 0)
        {
            var tail = (_minimumHead + _minimumCount - 1) % capacity;
            if (_minimumValues[tail] < value) break;
            _minimumCount--;
        }

        var slot = (_minimumHead + _minimumCount) % capacity;
        _minimumValues[slot] = value;
        _minimumStamps[slot] = _stamp;
        _minimumCount++;

        var oldest = _stamp - _delayFrames;
        while (_minimumStamps[_minimumHead] < oldest)
        {
            _minimumHead = (_minimumHead + 1) % capacity;
            _minimumCount--;
        }

        _stamp++;
        return _minimumValues[_minimumHead];
    }

    /// <summary>
    /// Gain that would bring <paramref name="peak"/> down to the threshold, with the knee
    /// easing the transition into limiting.
    /// </summary>
    private static double DesiredGain(float peak)
    {
        if (!(peak > KneeStart)) return 1;

        var levelDb = 20 * Math.Log10(peak);
        var over = levelDb - ThresholdDb;

        var reductionDb = over >= KneeDb / 2
            ? -over
            : -(over + KneeDb / 2) * (over + KneeDb / 2) / (2 * KneeDb);

        return Math.Pow(10, reductionDb / 20);
    }

    private static void Clamp(Span<float> buffer, int count)
    {
        // Porównania z NaN są fałszywe, więc NaN przeszedłby przez zwykłe obcięcie do urządzenia.
        for (var i = 0; i < count; i++)
        {
            var value = buffer[i];
            buffer[i] = float.IsNaN(value) ? 0f : Math.Clamp(value, -1f, 1f);
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Fill(_box, 1.0);
        _boxSum = _delayFrames;
        _minimumHead = 0;
        _minimumCount = 0;
        _stamp = 0;
        _cursor = 0;
        _envelope = 1;
        _lastGain = 1;
    }
}
