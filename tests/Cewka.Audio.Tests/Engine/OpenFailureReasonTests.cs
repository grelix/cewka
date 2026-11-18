using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Przyczyna nieudanego otwarcia, którą silnik zgłasza dla prawdziwych dekoderów. Odmiana
/// rozpoznanego formatu, której dekoder nie obsługuje, jest formatem nieobsługiwanym, a nie
/// plikiem uszkodzonym.
/// </summary>
public class OpenFailureReasonTests
{
    private static TrackFailureReason FailureOf(byte[] data, AudioFileFormat format)
    {
        using var h = new EngineHarness();
        h.Engine.SetQueue([h.Track("plik", () =>
            DecoderFactory.Open(new MemoryStream(data), format, true, EngineHarness.Rate, EngineHarness.Channels))]);

        h.PumpUntil(() => h.Finished > 0);
        EngineHarness.WaitUntil(() => !h.Failures.IsEmpty);
        return Assert.Single(h.Failures).Failure.Reason;
    }

    /// <summary>WavPack sprzed wersji 4: nagłówek RIFF, a za nim blok starego formatu.</summary>
    private static byte[] LegacyWavPack()
    {
        var header = TestAudioFiles.PcmWav(new int[200], 2, 44100, 16)[..44];
        return TestAudioFiles.Concat(header, "wvpk"u8.ToArray(), BitConverter.GetBytes(0x0003), new byte[400]);
    }

    [Fact]
    public void DsdWavPackIsUnsupportedFormat() =>
        Assert.Equal(TrackFailureReason.UnsupportedFormat,
            FailureOf(TestAudioFiles.DataFile("cisza-dsd-stereo.wv"), AudioFileFormat.WavPack));

    [Fact]
    public void WavPackOlderThanVersionFourIsUnsupportedFormat() =>
        Assert.Equal(TrackFailureReason.UnsupportedFormat, FailureOf(LegacyWavPack(), AudioFileFormat.WavPack));

    /// <summary>Śmieci w miejscu pliku zostają plikiem uszkodzonym, także w dekoderze miniaudio.</summary>
    [Theory]
    [InlineData(AudioFileFormat.WavPack)]
    [InlineData(AudioFileFormat.Wav)]
    [InlineData(AudioFileFormat.Mp3)]
    [InlineData(AudioFileFormat.Flac)]
    public void GarbageIsDamagedFile(AudioFileFormat format)
    {
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray();
        Assert.Equal(TrackFailureReason.DamagedFile, FailureOf(garbage, format));
    }
}
