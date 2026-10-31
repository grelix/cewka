using Cewka.Audio.Dsp;
using Xunit;
using static Cewka.Audio.Tests.Signals;

namespace Cewka.Audio.Tests;

/// <summary>Odporność toru na nietypowe częstotliwości próbkowania, NaN i ciszę cyfrową.</summary>
public class RobustnessTests
{
    /// <summary>
    /// Urządzenia o niskim próbkowaniu (zestaw słuchawkowy w trybie rozmowy, stare karty)
    /// nie mogą rozregulować półki górnej kompensacji: wynik ma zostać skończony i ograniczony.
    /// </summary>
    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(12000)]
    [InlineData(16000)]
    public void LoudnessStaysStableAtLowSampleRates(int sampleRate)
    {
        var loudness = new Loudness { Enabled = true, Strength = 100 };
        loudness.SetListeningLevel(-30, 0);
        loudness.Prepare(sampleRate, Channels);

        // Szum szerokopasmowy pobudza i okolice Nyquista, i składową stałą.
        var random = new Random(1234);
        var buffer = new float[1024 * Channels];
        var peak = 0.0;

        for (var block = 0; block < 1000; block++)
        {
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (float)(random.NextDouble() - 0.5);
            loudness.Process(buffer, 1024);

            Assert.True(AllFinite(buffer), $"wynik przestał być skończony w bloku {block}");
            peak = Math.Max(peak, Peak(buffer));
        }

        Assert.True(peak < 4, $"wynik rozbiega się (szczyt {peak:F2})");
    }

    /// <summary>Półka powyżej 0,45 częstotliwości próbkowania jest przepustem, nie wzmocnieniem całego pasma.</summary>
    [Fact]
    public void HighShelfAboveNyquistIsAPassThrough()
    {
        var shelf = Biquad.Identity();
        shelf.SetHighShelf(8000, 0.7071, 4, 8000);

        Assert.Equal((1.0, 0.0, 0.0, 0.0, 0.0), shelf.Coefficients);
    }

    /// <summary>Stan, który przestał być liczbą skończoną, zostaje wyzerowany i filtr wraca do pracy.</summary>
    [Fact]
    public void SanitiseRecoversFromNaN()
    {
        var filter = Biquad.Identity();
        filter.SetPeaking(1000, 1.4, 6, 48000);

        filter.Process(float.PositiveInfinity);
        filter.Process(0);
        Assert.True(float.IsNaN(filter.Process(0)));

        filter.Sanitise();
        Assert.Equal(0f, filter.Process(0));
        Assert.True(float.IsFinite(filter.Process(0.5f)));
    }

    /// <summary>Stan wygasły w ciszy cyfrowej trafia w zero, zanim przejdzie w liczby subnormalne.</summary>
    [Fact]
    public void SanitiseFlushesADecayedState()
    {
        var filter = Biquad.Identity();
        filter.SetPeaking(32, 1.4, 12, 48000);

        filter.ProcessDouble(1);
        for (var i = 0; i < 48000 * 3; i++) filter.ProcessDouble(0);
        Assert.NotEqual(0.0, filter.ProcessDouble(0));

        filter.Sanitise();

        // Po wyzerowaniu stanu filtr na wejściu zero daje dokładne zero.
        Assert.Equal(0.0, filter.ProcessDouble(0));
    }
}
