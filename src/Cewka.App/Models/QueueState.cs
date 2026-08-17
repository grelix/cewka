using System.Text.Json.Serialization;
using Cewka.Audio.Playback;

namespace Cewka.App.Models;

/// <summary>
/// The playback queue as it stood when the application last closed.
/// <para>
/// Kept apart from <see cref="AppSettings"/> because it changes far more often and can grow
/// to thousands of entries; mixing the two would mean rewriting the whole settings file every
/// time a folder is added.
/// </para>
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
    /// The two-state form written by earlier versions. Read only for compatibility — a queue
    /// saved before repeat-one existed should not lose its setting on the first run afterwards.
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
