using System.Globalization;

namespace Cewka.Audio.Metadata;

/// <summary>
/// Reads tags, cover art and technical details through ATL.NET.
/// <para>
/// ATL was chosen over the more widely used TagLib# purely on licensing: TagLib# is LGPL,
/// which raises the same difficulty for a single-file MIT build that ruled out FFmpeg.
/// </para>
/// </summary>
public static class MetadataReader
{
    /// <summary>
    /// Reads what the file has to say about itself. Never throws for a readable file:
    /// missing tags degrade to sensible defaults, because a queue entry that cannot be
    /// displayed is worse than one displayed by file name.
    /// </summary>
    public static TrackMetadata Read(string path)
    {
        try
        {
            var track = new ATL.Track(path);

            return new TrackMetadata
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(track.Title)
                    ? System.IO.Path.GetFileNameWithoutExtension(path)
                    : track.Title.Trim(),
                Artist = Clean(track.Artist),
                Album = Clean(track.Album),
                AlbumArtist = Clean(track.AlbumArtist),
                // A file with an empty or malformed date tag often parses to year 1;
                // showing that beside the album would be worse than showing nothing.
                Year = track.Date?.Year is > 1000 and var year ? year : null,
                TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : null,
                Duration = TimeSpan.FromMilliseconds(track.DurationMs),
                Bitrate = track.Bitrate,
                SampleRate = (int)Math.Round(track.SampleRate),
                BitDepth = track.BitDepth > 0 ? track.BitDepth : 0,
                Channels = track.ChannelsArrangement?.NbChannels ?? 0,
                IsVariableBitrate = track.IsVBR,
                Codec = DescribeCodec(track),
                CoverArt = track.EmbeddedPictures.Count > 0 ? track.EmbeddedPictures[0].PictureData : null,
                ReplayGainTrack = ReadGain(track, "REPLAYGAIN_TRACK_GAIN"),
                ReplayGainAlbum = ReadGain(track, "REPLAYGAIN_ALBUM_GAIN"),
                ReplayGainTrackPeak = ReadPeak(track, "REPLAYGAIN_TRACK_PEAK"),
            };
        }
        catch (Exception)
        {
            return Fallback(path);
        }
    }

    private static TrackMetadata Fallback(string path) => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
    };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? DescribeCodec(ATL.Track track)
    {
        var format = track.AudioFormat?.ShortName ?? track.AudioFormat?.Name;
        if (string.IsNullOrWhiteSpace(format)) return null;

        // ATL names some formats verbosely, and its short names are not the ones a listener
        // recognises — "MPEG" for an MP3, for one. The badge in the design is terse.
        var upper = format.ToUpperInvariant();

        if (upper.Contains("MPEG") || upper.Contains("MP3")) return "MP3";
        if (upper.Contains("FLAC")) return "FLAC";
        if (upper.Contains("OPUS")) return "OPUS";
        if (upper.Contains("VORBIS") || upper.Contains("OGG")) return "OGG";
        if (upper.Contains("WAV") || upper.Contains("PCM")) return "WAV";
        if (upper.Contains("ALAC")) return "ALAC";
        if (upper.Contains("AAC") || upper.Contains("MP4")) return "AAC";

        return upper;
    }

    /// <summary>
    /// ReplayGain values are stored as text with a unit, for example <c>-6.48 dB</c>.
    /// The decimal separator is always a full stop regardless of locale, so parsing has to
    /// be culture-invariant.
    /// </summary>
    private static double? ReadGain(ATL.Track track, string key)
    {
        var raw = FindField(track, key);
        if (raw is null) return null;

        var text = raw.Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        // A gain beyond this range is certainly a malformed tag, not a quiet recording.
        return value is >= -60 and <= 60 ? value : null;
    }

    private static double? ReadPeak(ATL.Track track, string key)
    {
        var raw = FindField(track, key);
        if (raw is null) return null;

        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               && value is > 0 and <= 10
            ? value
            : null;
    }

    /// <summary>Tag keys differ in case between containers, so the lookup ignores it.</summary>
    private static string? FindField(ATL.Track track, string key)
    {
        foreach (var pair in track.AdditionalFields)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value;
        }

        return null;
    }
}
