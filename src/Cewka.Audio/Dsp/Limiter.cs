namespace Cewka.Audio.Dsp;

/// <summary>
/// Soft-knee limiter with lookahead, sitting at the end of the chain.
/// <para>
/// It exists because the equaliser can add up to 12 dB in several bands at once, and most
/// modern recordings already peak close to full scale. Without it, boosting the bass would
/// simply clip. The lookahead delay lets the gain start falling <em>before</em> the peak
/// arrives, so loud transients are caught without the audible pumping a plain
/// zero-lookahead limiter produces.
/// </para>
/// <para>
/// Gain reduction is shared by all channels. Reducing each channel on its own would move
/// the stereo image whenever one side happened to be louder.
/// </para>
/// </summary>
public sealed class Limiter : IAudioProcessor
{
    private const double LookaheadSeconds = 0.005;
    private const double AttackSeconds = 0.001;
    private const double ReleaseSeconds = 0.100;

    /// <summary>A little below full scale, leaving room for inter-sample peaks.</summary>
    private const double ThresholdDb = -1.0;

    /// <summary>Width of the soft knee, in decibels around the threshold.</summary>
    private const double KneeDb = 4.0;

    private float[] _delay = [];
    private int _delayFrames;
    private int _cursor;
    private int _channels = 2;

    private double _envelope = 1;
    private double _attackCoefficient;
    private double _releaseCoefficient;

    public bool Enabled { get; set; } = true;

    /// <summary>Current gain reduction in decibels, for display. Never positive.</summary>
    public double GainReductionDb => 20 * Math.Log10(Math.Max(_envelope, 1e-6));

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _delayFrames = Math.Max(1, (int)(sampleRate * LookaheadSeconds));
        _delay = new float[_delayFrames * channels];
        _cursor = 0;
        _envelope = 1;

        _attackCoefficient = 1 - Math.Exp(-1.0 / (AttackSeconds * sampleRate));
        _releaseCoefficient = 1 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
    }

    public void Process(Span<float> buffer, int frames)
    {
        if (!Enabled)
        {
            // Even when off, nothing may leave the chain outside the representable range.
            Clamp(buffer, frames * _channels);
            return;
        }

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

            var desired = DesiredGain(peak);

            // Fall quickly, recover slowly: the opposite would sound like breathing.
            var coefficient = desired < _envelope ? _attackCoefficient : _releaseCoefficient;
            _envelope += (desired - _envelope) * coefficient;

            var gain = (float)_envelope;
            var delayIndex = _cursor * _channels;

            for (var c = 0; c < _channels; c++)
            {
                var delayed = _delay[delayIndex + c];
                _delay[delayIndex + c] = buffer[baseIndex + c];
                buffer[baseIndex + c] = delayed * gain;
            }

            _cursor++;
            if (_cursor >= _delayFrames) _cursor = 0;
        }

        Clamp(buffer, frames * _channels);
    }

    /// <summary>
    /// Gain that would bring <paramref name="peak"/> down to the threshold, with the knee
    /// easing the transition into limiting.
    /// </summary>
    private static double DesiredGain(float peak)
    {
        if (peak <= 1e-9f) return 1;

        var levelDb = 20 * Math.Log10(peak);
        var over = levelDb - ThresholdDb;

        if (over <= -KneeDb / 2) return 1;

        var reductionDb = over >= KneeDb / 2
            ? -over
            : -(over + KneeDb / 2) * (over + KneeDb / 2) / (2 * KneeDb);

        return Math.Pow(10, reductionDb / 20);
    }

    private static void Clamp(Span<float> buffer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var value = buffer[i];
            if (value > 1f) buffer[i] = 1f;
            else if (value < -1f) buffer[i] = -1f;
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _cursor = 0;
        _envelope = 1;
    }
}
