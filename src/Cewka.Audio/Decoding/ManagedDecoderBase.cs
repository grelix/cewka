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
    private IReadOnlyList<Speaker> _layout = [];
    private float[] _source = [];
    private int _pendingOffset;
    private int _pendingFrames;
    private bool _endOfStream;
    private bool _reconfigured;

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

    /// <summary>
    /// Called by the subclass once the underlying reader knows the source format.
    /// <paramref name="layout"/> names the speaker of each source channel, which the downmix needs.
    /// </summary>
    protected void Configure(
        int sourceRate, int sourceChannels, int targetRate, int targetChannels, BitrateMeter? meter,
        IReadOnlyList<Speaker> layout)
    {
        _meter = meter;
        _meter?.Configure(targetRate);

        SourceSampleRate = sourceRate;
        SourceChannels = sourceChannels;
        SampleRate = targetRate;
        Channels = targetChannels;

        _layout = layout;
        _converter = new SampleConverter(
            sourceChannels, sourceRate, targetChannels, targetRate, AudioQuality.ResamplerFilterOrder, layout);
    }

    /// <summary>
    /// Przebudowuje konwerter, gdy źródło zmieniło format w trakcie strumienia. HE-AAC z niejawnym
    /// SBR podaje przy otwarciu częstotliwość rdzenia, a pierwsza próbka przychodzi w podwójnej.
    /// Wolno wywołać tylko z <see cref="ReadSource"/>, zanim do bufora trafiła jakakolwiek ramka;
    /// <see cref="ReadSource"/> zwraca wtedy zero, a klasa bazowa pyta ponownie z buforem
    /// dobranym do nowej liczby kanałów. Pozycja i długość liczone są w ramkach wyjściowych,
    /// więc zostają bez zmian.
    /// </summary>
    protected void Reconfigure(int sourceRate, int sourceChannels, IReadOnlyList<Speaker> layout)
    {
        RebuildConverter(sourceRate, sourceChannels, layout);

        SourceSampleRate = sourceRate;
        SourceChannels = sourceChannels;
        _reconfigured = true;
    }

    private void RebuildConverter(int sourceRate, int sourceChannels, IReadOnlyList<Speaker> layout)
    {
        var previous = _converter;
        _converter = new SampleConverter(
            sourceChannels, sourceRate, Channels, SampleRate, previous.FilterOrder, layout);
        previous.Dispose();
        _layout = layout;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with at most <paramref name="frames"/> source frames
    /// and returns how many were written. Zero means the stream has ended, unless
    /// <see cref="Reconfigure"/> was called during this read.
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

                _reconfigured = false;
                var got = ReadSource(_source.AsSpan(0, need * SourceChannels), need);
                if (got <= 0)
                {
                    if (_reconfigured) continue;

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

        // Resampler pamięta ostatnie próbki i stan filtru ze starego miejsca.
        if (_converter.NeedsResampling) RebuildConverter(SourceSampleRate, SourceChannels, _layout);

        _pendingOffset = 0;
        _pendingFrames = 0;
        _endOfStream = false;
        PositionFrames = frame;

        // Po przewinięciu zależność bajtów od czasu przestaje obowiązywać.
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
