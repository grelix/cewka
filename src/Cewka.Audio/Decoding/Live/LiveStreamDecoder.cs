using System.Diagnostics.CodeAnalysis;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Live;

/// <summary>
/// Dźwięk ze źródła bajtów na żywo. Bajty czeka się w oknie, aż starczy ich na porcję, której
/// dekoder ramek nie weźmie za śmieci; brak danych to stan, a nie koniec strumienia.
/// </summary>
internal sealed class LiveStreamDecoder : ManagedDecoderBase, ILiveDecoder
{
    private const int WindowBytes = 8192;

    /// <summary>Tyle bajtów bez jednej próbki to już nie jest strumień tego kodeka.</summary>
    private const int MaxBytesWithoutSound = 256 * 1024;

    /// <summary>Mniejsze zmiany przepływności nie przesuwają progów bufora.</summary>
    private const int BitrateReportStep = 8;

    private readonly ILiveByteSource _source;
    private readonly Func<LiveCodec, ILiveFrameDecoder?> _createFrameDecoder;
    private readonly BitrateMeter _meter = new();

    private readonly byte[] _window = new byte[WindowBytes];
    private int _windowStart;
    private int _windowEnd;
    private long _receivedBytes;

    /// <summary>Znacznik z miejscem w bajtach strumienia: obowiązuje, gdy zużycie je minie.</summary>
    private readonly Queue<(long Position, StreamMarker Marker)> _markers = new();

    private ILiveFrameDecoder? _frameDecoder;
    private float[] _pcm = [];
    private int _pcmOffset;
    private int _pcmFrames;
    private int _pcmRate;
    private int _pcmChannels;

    private StreamBegin? _pendingBegin;
    private bool _sourceEnded;
    private bool _waiting;
    private bool _finished;
    private int _bytesWithoutSound;
    private int _reportedBitrate;

    public LiveStreamDecoder(
        ILiveByteSource source, int outputRate, int outputChannels,
        Func<LiveCodec, ILiveFrameDecoder?>? frameDecoders = null)
    {
        _source = source;
        _createFrameDecoder = frameDecoders ?? LiveFrameDecoders.Create;

        Configure(outputRate, outputChannels, outputRate, outputChannels, _meter, ChannelLayouts.Wave(outputChannels));
    }

    public override bool CanSeek => false;

    public LiveFailure? EndFailure { get; private set; }

    public LiveCodec Codec { get; private set; }

    protected override bool SourceWaiting => _waiting;

    private int Available => _windowEnd - _windowStart;

    private long ConsumedBytes => _receivedBytes - Available;

    public LiveReadStatus ReadLive(Span<float> destination, out int frames)
    {
        frames = Read(destination);

        if (frames > 0) return LiveReadStatus.Data;
        return _finished ? LiveReadStatus.Ended : LiveReadStatus.Waiting;
    }

    public bool TryTakeMarker([MaybeNullWhen(false)] out StreamMarker marker, out long outputFrame)
    {
        outputFrame = Position;

        if (_markers.TryPeek(out var next) && next.Position <= ConsumedBytes)
        {
            _markers.Dequeue();
            marker = next.Marker;
            return true;
        }

        marker = null;
        return false;
    }

    protected override int ReadSource(Span<float> destination, int frames)
    {
        _waiting = false;

        while (true)
        {
            if (_pcmFrames > 0)
            {
                if (_pcmRate == SourceSampleRate && _pcmChannels == SourceChannels)
                    return TakePcm(destination, frames);

                // Ramka czeka; baza pyta ponownie z buforem dobranym do nowej liczby kanałów.
                // Dekodery systemu oddają kolejność WAVE; MP3 ma najwyżej dwa kanały, gdzie kolejności są te same.
                Reconfigure(_pcmRate, _pcmChannels, ChannelLayouts.Wave(_pcmChannels));
                return 0;
            }

            if (_finished) return 0;

            Fill();

            // Przed nowym połączeniem i na końcu nic już nie dojdzie: końcówka gra do ostatniej całej ramki.
            var draining = _pendingBegin is not null || _sourceEnded;

            if (_frameDecoder is { } decoder && (Available >= decoder.MinInputBytes || draining && Available > 0))
            {
                if (DecodeFrame(decoder)) continue;

                _waiting = true;
                return 0;
            }

            // Bajty się skończyły, a dekoder pracujący w tle może mieć jeszcze próbki.
            if (_frameDecoder is { } background)
            {
                var pending = draining
                    ? background.Drain(_pcm, out var pendingFormat)
                    : background.TakePending(_pcm, out pendingFormat);

                if (pending > 0)
                {
                    KeepPcm(pending, pendingFormat);
                    continue;
                }
            }

            if (_pendingBegin is { } begin)
            {
                Begin(begin);
                return 0;
            }

            if (_frameDecoder is null && Available > 0)
            {
                Finish(new LiveFailure(LiveFailureReason.CodecNotSupportedYet, Codec: Codec));
                return 0;
            }

            if (_sourceEnded)
            {
                Finish(_source.Failure);
                return 0;
            }

            _waiting = true;
            return 0;
        }
    }

    private int TakePcm(Span<float> destination, int frames)
    {
        var channels = SourceChannels;
        var take = Math.Min(frames, _pcmFrames);

        _pcm.AsSpan(_pcmOffset * channels, take * channels).CopyTo(destination);
        _pcmOffset += take;
        _pcmFrames -= take;

        return take;
    }

    /// <summary>Dopełnia okno; zatrzymuje się przed początkiem nowego połączenia.</summary>
    private void Fill()
    {
        if (_pendingBegin is not null || _sourceEnded) return;

        if (_windowStart > 0)
        {
            _window.AsSpan(_windowStart, Available).CopyTo(_window);
            _windowEnd -= _windowStart;
            _windowStart = 0;
        }

        while (_windowEnd < _window.Length)
        {
            var status = _source.Read(_window.AsSpan(_windowEnd), out var bytes, out var marker);

            if (status == LiveReadStatus.Waiting) return;

            if (status == LiveReadStatus.Ended)
            {
                _sourceEnded = true;
                return;
            }

            _windowEnd += bytes;
            _receivedBytes += bytes;

            if (marker is StreamBegin begin)
            {
                _pendingBegin = begin;
                return;
            }

            if (marker is not null) _markers.Enqueue((_receivedBytes, marker));
        }
    }

    /// <summary>False, gdy dekoder chce więcej bajtów, a okno może ich jeszcze przyjąć.</summary>
    private bool DecodeFrame(ILiveFrameDecoder decoder)
    {
        var frames = decoder.Decode(_window.AsSpan(_windowStart, Available), _pcm, out var consumed, out var format);

        if (decoder.Failure is { } failure)
        {
            OnUndecodable(failure);
            return true;
        }

        if (consumed <= 0 && frames <= 0)
        {
            if (Available < _window.Length && _pendingBegin is null && !_sourceEnded) return false;
            consumed = 1;
        }

        _windowStart += consumed;
        _meter.AddBytes(consumed);
        ReportBitrate();

        if (frames > 0)
        {
            KeepPcm(frames, format);
            return true;
        }

        _bytesWithoutSound += consumed;
        if (_bytesWithoutSound > MaxBytesWithoutSound)
            OnUndecodable(new LiveFailure(LiveFailureReason.UndecodableData, Codec: Codec));

        return true;
    }

    private void KeepPcm(int frames, LiveFrameFormat format)
    {
        _bytesWithoutSound = 0;
        _pcmOffset = 0;
        _pcmFrames = frames;
        _pcmRate = format.SampleRate;
        _pcmChannels = format.Channels;
    }

    /// <summary>O zapasowym adresie wie źródło; kończymy dopiero, gdy nie ma czego spróbować.</summary>
    private void OnUndecodable(LiveFailure failure)
    {
        if (!_source.ReportUndecodable(failure))
        {
            Finish(failure);
            return;
        }

        _bytesWithoutSound = 0;
        _windowStart = _windowEnd;
    }

    /// <summary>Nowe połączenie: stare bajty, stan dekodera i próbki resamplera nie mają z nim związku.</summary>
    private void Begin(StreamBegin begin)
    {
        _pendingBegin = null;
        _windowStart = 0;
        _windowEnd = 0;
        _bytesWithoutSound = 0;
        _reportedBitrate = 0;
        _meter.Reset();
        _markers.Enqueue((_receivedBytes, begin));

        if (_frameDecoder is not null && begin.Codec == Codec)
        {
            _frameDecoder.Reset();
        }
        else
        {
            _frameDecoder?.Dispose();
            _frameDecoder = _createFrameDecoder(begin.Codec);
            Codec = begin.Codec;
        }

        if (_frameDecoder is null)
        {
            Finish(new LiveFailure(LiveFailureReason.CodecNotSupportedYet, Codec: begin.Codec));
            return;
        }

        if (_pcm.Length < _frameDecoder.MaxOutputSamples) _pcm = new float[_frameDecoder.MaxOutputSamples];

        Reconfigure(SourceSampleRate, SourceChannels, ChannelLayouts.Wave(SourceChannels));
    }

    private void Finish(LiveFailure? failure)
    {
        _finished = true;
        EndFailure = failure;
        _windowStart = _windowEnd;
    }

    private void ReportBitrate()
    {
        // Końcówka starego połączenia: bufor liczy już progi nowego.
        if (_pendingBegin is not null) return;

        var kilobits = _meter.Kilobits;
        if (kilobits <= 0 || Math.Abs(kilobits - _reportedBitrate) <= BitrateReportStep) return;

        _reportedBitrate = kilobits;
        _source.ReportBitrate(kilobits);
    }

    protected override void SeekSource(long sourceFrame) { }

    protected override void DisposeCore() => _frameDecoder?.Dispose();
}
