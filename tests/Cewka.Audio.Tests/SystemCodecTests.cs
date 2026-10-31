using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Kodeki systemowe na plikach zbudowanych w teście. Na Windows to Media Foundation;
/// w pozostałych systemach testy kończą się bez sprawdzeń, bo GStreamer nie jest tam pewny.
/// </summary>
public sealed class SystemCodecTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-test-").FullName;

    private static bool Available => OperatingSystem.IsWindows() && SystemCodecs.IsAvailable;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void FailedOpenReleasesTheFile()
    {
        if (!Available) return;

        var path = Write("nieznany-kodek.wav", TestAudioFiles.WavWithUnknownCodec());

        var ex = Assert.Throws<AudioException>(() => SystemCodecs.Open(path, 48000, 2, null));

        // Wyłączny dostęp jest możliwy tylko wtedy, gdy nikt nie trzyma uchwytu pliku.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        Assert.Equal(DecoderFailure.CodecMissing, ex.Reason);
        Assert.Equal(DecoderFailures.Describe(DecoderFailure.CodecMissing), ex.Message);
    }

    [Fact]
    public void StereoFileDecodesUnchanged()
    {
        if (!Available) return;

        var source = TestAudioFiles.ToneInChannel(2, 1, 48000, 48000);
        var path = Write("stereo.wav", TestAudioFiles.Wav(source, 2, 48000));

        using var decoder = SystemCodecs.Open(path, 48000, 2, null);
        var (left, right) = Rms(decoder);

        Assert.Equal(2, decoder.SourceChannels);
        Assert.True(left < 0.001, $"lewy {left}");
        Assert.InRange(right, 0.34, 0.37);
    }

    [Fact]
    public void SurroundCentreIsHeardOnBothSides()
    {
        if (!Available) return;

        // WAVE 5.1: FL, FR, FC, LFE, BL, BR; ton tylko w kanale centralnym.
        var source = TestAudioFiles.ToneInChannel(6, 2, 48000, 48000);
        var path = Write("surround.wav", TestAudioFiles.Wav(source, 6, 48000));

        using var decoder = SystemCodecs.Open(path, 48000, 2, null);
        var (left, right) = Rms(decoder);

        Assert.Equal(6, decoder.SourceChannels);
        Assert.True(left > 0.05, $"lewy {left}");
        Assert.Equal(left, right, 3);
    }

    internal static (double Left, double Right) Rms(IAudioDecoder decoder)
    {
        var buffer = new float[4096 * 2];
        double left = 0, right = 0;
        long frames = 0;
        int read;

        while ((read = decoder.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                left += buffer[2 * i] * buffer[2 * i];
                right += buffer[2 * i + 1] * buffer[2 * i + 1];
            }
            frames += read;
        }

        Assert.True(frames > 0);
        return (Math.Sqrt(left / frames), Math.Sqrt(right / frames));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
