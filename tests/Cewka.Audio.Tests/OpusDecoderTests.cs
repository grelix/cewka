using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Opus w kontenerze Ogg, na plikach złożonych w teście koderem Concentus.</summary>
public class OpusDecoderTests
{
    private static IAudioDecoder Open(byte[] bytes, int rate = 48000, int channels = 2) =>
        DecoderFactory.Open(new MemoryStream(bytes), AudioFileFormat.Opus, ownsStream: true, rate, channels);

    private static long ReadAll(IAudioDecoder decoder, List<float>? collected = null)
    {
        var buffer = new float[4096 * decoder.Channels];
        long frames = 0;
        int read;

        while ((read = decoder.Read(buffer)) > 0)
        {
            frames += read;
            collected?.AddRange(buffer.AsSpan(0, read * decoder.Channels).ToArray());
        }

        return frames;
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(16000)]
    [InlineData(8000)]
    public void SeekToOrPastTheEndDoesNotThrow(int rate)
    {
        using var decoder = Open(TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 37 }), rate);

        decoder.Seek(decoder.TotalFrames);
        ReadAll(decoder);

        decoder.Seek(decoder.TotalFrames + rate);
        ReadAll(decoder);

        // Po przewinięciu poza koniec dekoder nadal wraca na początek.
        decoder.Seek(0);
        Assert.True(ReadAll(decoder) > 0);
    }

    [Fact]
    public void PreSkipIsDroppedFromTheStart()
    {
        // 50 ramek po 960 próbek, z czego pierwsze 312 to opóźnienie kodera.
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50, PreSkip = 312 });

        using var decoder = Open(bytes);

        Assert.Equal(48000 - 312, decoder.TotalFrames);
        Assert.Equal(48000 - 312, ReadAll(decoder));

        decoder.Seek(0);
        Assert.Equal(48000 - 312, ReadAll(decoder));
    }

    [Fact]
    public void OutputGainFromTheHeaderIsApplied()
    {
        static double Rms(byte[] bytes)
        {
            using var decoder = Open(bytes);
            var samples = new List<float>();
            ReadAll(decoder, samples);
            return Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Count);
        }

        var plain = Rms(TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50 }));
        var quieter = Rms(TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50, OutputGainQ8 = -6 * 256 }));

        Assert.Equal(Math.Pow(10, -6 / 20.0), quieter / plain, 2);
    }

    [Fact]
    public void SurroundOpusPlaysWithCentreOnBothSides()
    {
        // Rodzina mapowania 1, kolejność Vorbis: L, C, R, RL, RR, LFE; ton tylko w środku.
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Channels = 6, Frames = 50, ToneChannel = 1 });

        using var decoder = Open(bytes);
        var samples = new List<float>();
        ReadAll(decoder, samples);

        double left = 0, right = 0;
        for (var i = 0; i + 1 < samples.Count; i += 2)
        {
            left += samples[i] * samples[i];
            right += samples[i + 1] * samples[i + 1];
        }

        Assert.Equal(6, decoder.SourceChannels);
        Assert.True(left > 1, $"lewy {left}");
        Assert.Equal(1.0, left / right, 2);
    }

    [Fact]
    public void UnknownMappingFamilyIsReportedAsUnsupportedLayout()
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Channels = 6, Frames = 5, MappingFamilyOverride = 255 });

        var ex = Assert.Throws<AudioException>(() => Open(bytes));
        Assert.Equal(DecoderFailure.UnsupportedChannelLayout, ex.Reason);
    }
}
