using Cewka.Audio.Decoding.Live;
using Xunit;

namespace Cewka.Audio.Tests.Live;

/// <summary>Dane z sieci są niezaufane: kod natywny nie może wyjść poza bufor ani zwrócić bzdur.</summary>
public sealed class NativeFuzzTests
{
    private const int Seed = 20261104;
    private const int Rounds = 2000;
    private const int Guard = 64;

    [Fact]
    public void RandomAndDamagedInputStaysWithinTheBuffers()
    {
        var random = new Random(Seed);
        using var decoder = new Mp3FrameDecoder();

        var output = new float[decoder.MaxOutputSamples + Guard];
        var limit = output.AsMemory(0, decoder.MaxOutputSamples);

        for (var round = 0; round < Rounds; round++)
        {
            var input = Build(random, round);
            if (random.Next(4) == 0) decoder.Reset();

            output.AsSpan(decoder.MaxOutputSamples).Fill(float.NaN);

            var offset = 0;
            while (offset < input.Length)
            {
                var frames = decoder.Decode(input.AsSpan(offset), limit.Span, out var consumed, out var format);

                Assert.InRange(consumed, 0, input.Length - offset);
                Assert.InRange(frames, 0, 1152);

                if (frames > 0)
                {
                    Assert.InRange(format.Channels, 1, 2);
                    Assert.InRange(format.SampleRate, 8000, 48000);
                    Assert.True(frames * format.Channels <= decoder.MaxOutputSamples);

                    for (var i = 0; i < frames * format.Channels; i++)
                        Assert.InRange(output[i], -1f, 1f);
                }

                for (var i = decoder.MaxOutputSamples; i < output.Length; i++)
                    Assert.True(float.IsNaN(output[i]), $"runda {round}: zapis za buforem");

                if (consumed == 0) break;
                offset += consumed;
            }
        }
    }

    private static byte[] Build(Random random, int round) => (round % 4) switch
    {
        0 => RandomBytes(random),
        1 => WithHeaders(random),
        2 => Mutated(random),
        _ => Truncated(random),
    };

    private static byte[] RandomBytes(Random random)
    {
        var data = new byte[random.Next(0, 12000)];
        random.NextBytes(data);
        return data;
    }

    /// <summary>Losowe nagłówki w losowych miejscach: każda warstwa, częstotliwość i tryb, też wolny format.</summary>
    private static byte[] WithHeaders(Random random)
    {
        var data = RandomBytes(random);

        for (var i = random.Next(1, 40); i > 0 && data.Length > 4; i--)
        {
            var at = random.Next(data.Length - 4);
            data[at] = 0xFF;
            data[at + 1] = (byte)(0xE0 | random.Next(32));
            data[at + 2] = (byte)random.Next(256);
            data[at + 3] = (byte)random.Next(256);
        }

        return data;
    }

    private static byte[] Mutated(Random random)
    {
        var data = Noise(random);

        for (var i = random.Next(1, 200); i > 0; i--)
            data[random.Next(data.Length)] = (byte)random.Next(256);

        return data;
    }

    private static byte[] Truncated(Random random)
    {
        var data = Noise(random);
        return data.AsSpan(random.Next(data.Length / 2), random.Next(1, data.Length / 2)).ToArray();
    }

    private static byte[] Noise(Random random)
    {
        var (rate, channels) = random.Next(3) switch { 0 => (44100, 2), 1 => (48000, 2), _ => (48000, 1) };
        return TestAudioFiles.Mp3NoiseFrames(random.Next(4, 30), random.Next(), rate, channels);
    }
}
