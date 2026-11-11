using System.Buffers;

namespace Cewka.Audio.Streaming.Hls;

/// <summary>Pobieranie dla strumienia HLS; adresy przechodzą tę samą regułę co przy otwarciu.</summary>
internal interface IHlsFetcher
{
    /// <summary>Cała playlista i adres, spod którego ostatecznie przyszła.</summary>
    Task<(byte[] Content, Uri FinalUrl)> FetchPlaylistAsync(Uri url, Uri from, CancellationToken ct);

    Task<HttpResponseMessage> OpenSegmentAsync(Uri url, Uri from, CancellationToken ct);
}

/// <summary>
/// HLS jako zwykły strumień bajtów ADTS albo MP3: sesja czyta go jak każde inne połączenie. Segmenty idą
/// kolejno, a playlista odświeża się według RFC 8216. Błąd w trakcie grania jest zawsze przejściowy: nowe
/// połączenie zaczyna od playlisty głównej, która odnawia też identyfikatory sesji w adresach wariantów.
/// </summary>
internal sealed class HlsAudioStream : Stream
{
    private const int ChunkBytes = 16 * 1024;

    /// <summary>Tyle bajtów segmentu rozstrzyga o jego rodzaju.</summary>
    private const int DetectBytes = 12;

    /// <summary>Tyle najwyżej dźwięku z końca playlisty: więcej wyszłoby poza bufor sesji i tylko opóźniło start.</summary>
    private const double MaxStartSeconds = 20;

    private const int StartTargets = 3;

    private static readonly TimeSpan MinReload = TimeSpan.FromMilliseconds(500);

    private readonly IHlsFetcher _fetcher;
    private readonly Uri _mediaUrl;
    private readonly Queue<HlsSegment> _segments = new();
    private readonly TsDemuxer _demuxer = new();
    private readonly ArrayBufferWriter<byte> _output = new(ChunkBytes);
    private readonly byte[] _chunk = new byte[ChunkBytes];
    private readonly byte[] _detect = new byte[DetectBytes];

    private Uri _reloadUrl;
    private double _targetSeconds;
    private bool _ended;
    private long _nextSequence;
    private DateTimeOffset _nextReload;

    private HttpResponseMessage? _segmentResponse;
    private Stream? _segmentBody;
    private SegmentKind _kind;
    private int _detected;
    private long _skipBytes;
    private int _outputRead;

    private enum SegmentKind { Unknown, Transport, Packed }

    public HlsAudioStream(IHlsFetcher fetcher, Uri mediaUrl, HlsMedia first)
    {
        _fetcher = fetcher;
        _mediaUrl = mediaUrl;
        _reloadUrl = mediaUrl;
        _targetSeconds = first.TargetSeconds;
        _ended = first.Ended;
        _nextReload = NextReload(changed: true);

        foreach (var segment in StartOf(first)) _segments.Enqueue(segment);

        _nextSequence = first.Segments.Count > 0 ? first.Segments[^1].Sequence + 1 : 0;
    }

    /// <summary>Ile bez nowych danych nie jest jeszcze zastojem: segmenty przychodzą co czas docelowy.</summary>
    public TimeSpan QuietLimit => TimeSpan.FromSeconds(_targetSeconds * StartTargets);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Kilka segmentów od końca, jak zaleca RFC; zamknięta playlista gra od początku.</summary>
    private static IEnumerable<HlsSegment> StartOf(HlsMedia playlist)
    {
        if (playlist.Ended) return playlist.Segments;

        var wanted = Math.Min(playlist.TargetSeconds * StartTargets, MaxStartSeconds);
        var collected = 0.0;
        var start = playlist.Segments.Count;

        while (start > 0 && collected < wanted)
        {
            start--;
            collected += playlist.Segments[start].Seconds;
        }

        return playlist.Segments.Skip(start);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_outputRead < _output.WrittenCount)
            {
                var count = Math.Min(buffer.Length, _output.WrittenCount - _outputRead);
                _output.WrittenMemory.Slice(_outputRead, count).CopyTo(buffer);
                _outputRead += count;
                return count;
            }

            _output.ResetWrittenCount();
            _outputRead = 0;

            if (_segmentBody is { } body)
            {
                // Urwanie w trakcie segmentu sesja rozpoznaje jak każde inne urwane połączenie.
                var read = await body.ReadAsync(_chunk, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    FinishSegment();
                    continue;
                }

                AddSegmentBytes(_chunk.AsSpan(0, read));
                continue;
            }

            if (!await OpenNextSegmentAsync(cancellationToken).ConfigureAwait(false)) return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task<bool> OpenNextSegmentAsync(CancellationToken ct)
    {
        while (!_segments.TryPeek(out _))
        {
            if (_ended) return false;

            var wait = _nextReload - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

            await ReloadAsync(ct).ConfigureAwait(false);
        }

        var segment = _segments.Dequeue();

        try
        {
            _segmentResponse = await _fetcher.OpenSegmentAsync(segment.Url, _reloadUrl, ct).ConfigureAwait(false);
            _segmentBody = await _segmentResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (LiveOpenException e)
        {
            CloseSegment();

            // Segment mógł już wypaść z serwera: to minie po nowym połączeniu. Stała odmowa adresu zostaje stała.
            if (e.Failure.Reason is LiveFailureReason.UrlBlocked or LiveFailureReason.AddressRefused) throw;

            throw Transient(e.Failure.Reason, e.Message);
        }

        _kind = SegmentKind.Unknown;
        _detected = 0;
        _skipBytes = 0;
        return true;
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        byte[] content;
        Uri finalUrl;

        try
        {
            (content, finalUrl) = await _fetcher.FetchPlaylistAsync(_mediaUrl, _reloadUrl, ct).ConfigureAwait(false);
        }
        catch (LiveOpenException e) when (e.Failure.Reason is not (LiveFailureReason.UrlBlocked or LiveFailureReason.AddressRefused))
        {
            throw Transient(e.Failure.Reason, e.Message);
        }

        if (HlsPlaylistParser.Parse(content, finalUrl) is not HlsMedia playlist)
            throw Transient(LiveFailureReason.UnrecognisedContent, "Zamiast playlisty wariantu przyszła główna");

        if (playlist.Unsupported is { } unsupported)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.HlsNotSupportedYet, Detail: unsupported));

        _reloadUrl = finalUrl;
        _targetSeconds = playlist.TargetSeconds;
        _ended = playlist.Ended;

        var changed = false;

        foreach (var segment in playlist.Segments)
        {
            if (segment.Sequence < _nextSequence) continue;

            _segments.Enqueue(segment);
            _nextSequence = segment.Sequence + 1;
            changed = true;
        }

        _nextReload = NextReload(changed);
    }

    /// <summary>Po zmianie po czasie docelowym, a bez zmiany po jego połowie.</summary>
    private DateTimeOffset NextReload(bool changed)
    {
        var wait = TimeSpan.FromSeconds(changed ? _targetSeconds : _targetSeconds / 2);
        return DateTimeOffset.UtcNow + (wait < MinReload ? MinReload : wait);
    }

    private void AddSegmentBytes(ReadOnlySpan<byte> data)
    {
        if (_kind == SegmentKind.Unknown)
        {
            var take = Math.Min(DetectBytes - _detected, data.Length);
            data[..take].CopyTo(_detect.AsSpan(_detected));
            _detected += take;
            data = data[take..];

            if (_detected < DetectBytes) return;

            Detect();
            AddClassified(_detect);
        }

        AddClassified(data);
    }

    /// <summary>Rodzaj z pierwszych bajtów: rozszerzenia adresów nie są wiarygodne.</summary>
    private void Detect()
    {
        ReadOnlySpan<byte> head = _detect.AsSpan(0, _detected);

        if (head.Length >= 8 && (head[4..8].SequenceEqual("ftyp"u8) || head[4..8].SequenceEqual("styp"u8)
            || head[4..8].SequenceEqual("moof"u8) || head[4..8].SequenceEqual("sidx"u8)))
        {
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.HlsNotSupportedYet, Detail: "Segmenty fMP4"));
        }

        if (head[0] == 0x47)
        {
            _kind = SegmentKind.Transport;
            return;
        }

        _kind = SegmentKind.Packed;

        // Zwykle znacznik czasu w ID3 przed każdym segmentem samego dźwięku.
        if (head.Length >= 10 && head.StartsWith("ID3"u8))
        {
            _skipBytes = 10L + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F));
            if ((head[5] & 0x10) != 0) _skipBytes += 10;
        }
    }

    private void AddClassified(ReadOnlySpan<byte> data)
    {
        if (_kind == SegmentKind.Transport)
        {
            _demuxer.Push(data, _output);
            return;
        }

        if (_skipBytes > 0)
        {
            var skip = (int)Math.Min(_skipBytes, data.Length);
            _skipBytes -= skip;
            data = data[skip..];
        }

        if (!data.IsEmpty) _output.Write(data);
    }

    private void FinishSegment()
    {
        // Segment krótszy od próbki rodzaju.
        if (_kind == SegmentKind.Unknown && _detected > 0)
        {
            Detect();
            AddClassified(_detect.AsSpan(0, _detected));
        }

        _demuxer.EndSegment();
        CloseSegment();
    }

    private void CloseSegment()
    {
        _segmentBody?.Dispose();
        _segmentResponse?.Dispose();
        _segmentBody = null;
        _segmentResponse = null;
    }

    private static LiveOpenException Transient(LiveFailureReason reason, string detail) =>
        new(new LiveFailure(reason, Detail: detail), transient: true);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseSegment();
        base.Dispose(disposing);
    }
}
