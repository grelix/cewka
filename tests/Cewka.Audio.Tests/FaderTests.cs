using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Liniowe wyciszanie miękkiej pauzy, przebieg po przebiegu.</summary>
public class FaderTests
{
    private const int Rate = 1000;

    private static Fader Prepared()
    {
        var fader = new Fader();
        fader.Prepare(Rate, 1);
        return fader;
    }

    private static float[] Run(Fader fader, int frames, float value = 1f)
    {
        var buffer = Enumerable.Repeat(value, frames).ToArray();
        fader.Process(buffer, frames);
        return buffer;
    }

    [Fact]
    public void BezZleceniaPrzepuszczaBezZmian()
    {
        var output = Run(Prepared(), 10, 0.5f);
        Assert.All(output, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void WyciszenieJestLinioweIKonczySieWZadanymCzasie()
    {
        var fader = Prepared();
        fader.FadeTo(0, 0.1);

        var output = Run(fader, 120);

        Assert.Equal(0.99f, output[0], 1e-5f);
        Assert.Equal(0.5f, output[49], 1e-5f);
        Assert.Equal(0f, output[99]);
        Assert.All(output.Skip(100), s => Assert.Equal(0f, s));
        Assert.Equal(0, fader.Level);
    }

    [Fact]
    public void ZmianaKierunkuWPolowieZaczynaOdBiezacegoPoziomu()
    {
        var fader = Prepared();
        fader.FadeTo(0, 0.1);
        Run(fader, 50);
        Assert.Equal(0.5, fader.Level, 1e-6);

        fader.FadeTo(1, 0.1);
        var output = Run(fader, 120);

        // Z połowy do pełnego poziomu w zadanym czasie, bez skoku na początku.
        Assert.Equal(0.505f, output[0], 1e-5f);
        Assert.Equal(1f, output[99], 1e-5f);
        Assert.Equal(1, fader.Level);
    }

    [Fact]
    public void ZerowyCzasToSkok()
    {
        var fader = Prepared();
        fader.FadeTo(0, 0);

        Assert.All(Run(fader, 5), s => Assert.Equal(0f, s));
        Assert.Equal(0, fader.Target);

        fader.FadeTo(1, 0);
        Assert.Equal(1, fader.Target);
        Assert.All(Run(fader, 5), s => Assert.Equal(1f, s));
    }

    [Fact]
    public void CzyszczenieToruZostawiaPoziomWyciszenia()
    {
        var fader = Prepared();
        fader.FadeTo(0, 0);
        Run(fader, 1);

        fader.Reset();

        Assert.All(Run(fader, 5), s => Assert.Equal(0f, s));
    }
}
