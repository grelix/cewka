using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Live;

/// <summary>Dekoder systemu dla jednego zestawu parametrów strumienia AAC. Wszystko na wątku dekodującym.</summary>
internal interface IAacBackend : IDisposable
{
    /// <summary>Stan po błędzie, z którego tylko nowy dekoder wychodzi czysto.</summary>
    bool Failed { get; }

    /// <summary>Oddaje całą ramkę ADTS. Ramkę odrzuconą przez dekoder traci się bez skutków dla następnych.</summary>
    void Push(ReadOnlySpan<byte> frame);

    /// <summary>Najwyżej jedna porcja próbek; zero, gdy dekoder nic jeszcze nie oddał. Nie blokuje na dłużej niż chwilę.</summary>
    int Pull(Span<float> output, out int sampleRate, out int channels);

    /// <summary>Ramek już nie będzie: kolejne porcje aż do zera; po nim dekoder nadaje się tylko do zamknięcia.</summary>
    int Drain(Span<float> output, out int sampleRate, out int channels);
}

/// <summary>Dekoder nie powstał; przyczyna określa, czy zawinił system, czy strumień.</summary>
internal sealed class AacBackendException(LiveFailureReason reason, string detail) : Exception(detail)
{
    public LiveFailureReason Reason { get; } = reason;
}

/// <summary>
/// AAC z ramek ADTS. Tu leży synchronizacja i zmiana parametrów, wspólne dla obu systemów; dekodowanie,
/// razem z SBR i PS, należy do dekodera systemu. Częstotliwość HE-AAC i liczba kanałów HE-AAC v2 wynikają
/// dopiero z próbek, bo nagłówek ADTS podaje tylko warstwę podstawową.
/// </summary>
internal sealed class AacFrameDecoder(Func<AdtsHeader, IAacBackend> createBackend) : ILiveFrameDecoder
{
    /// <summary>Ramka HE-AAC po SBR ma 2048 próbek na kanał, a ADTS opisuje najwyżej 8 kanałów.</summary>
    private const int MaxSamples = 2048 * 8;

    private IAacBackend? _backend;
    private AdtsHeader _stream;
    private bool _locked;
    private int _kilobits;

    public int MinInputBytes => AdtsHeader.MinLength;

    public int MaxOutputSamples => MaxSamples;

    public LiveFailure? Failure { get; private set; }

    public int Decode(ReadOnlySpan<byte> input, Span<float> output, out int consumed, out LiveFrameFormat format)
    {
        consumed = 0;

        // Próbki czekające w dekoderze mają pierwszeństwo przed kolejną ramką.
        var frames = Pull(output, out format);
        if (frames > 0) return frames;

        var start = FindFrame(input, out var header, out var complete);

        if (start > 0 || !complete)
        {
            consumed = start;
            return 0;
        }

        if (!EnsureBackend(header))
        {
            consumed = input.Length;
            return 0;
        }

        _backend!.Push(input[..header.FrameLength]);
        _locked = true;

        // Nagłówek podaje warstwę podstawową: 1024 próbki na ramkę także przy HE-AAC.
        _kilobits = (int)Math.Round(header.FrameLength * 8.0 * header.SampleRate / 1024 / 1000);
        consumed = header.FrameLength;

        return Pull(output, out format);
    }

    public int TakePending(Span<float> output, out LiveFrameFormat format) => Pull(output, out format);

    public int Drain(Span<float> output, out LiveFrameFormat format)
    {
        format = default;

        if (_backend is not { } backend) return 0;

        var frames = backend.Drain(output, out var sampleRate, out var channels);
        if (frames > 0) format = new LiveFrameFormat(sampleRate, channels, _kilobits);

        return frames;
    }

    public void Reset()
    {
        CloseBackend();
        Failure = null;
        _locked = false;
    }

    public void Dispose() => CloseBackend();

    /// <summary>
    /// Zwraca miejsce pierwszej ramki. Po synchronizacji wystarcza nagłówek na początku okna; bez niej
    /// nagłówek musi potwierdzić następny, bo 12 bitów synchronizacji pojawia się też w samych danych.
    /// </summary>
    private int FindFrame(ReadOnlySpan<byte> input, out AdtsHeader header, out bool complete)
    {
        complete = false;
        header = default;

        if (_locked && AdtsHeader.TryParse(input, out header) && header.SameStreamAs(_stream))
        {
            complete = input.Length >= header.FrameLength;
            return 0;
        }

        _locked = false;

        for (var at = 0; at + AdtsHeader.MinLength <= input.Length; at++)
        {
            if (!AdtsHeader.TryParse(input[at..], out header)) continue;

            var next = at + header.FrameLength;

            // Za krótkie okno: pomijane jest to, co przed nagłówkiem, a reszta czeka na dane.
            if (next + AdtsHeader.MinLength > input.Length) return at;

            if (!AdtsHeader.TryParse(input[next..], out var following) || !following.SameStreamAs(header)) continue;

            complete = at == 0;
            return at;
        }

        return Math.Max(0, input.Length - (AdtsHeader.MinLength - 1));
    }

    private bool EnsureBackend(AdtsHeader header)
    {
        if (_backend is not null && header.SameStreamAs(_stream)) return true;

        CloseBackend();

        try
        {
            _backend = createBackend(header);
            _stream = header;
            return true;
        }
        catch (AacBackendException failure)
        {
            Failure = new LiveFailure(failure.Reason, Codec: LiveCodec.Aac, Detail: failure.Message);
            return false;
        }
    }

    private int Pull(Span<float> output, out LiveFrameFormat format)
    {
        format = default;

        if (_backend is not { } backend) return 0;

        var frames = backend.Pull(output, out var sampleRate, out var channels);
        if (frames > 0) format = new LiveFrameFormat(sampleRate, channels, _kilobits);

        // Nowy dekoder powstanie przy następnej ramce; strumień bez dźwięku kończy limit bajtów wyżej.
        if (backend.Failed) CloseBackend();

        return frames;
    }

    private void CloseBackend()
    {
        _backend?.Dispose();
        _backend = null;
    }
}
