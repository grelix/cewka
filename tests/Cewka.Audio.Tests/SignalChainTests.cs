using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Behaviour of the equaliser, the limiter and the gain stage.</summary>
public class SignalChainTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // ---------- korektor ----------

    /// <summary>
    /// Every band at zero must leave the signal exactly as it was. This is not a formality:
    /// a filter that is only <em>nearly</em> transparent at unity gain colours everything
    /// the user never asked to change.
    /// </summary>
    [Fact]
    public void FlatEqualiserIsTransparent()
    {
        var equaliser = new Equaliser();
        equaliser.Prepare(SampleRate, Channels);

        var original = Sine(1000, 0.5, 4096);
        var processed = (float[])original.Clone();

        equaliser.Process(processed, 4096);

        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], processed[i], tolerance: 1e-6f);
    }

    /// <summary>A boost at the band centre raises a tone at that frequency by the requested amount.</summary>
    [Theory]
    [InlineData(5, 1000.0, 6.0)]   // 1 kHz
    [InlineData(7, 4000.0, -6.0)]  // 4 kHz
    [InlineData(2, 125.0, 9.0)]    // 125 Hz
    public void BandGainMovesItsOwnFrequency(int band, double frequency, double gainDb)
    {
        var equaliser = new Equaliser();
        equaliser.SetGain(band, gainDb);
        equaliser.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(frequency, 0.25, frames);
        equaliser.Process(buffer, frames);

        // Skip the first tenth of a second: the filter has to settle first.
        var expected = 0.25 * Math.Pow(10, gainDb / 20);
        var measured = PeakAfter(buffer, skipFrames: 4800);

        Assert.InRange(measured, expected * 0.94, expected * 1.06);
    }

    /// <summary>A band far from the tone must leave it alone.</summary>
    [Fact]
    public void BandGainLeavesDistantFrequenciesAlone()
    {
        var equaliser = new Equaliser();
        equaliser.SetGain(0, 12.0);  // 32 Hz
        equaliser.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(4000, 0.25, frames);
        equaliser.Process(buffer, frames);

        Assert.InRange(PeakAfter(buffer, 4800), 0.25 * 0.97, 0.25 * 1.03);
    }

    /// <summary>Turning the equaliser off bypasses it entirely, whatever the faders say.</summary>
    [Fact]
    public void DisabledEqualiserIsBypassed()
    {
        var equaliser = new Equaliser { Enabled = false };
        for (var band = 0; band < Equaliser.Frequencies.Length; band++) equaliser.SetGain(band, 12);
        equaliser.Prepare(SampleRate, Channels);

        var original = Sine(1000, 0.5, 2048);
        var processed = (float[])original.Clone();
        equaliser.Process(processed, 2048);

        Assert.Equal(original, processed);
    }

    /// <summary>The preamp scales everything, and its decibel value has to mean what it says.</summary>
    [Fact]
    public void PreampAppliesTheStatedDecibels()
    {
        var equaliser = new Equaliser { Preamp = -6.0 };
        equaliser.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(1000, 0.5, frames);
        equaliser.Process(buffer, frames);

        var expected = 0.5 * Math.Pow(10, -6.0 / 20);
        Assert.InRange(PeakAfter(buffer, 4800), expected * 0.97, expected * 1.03);
    }

    // ---------- limiter ----------

    /// <summary>Nothing may leave the limiter above full scale, whatever arrives.</summary>
    [Fact]
    public void LimiterKeepsTheSignalInRange()
    {
        var limiter = new Limiter();
        limiter.Prepare(SampleRate, Channels);

        const int frames = 48000;
        var buffer = Sine(440, 2.5, frames);   // grubo powyżej pełnej skali
        limiter.Process(buffer, frames);

        foreach (var sample in buffer) Assert.InRange(sample, -1.0f, 1.0f);
    }

    /// <summary>
    /// Quiet material must pass through untouched apart from the lookahead delay. A limiter
    /// that quietly compresses everything would defeat the point of having one.
    /// </summary>
    [Fact]
    public void LimiterLeavesQuietMaterialAlone()
    {
        var limiter = new Limiter();
        limiter.Prepare(SampleRate, Channels);

        const int frames = 24000;
        var original = Sine(440, 0.2, frames);
        var processed = (float[])original.Clone();
        limiter.Process(processed, frames);

        // Lookahead o 5 ms = 240 ramek przy 48 kHz.
        const int delay = 240;
        for (var frame = delay; frame < frames; frame++)
        {
            var expected = original[(frame - delay) * Channels];
            Assert.Equal(expected, processed[frame * Channels], tolerance: 1e-3f);
        }
    }

    /// <summary>Gain reduction is shared, so a loud left channel must not shift the image.</summary>
    [Fact]
    public void LimiterAppliesTheSameReductionToBothChannels()
    {
        var limiter = new Limiter();
        limiter.Prepare(SampleRate, Channels);

        const int frames = 24000;
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(1.8 * Math.Sin(2 * Math.PI * 440 * frame / SampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value * 0.5f;   // prawy kanał cichszy o 6 dB
        }

        limiter.Process(buffer, frames);

        // Stosunek kanałów musi przetrwać limitowanie.
        for (var frame = frames / 2; frame < frames; frame++)
        {
            var left = buffer[frame * Channels];
            var right = buffer[frame * Channels + 1];
            if (Math.Abs(left) < 0.05f) continue;

            Assert.InRange(right / left, 0.48f, 0.52f);
        }
    }

    // ---------- wzmocnienie ----------

    /// <summary>The gain stage ramps instead of stepping, which is what keeps it silent.</summary>
    [Fact]
    public void GainStageRampsRatherThanJumps()
    {
        var gain = new GainStage(smoothingSeconds: 0.02);
        gain.Prepare(SampleRate, Channels);
        gain.Target = 1;
        gain.SnapToTarget();

        gain.Target = 0;

        var buffer = new float[2048 * Channels];
        Array.Fill(buffer, 1f);
        gain.Process(buffer, 2048);

        // Pierwsza próbka nie może spaść od razu do zera.
        Assert.True(buffer[0] > 0.9f, "wzmocnienie zmieniło się skokowo");

        // Ale w ciągu kilku okresów wygładzania musi dojść blisko celu.
        Assert.True(buffer[^1] < 0.15f, "wzmocnienie nie dotarło do celu");
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-6.0, 0.5012)]
    [InlineData(6.0, 1.9953)]
    public void GainStageConvertsDecibelsCorrectly(double decibels, double expectedLinear)
    {
        var gain = new GainStage();
        gain.TargetDecibels = decibels;
        Assert.Equal(expectedLinear, gain.Target, tolerance: 0.001);
    }

    // ---------- pomocnicze ----------

    private static float[] Sine(double frequency, double amplitude, int frames)
    {
        var buffer = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / SampleRate));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value;
        }

        return buffer;
    }

    private static double PeakAfter(float[] buffer, int skipFrames)
    {
        var peak = 0.0;
        for (var frame = skipFrames; frame < buffer.Length / Channels; frame++)
            peak = Math.Max(peak, Math.Abs(buffer[frame * Channels]));

        return peak;
    }
}
