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
}
