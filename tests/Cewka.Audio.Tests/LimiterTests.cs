using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Limiter wobec krótkich stanów nieustalonych i przełączania.</summary>
public class LimiterTests
{
    private const int SampleRate = 48000;

    /// <summary>Próg −1 dBFS jako amplituda, z zapasem na zaokrąglenia w float.</summary>
    private static readonly double Ceiling = Math.Pow(10, -1.0 / 20) + 0.002;

    /// <summary>
    /// Pojedyncza próbka dwukrotnie ponad pełną skalą musi zostać sprowadzona do progu
    /// wzmocnieniem, a nie obcięciem: wynik poniżej progu, a nie równy jedności.
    /// </summary>
    [Fact]
    public void SingleSampleImpulseIsLimitedNotClipped()
    {
        var limiter = Prepared(SampleRate);
        var buffer = new float[4800 * Channels];
        buffer[1000 * Channels] = 2f;
        buffer[1000 * Channels + 1] = -2f;

        limiter.Process(buffer, 4800);

        var peak = Peak(buffer);
        Assert.True(peak <= Ceiling, $"szczyt {peak:F3} ponad progiem");
        Assert.True(peak > 0.85, $"impuls zniknął zamiast zostać ograniczony ({peak:F3})");
    }

    /// <summary>
    /// Seria uderzeń po 15 próbek, 4 dB ponad progiem, jak talerz albo werbel podbity korektorem.
    /// Wyprzedzenie ma zdążyć: żadna próbka ponad progiem.
    /// </summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void ShortBurstsStayUnderTheThreshold(int sampleRate)
    {
        var limiter = Prepared(sampleRate);
        var frames = sampleRate;
        var buffer = new float[frames * Channels];

        for (var start = 500; start + 15 < frames; start += 3000)
        for (var i = 0; i < 15; i++)
        {
            var value = (i % 2 == 0 ? 1.41f : -1.41f);
            buffer[(start + i) * Channels] = value;
            buffer[(start + i) * Channels + 1] = value;
        }

        limiter.Process(buffer, frames);

        Assert.True(Peak(buffer) <= Ceiling, $"szczyt {Peak(buffer):F3} ponad progiem");
    }

    /// <summary>
    /// Wyłączenie w trakcie gry nie może przeskoczyć z sygnału opóźnionego na bieżący: byłby
    /// to skok w przebiegu i 5 ms dźwięku znikające bez śladu.
    /// </summary>
    [Fact]
    public void DisablingKeepsTheWaveformContinuous()
    {
        var limiter = Prepared(SampleRate);
        var tone = Sine(440, 0.5, 9600, SampleRate);

        limiter.Process(tone.AsSpan(0, 4800 * Channels), 4800);
        limiter.Enabled = false;
        limiter.Process(tone.AsSpan(4800 * Channels), 4800);

        // Największy krok sinusa 440 Hz o amplitudzie 0,5 to około 0,029 na próbkę.
        var step = LargestStep(tone, channel: 0);
        Assert.True(step < 0.035, $"skok w przebiegu {step:F3}");
    }

    /// <summary>Ponowne włączenie nie wypuszcza dźwięku zapamiętanego w chwili wyłączenia.</summary>
    [Fact]
    public void ReenablingDoesNotReplayStaleAudio()
    {
        var limiter = Prepared(SampleRate);
        var tone = Sine(440, 0.5, 4800, SampleRate);
        limiter.Process(tone, 4800);

        limiter.Enabled = false;
        var silence = new float[48000 * Channels];
        limiter.Process(silence, 48000);

        limiter.Enabled = true;
        var after = new float[4800 * Channels];
        limiter.Process(after, 4800);

        Assert.Equal(0.0, Peak(after));
    }

    /// <summary>Włączenie przy głośnym materiale wprowadza redukcję płynnie, bez skoku wzmocnienia.</summary>
    [Fact]
    public void EnablingRampsTheReductionIn()
    {
        var limiter = new Limiter { Enabled = false };
        limiter.Prepare(SampleRate, Channels);

        // Stała dodatnia ponad progiem: każda zmiana wyjścia to zmiana wzmocnienia.
        var buffer = new float[9600 * Channels];
        Array.Fill(buffer, 1.5f);
        limiter.Process(buffer.AsSpan(0, 4800 * Channels), 4800);
        limiter.Enabled = true;
        limiter.Process(buffer.AsSpan(4800 * Channels), 4800);

        // Pierwsze ramki to jeszcze zera z linii opóźnienia.
        var settled = buffer[(1000 * Channels)..];
        Assert.True(LargestStep(settled, channel: 0) < 0.01, $"skok wzmocnienia {LargestStep(settled, 0):F3}");
        Assert.True(buffer[^1] < 0.9f, "redukcja nie weszła");
    }

    /// <summary>NaN na wejściu nie może dotrzeć do urządzenia: porównania z NaN są fałszywe.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NaNNeverReachesTheOutput(bool enabled)
    {
        var limiter = new Limiter { Enabled = enabled };
        limiter.Prepare(SampleRate, Channels);

        var buffer = Sine(440, 0.5, 4800, SampleRate);
        buffer[100] = float.NaN;
        limiter.Process(buffer, 4800);

        Assert.True(AllFinite(buffer));
    }

    private static Limiter Prepared(int sampleRate)
    {
        var limiter = new Limiter();
        limiter.Prepare(sampleRate, Channels);
        return limiter;
    }
}
