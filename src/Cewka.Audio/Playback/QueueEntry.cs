using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;

namespace Cewka.Audio.Playback;

/// <summary>One item in the playback queue, with whatever is known about it so far.</summary>
public sealed class QueueEntry
{
    public required string Path { get; init; }

    /// <summary>Read lazily; null until the file has been examined.</summary>
    public TrackMetadata? Metadata { get; internal set; }

    public AudioFileFormat Format { get; internal set; } = AudioFileFormat.Unknown;

    /// <summary>
    /// Set when no decoder on this system can handle the file. The entry stays in the
    /// queue, greyed out, with <see cref="UnsupportedReason"/> explaining why.
    /// </summary>
    public bool IsUnsupported { get; internal set; }

    public string? UnsupportedReason { get; internal set; }

    public TimeSpan Duration => Metadata?.Duration ?? TimeSpan.Zero;

    public string Title => Metadata?.Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public string? Artist => Metadata?.Artist;

    /// <summary>
    /// Reads tags on first use, so adding a large folder stays instant. Safe to call from a
    /// background thread; the engine calls it as a track is opened, and the interface calls
    /// it ahead of time to fill in the queue.
    /// </summary>
    public void EnsureMetadata()
    {
        if (Metadata is not null) return;

        Format = AudioFileFormatDetector.Detect(Path);
        Metadata = MetadataReader.Read(Path);

        if (!DecoderFactory.IsSupported(Format))
        {
            IsUnsupported = true;
            UnsupportedReason = DecoderFactory.DescribeUnsupported(Format);
        }
    }
}
