using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Live;

public sealed class LiveStreamDecoderTests
{
    private const int OutputRate = 48000;
    private const int OutputChannels = 2;
    private const int Mp3Frame = 1152;

    private static LiveStreamDecoder Open(FakeByteSource source, Func<LiveCodec, ILiveFrameDecoder?>? frameDecoders = null) =>
        new(source, OutputRate, OutputChannels, frameDecoders);

    /// <summary>Czyta, dopóki są dane; zwraca stan, na którym stanęło.</summary>
    private static LiveReadStatus Drain(ILiveDecoder decoder, List<float> samples, int readFrames = 1024)
    {
        var buffer = new float[readFrames * OutputChannels];

        while (true)
        {
            var status = decoder.ReadLive(buffer, out var frames);
            if (status != LiveReadStatus.Data) return status;

            Assert.True(frames > 0);
            samples.AddRange(buffer.AsSpan(0, frames * OutputChannels).ToArray());
        }
    }

    private static List<float> DecodeWhole(byte[] data)
    {
        var source = new FakeByteSource().Begin().Add(data);
        source.Complete();

        using var decoder = Open(source);
        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));
        return samples;
    }

    // ---------- dane, czekam, koniec ----------

    [Fact]
    public void BelowTheMinimumPortionWaitsWithoutTouchingTheFrameDecoder()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(40, 1, OutputRate);
        var counting = new CountingFrameDecoder(new Mp3FrameDecoder());
        var source = new FakeByteSource().Begin().Add(data.AsSpan(0, 5999));

        using var decoder = Open(source, _ => counting);
        var buffer = new float[2048];

        Assert.Equal(LiveReadStatus.Waiting, decoder.ReadLive(buffer, out var frames));
        Assert.Equal(0, frames);
        Assert.Equal(0, counting.Calls);

        source.Add(data.AsSpan(5999, 1));

        Assert.Equal(LiveReadStatus.Data, decoder.ReadLive(buffer, out frames));
        Assert.True(frames > 0);
        Assert.True(counting.Calls > 0);
    }

    [Fact]
    public void WaitingDoesNotEndTheStream()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(40, 2, OutputRate);
        var source = new FakeByteSource().Begin();

        using var decoder = Open(source);
        var buffer = new float[2048];

        for (var i = 0; i < 100; i++)
            Assert.Equal(LiveReadStatus.Waiting, decoder.ReadLive(buffer, out _));

        source.Add(data);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, samples));
        Assert.Contains(samples, sample => sample != 0f);
        Assert.Null(decoder.EndFailure);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(137, 0)]
    [InlineData(0, 4242)]
    public void SamplesDoNotDependOnThePortions(int portion, int seed)
    {
        var data = TestAudioFiles.Mp3NoiseFrames(60, 3, OutputRate);
        var expected = DecodeWhole(data);
        Assert.Contains(expected, sample => sample != 0f);

        var random = new Random(seed);
        var source = new FakeByteSource().Begin();
        using var decoder = Open(source);

        var actual = new List<float>();
        var offset = 0;

        while (offset < data.Length)
        {
            var size = Math.Min(portion > 0 ? portion : random.Next(1, 900), data.Length - offset);
            source.Add(data.AsSpan(offset, size));
            offset += size;

            Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, actual, 700));
        }

        source.Complete();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, actual, 700));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StartInTheMiddleOfAFrameFindsTheNextOne()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(40, 4, OutputRate);

        var samples = DecodeWhole(data[100..]);

        Assert.Contains(samples, sample => sample != 0f);
        Assert.Equal(0, samples.Count % (Mp3Frame * OutputChannels));
    }

    [Fact]
    public void StoppedSourceEndsWithoutFailure()
    {
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(40, 5, OutputRate));
        source.Complete();

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));
        Assert.NotEmpty(samples);
        Assert.Null(decoder.EndFailure);

        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out var frames));
        Assert.Equal(0, frames);
    }

    [Fact]
    public void FailedSourceEndsWithItsFailure()
    {
        var failure = new LiveFailure(LiveFailureReason.ConnectionFailed);
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(40, 6, OutputRate));
        source.Complete(failure);

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));
        Assert.NotEmpty(samples);
        Assert.Equal(failure, decoder.EndFailure);
    }

    [Fact]
    public void EmptyDestinationIsNotTakenForTheEnd()
    {
        var source = new FakeByteSource().Begin();

        using var decoder = Open(source);

        Assert.Equal(LiveReadStatus.Waiting, decoder.ReadLive(Span<float>.Empty, out var frames));
        Assert.Equal(0, frames);
    }

    [Fact]
    public void GarbageEndsWithUndecodableData()
    {
        var garbage = new byte[300 * 1024];
        new Random(77).NextBytes(garbage);
        for (var i = 0; i < garbage.Length; i++)
            if (garbage[i] == 0xFF) garbage[i] = 0;

        var source = new FakeByteSource().Begin().Add(garbage);

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));
        Assert.Empty(samples);
        Assert.Equal(LiveFailureReason.UndecodableData, decoder.EndFailure?.Reason);
        Assert.Equal(LiveCodec.Mp3, decoder.EndFailure?.Codec);
    }

    /// <summary>Źródło ma zapasowy adres: śmieci przepadają, a dekoder zaczyna od nowego połączenia.</summary>
    [Fact]
    public void GarbageWaitsForTheNextConnectionWhenTheSourceCanTryAnother()
    {
        var source = new FakeByteSource { HasFallback = true }.Begin().Add(new byte[300 * 1024]);

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, samples));
        Assert.Empty(samples);
        Assert.Null(decoder.EndFailure);
        Assert.Equal(LiveFailureReason.UndecodableData, Assert.Single(source.Undecodable).Reason);

        source.Begin().Add(TestAudioFiles.Mp3NoiseFrames(40));
        Drain(decoder, samples);

        Assert.NotEmpty(samples);
    }

    [Fact]
    public void FrameDecoderThatConsumesNothingLosesOneByteOnlyWithAFullWindow()
    {
        var stuck = new StuckFrameDecoder();
        var source = new FakeByteSource().Begin().Add(new byte[7000]);

        using var decoder = Open(source, _ => stuck);
        var buffer = new float[2048];

        // Okno niepełne: dekoder może dostać więcej, więc nic nie przepada.
        Assert.Equal(LiveReadStatus.Waiting, decoder.ReadLive(buffer, out _));
        Assert.Equal(7000, stuck.LastInputLength);

        source.Add(new byte[300 * 1024]);
        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(buffer, out _));
        Assert.Equal(LiveFailureReason.UndecodableData, decoder.EndFailure?.Reason);
    }

    [Fact]
    public void OggHasNoDecoderYet()
    {
        var source = new FakeByteSource().Begin(LiveCodec.Ogg).Add(new byte[6000]);

        using var decoder = Open(source);

        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out var frames));
        Assert.Equal(0, frames);
        Assert.Equal(LiveFailureReason.CodecNotSupportedYet, decoder.EndFailure?.Reason);
        Assert.Equal(LiveCodec.Ogg, decoder.EndFailure?.Codec);
        Assert.Equal(LiveCodec.Ogg, decoder.Codec);
    }

    [Fact]
    public void BytesBeforeTheBeginningHaveNoCodec()
    {
        var source = new FakeByteSource().Add(TestAudioFiles.Mp3NoiseFrames(40, 8, OutputRate));

        using var decoder = Open(source);

        Assert.Equal(LiveCodec.Unknown, decoder.Codec);
        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out _));
        Assert.Equal(LiveFailureReason.CodecNotSupportedYet, decoder.EndFailure?.Reason);
    }

    [Fact]
    public void LiveStreamHasNoLengthAndCannotSeek()
    {
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(40, 9, OutputRate));

        using var decoder = Open(source);
        Assert.Equal(LiveReadStatus.Data, decoder.ReadLive(new float[2048], out var frames));

        decoder.Seek(0);

        Assert.False(decoder.CanSeek);
        Assert.Equal(0, decoder.TotalFrames);
        Assert.Equal(frames, decoder.Position);
        Assert.Equal(OutputRate, decoder.SampleRate);
        Assert.Equal(OutputChannels, decoder.Channels);
        Assert.Equal(LiveCodec.Mp3, decoder.Codec);
    }

    [Fact]
    public void BitrateIsMeasuredAndReportedToTheSource()
    {
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(400, 10, OutputRate));

        using var decoder = Open(source);
        Drain(decoder, []);

        Assert.InRange(decoder.InstantaneousBitrate, 120, 136);
        Assert.Single(source.ReportedBitrates);
        Assert.InRange(source.ReportedBitrates[0], 120, 136);
    }

    // ---------- zmiana formatu i nieciągłość ----------

    [Fact]
    public void RateChangeInTheMiddleOfTheStreamKeepsTheTimeAndTheSound()
    {
        var before = TestAudioFiles.Mp3NoiseFrames(1000, 20);
        var after = TestAudioFiles.Mp3NoiseFrames(1000, 21, 48000);
        var source = new FakeByteSource().Begin().Add(before).Add(after);
        source.Complete();

        using var decoder = Open(source);

        var rates = new List<int>();
        var samples = new List<float>();
        var buffer = new float[2048];

        while (decoder.ReadLive(buffer, out var frames) == LiveReadStatus.Data)
        {
            samples.AddRange(buffer.AsSpan(0, frames * OutputChannels).ToArray());
            rates.Add(decoder.SourceSampleRate);
        }

        Assert.Equal([44100, 48000], rates.Distinct());
        Assert.Equal(48000, decoder.SourceSampleRate);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));

        var expected = 1000.0 * Mp3Frame * OutputRate / 44100 + 1000.0 * Mp3Frame;
        Assert.InRange(samples.Count / OutputChannels, expected * 0.98, expected * 1.02);

        // Po zmianie częstotliwości dekoder zaczyna od czystego stanu i bez resamplera.
        var tail = DecodeWhole(after);
        Assert.Equal(tail, samples.Skip(samples.Count - tail.Count));
    }

    [Fact]
    public void SwitchToMonoDuplicatesTheChannelFromTheFirstFrameOfTheNewFormat()
    {
        var stereo = TestAudioFiles.Mp3NoiseFrames(200, 22, OutputRate);
        var mono = TestAudioFiles.Mp3NoiseFrames(200, 23, OutputRate, 1);
        var source = new FakeByteSource().Begin().Add(stereo).Add(mono);
        source.Complete();

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples, 300));

        Assert.Equal(1, decoder.SourceChannels);

        var frames = samples.Count / OutputChannels;
        Assert.InRange(frames, (400 - 20) * Mp3Frame, 400 * Mp3Frame);

        // Stereo ma różne kanały, więc pomieszanie ramek byłoby widać; koniec to mono w obu.
        var firstDifferent = Enumerable.Range(0, frames).First(i => samples[2 * i] != samples[2 * i + 1]);
        Assert.InRange(firstDifferent, 0, Mp3Frame);

        for (var i = frames - 150 * Mp3Frame; i < frames; i++)
            Assert.Equal(samples[2 * i], samples[2 * i + 1]);

        Assert.Contains(samples.Skip(samples.Count - 2 * Mp3Frame), sample => sample != 0f);
    }

    [Fact]
    public void ReadSizeDoesNotChangeTheSamplesAcrossAFormatChange()
    {
        var data = TestAudioFiles.Concat(
            TestAudioFiles.Mp3NoiseFrames(60, 24),
            TestAudioFiles.Mp3NoiseFrames(60, 25, OutputRate),
            TestAudioFiles.Mp3NoiseFrames(60, 26, OutputRate, 1));

        var expected = DecodeWhole(data);

        var source = new FakeByteSource().Begin().Add(data);
        source.Complete();
        using var decoder = Open(source);

        var actual = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, actual, 311));

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void BeginForgetsDecoderAndResamplerState(int rate)
    {
        var first = TestAudioFiles.Mp3NoiseFrames(100, 27, rate);
        var second = TestAudioFiles.Mp3NoiseFrames(100, 28, rate);
        var expected = DecodeWhole(second);

        var source = new FakeByteSource().Begin().Add(first).Begin().Add(second);
        source.Complete();
        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));

        Assert.True(samples.Count > expected.Count);
        Assert.Equal(expected, samples.Skip(samples.Count - expected.Count));
    }

    /// <summary>Przed nowym połączeniem końcówka starego gra do ostatniej całej ramki: próg chroni tylko środek strumienia.</summary>
    [Fact]
    public void BeginDecodesEveryWholeFrameOfTheOldConnection()
    {
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(100, 29, OutputRate)).Begin();
        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, samples));

        Assert.Equal(100 * Mp3Frame * OutputChannels, samples.Count);
    }

    /// <summary>
    /// Serwer bez zapasu nadaje w czasie rzeczywistym, a okno dekodera i pierścień PCM biorą bajty od razu.
    /// Czas wirtualny: co ramkę przychodzi jedna ramka, dekoder dopełnia pierścień, wyjście bierze ramkę.
    /// W sieci zdarza się drganie, więc jedna przerwa w dostawie ma się zmieścić w zapasie.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(128)]
    public void LowBitrateStationWithoutABacklogStartsWithoutAGap(int kilobits)
    {
        const int ringFrames = OutputRate / 2;
        const int frameCount = 25_000 / 24;
        const int jitterFrames = 250 / 24;

        var buffer = new CompressedBuffer(new LiveOptions());
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(kilobits);
        using var decoder = new LiveStreamDecoder(buffer, OutputRate, OutputChannels);

        var data = TestAudioFiles.Mp3NoiseFrames(frameCount, 33, OutputRate, kilobits: kilobits);
        var frameLength = data.Length / frameCount;
        var pcm = new float[1024 * OutputChannels];
        var closed = 0;
        buffer.GateChanged += open => closed += open ? 0 : 1;
        var startedAt = -1;
        var inRing = 0;
        var started = false;
        var gaps = 0;

        var delivered = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var heldBack = startedAt >= 0 && frame >= startedAt + 100 && frame < startedAt + 100 + jitterFrames;
            if (!heldBack)
            {
                buffer.Write(data.AsSpan(delivered, (frame + 1) * frameLength - delivered));
                delivered = (frame + 1) * frameLength;
            }

            while (inRing < ringFrames)
            {
                var take = Math.Min(1024, ringFrames - inRing) * OutputChannels;
                if (decoder.ReadLive(pcm.AsSpan(0, take), out var frames) != LiveReadStatus.Data) break;
                inRing += frames;
            }

            if (inRing > 0 && !started)
            {
                started = true;
                startedAt = frame;
            }

            if (!started) continue;

            if (inRing < Mp3Frame) gaps++;
            inRing = Math.Max(0, inRing - Mp3Frame);
        }

        Assert.True(started);
        Assert.Equal(0, gaps);
        Assert.Equal(0, closed);
    }

    /// <summary>Pomiar nowego połączenia zgłasza się zawsze, także gdy wyszedł tak samo jak poprzedni.</summary>
    [Fact]
    public void BeginForgetsTheReportedBitrate()
    {
        var noise = TestAudioFiles.Mp3NoiseFrames(100, 31, OutputRate);
        var source = new FakeByteSource().Begin().Add(noise);
        using var decoder = Open(source);
        var samples = new List<float>();
        Drain(decoder, samples);
        Assert.Single(source.ReportedBitrates);

        source.Begin().Add(noise);
        Drain(decoder, samples);

        Assert.Equal(2, source.ReportedBitrates.Count);
    }

    /// <summary>Końcówka starego połączenia gra już po znaczniku nowego, więc jej pomiar nie może zmylić bufora.</summary>
    [Fact]
    public void TailOfTheOldConnectionDoesNotReportItsBitrate()
    {
        // Przy 64 kb/s całe okno to sekunda, więc pomiar dojrzewa już w końcówce.
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(40, 31, OutputRate, kilobits: 64)).Begin();
        using var decoder = Open(source);

        var samples = new List<float>();
        Drain(decoder, samples);

        Assert.Equal(40 * Mp3Frame * OutputChannels, samples.Count);
        Assert.Empty(source.ReportedBitrates);
    }

    /// <summary>Koniec strumienia działa tak samo jak początek nowego połączenia.</summary>
    [Fact]
    public void EndDecodesEveryWholeFrame()
    {
        var source = new FakeByteSource().Begin().Add(TestAudioFiles.Mp3NoiseFrames(100, 29, OutputRate));
        source.Complete();
        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));

        Assert.Equal(100 * Mp3Frame * OutputChannels, samples.Count);
    }

    /// <summary>Urwana ostatnia ramka to nie dźwięk: znika bez błędu.</summary>
    [Fact]
    public void BeginDropsAPartialLastFrame()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(20, 29, OutputRate);
        var source = new FakeByteSource().Begin().Add(data.AsSpan(0, data.Length - 100)).Begin();
        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, samples));

        Assert.Equal(19 * Mp3Frame * OutputChannels, samples.Count);
        Assert.Null(decoder.EndFailure);
    }

    [Fact]
    public void BeginWithTheSameCodecResetsTheDecoderAndAnotherCodecReplacesIt()
    {
        var created = new List<CountingFrameDecoder>();
        var noise = TestAudioFiles.Mp3NoiseFrames(40, 30, OutputRate);
        var source = new FakeByteSource().Begin().Add(noise).Begin().Add(noise).Begin(LiveCodec.Ogg).Add(new byte[6000]);

        using var decoder = Open(source, codec =>
        {
            if (codec != LiveCodec.Mp3) return null;
            created.Add(new CountingFrameDecoder(new Mp3FrameDecoder()));
            return created[^1];
        });

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Ended, Drain(decoder, samples));

        Assert.NotEmpty(samples);
        Assert.Single(created);
        Assert.Equal(1, created[0].Resets);
        Assert.Equal(LiveFailureReason.CodecNotSupportedYet, decoder.EndFailure?.Reason);
        Assert.Equal(LiveCodec.Ogg, decoder.EndFailure?.Codec);
    }

    // ---------- znaczniki ----------

    private const int NoiseFrameLength = 384;

    private static List<(StreamMarker Marker, long Frame)> TakeMarkers(ILiveDecoder decoder)
    {
        var taken = new List<(StreamMarker, long)>();
        while (decoder.TryTakeMarker(out var marker, out var frame)) taken.Add((marker, frame));
        return taken;
    }

    [Fact]
    public void TitleDoesNotOvertakeTheSound()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(100, 40, OutputRate);
        var source = new FakeByteSource().Begin()
            .Add(data.AsSpan(0, 10 * NoiseFrameLength))
            .Title("Następny")
            .Add(data.AsSpan(10 * NoiseFrameLength));

        using var decoder = Open(source);
        var buffer = new float[Mp3Frame / 4 * OutputChannels];

        var titles = new List<(StreamTitle Title, long Frame)>();
        while (decoder.ReadLive(buffer, out _) == LiveReadStatus.Data)
        {
            foreach (var (marker, frame) in TakeMarkers(decoder))
            {
                if (marker is StreamTitle title) titles.Add((title, frame));
            }
        }

        var (taken, at) = Assert.Single(titles);
        Assert.Equal("Następny", taken.Title);
        Assert.InRange(at, 9 * Mp3Frame, 11 * Mp3Frame);
    }

    [Fact]
    public void TitleWaitsWhileTheBytesBeforeItWereNotDecoded()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(40, 41, OutputRate);
        var source = new FakeByteSource().Begin().Add(data.AsSpan(0, 1000)).Title("A").Add(data.AsSpan(1000, 4999));

        using var decoder = Open(source);

        Assert.Equal(LiveReadStatus.Waiting, decoder.ReadLive(new float[2048], out _));
        Assert.DoesNotContain(TakeMarkers(decoder), item => item.Marker is StreamTitle);

        source.Add(data.AsSpan(5999));

        var samples = new List<float>();
        Drain(decoder, samples);
        Assert.Contains(TakeMarkers(decoder), item => item.Marker is StreamTitle { Title: "A" });
    }

    [Fact]
    public void OutputFramesOfMarkersNeverGoBack()
    {
        var random = new Random(4242);
        var data = TestAudioFiles.Mp3NoiseFrames(600, 42, OutputRate);
        var source = new FakeByteSource().Begin();

        var offset = 0;
        var added = 0;
        while (offset < data.Length)
        {
            var size = Math.Min(random.Next(1, 3000), data.Length - offset);
            source.Add(data.AsSpan(offset, size)).Title($"T{added++}");
            offset += size;
        }

        source.Complete();
        using var decoder = Open(source);

        var taken = new List<(StreamMarker Marker, long Frame)>();
        var buffer = new float[2048];
        while (decoder.ReadLive(buffer.AsSpan(0, 2 * random.Next(1, 1024)), out _) != LiveReadStatus.Ended)
        {
            taken.AddRange(TakeMarkers(decoder));
        }

        taken.AddRange(TakeMarkers(decoder));

        var titles = taken.Where(item => item.Marker is StreamTitle).ToList();
        Assert.Equal(added, titles.Count);

        for (var i = 0; i < titles.Count; i++)
            Assert.Equal($"T{i}", ((StreamTitle)titles[i].Marker).Title);

        for (var i = 1; i < taken.Count; i++)
            Assert.True(taken[i].Frame >= taken[i - 1].Frame);

        Assert.All(taken, item => Assert.InRange(item.Frame, 0, decoder.Position));
    }

    [Fact]
    public void TitleBeforeANewConnectionIsNotLostAndKeepsItsOrder()
    {
        var noise = TestAudioFiles.Mp3NoiseFrames(40, 43, OutputRate);
        var source = new FakeByteSource().Begin()
            .Add(noise)
            .Title("Stary")
            .Add(noise.AsSpan(0, 500))
            .Begin()
            .Title(null)
            .Add(noise);

        using var decoder = Open(source);

        var samples = new List<float>();
        Assert.Equal(LiveReadStatus.Waiting, Drain(decoder, samples));

        var markers = TakeMarkers(decoder).Select(item => item.Marker).ToList();

        Assert.Equal(
            [new StreamBegin(LiveCodec.Mp3), new StreamTitle("Stary"), new StreamBegin(LiveCodec.Mp3), new StreamTitle(null)],
            markers);
    }

    [Fact]
    public void TitlesInTheDroppedTailAreStillDelivered()
    {
        var source = new FakeByteSource().Begin().Add(new byte[100]).Title("Koniec");
        source.Complete();

        using var decoder = Open(source);

        Assert.Equal(LiveReadStatus.Ended, decoder.ReadLive(new float[2048], out _));
        Assert.Contains(TakeMarkers(decoder), item => item.Marker is StreamTitle { Title: "Koniec" });
    }

    private sealed class CountingFrameDecoder(ILiveFrameDecoder inner) : ILiveFrameDecoder
    {
        public int Calls;
        public int Resets;

        public int MinInputBytes => inner.MinInputBytes;
        public int MaxOutputSamples => inner.MaxOutputSamples;

        public int Decode(ReadOnlySpan<byte> input, Span<float> output, out int consumed, out LiveFrameFormat format)
        {
            Calls++;
            return inner.Decode(input, output, out consumed, out format);
        }

        public void Reset()
        {
            Resets++;
            inner.Reset();
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class StuckFrameDecoder : ILiveFrameDecoder
    {
        public int LastInputLength;

        public int MinInputBytes => 6000;
        public int MaxOutputSamples => 2304;

        public int Decode(ReadOnlySpan<byte> input, Span<float> output, out int consumed, out LiveFrameFormat format)
        {
            LastInputLength = input.Length;
            consumed = 0;
            format = default;
            return 0;
        }

        public void Reset() { }

        public void Dispose() { }
    }
}
