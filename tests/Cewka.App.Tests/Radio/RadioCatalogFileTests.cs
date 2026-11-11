using System.Text.Json;
using Cewka.App.Radio;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>
/// Test strażniczy pliku stacji w programie (<c>Radio/stations.json</c>): schemat, unikalność, taksonomia,
/// adresy bez elementów zakazanych i bez domen z listy blokad, bez Rosji, Białorusi i rosyjskiego.
/// </summary>
public class RadioCatalogFileTests
{
    private static readonly Lazy<RadioCatalog> Catalog = new(() => RadioCatalog.LoadFile(RadioTestData.StationsFile()));

    private static readonly HashSet<string> RussianLanguages = new(StringComparer.OrdinalIgnoreCase) { "ru", "rus", "russian", "русский" };

    /// <summary>Kraje z mniejszą liczbą stacji niż 15 – wyjątki z decyzji właściciela (ścisłe reguły nie dają więcej).</summary>
    private static readonly Dictionary<string, int> SmallCountries = new() { ["VN"] = 6, ["CY"] = 10, ["MX"] = 11 };

    private static readonly Dictionary<string, string[]> CountryLanguages = new()
    {
        ["PL"] = ["pl"], ["CZ"] = ["cs"], ["DE"] = ["de"], ["AT"] = ["de"], ["CH"] = ["de", "fr", "it"], ["GR"] = ["el"],
        ["CY"] = ["el"], ["GB"] = ["en"], ["US"] = ["en"], ["AU"] = ["en"], ["IE"] = ["en"], ["CA"] = ["en", "fr"],
        ["ES"] = ["es"], ["MX"] = ["es"], ["AR"] = ["es"], ["FR"] = ["fr"], ["BE"] = ["nl", "fr"], ["HU"] = ["hu"],
        ["ID"] = ["id"], ["IT"] = ["it"], ["NL"] = ["nl"], ["PT"] = ["pt"], ["BR"] = ["pt"], ["RO"] = ["ro"],
        ["MD"] = ["ro"], ["TR"] = ["tr"], ["UA"] = ["uk"], ["VN"] = ["vi"],
    };

    [Fact]
    public void PlikMaSchemat1IStacje()
    {
        using var stream = File.OpenRead(RadioTestData.StationsFile());
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.True(Catalog.Value.Stations.Count >= 700, $"stacji: {Catalog.Value.Stations.Count}");
    }

    [Fact]
    public void IdentyfikatoryStacjiSaUnikalneINiepuste()
    {
        var stations = Catalog.Value.Stations;
        Assert.All(stations, s => Assert.False(string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)));
        Assert.Equal(stations.Count, stations.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void KategorieSaZnaneIMaDoTrzech()
    {
        Assert.All(Catalog.Value.Stations, s =>
        {
            Assert.InRange(s.Categories.Count, 1, 3);
            Assert.All(s.Categories, c => Assert.True(RadioCategories.IsKnown(c), $"{s.Id}: {c}"));
        });
        Assert.Equal(RadioCategories.All.Count, Catalog.Value.Categories().Count);
    }

    [Fact]
    public void KodyKrajowSaDwuliterowe()
    {
        Assert.All(Catalog.Value.Stations, s => Assert.Matches("^[A-Z]{2}$", s.Country));
        Assert.True(Catalog.Value.CountryCodes.Count >= 28);
    }

    [Fact]
    public void BezRosjiBialorusiIRosyjskiego()
    {
        Assert.DoesNotContain(Catalog.Value.Stations, s => s.Country is "RU" or "BY");
        Assert.All(Catalog.Value.Stations, s => Assert.DoesNotContain(s.Languages, RussianLanguages.Contains));
    }

    [Fact]
    public void AdresyStacjiSaHttpBezElementowZakazanychIBezDomenZListyBlokad()
    {
        var blocklist = RadioBlocklist.BuiltIn;
        foreach (var station in Catalog.Value.Stations)
        {
            Assert.NotEmpty(station.Streams);
            Assert.False(blocklist.IsBlockedName(station.Name), station.Id);
            foreach (var stream in station.Streams)
            {
                Assert.True(Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri), $"{station.Id}: {stream.Url}");
                Assert.True(uri.Scheme is "http" or "https", $"{station.Id}: {stream.Url}");
                Assert.False(blocklist.IsBlockedHost(uri.IdnHost), $"{station.Id}: {stream.Url}");
                Assert.True(ForbiddenUrlParts.Find(uri).Count == 0, $"{station.Id}: {stream.Url}: {string.Join(',', ForbiddenUrlParts.Find(uri))}");
            }
        }
    }

    [Fact]
    public void KodekiPochodzaZObslugiwanegoZbioru()
    {
        var allowed = new[] { "mp3", "aac", "ogg", "unknown" };
        Assert.All(Catalog.Value.Stations, s => Assert.All(s.Streams, x => Assert.Contains(x.Codec, allowed)));
        Assert.All(Catalog.Value.Stations, s => Assert.InRange(s.Recommended, 0, s.Streams.Count - 1));
    }

    [Fact]
    public void LogoSaPrzezHttpsIBezListyBlokad()
    {
        Assert.All(Catalog.Value.Stations, s => Assert.All(s.Logos, url =>
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), $"{s.Id}: {url}");
            Assert.Equal("https", uri.Scheme);
            Assert.False(RadioBlocklist.BuiltIn.IsBlockedHost(uri.IdnHost));
        }));
    }

    [Fact]
    public void PlikNieZawieraPolPopularnosciUwagPrawnychPochodzeniaIGrupyRegulaminu()
    {
        var text = File.ReadAllText(RadioTestData.StationsFile());
        foreach (var forbidden in new[] { "popularnosc", "uwagi_prawne", "ocena_prawna", "pochodzenie", "regulamin", "radio_browser", "stationuuid", "votes" })
            Assert.DoesNotContain($"\"{forbidden}\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KazdyKrajMaWymaganaLiczbeStacjiWJezykuKraju()
    {
        foreach (var code in Catalog.Value.CountryCodes)
        {
            var languages = CountryLanguages[code];
            var count = Catalog.Value.InCountry(code).Count(s => s.Languages.Any(languages.Contains));
            var required = SmallCountries.GetValueOrDefault(code, 15);
            Assert.True(count >= required, $"{code}: {count} stacji w języku kraju, wymagane {required}");
        }
    }

    [Fact]
    public void KazdaStacjaMaOdtwarzalnyAdresPrzyMp3()
    {
        // Sam MP3 (Windows N, Linux bez gst-libav) zostawia część stacji bez adresu; reszta ma co najmniej jeden.
        var playable = Catalog.Value.Stations.Count(s => s.IsPlayable(c => c == LiveCodec.Mp3));
        Assert.True(playable >= 500, $"stacji z MP3: {playable}");
    }
}
