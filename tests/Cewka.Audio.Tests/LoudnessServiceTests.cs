using System.Text.Json;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Wzmocnienie normalizacji z tagów i z własnej analizy, z ochroną szczytów.</summary>
public sealed class LoudnessServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-testy-" + Guid.NewGuid().ToString("N"));

    public LoudnessServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Tag +12 dB przy celu −14 LUFS dałby +16 dB, a szczyt 0,9 zostawia niespełna 1 dB zapasu:
    /// wzmocnienie zostaje ścięte tak, żeby szczyt wypadł na progu limitera.
    /// </summary>
    [Fact]
    public void TaggedGainIsLimitedByThePeak()
    {
        var gain = LoudnessService.FromReplayGain(12, peak: 0.9, targetLufs: -14);

        var ceiling = -20 * Math.Log10(0.9) - LoudnessService.PeakHeadroomDb;
        Assert.Equal(ceiling, gain.GainDecibels, precision: 6);
        Assert.True(gain.GainDecibels <= -0.08);

        // Utwór gra ciszej od celu o tyle, o ile ścięto wzmocnienie.
        Assert.Equal(4 - (16 - ceiling), gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Muzyka poważna −30 LUFS ze szczytem −1 dBFS nie dostaje dodatniego wzmocnienia.</summary>
    [Fact]
    public void MeasuredGainIsLimitedByThePeak()
    {
        var gain = LoudnessService.FromMeasurement(-30, peak: Math.Pow(10, -1.0 / 20), targetLufs: -18);

        Assert.InRange(gain.GainDecibels, -1e-9, 1e-9);
        Assert.Equal(-12, gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Ściszanie nie potrzebuje ochrony szczytów i zostaje bez zmian.</summary>
    [Fact]
    public void NegativeGainIsNotTouched()
    {
        var gain = LoudnessService.FromMeasurement(-9, peak: 1.0, targetLufs: -18);

        Assert.Equal(-9, gain.GainDecibels, precision: 6);
        Assert.Equal(0, gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Bez znanego szczytu wzmocnienie z tagu zostaje, a ochronę przejmuje limiter.</summary>
    [Fact]
    public void UnknownPeakLeavesTheGain()
    {
        var gain = LoudnessService.FromReplayGain(6, peak: null, targetLufs: -18);
        Assert.Equal(6, gain.GainDecibels, precision: 6);
    }

    [Fact]
    public void AnalyserReportsTheSamplePeak()
    {
        var analyser = new LoudnessAnalyser(48000, 2);
        var buffer = Signals.Sine(1000, 0.5, 48000, 48000);
        buffer[1001] = -0.75f;
        analyser.Add(buffer, 48000);

        Assert.Equal(0.75, analyser.SamplePeak, precision: 6);
    }

    /// <summary>
    /// Próbka nieskończona z uszkodzonego pliku dałaby nieskończony szczyt, wzmocnienie −∞ dB
    /// i wyjątek przy zapisie pamięci podręcznej do JSON.
    /// </summary>
    [Fact]
    public void NonFiniteSamplesAreIgnoredByTheAnalyser()
    {
        var clean = new LoudnessAnalyser(48000, 2);
        clean.Add(Signals.Sine(1000, 0.5, 48000, 48000), 48000);

        var analyser = new LoudnessAnalyser(48000, 2);
        var buffer = Signals.Sine(1000, 0.5, 48000, 48000);
        buffer[1001] = float.PositiveInfinity;
        buffer[2002] = float.NegativeInfinity;
        buffer[3003] = float.NaN;
        analyser.Add(buffer, 48000);

        Assert.Equal(0.5, analyser.SamplePeak, precision: 6);
        var loudness = analyser.ComputeIntegratedLoudness();
        Assert.NotNull(loudness);
        Assert.Equal(clean.ComputeIntegratedLoudness()!.Value, loudness!.Value, precision: 1);
    }

    /// <summary>Szczyt spoza liczb skończonych traktowany jest jak nieznany.</summary>
    [Fact]
    public void NonFinitePeakCountsAsUnknown()
    {
        var gain = LoudnessService.FromMeasurement(-24, peak: double.PositiveInfinity, targetLufs: -18);
        Assert.Equal(6, gain.GainDecibels, precision: 6);
    }

    /// <summary>Wpis z pomiaru szczytu ogranicza wzmocnienie utworu z pamięci podręcznej.</summary>
    [Fact]
    public void CachedMeasurementIsLimitedByItsPeak()
    {
        var file = CreateFile("dynamic.flac");
        using var service = ServiceWithCache(file, integratedLufs: -30, samplePeak: 0.5);

        var gain = service.GetTrackGain(new QueueEntry { Path = file });

        Assert.NotNull(gain);
        Assert.Equal(-20 * Math.Log10(0.5) - 1, gain!.Value.GainDecibels, precision: 6);
    }

    /// <summary>
    /// Wpis sprzed pomiaru szczytu nie podbija utworu i trafia do ponownej analizy. Po niej
    /// wzmocnienie rośnie do granicy wyznaczonej szczytem.
    /// </summary>
    [Fact]
    public void CachedRecordWithoutPeakIsNotBoostedUntilRemeasured()
    {
        var file = CreateFile("old.flac");
        var measured = new List<string>();
        using var announced = new ManualResetEventSlim();

        using var service = ServiceWithCache(file, integratedLufs: -30, samplePeak: null, measure: (path, _) =>
        {
            lock (measured) measured.Add(path);
            return new LoudnessMeasurement(-30, 0.5);
        });
        service.Measured += (_, _) => announced.Set();

        var gain = service.GetTrackGain(new QueueEntry { Path = file });

        Assert.NotNull(gain);
        Assert.Equal(0, gain!.Value.GainDecibels, precision: 6);
        Assert.Equal(-12, gain.Value.ProgrammeLevelDecibels, precision: 6);

        Assert.True(announced.Wait(TimeSpan.FromSeconds(5)), "plik nie trafił do analizy");
        lock (measured) Assert.Equal([file], measured);

        var remeasured = service.GetTrackGain(new QueueEntry { Path = file });
        Assert.NotNull(remeasured);
        Assert.Equal(-20 * Math.Log10(0.5) - 1, remeasured!.Value.GainDecibels, precision: 6);
    }

    /// <summary>Zamknięcie przerywa trwający pomiar, zamiast czekać na koniec długiego pliku.</summary>
    [Fact]
    public void DisposeCancelsARunningMeasurement()
    {
        using var started = new ManualResetEventSlim();
        var cancelled = false;

        // Oczekiwanie w Dispose dłuższe niż pomiar: wątek kończy się w nim tylko wtedy, gdy pomiar
        // dostał sygnał przerwania.
        var service = new LoudnessService(Path.Combine(_directory, "loudness.json"), (_, token) =>
        {
            started.Set();
            cancelled = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return new LoudnessMeasurement(-20, 0.5);
        }, shutdownWait: TimeSpan.FromSeconds(30));

        service.GetTrackGain(new QueueEntry { Path = CreateFile("long.flac") });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "pomiar się nie zaczął");

        service.Dispose();
        service.Dispose();

        Assert.True(cancelled, "pomiar nie dostał sygnału przerwania");
        Assert.True(service.WaitForWorker(TimeSpan.Zero), "wątek analizy przeżył zamknięcie");
    }

    /// <summary>
    /// Pomiar, który nie sprawdza przerwania i kończy się po czasie oczekiwania w Dispose,
    /// nie może sięgnąć po zwolnioną kolejkę: wyjątek w wątku tła zakończyłby proces.
    /// Wynik spóźnionego pomiaru nie jest już ogłaszany, a kolejny plik nie jest mierzony.
    /// </summary>
    [Fact]
    public void MeasurementOutlivingDisposeEndsQuietly()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;

        var service = new LoudnessService(Path.Combine(_directory, "loudness.json"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            release.Wait();
            return new LoudnessMeasurement(-20, 0.5);
        }, shutdownWait: TimeSpan.FromMilliseconds(50));

        var announced = false;
        service.Measured += (_, _) => announced = true;

        service.GetTrackGain(new QueueEntry { Path = CreateFile("first.flac") });
        service.GetTrackGain(new QueueEntry { Path = CreateFile("second.flac") });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "pomiar się nie zaczął");

        service.Dispose();
        Assert.False(service.WaitForWorker(TimeSpan.Zero), "pomiar skończył się przed zamknięciem");

        release.Set();
        Assert.True(service.WaitForWorker(TimeSpan.FromSeconds(5)), "wątek analizy się nie skończył");

        Assert.False(announced);
        Assert.Equal(1, calls);
    }

    // ---------- pomocnicze ----------

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    private LoudnessService ServiceWithCache(string file, double integratedLufs, double? samplePeak,
        Func<string, CancellationToken, LoudnessMeasurement?>? measure = null)
    {
        var info = new FileInfo(file);
        var record = new Dictionary<string, object?>
        {
            ["path"] = file,
            ["length"] = info.Length,
            ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
            ["integratedLufs"] = integratedLufs,
        };
        if (samplePeak is not null) record["samplePeak"] = samplePeak;

        var cache = Path.Combine(_directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new[] { record }));
        var service = measure is null ? new LoudnessService(cache) : new LoudnessService(cache, measure);
        service.TargetLufs = -18;
        return service;
    }

    /// <summary>Dekoder, który gra bez końca i podaje długość, jaką mu wpisano.</summary>
    private sealed class EndlessDecoder(long reportedFrames) : Cewka.Audio.Decoding.IAudioDecoder
    {
        public long FramesRead;

        public int SampleRate => 48000;
        public int Channels => 2;
        public long TotalFrames => reportedFrames;
        public long Position => FramesRead;
        public bool CanSeek => false;
        public int SourceSampleRate => 48000;
        public int SourceChannels => 2;
        public int InstantaneousBitrate => 0;

        public int Read(Span<float> destination)
        {
            destination.Fill(0.1f);
            FramesRead += destination.Length / 2;
            return destination.Length / 2;
        }

        public void Seek(long frame) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Długość utworu to liczba z pliku, a pomiar zajmuje jedyny wątek analizy. Plik zapowiadający
    /// dni dźwięku nie jest mierzony wcale, a taki, który długości nie podaje, tylko do limitu.
    /// </summary>
    [Fact]
    public void PomiarOdmawiaPlikowiDluzszemuNizLimit()
    {
        var announced = new EndlessDecoder(LoudnessService.MaxMeasuredFrames + 1);
        Assert.Throws<LoudnessService.MeasurementRefusedException>(
            () => LoudnessService.Measure(announced, CancellationToken.None));
        Assert.Equal(0, announced.FramesRead);

        var silent = new EndlessDecoder(0);
        Assert.Throws<LoudnessService.MeasurementRefusedException>(
            () => LoudnessService.Measure(silent, CancellationToken.None, maxFrames: 48000 * 30));
        Assert.InRange(silent.FramesRead, 48000 * 30, 48000 * 31);
    }

    [Fact]
    public void PlikZbytDlugiNieWracaDoKolejkiPomiaru()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cewka-loud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var file = Path.Combine(dir, "dlugi.wav");
            File.WriteAllBytes(file, [1, 2, 3]);
            var calls = 0;

            using var service = new LoudnessService(Path.Combine(dir, "loudness.json"), (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new LoudnessService.MeasurementRefusedException();
            });

            service.RequestEnvelope(file);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)));

            // Odmowa trafia do zbioru po wyjściu z pomiaru; dopiero wtedy drugie żądanie jest pomijane.
            Thread.Sleep(200);
            service.RequestEnvelope(file);
            Thread.Sleep(300);

            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Analiza otwiera pliki na poziomie oszczędnym niezależnie od ustawienia, a ustawienie
    /// dla odtwarzania zostaje nietknięte.
    /// </summary>
    [Fact]
    public void AnalysisUsesTheEconomyLevel()
    {
        var path = Path.Combine(_directory, "analiza.wav");
        File.WriteAllBytes(path, TestAudioFiles.Wav(new float[4410 * 2], 2, 44100));

        // Zakres wątku zamiast ustawienia ogólnego, żeby nie wpływać na testy obok.
        using (AudioQuality.UseInThisThread(ResamplingLevel.High))
        {
            using var decoder = (Decoding.ManagedDecoderBase)LoudnessService.OpenForAnalysis(path);

            Assert.Equal(ResamplingLevel.Economy, decoder.ResamplingLevel);
            Assert.Equal(ResamplingLevel.High, AudioQuality.Current);
        }
    }
}
