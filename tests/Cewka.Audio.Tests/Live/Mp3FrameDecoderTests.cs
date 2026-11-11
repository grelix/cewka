using Cewka.Audio.Decoding;
using Cewka.Audio.Decoding.Live;
using Xunit;

namespace Cewka.Audio.Tests.Live;

public sealed class Mp3FrameDecoderTests
{
    [Fact]
    public void ZeroPayloadFrameGivesSilenceAndItsFormat()
    {
        using var decoder = new Mp3FrameDecoder();
        var output = new float[decoder.MaxOutputSamples];

        var frames = decoder.Decode(TestAudioFiles.Mp3Frames(20), output, out var consumed, out var format);

        Assert.Equal(1152, frames);
        Assert.Equal(417, consumed);
        Assert.Equal(new LiveFrameFormat(44100, 2, 128), format);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(44100, 2, 417)]
    [InlineData(48000, 2, 384)]
    [InlineData(48000, 1, 384)]
    public void NoiseFramesGiveAudibleSoundInTheFormatOfTheFrame(int rate, int channels, int length)
    {
        using var decoder = new Mp3FrameDecoder();
        var output = new float[decoder.MaxOutputSamples];

        var frames = decoder.Decode(TestAudioFiles.Mp3NoiseFrames(20, 7, rate, channels), output, out var consumed, out var format);

        Assert.Equal(1152, frames);
        Assert.Equal(length, consumed);
        Assert.Equal(new LiveFrameFormat(rate, channels, 128), format);
        Assert.Contains(output.AsSpan(0, frames * channels).ToArray(), sample => sample != 0f);
    }

    /// <summary>Plikowa droga przez dr_mp3 i dekoder ramek muszą brzmieć identycznie.</summary>
    [Fact]
    public void TwoHundredNoiseFramesMatchTheFileDecoderSampleForSample()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(200, 11);

        using var reference = new MiniaudioDecoder(new MemoryStream(data), true, 44100, 2);
        var expected = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = reference.Read(buffer)) > 0) expected.AddRange(buffer.AsSpan(0, read * 2).ToArray());

        var actual = DecodeAll(data);

        Assert.Contains(expected, sample => sample != 0f);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }

    /// <summary>Stąd reguła najmniejszej porcji: krótka porcja znika bez śladu.</summary>
    [Fact]
    public void ShortPortionIsEatenAsGarbage()
    {
        var data = TestAudioFiles.Mp3NoiseFrames(4, 3);
        var output = new float[2304];

        using var decoder = new Mp3FrameDecoder();

        Assert.Equal(0, decoder.Decode(data.AsSpan(0, 418), output, out var consumed, out _));
        Assert.Equal(418, consumed);

        decoder.Reset();
        Assert.Equal(1152, decoder.Decode(data.AsSpan(0, 421), output, out consumed, out _));
        Assert.Equal(417, consumed);
    }

    [Fact]
    public void EmptyInputConsumesNothing()
    {
        using var decoder = new Mp3FrameDecoder();

        Assert.Equal(0, decoder.Decode(ReadOnlySpan<byte>.Empty, new float[2304], out var consumed, out _));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TooSmallOutputIsRefusedBeforeDecoding()
    {
        using var decoder = new Mp3FrameDecoder();

        Assert.Throws<AudioException>(() =>
            decoder.Decode(TestAudioFiles.Mp3Frames(20), new float[2303], out _, out _));
    }

    /// <summary>Po Reset dekoder nie pamięta poprzedniego sygnału: ramki brzmią jak z nowego dekodera.</summary>
    [Fact]
    public void ResetForgetsThePreviousSignal()
    {
        var first = TestAudioFiles.Mp3NoiseFrames(30, 21, 48000);
        var second = TestAudioFiles.Mp3NoiseFrames(30, 22, 48000);
        var expected = DecodeAll(second);

        using var decoder = new Mp3FrameDecoder();
        var output = new float[2304];
        Assert.Equal(1152, decoder.Decode(first, output, out _, out _));

        decoder.Reset();
        Assert.Equal(1152, decoder.Decode(second, output, out _, out _));

        Assert.Equal(expected.Take(2304), output);
    }

    private static List<float> DecodeAll(byte[] data)
    {
        using var decoder = new Mp3FrameDecoder();

        var all = new List<float>();
        var output = new float[decoder.MaxOutputSamples];
        var offset = 0;

        while (offset < data.Length)
        {
            var frames = decoder.Decode(data.AsSpan(offset), output, out var consumed, out var format);
            Assert.True(consumed > 0);
            offset += consumed;
            all.AddRange(output.AsSpan(0, frames * format.Channels).ToArray());
        }

        return all;
    }
}
