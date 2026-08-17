namespace Cewka.Audio.Dsp;

/// <summary>
/// Ten-band graphic equaliser with a preamp, matching the bands in the design.
/// <para>
/// Each band is one peaking section per channel. Gains are smoothed towards their targets
/// once per buffer rather than applied immediately: moving a fader recomputes filter
/// coefficients, and a step change in coefficients is audible as a click.
/// </para>
/// </summary>
public sealed class Equaliser : IAudioProcessor
{
    /// <summary>Centre frequencies, one octave apart, as shown on the faders.</summary>
    public static readonly double[] Frequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>
    /// Q for one-octave bands: 1 / (2·sinh(ln2 / 2)). With this value the bells of adjacent
    /// bands sum to a flat response when every fader is set alike, which is what makes the
    /// preamp behave predictably.
    /// </summary>
    private static readonly double OctaveQ = 1.0 / (2.0 * Math.Sinh(Math.Log(2.0) / 2.0));

    /// <summary>Smoothing time for fader movement.</summary>
    private const double SmoothingSeconds = 0.05;

    /// <summary>Below this change the coefficients are left alone.</summary>
    private const double RecomputeThresholdDb = 0.01;

    private readonly double[] _targetGains = new double[Frequencies.Length];
    private readonly double[] _currentGains = new double[Frequencies.Length];
    private Biquad[,] _sections = new Biquad[0, 0];

    private double _targetPreamp;
    private double _currentPreamp;
    private float _preampLinear = 1;

    private int _sampleRate = 48000;
    private int _channels = 2;
    private double _smoothingPerBuffer = 0.5;

    /// <summary>When false the equaliser passes audio through untouched.</summary>
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

        for (var band = 0; band < Frequencies.Length; band++)
        for (var channel = 0; channel < channels; channel++)
            _sections[band, channel] = Biquad.Identity();

        // Buffers arrive roughly every 10 ms; convert the smoothing time to a per-buffer factor.
        const double assumedBufferSeconds = 0.01;
        _smoothingPerBuffer = 1 - Math.Exp(-assumedBufferSeconds / SmoothingSeconds);

        for (var band = 0; band < Frequencies.Length; band++) _currentGains[band] = _targetGains[band];
        _currentPreamp = _targetPreamp;

        UpdateCoefficients(force: true);
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled) return;

        UpdateCoefficients(force: false);

        var preamp = _preampLinear;

        for (var band = 0; band < Frequencies.Length; band++)
        {
            // Skip bands sitting at zero: with the cookbook coefficients they are an exact
            // pass-through, so running them would only cost time.
            if (Math.Abs(_currentGains[band]) < RecomputeThresholdDb) continue;

            for (var channel = 0; channel < _channels; channel++)
            {
                ref var section = ref _sections[band, channel];
                for (var frame = 0; frame < frames; frame++)
                {
                    var index = frame * _channels + channel;
                    buffer[index] = section.Process(buffer[index]);
                }
            }
        }

        if (Math.Abs(preamp - 1f) > 1e-6f)
        {
            for (var i = 0; i < frames * _channels; i++) buffer[i] *= preamp;
        }
    }

    private void UpdateCoefficients(bool force)
    {
        for (var band = 0; band < Frequencies.Length; band++)
        {
            var target = _targetGains[band];
            var current = _currentGains[band];

            if (!force && Math.Abs(target - current) < RecomputeThresholdDb) continue;

            var next = force ? target : current + (target - current) * _smoothingPerBuffer;

            // Snap once the remaining distance stops mattering, so the value settles exactly.
            if (Math.Abs(target - next) < RecomputeThresholdDb) next = target;

            _currentGains[band] = next;

            for (var channel = 0; channel < _channels; channel++)
                _sections[band, channel].SetPeaking(Frequencies[band], OctaveQ, next, _sampleRate);
        }

        if (force || Math.Abs(_targetPreamp - _currentPreamp) >= RecomputeThresholdDb)
        {
            _currentPreamp = force
                ? _targetPreamp
                : _currentPreamp + (_targetPreamp - _currentPreamp) * _smoothingPerBuffer;

            if (Math.Abs(_targetPreamp - _currentPreamp) < RecomputeThresholdDb) _currentPreamp = _targetPreamp;

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
