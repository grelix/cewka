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
/// Odczyt i zapis <c>radio.json</c> obok <c>queue.json</c>. Plik chwilowo niedostępny albo zapisany przez nowszą
/// wersję nie jest nadpisywany: wczytanie oddaje stan tylko do odczytu, a zapis go pomija. Plik uszkodzony
/// zostaje w kopii obok, a stan zaczyna się od nowa.
/// </summary>
public static class RadioStateStore
{
    public static RadioState Load() => Load(AppPaths.RadioFile);

    public static RadioState Load(string path) => Load(path, KeepCopy);

    /// <param name="keepCopy">Zachowuje kopię uszkodzonego pliku; fałsz, gdy się nie udało.</param>
    internal static RadioState Load(string path, Func<string, bool> keepCopy)
    {
        if (!File.Exists(path))
            return new RadioState();

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Plik chwilowo zablokowany ma zwykle poprawną treść; w tej sesji nie wolno go nadpisać.
            Console.Error.WriteLine($"[cewka] zapisany stan radia niedostępny: {ex.Message}");
            return Unreadable();
        }

        try
        {
            var state = JsonSerializer.Deserialize(json, RadioJsonContext.Default.RadioState)
                ?? throw new JsonException("pusty stan");

            // Plik nowszej wersji nie jest nadpisywany, ale wczytany stan musi być tak samo kompletny.
            if (state.SchemaVersion > RadioState.CurrentSchema)
                state.ReadOnly = true;

            Normalise(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Plik nowszej wersji, w którym zmienił się typ pola: dla niej jest poprawny, więc
            // zostaje nietknięty, tak jak plik nowszej wersji, który dał się odczytać.
            if (DeclaredSchema(json) > RadioState.CurrentSchema)
            {
                Console.Error.WriteLine($"[cewka] stan radia zapisała nowsza wersja programu: {ex.Message}");
                return Unreadable();
            }

            // Uszkodzonej treści nikt już nie odczyta; oryginał zostaje obok, a stan zapisuje się od nowa.
            // Bez kopii oryginał jest jedynym egzemplarzem, więc wtedy nie wolno go nadpisać.
            Console.Error.WriteLine($"[cewka] zapisany stan radia nieczytelny: {ex.Message}");
            return keepCopy(path) ? new RadioState() : Unreadable();
        }
    }

    /// <summary>Numer schematu zapisany w pliku; zero, gdy pliku nie da się rozebrać nawet na tyle.</summary>
    private static int DeclaredSchema(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("schemaVersion", out var version)
                   && version.ValueKind == JsonValueKind.Number
                   && version.TryGetInt32(out var number)
                ? number
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static bool KeepCopy(string path)
    {
        try
        {
            File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zachować kopii stanu radia: {ex.Message}");
            return false;
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
                foreach (var stream in station.Streams)
                    stream.Codec ??= "unknown";

                station.Categories = station.Categories?.Where(c => c is not null).ToList() ?? [];
                station.Languages = station.Languages?.Where(l => l is not null).ToList() ?? [];
                station.Logos = station.Logos?.Where(l => l is not null).ToList() ?? [];
                station.Name ??= station.Id;
                station.Country ??= string.Empty;
                result.Add(station);
            }

            return result;
        }
    }
}
