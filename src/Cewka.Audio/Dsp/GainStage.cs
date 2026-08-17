namespace Cewka.Audio.Dsp;

/// <summary>
/// A smoothly changing gain, used both for the volume slider and for loudness
/// normalisation. Any gain that can change while audio is flowing has to ramp; setting a
/// new multiplier between one sample and the next is a step in the waveform, and a step is
/// a click.
/// </summary>
public sealed class GainStage : IAudioProcessor
{
    private readonly double _smoothingSeconds;

    private int _channels = 2;
    private double _coefficient = 0.5;
    private float _current = 1;
    private float _target = 1;

    public GainStage(double smoothingSeconds = 0.02) => _smoothingSeconds = smoothingSeconds;

    /// <summary>Target gain as a linear multiplier.</summary>
    public float Target
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 8f);
    }

    /// <summary>Target gain in decibels.</summary>
    public double TargetDecibels
    {
        get => 20 * Math.Log10(Math.Max(_target, 1e-6f));
        set => Target = (float)Math.Pow(10, Math.Clamp(value, -60, 18) / 20);
    }

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;
        _coefficient = 1 - Math.Exp(-1.0 / (_smoothingSeconds * sampleRate));
        _current = _target;
    }

    public void Process(Span<float> buffer, int frames)
    {
        // Nothing to do when the gain is at unity and not moving.
        if (Math.Abs(_current - 1f) < 1e-6f && Math.Abs(_target - 1f) < 1e-6f) return;

        for (var frame = 0; frame < frames; frame++)
        {
            _current += (float)((_target - _current) * _coefficient);

            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= _current;
        }
    }

    /// <summary>Jumps straight to the target, for use at a track change.</summary>
    public void SnapToTarget() => _current = _target;

    public void Reset() => _current = _target;
}
