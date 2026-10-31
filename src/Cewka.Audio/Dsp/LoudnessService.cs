using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;
using Cewka.Platform;

namespace Cewka.Audio.Dsp;

/// <summary>One measured file. Size and timestamp guard against a stale entry after re-tagging.</summary>
public sealed class LoudnessRecord
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required long ModifiedTicks { get; init; }
    public required double IntegratedLufs { get; init; }

    /// <summary>Największa bezwzględna wartość próbki; brak we wpisach zapisanych przed pomiarem szczytu.</summary>
    public double? SamplePeak { get; init; }
}

/// <summary>Wynik analizy jednego pliku.</summary>
public readonly record struct LoudnessMeasurement(double IntegratedLufs, double SamplePeak);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<LoudnessRecord>))]
internal sealed partial class LoudnessJsonContext : JsonSerializerContext;

/// <summary>
/// Dostarcza wzmocnienie wyrównania dla utworu: z tagów ReplayGain, z pamięci podręcznej
/// analiz albo, gdy obu brak, zleca analizę w tle. Przy pierwszym odsłuchu taki utwór gra więc
/// bez wyrównania, żeby analiza nie opóźniła startu.
///
/// <para>Dodatnie wzmocnienie ograniczane jest szczytem utworu (z tagu albo z analizy) do progu
/// limitera; inaczej podbite ciche nagranie trafiałoby limiterem w każdy akcent.</para>
/// </summary>
public sealed class LoudnessService : ITrackGainSource, IDisposable
{
    /// <summary>Poziom odniesienia ReplayGain 2.0, do którego odnoszą się tagi.</summary>
    public const double ReferenceLufs = -18.0;

    /// <summary>Zapas pod pełną skalą przy ograniczaniu wzmocnienia szczytem; równy progowi limitera.</summary>
    public const double PeakHeadroomDb = 1.0;

    /// <summary>Format used for the analysis; matching the pipeline avoids a second resample.</summary>
    private const int AnalysisRate = 48000;
    private const int AnalysisChannels = 2;

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, LoudnessRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _pending = new(new ConcurrentQueue<string>());
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _queueLock = new();
    private readonly Lock _saveLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<string, CancellationToken, LoudnessMeasurement?> _measure;
    private readonly Thread _worker;
    private readonly TimeSpan _shutdownWait;
    private volatile bool _dirty;
    private int _disposed;

    public LoudnessService() : this(Path.Combine(AppPaths.CacheDirectory, "loudness.json"))
    {
    }

    public LoudnessService(string cachePath) : this(cachePath, MeasureFile)
    {
    }

    /// <summary>Z własnym pomiarem w miejsce dekodowania pliku; do testów.</summary>
    public LoudnessService(string cachePath, Func<string, CancellationToken, LoudnessMeasurement?> measure)
        : this(cachePath, measure, TimeSpan.FromSeconds(2))
    {
    }

    /// <param name="shutdownWait">Jak długo Dispose czeka na koniec trwającego pomiaru.</param>
    internal LoudnessService(string cachePath, Func<string, CancellationToken, LoudnessMeasurement?> measure,
        TimeSpan shutdownWait)
    {
        _cachePath = cachePath;
        _measure = measure;
        _shutdownWait = shutdownWait;
        Load();

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Cewka.Loudness",
            // Praca porządkowa nie może konkurować z dekodowaniem ani z interfejsem.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>Raised after a file has been measured, so the interface can refresh.</summary>
    public event Action<string, double>? Measured;

    /// <summary>
    /// Poziom, do którego wyrównywane są utwory. Zmiana nie unieważnia pamięci podręcznej, bo
    /// ta przechowuje zmierzoną głośność, a wzmocnienie liczone jest przy każdym odczycie.
    /// </summary>
    public double TargetLufs { get; set; } = ReferenceLufs;

    /// <summary>
    /// Pomija tagi ReplayGain i mierzy każdy utwór samodzielnie, dla zbiorów z tagami
    /// z różnych narzędzi.
    /// </summary>
    public bool AlwaysAnalyse { get; set; }

    public TrackGain? GetTrackGain(QueueEntry entry)
    {
        entry.EnsureMetadata();

        // 1. Tagi. Ich wzmocnienie odnosi się do poziomu ReplayGain, więc inny poziom docelowy
        //    trzeba doliczyć.
        if (!AlwaysAnalyse)
        {
            var tagged = entry.Metadata?.ReplayGainTrack;
            if (tagged is not null)
            {
                var peak = entry.Metadata?.ReplayGainTrackPeak;
                if (peak is null && TryGetCached(entry.Path, out var measured)) peak = measured.SamplePeak;
                return FromReplayGain(tagged.Value, peak, TargetLufs);
            }
        }

        // 2. Wcześniejsza analiza.
        if (TryGetCached(entry.Path, out var record))
        {
            if (record.SamplePeak is not null)
                return FromMeasurement(record.IntegratedLufs, record.SamplePeak, TargetLufs);

            // Wpis sprzed pomiaru szczytu: do ponownej analizy, a do tej pory bez podbijania.
            Enqueue(entry.Path);
            return LowerTo(FromMeasurement(record.IntegratedLufs, null, TargetLufs), 0);
        }

        // 3. Do kolejki; ten odsłuch zagra bez wyrównania.
        Enqueue(entry.Path);
        return null;
    }

    /// <summary>Wzmocnienie z tagu ReplayGain dla zadanego poziomu docelowego, ograniczone szczytem.</summary>
    public static TrackGain FromReplayGain(double replayGainDb, double? peak, double targetLufs) =>
        LimitByPeak(new TrackGain(replayGainDb + targetLufs - ReferenceLufs, targetLufs - ReferenceLufs), peak);

    /// <summary>Wzmocnienie z własnego pomiaru dla zadanego poziomu docelowego, ograniczone szczytem.</summary>
    public static TrackGain FromMeasurement(double integratedLufs, double? peak, double targetLufs) =>
        LimitByPeak(new TrackGain(targetLufs - integratedLufs, targetLufs - ReferenceLufs), peak);

    private static TrackGain LimitByPeak(TrackGain gain, double? peak)
    {
        if (gain.GainDecibels <= 0 || peak is not > 0 || !double.IsFinite(peak.Value)) return gain;
        return LowerTo(gain, -20 * Math.Log10(peak.Value) - PeakHeadroomDb);
    }

    /// <summary>Obniża wzmocnienie do zadanego, a o tyle samo poziom, na jakim program zagra.</summary>
    private static TrackGain LowerTo(TrackGain gain, double ceilingDb) =>
        gain.GainDecibels <= ceilingDb
            ? gain
            : new TrackGain(ceilingDb, gain.ProgrammeLevelDecibels - (gain.GainDecibels - ceilingDb));

    private bool TryGetCached(string path, out LoudnessRecord record)
    {
        record = null!;
        if (!_cache.TryGetValue(path, out var candidate)) return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != candidate.Length ||
                info.LastWriteTimeUtc.Ticks != candidate.ModifiedTicks)
            {
                _cache.TryRemove(path, out _);
                return false;
            }
        }
        catch
        {
            return false;
        }

        record = candidate;
        return true;
    }

    private void Enqueue(string path)
    {
        lock (_queueLock)
        {
            if (!_queued.Add(path)) return;
        }

        try { _pending.Add(path); }
        catch (InvalidOperationException) { /* zamykanie */ }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var path in _pending.GetConsumingEnumerable(_stopping.Token))
                MeasureOne(path);
        }
        catch (OperationCanceledException)
        {
            // Zamykanie; pamięć podręczną zapisuje Dispose.
        }
    }

    private void MeasureOne(string path)
    {
        var token = _stopping.Token;

        try
        {
            var measurement = _measure(path, token);
            if (measurement is null || token.IsCancellationRequested) return;

            var info = new FileInfo(path);
            var record = new LoudnessRecord
            {
                Path = path,
                Length = info.Length,
                ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                IntegratedLufs = measurement.Value.IntegratedLufs,
                SamplePeak = measurement.Value.SamplePeak,
            };

            _cache[path] = record;
            _dirty = true;

            Measured?.Invoke(path, FromMeasurement(record.IntegratedLufs, record.SamplePeak, TargetLufs).GainDecibels);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] analiza głośności nie powiodła się: {Path.GetFileName(path)} — {ex.Message}");
        }
        finally
        {
            lock (_queueLock) _queued.Remove(path);

            // Zapis dopiero gdy kolejka pusta — inaczej przy dodaniu folderu plik byłby
            // przepisywany setki razy. Kolekcja nie jest zwalniana, dopóki ten wątek żyje.
            if (_dirty && _pending.Count == 0 && !token.IsCancellationRequested) Save();
        }
    }

    private static LoudnessMeasurement? MeasureFile(string path, CancellationToken token)
    {
        using var decoder = DecoderFactory.Open(path, AnalysisRate, AnalysisChannels);

        var analyser = new LoudnessAnalyser(AnalysisRate, AnalysisChannels);
        var buffer = new float[8192 * AnalysisChannels];

        while (true)
        {
            // Przy zamykaniu pomiar godzinnego pliku nie może trzymać wątku do końca.
            token.ThrowIfCancellationRequested();

            var frames = decoder.Read(buffer);
            if (frames == 0) break;
            analyser.Add(buffer, frames);
        }

        // Wartości nieskończonej nie da się zapisać w JSON; zablokowałaby zapis całej pamięci podręcznej.
        var loudness = analyser.ComputeIntegratedLoudness();
        if (loudness is not { } lufs || !double.IsFinite(lufs) || !double.IsFinite(analyser.SamplePeak)) return null;
        return new LoudnessMeasurement(lufs, analyser.SamplePeak);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;

            var json = File.ReadAllText(_cachePath);
            var records = JsonSerializer.Deserialize(json, LoudnessJsonContext.Default.ListLoudnessRecord);
            if (records is null) return;

            foreach (var record in records) _cache[record.Path] = record;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] pamięć podręczna głośności nieczytelna: {ex.Message}");
        }
    }

    private void Save()
    {
        // Wątek analizy i Dispose mogą zapisywać naraz, a plik tymczasowy jest wspólny.
        using var scope = _saveLock.EnterScope();

        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_cache.Values.ToList(), LoudnessJsonContext.Default.ListLoudnessRecord);

            var temporary = _cachePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _cachePath, overwrite: true);

            _dirty = false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać pamięci podręcznej głośności: {ex.Message}");
        }
    }

    /// <summary>Czeka na koniec wątku analizy; do testów.</summary>
    internal bool WaitForWorker(TimeSpan timeout) => _worker.Join(timeout);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stopping.Cancel();
        _pending.CompleteAdding();

        // Kolekcja i źródło przerwania zwalniane tylko po faktycznym końcu wątku. Wątek, który
        // jeszcze mierzy plik niesprawdzający przerwania, sięgnąłby po nie i padł z wyjątkiem
        // kończącym proces.
        var finished = _worker.Join(_shutdownWait);

        if (_dirty) Save();
        if (!finished) return;

        _pending.Dispose();
        _stopping.Dispose();
    }
}
