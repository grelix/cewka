using Cewka.App.Services;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia sprawdzania wersji — te części, które nie wymagają sieci: budowanie adresu
/// usługi, odczyt numeru ze znacznika wydania i ocena, czy adres wolno podać powłoce systemu.
///
/// <para>Samo żądanie sieciowe nie jest tu sprawdzane celowo: test zależny od dostępności
/// zewnętrznego serwisu przestaje mówić o programie, a zaczyna o łączu.</para>
/// </summary>
public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("https://github.com/grelix/cewka", "https://api.github.com/repos/grelix/cewka/releases/latest")]
    [InlineData("https://github.com/grelix/cewka/", "https://api.github.com/repos/grelix/cewka/releases/latest")]
    [InlineData("http://github.com/grelix/cewka", "https://api.github.com/repos/grelix/cewka/releases/latest")]
    [InlineData("https://GitHub.com/Grelix/Cewka", "https://api.github.com/repos/Grelix/Cewka/releases/latest")]
    public void AdresUslugiZbudowanyZAdresuRepozytorium(string repository, string expected)
    {
        Assert.Equal(expected, UpdateCheck.ApiUrl(repository));
    }

    /// <summary>
    /// Adresy, z których nie da się zbudować zapytania. Wtedy sprawdzanie ma być po prostu
    /// niedostępne, a nie zgłaszać błąd albo pytać przypadkowy serwer.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nie-adres")]
    [InlineData("ftp://github.com/grelix/cewka")]
    [InlineData("https://gitlab.com/grelix/cewka")]
    [InlineData("https://github.com/grelix")]
    [InlineData("https://github.com/")]
    public void AdresyNieNadajaceSieDoSprawdzania(string? repository)
    {
        Assert.Null(UpdateCheck.ApiUrl(repository));
    }

    [Fact]
    public void AdresStronyWydanDoklejanyDoRepozytorium()
    {
        Assert.Equal("https://github.com/grelix/cewka/releases",
            UpdateCheck.ReleasesUrl("https://github.com/grelix/cewka"));

        Assert.Equal("https://github.com/grelix/cewka/releases",
            UpdateCheck.ReleasesUrl("https://github.com/grelix/cewka/"));

        Assert.Null(UpdateCheck.ReleasesUrl("nie-adres"));
    }

    [Theory]
    [InlineData("v0.7.0", 0, 7, 0)]
    [InlineData("0.7.0", 0, 7, 0)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v10.20.30", 10, 20, 30)]
    // Zapis dwuczłonowy uzupełniany zerem: „0.8" i „0.8.0" to jedno wydanie, a bez tego
    // Version uznałby pierwsze za starsze od drugiego.
    [InlineData("v0.8", 0, 8, 0)]
    // Oznaczenie przedwydania odcinane — Version go nie przyjmuje.
    [InlineData("v1.0.0-rc1", 1, 0, 0)]
    [InlineData("  v0.7.0  ", 0, 7, 0)]
    public void NumerWersjiOdczytanyZeZnacznika(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateCheck.ParseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wydanie")]
    [InlineData("v")]
    [InlineData("vNaN")]
    public void ZnacznikiNieBedaceNumeremWersji(string? tag)
    {
        Assert.Null(UpdateCheck.ParseTag(tag));
    }

    /// <summary>
    /// Kolejność wersji rozstrzyga o pokazaniu komunikatu, więc warto ją sprawdzić wprost —
    /// zwłaszcza przypadek 0.10 wobec 0.9, w którym porównanie tekstowe dałoby odwrotny wynik.
    /// </summary>
    [Fact]
    public void KolejnoscWersjiLiczonaLiczbowo()
    {
        Assert.True(UpdateCheck.ParseTag("v0.10.0") > UpdateCheck.ParseTag("v0.9.0"));
        Assert.True(UpdateCheck.ParseTag("v1.0.0") > UpdateCheck.ParseTag("v0.99.99"));
        Assert.True(UpdateCheck.ParseTag("v0.7.1") > UpdateCheck.ParseTag("v0.7.0"));
        Assert.Equal(UpdateCheck.ParseTag("v0.7.0"), UpdateCheck.ParseTag("0.7.0"));
    }

    /// <summary>
    /// Adres idzie do powłoki systemu, więc wolno przepuścić wyłącznie http i https. Bez tego
    /// wartość z odpowiedzi serwisu mogłaby wskazać plik albo program.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/grelix/cewka", true)]
    [InlineData("http://example.org", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:kto@gdzie.pl", false)]
    [InlineData("github.com/grelix/cewka", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void DoPowlokiTrafiajaWylacznieAdresyHttp(string? address, bool expected)
    {
        Assert.Equal(expected, WebLink.IsSafe(address));
    }

    /// <summary>
    /// Adres repozytorium ma pochodzić z metadanych zestawu, a nie ze stałej w kodzie okna.
    /// Ten test pilnuje, że metadane w ogóle trafiają do wyniku kompilacji — bez nich wpis
    /// w oknie „O programie" byłby pusty, a sprawdzanie wersji niedostępne.
    /// </summary>
    [Fact]
    public void AdresRepozytoriumObecnyWMetadanychZestawu()
    {
        Assert.True(WebLink.IsSafe(UpdateCheck.Repository),
            $"metadane zestawu nie zawierają adresu repozytorium (odczytano: '{UpdateCheck.Repository}')");

        Assert.NotNull(UpdateCheck.ApiUrl(UpdateCheck.Repository));
    }

    /// <summary>
    /// Wersja programu i numer ze znacznika wydania muszą dać się porównać bez zastrzeżeń.
    /// Sprawdzenie idzie okrężnie — przez zbudowanie znacznika z wersji programu i odczytanie go
    /// z powrotem — bo właśnie taka droga zachodzi przy każdym sprawdzeniu i tylko ona dowodzi,
    /// że oba numery są tego samego kształtu.
    /// </summary>
    [Fact]
    public void WersjaProgramuPorownywalnaZeZnacznikiemWydania()
    {
        Assert.True(UpdateCheck.Current > new Version(0, 0, 0));

        var tag = "v" + UpdateCheck.Current.ToString(3);

        Assert.Equal(UpdateCheck.Current, UpdateCheck.ParseTag(tag));
        Assert.False(UpdateCheck.ParseTag(tag) > UpdateCheck.Current, "wydanie o tym samym numerze nie jest nowsze");
    }
}
