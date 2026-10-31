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
}
