using System.Text.Json;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Obwiednia pliku liczona razem z analizą głośności i jej pamięć podręczna.</summary>
public sealed class FileEnvelopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-testy-" + Guid.NewGuid().ToString("N"));

    public FileEnvelopeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static float[] Burst(int frames, int loudFrom, int loudTo, float level)
    {
        var buffer = new float[frames * 2];
        for (var i = loudFrom; i < loudTo; i++)
        {
            buffer[2 * i] = level;
            buffer[2 * i + 1] = -level;
        }

        return buffer;
    }

    [Fact]
    public void ZnanyPrzebiegDajeSzczytIRmsWKazdymOdcinku()
    {
        // Pierwsza połowa pliku: stała wartość 0,5 (szczyt i RMS 0,5); druga: cisza.
        const int frames = 640_000;
        var builder = new EnvelopeBuilder();
        builder.Add(Burst(frames, 0, frames / 2, 0.5f), frames, 2);

        var envelope = builder.Build();

        Assert.NotNull(envelope);
        Assert.Equal(FileEnvelope.TargetPoints, envelope!.Count);
        Assert.Equal(0.5, envelope.PeakAt(10), 2);
        Assert.Equal(0.5, envelope.RmsAt(10), 2);
        Assert.Equal(0.5, envelope.PeakAt(990), 2);
        Assert.Equal(0, envelope.PeakAt(1010), 3);
        Assert.Equal(0, envelope.RmsAt(1990), 3);
    }

    [Fact]
    public void SzczytJestWiekszyOdRmsDlaSinusa()
    {
        const int frames = 96_000;
        var builder = new EnvelopeBuilder();
        // 375 Hz przy 48 kHz to równo 2 okresy na odcinek 256 ramek, więc RMS każdego odcinka jest dokładny.
        builder.Add(Signals.Sine(375, 0.8, frames, 48000), frames, 2);

        var envelope = builder.Build()!;

        Assert.Equal(0.8, envelope.PeakAt(envelope.Count / 2), 1);
        Assert.Equal(0.8 / Math.Sqrt(2), envelope.RmsAt(envelope.Count / 2), 1);
    }

    [Fact]
    public void PamiecJestStalaDlaBardzoDlugiegoStrumienia()
    {
        // Ponad 4096 odcinków wymusza scalanie parami; wynik nadal ma 2000 punktów, a dźwięk
        // z końca pliku trafia na koniec obwiedni.
        const int chunk = 48_000;
        var builder = new EnvelopeBuilder();
        var quiet = new float[chunk * 2];
        for (var second = 0; second < 700; second++) builder.Add(quiet, chunk, 2);
        builder.Add(Burst(chunk, 0, chunk, 0.9f), chunk, 2);

        var envelope = builder.Build()!;

        Assert.Equal(FileEnvelope.TargetPoints, envelope.Count);
        Assert.Equal(0, envelope.PeakAt(100), 3);
        Assert.Equal(0.9, envelope.PeakAt(envelope.Count - 1), 2);
    }

    [Fact]
    public void KrotkiPlikDajeMniejOdcinkowNizDocelowo()
    {
        var builder = new EnvelopeBuilder();
        builder.Add(Burst(1000, 0, 1000, 0.25f), 1000, 2);

        var envelope = builder.Build()!;

        Assert.InRange(envelope.Count, 1, 8);
        Assert.Equal(0.25, envelope.PeakAt(envelope.Count - 1), 2);
    }

    [Fact]
    public void PustyStrumienNieMaObwiedni() => Assert.Null(new EnvelopeBuilder().Build());

    [Fact]
    public void ProbkiNieskonczoneLiczaSieJakCisza()
    {
        var buffer = Burst(2000, 0, 2000, 0.5f);
        buffer[10] = float.NaN;
        buffer[11] = float.PositiveInfinity;
        var builder = new EnvelopeBuilder();
        builder.Add(buffer, 2000, 2);

        var envelope = builder.Build()!;

        for (var i = 0; i < envelope.Count; i++) Assert.InRange(envelope.PeakAt(i), 0, 0.51);
    }

    [Fact]
    public void ObwiedniaPrzezywaZapisIOdczytPamieciPodrecznej()
    {
        var file = Path.Combine(_directory, "a.flac");
        File.WriteAllBytes(file, new byte[64]);
        var builder = new EnvelopeBuilder();
        builder.Add(Burst(48_000, 0, 24_000, 0.5f), 48_000, 2);
        var envelope = builder.Build()!;
        var cache = Path.Combine(_directory, "loudness.json");

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-18, 0.5, envelope)))
        {
            using var done = new ManualResetEventSlim();
            service.Measured += (_, _) => done.Set();
            Assert.Null(service.RequestEnvelope(file));
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(service.GetEnvelope(file));
        }

        using var reloaded = new LoudnessService(cache, (_, _) => null);
        var read = reloaded.RequestEnvelope(file);

        Assert.NotNull(read);
        Assert.Equal(envelope.Count, read!.Count);
        Assert.Equal(envelope.Peak, read.Peak);
        Assert.Equal(envelope.Rms, read.Rms);
    }

    [Fact]
    public void StaryWpisBezObwiedniLiczySieRazJeszczeBezBledu()
    {
        var file = Path.Combine(_directory, "old.flac");
        File.WriteAllBytes(file, new byte[64]);
        var info = new FileInfo(file);
        var cache = Path.Combine(_directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = file,
                ["length"] = info.Length,
                ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
                ["integratedLufs"] = -20.0,
                ["samplePeak"] = 0.7,
            },
        }));

        var calls = 0;
        var builder = new EnvelopeBuilder();
        builder.Add(Burst(4800, 0, 4800, 0.3f), 4800, 2);
        using var done = new ManualResetEventSlim();
        using var service = new LoudnessService(cache, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new LoudnessMeasurement(-20, 0.7, builder.Build());
        });
        service.Measured += (_, _) => done.Set();

        Assert.Null(service.GetEnvelope(file));
        Assert.Null(service.RequestEnvelope(file));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, calls);
        Assert.NotNull(service.GetEnvelope(file));
        Assert.NotNull(service.RequestEnvelope(file));
    }

    [Fact]
    public void UszkodzonaObwiednVWpisieJestIgnorowana()
    {
        var file = Path.Combine(_directory, "bad.flac");
        File.WriteAllBytes(file, new byte[64]);
        var info = new FileInfo(file);
        var cache = Path.Combine(_directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = file,
                ["length"] = info.Length,
                ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
                ["integratedLufs"] = -20.0,
                ["samplePeak"] = 0.7,
                ["envelopePeak"] = Convert.ToBase64String(new byte[10]),
                ["envelopeRms"] = Convert.ToBase64String(new byte[3]),
            },
        }));

        using var service = new LoudnessService(cache, (_, _) => null);

        Assert.Null(service.GetEnvelope(file));
        Assert.NotNull(service.GetTrackGain(new Cewka.Audio.Playback.QueueEntry { Path = file }));
    }
}
