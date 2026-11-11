using Cewka.App.Radio;

namespace Cewka.App.Tests.Radio;

/// <summary>Stacje do testów katalogu, wyszukiwania i stanu.</summary>
internal static class RadioTestData
{
    public static RadioStation Station(
        string id, string name, string country, string scope = "national", string[]? categories = null,
        string? city = null, string? cityLatin = null, string[]? languages = null, params StationStream[] streams) => new()
    {
        Id = id,
        Name = name,
        Country = country,
        Scope = scope,
        City = city,
        CityLatin = cityLatin,
        Categories = [.. categories ?? [RadioCategories.Hits]],
        Languages = [.. languages ?? ["en"]],
        Streams = streams.Length == 0 ? [Stream($"https://{id}.example.com/live")] : [.. streams],
    };

    public static StationStream Stream(string url, string codec = "mp3", int kbps = 128, bool hls = false) =>
        new() { Url = url, Codec = codec, Kbps = kbps, Hls = hls };

    public static string StationsFile() => Path.Combine(AppContext.BaseDirectory, "Radio", "stations.json");

    public static string NewTempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cewka-radio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "radio.json");
    }
}
