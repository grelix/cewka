using System.Collections.Concurrent;
using System.Text.Json;
using Cewka.Audio.Dsp;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Wybór między wzmocnieniem utworu a albumu: tryb, sąsiedzi w kolejności odtwarzania, znaczniki
/// i pomiary, oraz wzór głośności albumu.
/// </summary>
public sealed class AlbumGainTests : IDisposable
{
    private const double Target = -18;
    private const double Precision = 1e-6;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-album-" + Guid.NewGuid().ToString("N"));
    private readonly List<Dictionary<string, object?>> _records = [];
    private readonly ConcurrentQueue<string> _measured = new();

    public AlbumGainTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ---------- wzór ----------

    /// <summary>Średnia energetyczna ważona czasem: głośny krótki utwór waży mniej niż cichy długi.</summary>
    [Fact]
    public void GlosnoscAlbumuToSredniaEnergetycznaWazonaCzasem()
    {
        var (lufs, peak) = LoudnessService.AlbumLoudness(
        [
            (Record(-10, 0.5), 60),
            (Record(-20, 0.9), 180),
        ]);

        var expected = 10 * Math.Log10((60 * Math.Pow(10, -1.0) + 180 * Math.Pow(10, -2.0)) / 240);
        Assert.Equal(expected, lufs, Precision);
        Assert.Equal(0.9, peak);
    }

    /// <summary>Album z utworów równej głośności ma tę samą głośność niezależnie od długości.</summary>
    [Fact]
    public void RownaGlosnoscDajeTeSamaGlosnoscAlbumu()
    {
        var (lufs, _) = LoudnessService.AlbumLoudness([(Record(-14, 0.5), 30), (Record(-14, 0.5), 400)]);
        Assert.Equal(-14, lufs, Precision);
    }

    /// <summary>Utwór bez znanej długości waży tyle co średnia pozostałych; bez żadnej wszystkie jednakowo.</summary>
    [Fact]
    public void NieznanaDlugoscWazyJakSrednia()
    {
        var (withUnknown, _) = LoudnessService.AlbumLoudness([(Record(-10, 0.5), 100), (Record(-20, 0.5), 0)]);
        var (equal, _) = LoudnessService.AlbumLoudness([(Record(-10, 0.5), 100), (Record(-20, 0.5), 100)]);
        Assert.Equal(equal, withUnknown, Precision);

        var (none, _) = LoudnessService.AlbumLoudness([(Record(-10, 0.5), 0), (Record(-20, 0.5), double.NaN)]);
        Assert.Equal(equal, none, Precision);
    }

    // ---------- macierz wyboru ----------

    // Dwa utwory jednego albumu po 100 s: −10 i −20 LUFS. Album: 10·log10((0,1 + 0,01) / 2).
    private static readonly double AlbumLufs = 10 * Math.Log10((0.1 + 0.01) / 2);
    private static readonly double AlbumGain = Target - AlbumLufs;

    public enum Neighbour { None, SameAlbum, OtherAlbum }

    [Theory]
    [InlineData(NormalisationScope.Track, Neighbour.SameAlbum, false)]
    [InlineData(NormalisationScope.Track, Neighbour.None, false)]
    [InlineData(NormalisationScope.Auto, Neighbour.SameAlbum, true)]
    [InlineData(NormalisationScope.Auto, Neighbour.OtherAlbum, false)]
    [InlineData(NormalisationScope.Auto, Neighbour.None, false)]
    [InlineData(NormalisationScope.Album, Neighbour.SameAlbum, true)]
    [InlineData(NormalisationScope.Album, Neighbour.OtherAlbum, true)]
    [InlineData(NormalisationScope.Album, Neighbour.None, true)]
    public void TrybISasiedziWybierajaWzmocnienie(NormalisationScope scope, Neighbour neighbour, bool album)
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", lufs: -20, peak: 0.25);
        var other = Track("c", "Inny", lufs: -15, peak: 0.5);
        using var service = Service(scope);

        // Album do pomiaru to ciąg wpisów kolejki; sąsiad w kolejności odtwarzania może być inny (losowanie).
        var queue = new[] { a, b, other };
        QueueEntry? next = neighbour switch
        {
            Neighbour.SameAlbum => b,
            Neighbour.OtherAlbum => other,
            _ => null,
        };

        var gain = service.GetTrackGain(a, new TrackGainContext(null, next, queue, 0));

        Assert.NotNull(gain);
        if (album)
        {
            Assert.Equal(TrackGainKind.Album, gain.Value.Kind);
            Assert.Equal(AlbumGain, gain.Value.GainDecibels, Precision);
        }
        else
        {
            Assert.Equal(TrackGainKind.Track, gain.Value.Kind);
            Assert.Equal(Target + 10, gain.Value.GainDecibels, Precision);
        }
    }

    /// <summary>Sąsiad poprzedni liczy się tak samo jak następny.</summary>
    [Fact]
    public void PoprzedniZTegoSamegoAlbumuWlaczaAlbum()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", lufs: -20, peak: 0.25);
        using var service = Service(NormalisationScope.Auto);

        var gain = service.GetTrackGain(b, new TrackGainContext(a, null, [a, b], 1));

        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(AlbumGain, gain.Value.GainDecibels, Precision);
    }

    /// <summary>Wpis bez albumu dostaje wzmocnienie utworu w każdym trybie, także gdy sąsiedzi nie mają albumu.</summary>
    [Theory]
    [InlineData(NormalisationScope.Album)]
    [InlineData(NormalisationScope.Auto)]
    public void WpisBezAlbumuDostajeWzmocnienieUtworu(NormalisationScope scope)
    {
        var a = Track("a", null, lufs: -10, peak: 0.5);
        var b = Track("b", null, lufs: -20, peak: 0.25);
        using var service = Service(scope);

        var gain = service.GetTrackGain(a, new TrackGainContext(null, b, [a, b], 0));

        Assert.Equal(TrackGainKind.Track, gain!.Value.Kind);
        Assert.Equal(Target + 10, gain.Value.GainDecibels, Precision);
    }

    /// <summary>Wpis bez otoczenia w trybie „Album” to album z jednego utworu.</summary>
    [Fact]
    public void BezOtoczeniaAlbumemJestSamUtwor()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        using var service = Service(NormalisationScope.Album);

        var gain = service.GetTrackGain(a);

        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(Target + 10, gain.Value.GainDecibels, Precision);
    }

    // ---------- znaczniki ----------

    /// <summary>Znacznik albumu ma pierwszeństwo przed pomiarem i ogranicza go szczyt albumu ze znacznika.</summary>
    [Fact]
    public void ZnacznikAlbumuMaPierwszenstwo()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5, albumGain: 9, albumPeak: Math.Pow(10, -4.0 / 20));
        var b = Track("b", "Album", lufs: -20, peak: 0.25);
        using var service = Service(NormalisationScope.Auto);

        var gain = service.GetTrackGain(a, new TrackGainContext(null, b, [a, b], 0));

        // +9 dB ścięte do szczytu −4 dBFS: 4 dB − 1 dB zapasu.
        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(3, gain.Value.GainDecibels, Precision);
        Assert.Equal(-6, gain.Value.ProgrammeLevelDecibels, Precision);
    }

    /// <summary>Bez znacznika szczytu albumu wzmocnienie ogranicza największy znany szczyt utworów albumu.</summary>
    [Fact]
    public void BezSzczytuAlbumuDecydujeNajwiekszySzczytUtworow()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.25, albumGain: 9);
        var b = Track("b", "Album", lufs: -20, peak: Math.Pow(10, -4.0 / 20));
        var c = Track("c", "Album", trackPeak: Math.Pow(10, -6.0 / 20));
        using var service = Service(NormalisationScope.Auto);

        var gain = service.GetTrackGain(a, new TrackGainContext(null, b, [a, b, c], 0));

        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(3, gain.Value.GainDecibels, Precision);
    }

    /// <summary>„Pomijanie tagów ReplayGain” pomija też znaczniki albumu: liczy się pomiar.</summary>
    [Fact]
    public void PomijanieTagowPomijaZnacznikAlbumu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.25, albumGain: 9, trackGain: 4);
        var b = Track("b", "Album", lufs: -20, peak: 0.25, albumGain: 9, trackGain: 4);
        using var service = Service(NormalisationScope.Auto);
        var context = new TrackGainContext(null, b, [a, b], 0);

        Assert.Equal(9, service.GetTrackGain(a, context)!.Value.GainDecibels, Precision);

        service.AlwaysAnalyse = true;
        var gain = service.GetTrackGain(a, context);

        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(AlbumGain, gain.Value.GainDecibels, Precision);
    }

    /// <summary>Ze znacznikami utworów, bez znaczników albumu, album pochodzi z pomiarów.</summary>
    [Fact]
    public void ZnacznikiUtworowBezAlbumowychDajaPomiarAlbumu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5, trackGain: 4);
        var b = Track("b", "Album", lufs: -20, peak: 0.25, trackGain: -3);
        using var service = Service(NormalisationScope.Album);

        var gain = service.GetTrackGain(a, new TrackGainContext(null, b, [a, b], 0));

        Assert.Equal(AlbumGain, gain!.Value.GainDecibels, Precision);
    }

    // ---------- szczyt ----------

    /// <summary>Dodatnie wzmocnienie albumu ogranicza szczyt albumu, nie szczyt granego utworu.</summary>
    [Fact]
    public void WzmocnienieAlbumuOgraniczaSzczytAlbumu()
    {
        var a = Track("a", "Album", lufs: -30, peak: 0.9);
        var b = Track("b", "Album", lufs: -30, peak: 0.25);
        using var service = Service(NormalisationScope.Auto);

        var gain = service.GetTrackGain(b, new TrackGainContext(a, null, [a, b], 1));

        var ceiling = -20 * Math.Log10(0.9) - LoudnessService.PeakHeadroomDb;
        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
        Assert.Equal(ceiling, gain.Value.GainDecibels, Precision);
        Assert.Equal(0 - (12 - ceiling), gain.Value.ProgrammeLevelDecibels, Precision);
    }

    // ---------- album zmierzony w części ----------

    /// <summary>
    /// Do kompletu pomiarów gra wzmocnienie utworu, a brakujące utwory idą do analizy. Po niej
    /// następne otwarcie dostaje wzmocnienie albumu.
    /// </summary>
    [Fact]
    public void AlbumZmierzonyWCzesciGraWzmocnienieUtworuDoKompletu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", seconds: 100);
        using var service = Service(NormalisationScope.Auto, measure: _ => new LoudnessMeasurement(-20, 0.25));
        using var measured = new ManualResetEventSlim();
        service.Measured += (_, _) => measured.Set();
        var context = new TrackGainContext(null, b, [a, b], 0);

        var gain = service.GetTrackGain(a, context);

        Assert.Equal(TrackGainKind.Track, gain!.Value.Kind);
        Assert.Equal(Target + 10, gain.Value.GainDecibels, Precision);

        Assert.True(measured.Wait(TimeSpan.FromSeconds(5)), "brakujący utwór albumu nie trafił do analizy");
        Assert.Equal([b.Path], _measured.ToArray());

        var later = service.GetTrackGain(a, context);
        Assert.Equal(TrackGainKind.Album, later!.Value.Kind);
        Assert.Equal(AlbumGain, later.Value.GainDecibels, Precision);
    }

    /// <summary>Utwór, którego nie da się zmierzyć, nie wstrzymuje albumu: album liczy się bez niego.</summary>
    [Fact]
    public void UtworNieDoZmierzeniaNieWstrzymujeAlbumu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", seconds: 100);
        var c = Track("c", "Album", lufs: -20, peak: 0.25);
        using var service = Service(NormalisationScope.Album, measure: _ => null);
        var context = new TrackGainContext(null, b, [a, b, c], 0);

        Assert.Equal(TrackGainKind.Track, service.GetTrackGain(a, context)!.Value.Kind);
        WaitFor(() => _measured.Contains(b.Path) && service.GetTrackGain(a, context)!.Value.Kind == TrackGainKind.Album);

        Assert.Equal(AlbumGain, service.GetTrackGain(a, context)!.Value.GainDecibels, Precision);
    }

    /// <summary>
    /// Chwilowy błąd odczytu (plik zajęty, udział sieciowy na moment niedostępny) nie wyklucza utworu
    /// z albumu: album zostaje niekompletny, gra wzmocnienie utworu, a następne otwarcie zleca pomiar
    /// ponownie. Inaczej album liczyłby się bez utworu, a po jego późniejszym pomiarze wzmocnienie
    /// albumu by się zmieniło i między utworami jednej płyty byłoby słychać skok.
    /// </summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public void ChwilowyBladOdczytuNieWykluczaUtworuZAlbumu(Type error)
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", seconds: 100);
        var barrier = Track("zapora", null, seconds: 1);
        var failures = 0;

        using var service = Service(NormalisationScope.Album, measure: path =>
        {
            if (path == b.Path && Interlocked.Increment(ref failures) == 1)
                throw (Exception)Activator.CreateInstance(error, "plik chwilowo niedostępny")!;
            return new LoudnessMeasurement(-20, 0.25);
        });
        var context = new TrackGainContext(null, b, [a, b], 0);

        Assert.Equal(TrackGainKind.Track, service.GetTrackGain(a, context)!.Value.Kind);

        // Wątek analizy bierze zadania po kolei: po pomiarze zapory porażka utworu b jest już obsłużona.
        service.RequestEnvelope(barrier.Path);
        WaitFor(() => _measured.Contains(barrier.Path));

        Assert.Equal(TrackGainKind.Track, service.GetTrackGain(a, context)!.Value.Kind);

        WaitFor(() => _measured.Count(path => path == b.Path) == 2);
        WaitFor(() => service.GetTrackGain(a, context)!.Value.Kind == TrackGainKind.Album);
        Assert.Equal(AlbumGain, service.GetTrackGain(a, context)!.Value.GainDecibels, Precision);
    }

    /// <summary>
    /// Błąd treści pliku jest trwały, a usunięty plik i tak nie zagra: utwór wypada z albumu, album
    /// dostaje wzmocnienie z pozostałych utworów, a pomiar nie jest ponawiany.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BladTresciAlboBrakPlikuWykluczaUtworZAlbumu(bool missing)
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", seconds: 100);
        var c = Track("c", "Album", lufs: -20, peak: 0.25);
        if (missing) File.Delete(b.Path);

        using var service = Service(NormalisationScope.Album, measure: path => path != b.Path ? null
            : missing ? throw new FileNotFoundException("Brak pliku.", path) : throw new InvalidDataException("uszkodzona ramka"));
        var context = new TrackGainContext(null, b, [a, b, c], 0);

        Assert.Equal(TrackGainKind.Track, service.GetTrackGain(a, context)!.Value.Kind);
        WaitFor(() => _measured.Contains(b.Path) && service.GetTrackGain(a, context)!.Value.Kind == TrackGainKind.Album);

        Assert.Equal(AlbumGain, service.GetTrackGain(a, context)!.Value.GainDecibels, Precision);
        Thread.Sleep(200);
        Assert.Equal(1, _measured.Count(path => path == b.Path));
    }

    /// <summary>
    /// Odświeżenie z wątku interfejsu nie czyta znaczników sąsiadów: sąsiad bez odczytanych metadanych
    /// nie jest z tego samego albumu, a odczyt idzie do wątku analizy. Po nim wynik jest taki jak
    /// przy otwarciu utworu.
    /// </summary>
    [Theory]
    [InlineData(NormalisationScope.Auto)]
    [InlineData(NormalisationScope.Album)]
    public void OdswiezenieNieCzytaZnacznikowSasiadow(NormalisationScope scope)
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5, seconds: 0.5);
        var unread = Unread("b", album: "Album", record: (-20, 0.25));
        using var gate = new ManualResetEventSlim();
        var blocker = Track("blokada", null, seconds: 1);

        using var service = Service(scope, measure: path =>
        {
            if (path == blocker.Path) gate.Wait(TimeSpan.FromSeconds(10));
            return new LoudnessMeasurement(-20, 0.25);
        });

        service.GetTrackGain(blocker);
        WaitFor(() => _measured.Contains(blocker.Path));

        var refresh = new TrackGainContext(null, unread, [a, unread], 0) { MayReadTags = false };
        var gain = service.GetTrackGain(a, refresh);

        Assert.Null(unread.Metadata);
        Assert.Equal(TrackGainKind.Track, gain!.Value.Kind);

        gate.Set();
        WaitFor(() => unread.Metadata is not null);

        var opening = new TrackGainContext(null, unread, [a, unread], 0);
        var expected = service.GetTrackGain(a, opening);
        Assert.Equal(TrackGainKind.Album, expected!.Value.Kind);
        Assert.Equal(expected, service.GetTrackGain(a, refresh));
    }

    /// <summary>
    /// Znaczniki reszty albumu czyta wątek analizy: wątek pytający nie otwiera plików poza sąsiadami,
    /// a utwór o nieodczytanych znacznikach zostawia album niekompletnym.
    /// </summary>
    [Fact]
    public void ZnacznikiResztyAlbumuCzytaWatekAnalizy()
    {
        // Długość jak w pliku wzorcowym WAV (pół sekundy), żeby wagi były równe.
        var a = Track("a", "Album", lufs: -10, peak: 0.5, seconds: 0.5);
        var b = Track("b", "Album", lufs: -20, peak: 0.25, seconds: 0.5);
        var unread = Unread("c", album: "Album", record: (-20, 0.25));
        using var gate = new ManualResetEventSlim();
        var blocker = Track("blokada", null, seconds: 1);

        using var service = Service(NormalisationScope.Album, measure: path =>
        {
            if (path == blocker.Path) gate.Wait(TimeSpan.FromSeconds(10));
            return new LoudnessMeasurement(-20, 0.25);
        });

        // Wątek analizy zajęty innym plikiem: nic z albumu nie zdąży się w tle odczytać.
        service.GetTrackGain(blocker);
        WaitFor(() => _measured.Contains(blocker.Path));

        var context = new TrackGainContext(null, null, [a, b, unread], 0);
        var gain = service.GetTrackGain(a, context);

        Assert.Null(unread.Metadata);
        Assert.Equal(TrackGainKind.Track, gain!.Value.Kind);

        gate.Set();
        WaitFor(() => unread.Metadata is not null && service.GetTrackGain(a, context)!.Value.Kind == TrackGainKind.Album);

        // Trzy utwory po równym czasie: −10, −20 i −20 LUFS.
        var expected = Target - 10 * Math.Log10((0.1 + 0.01 + 0.01) / 3);
        Assert.Equal(expected, service.GetTrackGain(a, context)!.Value.GainDecibels, 0.05);
    }

    /// <summary>Sąsiad o nieodczytanych znacznikach jest czytany od razu: tryb „Automatyczny” go potrzebuje.</summary>
    [Fact]
    public void SasiadBezZnacznikowJestCzytanyOdRazu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var unread = Unread("b", album: "Album", record: (-20, 0.25));
        using var service = Service(NormalisationScope.Auto);

        var gain = service.GetTrackGain(a, new TrackGainContext(null, unread, [a, unread], 0));

        Assert.NotNull(unread.Metadata);
        Assert.Equal(TrackGainKind.Album, gain!.Value.Kind);
    }

    /// <summary>Album do pomiaru to najwyżej 200 kolejnych wpisów kolejki wokół granego.</summary>
    [Fact]
    public void AlbumMaNajwyzej200Wpisow()
    {
        var queue = Enumerable.Range(0, 260).Select(i => Track("t" + i, "Album", seconds: 10)).ToArray();
        using var service = Service(NormalisationScope.Album, measure: _ => null);

        var gain = service.GetTrackGain(queue[130], new TrackGainContext(null, null, queue, 130));

        Assert.Null(gain);
        WaitFor(() => _measured.Count >= 200);
        Thread.Sleep(200);
        Assert.Equal(200, _measured.Distinct().Count());
    }

    /// <summary>Zmiana trybu działa od następnego odczytu wzmocnienia.</summary>
    [Fact]
    public void ZmianaTrybuDzialaOdNastepnegoOdczytu()
    {
        var a = Track("a", "Album", lufs: -10, peak: 0.5);
        var b = Track("b", "Album", lufs: -20, peak: 0.25);
        using var service = Service(NormalisationScope.Auto);
        var context = new TrackGainContext(null, b, [a, b], 0);

        Assert.Equal(TrackGainKind.Album, service.GetTrackGain(a, context)!.Value.Kind);
        service.Scope = NormalisationScope.Track;
        Assert.Equal(TrackGainKind.Track, service.GetTrackGain(a, context)!.Value.Kind);
    }

    // ---------- pomocnicze ----------

    private static LoudnessRecord Record(double lufs, double? peak) => new()
    {
        Path = "x",
        Length = 1,
        ModifiedTicks = 1,
        IntegratedLufs = lufs,
        SamplePeak = peak,
    };

    private static void WaitFor(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "warunek nie został spełniony");

    /// <summary>Wpis z odczytanymi znacznikami i, gdy podano głośność, z pomiarem w pamięci podręcznej.</summary>
    private QueueEntry Track(string name, string? album, double? lufs = null, double? peak = null, double seconds = 100,
        double? albumGain = null, double? albumPeak = null, double? trackGain = null, double? trackPeak = null)
    {
        var path = Path.Combine(_directory, name + ".flac");
        File.WriteAllBytes(path, new byte[64]);
        if (lufs is not null) AddRecord(path, lufs.Value, peak);

        return new QueueEntry
        {
            Path = path,
            Metadata = new TrackMetadata
            {
                Path = path,
                Title = name,
                Album = album,
                Duration = TimeSpan.FromSeconds(seconds),
                ReplayGainAlbum = albumGain,
                ReplayGainAlbumPeak = albumPeak,
                ReplayGainTrack = trackGain,
                ReplayGainTrackPeak = trackPeak,
            },
        };
    }

    /// <summary>Prawdziwy plik WAV ze znacznikiem albumu, którego metadanych jeszcze nie odczytano.</summary>
    private QueueEntry Unread(string name, string album, (double Lufs, double Peak) record)
    {
        var path = Path.Combine(_directory, name + ".wav");
        File.WriteAllBytes(path, TestAudioFiles.Seed("wav"));

        var tags = new ATL.Track(path) { Album = album, Title = name };
        Assert.True(tags.Save(), "nie zapisano znaczników");

        AddRecord(path, record.Lufs, record.Peak);
        return new QueueEntry { Path = path };
    }

    private void AddRecord(string path, double lufs, double? peak)
    {
        var info = new FileInfo(path);
        var record = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["length"] = info.Length,
            ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
            ["integratedLufs"] = lufs,
        };
        if (peak is not null) record["samplePeak"] = peak;
        _records.Add(record);
    }

    private LoudnessService Service(NormalisationScope scope, Func<string, LoudnessMeasurement?>? measure = null)
    {
        var cache = Path.Combine(_directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(_records));

        var service = new LoudnessService(cache, (path, _) =>
        {
            _measured.Enqueue(path);
            return measure?.Invoke(path);
        })
        {
            TargetLufs = Target,
            Scope = scope,
        };
        return service;
    }
}
