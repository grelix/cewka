using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;

namespace Cewka.Audio.Playback;

/// <summary>One item in the playback queue, with whatever is known about it so far.</summary>
public sealed class QueueEntry
{
    private readonly Lock _metadataLock = new();
    private TrackMetadata? _metadata;

    public required string Path { get; init; }

    /// <summary>Read lazily; null until the file has been examined.</summary>
    public TrackMetadata? Metadata
    {
        get => Volatile.Read(ref _metadata);
        internal set => Volatile.Write(ref _metadata, value);
    }

    public AudioFileFormat Format { get; internal set; } = AudioFileFormat.Unknown;

    /// <summary>
    /// Set when no decoder on this system can handle the file. The entry stays in the
    /// queue, greyed out, with <see cref="UnsupportedReason"/> explaining why.
    /// </summary>
    public bool IsUnsupported { get; internal set; }

    public string? UnsupportedReason { get; internal set; }

    /// <summary>
    /// Przyczyna ostatniego nieudanego otwarcia pliku w obsługiwanym formacie; null, gdy plik
    /// się otworzył. Nie wyklucza wpisu na stałe: błąd odczytu bywa chwilowy, więc każda
    /// kolejna próba otwiera plik od nowa.
    /// </summary>
    public TrackFailureReason? OpenFailure { get; internal set; }

    public TimeSpan Duration => Metadata?.Duration ?? TimeSpan.Zero;

    public string Title => Metadata?.Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public string? Artist => Metadata?.Artist;

    /// <summary>
    /// Reads tags on first use, so adding a large folder stays instant. Safe to call from a
    /// background thread; the engine calls it as a track is opened, and the interface calls
    /// it ahead of time to fill in the queue.
    ///
    /// <para>Pułapka: wołają ją naraz wątek dekodujący i wątek wypełniający kolejkę. Metadane
    /// publikuje się dlatego na końcu, po formacie i ocenie obsługi, a całość idzie pod
    /// blokadą: drugi wątek czeka na wynik, zamiast czytać plik po raz drugi albo zobaczyć
    /// metadane bez oznaczenia, że format jest nieobsługiwany.</para>
    /// </summary>
    public void EnsureMetadata()
    {
        if (Metadata is not null) return;

        lock (_metadataLock)
        {
            if (Metadata is not null) return;

            var format = AudioFileFormatDetector.Detect(Path);
            var metadata = MetadataReader.Read(Path);

            Format = format;

            if (!DecoderFactory.IsSupported(format))
            {
                UnsupportedReason = DecoderFactory.DescribeUnsupported(format);
                IsUnsupported = true;
            }

            Metadata = metadata;
        }
    }
}
