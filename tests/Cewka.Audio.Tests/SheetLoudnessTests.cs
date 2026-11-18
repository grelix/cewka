using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Pomiar głośności i obwiednia utworów arkusza CUE: osobny klucz dla każdego wycinka,
/// pomiar samego wycinka, dotychczasowe klucze dla całych plików.
/// </summary>
public sealed class SheetLoudnessTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-glosnosc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static SheetTrack Track(int number, long start, long? end) =>
        new() { SheetPath = "plyta.cue", Number = number, Start = start, End = end };

    /// <summary>Sekunda głośnego tonu, potem sekunda cichego, przy 48 kHz.</summary>
    private string LoudThenQuiet()
    {
        var loud = Signals.Sine(1000, 0.5, 48000, 48000);
        var quiet = Signals.Sine(1000, 0.05, 48000, 48000);
        var path = Path.Combine(_directory, "plyta.wav");
        File.WriteAllBytes(path, TestAudioFiles.Wav([.. loud, .. quiet], 2, 48000));
        return path;
    }

    private static (LoudnessMeasurement Value, long Frames) Measure(string key)
    {
        using var decoder = LoudnessService.OpenForAnalysis(key);
        var frames = decoder.TotalFrames;
        return (LoudnessService.Measure(decoder, CancellationToken.None)!.Value, frames);
    }

    [Fact]
    public void PomiarWycinkaDekodujeTylkoWycinek()
    {
        var path = LoudThenQuiet();

        var whole = Measure(path);
        var first = Measure(Track(1, 0, 75).CacheKey(path));
        var second = Measure(Track(2, 75, null).CacheKey(path));

        Assert.Equal(96000, whole.Frames);
        Assert.Equal(48000, first.Frames);
        Assert.Equal(48000, second.Frames);

        // Ton dziesięć razy cichszy to 20 dB mniej; szczyt każdego wycinka to jego własny ton.
        Assert.Equal(20, first.Value.IntegratedLufs - second.Value.IntegratedLufs, 1);
        Assert.Equal(0.5, first.Value.SamplePeak, 2);
        Assert.Equal(0.05, second.Value.SamplePeak, 2);
        Assert.True(whole.Value.IntegratedLufs < first.Value.IntegratedLufs);
    }

    [Fact]
    public void KazdyWycinekMaWlasnyPomiarACalyPlikDotychczasowyKlucz()
    {
        var path = LoudThenQuiet();
        var measured = new List<string>();
        using var all = new CountdownEvent(3);

        using var service = new LoudnessService(Path.Combine(_directory, "loudness.json"), (key, token) =>
        {
            lock (measured) measured.Add(key);
            return new LoudnessMeasurement(SheetTrack.TryParseCacheKey(key, out _, out var start, out _) ? -10 - start / 75 : -30, 0.5);
        });
        service.Measured += (_, _) => all.Signal();

        var first = new QueueEntry { Path = path, Sheet = Track(1, 0, 75) };
        var second = new QueueEntry { Path = path, Sheet = Track(2, 75, null) };
        var whole = new QueueEntry { Path = path };

        Assert.Null(service.GetTrackGain(first));
        Assert.Null(service.GetTrackGain(second));
        Assert.Null(service.GetTrackGain(whole));
        Assert.True(all.Wait(TimeSpan.FromSeconds(10)), "nie wszystko zmierzono");

        lock (measured) Assert.Equal(new[] { first.CacheKey, second.CacheKey, path }.Order(), measured.Order());

        // Cel −18 LUFS: wycinek −10 LUFS ściszony o 8 dB, drugi −11 LUFS o 7 dB, cały plik zmierzony osobno.
        Assert.Equal(-8, service.GetTrackGain(first)!.Value.GainDecibels, 6);
        Assert.Equal(-7, service.GetTrackGain(second)!.Value.GainDecibels, 6);
        Assert.NotEqual(-8, service.GetTrackGain(whole)!.Value.GainDecibels, 3);
    }

    [Fact]
    public void ObwiedniaWycinkaPodJegoKluczemTakzePoPonownymUruchomieniu()
    {
        var path = LoudThenQuiet();
        var cache = Path.Combine(_directory, "loudness.json");
        var key = Track(2, 75, null).CacheKey(path);
        var envelope = new FileEnvelope([1, 2, 3], [4, 5, 6]);

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-20, 0.5, envelope)))
        {
            using var measured = new ManualResetEventSlim();
            service.Measured += (_, _) => measured.Set();

            Assert.Null(service.RequestEnvelope(key));
            Assert.True(measured.Wait(TimeSpan.FromSeconds(10)));

            Assert.Equal(envelope.Peak, service.GetEnvelope(key)!.Peak);
            Assert.Null(service.GetEnvelope(path));
        }

        using var reopened = new LoudnessService(cache, (_, _) => null);
        Assert.Equal(envelope.Rms, reopened.GetEnvelope(key)!.Rms);
        Assert.Null(reopened.GetEnvelope(Track(1, 0, 75).CacheKey(path)));
    }
}
