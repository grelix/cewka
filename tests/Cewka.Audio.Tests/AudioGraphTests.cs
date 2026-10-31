using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Cały łańcuch: kolejność stopni i odporność na uszkodzone próbki.</summary>
public class AudioGraphTests
{
    private const int SampleRate = 48000;

    /// <summary>
    /// Przy cichym słuchaniu podbicie korektora mieści się w skali po ściszeniu. Limiter chroni
    /// rzeczywiste wyjście, więc nie ma czego ograniczać, a podbicie zostaje w całości.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LimiterActsOnTheOutputLevelAfterTheVolume(bool limiterEnabled)
    {
        var graph = new AudioGraph();
        graph.Equaliser.SetGain(5, 12);       // 1 kHz
        graph.Volume.Target = 0.1f;
        graph.Limiter.Enabled = limiterEnabled;
        graph.Prepare(SampleRate, Channels);

        var buffer = Sine(1000, 0.9, SampleRate, SampleRate);
        ProcessInBlocks(graph, buffer, 480);

        var expected = 0.9 * Math.Pow(10, 12.0 / 20) * 0.1;
        var measured = Goertzel(buffer, 0, 1000, SampleRate, skipFrames: 9600);

        Assert.InRange(measured, expected * 0.97, expected * 1.03);
        Assert.InRange(graph.Limiter.GainReductionDb, -0.01, 0);
    }

    /// <summary>Przy pełnej głośności limiter nadal sprowadza przesterowany sygnał do progu.</summary>
    [Fact]
    public void LimiterStillProtectsAtFullVolume()
    {
        var graph = new AudioGraph();
        graph.Equaliser.SetGain(5, 12);
        graph.Prepare(SampleRate, Channels);

        var buffer = Sine(1000, 0.9, SampleRate, SampleRate);
        ProcessInBlocks(graph, buffer, 480);

        Assert.InRange(Peak(buffer, skipFrames: 9600), 0.85, 0.9);
    }

    /// <summary>
    /// Jedna próbka NaN z uszkodzonego pliku nie może wyciszyć odtwarzania: kolejne bufory
    /// grają normalnie, a fala na ekranie nie zamiera.
    /// </summary>
    [Fact]
    public void NaNInTheInputDoesNotPoisonTheChain()
    {
        var graph = new AudioGraph();
        graph.Equaliser.SetGain(0, 6);
        graph.Equaliser.SetGain(5, 6);
        graph.Loudness.Enabled = true;
        graph.Volume.Target = 0.5f;
        graph.Prepare(SampleRate, Channels);

        var damaged = Sine(1000, 0.25, 4800, SampleRate);
        damaged[1000] = float.NaN;
        damaged[2001] = float.PositiveInfinity;
        ProcessInBlocks(graph, damaged, 480);

        var after = Sine(1000, 0.25, SampleRate, SampleRate);
        ProcessInBlocks(graph, after, 480);

        Assert.True(AllFinite(damaged));
        Assert.True(Goertzel(after, 0, 1000, SampleRate, skipFrames: 9600) > 0.1);
        Assert.True(float.IsFinite(graph.Analyser.Level) && graph.Analyser.Level > 0);
    }

    /// <summary>
    /// Po przewinięciu silnik czyści tor. W linii opóźnienia limitera, która pracuje także po
    /// jego wyłączeniu, ani w filtrach nie może zostać stary dźwięk: cisza na wejściu daje
    /// dokładną ciszę na wyjściu od pierwszej próbki.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetLeavesNoStaleAudioInTheChain(bool limiterEnabled)
    {
        var graph = new AudioGraph();
        graph.Equaliser.SetGain(0, 6);
        graph.Loudness.Enabled = true;
        graph.VirtualBass.Enabled = true;
        graph.StereoWidth.Enabled = true;
        graph.Crossfeed.Enabled = true;
        graph.Limiter.Enabled = limiterEnabled;
        graph.Prepare(SampleRate, Channels);

        var loud = Sine(1000, 0.9, 4800, SampleRate);
        ProcessInBlocks(graph, loud, 480);

        graph.Reset();

        var silence = new float[480 * Channels];
        graph.Process(silence, 480);

        Assert.All(silence, sample => Assert.Equal(0f, sample));
    }

    /// <summary>Analizator sam w sobie też wraca do pracy po próbce NaN.</summary>
    [Fact]
    public void AnalyserRecoversFromNaN()
    {
        var analyser = new SpectrumAnalyser();
        analyser.Prepare(SampleRate, Channels);

        // Próbka NaN w każdym oknie analizy.
        var damaged = Sine(1000, 0.5, 4800, SampleRate);
        for (var frame = 100; frame < 4800; frame += 200) damaged[frame * Channels] = float.NaN;
        ProcessInBlocks(analyser, damaged, 480);

        var clean = Sine(1000, 0.5, 9600, SampleRate);
        ProcessInBlocks(analyser, clean, 480);

        Assert.True(float.IsFinite(analyser.Level) && analyser.Level > 0);
        Assert.True(float.IsFinite(analyser.MidBand) && analyser.MidBand > 0);
    }
}
