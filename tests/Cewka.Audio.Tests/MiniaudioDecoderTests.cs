using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// MP3, FLAC i WAV przechodzą przez tę samą konwersję co pozostałe dekodery. Długość, pozycja
/// i przewijanie liczone są w ramkach wyjściowych, a kanały plików wielokanałowych trafiają
/// na swoje miejsca.
/// </summary>
public sealed class MiniaudioDecoderTests
{
    private static MiniaudioDecoder Open(float[] data, int channels, int rate, int targetRate = 48000, int targetChannels = 2,
        uint? mask = null) =>
        new(new MemoryStream(TestAudioFiles.Wav(data, channels, rate, mask)), true, targetRate, targetChannels);

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var result = new List<float>();
        var buffer = new float[1500 * decoder.Channels];
        int read;
        while ((read = decoder.Read(buffer)) > 0) result.AddRange(buffer.AsSpan(0, read * decoder.Channels));
        return result.ToArray();
    }

    /// <summary>Dwie sekundy: pierwsza z tonem 1 kHz, druga z tonem 3 kHz.</summary>
    private static float[] TwoTones(int rate)
    {
        var data = new float[2 * rate * 2];
        for (var i = 0; i < 2 * rate; i++)
        {
            var frequency = i < rate ? 1000 : 3000;
            var value = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate));
            data[2 * i] = value;
            data[2 * i + 1] = value;
        }

        return data;
    }

    [Fact]
    public void ResamplesLikeEveryOtherDecoder()
    {
        var data = new float[30001 * 2];
        var random = new Random(9);
        for (var i = 0; i < data.Length; i++) data[i] = (float)(random.NextDouble() - 0.5);

        using var decoder = Open(data, 2, 44100);
        var output = ReadAll(decoder);

        Assert.Equal(44100, decoder.SourceSampleRate);
        Assert.Equal(SincResampler.OutputFrames(30001, 44100, 48000), decoder.TotalFrames);
        Assert.Equal(decoder.TotalFrames, output.Length / 2);
        Assert.Equal(decoder.TotalFrames, decoder.Position);

        var reference = SincResamplerTests.Run(new SincResampler(2, 44100, 48000, decoder.ResamplingLevel), data, 2, 4096);
        Assert.Equal(reference, output);
    }

    [Fact]
    public void SameRateIsCopiedSampleForSample()
    {
        var data = TwoTones(48000);
        using var decoder = Open(data, 2, 48000);

        Assert.Equal(96000, decoder.TotalFrames);
        Assert.Equal(data, ReadAll(decoder));
    }

    /// <summary>
    /// Przewijanie w ramkach wyjściowych: plik 96 kHz przewinięty do 0,75 s na wyjściu 48 kHz gra
    /// jeszcze pierwszą sekundę. Ramki źródłowe zawiodłyby go dwa razy za daleko.
    /// </summary>
    [Theory]
    [InlineData(96000)]
    [InlineData(44100)]
    public void SeekIsCountedInOutputFrames(int rate)
    {
        using var decoder = Open(TwoTones(rate), 2, rate);

        decoder.Seek(36000);
        Assert.Equal(36000, decoder.Position);

        var after = new float[4800 * 2];
        Assert.Equal(4800, decoder.Read(after));

        Assert.True(Signals.Goertzel(after, 0, 1000, 48000, 200) > 0.4);
        Assert.True(Signals.Goertzel(after, 0, 3000, 48000, 200) < 0.01);

        decoder.Seek(72000);
        decoder.Read(after);
        Assert.True(Signals.Goertzel(after, 0, 3000, 48000, 200) > 0.4);
        Assert.Equal(76800, decoder.Position);
    }

    /// <summary>
    /// Kanały boczne z maski 0x60F na urządzeniu 5.1 trafiają tam, gdzie urządzenie ma otoczenie,
    /// a pozostałe kanały milczą.
    /// </summary>
    [Fact]
    public void SurroundChannelsKeepTheirPlaceOnAnEqualDevice()
    {
        for (var channel = 0; channel < 6; channel++)
        {
            var data = TestAudioFiles.ToneInChannel(6, channel, 4800, 44100);
            using var decoder = Open(data, 6, 44100, targetChannels: 6, mask: 0x60F);
            var output = ReadAll(decoder);

            for (var c = 0; c < 6; c++)
            {
                var level = Signals.Peak(Pick(output, 6, c));
                if (c == channel) Assert.True(level > 0.4, $"kanał {channel}: {level}");
                else Assert.True(level < 1e-6, $"kanał {channel} słychać w {c}: {level}");
            }
        }
    }

    /// <summary>
    /// Maska z pliku decyduje o miksie: w układzie FL FR FC BC czwarty kanał to tył środkowy,
    /// słyszalny po obu stronach, a nie prawy tylny z kolejności domyślnej.
    /// </summary>
    [Fact]
    public void ChannelMaskFromTheFileDecidesTheDownmix()
    {
        var data = TestAudioFiles.ToneInChannel(4, 3, 4800, 44100);

        using var masked = Open(data, 4, 44100, mask: 0x107);
        var output = ReadAll(masked);
        var left = Signals.Peak(Pick(output, 2, 0));
        var right = Signals.Peak(Pick(output, 2, 1));

        Assert.True(left > 0.1);
        Assert.Equal(left, right, 4);

        using var plain = Open(data, 4, 44100);
        var defaultOutput = ReadAll(plain);
        Assert.True(Signals.Peak(Pick(defaultOutput, 2, 0)) < 1e-6);
    }

    [Fact]
    public void WaveCentreAndLfeAreMixedToStereo()
    {
        var centre = ReadAll(Open(TestAudioFiles.ToneInChannel(6, 2, 4800, 48000), 6, 48000));
        Assert.True(Signals.Peak(Pick(centre, 2, 0)) > 0.1);
        Assert.Equal(Signals.Peak(Pick(centre, 2, 0)), Signals.Peak(Pick(centre, 2, 1)), 6);

        var lfe = ReadAll(Open(TestAudioFiles.ToneInChannel(6, 3, 4800, 48000), 6, 48000));
        Assert.Equal(0, Signals.Peak(lfe));
    }

    [Fact]
    public void LevelComesFromTheOpeningThread()
    {
        var data = TwoTones(44100);

        MiniaudioDecoder decoder;
        using (AudioQuality.UseInThisThread(ResamplingLevel.High)) decoder = Open(data, 2, 44100);

        using (decoder) Assert.Equal(ResamplingLevel.High, decoder.ResamplingLevel);
    }

    /// <summary>Jeden kanał z przeplotu, w układzie dwukanałowym dla pomocników sygnału.</summary>
    private static float[] Pick(float[] interleaved, int channels, int channel)
    {
        var frames = interleaved.Length / channels;
        var result = new float[frames * 2];
        for (var i = 0; i < frames; i++) result[2 * i] = result[2 * i + 1] = interleaved[i * channels + channel];
        return result;
    }
}
