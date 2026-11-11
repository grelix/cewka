using Cewka.Platform.Linux;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Reguły panelu MPRIS dla stacji radiowych, które nie wymagają szyny sesji.</summary>
public sealed class MprisRadioRulesTests
{
    [Fact]
    public void StacjaNieMaOsiCzasuAUtworTak()
    {
        Assert.False(MprisRules.CanSeek(live: true));
        Assert.True(MprisRules.CanSeek(live: false));
    }

    [Fact]
    public void LogoZPamieciPodrecznejJestPodawaneJakoAdresPliku()
    {
        var path = Path.Combine(Path.GetTempPath(), "cewka-logo", "a b.png");

        var url = MprisRules.ArtUrl(path);

        Assert.StartsWith("file:///", url);
        Assert.EndsWith("a%20b.png", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wzgledna/sciezka.png")]
    public void BrakLogoLubSciezkaWzglednaDajePustyAdres(string? path) =>
        Assert.Equal(string.Empty, MprisRules.ArtUrl(path));
}
