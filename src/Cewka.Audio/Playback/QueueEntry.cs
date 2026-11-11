using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Playback;

/// <summary>Stacja i jej połączenie: wpis stacji powstaje przy każdym łączeniu od nowa.</summary>
internal sealed record LiveBinding(LiveSource Source, ILiveSession Session);

/// <summary>One item in the playback queue, with whatever is known about it so far.</summary>
public sealed class QueueEntry
{
    private readonly Lock _metadataLock = new();
    private TrackMetadata? _metadata;

    public required string Path { get; init; }

    /// <summary>Tylko dla stacji; ta nigdy nie trafia do kolejki.</summary>
    internal LiveBinding? Live { get; init; }

    public bool IsLive => Live is not null;

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
    /// Czyta tagi przy pierwszym użyciu, żeby dodanie dużego folderu było natychmiastowe.
    ///
    /// <para>Pułapka: wołają ją naraz wątek dekodujący i wątek wypełniający kolejkę. Całość idzie
    /// pod blokadą, a metadane publikowane są na końcu, po formacie i ocenie obsługi, żeby drugi
    /// wątek nie zobaczył ich bez oznaczenia formatu nieobsługiwanego.</para>
    /// </summary>
    public void EnsureMetadata()
    {
        if (IsLive || Metadata is not null) return;

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
