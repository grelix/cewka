using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// WavPack przez bibliotekę natywną. Pliki w <c>Dane</c> wytworzył oficjalny koder WavPack 5.9.0
/// z plików WAV budowanych tu w teście, więc dekodowanie ma oddać te same próbki co WAV.
/// </summary>
public sealed class WavPackDecoderTests : IDisposable
{
    private const string Stereo16 = "ton-440-stereo-44100.wv";
    private const string Stereo24 = "ton-440-stereo-44100-24bit.wv";
    private const string StereoFloat = "ton-440-stereo-44100-float.wv";
    private const string Quad = "ton-1000-4kanaly-fl-fr-fc-bc-44100.wv";

    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-wv-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static IAudioDecoder Open(string name, int rate = 44100, int channels = 2) =>
        DecoderFactory.Open(new MemoryStream(TestAudioFiles.DataFile(name)), AudioFileFormat.WavPack, true, rate, channels);

    private static IAudioDecoder OpenWav(byte[] wav, int rate = 44100, int channels = 2) =>
        DecoderFactory.Open(new MemoryStream(wav), AudioFileFormat.Wav, true, rate, channels);

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var result = new List<float>();
        var buffer = new float[1500 * decoder.Channels];
        int read;
        while ((read = decoder.Read(buffer)) > 0) result.AddRange(buffer.AsSpan(0, read * decoder.Channels));
        return result.ToArray();
    }

    /// <summary>Plik WAV, z którego koder zrobił dany plik WavPack.</summary>
    private static byte[] SourceWav(string name) => name switch
    {
        Stereo16 => TestAudioFiles.PcmWav(TestAudioFiles.Quantise(TestAudioFiles.WavPackTone(22050), 16), 2, 44100, 16),
        Stereo24 => TestAudioFiles.PcmWav(TestAudioFiles.Quantise(TestAudioFiles.WavPackTone(11025), 24), 2, 44100, 24),
        StereoFloat => TestAudioFiles.Wav(TestAudioFiles.WavPackTone(11025).Select(x => (float)x).ToArray(), 2, 44100),
        Quad => TestAudioFiles.PcmWav(
            TestAudioFiles.Quantise(TestAudioFiles.ToneInChannel(4, 3, 4800, 44100).Select(x => (double)x).ToArray(), 16),
            4, 44100, 16, 0x107),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData(Stereo16, 16, false, 22050)]
    [InlineData(Stereo24, 24, false, 11025)]
    [InlineData(StereoFloat, 32, true, 11025)]
    public void DecodesSampleForSampleLikeTheSourceWave(string name, int bits, bool isFloat, int frames)
    {
        using var decoder = Open(name);
        var wavPack = (WavPackDecoder)decoder;

        Assert.Equal(44100, decoder.SourceSampleRate);
        Assert.Equal(2, decoder.SourceChannels);
        Assert.Equal(bits, wavPack.BitsPerSample);
        Assert.Equal(isFloat, wavPack.IsFloat);
        Assert.Equal(frames, decoder.TotalFrames);

        var output = ReadAll(decoder);
        Assert.Equal(frames, decoder.Position);

        using var reference = OpenWav(SourceWav(name));
        Assert.Equal(ReadAll(reference), output);
    }

    [Fact]
    public void LengthAndPositionAreInOutputFrames()
    {
        using var decoder = Open(Stereo16, rate: 48000);
        var output = ReadAll(decoder);

        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(SincResampler.OutputFrames(22050, 44100, 48000), decoder.TotalFrames);
        Assert.Equal(decoder.TotalFrames, output.Length / 2);
        Assert.Equal(decoder.TotalFrames, decoder.Position);
    }

    [Fact]
    public void SeekLandsOnTheRequestedFrame()
    {
        float[] whole;
        using (var full = Open(Stereo16)) whole = ReadAll(full);

        using var decoder = Open(Stereo16);
        var buffer = new float[1000 * 2];

        decoder.Seek(10000);
        Assert.Equal(10000, decoder.Position);
        Assert.Equal(1000, decoder.Read(buffer));
        Assert.Equal(whole.AsSpan(10000 * 2, 2000).ToArray(), buffer);

        decoder.Seek(0);
        Assert.Equal(1000, decoder.Read(buffer));
        Assert.Equal(whole.AsSpan(0, 2000).ToArray(), buffer);
        Assert.Equal(1000, decoder.Position);
    }

    /// <summary>Cel za końcem stawia dekoder na końcu, a przewinięcie z powrotem dalej działa.</summary>
    [Fact]
    public void SeekBeyondTheEndStopsAtTheEndAndCanComeBack()
    {
        using var decoder = Open(Stereo16);
        var buffer = new float[1000 * 2];

        decoder.Seek(10 * 44100);
        Assert.Equal(22050, decoder.Position);
        Assert.Equal(0, decoder.Read(buffer));

        decoder.Seek(5000);
        Assert.Equal(1000, decoder.Read(buffer));
        Assert.Equal(6000, decoder.Position);
    }

    /// <summary>
    /// Maska z pliku decyduje o miksie: w układzie FL FR FC BC czwarty kanał to tył środkowy,
    /// słyszalny po obu stronach tak samo jak z pliku WAV z tą maską.
    /// </summary>
    [Fact]
    public void ChannelMaskFromTheFileDecidesTheDownmix()
    {
        using var decoder = Open(Quad);
        Assert.Equal(4, decoder.SourceChannels);

        var output = ReadAll(decoder);
        using var reference = OpenWav(SourceWav(Quad));
        Assert.Equal(ReadAll(reference), output);

        var left = Signals.Peak(output.Where((_, i) => i % 2 == 0).SelectMany(x => new[] { x, x }).ToArray());
        var right = Signals.Peak(output.Where((_, i) => i % 2 == 1).SelectMany(x => new[] { x, x }).ToArray());
        Assert.True(left > 0.1, $"lewy {left}");
        Assert.Equal(left, right, 4);
    }

    [Fact]
    public void FormatIsRecognisedByContentAndExtension()
    {
        var bytes = TestAudioFiles.DataFile(Stereo16);

        Assert.Equal(AudioFileFormat.WavPack, AudioFileFormatDetector.Detect(new MemoryStream(bytes)));
        Assert.Equal(AudioFileFormat.WavPack, AudioFileFormatDetector.Detect(new MemoryStream(bytes), "plik.flac"));
        Assert.Equal(AudioFileFormat.WavPack, AudioFileFormatDetector.Detect(new MemoryStream(new byte[64]), "plik.wv"));
        Assert.Contains(".wv", AudioFileFormatDetector.SupportedExtensions);
        Assert.DoesNotContain(".wvc", AudioFileFormatDetector.SupportedExtensions);
        Assert.True(DecoderFactory.IsSupported(AudioFileFormat.WavPack));

        // Zwykły WAV z rozszerzeniem .wav zostaje plikiem WAV.
        var wav = SourceWav(Stereo16);
        Assert.Equal(AudioFileFormat.Wav, AudioFileFormatDetector.Detect(new MemoryStream(wav), "plik.wav"));
    }

    [Fact]
    public void PathWithPolishCharactersOpens()
    {
        var path = Path.Combine(_directory, "Zażółć gęślą jaźń.wv");
        File.WriteAllBytes(path, TestAudioFiles.DataFile(Stereo16));

        using var decoder = DecoderFactory.Open(path, 44100, 2);
        Assert.IsType<WavPackDecoder>(decoder);
        Assert.Equal(22050 * 2, ReadAll(decoder).Length);
    }

    /// <summary>
    /// Biblioteka jest zbudowana bez DSD: taki plik dostaje opis formatu nieobsługiwanego.
    /// Plik powstał tym samym koderem z krótkiego pliku DSF z ciszą DSD.
    /// </summary>
    [Fact]
    public void DsdFileIsUnsupported()
    {
        var ex = Assert.Throws<AudioException>(() => Open("cisza-dsd-stereo.wv"));
        Assert.Equal(DecoderFailure.Unspecified, ex.Reason);
        Assert.Equal(DecoderFailures.Describe(DecoderFailure.Unspecified), ex.Message);
    }

    /// <summary>
    /// WavPack sprzed wersji 4 zaczyna się nagłówkiem RIFF. Z rozszerzeniem .wv trafia do WavPack
    /// i dostaje opis formatu nieobsługiwanego, zamiast grać dane skompresowane jako szum WAV.
    /// </summary>
    [Fact]
    public void FileOlderThanVersionFourIsUnsupported()
    {
        var header = SourceWav(Stereo16)[..44];
        var legacy = TestAudioFiles.Concat(header, "wvpk"u8.ToArray(), BitConverter.GetBytes(0x0003), new byte[400]);

        var path = Path.Combine(_directory, "stary.wv");
        File.WriteAllBytes(path, legacy);

        Assert.Equal(AudioFileFormat.WavPack, AudioFileFormatDetector.Detect(path));
        var ex = Assert.Throws<AudioException>(() => DecoderFactory.Open(path, 44100, 2));
        Assert.Equal(DecoderFailure.Unspecified, ex.Reason);
    }

    /// <summary>Plik 33-kanałowy przekracza granicę biblioteki: bufor pośredni rośnie z liczbą kanałów.</summary>
    [Fact]
    public void TooManyChannelsAreRejectedBeforeDecoding()
    {
        var ex = Assert.Throws<AudioException>(() => Open("cisza-33-kanaly-44100.wv"));
        Assert.Equal(DecoderFailure.UnsupportedChannelLayout, ex.Reason);
    }

    /// <summary>
    /// Plik hybrydowy gra część stratną, także gdy obok leży plik korekcyjny: dekoder dostaje tylko
    /// strumień pliku .wv. Wynik różni się więc od źródłowego WAV, ale jest blisko niego.
    /// </summary>
    [Fact]
    public void CorrectionFileIsIgnored()
    {
        var alone = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "sam")).FullName, "hybryda.wv");
        var paired = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "z-korekcja")).FullName, "hybryda.wv");
        File.WriteAllBytes(alone, TestAudioFiles.DataFile("ton-440-stereo-44100-hybryda.wv"));
        File.WriteAllBytes(paired, TestAudioFiles.DataFile("ton-440-stereo-44100-hybryda.wv"));
        File.WriteAllBytes(Path.ChangeExtension(paired, ".wvc"), TestAudioFiles.DataFile("ton-440-stereo-44100-hybryda.wvc"));

        float[] lossy, withCorrection, lossless;
        using (var decoder = DecoderFactory.Open(alone, 44100, 2)) lossy = ReadAll(decoder);
        using (var decoder = DecoderFactory.Open(paired, 44100, 2)) withCorrection = ReadAll(decoder);
        using (var decoder = OpenWav(SourceWav(Stereo16))) lossless = ReadAll(decoder);

        Assert.Equal(lossy, withCorrection);
        Assert.Equal(lossless.Length, lossy.Length);
        Assert.NotEqual(lossless, lossy);

        var error = lossy.Zip(lossless, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(error < 0.05, $"błąd części stratnej {error}");
    }

    [Fact]
    public void TagsAndBadgeComeFromTheFile()
    {
        var path = Path.Combine(_directory, "znaczniki.wv");
        File.WriteAllBytes(path, TestAudioFiles.Tagged("wv"));

        var metadata = MetadataReader.Read(path);

        Assert.Equal(TestAudioFiles.TaggedTitle, metadata.Title);
        Assert.Equal("Wykonawca", metadata.Artist);
        Assert.Equal("Album", metadata.Album);
        Assert.Equal(3, metadata.TrackNumber);
        Assert.Equal(-6.48, metadata.ReplayGainTrack!.Value, 2);
        Assert.Equal(TestAudioFiles.TaggedPictureBytes, MetadataReader.ReadCover(path)?.Length);

        Assert.Equal("WavPack", metadata.Codec);
        Assert.Equal(16, metadata.BitDepth);
        Assert.Equal(44100, metadata.SampleRate);
        Assert.Matches(@"^WavPack 16 bit / \d+ kbps / 44\.1 kHz$", metadata.BuildBadge(null));

        // Znaczniki APEv2 na końcu nie zmieniają dźwięku.
        using var decoder = DecoderFactory.Open(path, 44100, 2);
        using var plain = Open(Stereo16);
        Assert.Equal(ReadAll(plain), ReadAll(decoder));
    }
}
