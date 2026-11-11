using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Stacja to wpis bez pliku: nic nie czyta go z dysku ani nie mierzy.</summary>
public sealed class QueueEntryLiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-testy-" + Guid.NewGuid().ToString("N"));

    public QueueEntryLiveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static QueueEntry LiveEntry()
    {
        var source = new LiveSource("s1", "Stacja", [new Uri("http://127.0.0.1:1/")]);
        return new QueueEntry { Path = source.StationId, Live = new LiveBinding(source, new FakeLiveSession(source)) };
    }

    [Fact]
    public void LiveEntryIsNotReadFromDisk()
    {
        var entry = LiveEntry();

        entry.EnsureMetadata();

        Assert.True(entry.IsLive);
        Assert.Null(entry.Metadata);
        Assert.False(entry.IsUnsupported);
        Assert.False(new QueueEntry { Path = "plik.mp3" }.IsLive);
    }

    /// <summary>Plik analizuje się w tle, ale stacji nie ma czego analizować: strumień nie ma końca.</summary>
    [Fact]
    public void LoudnessServiceDoesNotMeasureAStation()
    {
        var measured = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var fileMeasured = new ManualResetEventSlim();
        var file = Path.Combine(_directory, "plik.flac");
        File.WriteAllBytes(file, new byte[16]);
        using var service = new LoudnessService(
            Path.Combine(_directory, "glosnosc.json"),
            (path, _) =>
            {
                measured.Enqueue(path);
                if (path == file) fileMeasured.Set();
                return new LoudnessMeasurement(-20, 0.5);
            });

        Assert.Null(service.GetTrackGain(LiveEntry()));

        // Plik jest za stacją w kolejce, a wątek analizy ma niski priorytet: jego pomiar znaczy, że do stacji już doszedł.
        service.GetTrackGain(new QueueEntry { Path = file });
        Assert.True(fileMeasured.Wait(TimeSpan.FromMinutes(1)), "wątek analizy nie doszedł do pliku");

        Assert.Equal([file], measured);
    }
}
