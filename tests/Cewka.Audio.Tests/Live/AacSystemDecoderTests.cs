using System.Diagnostics;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Live;

/// <summary>Pominięcie tam, gdzie system nie ma dekodera AAC (Windows N, Linux bez gst-libav).</summary>
public sealed class AacDecoderFactAttribute : FactAttribute
{
    public AacDecoderFactAttribute()
    {
        if (!LiveFrameDecoders.IsPlayable(LiveCodec.Aac)) Skip = "System nie ma dekodera AAC.";
    }
}

/// <summary>
/// Prawdziwy dekoder systemu na tonach sinusoidalnych zapisanych w ADTS. Pliki w <c>Dane</c> powstały
/// z generatora: <c>audiotestsrc ! avenc_aac ! aacparse</c> w GStreamerze.
/// </summary>
public sealed class AacSystemDecoderTests
{
    private const int StereoFrames = 65 * 1024;
    private const int MonoFrames = 22 * 1024;

    private static readonly byte[] Stereo = Load("ton-440-stereo-44100.aac");
    private static readonly byte[] Mono = Load("ton-880-mono-22050.aac");

    private static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", name));

    private sealed record Part(int Rate, int Channels, List<float> Samples)
    {
        public int Frames => Samples.Count / Channels;

        /// <summary>Wysokość dźwięku z przejść przez zero w środku części, z pierwszego kanału.</summary>
        public double Pitch()
        {
            var skip = Frames / 4;
            var count = Frames / 2;
            var crossings = 0;

            for (var frame = skip + 1; frame < skip + count; frame++)
            {
                var before = Samples[(frame - 1) * Channels];
                var now = Samples[frame * Channels];
                if (before <= 0 && now > 0) crossings++;
            }

            return crossings * (double)Rate / count;
        }

        public double Rms()
        {
            var sum = 0.0;
            foreach (var sample in Samples) sum += sample * sample;
            return Math.Sqrt(sum / Samples.Count);
        }
    }

    /// <summary>Podaje całe dane i zbiera próbki, aż dekoder pracujący w tle przestanie je oddawać.</summary>
    private static List<Part> DecodeAll(ILiveFrameDecoder decoder, ReadOnlySpan<byte> data)
    {
        var parts = new List<Part>();
        var output = new float[decoder.MaxOutputSamples];
        var quiet = Stopwatch.StartNew();
        var position = 0;

        while (quiet.ElapsedMilliseconds < 500)
        {
            var frames = decoder.Decode(data[position..], output, out var consumed, out var format);
            position += consumed;

            if (frames > 0)
            {
                if (parts.Count == 0 || parts[^1].Rate != format.SampleRate || parts[^1].Channels != format.Channels)
                    parts.Add(new Part(format.SampleRate, format.Channels, []));

                parts[^1].Samples.AddRange(output.AsSpan(0, frames * format.Channels).ToArray());
            }

            if (frames > 0 || consumed > 0) quiet.Restart();
            else Thread.Sleep(1);
        }

        return parts;
    }

    private static AacFrameDecoder Open() => new(AacBackends.Create);

    [AacDecoderFact]
    public void StereoToneKeepsItsLengthPitchAndFormat()
    {
        using var decoder = Open();

        var part = DecodeAll(decoder, Stereo).Single();

        Assert.Equal(44100, part.Rate);
        Assert.Equal(2, part.Channels);
        Assert.InRange(part.Frames, StereoFrames - 4096, StereoFrames + 4096);
        Assert.InRange(part.Pitch(), 435, 445);
        Assert.InRange(part.Rms(), 0.2, 0.5);
        Assert.Null(decoder.Failure);
    }

    [AacDecoderFact]
    public void MonoToneKeepsItsPitchAtLowRate()
    {
        using var decoder = Open();

        var part = DecodeAll(decoder, Mono).Single();

        Assert.InRange(part.Pitch(), 870, 890);
        Assert.InRange(part.Frames * (double)22050 / part.Rate, MonoFrames - 4096, MonoFrames + 4096);
    }

    [AacDecoderFact]
    public void ParametersChangingInTheStreamChangeTheFormat()
    {
        using var decoder = Open();

        var parts = DecodeAll(decoder, [.. Stereo, .. Mono]);

        Assert.True(parts.Count >= 2, $"części: {parts.Count}");
        Assert.InRange(parts[0].Pitch(), 435, 445);
        Assert.InRange(parts[^1].Pitch(), 870, 890);
    }

    [AacDecoderFact]
    public void DamagedFramesDoNotStopTheStream()
    {
        var data = Stereo.ToArray();

        // Treść kilku ramek i jeden nagłówek w środku strumienia.
        var position = 0;
        for (var frame = 0; AdtsHeader.TryParse(data.AsSpan(position), out var header); frame++)
        {
            if (frame is >= 20 and < 25)
            {
                for (var i = position + AdtsHeader.MinLength; i < position + header.FrameLength; i++) data[i] ^= 0x5A;
            }

            var length = header.FrameLength;
            if (frame == 30) data[position] = 0;

            position += length;
        }

        using var decoder = Open();

        var frames = DecodeAll(decoder, data).Sum(part => part.Frames);

        Assert.True(frames > StereoFrames * 3 / 4, $"ramek: {frames}");
        Assert.Null(decoder.Failure);
    }

    [AacDecoderFact]
    public void ResetStartsTheStreamCleanly()
    {
        using var decoder = Open();

        DecodeAll(decoder, Stereo);
        decoder.Reset();

        var part = DecodeAll(decoder, Mono).Single();

        Assert.InRange(part.Pitch(), 870, 890);
    }

    [AacDecoderFact]
    public void RandomDamageNeverThrows()
    {
        var random = new Random(20261105);
        var length = Math.Min(Stereo.Length, 2048);

        for (var round = 0; round < 150; round++)
        {
            var data = Stereo.AsSpan(0, length).ToArray();

            if (round % 3 == 0)
            {
                random.NextBytes(data);
            }
            else
            {
                for (var hit = random.Next(1, 40); hit > 0; hit--) data[random.Next(length)] = (byte)random.Next(256);
            }

            using var decoder = Open();
            var output = new float[decoder.MaxOutputSamples];
            var position = 0;

            for (var call = 0; call < 200 && position < data.Length; call++)
            {
                var frames = decoder.Decode(data.AsSpan(position), output, out var consumed, out var format);

                Assert.True(consumed >= 0 && position + consumed <= data.Length);
                if (frames > 0) Assert.True(frames * format.Channels <= output.Length);

                if (consumed == 0 && frames == 0) break;
                position += consumed;
            }
        }
    }

    [AacDecoderFact]
    public void StreamDecoderPlaysAdtsAtTheOutputRate()
    {
        var source = new FakeByteSource().Begin(LiveCodec.Aac).Add(Stereo).Add(Stereo);
        source.Complete();

        using var decoder = new LiveStreamDecoder(source, 48000, 2);

        var buffer = new float[8192];
        var total = 0L;
        var watch = Stopwatch.StartNew();

        while (watch.ElapsedMilliseconds < 5000)
        {
            var status = decoder.ReadLive(buffer, out var frames);
            total += frames;

            if (status == LiveReadStatus.Ended) break;
            if (status == LiveReadStatus.Waiting) Thread.Sleep(1);
        }

        Assert.Null(decoder.EndFailure);
        Assert.Equal(LiveCodec.Aac, decoder.Codec);

        // Cała końcówka: dekoder pracujący w tle oddaje ją przed końcem strumienia.
        var expected = 2 * StereoFrames * 48000L / 44100;
        Assert.InRange(total, expected - 4096, expected + 4096);
    }
}
