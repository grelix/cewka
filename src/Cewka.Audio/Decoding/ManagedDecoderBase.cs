namespace Cewka.Audio.Decoding;

/// <summary>
/// Shared plumbing for decoders whose library hands back samples at the file's own rate
/// and channel count. Subclasses only have to fetch source frames; the pump below feeds
/// them through <see cref="SampleConverter"/> until the caller's buffer is full.
/// </summary>
internal abstract class ManagedDecoderBase : IAudioDecoder, IContinuousDecoder
{
    /// <summary>Upper bound on a single source read, so a slow track cannot allocate without limit.</summary>
    private const int MaxSourceFramesPerPull = 16384;

    /// <summary>Poziom z chwili utworzenia, w wątku, który otwiera dekoder.</summary>
    private readonly ResamplingLevel _level = AudioQuality.Current;

    private SampleConverter _converter = null!;
    private IReadOnlyList<Speaker> _layout = [];
    private float[] _source = [];
    private int _pendingOffset;
    private int _pendingFrames;
    private bool _endOfStream;
    private bool _reconfigured;

    /// <summary>Źródło oddało już jakąś ramkę albo było przewijane; stanu poprzedniego utworu przejąć nie wolno.</summary>
    private bool _started;

    /// <summary>Końcówkę filtru przejął następny utwór albo została już wydana.</summary>
    private bool _tailGone;

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

    public bool HoldTail { get; set; }

    /// <summary>Poziom konwersji, z jakim dekoder powstał.</summary>
    internal ResamplingLevel ResamplingLevel => _level;

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
        _converter = NewConverter();
    }

    /// <summary>
    /// Przebudowuje konwerter, gdy źródło zmieniło format w trakcie strumienia. HE-AAC z niejawnym
    /// SBR podaje przy otwarciu częstotliwość rdzenia, a pierwsza próbka przychodzi w podwójnej.
    /// Wolno wywołać tylko z <see cref="ReadSource"/>, zanim do bufora trafiła jakakolwiek ramka;
    /// <see cref="ReadSource"/> zwraca wtedy zero, a klasa bazowa pyta ponownie z buforem
    /// dobranym do nowej liczby kanałów. Pozycja i długość liczone są w ramkach wyjściowych,
    /// więc zostają bez zmian. Nowy konwerter zaczyna od zera: historia filtru należy do
    /// strumienia w starym formacie.
    /// </summary>
    protected void Reconfigure(int sourceRate, int sourceChannels, IReadOnlyList<Speaker> layout)
    {
        SourceSampleRate = sourceRate;
        SourceChannels = sourceChannels;
        _layout = layout;
        _converter = NewConverter();
        _reconfigured = true;
    }

    private SampleConverter NewConverter() =>
        new(SourceChannels, SourceSampleRate, Channels, SampleRate, _level, _layout);

    /// <summary>
    /// Fills <paramref name="destination"/> with at most <paramref name="frames"/> source frames
    /// and returns how many were written. Zero means the stream has ended, unless
    /// <see cref="Reconfigure"/> was called during this read.
    /// </summary>
    protected abstract int ReadSource(Span<float> destination, int frames);

    /// <summary>Zero z <see cref="ReadSource"/> oznacza chwilowy brak danych, a nie koniec.</summary>
    protected virtual bool SourceWaiting => false;

    /// <summary>Moves the underlying reader; the frame index is expressed in source frames.</summary>
    protected abstract void SeekSource(long sourceFrame);

    /// <summary>
    /// Przewija źródło i zwraca ramkę źródła, na której faktycznie stanęło. Dekoder, który wie
    /// o nieudanym przewinięciu, podaje tu prawdziwe miejsce, żeby pozycja opisywała to, co zagra.
    /// </summary>
    protected virtual long SeekSourceTo(long sourceFrame)
    {
        SeekSource(sourceFrame);
        return sourceFrame;
    }

    public int Read(Span<float> destination)
    {
        var wanted = destination.Length / Channels;
        if (wanted <= 0) return 0;

        var produced = 0;

        while (produced < wanted)
        {
            if (_pendingFrames == 0)
            {
                // Najpierw to, co konwerter może już policzyć z przyjętego wejścia: przy pełnym
                // buforze wywołującego część zostaje na następny odczyt, a nie może zginąć przy
                // zmianie formatu ani przejść do następnego utworu.
                var ready = _converter.Process([], 0, destination[(produced * Channels)..], out _);
                if (ready > 0)
                {
                    produced += ready;
                    continue;
                }

                if (_endOfStream)
                {
                    if (HoldTail) break;

                    var drained = DrainInto(destination[(produced * Channels)..]);
                    if (drained == 0) break;

                    produced += drained;
                    continue;
                }

                var need = (int)Math.Clamp(_converter.RequiredSourceFrames(wanted - produced), 1, MaxSourceFramesPerPull);
                EnsureSourceCapacity(need);

                _reconfigured = false;
                var got = ReadSource(_source.AsSpan(0, need * SourceChannels), need);
                if (got <= 0)
                {
                    if (_reconfigured) continue;
                    if (SourceWaiting) break;

                    _endOfStream = true;
                    continue;
                }

                _pendingOffset = 0;
                _pendingFrames = got;
                _started = true;
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

    public int DrainTail(Span<float> destination)
    {
        if (!_endOfStream || _pendingFrames != 0) return 0;

        var drained = DrainInto(destination);
        PositionFrames += drained;
        _meter?.AddFrames(drained);
        return drained;
    }

    private int DrainInto(Span<float> destination)
    {
        if (_tailGone) return 0;

        var drained = _converter.Drain(destination);
        if (drained == 0) _tailGone = true;
        return drained;
    }

    public bool TryContinueFrom(IAudioDecoder previous)
    {
        if (previous is not ManagedDecoderBase other || ReferenceEquals(other, this)) return false;

        // Ten zaczął już grać od zera albo poprzedni nie skończył: strumienie się nie sklejają.
        if (_started || PositionFrames != 0 || _endOfStream) return false;
        if (!other._endOfStream || other._pendingFrames != 0 || other._tailGone) return false;

        if (!other._converter.NeedsResampling || !other._converter.SameConversionAs(_converter)) return false;

        _converter = other._converter;
        other._converter = other.NewConverter();
        other._tailGone = true;
        return true;
    }

    public void Seek(long frame)
    {
        if (!CanSeek) return;

        var sourceFrame = SourceSampleRate == SampleRate
            ? frame
            : (long)Math.Round(frame * (double)SourceSampleRate / SampleRate);

        var target = Math.Max(0, sourceFrame);
        var landed = SeekSourceTo(target);

        // Resampler pamięta ostatnie próbki ze starego miejsca, po przejęciu także z poprzedniego utworu.
        _converter.Reset();

        _pendingOffset = 0;
        _pendingFrames = 0;
        _endOfStream = false;
        _tailGone = false;
        _started = true;
        PositionFrames = landed == target
            ? frame
            : SourceSampleRate == SampleRate ? landed : (long)Math.Round(landed * (double)SampleRate / SourceSampleRate);

        // Po przewinięciu zależność bajtów od czasu przestaje obowiązywać.
        _meter?.Reset();
    }

    private void EnsureSourceCapacity(int frames)
    {
        var needed = frames * SourceChannels;
        if (_source.Length < needed) _source = new float[needed];
    }

    protected virtual void DisposeCore() { }

    public void Dispose() => DisposeCore();
}
