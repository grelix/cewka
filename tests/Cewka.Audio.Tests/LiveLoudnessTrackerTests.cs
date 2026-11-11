using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Dociąganie stacji do poziomu plików: małe kroki, w rytmie ramek, a nie zegara.</summary>
public class LiveLoudnessTrackerTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const double Target = -18;
    private const int ChunkFrames = 9600;

    private static double Amplitude(double levelDbfs) => Math.Pow(10, levelDbfs / 20);

    /// <summary>Podaje sinus 1 kHz porcjami i zbiera każdą korektę razem z czasem dźwięku.</summary>
    private static List<(double Seconds, double GainDb)> Feed(
        LiveLoudnessTracker tracker, double levelDbfs, double seconds, double target = Target,
        Action<float[]>? shape = null)
    {
        var corrections = new List<(double, double)>();
        var chunks = (int)(seconds * Rate / ChunkFrames);

        for (var i = 0; i < chunks; i++)
        {
            var samples = levelDbfs <= -200
                ? new float[ChunkFrames * Channels]
                : Signals.Sine(1000, Amplitude(levelDbfs), ChunkFrames, Rate, startFrame: (long)i * ChunkFrames);
            shape?.Invoke(samples);

            tracker.Add(samples, ChunkFrames);
            if (tracker.TryUpdate(target, out var gain))
            {
                Assert.Equal(tracker.GainDb, gain.GainDecibels);
                corrections.Add(((i + 1) * ChunkFrames / (double)Rate, gain.GainDecibels));
            }
        }

        return corrections;
    }

    [Fact]
    public void QuietStationIsPulledUpInSmallSteps()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        var corrections = Feed(tracker, -30, seconds: 70);

        Assert.NotEmpty(corrections);
        Assert.True(corrections[0].Seconds >= 10, "pierwsza korekta przed 10 s");

        var previous = 0.0;
        foreach (var (_, gain) in corrections)
        {
            Assert.True(Math.Abs(gain - previous) <= 0.5 + 1e-9, $"krok z {previous} na {gain}");
            previous = gain;
        }

        for (var i = 1; i < corrections.Count; i++)
            Assert.True(corrections[i].Seconds - corrections[i - 1].Seconds >= 2 - 0.25);

        Assert.InRange(-30 + tracker.GainDb, Target - 1, Target + 1);
    }

    [Fact]
    public void LoudStationIsPulledDown()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        var corrections = Feed(tracker, -8, seconds: 60);

        Assert.NotEmpty(corrections);
        Assert.All(corrections, correction => Assert.True(correction.GainDb < 0));
        Assert.InRange(-8 + tracker.GainDb, Target - 1, Target + 1);
    }

    [Fact]
    public void SilenceChangesNothing()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 3);

        var corrections = Feed(tracker, -300, seconds: 30);

        Assert.Empty(corrections);
        Assert.Equal(3, tracker.GainDb);
    }

    /// <summary>Podbite ciche nagranie trafiałoby ogranicznikiem w każdy akcent.</summary>
    [Fact]
    public void PeakLimitsTheGrowth()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        Feed(tracker, -30, seconds: 40, shape: samples => samples[100] = samples[101] = 0.99f);

        Assert.InRange(tracker.GainDb, -1.5, 0);
    }

    [Fact]
    public void GainStaysWithinTwelveDecibels()
    {
        var quiet = new LiveLoudnessTracker(Rate, Channels, startGainDb: 11);
        Feed(quiet, -55, seconds: 30);
        Assert.Equal(12, quiet.GainDb);

        var loud = new LiveLoudnessTracker(Rate, Channels, startGainDb: -11);
        Feed(loud, -3, seconds: 30);
        Assert.InRange(loud.GainDb, -12, -11);
    }

    [Fact]
    public void KnownLevelIsTheStartingPoint()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 6);
        Assert.Equal(6, tracker.GainDb);

        var corrections = Feed(tracker, -18, seconds: 14);

        Assert.Equal(5.5, corrections[0].GainDb);
        Assert.True(corrections[0].Seconds >= 10);
    }

    [Fact]
    public void TargetIsTakenAtEachCorrection()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        Assert.Empty(Feed(tracker, -14, seconds: 20, target: -14));

        Feed(tracker, -14, seconds: 2, target: -18);
        Assert.Equal(-0.5, tracker.GainDb);
    }

    /// <summary>Doba słuchania nie zajmuje więcej pamięci niż trzy minuty okna.</summary>
    [Fact]
    public void DayOfListeningKeepsOnlyTheWindow()
    {
        const int rate = 1000;
        var tracker = new LiveLoudnessTracker(rate, 1, startGainDb: 0);
        var silence = new float[rate * 60];

        for (var minute = 0; minute < 24 * 60; minute++)
        {
            tracker.Add(silence, silence.Length);
            tracker.TryUpdate(Target, out _);
            Assert.True(tracker.BlockCount <= 1800);
        }

        Assert.Equal(1800, tracker.BlockCount);
    }

    /// <summary>Po przejściu całego okna liczy się już tylko nowy poziom.</summary>
    [Fact]
    public void SinglePeakLeavesTheWindowToo()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        var first = true;
        Feed(tracker, -30, seconds: 300, shape: samples =>
        {
            if (first) samples[0] = 0.99f;
            first = false;
        });

        Assert.InRange(tracker.GainDb, 10, 12);
    }

    [Fact]
    public void OldMaterialLeavesTheWindow()
    {
        var tracker = new LiveLoudnessTracker(Rate, Channels, startGainDb: 0);

        Feed(tracker, -18, seconds: 20);
        Assert.Equal(0, tracker.GainDb);

        Feed(tracker, -24, seconds: 200);
        Assert.InRange(tracker.GainDb, 5.4, 6.6);
    }
}
