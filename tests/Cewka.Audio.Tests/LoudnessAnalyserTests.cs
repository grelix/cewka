using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Checks the loudness measurement against the numbers in ITU-R BS.1770-4 rather than
/// against itself. A meter that is merely self-consistent is worthless: it would happily
/// normalise every track to the wrong level.
/// </summary>
public class LoudnessAnalyserTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    /// <summary>
    /// A stereo 1 kHz sine at −23 dBFS reads −23 LUFS. This is the calibration point the
    /// −0.691 offset in the specification exists to produce, so it exercises the K-weighting
    /// filter, the block gating and the offset together.
    /// </summary>
    [Theory]
    [InlineData(-23.0)]
    [InlineData(-18.0)]
    [InlineData(-30.0)]
    public void SineToneMeasuresAtItsOwnLevel(double levelDbfs)
    {
        var amplitude = Math.Pow(10, levelDbfs / 20);
        var analyser = new LoudnessAnalyser(SampleRate, Channels);

        FeedSine(analyser, frequency: 1000, amplitude: amplitude, seconds: 10);

        var measured = analyser.ComputeIntegratedLoudness();

        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, levelDbfs - 0.3, levelDbfs + 0.3);
    }

    /// <summary>Silence has no measurable loudness; the absolute gate must reject every block.</summary>
    [Fact]
    public void SilenceHasNoMeasurableLoudness()
    {
        var analyser = new LoudnessAnalyser(SampleRate, Channels);
        var buffer = new float[SampleRate * Channels];

        for (var second = 0; second < 5; second++) analyser.Add(buffer, SampleRate);

        Assert.Null(analyser.ComputeIntegratedLoudness());
    }

    /// <summary>Material shorter than one 400 ms block cannot be measured at all.</summary>
    [Fact]
    public void VeryShortMaterialIsNotMeasured()
    {
        var analyser = new LoudnessAnalyser(SampleRate, Channels);
        FeedSine(analyser, frequency: 1000, amplitude: 0.5, seconds: 0.2);

        Assert.Null(analyser.ComputeIntegratedLoudness());
    }

    /// <summary>
    /// Długa cisza nie zaniża pomiaru. Ciszę cyfrową odrzuca bramka bezwzględna; bramkę
    /// względną sprawdzają testy poniżej.
    /// </summary>
    [Fact]
    public void LongSilenceDoesNotDragTheMeasurementDown()
    {
        var loudOnly = new LoudnessAnalyser(SampleRate, Channels);
        FeedSine(loudOnly, 1000, Math.Pow(10, -20.0 / 20), 5);
        var reference = loudOnly.ComputeIntegratedLoudness();

        var withSilence = new LoudnessAnalyser(SampleRate, Channels);
        FeedSine(withSilence, 1000, Math.Pow(10, -20.0 / 20), 5);
        var silence = new float[SampleRate * Channels];
        for (var second = 0; second < 25; second++) withSilence.Add(silence, SampleRate);
        var gated = withSilence.ComputeIntegratedLoudness();

        Assert.NotNull(reference);
        Assert.NotNull(gated);
        Assert.InRange(gated!.Value, reference!.Value - 0.5, reference.Value + 0.5);
    }

    /// <summary>
    /// Przypadek 3 z EBU Tech 3341: 10 s przy −36 dBFS, 60 s przy −23 dBFS, 10 s przy −36 dBFS.
    /// Ciche odcinki leżą ponad bramką bezwzględną, ale pod względną, więc wynik to −23,0 LUFS;
    /// bez bramki względnej wyszłoby około −24,2.
    /// </summary>
    [Fact]
    public void RelativeGateMatchesEbuTech3341Case3()
    {
        var analyser = new LoudnessAnalyser(SampleRate, Channels);
        FeedSine(analyser, 1000, Math.Pow(10, -36.0 / 20), 10);
        FeedSine(analyser, 1000, Math.Pow(10, -23.0 / 20), 60);
        FeedSine(analyser, 1000, Math.Pow(10, -36.0 / 20), 10);

        var measured = analyser.ComputeIntegratedLoudness();

        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, -23.1, -22.9);
    }

    /// <summary>
    /// Cichy fragment ponad bramką bezwzględną, a pod względną, nie zaniża pomiaru:
    /// 10 s przy −20 dBFS i 30 s przy −45 dBFS mierzy −20 LUFS, a nie około −26.
    /// </summary>
    [Fact]
    public void QuietPassageBelowTheRelativeGateIsIgnored()
    {
        var analyser = new LoudnessAnalyser(SampleRate, Channels);
        FeedSine(analyser, 1000, Math.Pow(10, -20.0 / 20), 10);
        FeedSine(analyser, 1000, Math.Pow(10, -45.0 / 20), 30);

        var measured = analyser.ComputeIntegratedLoudness();

        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, -20.3, -19.7);
    }

    /// <summary>ReplayGain 2.0 normalises to −18 LUFS.</summary>
    [Theory]
    [InlineData(-23.0, 5.0)]
    [InlineData(-18.0, 0.0)]
    [InlineData(-12.0, -6.0)]
    public void ReplayGainIsTheDistanceFromTheReferenceLevel(double measured, double expectedGain)
    {
        Assert.Equal(expectedGain, LoudnessAnalyser.ToReplayGain(measured), precision: 6);
    }

    private static void FeedSine(LoudnessAnalyser analyser, double frequency, double amplitude, double seconds)
    {
        const int chunkFrames = 4096;
        var buffer = new float[chunkFrames * Channels];
        var totalFrames = (long)(SampleRate * seconds);
        long produced = 0;

        while (produced < totalFrames)
        {
            var frames = (int)Math.Min(chunkFrames, totalFrames - produced);

            for (var i = 0; i < frames; i++)
            {
                var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (produced + i) / SampleRate));
                buffer[i * Channels] = value;
                buffer[i * Channels + 1] = value;
            }

            analyser.Add(buffer, frames);
            produced += frames;
        }
    }
}
