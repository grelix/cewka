namespace Cewka.Audio.Dsp;

/// <summary>
/// Pomiar stacji w trakcie gry i łagodne dociąganie do poziomu plików: wolne wyrównanie poziomu,
/// nie kompresja. Czas liczy się w ramkach dźwięku, nie zegarem, więc przerwa w sieci nic nie
/// zmienia, a test nie musi czekać.
/// </summary>
internal sealed class LiveLoudnessTracker
{
    /// <summary>180 s przy 10 blokach na sekundę: mowa i muzyka nie szarpią poziomem.</summary>
    private const int WindowBlocks = 1800;

    private const double FirstCorrectionSeconds = 10;
    private const double CorrectionEverySeconds = 2;
    private const double StepDb = 0.5;
    private const double LimitDb = 12;

    private readonly LoudnessAnalyser _analyser;
    private readonly long _correctionFrames;
    private long _framesToCorrection;

    public LiveLoudnessTracker(int rate, int channels, double startGainDb)
    {
        _analyser = new LoudnessAnalyser(rate, channels, WindowBlocks);
        _correctionFrames = (long)(CorrectionEverySeconds * rate);
        _framesToCorrection = (long)(FirstCorrectionSeconds * rate);
        GainDb = startGainDb;
    }

    public double GainDb { get; private set; }

    internal int BlockCount => _analyser.BlockCount;

    public void Add(ReadOnlySpan<float> samples, int frames)
    {
        _analyser.Add(samples, frames);
        _framesToCorrection -= frames;
    }

    /// <summary>Jeden krok w stronę poziomu docelowego, jeśli pora i jest po co.</summary>
    public bool TryUpdate(double targetLufs, out TrackGain gain)
    {
        gain = default;

        if (_framesToCorrection > 0) return false;
        _framesToCorrection = _correctionFrames;

        // Sama cisza nic nie mówi o poziomie stacji.
        if (_analyser.ComputeIntegratedLoudness() is not { } lufs) return false;

        var wanted = LoudnessService.FromMeasurement(lufs, _analyser.SamplePeak, targetLufs).GainDecibels;
        var difference = Math.Clamp(wanted, -LimitDb, LimitDb) - GainDb;
        if (Math.Abs(difference) < StepDb) return false;

        GainDb = Math.Clamp(GainDb + Math.Sign(difference) * StepDb, -LimitDb, LimitDb);
        gain = new TrackGain(GainDb, lufs + GainDb - LoudnessService.ReferenceLufs);
        return true;
    }
}
