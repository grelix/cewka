using System.Text.Json.Serialization;
using Cewka.Audio.Playback;

namespace Cewka.App.Models;

/// <summary>
/// Kolejka z chwili ostatniego zamknięcia. Osobno od <see cref="AppSettings"/>, bo zmienia się
/// znacznie częściej i bywa długa na tysiące pozycji.
/// </summary>
public sealed class QueueState
{
    public List<string> Paths { get; set; } = [];

    /// <summary>Index of the track that was playing, or −1.</summary>
    public int CurrentIndex { get; set; } = -1;

    /// <summary>Position within that track, in seconds.</summary>
    public double PositionSeconds { get; set; }

    public bool Shuffle { get; set; }

    /// <summary>
    /// Repeat mode by name: <c>None</c>, <c>Queue</c> or <c>Track</c>.
    /// </summary>
    public string? Repeat { get; set; }

    /// <summary>
    /// Dwustanowa postać ze starszych plików, czytana tylko dla zgodności, żeby kolejka zapisana
    /// przed powtarzaniem jednego utworu nie straciła ustawienia.
    /// </summary>
    public bool RepeatQueue { get; set; }

    /// <summary>
    /// Otwarcia niedokończone przed zamknięciem okna, w kolejności przybycia; następne uruchomienie
    /// je dokańcza. Pominięte w pliku, gdy ich nie ma; starsze wersje pomijają nieznane pole.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PendingOpenState>? PendingOpens { get; set; }

    /// <summary>Resolves the mode from whichever form the file happens to carry.</summary>
    public RepeatMode ReadRepeat() => Repeat switch
    {
        "Track" => RepeatMode.Track,
        "Queue" => RepeatMode.Queue,
        "None" => RepeatMode.None,
        _ => RepeatQueue ? RepeatMode.Queue : RepeatMode.None,
    };
}

/// <summary>Otwarcie plików przerwane zamknięciem okna.</summary>
public sealed class PendingOpenState
{
    /// <summary>Ścieżki w postaci przysłanej: pliki i foldery, a przy <c>Playlist</c> plik listy.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Akcja otwierania: <c>Outside</c>, <c>Add</c>, <c>Replace</c>, <c>Append</c> albo <c>Playlist</c>.</summary>
    public string? Action { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QueueState))]
internal sealed partial class QueueStateJsonContext : JsonSerializerContext;
