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

    /// <summary>
    /// Ostatni pakiet jest dopełniony do pełnej ramki, a granula ostatniej strony mówi, ile próbek
    /// jest prawdziwych (RFC 7845, rozdział 4.4). Dopełnienie nie może trafić do dźwięku: między
    /// utworami granymi bez przerwy dawałoby do 20 ms obcego sygnału.
    /// </summary>
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void EndPaddingIsTrimmed(int rate)
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50, PreSkip = 312, EndTrim = 400 });

        using var decoder = Open(bytes, rate);
        var expected = (long)Math.Round((48000 - 312 - 400) * (double)rate / 48000);

        Assert.Equal(expected, decoder.TotalFrames);
        Assert.InRange(ReadAll(decoder), expected - 2, expected + 2);

        decoder.Seek(0);
        Assert.InRange(ReadAll(decoder), expected - 2, expected + 2);

        // Po przewinięciu w środek koniec wypada w tym samym miejscu, z dokładnością do pakietu,
        // od którego czytnik zaczyna po przewinięciu.
        decoder.Seek(decoder.TotalFrames / 2);
        var rest = ReadAll(decoder);
        Assert.InRange(rest, expected / 2 - 2, expected / 2 + 3 * 960);
    }

    /// <summary>
    /// Prawdziwe pliki mają na stronie wiele pakietów. Obcięcie dotyczy wtedy tylko końca ostatniego
    /// pakietu ostatniej strony, a wcześniejsze strony i pakiety zostają całe.
    /// </summary>
    [Theory]
    [InlineData(10, 400)]
    [InlineData(10, 0)]
    [InlineData(7, 959)]
    [InlineData(50, 400)]
    [InlineData(3, 1500)]
    public void EndPaddingIsTrimmedWithManyPacketsPerPage(int packetsPerPage, int endTrim)
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions
        {
            Frames = 50, PreSkip = 312, EndTrim = endTrim, PacketsPerPage = packetsPerPage,
        });

        using var decoder = Open(bytes);
        var expected = 48000 - 312 - endTrim;

        Assert.Equal(expected, decoder.TotalFrames);
        Assert.Equal(expected, ReadAll(decoder));

        decoder.Seek(0);
        Assert.Equal(expected, ReadAll(decoder));
    }

    /// <summary>
    /// Przewinięcie trafia dokładnie w cel, a koniec wypada tam, gdzie powinien. Czytnik Ogg staje
    /// przed celem, nawet o całą stronę, więc bez wyrównania dźwięk zaczynałby się do sekundy za
    /// wcześnie, a pozycja na ekranie wyprzedzałaby go o tyle samo.
    /// </summary>
    [Theory]
    [InlineData(10, 400, 12 * 960)]
    [InlineData(10, 400, 12 * 960 + 333)]
    [InlineData(10, 400, 25 * 960 + 1)]
    [InlineData(10, 400, 3 * 960)]
    [InlineData(10, 400, 45 * 960 + 100)]
    [InlineData(10, 400, 49 * 960)]
    [InlineData(1, 400, 30 * 960 + 77)]
    [InlineData(50, 0, 40 * 960)]
    [InlineData(50, 400, 40 * 960)]
    [InlineData(50, 400, 100)]
    [InlineData(10, 400, 5)]
    [InlineData(7, 959, 20 * 960 + 5)]
    public void SeekLandsExactlyOnTheTarget(int packetsPerPage, int endTrim, int target)
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions
        {
            Frames = 50, PreSkip = 312, EndTrim = endTrim, PacketsPerPage = packetsPerPage,
        });

        using var decoder = Open(bytes);
        var total = 48000 - 312 - endTrim;

        decoder.Seek(target);

        Assert.Equal(total - target, ReadAll(decoder));
    }

    /// <summary>Długość pakietu z bajtu TOC zgadza się z tym, co oddaje dekoder.</summary>
    [Theory]
    [InlineData(new byte[] { 0xF8 }, 960)]        // CELT, 20 ms, jedna ramka
    [InlineData(new byte[] { 0xF9 }, 1920)]       // dwie ramki
    [InlineData(new byte[] { 0x80 }, 120)]        // CELT, 2,5 ms
    [InlineData(new byte[] { 0x08 }, 960)]        // SILK, 20 ms
    [InlineData(new byte[] { 0x18 }, 2880)]       // SILK, 60 ms
    [InlineData(new byte[] { 0x60 }, 480)]        // hybrydowy, 10 ms
    [InlineData(new byte[] { 0xFB, 0x03 }, 2880)] // trzy ramki po 20 ms
    [InlineData(new byte[] { }, 0)]
    public void PacketLengthComesFromTheTocByte(byte[] packet, int samples)
    {
        Assert.Equal(samples, Cewka.Audio.Decoding.OpusDecoder.PacketSamples(packet));
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

    /// <summary>
    /// Granula ostatniej strony to liczba z pliku. Niedorzeczna nie może stać się długością utworu.
    /// Czytnik nie umie w takim pliku przewijać, więc przewinięcie kończy utwór, ale bez wyjątku;
    /// od początku plik gra dalej.
    /// </summary>
    [Theory]
    [InlineData(1L << 60)]
    [InlineData(long.MaxValue)]
    [InlineData(-5L)]
    public void NiedorzecznaGranulaKoncaDajeDlugoscNieznana(long granule)
    {
        var bytes = TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 50, PacketsPerPage = 10 });

        // Granula leży w bajtach 6..13 nagłówka strony; ostatnia strona zaczyna się na ostatnim „OggS”.
        var last = bytes.AsSpan().LastIndexOf("OggS"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(last + 6), granule);
        TestAudioFiles.OggWriter.FixChecksums(bytes);

        using var decoder = Open(bytes);

        Assert.Equal(0, decoder.TotalFrames);

        Assert.True(ReadAll(decoder) > 0);

        decoder.Seek(24000);
        Assert.True(ReadAll(decoder) >= 0);

        decoder.Seek(0);
        Assert.True(ReadAll(decoder) > 0);
    }
}
