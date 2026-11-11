using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.App.Services;
using Cewka.Platform;

namespace Cewka.App.Radio;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RadioState))]
[JsonSerializable(typeof(StationCatalogFile))]
[JsonSerializable(typeof(RadioStation))]
internal sealed partial class RadioJsonContext : JsonSerializerContext;

/// <summary>
/// Odczyt i zapis <c>radio.json</c> obok <c>queue.json</c>. Plik nieczytelny albo zapisany przez nowszą wersję
/// nie jest nadpisywany pustym stanem: wczytanie oddaje stan tylko do odczytu, a zapis go pomija.
/// </summary>
public static class RadioStateStore
{
    public static RadioState Load() => Load(AppPaths.RadioFile);

    public static RadioState Load(string path)
    {
        if (!File.Exists(path))
            return new RadioState();

        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), RadioJsonContext.Default.RadioState);
            if (state is null)
                return Unreadable();

            if (state.SchemaVersion > RadioState.CurrentSchema)
            {
                state.ReadOnly = true;
                return state;
            }

            Normalise(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"[cewka] zapisany stan radia nieczytelny: {ex.Message}");
            return Unreadable();
        }
    }

    /// <summary>Zapisuje stan; false, gdy stan jest tylko do odczytu albo zapis się nie udał.</summary>
    public static bool Save(RadioState state) => Save(state, AppPaths.RadioFile);

    public static bool Save(RadioState state, string path)
    {
        if (state.ReadOnly)
            return false;

        try
        {
            state.SchemaVersion = RadioState.CurrentSchema;
            AtomicFile.Write(path, JsonSerializer.Serialize(state, RadioJsonContext.Default.RadioState));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać stanu radia: {ex.Message}");
            return false;
        }
    }

    private static RadioState Unreadable() => new() { ReadOnly = true };

    /// <summary>Poprawny składniowo plik z pustymi polami albo wpisami bez adresów nie może wywrócić startu.</summary>
    private static void Normalise(RadioState state)
    {
        state.Favourites = Clean(state.Favourites);
        state.Recent = Clean(state.Recent);
        state.TrimRecent(RecentLimits.Max);
        state.Context ??= new RadioListContext();
        state.Context.View = string.IsNullOrEmpty(state.Context.View) ? RadioListContext.ViewCountry : state.Context.View;

        static List<RadioStation> Clean(List<RadioStation>? list)
        {
            var result = new List<RadioStation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var station in list ?? [])
            {
                if (station is null || string.IsNullOrWhiteSpace(station.Id) || !seen.Add(station.Id))
                    continue;

                station.Streams = station.Streams?.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Url)).ToList() ?? [];
                station.Categories ??= [];
                station.Languages ??= [];
                station.Logos ??= [];
                station.Name ??= station.Id;
                station.Country ??= string.Empty;
                result.Add(station);
            }

            return result;
        }
    }
}
