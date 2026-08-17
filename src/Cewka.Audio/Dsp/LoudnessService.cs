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
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<LoudnessRecord>))]
internal sealed partial class LoudnessJsonContext : JsonSerializerContext;

/// <summary>
/// Supplies the normalisation gain for a track.
///
/// <para>Kolejność źródeł: najpierw tagi ReplayGain, bo nic nie kosztują i zwykle pochodzą
/// z porządnego pomiaru; potem pamięć podręczna wcześniejszych analiz; a gdy i tej nie ma,
/// plik trafia do kolejki analizy w tle. Analiza pełnego utworu trwa ułamek sekundy, ale
/// prowadzona jest poza wątkiem dekodującym, żeby nie opóźniła startu odtwarzania — przy
/// pierwszym odsłuchu utwór zagra bez wyrównania, przy kolejnym już z nim.</para>
/// </summary>
public sealed class LoudnessService : ITrackGainSource, IDisposable
{
    /// <summary>ReplayGain 2.0 reference level — poziom, do którego odnoszą się tagi.</summary>
    public const double ReferenceLufs = -18.0;

    /// <summary>Format used for the analysis; matching the pipeline avoids a second resample.</summary>
    private const int AnalysisRate = 48000;
    private const int AnalysisChannels = 2;

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, LoudnessRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _pending = new(new ConcurrentQueue<string>());
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _queueLock = new();
    private readonly Thread _worker;
    private volatile bool _running = true;
    private bool _dirty;

    public LoudnessService() : this(Path.Combine(AppPaths.CacheDirectory, "loudness.json"))
    {
    }

    public LoudnessService(string cachePath)
    {
        _cachePath = cachePath;
        Load();

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Cewka.Loudness",
            // Deliberately below normal: this is housekeeping and must never compete
            // with decoding or with the interface.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>Raised after a file has been measured, so the interface can refresh.</summary>
    public event Action<string, double>? Measured;

    /// <summary>
    /// Poziom, do którego wyrównywane są utwory. Domyślnie <see cref="ReferenceLufs"/>, czyli
    /// poziom odniesienia ReplayGain 2.0.
    ///
    /// <para>Zmiana nie unieważnia pamięci podręcznej: przechowywana jest zmierzona głośność
    /// utworu, a nie wyliczone wzmocnienie, więc nowy poziom wystarczy odjąć ponownie. Dlatego
    /// przestawienie tego ustawienia nie każe niczego analizować po raz drugi.</para>
    /// </summary>
    public double TargetLufs { get; set; } = ReferenceLufs;

    /// <summary>
    /// Gdy prawda, tagi ReplayGain są pomijane i każdy utwór trafia do własnej analizy.
    /// Do zbiorów, w których tagi pochodzą z nieznanego źródła albo z różnych narzędzi —
    /// jednolity pomiar jest wtedy wart oczekiwania na pierwszy odsłuch.
    /// </summary>
    public bool AlwaysAnalyse { get; set; }

    public double? GetGainDecibels(QueueEntry entry)
    {
        entry.EnsureMetadata();

        // 1. Tagi — najtańsze i zwykle najlepsze źródło. Wpisane w nich wzmocnienie odnosi się
        //    do poziomu ReplayGain, więc inny poziom docelowy trzeba do niego doliczyć.
        if (!AlwaysAnalyse)
        {
            var tagged = entry.Metadata?.ReplayGainTrack;
            if (tagged is not null) return tagged + (TargetLufs - ReferenceLufs);
        }

        // 2. Wcześniejsza analiza.
        if (TryGetCached(entry.Path, out var record))
            return TargetLufs - record.IntegratedLufs;

        // 3. Do kolejki; ten odsłuch zagra bez wyrównania.
        Enqueue(entry.Path);
        return null;
    }

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
        foreach (var path in _pending.GetConsumingEnumerable())
        {
            if (!_running) break;

            try
            {
                var loudness = Measure(path);
                if (loudness is null) continue;

                var info = new FileInfo(path);
                var record = new LoudnessRecord
                {
                    Path = path,
                    Length = info.Length,
                    ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                    IntegratedLufs = loudness.Value,
                };

                _cache[path] = record;
                _dirty = true;

                Measured?.Invoke(path, TargetLufs - loudness.Value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] analiza głośności nie powiodła się: {Path.GetFileName(path)} — {ex.Message}");
            }
            finally
            {
                lock (_queueLock) _queued.Remove(path);

                // Zapis dopiero gdy kolejka pusta — inaczej przy dodaniu folderu
                // plik byłby przepisywany setki razy.
                if (_dirty && _pending.Count == 0) Save();
            }
        }
    }

    private static double? Measure(string path)
    {
        using var decoder = DecoderFactory.Open(path, AnalysisRate, AnalysisChannels);

        var analyser = new LoudnessAnalyser(AnalysisRate, AnalysisChannels);
        var buffer = new float[8192 * AnalysisChannels];

        while (true)
        {
            var frames = decoder.Read(buffer);
            if (frames == 0) break;
            analyser.Add(buffer, frames);
        }

        return analyser.ComputeIntegratedLoudness();
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

    public void Dispose()
    {
        _running = false;
        _pending.CompleteAdding();
        _worker.Join(2000);

        if (_dirty) Save();
        _pending.Dispose();
    }
}
