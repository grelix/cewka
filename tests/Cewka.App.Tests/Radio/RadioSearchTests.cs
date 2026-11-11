using Cewka.App.Radio;
using Xunit;
using static Cewka.App.Tests.Radio.RadioTestData;

namespace Cewka.App.Tests.Radio;

public class RadioSearchTests
{
    private static readonly Dictionary<string, string> CountryNames = new()
    {
        ["PL"] = "Polska", ["CZ"] = "Česko", ["GR"] = "Grecja", ["UA"] = "Ukraina", ["HU"] = "Węgry", ["VN"] = "Wietnam",
        ["US"] = "Stany Zjednoczone", ["GB"] = "Wielka Brytania",
    };

    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        [RadioCategories.Hits] = "Przeboje", [RadioCategories.NewsTalk] = "Informacje i rozmowy", [RadioCategories.Rock] = "Rock i alternatywa",
    };

    private static RadioCatalog Catalog() => new(
    [
        Station("pl-zet", "Radio ZET", "PL", city: "Warszawa", cityLatin: "Warszawa"),
        Station("pl-lodz", "Radio Łódź", "PL", "local", city: "Łódź", cityLatin: "Lodz"),
        Station("pl-fm", "Eska Rock 102,2", "PL", categories: [RadioCategories.Rock]),
        Station("cz-1", "Evropa 2", "CZ", city: "Praha"),
        Station("gr-1", "ΣΚΑΪ 100,3", "GR", city: "Αθήνα", cityLatin: "Athina"),
        Station("ua-1", "Хіт FM", "UA", city: "Київ", cityLatin: "Kyiv"),
        Station("hu-1", "Petőfi Rádió", "HU", city: "Budapest"),
        Station("vn-1", "VOV Giao thông", "VN", city: "Hà Nội", cityLatin: "Hanoi"),
        Station("us-1", "WNYC", "US", city: "New York", categories: [RadioCategories.NewsTalk]),
        Station("gb-1", "Radio 4", "GB", categories: [RadioCategories.NewsTalk]),
    ]);

    private static IReadOnlyList<string> Ids(string query, string? selected = null, string? defaultCountry = null, string? category = null) =>
        [.. RadioSearch.Run(Catalog(), new RadioSearchRequest(
            query, selected, defaultCountry, category, c => CountryNames[c], k => CategoryNames.GetValueOrDefault(k, k))).Select(s => s.Id)];

    [Theory]
    [InlineData("lodz", "pl-lodz")]
    [InlineData("ŁÓDŹ", "pl-lodz")]
    [InlineData("radio lodz", "pl-lodz")]
    [InlineData("skai", "gr-1")]
    [InlineData("athina", "gr-1")]
    [InlineData("hit fm", "ua-1")]
    [InlineData("khit", "ua-1")]
    [InlineData("kyiv", "ua-1")]
    [InlineData("petofi", "hu-1")]
    [InlineData("budapest", "hu-1")]
    [InlineData("hanoi", "vn-1")]
    [InlineData("ha noi", "vn-1")]
    [InlineData("new york", "us-1")]
    [InlineData("102.2", "pl-fm")]
    [InlineData("102,2", "pl-fm")]
    [InlineData("100,3", "gr-1")]
    public void ZapytanieZnajdujeStacjePoNazwieAliasieMiescieICzestotliwosci(string query, string expectedId) =>
        Assert.Equal([expectedId], Ids(query));

    [Fact]
    public void ZapytanieZNazwaKrajuIKategoriiWJezykuInterfejsu()
    {
        Assert.Equal(["cz-1"], Ids("cesko"));
        Assert.Equal(["hu-1"], Ids("wegry"));
        Assert.Contains("pl-fm", Ids("rock i alternatywa"));
        Assert.Equal(["us-1", "gb-1"], Ids("informacje"));
        Assert.Equal(["pl-fm"], Ids("polska rock"));
    }

    [Fact]
    public void WszystkieSlowaZapytaniaMuszaPasowac()
    {
        Assert.Empty(Ids("radio zet praha"));
        Assert.Equal(["pl-zet"], Ids("zet warszawa"));
    }

    [Fact]
    public void BrakWynikowDajePustaListe() => Assert.Empty(Ids("nieistniejaca"));

    [Fact]
    public void PustyZapytanieDajeCalaListeKategoriaJaZawęża()
    {
        Assert.Equal(10, Ids("").Count);
        Assert.Equal(["us-1", "gb-1"], Ids("", category: RadioCategories.NewsTalk));
    }

    [Fact]
    public void WynikiNajpierwKrajWybranyPotemDomyslnyPotemResztaWgNazwKrajow()
    {
        var ids = Ids("radio", selected: "GB", defaultCountry: "PL");

        // GB (Radio 4), potem PL (zasięg i alfabet), potem reszta: Węgry (Petőfi Rádió).
        Assert.Equal(["gb-1", "pl-zet", "pl-lodz", "hu-1"], ids);
    }

    /// <summary>W wynikach z jednego kraju stacje z logo stoją przed stacjami bez logo.</summary>
    [Fact]
    public void WWynikachKrajuNajpierwStacjeZLogo()
    {
        var withLogo = Station("pl-lodz", "Radio Łódź", "PL", "local", city: "Łódź", cityLatin: "Lodz");
        withLogo.Logos = ["https://lodz.example.com/logo.png"];
        var catalog = new RadioCatalog([Station("pl-zet", "Radio ZET", "PL"), withLogo, Station("cz-1", "Radio Praha", "CZ")]);

        var ids = RadioSearch.Run(catalog, new RadioSearchRequest("radio", "PL", null, null, c => CountryNames.GetValueOrDefault(c, c), k => k))
            .Select(s => s.Id);

        Assert.Equal(["pl-lodz", "pl-zet", "cz-1"], ids);
    }

    [Fact]
    public void ResztaKrajowWynikowJestPosortowanaAlfabetycznieWgNazwyKraju()
    {
        var ids = Ids("", selected: "PL");

        Assert.Equal(
            ["pl-fm", "pl-zet", "pl-lodz", "cz-1", "gr-1", "us-1", "ua-1", "hu-1", "gb-1", "vn-1"],
            ids);
    }

    [Fact]
    public void SzukanieBezResolveraNazwKrajowNieSzukaWNazwachKrajow()
    {
        var ids = RadioSearch.Run(Catalog(), new RadioSearchRequest("polska")).Select(s => s.Id);

        Assert.Empty(ids);
    }
}
