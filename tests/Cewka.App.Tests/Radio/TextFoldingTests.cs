using Cewka.App.Radio;
using Xunit;

namespace Cewka.App.Tests.Radio;

public class TextFoldingTests
{
    [Theory]
    [InlineData("Radio Łódź", "radio lodz")]
    [InlineData("Straße", "strasse")]
    [InlineData("Česko Žďár", "cesko zdar")]
    [InlineData("Fête de l'Été", "fete de l ete")]
    [InlineData("Hà Nội", "ha noi")]
    [InlineData("Đà Nẵng", "da nang")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("Chișinău", "chisinau")]
    [InlineData("  RMF   FM ", "rmf fm")]
    public void ZnakiDiakrytyczneSaZdejmowane(string text, string expected) => Assert.Equal(expected, TextFolding.Fold(text));

    [Theory]
    [InlineData("ΣΚΑΪ 100,3", "skai 100.3")]
    [InlineData("Ράδιο Άστρα", "radio astra")]
    [InlineData("ΠΟΛΙΤΗΣ Radio", "politis radio")]
    [InlineData("Αθήνα", "athina")]
    public void GreckiJestTransliterowanyIBezTonosu(string text, string expected) => Assert.Equal(expected, TextFolding.Fold(text));

    [Theory]
    [InlineData("Хіт FM", "hit fm")]
    [InlineData("Українське радіо", "ukrayinske radio")]
    [InlineData("Київ", "kyyiv")]
    public void CyrylicaJestTransliterowana(string text, string expected) => Assert.Equal(expected, TextFolding.Fold(text));

    [Fact]
    public void UkrainskieHMaDrugiZapisKh() => Assert.Equal("khit fm", TextFolding.FoldAlternative("Хіт FM"));

    [Theory]
    [InlineData("102,2")]
    [InlineData("102.2")]
    public void CzestotliwoscZPrzecinkiemIKropkaDajeTenSamKlucz(string text) => Assert.Equal("102.2", TextFolding.Fold(text));

    [Fact]
    public void PrzecinekMiedzyLiczbamiPozaCzestotliwosciaNieRozbijaSlow() =>
        Assert.Equal("radio zet 107.5 fm", TextFolding.Fold("Radio ZET 107,5 FM"));

    [Fact]
    public void KluczSortowaniaUstawiaCzeskoPrzyCIUkrainePrzyU()
    {
        var names = new[] { "Zambia", "Česko", "Україна", "Dania", "Ukraine" };
        var sorted = names.OrderBy(TextFolding.SortKey, StringComparer.Ordinal).ToArray();

        Assert.Equal(["Česko", "Dania", "Ukraine", "Україна", "Zambia"], sorted);
    }

    [Fact]
    public void PustyTekstDajePustyKlucz()
    {
        Assert.Equal(string.Empty, TextFolding.Fold(null));
        Assert.Equal(string.Empty, TextFolding.Fold("   "));
    }
}
