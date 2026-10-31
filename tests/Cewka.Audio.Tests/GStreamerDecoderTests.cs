using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Potok GStreamera na plikach zbudowanych w teście. Pliki są w Ogg Opus, bo do ich
/// odczytu wystarczają wtyczki z gstreamer1.0-plugins-base, wymaganego przez odtwarzacz.
/// </summary>
public sealed class GStreamerDecoderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-test-").FullName;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [GStreamerFact]
    public void OpusFileDecodesThroughThePipeline()
    {
        var path = Write("ton.opus", TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 100 }));

        using var decoder = SystemCodecs.Open(path, 48000, 2, null);
        var (left, right) = SystemCodecTests.Rms(decoder);

        Assert.Equal(48000, decoder.SourceSampleRate);
        Assert.Equal(2, decoder.SourceChannels);
        Assert.True(left > 0.1, $"lewy {left}");
        Assert.True(right > 0.1, $"prawy {right}");
    }

    [GStreamerFact]
    public void UnreadableFileFailsWithReasonAndIsReleased()
    {
        var path = Write("puste.m4a", new byte[64 * 1024]);

        var ex = Assert.Throws<AudioException>(() => SystemCodecs.Open(path, 48000, 2, null));

        Assert.NotEqual(DecoderFailure.Unspecified, ex.Reason);
        Assert.Equal(DecoderFailures.Describe(ex.Reason), ex.Message);
        Assert.DoesNotContain(path, OpenFiles());
    }

    [GStreamerFact]
    public async Task DisposeDuringReadEndsTheRead()
    {
        var path = Write("dlugi.opus", TestAudioFiles.Opus(new TestAudioFiles.OpusOptions { Frames = 500 }));
        var decoder = SystemCodecs.Open(path, 48000, 2, null);

        using var started = new ManualResetEventSlim();
        var reading = Task.Run(() =>
        {
            var buffer = new float[1024 * 2];
            started.Set();
            while (decoder.Read(buffer) > 0) { }
        });

        started.Wait(TimeSpan.FromSeconds(10));
        decoder.Dispose();

        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(reading, finished);
        await reading;
    }

    /// <summary>Pliki, na które wskazują otwarte deskryptory procesu.</summary>
    private static List<string> OpenFiles()
    {
        var targets = new List<string>();

        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(descriptor).LinkTarget is { } target) targets.Add(target);
            }
            catch (IOException) { }
        }

        return targets;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
