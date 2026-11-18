using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>AIFF i AIFF-C: rodzaje zapisu próbek, nagłówek, przewijanie, długość i znaczniki.</summary>
public sealed class AiffDecoderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-aiff-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static float[] Ramp(int frames, int channels)
    {
        var data = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < channels; c++)
            data[i * channels + c] = (float)(0.9 * Math.Sin(i * 0.013 + c * 1.7));
        return data;
    }

    private static float[] DecodeAll(IAudioDecoder decoder)
    {
        var result = new List<float>();
        var buffer = new float[1000 * decoder.Channels];
        int frames;
        while ((frames = decoder.Read(buffer)) > 0) result.AddRange(buffer.AsSpan(0, frames * decoder.Channels).ToArray());
        return [.. result];
    }

    private static IAudioDecoder Open(byte[] file, int rate = 44100, int channels = 2) =>
        DecoderFactory.Open(new MemoryStream(file), AudioFileFormat.Aiff, ownsStream: true, rate, channels);

    [Theory]
    [InlineData(null, 8, 1.0 / 127)]
    [InlineData(null, 16, 1.0 / 32767)]
    [InlineData(null, 20, 1.0 / 524287)]
    [InlineData(null, 24, 1.0 / 8388607)]
    [InlineData(null, 32, 1e-6)]
    [InlineData("NONE", 16, 1.0 / 32767)]
    [InlineData("twos", 24, 1.0 / 8388607)]
    [InlineData("sowt", 16, 1.0 / 32767)]
    [InlineData("sowt", 24, 1.0 / 8388607)]
    [InlineData("in24", 24, 1.0 / 8388607)]
    [InlineData("in32", 32, 1e-6)]
    [InlineData("fl32", 32, 1e-7)]
    [InlineData("FL32", 32, 1e-7)]
    [InlineData("fl64", 64, 1e-7)]
    [InlineData("FL64", 64, 1e-7)]
    public void ProbkiWracajaTakieJakZapisane(string? compression, int bits, double step)
    {
        var source = Ramp(3000, 2);
        using var decoder = Open(TestAudioFiles.Aiff(source, 2, 44100, bits, compression));

        Assert.Equal(44100, decoder.SourceSampleRate);
        Assert.Equal(2, decoder.SourceChannels);
        Assert.Equal(3000, decoder.TotalFrames);

        var decoded = DecodeAll(decoder);
        Assert.Equal(source.Length, decoded.Length);

        for (var i = 0; i < source.Length; i++)
            Assert.True(Math.Abs(source[i] - decoded[i]) <= step * 1.01, $"próbka {i}: {decoded[i]} zamiast {source[i]}");
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void CzestotliwoscZLiczbyRozszerzonej(int rate)
    {
        Assert.Equal(rate, AiffDecoder.ReadExtended(TestAudioFiles.Extended(rate)));

        using var decoder = Open(TestAudioFiles.Aiff(Ramp(rate / 10, 1), 1, rate), 48000, 2);
        Assert.Equal(rate, decoder.SourceSampleRate);
        Assert.Equal(Dsp.SincResampler.OutputFrames(rate / 10, rate, 48000), decoder.TotalFrames);
        Assert.InRange(DecodeAll(decoder).Length / 2, decoder.TotalFrames - 1, decoder.TotalFrames + 1);
    }

    [Fact]
    public void PrzewinieciePodajeTeSameProbkiCoOdczytOdPoczatku()
    {
        var source = Ramp(20000, 2);
        var file = TestAudioFiles.Aiff(source, 2, 44100, 24);

        float[] whole;
        using (var decoder = Open(file)) whole = DecodeAll(decoder);

        using var seeking = Open(file);
        seeking.Seek(12345);
        Assert.Equal(12345, seeking.Position);

        var rest = DecodeAll(seeking);
        Assert.Equal(whole.AsSpan(12345 * 2).ToArray(), rest);

        seeking.Seek(500_000);
        Assert.Empty(DecodeAll(seeking));
    }

    [Fact]
    public void FragmentyWDowolnejKolejnosciIZNieparzystaDlugoscia()
    {
        var source = Ramp(1000, 2);
        var odd = TestAudioFiles.AiffChunk("NAME", [(byte)'a', (byte)'b', (byte)'c']);
        var file = TestAudioFiles.Aiff(source, 2, 44100, 16, before: odd, after: odd, formatAfterData: true);

        using var decoder = Open(file);
        Assert.Equal(1000, decoder.TotalFrames);
        Assert.Equal(2000, DecodeAll(decoder).Length);
    }

    [Fact]
    public void UrwanyPlikGraTyleIleMa()
    {
        var file = TestAudioFiles.Aiff(Ramp(4000, 2), 2, 44100);
        var cut = file[..(file.Length - 1000 * 4)]; // 1000 ramek po dwa kanały po dwa bajty

        using var decoder = Open(cut);
        Assert.Equal(3000, decoder.TotalFrames);
        Assert.Equal(6000, DecodeAll(decoder).Length);
    }

    [Fact]
    public void MonoGraWObuKanalach()
    {
        using var decoder = Open(TestAudioFiles.Aiff(Ramp(100, 1), 1, 44100));
        var decoded = DecodeAll(decoder);

        Assert.Equal(200, decoded.Length);
        Assert.True(decoded.Where((_, i) => i % 2 == 0).SequenceEqual(decoded.Where((_, i) => i % 2 == 1)));
    }

    [Fact]
    public void RozpoznanieFormatuPoZawartosciIRozszerzeniu()
    {
        var aiff = TestAudioFiles.Aiff(Ramp(10, 2), 2, 44100);
        var aifc = TestAudioFiles.Aiff(Ramp(10, 2), 2, 44100, compression: "sowt");

        Assert.Equal(AudioFileFormat.Aiff, AudioFileFormatDetector.Detect(new MemoryStream(aiff), "plik.mp3"));
        Assert.Equal(AudioFileFormat.Aiff, AudioFileFormatDetector.Detect(new MemoryStream(aifc), "plik"));
        Assert.Equal(AudioFileFormat.Aiff, AudioFileFormatDetector.Detect(new MemoryStream([1, 2]), "utwor.aif"));
        Assert.Equal(AudioFileFormat.Aiff, AudioFileFormatDetector.Detect(new MemoryStream([1, 2]), "utwor.AIFF"));
        Assert.Equal(AudioFileFormat.Aiff, AudioFileFormatDetector.Detect(new MemoryStream([1, 2]), "utwor.aifc"));

        foreach (var extension in new[] { ".aiff", ".aif", ".aifc" })
            Assert.Contains(extension, AudioFileFormatDetector.SupportedExtensions);
    }

    [Theory]
    [InlineData("ulaw")]
    [InlineData("alaw")]
    [InlineData("ima4")]
    public void InnaKompresjaToFormatNieobslugiwany(string compression)
    {
        var file = TestAudioFiles.Aiff(Ramp(10, 2), 2, 44100, compression: compression);

        Assert.Equal(AudioFileFormat.Unknown, AudioFileFormatDetector.Detect(new MemoryStream(file), "plik.aifc"));
        Assert.False(DecoderFactory.IsSupported(AudioFileFormatDetector.Detect(new MemoryStream(file), "plik.aifc")));

        var failure = Assert.Throws<AudioException>(() => Open(file));
        Assert.Equal(DecoderFailure.UnrecognisedFormat, failure.Reason);
    }

    [Fact]
    public void ZepsutyNaglowekToPlikUszkodzony()
    {
        var file = TestAudioFiles.Aiff(Ramp(10, 2), 2, 44100);
        file[20] = 0x7F; // starszy bajt liczby kanałów w opisie formatu

        var failure = Assert.Throws<AudioException>(() => Open(file));
        Assert.Equal(DecoderFailure.DamagedFile, failure.Reason);
    }

    [Fact]
    public void ZnacznikiWeFragmencieId3IPlakietkaZGlebia()
    {
        var path = Path.Combine(_directory, "utwor.aiff");
        File.WriteAllBytes(path, TestAudioFiles.Tagged("aiff"));

        var metadata = MetadataReader.Read(path);

        Assert.Equal(TestAudioFiles.TaggedTitle, metadata.Title);
        Assert.Equal("Wykonawca", metadata.Artist);
        Assert.Equal(3, metadata.TrackNumber);
        Assert.Equal(-6.48, metadata.ReplayGainTrack!.Value, 2);
        Assert.Equal(44100, metadata.SampleRate);
        Assert.Equal(16, metadata.BitDepth);
        Assert.Equal("AIFF 16 bit / 1411 kbps / 44.1 kHz", metadata.FormatBadge);
        Assert.NotNull(MetadataReader.ReadCover(path));

        // Plik ze znacznikami dalej gra w całości.
        using var decoder = DecoderFactory.Open(path, 44100, 2);
        Assert.Equal(22050, decoder.TotalFrames);
        Assert.Equal(22050 * 2, DecodeAll(decoder).Length);
    }
}
