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

    // ================= ograniczanie dynamiki =================

    /// <summary>
    /// Włączenie „trybu nocnego” na 100% w trakcie głośnej muzyki nie może wypchnąć wzmocnienia
    /// o kilkanaście decybeli w jednej próbce.
    /// </summary>
    [Fact]
    public void DynamicRangeSwitchesOnAndOffWithoutAStep()
    {
        var dynamics = new DynamicRange { Enabled = false, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);

        var tone = Sine(100, 0.5, 3 * SampleRate, SampleRate);

        ProcessRange(dynamics, tone, 0, SampleRate);
        dynamics.Enabled = true;
        ProcessRange(dynamics, tone, SampleRate, 2 * SampleRate);
        dynamics.Enabled = false;
        ProcessRange(dynamics, tone, 2 * SampleRate, 3 * SampleRate);

        Assert.True(LargestStep(tone, 0) < NaturalStep(100, 0.5) * 2, $"skok {LargestStep(tone, 0):F4}");
    }

    /// <summary>Po wygaszeniu wyłączony stopień przepuszcza sygnał bez zmiany ani jednej próbki.</summary>
    [Fact]
    public void DynamicRangeBypassesOnceFadedOut()
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 100 };
        dynamics.Prepare(SampleRate, Channels);
        ProcessInBlocks(dynamics, Sine(100, 0.5, SampleRate, SampleRate), Block);

        dynamics.Enabled = false;
        ProcessInBlocks(dynamics, Sine(100, 0.5, SampleRate, SampleRate), Block);

        var original = Sine(100, 0.5, 4800, SampleRate);
        var processed = (float[])original.Clone();
        ProcessInBlocks(dynamics, processed, Block);
        Assert.Equal(original, processed);
    }

    /// <summary>Zmiana siły na cichym materiale przesuwa wzmocnienie wyrównawcze płynnie.</summary>
    [Fact]
    public void DynamicRangeStrengthChangeIsSmooth()
    {
        var dynamics = new DynamicRange { Enabled = true, Strength = 10 };
        dynamics.Prepare(SampleRate, Channels);

        // Przebieg stały: każda zmiana wyniku jest zmianą wzmocnienia.
        var buffer = new float[2 * SampleRate * Channels];
        Array.Fill(buffer, 0.01f);

        ProcessRange(dynamics, buffer, 0, SampleRate);
        dynamics.Strength = 100;
        ProcessRange(dynamics, buffer, SampleRate, 2 * SampleRate);

        Assert.True(LargestStep(buffer, 0, fromFrame: 4800) < 0.002, $"skok wzmocnienia {LargestStep(buffer, 0, 4800):F4}");
        Assert.True(buffer[^1] > 0.03f, "wzmocnienie wyrównawcze nie weszło");
    }

    // ================= bas wirtualny =================

    /// <summary>
    /// Wyłączenie w chwili, gdy domieszka harmonicznych jest największa, nie może jej uciąć
    /// w jednej próbce. Miarą jest sama domieszka: wynik minus wejście.
    /// </summary>
    [Fact]
    public void VirtualBassSwitchesOffWithoutAStep()
    {
        var original = Sine(45, 0.5, 3 * SampleRate, SampleRate);

        // Przebieg domieszki przy stopniu włączonym bez przerwy wskazuje chwilę jej szczytu.
        var probe = new VirtualBass { Enabled = true, Strength = 100 };
        probe.Prepare(SampleRate, Channels);
        var reference = (float[])original.Clone();
        ProcessRange(probe, reference, 0, 3 * SampleRate);

        var switchFrame = 2 * SampleRate;
        for (var frame = 2 * SampleRate; frame < 2 * SampleRate + 1067; frame++)
            if (Math.Abs(reference[frame * Channels] - original[frame * Channels]) >
                Math.Abs(reference[switchFrame * Channels] - original[switchFrame * Channels])) switchFrame = frame;

        var bass = new VirtualBass { Enabled = true, Strength = 100 };
        bass.Prepare(SampleRate, Channels);
        var tone = (float[])original.Clone();
        ProcessRange(bass, tone, 0, switchFrame);
        bass.Enabled = false;
        ProcessRange(bass, tone, switchFrame, 3 * SampleRate);

        var added = new float[tone.Length];
        for (var i = 0; i < tone.Length; i++) added[i] = tone[i] - original[i];

        var steady = LargestStep(added[..((switchFrame - 1) * Channels)], 0, fromFrame: SampleRate);
        Assert.True(LargestStep(added, 0, fromFrame: SampleRate) <= steady * 1.5,
            $"skok domieszki {LargestStep(added, 0, SampleRate):F4} wobec {steady:F4} w stanie ustalonym");
    }
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
