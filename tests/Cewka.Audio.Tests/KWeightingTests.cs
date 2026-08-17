using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// The K-weighting filter is derived from its prototype parameters rather than hard-coded,
/// so that it can be built at any sample rate. These tests confirm the derivation reproduces
/// the coefficient tables printed in ITU-R BS.1770-4 for 48 kHz, which is the only rate the
/// specification tabulates.
/// </summary>
public class KWeightingTests
{
    // Tablica 1 z BS.1770-4: pierwszy stopien, polka gorna.
    private const double ShelfB0 = 1.53512485958697;
    private const double ShelfB1 = -2.69169618940638;
    private const double ShelfB2 = 1.19839281085285;
    private const double ShelfA1 = -1.69065929318241;
    private const double ShelfA2 = 0.73248077421585;

    // Tablica 2 z BS.1770-4: drugi stopien, filtr gornoprzepustowy RLB.
    private const double HighPassA1 = -1.99004745483398;
    private const double HighPassA2 = 0.99007225036621;

    [Fact]
    public void ShelfStageMatchesTheSpecificationAt48kHz()
    {
        var filter = new Biquad();
        filter.SetKWeightingShelf(48000);

        var (b0, b1, b2, a1, a2) = filter.Coefficients;

        Assert.Equal(ShelfB0, b0, precision: 6);
        Assert.Equal(ShelfB1, b1, precision: 6);
        Assert.Equal(ShelfB2, b2, precision: 6);
        Assert.Equal(ShelfA1, a1, precision: 6);
        Assert.Equal(ShelfA2, a2, precision: 6);
    }

    [Fact]
    public void HighPassStageMatchesTheSpecificationAt48kHz()
    {
        var filter = new Biquad();
        filter.SetKWeightingHighPass(48000);

        var (b0, b1, b2, a1, a2) = filter.Coefficients;

        // Licznik filtru RLB to dokladnie 1, -2, 1.
        Assert.Equal(1.0, b0, precision: 5);
        Assert.Equal(-2.0, b1, precision: 5);
        Assert.Equal(1.0, b2, precision: 5);
        Assert.Equal(HighPassA1, a1, precision: 5);
        Assert.Equal(HighPassA2, a2, precision: 5);
    }

    /// <summary>
    /// The calibration case named in the specification: a 997 Hz sine at full scale in a
    /// single channel of a stereo pair measures −3.01 LKFS.
    /// </summary>
    [Fact]
    public void FullScaleToneInOneChannelMeasuresMinusThreeDecibels()
    {
        const int sampleRate = 48000;
        var analyser = new LoudnessAnalyser(sampleRate, 2);

        const int frames = sampleRate * 10;
        var buffer = new float[4096 * 2];
        var produced = 0;

        while (produced < frames)
        {
            var chunk = Math.Min(4096, frames - produced);
            for (var i = 0; i < chunk; i++)
            {
                buffer[i * 2] = (float)Math.Sin(2 * Math.PI * 997 * (produced + i) / sampleRate);
                buffer[i * 2 + 1] = 0;
            }

            analyser.Add(buffer, chunk);
            produced += chunk;
        }

        var measured = analyser.ComputeIntegratedLoudness();

        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, -3.31, -2.71);
    }

    /// <summary>
    /// The derivation has to hold at the other rates a music library actually contains.
    /// A tone of a given level must measure the same whatever the rate it was sampled at.
    /// </summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void MeasurementIsIndependentOfSampleRate(int sampleRate)
    {
        var analyser = new LoudnessAnalyser(sampleRate, 2);
        const double amplitude = 0.0707945784;   // -23 dBFS

        var frames = sampleRate * 8;
        var buffer = new float[4096 * 2];
        var produced = 0;

        while (produced < frames)
        {
            var chunk = Math.Min(4096, frames - produced);
            for (var i = 0; i < chunk; i++)
            {
                var value = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * (produced + i) / sampleRate));
                buffer[i * 2] = value;
                buffer[i * 2 + 1] = value;
            }

            analyser.Add(buffer, chunk);
            produced += chunk;
        }

        var measured = analyser.ComputeIntegratedLoudness();

        Assert.NotNull(measured);
        Assert.InRange(measured!.Value, -23.4, -22.6);
    }

    /// <summary>
    /// The limiter must do its work by reducing gain, not by running into the safety clamp.
    /// A loud sine should come out near the threshold, clearly below full scale — if it came
    /// out at exactly ±1 the output would be clipped, which is the very thing being avoided.
    /// </summary>
    [Fact]
    public void LimiterReducesGainRatherThanClipping()
    {
        const int sampleRate = 48000;
        var limiter = new Limiter();
        limiter.Prepare(sampleRate, 2);

        const int frames = sampleRate;
        var buffer = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(2.0 * Math.Sin(2 * Math.PI * 440 * frame / sampleRate));
            buffer[frame * 2] = value;
            buffer[frame * 2 + 1] = value;
        }

        limiter.Process(buffer, frames);

        // Po ustabilizowaniu obwiedni szczyt powinien siedziec przy progu -1 dBFS.
        var peak = 0f;
        for (var frame = sampleRate / 2; frame < frames; frame++)
            peak = Math.Max(peak, Math.Abs(buffer[frame * 2]));

        Assert.InRange(peak, 0.80f, 0.95f);
    }
}
