namespace Cewka.Audio.Dsp;

/// <summary>
/// Ten-band graphic equaliser with a preamp, matching the bands in the design.
/// <para>
/// Each band is one peaking section per channel. Gains are smoothed towards their targets
/// once per buffer rather than applied immediately: moving a fader recomputes filter
/// coefficients, and a step change in coefficients is audible as a click. Przedwzmacniacz
/// zmienia się liniowo w obrębie bufora, a wyłączenie sprowadza wszystkie pasma płynnie do
/// zera i dopiero wtedy omija korektor.
/// </para>
/// </summary>
public sealed class Equaliser : IAudioProcessor
{
    /// <summary>Centre frequencies, one octave apart, as shown on the faders.</summary>
    public static readonly double[] Frequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>
    /// Q for one-octave bands: 1 / (2·sinh(ln2 / 2)). Przy tej dobroci połowa wzmocnienia dzwonu
    /// w decybelach wypada pół oktawy od środka, więc sąsiednie pasma zachodzą na siebie. Przy
    /// jednakowych suwakach charakterystyka jest wyższa od wskazania (w środkach pasm około
    /// półtora raza) i lekko falista; dokładność ustawień nie jest celem tego korektora.
    /// </summary>
    private static readonly double OctaveQ = 1.0 / (2.0 * Math.Sinh(Math.Log(2.0) / 2.0));

    /// <summary>Smoothing time for fader movement.</summary>
    private const double SmoothingSeconds = 0.05;

    /// <summary>Below this change the coefficients are left alone.</summary>
    private const double RecomputeThresholdDb = 0.01;

    private readonly double[] _targetGains = new double[Frequencies.Length];
    private readonly double[] _currentGains = new double[Frequencies.Length];
    private readonly bool[] _bandRunning = new bool[Frequencies.Length];
    private Biquad[,] _sections = new Biquad[0, 0];

    private double _targetPreamp;
    private double _currentPreamp;
    private float _preampLinear = 1;

    private int _sampleRate = 48000;
    private int _channels = 2;

    /// <summary>When false the equaliser fades to flat and then passes audio through untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Preamp in decibels, −12 to +12.</summary>
    public double Preamp
    {
        get => _targetPreamp;
        set => _targetPreamp = Math.Clamp(value, -12, 12);
    }

    public void SetGain(int band, double decibels)
    {
        if (band < 0 || band >= _targetGains.Length) return;
        _targetGains[band] = Math.Clamp(decibels, -12, 12);
    }

    public double GetGain(int band) => band >= 0 && band < _targetGains.Length ? _targetGains[band] : 0;

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _sections = new Biquad[Frequencies.Length, channels];
        Array.Clear(_bandRunning);

        for (var band = 0; band < Frequencies.Length; band++)
        for (var channel = 0; channel < channels; channel++)
            _sections[band, channel] = Biquad.Identity();

        UpdateCoefficients(0, force: true);
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled && IsFlat()) return;

        var preampBefore = _preampLinear;
        UpdateCoefficients(frames, force: false);

        for (var band = 0; band < Frequencies.Length; band++)
        {
            // Skip bands sitting at zero: with the cookbook coefficients they are an exact
            // pass-through, so running them would only cost time.
            if (Math.Abs(_currentGains[band]) < RecomputeThresholdDb)
            {
                _bandRunning[band] = false;
                continue;
            }

            for (var channel = 0; channel < _channels; channel++)
            {
                ref var section = ref _sections[band, channel];

                // Przy zerowym wzmocnieniu stan dzwonu wynosi zero; zatrzymany przy pominięciu
                // pochodziłby sprzed wielu buforów.
                if (!_bandRunning[band]) section.Reset();

                for (var frame = 0; frame < frames; frame++)
                {
                    var index = frame * _channels + channel;
                    buffer[index] = section.Process(buffer[index]);
                }

                section.Sanitise();
            }

            _bandRunning[band] = true;
        }

        ApplyPreamp(buffer, frames, preampBefore, _preampLinear);
    }

    /// <summary>Przedwzmacniacz przechodzi liniowo od wartości z poprzedniego bufora do bieżącej.</summary>
    private void ApplyPreamp(Span<float> buffer, int frames, float from, float to)
    {
        if (from == to)
        {
            if (Math.Abs(to - 1f) <= 1e-6f) return;
            for (var i = 0; i < frames * _channels; i++) buffer[i] *= to;
            return;
        }

        var step = (to - from) / frames;
        for (var frame = 0; frame < frames; frame++)
        {
            var gain = from + step * (frame + 1);
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= gain;
        }
    }

    private bool IsFlat()
    {
        if (_currentPreamp != 0) return false;
        foreach (var gain in _currentGains)
            if (gain != 0) return false;
        return true;
    }

    private void UpdateCoefficients(int frames, bool force)
    {
        // Stała czasowa w sekundach, niezależnie od tego, ile ramek przynosi bufor.
        var step = force ? 1 : 1 - Math.Exp(-frames / (_sampleRate * SmoothingSeconds));
        var enabled = Enabled;

        for (var band = 0; band < Frequencies.Length; band++)
        {
            var target = enabled ? _targetGains[band] : 0;
            var current = _currentGains[band];

            if (!force && current == target) continue;

            var next = current + (target - current) * step;

            // Snap once the remaining distance stops mattering, so the value settles exactly.
            if (Math.Abs(target - next) < RecomputeThresholdDb) next = target;

            _currentGains[band] = next;

            for (var channel = 0; channel < _channels; channel++)
                _sections[band, channel].SetPeaking(Frequencies[band], OctaveQ, next, _sampleRate);
        }

        var targetPreamp = enabled ? _targetPreamp : 0;
        if (force || _currentPreamp != targetPreamp)
        {
            _currentPreamp += (targetPreamp - _currentPreamp) * step;
            if (Math.Abs(targetPreamp - _currentPreamp) < RecomputeThresholdDb) _currentPreamp = targetPreamp;

            _preampLinear = (float)Math.Pow(10, _currentPreamp / 20);
        }
    }

    public void Reset()
    {
        for (var band = 0; band < Frequencies.Length; band++)
        for (var channel = 0; channel < _channels; channel++)
            _sections[band, channel].Reset();
    }
}
