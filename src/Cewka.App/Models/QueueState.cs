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

    /// <summary>Resolves the mode from whichever form the file happens to carry.</summary>
    public RepeatMode ReadRepeat() => Repeat switch
    {
        "Track" => RepeatMode.Track,
        "Queue" => RepeatMode.Queue,
        "None" => RepeatMode.None,
        _ => RepeatQueue ? RepeatMode.Queue : RepeatMode.None,
    };
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QueueState))]
internal sealed partial class QueueStateJsonContext : JsonSerializerContext;
