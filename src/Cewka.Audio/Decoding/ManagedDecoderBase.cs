namespace Cewka.Audio.Decoding;

/// <summary>
/// Shared plumbing for decoders whose library hands back samples at the file's own rate
/// and channel count. Subclasses only have to fetch source frames; the pump below feeds
/// them through <see cref="SampleConverter"/> until the caller's buffer is full.
/// </summary>
internal abstract class ManagedDecoderBase : IAudioDecoder
{
    /// <summary>Upper bound on a single source read, so a slow track cannot allocate without limit.</summary>
    private const int MaxSourceFramesPerPull = 16384;

    private SampleConverter _converter = null!;
    private float[] _source = [];
    private int _pendingOffset;
    private int _pendingFrames;
    private bool _endOfStream;

    private BitrateMeter? _meter;

    protected long PositionFrames;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int SourceSampleRate { get; private set; }
    public int SourceChannels { get; private set; }
    public long TotalFrames { get; protected set; }
    public long Position => PositionFrames;
    public abstract bool CanSeek { get; }
    public int InstantaneousBitrate => _meter?.Kilobits ?? 0;

    /// <summary>Called by the subclass once the underlying reader knows the source format.</summary>
    protected void Configure(int sourceRate, int sourceChannels, int targetRate, int targetChannels, BitrateMeter? meter = null)
    {
        _meter = meter;
        _meter?.Configure(targetRate);

        SourceSampleRate = sourceRate;
        SourceChannels = sourceChannels;
        SampleRate = targetRate;
        Channels = targetChannels;

        _converter = new SampleConverter(
            sourceChannels, sourceRate, targetChannels, targetRate, AudioQuality.ResamplerFilterOrder);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with at most <paramref name="frames"/> source frames
    /// and returns how many were written. Zero means the stream has ended.
    /// </summary>
    protected abstract int ReadSource(Span<float> destination, int frames);

    /// <summary>Moves the underlying reader; the frame index is expressed in source frames.</summary>
    protected abstract void SeekSource(long sourceFrame);

    public int Read(Span<float> destination)
    {
        var wanted = destination.Length / Channels;
        if (wanted <= 0) return 0;

        var produced = 0;

        while (produced < wanted)
        {
            if (_pendingFrames == 0)
            {
                if (_endOfStream) break;

                var need = (int)Math.Clamp(_converter.RequiredSourceFrames(wanted - produced), 1, MaxSourceFramesPerPull);
                EnsureSourceCapacity(need);

                var got = ReadSource(_source.AsSpan(0, need * SourceChannels), need);
                if (got <= 0)
                {
                    _endOfStream = true;
                    break;
                }

                _pendingOffset = 0;
                _pendingFrames = got;
            }

            var input = _source.AsSpan(_pendingOffset * SourceChannels, _pendingFrames * SourceChannels);
            var output = destination[(produced * Channels)..];

            var outFrames = _converter.Process(input, _pendingFrames, output, out var used);

            _pendingOffset += used;
            _pendingFrames -= used;
            produced += outFrames;

            // Neither side moved: the converter cannot make progress with this buffer size.
            if (outFrames == 0 && used == 0) break;
        }

        PositionFrames += produced;
        _meter?.AddFrames(produced);
        return produced;
    }

    public void Seek(long frame)
    {
        if (!CanSeek) return;

        var sourceFrame = SourceSampleRate == SampleRate
            ? frame
            : (long)Math.Round(frame * (double)SourceSampleRate / SampleRate);

        SeekSource(Math.Max(0, sourceFrame));

        _pendingOffset = 0;
        _pendingFrames = 0;
        _endOfStream = false;
        PositionFrames = frame;

        // Po przewinieciu zaleznosc bajtow od czasu przestaje obowiazywac.
        _meter?.Reset();
    }

    private void EnsureSourceCapacity(int frames)
    {
        var needed = frames * SourceChannels;
        if (_source.Length < needed) _source = new float[needed];
    }

    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        DisposeCore();
        _converter?.Dispose();
    }
}
