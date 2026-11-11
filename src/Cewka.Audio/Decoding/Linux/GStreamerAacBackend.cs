using System.Runtime.Versioning;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Linux;

/// <summary>
/// Potok <c>appsrc → aacparse → avdec_aac → audioconvert → appsink</c> pchany ramkami ADTS. Działa we
/// własnym wątku, więc próbki wracają z opóźnieniem kilku ramek.
///
/// <para>Parametrów strumienia nie ma w opisie potoku: bierze je <c>aacparse</c> z nagłówków ramek,
/// a <c>avdec_aac</c> zgłasza częstotliwość po SBR i kanały po PS w opisie próbek.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class GStreamerAacBackend : IAacBackend
{
    private const string PipelineDescription =
        "appsrc name=source caps=audio/mpeg,mpegversion=4,stream-format=adts ! aacparse ! avdec_aac ! audioconvert ! " +
        "appsink name=sink sync=false max-buffers=16 caps=audio/x-raw,format=F32LE,layout=interleaved";

    /// <summary>
    /// Stacje wysyłają na początku paczkę kilku sekund dźwięku. Bez granicy cała wchodzi do kolejki źródła,
    /// zanim potok ruszy, i przepada przy końcu strumienia albo zmianie parametrów. Blokowanie w samym
    /// <c>appsrc</c> nie wchodzi w grę: po błędzie potoku wstrzymałoby wątek dekodujący na zawsze.
    /// </summary>
    private const ulong MaxQueuedBytes = 2048;

    private nint _pipeline;
    private nint _source;
    private nint _sink;
    private nint _bus;
    private bool _draining;

    public GStreamerAacBackend()
    {
        nint error;

        try
        {
            GStreamerDecoder.EnsureStarted();
            _pipeline = Gst.ParseLaunch(PipelineDescription, out error);
        }
        catch (DllNotFoundException e)
        {
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, e.Message);
        }

        // Przy braku elementu biblioteka potrafi oddać potok częściowy razem z błędem.
        if (error != nint.Zero)
        {
            var message = Gst.TakeMessage(error);
            Dispose();
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, message);
        }

        if (_pipeline == nint.Zero)
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, "GStreamer nie zbudował potoku AAC.");

        _bus = Gst.ElementGetBus(_pipeline);
        _source = Gst.BinGetByName(_pipeline, "source");
        _sink = Gst.BinGetByName(_pipeline, "sink");

        if (_source == nint.Zero || _sink == nint.Zero || Gst.SetState(_pipeline, Gst.StatePlaying) == Gst.ChangeFailure)
        {
            Dispose();
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, "GStreamer nie uruchomił potoku AAC.");
        }
    }

    public bool Failed { get; private set; }

    public void Push(ReadOnlySpan<byte> frame)
    {
        var buffer = Gst.BufferNewAllocate(nint.Zero, (nuint)frame.Length, nint.Zero);
        if (buffer == nint.Zero)
        {
            Failed = true;
            return;
        }

        fixed (byte* data = frame)
        {
            Gst.BufferFill(buffer, 0, data, (nuint)frame.Length);
        }

        // Potok przejmuje bufor także wtedy, gdy zgłasza błąd przepływu.
        if (Gst.AppSrcPushBuffer(_source, buffer) != Gst.FlowOk) Failed = true;
    }

    public int Pull(Span<float> output, out int sampleRate, out int channels)
    {
        // Pełna kolejka: krótkie oczekiwanie na próbki, a po nim i tak kolejna ramka, gdyby potok stanął.
        var timeout = Gst.AppSrcCurrentLevelBytes(_source) >= MaxQueuedBytes ? Gst.TwentyMilliseconds : 0;

        return TakeSample(output, timeout, out sampleRate, out channels);
    }

    public int Drain(Span<float> output, out int sampleRate, out int channels)
    {
        if (!_draining)
        {
            _draining = true;
            Gst.AppSrcEndOfStream(_source);
        }

        // Po końcu strumienia odczyt wraca od razu; limit działa tylko wtedy, gdy potok stanął.
        return TakeSample(output, Gst.TwoHundredMilliseconds, out sampleRate, out channels);
    }

    private int TakeSample(Span<float> output, ulong timeout, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;

        if (Gst.PopError(_bus) is { } error)
        {
            DecoderFailures.Log($"GStreamer: błąd potoku AAC: {error.Message}");
            Failed = true;
            return 0;
        }

        var sample = Gst.AppSinkTryPullSample(_sink, timeout);
        if (sample == nint.Zero) return 0;

        try
        {
            if (!GStreamerDecoder.TryReadFormat(sample, out sampleRate, out channels)) return 0;

            var buffer = Gst.SampleGetBuffer(sample);
            if (buffer == nint.Zero || !Gst.BufferMap(buffer, out var map, Gst.MapRead)) return 0;

            try
            {
                var samples = Math.Min((int)(map.Size / sizeof(float)), output.Length);
                var frames = samples / channels;
                new ReadOnlySpan<float>((void*)map.Data, frames * channels).CopyTo(output);
                return frames;
            }
            finally
            {
                Gst.BufferUnmap(buffer, ref map);
            }
        }
        finally
        {
            Gst.MiniObjectUnref(sample);
        }
    }

    public void Dispose()
    {
        if (_pipeline != nint.Zero) Gst.SetState(_pipeline, Gst.StateNull);

        if (_source != nint.Zero) Gst.ObjectUnref(_source);
        if (_sink != nint.Zero) Gst.ObjectUnref(_sink);
        if (_bus != nint.Zero) Gst.ObjectUnref(_bus);
        if (_pipeline != nint.Zero) Gst.ObjectUnref(_pipeline);

        _source = _sink = _bus = _pipeline = nint.Zero;
    }
}
