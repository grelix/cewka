using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Fala na ekranie wygląda tak samo niezależnie od urządzenia i rozmiaru bufora.</summary>
public class SpectrumAnalyserTests
{
    /// <summary>
    /// Tempo opadania wskaźnika po ucichnięciu muzyki nie zależy od okresu urządzenia:
    /// po 200 ms ciszy poziom jest ten sam przy buforach 256 i 2048 ramek.
    /// </summary>
    [Fact]
    public void DecayDoesNotDependOnBufferSize()
    {
        var small = LevelAfterSilence(256);
        var large = LevelAfterSilence(2048);

        Assert.True(small > 0.01, $"wskaźnik opadł do zera ({small:F4})");
        Assert.InRange(large, small * 0.75, small * 1.25);
    }

    /// <summary>
    /// Ton 100 Hz trafia w pasmo niskie tak samo przy 48 i 96 kHz; przy stałym oknie prążek
    /// przy 96 kHz byłby dwa razy szerszy i bas wypadałby między prążki.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    public void BandsDoNotDependOnSampleRate(double frequency)
    {
        var at48 = Bands(48000, frequency);
        var at96 = Bands(96000, frequency);

        Assert.InRange(at96.Low, at48.Low * 0.85 - 0.01, at48.Low * 1.15 + 0.01);
        Assert.InRange(at96.Mid, at48.Mid * 0.85 - 0.01, at48.Mid * 1.15 + 0.01);
    }

    private static float LevelAfterSilence(int block)
    {
        const int sampleRate = 48000;
        var analyser = new SpectrumAnalyser();
        analyser.Prepare(sampleRate, Channels);

        ProcessInBlocks(analyser, Sine(1000, 0.5, block * (sampleRate / 2 / block), sampleRate), block);
        ProcessInBlocks(analyser, new float[block * (int)Math.Round(0.2 * sampleRate / block) * Channels], block);
        return analyser.Level;
    }

    private static (float Low, float Mid) Bands(int sampleRate, double frequency)
    {
        var analyser = new SpectrumAnalyser();
        analyser.Prepare(sampleRate, Channels);
        ProcessInBlocks(analyser, Sine(frequency, 0.3, sampleRate, sampleRate), 480);
        return (analyser.LowBand, analyser.MidBand);
    }
}
