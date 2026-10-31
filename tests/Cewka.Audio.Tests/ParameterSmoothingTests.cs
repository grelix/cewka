using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>
/// Zmiany ustawień i włączanie stopni w trakcie odtwarzania. Skok mnożnika albo współczynników
/// w jednej próbce to skok w przebiegu, czyli trzask; miarą jest największa różnica między
/// sąsiednimi próbkami wyniku.
/// </summary>
public class ParameterSmoothingTests
{
    private const int SampleRate = 48000;
    private const int Block = 480;

    /// <summary>Największy krok sinusa o danej częstotliwości i amplitudzie, z zapasem 20%.</summary>
    private static double NaturalStep(double frequency, double amplitude) =>
        1.2 * amplitude * 2 * Math.PI * frequency / SampleRate;

    // ================= wzmocnienie =================

    /// <summary>
    /// Po powrocie do jedności wzmocnienie dochodzi do celu dokładnie, więc stopień znowu
    /// przepuszcza próbki bez mnożenia.
    /// </summary>
    [Fact]
    public void GainStageSettlesExactlyOnUnity()
    {
        var gain = new GainStage(smoothingSeconds: 0.02);
        gain.Prepare(SampleRate, Channels);
        gain.Target = 0.5f;
        ProcessInBlocks(gain, new float[SampleRate * Channels], Block);
        gain.Target = 1f;
        ProcessInBlocks(gain, Sine(440, 0.5, SampleRate, SampleRate), Block);

        var original = Sine(440, 0.5, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(gain, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Głośność zero daje dokładne zero, a nie mnożenie przez liczbę subnormalną.</summary>
    [Fact]
    public void GainStageAtZeroOutputsExactSilence()
    {
        var gain = new GainStage(smoothingSeconds: 0.02);
        gain.Prepare(SampleRate, Channels);
        gain.Target = 0f;
        ProcessInBlocks(gain, Sine(440, 0.5, 2 * SampleRate, SampleRate), Block);

        var processed = Sine(440, 0.5, 4800, SampleRate);
        ProcessInBlocks(gain, processed, Block);
        Assert.All(processed, sample => Assert.Equal(0f, sample));
    }

    // ---------- pomocnicze ----------

    /// <summary>Wzmocnienie sygnału boku na zadanej częstotliwości, w decybelach.</summary>
    private static double SideGainDb(double frequency, double strength)
    {
        var width = new StereoWidth { Enabled = true, Strength = strength };
        width.Prepare(SampleRate, Channels);

        var buffer = SideOnly(frequency, 0.25, 2 * SampleRate, SampleRate);
        ProcessInBlocks(width, buffer, Block);
        return Db(Goertzel(buffer, 0, frequency, SampleRate, SampleRate) / 0.25);
    }

    /// <summary>Różnica poziomów 60 Hz między kanałem z sygnałem a przeciwnym, w decybelach.</summary>
    private static double BassSeparationDb(double strength)
    {
        var crossfeed = new Crossfeed { Enabled = true, Strength = strength };
        crossfeed.Prepare(SampleRate, Channels);

        var buffer = new float[SampleRate * Channels];
        for (var frame = 0; frame < SampleRate; frame++)
            buffer[frame * Channels] = (float)(0.5 * Math.Sin(2 * Math.PI * 60 * frame / SampleRate));
        ProcessInBlocks(crossfeed, buffer, Block);

        var direct = Goertzel(buffer, 0, 60, SampleRate, 9600);
        var cross = Goertzel(buffer, 1, 60, SampleRate, 9600);
        return Db(direct / cross);
    }
}
