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

    /// <summary>Mierzy plik i czeka na koniec pomiaru.</summary>
    private static void Measure(LoudnessService service, string file)
    {
        using var done = new ManualResetEventSlim();
        service.Measured += (_, _) => done.Set();
        Assert.Null(service.RequestEnvelope(file));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
    }

    private static FileEnvelope SomeEnvelope()
    {
        var builder = new EnvelopeBuilder();
        builder.Add(Burst(48_000, 0, 24_000, 0.5f), 48_000, 2);
        return builder.Build()!;
    }

    /// <summary>
    /// Obwiednia leży w osobnym pliku, a nie w pamięci podręcznej pomiarów: ta jest czytana w całości
    /// przy starcie i zapisywana w całości po każdym pomiarze, a obwiednia to ponad 5 kB na utwór.
    /// </summary>
    [Fact]
    public void ObwiedniaNieTrafiaDoPlikuPomiarow()
    {
        var file = Path.Combine(_directory, "a.flac");
        File.WriteAllBytes(file, new byte[64]);
        var cache = Path.Combine(_directory, "loudness.json");
        var envelope = SomeEnvelope();

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-18, 0.5, envelope)))
            Measure(service, file);

        var json = File.ReadAllText(cache);
        Assert.DoesNotContain("envelope", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.Length < 600, $"plik pomiarów ma {json.Length} znaków");
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "envelopes"), "*.env"));
    }

    /// <summary>Odbiorca poznaje zmianę obwiedni po tożsamości obiektu, więc kolejne pytania dają ten sam.</summary>
    [Fact]
    public void KolejnePytaniaOObwiednieDajaTenSamObiekt()
    {
        var file = Path.Combine(_directory, "a.flac");
        File.WriteAllBytes(file, new byte[64]);
        var cache = Path.Combine(_directory, "loudness.json");
        var envelope = SomeEnvelope();

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-18, 0.5, envelope)))
            Measure(service, file);

        using var reloaded = new LoudnessService(cache, (_, _) => null);
        var first = reloaded.GetEnvelope(file);

        Assert.NotNull(first);
        Assert.Same(first, reloaded.GetEnvelope(file));
        Assert.Same(first, reloaded.RequestEnvelope(file));
    }

    /// <summary>Plik pomiarów z wersji 1.0.0 trzymał obwiednię we wpisie; po zapisie leży w osobnym pliku.</summary>
    [Fact]
    public async Task ObwiedniaZeStaregoWpisuPrzenosiSieDoOsobnegoPliku()
    {
        var file = Path.Combine(_directory, "stary.flac");
        File.WriteAllBytes(file, new byte[64]);
        var info = new FileInfo(file);
        var cache = Path.Combine(_directory, "loudness.json");
        var envelope = SomeEnvelope();

        File.WriteAllText(cache, JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = file,
                ["length"] = info.Length,
                ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
                ["integratedLufs"] = -20.0,
                ["samplePeak"] = 0.7,
                ["envelopePeak"] = Convert.ToBase64String(envelope.Peak),
                ["envelopeRms"] = Convert.ToBase64String(envelope.Rms),
            },
        }));

        var measured = 0;
        using (var service = new LoudnessService(cache, (_, _) => { Interlocked.Increment(ref measured); return null; }))
        {
            // Obwiednia jest dostępna i przed przeniesieniem (z wpisu), i po nim (z pliku).
            Assert.Equal(envelope.Peak, service.RequestEnvelope(file)!.Peak);

            // Przeniesienie robi wątek analizy zaraz po starcie, a nie zamykanie programu.
            await service.StartupSave.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(envelope.Peak, service.RequestEnvelope(file)!.Peak);
        }

        Assert.DoesNotContain("envelope", File.ReadAllText(cache), StringComparison.OrdinalIgnoreCase);

        using var reloaded = new LoudnessService(cache, (_, _) => { Interlocked.Increment(ref measured); return null; });
        var read = reloaded.RequestEnvelope(file);

        Assert.NotNull(read);
        Assert.Equal(envelope.Peak, read!.Peak);
        Assert.Equal(envelope.Rms, read.Rms);
        Assert.Equal(0, measured);
        Assert.NotNull(reloaded.GetTrackGain(new Cewka.Audio.Playback.QueueEntry { Path = file }));
    }

    /// <summary>
    /// Gdy pliku obwiedni nie da się zapisać, obwiednia nie przepada: zostaje we wpisie pomiaru,
    /// i nowa, i przenoszona ze starego wpisu, a zapis ponawia próbę.
    /// </summary>
    [Fact]
    public async Task ObwiedniaZostajeWeWpisieGdyJejPlikuNieDaSieZapisac()
    {
        var file = Path.Combine(_directory, "a.flac");
        File.WriteAllBytes(file, new byte[64]);
        var old = Path.Combine(_directory, "stary.flac");
        File.WriteAllBytes(old, new byte[64]);
        var info = new FileInfo(old);
        var cache = Path.Combine(_directory, "loudness.json");
        var envelope = SomeEnvelope();

        File.WriteAllText(cache, JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["path"] = old,
                ["length"] = info.Length,
                ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
                ["integratedLufs"] = -20.0,
                ["samplePeak"] = 0.7,
                ["envelopePeak"] = Convert.ToBase64String(envelope.Peak),
                ["envelopeRms"] = Convert.ToBase64String(envelope.Rms),
            },
        }));

        // Plik w miejscu katalogu obwiedni: katalogu nie da się założyć ani nic w nim zapisać.
        var blocked = Path.Combine(_directory, "envelopes");
        File.WriteAllText(blocked, "zajęte");

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-18, 0.5, envelope)))
            Measure(service, file);

        using (var reloaded = new LoudnessService(cache, (_, _) => null))
        {
            Assert.Equal(envelope.Peak, reloaded.GetEnvelope(file)!.Peak);
            Assert.Equal(envelope.Peak, reloaded.GetEnvelope(old)!.Peak);
        }

        // Przeszkoda znika: następny zapis przenosi obie obwiednie do plików.
        File.Delete(blocked);
        using (var retried = new LoudnessService(cache, (_, _) => null))
        {
            await retried.StartupSave.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(retried.GetEnvelope(file));
        }

        Assert.DoesNotContain("envelope", File.ReadAllText(cache), StringComparison.OrdinalIgnoreCase);
        using var last = new LoudnessService(cache, (_, _) => null);
        Assert.Equal(envelope.Peak, last.GetEnvelope(file)!.Peak);
        Assert.Equal(envelope.Rms, last.GetEnvelope(old)!.Rms);
    }

    /// <summary>Obwiednia innej wersji utworu (zmieniony plik) nie jest oddawana.</summary>
    [Fact]
    public void ObwiedniaInnejWersjiUtworuJestOdrzucana()
    {
        var directory = Path.Combine(_directory, "envelopes");
        var store = new EnvelopeStore(directory);
        var envelope = SomeEnvelope();

        store.Write("C:/muzyka/a.flac", 100, 200, envelope);

        Assert.Equal(envelope.Peak, store.Read("C:/muzyka/a.flac", 100, 200)!.Peak);
        Assert.Equal(envelope.Rms, store.Read("c:/MUZYKA/A.flac", 100, 200)!.Rms);
        Assert.Null(store.Read("C:/muzyka/a.flac", 101, 200));
        Assert.Null(store.Read("C:/muzyka/a.flac", 100, 201));
        Assert.Null(store.Read("C:/muzyka/b.flac", 100, 200));

        // Plik ucięty albo z obcą zawartością to brak obwiedni, nie błąd.
        File.WriteAllBytes(store.FileFor("C:/muzyka/a.flac"), new byte[7]);
        Assert.Null(store.Read("C:/muzyka/a.flac", 100, 200));
    }

    /// <summary>Obwiednie utworów, których nie ma już w pamięci podręcznej, znikają przy zapisie.</summary>
    [Fact]
    public void ObwiednieBezWpisuSaSprzatane()
    {
        var file = Path.Combine(_directory, "a.flac");
        File.WriteAllBytes(file, new byte[64]);
        var cache = Path.Combine(_directory, "loudness.json");
        var envelope = SomeEnvelope();

        var orphans = new EnvelopeStore(Path.Combine(_directory, "envelopes"));
        orphans.Write("C:/nie-ma/usuniety.flac", 1, 2, envelope);
        var foreign = Path.Combine(_directory, "envelopes", "notatka.txt");
        File.WriteAllText(foreign, "cudzy plik");

        using (var service = new LoudnessService(cache, (_, _) => new LoudnessMeasurement(-18, 0.5, envelope)))
            Measure(service, file);

        Assert.False(File.Exists(orphans.FileFor("C:/nie-ma/usuniety.flac")));
        Assert.True(File.Exists(orphans.FileFor(file)));
        Assert.True(File.Exists(foreign));
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
