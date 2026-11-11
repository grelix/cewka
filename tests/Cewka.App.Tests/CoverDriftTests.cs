using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Bardzo wolny ruch okładki sceny: zakres, okres 60 s i kierunek zależny od ścieżki.</summary>
public sealed class CoverDriftTests
{
    [Fact]
    public void KierunekJestStalyDlaTejSamejSciezki()
    {
        Assert.Equal(CoverDrift.For(@"C:\muzyka\a.flac"), CoverDrift.For(@"C:\muzyka\a.flac"));
    }

    [Fact]
    public void RoznesciezkiDajaRozneKierunki()
    {
        var directions = Enumerable.Range(0, 40).Select(i => CoverDrift.For($@"C:\muzyka\{i}.flac")).Distinct().Count();

        Assert.True(directions >= 4, $"Różnych kierunków: {directions}");
    }

    [Fact]
    public void BrakSciezkiDajeKierunekDomyslny()
    {
        Assert.Equal(CoverDrift.For(null), CoverDrift.For(string.Empty));
    }

    [Fact]
    public void SkalaIPrzesunieciePozostajaWZakresie()
    {
        foreach (var path in new[] { "a", "b", "c", "dd", @"C:\x\y.mp3", "ęł" })
        {
            var direction = CoverDrift.For(path);
            for (var s = 0.0; s <= 180; s += 0.5)
            {
                var (scale, x, y) = CoverDrift.At(direction, s);
                Assert.InRange(scale, 1.0, 1.04 + 1e-9);
                Assert.InRange(x, -0.02 - 1e-9, 0.02 + 1e-9);
                Assert.InRange(y, -0.02 - 1e-9, 0.02 + 1e-9);
            }
        }
    }

    [Fact]
    public void RuchStartujeZMiejscaIPowtarzaSieCo60Sekund()
    {
        var direction = CoverDrift.For("a");
        var start = CoverDrift.At(direction, 0);

        Assert.Equal(1.0, start.Scale, 6);
        Assert.Equal(0.0, start.X, 6);
        Assert.Equal(0.0, start.Y, 6);

        var later = CoverDrift.At(direction, 17);
        Assert.NotEqual(start, later);

        var again = CoverDrift.At(direction, 77);
        Assert.Equal(later.Scale, again.Scale, 6);
        Assert.Equal(later.X, again.X, 6);
        Assert.Equal(later.Y, again.Y, 6);
    }

    [Fact]
    public void SkalaDosiegaMaksimumWPolowieCyklu()
    {
        Assert.Equal(1.04, CoverDrift.At(CoverDrift.For("a"), 30).Scale, 6);
    }
}
