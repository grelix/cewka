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

    private const double SnapDistance = 1e-6;

    private int _channels = 2;
    private double _coefficient = 0.5;
    private double _current = 1;
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
        var target = _target;

        // Wartość bieżąca w double, bo w float przyrost ginął w zaokrągleniu przed dojściem do
        // celu. Przyciąganie pozwala szybkiej ścieżce zadziałać, a przy celu zero nie zostawia
        // mnożnika w liczbach subnormalnych.
        if (Math.Abs(target - _current) < SnapDistance) _current = target;

        if (_current == target)
        {
            // Nothing to do when the gain is at unity and not moving.
            if (target == 1f) return;
            if (target == 0f)
            {
                buffer[..(frames * _channels)].Clear();
                return;
            }
        }

        for (var frame = 0; frame < frames; frame++)
        {
            _current += (target - _current) * _coefficient;

            var gain = (float)_current;
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= gain;
        }

        if (Math.Abs(target - _current) < SnapDistance) _current = target;
    }

    /// <summary>Jumps straight to the target, for use at a track change.</summary>
    public void SnapToTarget() => _current = _target;

    public void Reset() => _current = _target;
}
