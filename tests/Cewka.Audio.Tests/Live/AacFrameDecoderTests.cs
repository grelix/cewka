using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Live;

/// <summary>Synchronizacja ADTS i życie dekodera systemu, na atrapie zamiast dekodera.</summary>
public sealed class AacFrameDecoderTests
{
    private const int FrameLength = 64;

    private sealed class FakeBackend : IAacBackend
    {
        public List<int> Pushed { get; } = [];
        public Queue<(int Frames, int Rate, int Channels, float[] Samples)> Ready { get; } = new();
        public Queue<(int Frames, int Rate, int Channels, float[] Samples)> Tail { get; } = new();
        public bool Disposed { get; private set; }
        public bool Failed { get; set; }

        public void Push(ReadOnlySpan<byte> frame) => Pushed.Add(frame.Length);

        public int Pull(Span<float> output, out int sampleRate, out int channels)
        {
            sampleRate = 0;
            channels = 0;

            if (!Ready.TryDequeue(out var next)) return 0;

            sampleRate = next.Rate;
            channels = next.Channels;
            next.Samples.CopyTo(output);
            return next.Frames;
        }

        public int Drain(Span<float> output, out int sampleRate, out int channels)
        {
            sampleRate = 0;
            channels = 0;

            if (!Tail.TryDequeue(out var next)) return 0;

            sampleRate = next.Rate;
            channels = next.Channels;
            next.Samples.CopyTo(output);
            return next.Frames;
        }

        public void Dispose() => Disposed = true;
    }

    private readonly List<FakeBackend> _backends = [];
    private readonly List<AdtsHeader> _headers = [];

    private AacFrameDecoder Decoder() => new(header =>
    {
        _headers.Add(header);
        _backends.Add(new FakeBackend());
        return _backends[^1];
    });

    private static byte[] Frames(int count = 4) => TestAudioFiles.AdtsFrames(count: count);

    private static int Feed(AacFrameDecoder decoder, ReadOnlySpan<byte> input)
    {
        decoder.Decode(input, new float[decoder.MaxOutputSamples], out var consumed, out _);
        return consumed;
    }

    [Fact]
    public void HeaderReadsTheStreamParameters()
    {
        Assert.True(AdtsHeader.TryParse(Frames(), out var header));

        Assert.Equal(AdtsHeader.LowComplexityProfile, header.Profile);
        Assert.Equal(44100, header.SampleRate);
        Assert.Equal(2, header.ChannelConfiguration);
        Assert.Equal(FrameLength, header.FrameLength);
        Assert.Equal(0x1210, header.AudioSpecificConfig);
    }

    [Theory]
    [InlineData(0, 0xFE)]    // bez synchronizacji
    [InlineData(1, 0xF3)]    // warstwa MP3
    [InlineData(2, 0x7C)]    // zarezerwowany indeks częstotliwości
    [InlineData(4, 0x00)]    // długość ramki krótsza od nagłówka
    public void HeaderRejectsWhatIsNotAFrame(int index, byte value)
    {
        var data = Frames(1);
        data[index] = value;
        if (index == 4) data[5] = 0;

        Assert.False(AdtsHeader.TryParse(data, out _));
    }

    [Fact]
    public void HeaderWithChecksumNeedsNineBytes()
    {
        var data = Frames(1);
        data[1] = 0xF0;
        data[4] = 1;
        data[5] = 0;

        // Długość 8: dość na nagłówek bez sumy kontrolnej, za mało na nagłówek z nią.
        Assert.False(AdtsHeader.TryParse(data, out _));
    }

    [Fact]
    public void FirstFrameWaitsForTheHeaderOfTheNextOne()
    {
        using var decoder = Decoder();

        Assert.Equal(0, Feed(decoder, Frames(1)));
        Assert.Empty(_backends);

        Assert.Equal(FrameLength, Feed(decoder, Frames(2)));
        Assert.Equal([FrameLength], _backends.Single().Pushed);
    }

    [Fact]
    public void AfterLockAWholeFrameIsEnoughAndAPartOneWaits()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));

        Assert.Equal(FrameLength, Feed(decoder, Frames(1)));
        Assert.Equal(0, Feed(decoder, Frames(1).AsSpan(0, FrameLength - 1)));
        Assert.Equal(2, _backends.Single().Pushed.Count);
    }

    [Fact]
    public void GarbageBeforeTheFirstFrameIsSkipped()
    {
        using var decoder = Decoder();
        byte[] data = [1, 2, 3, .. Frames(2)];

        Assert.Equal(3, Feed(decoder, data));
        Assert.Empty(_backends);
        Assert.Equal(FrameLength, Feed(decoder, data.AsSpan(3)));
    }

    [Fact]
    public void SyncWordInsideDataIsNotAFrameWithoutAConfirmingHeader()
    {
        using var decoder = Decoder();
        var data = new byte[256];
        Frames(1).CopyTo(data, 0);

        // Za pierwszym nagłówkiem nie ma następnego, więc całość to śmieci, poza końcówką czekającą na dane.
        Assert.Equal(data.Length - (AdtsHeader.MinLength - 1), Feed(decoder, data));
        Assert.Empty(_backends);
    }

    [Fact]
    public void LostLockSearchesAgain()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));

        byte[] damaged = [0, 0, .. Frames(2)];

        Assert.Equal(2, Feed(decoder, damaged));
        Assert.Equal(FrameLength, Feed(decoder, damaged.AsSpan(2)));
        Assert.Equal(2, _backends.Single().Pushed.Count);
    }

    [Fact]
    public void NewStreamParametersBuildANewBackend()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));

        var other = Frames(2);
        other[2] = other[FrameLength + 2] = 0x5C; // 22,05 kHz

        Assert.Equal(FrameLength, Feed(decoder, other));

        Assert.Equal(2, _backends.Count);
        Assert.True(_backends[0].Disposed);
        Assert.False(_backends[1].Disposed);
        Assert.Equal(22050, _headers[1].SampleRate);
    }

    [Fact]
    public void ResetDropsTheBackendAndTheLock()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));

        decoder.Reset();

        Assert.True(_backends[0].Disposed);
        Assert.Equal(0, Feed(decoder, Frames(1)));
        Assert.Equal(FrameLength, Feed(decoder, Frames(2)));
        Assert.Equal(2, _backends.Count);
    }

    [Fact]
    public void BackendThatFailedIsReplacedOnTheNextFrame()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));
        _backends[0].Failed = true;

        Assert.Equal(FrameLength, Feed(decoder, Frames(1)));
        Assert.True(_backends[0].Disposed);
        Assert.Equal(FrameLength, Feed(decoder, Frames(1)));

        Assert.Equal(2, _backends.Count);
        Assert.Null(decoder.Failure);
    }

    [Fact]
    public void SamplesWaitingInTheBackendComeOutBeforeTheNextFrameIsTaken()
    {
        using var decoder = Decoder();
        Feed(decoder, Frames(2));
        _backends[0].Ready.Enqueue((2, 44100, 2, [1f, 2f, 3f, 4f]));

        var output = new float[decoder.MaxOutputSamples];
        var frames = decoder.Decode(Frames(1), output, out var consumed, out var format);

        Assert.Equal(2, frames);
        Assert.Equal(0, consumed);
        Assert.Equal(new LiveFrameFormat(44100, 2, 22), format);
        Assert.Equal([1f, 2f, 3f, 4f], output[..4]);
        Assert.Single(_backends[0].Pushed);
    }

    [Theory]
    [InlineData(LiveFailureReason.CodecUnavailable)]
    [InlineData(LiveFailureReason.UndecodableData)]
    public void MissingBackendIsReportedWithItsReason(LiveFailureReason reason)
    {
        using var decoder = new AacFrameDecoder(_ => throw new AacBackendException(reason, "szczegół"));

        Assert.Equal(Frames().Length, Feed(decoder, Frames()));

        Assert.Equal(new LiveFailure(reason, Codec: LiveCodec.Aac, Detail: "szczegół"), decoder.Failure);

        decoder.Reset();
        Assert.Null(decoder.Failure);
    }

    [Fact]
    public void StreamDecoderEndsWithTheReasonOfAMissingBackend()
    {
        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(20));

        using var decoder = new LiveStreamDecoder(
            source, 48000, 2, _ => new AacFrameDecoder(_ => throw new AacBackendException(LiveFailureReason.CodecUnavailable, "brak")));

        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out var frames));

        Assert.Equal(0, frames);
        Assert.Equal(LiveFailureReason.CodecUnavailable, decoder.EndFailure?.Reason);
        Assert.Equal(LiveCodec.Aac, decoder.EndFailure?.Codec);
    }

    [Fact]
    public void StreamDecoderTakesSamplesThatCameWithoutConsumedBytes()
    {
        var backend = new FakeBackend();
        var silence = new float[2048];
        backend.Ready.Enqueue((1024, 48000, 2, silence));
        backend.Ready.Enqueue((1024, 48000, 2, silence));
        backend.Ready.Enqueue((1024, 48000, 2, silence));

        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(4));

        using var decoder = new LiveStreamDecoder(source, 48000, 2, _ => new AacFrameDecoder(_ => backend));

        var total = 0;
        var buffer = new float[8192];
        while (decoder.ReadLive(buffer, out var frames) == LiveReadStatus.Data) total += frames;

        Assert.Equal(3 * 1024, total);
        Assert.Equal(4, backend.Pushed.Count);
    }

    private static long ReadAll(LiveStreamDecoder decoder)
    {
        var buffer = new float[8192];
        var total = 0L;
        while (decoder.ReadLive(buffer, out var frames) == LiveReadStatus.Data) total += frames;
        return total;
    }

    [Fact]
    public void TailOfTheBackendPlaysBeforeTheStreamEnds()
    {
        var backend = new FakeBackend();
        backend.Tail.Enqueue((1024, 48000, 2, new float[2048]));
        backend.Tail.Enqueue((512, 48000, 2, new float[1024]));

        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(4));
        source.Complete();

        using var decoder = new LiveStreamDecoder(source, 48000, 2, _ => new AacFrameDecoder(_ => backend));

        Assert.Equal(1536, ReadAll(decoder));
        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out _));
    }

    [Fact]
    public void TailOfTheBackendPlaysBeforeANewConnection()
    {
        var first = new FakeBackend();
        first.Tail.Enqueue((1024, 48000, 2, new float[2048]));
        var backends = new Queue<FakeBackend>([first, new FakeBackend()]);

        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(4)).Begin(LiveCodec.Aac).Add(Frames(4));

        using var decoder = new LiveStreamDecoder(source, 48000, 2, _ => new AacFrameDecoder(_ => backends.Dequeue()));

        Assert.Equal(1024, ReadAll(decoder));
        Assert.True(first.Disposed);
        Assert.Empty(backends);
    }

    [Fact]
    public void SamplesReadyDuringANetworkPauseArriveWithoutNewBytes()
    {
        var backend = new FakeBackend();
        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(4));

        using var decoder = new LiveStreamDecoder(source, 48000, 2, _ => new AacFrameDecoder(_ => backend));

        Assert.Equal(0, ReadAll(decoder));

        backend.Ready.Enqueue((1024, 48000, 2, new float[2048]));

        Assert.Equal(LiveReadStatus.Data, decoder.ReadLive(new float[2048], out var frames));
        Assert.Equal(1024, frames);
    }

    [Fact]
    public void DrainOfAFailedBackendGivesNothing()
    {
        using var decoder = new AacFrameDecoder(_ => throw new AacBackendException(LiveFailureReason.CodecUnavailable, "brak"));

        Assert.Equal(0, decoder.Drain(new float[64], out _));
        Assert.Equal(0, decoder.TakePending(new float[64], out _));
    }

    [Fact]
    public void FiveChannelsFollowTheWaveOrderOfTheSystemDecoders()
    {
        var backend = new FakeBackend();
        var centerOnly = new float[6 * 1024];
        for (var frame = 0; frame < 1024; frame++) centerOnly[frame * 6 + 2] = 0.5f;

        for (var i = 0; i < 8; i++) backend.Ready.Enqueue((1024, 48000, 6, centerOnly));

        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Frames(12));

        using var decoder = new LiveStreamDecoder(source, 48000, 2, _ => new AacFrameDecoder(_ => backend));

        var buffer = new float[8192];
        double left = 0, right = 0;
        while (decoder.ReadLive(buffer, out var frames) == LiveReadStatus.Data || frames > 0)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                left += Math.Abs(buffer[frame * 2]);
                right += Math.Abs(buffer[frame * 2 + 1]);
            }
        }

        Assert.True(left > 0);
        Assert.Equal(left, right, 3);
    }
}
