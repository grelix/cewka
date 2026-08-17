namespace Cewka.Audio.Metadata;

/// <summary>Everything read from a file's tags and header, ready for display.</summary>
public sealed class TrackMetadata
{
    public required string Path { get; init; }

    /// <summary>Falls back to the file name when the file carries no title tag.</summary>
    public required string Title { get; init; }

    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Kilobits per second, 0 when unknown.</summary>
    public int Bitrate { get; init; }

    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public bool IsVariableBitrate { get; init; }

    /// <summary>Codec name as reported by the tag reader, for example <c>FLAC</c>.</summary>
    public string? Codec { get; init; }

    /// <summary>Embedded cover art, in whatever format the file stored it.</summary>
    public byte[]? CoverArt { get; init; }

    /// <summary>Track gain from ReplayGain tags, in decibels.</summary>
    public double? ReplayGainTrack { get; init; }

    /// <summary>Album gain from ReplayGain tags, in decibels.</summary>
    public double? ReplayGainAlbum { get; init; }

    /// <summary>Track peak from ReplayGain tags, as a linear amplitude.</summary>
    public double? ReplayGainTrackPeak { get; init; }

    /// <summary>
    /// The badge shown beside the record, matching the design: codec, bit depth and rate.
    /// </summary>
    public string FormatBadge => BuildBadge(null);

    /// <summary>
    /// The badge, optionally with a live bitrate reading.
    /// <para>
    /// For a variable-bitrate file the figure in the tags is an average over the whole track:
    /// a quiet passage and a dense one are described by the same number. When a live reading
    /// is available it replaces that average and is marked <c>VBR</c>, so it is clear the
    /// value is meant to move.
    /// </para>
    /// </summary>
    public string BuildBadge(int? liveBitrate)
    {
        var codec = Codec ?? "?";
        var rate = SampleRate > 0
            ? (SampleRate % 1000 == 0
                ? $"{SampleRate / 1000} kHz"
                : $"{SampleRate / 1000.0:0.#} kHz".Replace('.', ','))
            : null;

        if (IsVariableBitrate && liveBitrate is > 0 && rate is not null)
            return $"{codec} VBR {liveBitrate} kbps / {rate}";

        // Lossless formats are described by bit depth, lossy ones by bitrate.
        if (BitDepth > 0 && rate is not null)
            return $"{codec} {BitDepth} bit / {rate}";

        if (Bitrate > 0 && rate is not null)
        {
            var prefix = IsVariableBitrate ? "VBR ~" : string.Empty;
            return $"{codec} {prefix}{Bitrate} kbps / {rate}";
        }

        return rate is not null ? $"{codec} / {rate}" : codec;
    }
}
