using System.Text;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia zapisu i odczytu listy odtwarzania oraz dwóch drobnych rzeczy, które łatwo
/// zepsuć bez zauważenia: rozpoznawania kodu języka systemu i tego, że intensywność zalecana
/// nie zmienia niczego.
/// </summary>
public sealed class PlaylistFileTests : IDisposable
{
    private readonly string _katalog =
        Path.Combine(Path.GetTempPath(), "cewka-testy-list-" + Guid.NewGuid().ToString("N")[..8]);

    public PlaylistFileTests() => Directory.CreateDirectory(_katalog);

    public void Dispose()
    {
        try { Directory.Delete(_katalog, recursive: true); }
        catch (IOException) { /* katalog tymczasowy: nieusunięty nie psuje niczego */ }
    }

    private string Plik(string nazwa)
    {
        var path = Path.Combine(_katalog, nazwa);
        File.WriteAllText(path, "nie jest to prawdziwy dźwięk, ale istnieje");
        return path;
    }

    [Fact]
    public void ObiegZamknietyZachowujeKolejnosc()
    {
        var pierwszy = Plik("a.mp3");
        var drugi = Plik("b.mp3");
        var trzeci = Plik("c.mp3");

        var lista = Path.Combine(_katalog, "kolejka.m3u8");

        PlaylistFile.Save(lista,
        [
            new PlaylistEntry(trzeci, "Trzeci", "Wykonawca", 61),
            new PlaylistEntry(pierwszy, "Pierwszy", null, 122.6),
            new PlaylistEntry(drugi, "Drugi", "Inny", 3),
        ]);

        var wynik = PlaylistFile.Load(lista);

        Assert.Equal(0, wynik.Missing);
        Assert.Equal([trzeci, pierwszy, drugi], wynik.Paths);
    }

    [Fact]
    public void PlikiWTymSamymDrzewieZapisaneSciezkaWzgledna()
    {
        var podkatalog = Path.Combine(_katalog, "album");
        Directory.CreateDirectory(podkatalog);

        var utwor = Path.Combine(podkatalog, "utwor.mp3");
        File.WriteAllText(utwor, "x");

        var lista = Path.Combine(_katalog, "lista.m3u8");
        PlaylistFile.Save(lista, [new PlaylistEntry(utwor, "Utwór", null, 10)]);

        var tekst = File.ReadAllText(lista);

        Assert.Contains("album/utwor.mp3", tekst);
        Assert.DoesNotContain(_katalog, tekst);
    }

    [Fact]
    public void PlikPozaDrzewemZapisanySciezkaBezwzgledna()
    {
        var obcy = Path.Combine(Path.GetTempPath(), "cewka-obcy-" + Guid.NewGuid().ToString("N")[..8] + ".mp3");
        File.WriteAllText(obcy, "x");

        try
        {
            var lista = Path.Combine(_katalog, "lista.m3u8");
            PlaylistFile.Save(lista, [new PlaylistEntry(obcy, "Obcy", null, 10)]);

            Assert.Contains(obcy, File.ReadAllText(lista));
            Assert.Equal([obcy], PlaylistFile.Load(lista).Paths);
        }
        finally
        {
            File.Delete(obcy);
        }
    }

    [Fact]
    public void BrakujacePlikiSaZgloszoneZamiastPominieteWCiszy()
    {
        var istniejacy = Plik("jest.mp3");
        var lista = Path.Combine(_katalog, "lista.m3u8");

        File.WriteAllText(lista, string.Join('\n',
        [
            "#EXTM3U",
            "#EXTINF:10,Jest",
            "jest.mp3",
            "#EXTINF:10,Nie ma",
            "nie-ma.mp3",
            "#EXTINF:10,Tez nie ma",
            "gdzies/indziej.mp3",
        ]));

        var wynik = PlaylistFile.Load(lista);

        Assert.Equal([istniejacy], wynik.Paths);
        Assert.Equal(2, wynik.Missing);
    }

    /// <summary>
    /// Plik zapisany w Windowsie ma się czytać w Linuksie, więc separatorem w liście jest
    /// ukośnik zwykły, niezależnie od tego, czym rozdziela ścieżki system.
    /// </summary>
    [Fact]
    public void ZapisNieZawieraUkosnikaOdwrotnego()
    {
        var podkatalog = Path.Combine(_katalog, "a", "b");
        Directory.CreateDirectory(podkatalog);

        var utwor = Path.Combine(podkatalog, "c.mp3");
        File.WriteAllText(utwor, "x");

        var lista = Path.Combine(_katalog, "lista.m3u8");
        PlaylistFile.Save(lista, [new PlaylistEntry(utwor, "C", null, 1)]);

        Assert.DoesNotContain("\\", File.ReadAllText(lista));
    }

    [Fact]
    public void ZapisBezZnacznikaKolejnosciBajtow()
    {
        var lista = Path.Combine(_katalog, "lista.m3u8");
        PlaylistFile.Save(lista, [new PlaylistEntry(Plik("a.mp3"), "Zażółć gęślą jaźń", "Wykonawca", 5)]);

        var bajty = File.ReadAllBytes(lista);

        Assert.False(bajty[0] == 0xEF && bajty[1] == 0xBB && bajty[2] == 0xBF);

        // Polskie znaki muszą przejść przez zapis i odczyt bez zmiany.
        Assert.Contains("Zażółć gęślą jaźń", Encoding.UTF8.GetString(bajty));
    }

    /// <summary>Nowa linia w tagu rozerwałaby plik na dwie pozycje.</summary>
    [Fact]
    public void NowaLiniaWTytuleNiePsujePliku()
    {
        var utwor = Plik("a.mp3");
        var lista = Path.Combine(_katalog, "lista.m3u8");

        PlaylistFile.Save(lista, [new PlaylistEntry(utwor, "Tytuł\nz nową linią", "Wykonawca\r", 5)]);

        Assert.Equal([utwor], PlaylistFile.Load(lista).Paths);
    }

    [Theory]
    [InlineData("pl-PL", "pl")]
    [InlineData("pt_BR.UTF-8", "pt")]
    [InlineData("uk:ru:en", "uk")]
    [InlineData("de@euro", "de")]
    [InlineData("RU", "ru")]
    [InlineData("C", null)]
    [InlineData("POSIX", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void KodJezykaSystemuRozpoznanyZRoznychZapisow(string? raw, string? expected)
    {
        Assert.Equal(expected, SystemLanguage.ParseLocale(raw));
    }

    /// <summary>
    /// Nasycenie przy ustawieniu zalecanym musi być dokładną jednością, a nie wartością bliską
    /// jedności: barwy wyciągane z okładki mają wtedy wychodzić identycznie jak przed
    /// wprowadzeniem tego ustawienia.
    /// </summary>
    [Fact]
    public void ZalecanaPrzejelaWartoscDawnejSubtelnej()
    {
        // Skala zjechała w dół w 0.8.0: tło było w praktyce mocniejsze, niż potrzeba. Wartości
        // wpisane tu wprost, bo to one są rozstrzygnięciem — przypadkowa zmiana którejkolwiek
        // przesunęłaby wygląd wszystkich okien naraz.
        Assert.Equal(0.72, ColourPreferences.Saturation(ColourIntensity.Recommended));
        Assert.Equal(1.28, ColourPreferences.Saturation(ColourIntensity.Intense));
    }

    /// <summary>
    /// Subtelna ma być co najmniej dwukrotnie słabsza od zalecanej — i to w obu miarach naraz,
    /// bo wrażenie mocy składa się z nasycenia barwy i z siły plam. Stopień słabszy tylko
    /// o kilkanaście procent nie różniłby się niczym widocznym na tle, po którym wędrują plamy.
    /// </summary>
    [Fact]
    public void SubtelnaJestPrzynajmniejDwaRazySlabszaOdZalecanej()
    {
        var nasycenie = ColourPreferences.Saturation(ColourIntensity.Subtle);
        var nasycenieZalecane = ColourPreferences.Saturation(ColourIntensity.Recommended);

        Assert.True(
            nasycenie <= nasycenieZalecane / 2,
            $"nasycenie subtelnej to {nasycenie}, a ma być najwyżej {nasycenieZalecane / 2}");

        double Srodek(ColourIntensity poziom)
        {
            var (minimum, maximum) = ColourPreferences.BackdropRange(poziom);
            return (minimum + maximum) / 2;
        }

        Assert.True(
            Srodek(ColourIntensity.Subtle) <= Srodek(ColourIntensity.Recommended) / 2,
            $"siła plam subtelnej to {Srodek(ColourIntensity.Subtle)}, " +
            $"a ma być najwyżej {Srodek(ColourIntensity.Recommended) / 2}");
    }

    /// <summary>
    /// Siła plam tła nie jest już stała — każda plama waha się między progami — więc jedność
    /// musi wypadać w środku zakresu ustawienia zalecanego. Inaczej tło byłoby średnio
    /// jaśniejsze albo ciemniejsze niż to, z jakim program był projektowany.
    /// </summary>
    [Fact]
    public void ZakresZalecanyLezySymetrycznieWokolSwojegoSrodka()
    {
        // Do 0.7.20 środkiem zalecanej była jedność, czyli siła, z jaką tło zaprojektowano.
        // Od 0.8.0 zalecana przejęła wartość dawnej subtelnej i jedność przestała być wartością
        // wyróżnioną — sprawdzana jest więc symetria wokół zadanego środka, a nie wokół jedynki.
        var (minimum, maximum) = ColourPreferences.BackdropRange(ColourIntensity.Recommended);

        Assert.Equal(0.78, (minimum + maximum) / 2, precision: 10);
        Assert.True(minimum < 0.78 && maximum > 0.78);
    }

    /// <summary>
    /// Trzy zakresy muszą iść po kolei i nie zachodzić na siebie środkami, bo inaczej
    /// „intensywna" mogłaby wypadać średnio słabiej niż „zalecana".
    /// </summary>
    [Fact]
    public void ZakresyIntensywnosciSaUporzadkowane()
    {
        var subtelna = ColourPreferences.BackdropRange(ColourIntensity.Subtle);
        var zalecana = ColourPreferences.BackdropRange(ColourIntensity.Recommended);
        var intensywna = ColourPreferences.BackdropRange(ColourIntensity.Intense);

        foreach (var zakres in new[] { subtelna, zalecana, intensywna })
            Assert.True(zakres.Minimum < zakres.Maximum, "próg dolny musi być mniejszy od górnego");

        double Srodek((double Minimum, double Maximum) zakres) => (zakres.Minimum + zakres.Maximum) / 2;

        Assert.True(Srodek(subtelna) < Srodek(zalecana));
        Assert.True(Srodek(zalecana) < Srodek(intensywna));

        // Wahanie ma być tą samą częścią średniej dla każdego ustawienia. Inaczej ustawienie
        // najspokojniejsze pulsowałoby względnie najmocniej, co przeczy jego nazwie.
        double Glebokosc((double Minimum, double Maximum) zakres) =>
            (zakres.Maximum - zakres.Minimum) / (zakres.Maximum + zakres.Minimum);

        Assert.Equal(Glebokosc(subtelna), Glebokosc(zalecana), precision: 9);
        Assert.Equal(Glebokosc(zalecana), Glebokosc(intensywna), precision: 9);

        Assert.True(ColourPreferences.Saturation(ColourIntensity.Subtle) < 1.0);
        Assert.True(ColourPreferences.Saturation(ColourIntensity.Intense) > 1.0);
    }

    /// <summary>
    /// Wybór losowy musi zawsze wskazać którąś z par o ustalonych barwach, nigdy siebie samego —
    /// inaczej rysowanie okładki weszłoby w nieskończone rozwiązywanie tego samego wyboru.
    /// </summary>
    [Fact]
    public void LosowanieZawszeDajeParaOUstalonychBarwach()
    {
        var trafione = new HashSet<PlaceholderPalette>();

        for (var i = 0; i < 300; i++)
        {
            var wybrana = CoilCover.Resolve(PlaceholderPalette.Random);

            Assert.NotEqual(PlaceholderPalette.Random, wybrana);
            Assert.Contains(wybrana, CoilCover.Fixed);
            trafione.Add(wybrana);
        }

        // Przy trzystu losowaniach z pięciu par trafienie w każdą jest praktycznie pewne;
        // brak którejś znaczyłby, że losowanie nie obejmuje całego zbioru.
        Assert.Equal(CoilCover.Fixed.Length, trafione.Count);
    }

    /// <summary>Pozycje o ustalonych barwach zwracają siebie, bez losowania.</summary>
    [Theory]
    [InlineData(PlaceholderPalette.BlueViolet)]
    [InlineData(PlaceholderPalette.Turquoise)]
    [InlineData(PlaceholderPalette.Amber)]
    [InlineData(PlaceholderPalette.Lime)]
    [InlineData(PlaceholderPalette.Graphite)]
    public void ParyOUstalonychBarwachNieSaLosowane(PlaceholderPalette palette)
    {
        Assert.Equal(palette, CoilCover.Resolve(palette));
    }
}
