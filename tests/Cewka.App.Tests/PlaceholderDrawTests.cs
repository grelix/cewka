using Cewka.App.Models;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia wyboru barw domyślnej okładki przy ustawieniu losowym.
///
/// <para>Powód powstania: okładka jest rysowana od nowa nie tylko przy przejściu do innego
/// utworu, ale też po przewinięciu w obrębie tego samego i po zmianie motywu. Losowanie
/// w miejscu rysowania zmieniało wtedy barwy przy przesunięciu suwaka postępu.</para>
/// </summary>
public sealed class PlaceholderDrawTests
{
    private const string Utwor = @"C:\Muzyka\album\utwor.mp3";
    private const string Inny = @"C:\Muzyka\album\inny.mp3";

    /// <summary>
    /// Sedno sprawy: wielokrotne rysowanie tego samego utworu daje jedną parę barw. Pięćdziesiąt
    /// wywołań przy pięciu parach do wyboru — gdyby losowanie zachodziło za każdym razem,
    /// prawdopodobieństwo takiego wyniku byłoby nieodróżnialne od zera.
    /// </summary>
    [Fact]
    public void TenSamUtworZawszeDostajeTeSameBarwy()
    {
        var draw = new PlaceholderDraw();
        var pierwsza = draw.For(PlaceholderPalette.Random, Utwor);

        for (var i = 0; i < 50; i++)
            Assert.Equal(pierwsza, draw.For(PlaceholderPalette.Random, Utwor));
    }

    /// <summary>
    /// Barwy muszą się jednak zmieniać między utworami — inaczej „losowanie co utwór" byłoby
    /// jednym losowaniem na uruchomienie programu. Sprawdzane na wielu ścieżkach, bo dwa kolejne
    /// losowania mogą wypaść tak samo.
    /// </summary>
    [Fact]
    public void RozneUtworyDostajaRozneBarwy()
    {
        var draw = new PlaceholderDraw();
        var trafione = new HashSet<PlaceholderPalette>();

        for (var i = 0; i < 200; i++)
            trafione.Add(draw.For(PlaceholderPalette.Random, $"C:\\Muzyka\\utwor-{i}.mp3"));

        Assert.Equal(CoilCover.Fixed.Length, trafione.Count);
    }

    /// <summary>
    /// Przejście do innego utworu i powrót do poprzedniego daje nowe losowanie. Tego właśnie
    /// chciał użytkownik, wybierając losowanie „przy każdym wczytaniu" zamiast stałego
    /// przypisania barw do pliku: ten sam utwór usłyszany później wygląda inaczej.
    /// </summary>
    [Fact]
    public void PowrotDoPoprzedniegoUtworuLosujeOdNowa()
    {
        var draw = new PlaceholderDraw();
        var rozne = 0;

        for (var i = 0; i < 200; i++)
        {
            var przed = draw.For(PlaceholderPalette.Random, Utwor);
            draw.For(PlaceholderPalette.Random, Inny);
            var po = draw.For(PlaceholderPalette.Random, Utwor);

            if (przed != po) rozne++;
        }

        Assert.True(rozne > 0, "powrót do utworu nigdy nie dał innych barw — pamięć nie jest odświeżana");
    }

    [Theory]
    [InlineData(PlaceholderPalette.BlueViolet)]
    [InlineData(PlaceholderPalette.Turquoise)]
    [InlineData(PlaceholderPalette.Amber)]
    [InlineData(PlaceholderPalette.Lime)]
    [InlineData(PlaceholderPalette.Graphite)]
    public void WyborOUstalonychBarwachNieJestLosowany(PlaceholderPalette chosen)
    {
        var draw = new PlaceholderDraw();

        Assert.Equal(chosen, draw.For(chosen, Utwor));
        Assert.Equal(chosen, draw.For(chosen, Inny));
        Assert.Equal(chosen, draw.For(chosen, null));
    }

    /// <summary>
    /// Pusta kolejka to też stan: ścieżka jest wtedy pusta i nie może powodować losowania
    /// przy każdym odświeżeniu okna.
    /// </summary>
    [Fact]
    public void BrakUtworuTakzeMaStalaBarwe()
    {
        var draw = new PlaceholderDraw();
        var pierwsza = draw.For(PlaceholderPalette.Random, null);

        for (var i = 0; i < 20; i++)
            Assert.Equal(pierwsza, draw.For(PlaceholderPalette.Random, null));
    }

    /// <summary>
    /// Zmiana ustawienia unieważnia pamięć: wybranie losowania ma dać widoczny skutek od razu,
    /// a nie dopiero przy następnym utworze.
    /// </summary>
    [Fact]
    public void UniewaznieniePamieciLosujeOdNowa()
    {
        var draw = new PlaceholderDraw();
        var rozne = 0;

        for (var i = 0; i < 200; i++)
        {
            var przed = draw.For(PlaceholderPalette.Random, Utwor);
            draw.Forget();
            if (draw.For(PlaceholderPalette.Random, Utwor) != przed) rozne++;
        }

        Assert.True(rozne > 0, "po unieważnieniu pamięci barwy nigdy się nie zmieniły");
    }
}
