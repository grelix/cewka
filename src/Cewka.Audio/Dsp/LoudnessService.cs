using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;
using Cewka.Platform;

namespace Cewka.Audio.Dsp;

/// <summary>
/// One measured file. Size and timestamp guard against a stale entry after re-tagging.
/// Utwór arkusza CUE ma tu klucz z zakresem wycinka (<see cref="QueueEntry.CacheKey"/>).
/// </summary>
public sealed class LoudnessRecord
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required long ModifiedTicks { get; init; }
    public required double IntegratedLufs { get; init; }

    /// <summary>Największa bezwzględna wartość próbki; brak we wpisach zapisanych przed pomiarem szczytu.</summary>
    public double? SamplePeak { get; init; }

    /// <summary>
    /// Szczyty obwiedni pliku we wpisach z wersji 1.0.0, która trzymała obwiednię w tym pliku.
    /// Nowe wpisy mają obwiednię w osobnym pliku (<see cref="EnvelopeStore"/>), a stare przenoszą
    /// się tam przy najbliższym zapisie.
    /// </summary>
    public byte[]? EnvelopePeak { get; init; }

    /// <summary>RMS obwiedni pliku, tej samej długości co <see cref="EnvelopePeak"/>.</summary>
    public byte[]? EnvelopeRms { get; init; }

    /// <summary>Obwiednia zapisana we wpisie; null, gdy jej brak albo zapis jest uszkodzony.</summary>
    [JsonIgnore]
    public FileEnvelope? Envelope =>
        EnvelopePeak is { Length: > 0 } peak && EnvelopeRms is { } rms && rms.Length == peak.Length
            ? new FileEnvelope(peak, rms)
            : null;

    /// <summary>Ten sam pomiar bez obwiedni we wpisie.</summary>
    internal LoudnessRecord WithoutEnvelope() => new()
    {
        Path = Path,
        Length = Length,
        ModifiedTicks = ModifiedTicks,
        IntegratedLufs = IntegratedLufs,
        SamplePeak = SamplePeak,
    };
}

/// <summary>Wynik analizy jednego pliku.</summary>
public readonly record struct LoudnessMeasurement(double IntegratedLufs, double SamplePeak, FileEnvelope? Envelope = null);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<LoudnessRecord>))]
internal sealed partial class LoudnessJsonContext : JsonSerializerContext;

/// <summary>
/// Dostarcza wzmocnienie wyrównania dla utworu: z tagów ReplayGain, z pamięci podręcznej
/// analiz albo, gdy obu brak, zleca analizę w tle. Przy pierwszym odsłuchu taki utwór gra więc
/// bez wyrównania, żeby analiza nie opóźniła startu.
///
/// <para>Dodatnie wzmocnienie ograniczane jest szczytem utworu (z tagu albo z analizy) do progu
/// limitera; inaczej podbite ciche nagranie trafiałoby limiterem w każdy akcent.</para>
///
/// <para>Wzmocnienie albumu (według <see cref="Scope"/>) pochodzi ze znaczników albumu albo ze
/// średniej energetycznej pomiarów utworów albumu i jest ograniczane szczytem albumu. Dopóki
/// album nie jest zmierzony w całości, gra wzmocnienie utworu.</para>
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
    private readonly BlockingCollection<Job> _pending = new(new ConcurrentQueue<Job>());
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pliki za długie na pomiar; do końca pracy programu nie wracają do kolejki.</summary>
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pliki, których ostatni pomiar nie dał wyniku. Album liczy się bez nich, zamiast czekać na
    /// pomiar, który się nie uda; sam utwór przy odtworzeniu próbuje dalej, jak dotąd.
    /// </summary>
    private readonly HashSet<string> _unmeasurable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wpisy, wokół których czeka przegląd albumu.</summary>
    private readonly HashSet<QueueEntry> _albumsQueued = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _queueLock = new();
    private readonly Lock _saveLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<string, CancellationToken, LoudnessMeasurement?> _measure;
    private readonly Thread _worker;
    private readonly TimeSpan _shutdownWait;
    private readonly EnvelopeStore _envelopes;
    private volatile bool _dirty;
    private int _disposed;

    /// <summary>
    /// Ostatnio oddana obwiednia. Kolejne pytania o ten sam utwór dostają ten sam obiekt, bo
    /// odbiorca po tożsamości poznaje, czy obwiednia się zmieniła, a odczyt z dysku nie powtarza się.
    /// </summary>
    private (string Path, long Length, long ModifiedTicks, FileEnvelope Envelope)? _lastEnvelope;
    private readonly Lock _envelopeLock = new();

    /// <summary>Obwiednie bez wpisu sprząta się raz na uruchomienie, przy pierwszym zapisie.</summary>
    private bool _envelopesPruned;

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
        _envelopes = new EnvelopeStore(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cachePath)) ?? string.Empty, "envelopes"));
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

    /// <summary>Kiedy brane jest wzmocnienie albumu; zmiana działa od następnego odczytu wzmocnienia.</summary>
    public NormalisationScope Scope { get; set; } = NormalisationScope.Auto;

    /// <summary>Najdłuższy ciąg wpisów kolejki liczony jako jeden album przy pomiarze.</summary>
    public const int MaxAlbumEntries = 200;

    public TrackGain? GetTrackGain(QueueEntry entry) => GetTrackGain(entry, TrackGainContext.Alone);

    /// <summary>
    /// Wzmocnienie albumu, gdy wymaga tego <see cref="Scope"/> i jest już znane; inaczej
    /// wzmocnienie utworu. Z wątku dekodującego czyta najwyżej znaczniki dwóch sąsiadów; znaczniki
    /// reszty albumu i pomiary zleca wątkowi analizy.
    /// </summary>
    public TrackGain? GetTrackGain(QueueEntry entry, TrackGainContext context)
    {
        // Stacja nie ma pliku do odczytu ani końca, do którego dałoby się ją zmierzyć.
        if (entry.IsLive) return null;

        if (!ReadTags(entry, context)) RequestAlbum(context);

        var track = TrackGainOf(entry);
        if (!WantsAlbumGain(entry, context)) return track;

        // Do kompletu pomiarów albumu gra wzmocnienie utworu.
        return AlbumGainOf(entry, context) ?? track;
    }

    /// <summary>
    /// Odczytuje znaczniki wpisu, o ile wolno to w wątku wołającym. Fałsz, gdy nie wolno, a znaczników
    /// brak: wtedy odczyt należy zlecić wątkowi analizy.
    /// </summary>
    private static bool ReadTags(QueueEntry entry, TrackGainContext context)
    {
        if (entry.Metadata is not null || entry.IsLive) return true;
        if (!context.MayReadTags) return false;

        entry.EnsureMetadata();
        return true;
    }

    private bool WantsAlbumGain(QueueEntry entry, TrackGainContext context)
    {
        var scope = Scope;
        if (scope == NormalisationScope.Track || !AlbumIdentity.HasAlbum(entry)) return false;
        if (scope == NormalisationScope.Auto)
            return IsAlbumNeighbour(entry, context.Previous, context) | IsAlbumNeighbour(entry, context.Next, context);

        // Album: sąsiedzi w kolejce, bo z nich składa się album do pomiaru. Na starcie kolejki ich
        // znaczniki mogą nie być jeszcze odczytane, a bez nich album byłby niekompletny. Bez prawa
        // odczytu album zostaje niekompletny, a znaczniki czyta przegląd albumu w tle.
        var position = context.Position;
        if (position >= 0 && position < context.Surrounding.Count)
        {
            if (position > 0) ReadTags(context.Surrounding[position - 1], context);
            if (position + 1 < context.Surrounding.Count) ReadTags(context.Surrounding[position + 1], context);
        }

        return true;
    }

    /// <summary>Sąsiad bez odczytanych znaczników, których nie wolno teraz czytać, nie jest z albumu; odczyt idzie w tle.</summary>
    private bool IsAlbumNeighbour(QueueEntry entry, QueueEntry? neighbour, TrackGainContext context)
    {
        if (neighbour is null || ReferenceEquals(neighbour, entry) || neighbour.IsLive) return false;

        if (!ReadTags(neighbour, context))
        {
            RequestAlbum(context);
            return false;
        }

        return AlbumIdentity.SameAlbum(entry, neighbour);
    }

    /// <summary>
    /// Wzmocnienie albumu wpisu albo null, gdy jeszcze nieznane. Znaczniki albumu mają pierwszeństwo
    /// przed pomiarem, chyba że ustawienie każe je pomijać.
    /// </summary>
    private TrackGain? AlbumGainOf(QueueEntry entry, TrackGainContext context)
    {
        var album = AlbumRun(entry, context, out var complete);

        if (!AlwaysAnalyse && entry.Metadata?.ReplayGainAlbum is { } tagged)
        {
            var peak = entry.Metadata.ReplayGainAlbumPeak;
            if (peak is null)
            {
                peak = KnownPeak(album);

                // Szczyty reszty albumu poznaje wątek analizy; następne otwarcie skorzysta z nich.
                if (!complete) RequestAlbum(context);
            }

            return FromReplayGain(tagged, peak, TargetLufs) with { Kind = TrackGainKind.Album };
        }

        if (!complete)
        {
            RequestAlbum(context);
            return null;
        }

        var measured = new List<(LoudnessRecord Record, double Seconds)>(album.Count);
        var missing = false;

        foreach (var member in album)
        {
            if (TryGetMeasured(member, out var record))
            {
                measured.Add((record, member.Duration.TotalSeconds));
                continue;
            }

            // Utwór, którego nie da się zmierzyć, nie może wstrzymać albumu na zawsze.
            if (member.IsUnsupported || IsUnmeasurable(member.CacheKey)) continue;

            missing = true;
            Enqueue(member.CacheKey);
        }

        if (missing || measured.Count == 0) return null;

        var (lufs, albumPeak) = AlbumLoudness(measured);
        return FromMeasurement(lufs, albumPeak, TargetLufs) with { Kind = TrackGainKind.Album };
    }

    /// <summary>
    /// Ciągły ciąg wpisów otoczenia z tym samym albumem co <paramref name="entry"/>, najwyżej
    /// <see cref="MaxAlbumEntries"/>. Bez czytania plików: ciąg urywa się na wpisie, którego
    /// znaczników jeszcze nie odczytano, i wtedy <paramref name="complete"/> jest fałszem.
    /// </summary>
    private static List<QueueEntry> AlbumRun(QueueEntry entry, TrackGainContext context, out bool complete)
    {
        var surrounding = context.Surrounding;
        var position = context.Position;
        complete = true;

        if (position < 0 || position >= surrounding.Count || !ReferenceEquals(surrounding[position], entry))
            return [entry];

        var first = position;
        var last = position;
        bool openBefore = true, openAfter = true;

        while ((openBefore || openAfter) && last - first + 1 < MaxAlbumEntries)
        {
            if (openBefore)
            {
                openBefore = Extends(entry, surrounding, first - 1, ref complete);
                if (openBefore) first--;
            }

            if (openAfter && last - first + 1 < MaxAlbumEntries)
            {
                openAfter = Extends(entry, surrounding, last + 1, ref complete);
                if (openAfter) last++;
            }
        }

        var run = new List<QueueEntry>(last - first + 1);
        for (var i = first; i <= last; i++) run.Add(surrounding[i]);
        return run;
    }

    private static bool Extends(QueueEntry entry, IReadOnlyList<QueueEntry> surrounding, int index, ref bool complete)
    {
        if (index < 0 || index >= surrounding.Count) return false;

        var candidate = surrounding[index];
        if (candidate.Metadata is null && !candidate.IsLive)
        {
            complete = false;
            return false;
        }

        return AlbumIdentity.SameAlbum(entry, candidate);
    }

    /// <summary>
    /// Głośność albumu jako średnia energetyczna głośności utworów ważona czasem trwania i szczyt
    /// jako największy ze szczytów utworów. Utwór o nieznanej długości waży tyle co średnia
    /// pozostałych, a gdy nie zna jej żaden, wszystkie ważą jednakowo.
    /// </summary>
    internal static (double Lufs, double? Peak) AlbumLoudness(IReadOnlyList<(LoudnessRecord Record, double Seconds)> tracks)
    {
        var known = tracks.Where(track => track.Seconds > 0 && double.IsFinite(track.Seconds)).ToList();
        var fallback = known.Count > 0 ? known.Average(track => track.Seconds) : 1.0;

        double energy = 0, time = 0;
        double? peak = null;

        foreach (var (record, seconds) in tracks)
        {
            var weight = seconds > 0 && double.IsFinite(seconds) ? seconds : fallback;
            energy += weight * Math.Pow(10, record.IntegratedLufs / 10);
            time += weight;

            if (record.SamplePeak is { } samplePeak && (peak is null || samplePeak > peak)) peak = samplePeak;
        }

        return (10 * Math.Log10(energy / time), peak);
    }

    /// <summary>Największy szczyt utworów albumu znany bez czytania plików: ze znaczników i z pomiarów.</summary>
    private double? KnownPeak(IEnumerable<QueueEntry> album)
    {
        double? peak = null;

        foreach (var member in album)
        {
            var metadata = member.Metadata;
            Consider(metadata?.ReplayGainAlbumPeak);
            Consider(metadata?.ReplayGainTrackPeak);
            if (_cache.TryGetValue(member.CacheKey, out var record)) Consider(record.SamplePeak);
        }

        return peak;

        void Consider(double? value)
        {
            if (value is > 0 && double.IsFinite(value.Value) && (peak is null || value > peak)) peak = value;
        }
    }

    /// <summary>
    /// Pomiar utworu albumu ze szczytem. Granego wpisu pilnuje odczyt jego wzmocnienia; pozostałe
    /// biorą wpis z pamięci, bez sprawdzania pliku na dysku w wątku dekodującym. Nieaktualny wpis
    /// usuwa przegląd albumu w wątku analizy albo odczyt wzmocnienia tego utworu.
    /// </summary>
    private bool TryGetMeasured(QueueEntry member, out LoudnessRecord record)
    {
        if (!_cache.TryGetValue(member.CacheKey, out record!)) return false;
        return record.SamplePeak is not null;
    }

    private TrackGain? TrackGainOf(QueueEntry entry)
    {
        // 1. Tagi. Ich wzmocnienie odnosi się do poziomu ReplayGain, więc inny poziom docelowy
        //    trzeba doliczyć.
        if (!AlwaysAnalyse)
        {
            var tagged = entry.Metadata?.ReplayGainTrack;
            if (tagged is not null)
            {
                var peak = entry.Metadata?.ReplayGainTrackPeak;
                if (peak is null && TryGetCached(entry.CacheKey, out var measured)) peak = measured.SamplePeak;
                return FromReplayGain(tagged.Value, peak, TargetLufs);
            }
        }

        // 2. Wcześniejsza analiza.
        if (TryGetCached(entry.CacheKey, out var record))
        {
            if (record.SamplePeak is not null)
                return FromMeasurement(record.IntegratedLufs, record.SamplePeak, TargetLufs);

            // Wpis sprzed pomiaru szczytu: do ponownej analizy, a do tej pory bez podbijania.
            Enqueue(entry.CacheKey);
            return LowerTo(FromMeasurement(record.IntegratedLufs, null, TargetLufs), 0);
        }

        // 3. Do kolejki; ten odsłuch zagra bez wyrównania.
        Enqueue(entry.CacheKey);
        return null;
    }

    /// <summary>Obwiednia pliku z pamięci podręcznej; null, gdy jej jeszcze nie policzono.</summary>
    public FileEnvelope? GetEnvelope(string path)
    {
        if (!TryGetCached(path, out var record)) return null;

        lock (_envelopeLock)
        {
            if (_lastEnvelope is { } last && last.Length == record.Length && last.ModifiedTicks == record.ModifiedTicks
                && string.Equals(last.Path, path, StringComparison.OrdinalIgnoreCase))
                return last.Envelope;

            // Wpis z wersji 1.0.0 niesie obwiednię sam, dopóki zapis nie przeniesie jej do pliku.
            var envelope = record.Envelope ?? _envelopes.Read(path, record.Length, record.ModifiedTicks);
            if (envelope is not null) _lastEnvelope = (path, record.Length, record.ModifiedTicks, envelope);

            return envelope;
        }
    }

    /// <summary>
    /// Obwiednia z pamięci podręcznej albo null: wtedy plik trafia do analizy w tle, a po jej końcu
    /// zgłasza to <see cref="Measured"/>. Wpis sprzed obwiedni jest liczony ponownie, bez błędu.
    /// </summary>
    public FileEnvelope? RequestEnvelope(string path)
    {
        var envelope = GetEnvelope(path);
        if (envelope is null) Enqueue(path);
        return envelope;
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
            : gain with
            {
                GainDecibels = ceilingDb,
                ProgrammeLevelDecibels = gain.ProgrammeLevelDecibels - (gain.GainDecibels - ceilingDb),
            };

    private bool TryGetCached(string path, out LoudnessRecord record)
    {
        record = null!;
        if (!_cache.TryGetValue(path, out var candidate)) return false;

        try
        {
            var info = new FileInfo(SheetTrack.FileOfCacheKey(path));
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
            if (_refused.Contains(path) || !_queued.Add(path)) return;
        }

        try { _pending.Add(new Job(path, null)); }
        catch (InvalidOperationException) { /* zamykanie */ }
    }

    /// <summary>
    /// Zleca wątkowi analizy przegląd albumu wokół wpisu: odczyt znaczników reszty albumu,
    /// sprawdzenie pomiarów na dysku i zlecenie brakujących.
    /// </summary>
    private void RequestAlbum(TrackGainContext context)
    {
        if (context.Position < 0 || context.Position >= context.Surrounding.Count) return;

        lock (_queueLock)
        {
            if (!_albumsQueued.Add(context.Surrounding[context.Position])) return;
        }

        try { _pending.Add(new Job(null, context)); }
        catch (InvalidOperationException) { /* zamykanie */ }
    }

    private bool IsUnmeasurable(string path)
    {
        lock (_queueLock) return _unmeasurable.Contains(path) || _refused.Contains(path);
    }

    /// <summary>Zadanie wątku analizy: pomiar pliku albo przegląd albumu.</summary>
    private readonly record struct Job(string? Path, TrackGainContext? Album);

    /// <summary>
    /// Przegląd albumu w wątku analizy. Czyta znaczniki wpisów wokół granego, dopóki należą do jego
    /// albumu, i zleca pomiar tym, którym go brak albo których plik zmienił się od pomiaru.
    /// Wynik nie zmienia granego utworu: skorzysta z niego następne otwarcie, jak ze świeżego pomiaru.
    /// </summary>
    private void ReviewAlbum(TrackGainContext context)
    {
        var token = _stopping.Token;
        var entry = context.Surrounding[context.Position];

        try
        {
            entry.EnsureMetadata();

            // Sąsiedzi w kolejności odtwarzania: przy losowaniu nie muszą leżeć obok w kolejce, a tryb
            // „Automatyczny” rozstrzyga po nich, także przy odświeżeniu, które ich nie czyta.
            context.Previous?.EnsureMetadata();
            context.Next?.EnsureMetadata();

            var surrounding = context.Surrounding;
            var members = new List<QueueEntry> { entry };
            int first = context.Position, last = context.Position;
            bool openBefore = true, openAfter = true;

            while ((openBefore || openAfter) && members.Count < MaxAlbumEntries && !token.IsCancellationRequested)
            {
                if (openBefore)
                {
                    openBefore = first > 0 && Belongs(surrounding[first - 1]);
                    if (openBefore) members.Add(surrounding[--first]);
                }

                if (openAfter && members.Count < MaxAlbumEntries)
                {
                    openAfter = last + 1 < surrounding.Count && Belongs(surrounding[last + 1]);
                    if (openAfter) members.Add(surrounding[++last]);
                }
            }

            // Ze znacznikiem albumu wpis nie potrzebuje pomiarów, tylko szczytów z odczytanych znaczników.
            if (!AlwaysAnalyse && entry.Metadata?.ReplayGainAlbum is not null) return;

            foreach (var member in members)
            {
                if (token.IsCancellationRequested) return;
                if (member.IsUnsupported || IsUnmeasurable(member.CacheKey)) continue;
                if (!TryGetCached(member.CacheKey, out var record) || record.SamplePeak is null) Enqueue(member.CacheKey);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] przegląd albumu nie powiódł się: {ex.Message}");
        }
        finally
        {
            lock (_queueLock) _albumsQueued.Remove(entry);
        }

        bool Belongs(QueueEntry candidate)
        {
            candidate.EnsureMetadata();
            return AlbumIdentity.SameAlbum(entry, candidate);
        }
    }

    private readonly TaskCompletionSource _startupSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Kończy się, gdy wątek analizy przeniósł po starcie obwiednie ze starych wpisów; do testów.</summary>
    internal Task StartupSave => _startupSave.Task;

    private void WorkerLoop()
    {
        try
        {
            // Obwiednie we wpisach z wersji 1.0.0 przenoszą się tutaj, w tle. Przy zamykaniu
            // programu zapis setek małych plików trzymałby proces.
            try
            {
                if (_dirty && !_stopping.IsCancellationRequested) Save();
            }
            finally
            {
                _startupSave.TrySetResult();
            }

            foreach (var job in _pending.GetConsumingEnumerable(_stopping.Token))
            {
                if (job.Album is { } album) ReviewAlbum(album);
                else MeasureOne(job.Path!);
            }
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
            if (token.IsCancellationRequested) return;
            if (measurement is null)
            {
                lock (_queueLock) _unmeasurable.Add(path);
                return;
            }

            var info = new FileInfo(SheetTrack.FileOfCacheKey(path));
            var length = info.Length;
            var modifiedTicks = info.LastWriteTimeUtc.Ticks;

            // Obwiednia przed wpisem: wpis bez pliku obwiedni kazałby mierzyć utwór jeszcze raz. Gdy
            // pliku nie da się zapisać, obwiednia zostaje we wpisie, jak w wersji 1.0.0, a najbliższy
            // zapis spróbuje przenieść ją ponownie.
            var envelope = measurement.Value.Envelope;
            var stored = envelope is not null && _envelopes.Write(path, length, modifiedTicks, envelope);
            if (envelope is null) _envelopes.Delete(path);

            var record = new LoudnessRecord
            {
                Path = path,
                Length = length,
                ModifiedTicks = modifiedTicks,
                IntegratedLufs = measurement.Value.IntegratedLufs,
                SamplePeak = measurement.Value.SamplePeak,
                EnvelopePeak = stored ? null : envelope?.Peak,
                EnvelopeRms = stored ? null : envelope?.Rms,
            };

            lock (_envelopeLock) _lastEnvelope = null;

            _cache[path] = record;
            _dirty = true;
            lock (_queueLock) _unmeasurable.Remove(path);

            Measured?.Invoke(path, FromMeasurement(record.IntegratedLufs, record.SamplePeak, TargetLufs).GainDecibels);
        }
        catch (OperationCanceledException)
        {
        }
        catch (MeasurementRefusedException)
        {
            lock (_queueLock) _refused.Add(path);
            Console.Error.WriteLine($"[cewka] plik za długi na pomiar głośności: {Path.GetFileName(SheetTrack.FileOfCacheKey(path))}");
        }
        catch (Exception ex)
        {
            // Błąd odczytu bywa chwilowy (plik zajęty, udział sieciowy na moment niedostępny): album
            // zostaje wtedy niekompletny, a pomiar zleci ponownie następne otwarcie utworu z albumu.
            // Wykluczenie utworu zmieniłoby wzmocnienie albumu po jego późniejszym pomiarze.
            if (!IsReadFailure(ex)) lock (_queueLock) _unmeasurable.Add(path);
            Console.Error.WriteLine($"[cewka] analiza głośności nie powiodła się: {Path.GetFileName(SheetTrack.FileOfCacheKey(path))} — {ex.Message}");
        }
        finally
        {
            lock (_queueLock) _queued.Remove(path);

            // Zapis dopiero gdy kolejka pusta — inaczej przy dodaniu folderu plik byłby
            // przepisywany setki razy. Kolekcja nie jest zwalniana, dopóki ten wątek żyje.
            if (_dirty && _pending.Count == 0 && !token.IsCancellationRequested) Save();
        }
    }

    /// <summary>
    /// Błąd wejścia/wyjścia albo odmowa dostępu, także opakowane przez dekoder. Koniec strumienia
    /// w środku danych to ucięty plik, czyli błąd treści. Usunięty plik i tak nie zagra, więc album
    /// liczy się bez niego; brak katalogu zostaje chwilowy, bo odłączony nośnik albo udział dotyczy
    /// zwykle całego albumu.
    /// </summary>
    private static bool IsReadFailure(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or EndOfStreamException) return false;
            if (current is IOException or UnauthorizedAccessException) return true;
        }

        return false;
    }

    /// <summary>
    /// Najdłuższy mierzony utwór. Pomiar trzyma dane każdego bloku i zajmuje jedyny wątek analizy,
    /// a długość to liczba z pliku: kilkadziesiąt kilobajtów z częstotliwością 1 Hz w nagłówku
    /// rozwija się w dni dźwięku, przez które pozostałe utwory czekałyby na swoją kolej.
    /// </summary>
    internal const long MaxMeasuredFrames = 6L * 3600 * AnalysisRate;

    internal sealed class MeasurementRefusedException : Exception;

    private static LoudnessMeasurement? MeasureFile(string path, CancellationToken token)
    {
        using var decoder = OpenForAnalysis(path);
        return Measure(decoder, token);
    }

    /// <summary>
    /// Dekoder do pomiaru, zawsze z konwersją na poziomie oszczędnym: głośność i obwiednia
    /// nie zależą od różnic między poziomami, a długi filtr spowolniłby analizę biblioteki.
    /// Klucz utworu arkusza daje dekoder samego wycinka, więc mierzony jest tylko on.
    /// </summary>
    internal static IAudioDecoder OpenForAnalysis(string key)
    {
        var sliced = SheetTrack.TryParseCacheKey(key, out var path, out var start, out var end);

        IAudioDecoder decoder;
        using (AudioQuality.UseInThisThread(ResamplingLevel.Economy))
            decoder = DecoderFactory.Open(path, AnalysisRate, AnalysisChannels);

        if (!sliced) return decoder;

        try
        {
            return new SliceDecoder(decoder, path, new SheetTrack { SheetPath = string.Empty, Number = 1, Start = start, End = end });
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    internal static LoudnessMeasurement? Measure(IAudioDecoder decoder, CancellationToken token, long maxFrames = MaxMeasuredFrames)
    {
        // Długość zapowiedziana w nagłówku pozwala odmówić bez dekodowania.
        if (decoder.TotalFrames > maxFrames) throw new MeasurementRefusedException();

        var analyser = new LoudnessAnalyser(AnalysisRate, AnalysisChannels);
        var envelope = new EnvelopeBuilder();
        var buffer = new float[8192 * AnalysisChannels];
        long measured = 0;

        while (true)
        {
            // Przy zamykaniu pomiar godzinnego pliku nie może trzymać wątku do końca.
            token.ThrowIfCancellationRequested();

            var frames = decoder.Read(buffer);
            if (frames == 0) break;

            // Nagłówek mógł nie podać długości albo podać mniejszą.
            if ((measured += frames) > maxFrames) throw new MeasurementRefusedException();

            analyser.Add(buffer, frames);
            envelope.Add(buffer, frames, AnalysisChannels);
        }

        // Wartości nieskończonej nie da się zapisać w JSON; zablokowałaby zapis całej pamięci podręcznej.
        var loudness = analyser.ComputeIntegratedLoudness();
        if (loudness is not { } lufs || !double.IsFinite(lufs) || !double.IsFinite(analyser.SamplePeak)) return null;
        return new LoudnessMeasurement(lufs, analyser.SamplePeak, envelope.Build());
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;

            var json = File.ReadAllText(_cachePath);
            var records = JsonSerializer.Deserialize(json, LoudnessJsonContext.Default.ListLoudnessRecord);
            if (records is null) return;

            foreach (var record in records)
            {
                _cache[record.Path] = record;

                // Obwiednia we wpisie: najbliższy zapis przeniesie ją do osobnego pliku.
                if (record.EnvelopePeak is not null || record.EnvelopeRms is not null) _dirty = true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] pamięć podręczna głośności nieczytelna: {ex.Message}");
        }
    }

    /// <summary>
    /// Przenosi obwiednie z wpisów wersji 1.0.0 do osobnych plików i zostawia we wpisach sam
    /// pomiar. Obwiednia, której pliku nie udało się zapisać, zostaje we wpisie do następnej próby.
    /// Wpis zmieniony w międzyczasie przez pomiar zostaje taki, jaki jest. Przy zamykaniu programu
    /// przenoszenie staje od razu, a reszta czeka we wpisach do następnego uruchomienia.
    /// </summary>
    private void MoveEnvelopesOutOfRecords()
    {
        foreach (var record in _cache.Values)
        {
            if (_stopping.IsCancellationRequested) return;
            if (record.EnvelopePeak is null && record.EnvelopeRms is null) continue;

            // Uszkodzony zapis obwiedni we wpisie nie ma czego przenosić; znika od razu.
            if (record.Envelope is { } envelope
                && !_envelopes.Write(record.Path, record.Length, record.ModifiedTicks, envelope))
                continue;

            _cache.TryUpdate(record.Path, record.WithoutEnvelope(), record);
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

            MoveEnvelopesOutOfRecords();

            var records = _cache.Values.ToList();
            var json = JsonSerializer.Serialize(records, LoudnessJsonContext.Default.ListLoudnessRecord);

            var temporary = _cachePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _cachePath, overwrite: true);

            _dirty = false;

            if (!_envelopesPruned)
            {
                _envelopesPruned = true;
                _envelopes.Prune(records.Select(record => record.Path));
            }
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
