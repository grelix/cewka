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
    /// Plakietka formatu, opcjonalnie z bieżącym pomiarem przepływności. Pomiar zastępuje
    /// średnią z tagów i dostaje oznaczenie <c>VBR</c>.
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

        // Format bezstratny opisuje głębia bitowa razem z przepływnością. Stratne nie mają
        // głębi bitowej, bo zapisują współczynniki przekształcenia, więc zostaje sama przepływność.
        if (BitDepth > 0 && rate is not null)
        {
            return Bitrate > 0
                ? $"{codec} {BitDepth} bit / {Bitrate} kbps / {rate}"
                : $"{codec} {BitDepth} bit / {rate}";
        }

        if (Bitrate > 0 && rate is not null)
        {
            var prefix = IsVariableBitrate ? "VBR ~" : string.Empty;
            return $"{codec} {prefix}{Bitrate} kbps / {rate}";
        }

        return rate is not null ? $"{codec} / {rate}" : codec;
    }
}
