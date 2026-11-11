using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Ogranicznik błysków: najwyżej 3 przejścia luminancji o 10% na sekundę, wolne zmiany bez zmian.</summary>
public class FlashLimiterTests
{
    private const double Frame = 1.0 / 60;

    /// <summary>Czasy przejść: odwróceń kierunku o co najmniej 10% luminancji.</summary>
    private static List<double> Transitions(IReadOnlyList<double> series)
    {
        var times = new List<double>();
        var extreme = series[0];
        var direction = 0;

        for (var i = 1; i < series.Count; i++)
        {
            var v = series[i];
            if (direction == 0)
            {
                if (Math.Abs(v - extreme) < FlashLimiter.Threshold) continue;
                direction = Math.Sign(v - extreme);
                extreme = v;
                times.Add(i * Frame);
                continue;
            }

            if ((v - extreme) * direction > 0) extreme = v;
            else if ((extreme - v) * direction >= FlashLimiter.Threshold)
            {
                direction = -direction;
                extreme = v;
                times.Add(i * Frame);
            }
        }

        return times;
    }

    private static int MostInOneSecond(List<double> times)
    {
        var most = 0;
        for (var i = 0; i < times.Count; i++)
        {
            var count = 0;
            for (var j = i; j < times.Count && times[j] - times[i] < 1.0 - 1e-6; j++) count++;
            most = Math.Max(most, count);
        }

        return most;
    }

    private static List<double> Run(Func<int, double> input, int frames)
    {
        var limiter = new FlashLimiter();
        var output = new List<double>(frames);
        for (var i = 0; i < frames; i++) output.Add(limiter.Limit(input(i), Frame));
        return output;
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void BassPulsesAreHeldToThreeTransitionsPerSecond(double hertz)
    {
        double Pulse(int i) => Math.Floor(i * Frame * hertz * 2) % 2 == 0 ? 0.9 : 0.1;

        var input = Enumerable.Range(0, 600).Select(i => Pulse(i)).ToList();
        Assert.True(MostInOneSecond(Transitions(input)) > 3, "sygnał testowy nie przekracza progu");

        var output = Run(Pulse, 600);

        Assert.True(MostInOneSecond(Transitions(output)) <= 3);
    }

    [Fact]
    public void NoiseIsHeldToThreeTransitionsPerSecond()
    {
        var random = new Random(20261105);
        var values = Enumerable.Range(0, 900).Select(_ => random.NextDouble()).ToArray();

        var output = Run(i => values[i], 900);

        Assert.True(MostInOneSecond(Transitions(output)) <= 3);
    }

    [Fact]
    public void JumpFromSilenceToFullScaleIsHeldToThreeTransitions()
    {
        // Cisza, pełna skala, cisza, pełna skala: szybkie skoki całego kadru.
        double Jump(int i) => (i / 6) % 2 == 0 ? 0.0 : 1.0;

        var output = Run(Jump, 600);

        Assert.True(MostInOneSecond(Transitions(output)) <= 3);
    }

    [Fact]
    public void RiseNeverBeatsEightyMillisecondsAndFallNeverBeatsTwoHundredFiftyMilliseconds()
    {
        var limiter = new FlashLimiter();
        limiter.Limit(0, Frame);

        var frames = 0;
        while (limiter.Limit(1, Frame) < 1) frames++;
        Assert.InRange(frames * Frame, FlashLimiter.RiseSeconds - 2 * Frame, 1.0);

        frames = 0;
        while (limiter.Limit(0, Frame) > 0) frames++;
        Assert.InRange(frames * Frame, FlashLimiter.FallSeconds - 2 * Frame, 1.0);
    }

    [Fact]
    public void SlowChangesPassUntouched()
    {
        double Slow(int i) => 0.5 + 0.3 * Math.Sin(2 * Math.PI * 0.5 * i * Frame);

        var output = Run(Slow, 600);

        for (var i = 0; i < output.Count; i++) Assert.Equal(Slow(i), output[i], 9);
    }

    [Fact]
    public void NonFiniteInputIsHarmless()
    {
        var limiter = new FlashLimiter();
        limiter.Limit(0.5, Frame);

        Assert.True(double.IsFinite(limiter.Limit(double.NaN, Frame)));
        Assert.True(double.IsFinite(limiter.Limit(double.PositiveInfinity, double.NaN)));
    }

    [Fact]
    public void LimiterDoesNotAllocate()
    {
        var limiter = new FlashLimiter();
        for (var i = 0; i < 200; i++) limiter.Limit(i % 2, Frame);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) limiter.Limit(i % 7 / 6.0, Frame);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void FrameLuminanceMeasuresBlackWhiteAndGrey()
    {
        var pixels = FrameLuminance.Width * FrameLuminance.Height;
        var buffer = new byte[pixels * 4];

        Assert.Equal(0, FrameLuminance.Measure(buffer));

        Array.Fill(buffer, (byte)255);
        Assert.Equal(1, FrameLuminance.Measure(buffer), 6);

        for (var i = 0; i < pixels; i++)
        {
            buffer[i * 4] = buffer[i * 4 + 1] = buffer[i * 4 + 2] = 128;
            buffer[i * 4 + 3] = 255;
        }

        Assert.Equal(0.2158605, FrameLuminance.Measure(buffer), 4);
    }
}
