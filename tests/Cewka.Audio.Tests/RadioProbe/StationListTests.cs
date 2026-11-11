using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

public sealed class StationListTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-radioprobe-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string Aa = """
        {"id":"aa-jeden","nazwa":"Jeden","kraj":"AA",
         "strumienie":[{"url":"http://example.invalid/1","kodek":"MP3"},{"url":"https://example.invalid/2"}],
         "logo":[{"url":"https://example.invalid/1.png","format":"png"}]}
        """;

    private const string Bb = """
        {"id":"bb-dwa","nazwa":"Dwa","kraj":"BB","strumienie":[{"url":"http://example.invalid/3"}],"logo":null}
        """;

    [Fact]
    public void Whole_catalogue_is_read_country_by_country()
    {
        var stations = StationList.Load(Write("v2.json", $$"""
            {"wersja":2,"kraje":[{"kraj":"AA","stacje":[{{Aa}}]},{"kraj":"BB","stacje":[{{Bb}}]}]}
            """));

        Assert.Equal(["aa-jeden", "bb-dwa"], stations.Select(s => s.Id));
        Assert.Equal("Jeden", stations[0].Name);
        Assert.Equal("AA", stations[0].Country);
        Assert.Equal(["http://example.invalid/1", "https://example.invalid/2"], stations[0].StreamUrls);
        Assert.Equal(["https://example.invalid/1.png"], stations[0].LogoUrls);
        Assert.Empty(stations[1].LogoUrls);
    }

    [Fact]
    public void Single_country_file_and_program_list_are_read()
    {
        var country = StationList.Load(Write("aa.json", $$"""{"kraj":"AA","stacje":[{{Aa}}]}"""));
        var list = StationList.Load(Write("lista.json", $"[{Bb}]"));

        Assert.Equal("aa-jeden", Assert.Single(country).Id);
        Assert.Equal("bb-dwa", Assert.Single(list).Id);
    }

    /// <summary>Pliki katalogu mają kraj raz, w korzeniu albo przy kraju, a nie przy każdej stacji.</summary>
    [Fact]
    public void Station_without_a_country_takes_it_from_its_file_or_catalogue_entry()
    {
        var file = StationList.Load(Write("aa.json", """{"kraj":"AA","stacje":[{"id":"x","strumienie":[]}]}"""));
        var catalogue = StationList.Load(Write("v2.json", """
            {"kraje":[{"kraj":"BB","stacje":[{"id":"y","strumienie":[]},{"id":"z","kraj":"CC","strumienie":[]}]}]}
            """));

        Assert.Equal("AA", Assert.Single(file).Country);
        Assert.Equal(["BB", "CC"], catalogue.Select(s => s.Country));
    }

    [Fact]
    public void Plain_text_addresses_are_accepted()
    {
        var stations = StationList.Load(Write("lista.json", """
            [{"id":"x","strumienie":["http://example.invalid/1"],"logo":"https://example.invalid/x.png"}]
            """));

        Assert.Equal(["http://example.invalid/1"], stations[0].StreamUrls);
        Assert.Equal(["https://example.invalid/x.png"], stations[0].LogoUrls);
    }

    [Fact]
    public void Directory_merges_files_and_repeated_station_counts_once()
    {
        Write("a.json", $$"""{"stacje":[{{Aa}}]}""");
        Write("b.json", $$"""{"stacje":[{{Aa}},{{Bb}}]}""");
        Write("uwagi.txt", "bez znaczenia");

        var stations = StationList.Load(_directory);

        Assert.Equal(["aa-jeden", "bb-dwa"], stations.Select(s => s.Id));
    }

    [Theory]
    [InlineData("nie json")]
    [InlineData("""{"wersja":2}""")]
    [InlineData("""[{"nazwa":"bez identyfikatora","strumienie":[]}]""")]
    public void Wrong_schema_is_an_error(string json)
    {
        Assert.Throws<InvalidDataException>(() => StationList.Load(Write("zly.json", json)));
    }

    [Fact]
    public void Missing_path_is_an_error()
    {
        Assert.Throws<InvalidDataException>(() => StationList.Load(Path.Combine(_directory, "nie-ma.json")));
    }

    [Fact]
    public void Selection_filters_by_country_and_limits_the_count()
    {
        var stations = new[]
        {
            new ProbeStation("a", "A", "PL", [], []),
            new ProbeStation("b", "B", "CZ", [], []),
            new ProbeStation("c", "C", "pl", [], []),
            new ProbeStation("d", "D", "PL", [], []),
        };

        Assert.Equal(["a", "c"], StationList.Select(stations, "PL", 2).Select(s => s.Id));
        Assert.Equal(4, StationList.Select(stations, null, null).Count);
    }
}
