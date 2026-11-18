using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Cewka.Audio.Streaming;
using Cewka.Platform;

namespace Cewka.App.Radio;

/// <summary>Logo stacji w dwóch rozmiarach: do listy (64 px) i do pola w oknie (512 px), z plikiem tego drugiego.</summary>
public sealed class RadioLogo(Bitmap list, Bitmap stage, string? stagePath) : IDisposable
{
    public Bitmap List { get; } = list;

    public Bitmap Stage { get; } = stage;

    /// <summary>
    /// Plik PNG w pamięci podręcznej; z niego korzysta panel multimediów systemu. Null, gdy obrazu
    /// nie udało się zapisać na dysk.
    /// </summary>
    public string? StagePath { get; } = stagePath;

    public void Dispose()
    {
        List.Dispose();
        Stage.Dispose();
    }
}

/// <summary>
/// Logo stacji pobierane w trakcie działania z adresów z listy (paczka nie zawiera cudzych logo), tylko po HTTPS,
/// z limitem bajtów i wymiarów. Wynik trafia do pamięci podręcznej na dysku: PNG 64 i 512 px pod kluczem
/// SHA-256 adresu, ważny 7 dni; porażki zapamiętane osobno (brak obrazu 7 dni, błąd sieci i serwera 1 dzień),
/// żeby martwy adres nie był odpytywany przy każdym wierszu. Uszkodzony obraz kończy się obrazem zastępczym,
/// nigdy wyjątkiem.
///
/// <para>Adresy stacji są próbowane po kolei: pierwszy to serwer z kopiami logo, następne to źródła
/// (serwer nadawcy albo Wikimedia Commons). Serwer, który nie odpowiada albo prosi o przerwę, jest
/// pomijany w całości na czas przerwy, a z jednego serwera idą najwyżej dwa pobrania naraz.</para>
/// </summary>
public sealed class RadioLogoService : IDisposable
{
    public const int ListSide = 64;
    public const int StageSide = 512;
    public const int MaxBytes = ImageFetcher.DefaultMaxBytes;
    public const int MaxDimension = 4096;
    public const long MaxCacheBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Jak długo logo z dysku wystarcza bez ponownego pobrania. Krótko, jak w pamięci podręcznej przeglądarki:
    /// kopia ma być tymczasowa, a zmienione albo wycofane logo nadawcy ma szybko zastąpić starą.
    /// </summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    public static readonly TimeSpan MissingFor = TimeSpan.FromDays(7);
    public static readonly TimeSpan UnavailableFor = TimeSpan.FromDays(1);

    /// <summary>
    /// Przerwa po odpowiedzi „za dużo żądań”. Serwery ograniczające tempo (Wikimedia Commons przy kilku
    /// logo pobranych z rzędu) oddają plik po kilku minutach, więc dłuższe czekanie zostawia stację bez
    /// logo bez powodu, a krótsze dokłada żądań serwerowi, który właśnie prosił o mniej.
    /// </summary>
    public static readonly TimeSpan ThrottledFor = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Jak długo pomijany jest serwer, który nie odpowiedział (brak połączenia, przekroczony czas, błąd 5xx).
    /// Bez tego każde logo z tego serwera czekałoby na przekroczenie czasu, zanim program sięgnie po adres
    /// zapasowy. Po przerwie serwer jest pytany od nowa.
    /// </summary>
    public static readonly TimeSpan HostDownFor = TimeSpan.FromMinutes(5);

    /// <summary>Najwięcej pobrań naraz z jednego serwera; tyle dopuszczają zasady Wikimedii dla plików.</summary>
    public const int MaxPerHost = 2;

    private static readonly TimeSpan ShortestPause = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestPause = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly Func<Uri, CancellationToken, Task<ImageFetchResult>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(3);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostPausedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Fetched>>> _loading = new();
    private readonly IDisposable? _owned;
    private CancellationTokenSource _cancel = new();

    public RadioLogoService(
        string directory, Func<Uri, CancellationToken, Task<ImageFetchResult>> fetch,
        Func<DateTimeOffset>? clock = null, IDisposable? owned = null)
    {
        _directory = directory;
        _fetch = fetch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _owned = owned;
    }

    /// <summary>Wersja produkcyjna: katalog w pamięci podręcznej programu i prawdziwe pobieranie.</summary>
    public static RadioLogoService CreateDefault()
    {
        var fetcher = new ImageFetcher();
        return new RadioLogoService(
            Path.Combine(AppPaths.CacheDirectory, "radio-logos"),
            (url, ct) => fetcher.FetchAsync(url, MaxBytes, ImageFetcher.DefaultTimeout, ct),
            owned: fetcher);
    }

    public string Directory => _directory;

    /// <summary>Logo z pamięci podręcznej bez żadnego połączenia; null, gdy go tam nie ma albo wygasło.</summary>
    public RadioLogo? TryGetCached(RadioStation station)
    {
        foreach (var uri in Candidates(station))
        {
            if (ReadCache(Key(uri)) is { Kind: CacheKind.Logo } hit)
                return hit.Logo;
        }

        return null;
    }

    /// <summary>
    /// Logo stacji: z pamięci podręcznej albo z sieci, pierwsze z adresów, które się powiodło. Null oznacza
    /// obraz zastępczy. Anulowanie przez <paramref name="ct"/> zgłasza wyjątek i nic nie zapisuje.
    /// </summary>
    public async Task<RadioLogo?> GetAsync(RadioStation station, CancellationToken ct = default)
    {
        foreach (var uri in Candidates(station))
        {
            ct.ThrowIfCancellationRequested();

            // Zapisane logo czytane jest od razu: zadanie pobrania zdekodowałoby je tylko po to,
            // żeby zwolnić, a to zwykła droga przy każdym wyborze stacji.
            var key = Key(uri);
            var cached = await Task.Run(() => ReadCache(key), ct).ConfigureAwait(false);
            if (cached.Kind == CacheKind.Logo) return cached.Logo;
            if (cached.Kind == CacheKind.RecentFailure) continue;

            var fetched = await LoadAsync(uri, ct).ConfigureAwait(false);
            if (fetched.Stored && ReadCache(key) is { Kind: CacheKind.Logo } hit) return hit.Logo;
            if (fetched.Image?.Full() is { } logo) return logo;
        }

        return null;
    }

    /// <summary>
    /// Samo logo wiersza listy (64 px). Lista pyta o nie dla każdego widocznego wiersza, a pełne logo
    /// niesie też obraz 512 px, czyli megabajt pamięci i dekodowanie, z których wiersz nie korzysta.
    /// Z pamięci podręcznej czytany jest więc tylko mały plik; pobranie idzie zwykłą drogą.
    /// </summary>
    public async Task<Bitmap?> GetListAsync(RadioStation station, CancellationToken ct = default)
    {
        foreach (var uri in Candidates(station))
        {
            ct.ThrowIfCancellationRequested();

            var key = Key(uri);
            var cached = await Task.Run(() => ReadListCache(key), ct).ConfigureAwait(false);
            if (cached.Kind == CacheKind.RecentFailure) continue;
            if (cached.List is not null) return cached.List;

            var fetched = await LoadAsync(uri, ct).ConfigureAwait(false);
            if (fetched.Stored && ReadListCache(key).List is { } list) return list;
            if (fetched.Image?.List() is { } decoded) return decoded;
        }

        return null;
    }

    /// <summary>
    /// Wołane po zdekodowaniu pobranego obrazu, z obrazami w pamięci. Program go nie ustawia; testy
    /// sprawdzają nim, co dzieje się z tymi obrazami dalej.
    /// </summary>
    internal Action<RadioLogo>? Rendered { get; set; }

    private static IEnumerable<Uri> Candidates(RadioStation station)
    {
        foreach (var text in station.Logos)
        {
            if (RadioStation.IsLogoAddress(text)) yield return new Uri(text, UriKind.Absolute);
        }
    }

    /// <summary>
    /// Przerywa pobrania w toku i w kolejce, np. po wyłączeniu logo w ustawieniach. Kolejne żądania działają
    /// normalnie; przerwane nic nie zapisują do pamięci podręcznej.
    /// </summary>
    public void CancelPending()
    {
        var old = Interlocked.Exchange(ref _cancel, new CancellationTokenSource());
        old.Cancel();
    }

    /// <summary>
    /// Wynik pobrania, wspólny dla wszystkich, którzy czekali na ten sam adres, dlatego bez obrazów:
    /// obrazu ze wspólnego wyniku nikt nie mógłby zwolnić, nie odbierając go pozostałym, a wiersz
    /// listy zostawiał w nim duży obraz bez właściciela. Logo zapisane na dysku każdy czyta stamtąd,
    /// a gdy zapis się nie udał albo wpis zdążył zniknąć, dekoduje z <see cref="Image"/>; zawsze
    /// we własnym egzemplarzu.
    /// </summary>
    private readonly record struct Fetched(bool Stored, LogoImage? Image);

    /// <summary>Sprawdzone dane pobranego obrazu, z których pytający dekoduje potrzebny mu rozmiar.</summary>
    private sealed class LogoImage(byte[] image, bool wide)
    {
        public Bitmap? List() => TryDecode(ListSide);

        public RadioLogo? Full()
        {
            if (TryDecode(ListSide) is not { } list) return null;
            if (TryDecode(StageSide) is { } stage) return new RadioLogo(list, stage, null);

            list.Dispose();
            return null;
        }

        private Bitmap? TryDecode(int side)
        {
            try
            {
                using var input = new MemoryStream(image);
                return Decode(input, wide, side);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return null;
            }
        }
    }

    private Task<Fetched> LoadAsync(Uri uri, CancellationToken ct)
    {
        var key = Key(uri);
        var token = _cancel.Token;
        var lazy = _loading.GetOrAdd(key, _ => new Lazy<Task<Fetched>>(() => Task.Run(() => FetchAndStoreAsync(uri, key, token))));
        if (lazy.IsValueCreated && lazy.Value.IsCompleted)
        {
            // Wynik skończonego pobrania nie jest już współdzielony: usunięcie wpisu przez ContinueWith może się spóźnić,
            // a kolejne żądanie ma ocenić pamięć podręczną od nowa (np. po wygaśnięciu wpisu).
            _loading.TryRemove(new KeyValuePair<string, Lazy<Task<Fetched>>>(key, lazy));
            lazy = _loading.GetOrAdd(key, _ => new Lazy<Task<Fetched>>(() => Task.Run(() => FetchAndStoreAsync(uri, key, token))));
        }

        var task = lazy.Value;
        task.ContinueWith(_ => _loading.TryRemove(key, out Lazy<Task<Fetched>>? _), TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    /// <summary>Liczba zadań pobrania uruchomionych od początku; dla testów.</summary>
    internal int FetchesStarted => Volatile.Read(ref _fetchesStarted);

    private int _fetchesStarted;

    /// <summary>Wołane zaraz po zapisaniu logo na dysku. Program go nie ustawia; testy psują nim wpis.</summary>
    internal Action? Stored { get; set; }

    private async Task<Fetched> FetchAndStoreAsync(Uri uri, string key, CancellationToken token)
    {
        Interlocked.Increment(ref _fetchesStarted);

        var cached = ReadCache(key);
        if (cached.Kind == CacheKind.Logo)
        {
            // Zapisało je pobranie, które skończyło się między pytaniem a startem tego zadania.
            cached.Logo!.Dispose();
            return new Fetched(true, null);
        }

        if (cached.Kind == CacheKind.RecentFailure) return default;

        // Serwer w przerwie nie dostaje żądania i nie zostaje po nim zapamiętana porażka adresu:
        // do końca przerwy działa adres zapasowy, a po niej ten sam adres jest pytany od nowa.
        var host = uri.IdnHost;
        if (IsPaused(host)) return default;

        // Najpierw kolejka serwera, potem wspólna: pobranie czekające na swój serwer nie zajmuje
        // miejsca pobraniom z innych serwerów.
        var hostGate = _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(MaxPerHost));
        await hostGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();

                // Przerwa mogła zacząć się, gdy to pobranie czekało w kolejce za tym, które ją wywołało.
                if (IsPaused(host)) return default;

                ImageFetchResult result;
                try
                {
                    result = await _fetch(uri, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    result = new ImageFetchResult(null, ImageFetchProblem.Unavailable);
                }

                if (!result.Ok)
                {
                    var pause = PauseFor(result);
                    if (pause is { } time)
                    {
                        // Dwa pobrania z jednego serwera kończą się w dowolnej kolejności, a obowiązuje
                        // dłuższa z przerw: późniejsza porażka nie skraca tej, o którą poprosił serwer.
                        var until = _clock() + time;
                        _hostPausedUntil.AddOrUpdate(host, until, (_, current) => current > until ? current : until);
                    }

                    WriteFailure(key, result.Problem switch
                    {
                        ImageFetchProblem.Throttled => pause ?? ThrottledFor,
                        ImageFetchProblem.Unavailable => UnavailableFor,
                        _ => MissingFor,
                    });
                    return default;
                }

                var rendered = Render(result.Bytes!, key);
                if (rendered.Stored)
                {
                    Prune();
                    Stored?.Invoke();
                }
                else if (rendered.Image is null)
                {
                    WriteFailure(key, MissingFor);
                }

                return rendered;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            hostGate.Release();
        }
    }

    /// <summary>
    /// Przerwa dla całego serwera po nieudanym pobraniu: tyle, ile podał serwer (nie mniej niż minuta
    /// i nie więcej niż doba), a bez podanego czasu zależnie od przyczyny. Null, gdy porażka dotyczy
    /// samego pliku, a nie serwera.
    /// </summary>
    internal static TimeSpan? PauseFor(ImageFetchResult result)
    {
        if (result.Problem is not (ImageFetchProblem.Throttled or ImageFetchProblem.Unavailable)) return null;

        if (result.RetryAfter is { } requested)
            return requested < ShortestPause ? ShortestPause : requested > LongestPause ? LongestPause : requested;

        return result.Problem == ImageFetchProblem.Throttled ? ThrottledFor : HostDownFor;
    }

    private bool IsPaused(string host) =>
        _hostPausedUntil.TryGetValue(host, out var until) && _clock() < until;

    /// <summary>
    /// Sprawdza format i wymiary w nagłówku, dopiero potem dekoduje; zapisuje oba rozmiary jako PNG.
    /// Wynik pusty oznacza zły obraz. Nieudany zapis na dysk (brak miejsca, plik zajęty przez drugą
    /// kopię programu) nim nie jest: logo pokazuje się z pamięci, a pobranie ponowi się przy następnej
    /// okazji, zamiast zostać zapamiętane na tydzień jako brak obrazu. Obrazy z dekodowania służą
    /// tylko zapisowi i są tu zwalniane.
    /// </summary>
    private Fetched Render(byte[] data, string key)
    {
        Bitmap? list = null;
        Bitmap? stage = null;
        bool wide;

        try
        {
            var format = LogoSniffer.Detect(data);
            if (format == LogoFormat.Ico)
            {
                data = LogoSniffer.IcoLargestPng(data) ?? [];
                format = LogoSniffer.Detect(data);
            }

            // SVG i nieznane formaty dostają obraz zastępczy: dekodera wektorów w programie nie ma.
            if (format is LogoFormat.Unknown or LogoFormat.Svg) return default;

            if (LogoSniffer.Dimensions(data, format) is not { } size
                || size.Width < 1 || size.Height < 1
                || size.Width > MaxDimension || size.Height > MaxDimension)
                return default;

            wide = size.Width >= size.Height;
            using var input = new MemoryStream(data);
            list = Decode(input, wide, ListSide);
            input.Position = 0;
            stage = Decode(input, wide, StageSide);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Uszkodzony albo nietypowy obraz: dekodery rzucają przy tym różnymi wyjątkami.
            list?.Dispose();
            stage?.Dispose();
            return default;
        }

        using var decoded = new RadioLogo(list, stage, null);
        Rendered?.Invoke(decoded);

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            SavePng(list, Path.Combine(_directory, key + ".list.png"));
            SavePng(stage, Path.Combine(_directory, key + ".stage.png"));
            File.WriteAllText(Path.Combine(_directory, key + ".meta"), "ok " + Stamp(_clock()), Encoding.ASCII);
            return new Fetched(true, new LogoImage(data, wide));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cewka] logo stacji nie zapisało się w pamięci podręcznej: {e.Message}");
            return new Fetched(false, new LogoImage(data, wide));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Koder odmówił obrazu: dla słuchacza to to samo co obraz, którego nie dało się odczytać.
            return default;
        }
    }

    private static Bitmap Decode(Stream input, bool wide, int side) => wide
        ? Bitmap.DecodeToWidth(input, side, BitmapInterpolationMode.HighQuality)
        : Bitmap.DecodeToHeight(input, side, BitmapInterpolationMode.HighQuality);

    private static void SavePng(Bitmap bitmap, string path)
    {
        // Nazwa tymczasowa własna dla każdego zapisu: dwie kopie programu pobierają to samo logo.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var file = File.Create(temp))
                bitmap.Save(file, new PngBitmapEncoderOptions());
            File.Move(temp, path, true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            throw;
        }
    }

    // ---------- Pamięć podręczna ----------

    private enum CacheKind { None, Logo, RecentFailure }

    private readonly record struct CacheEntry(CacheKind Kind, RadioLogo? Logo = null);

    /// <summary>Stan wpisu bez dekodowania obrazów: świeże logo, niedawna porażka albo nic.</summary>
    private CacheKind ReadCacheKind(string key)
    {
        var meta = Path.Combine(_directory, key + ".meta");
        if (!File.Exists(meta)) return CacheKind.None;

        var parts = File.ReadAllText(meta).Split(' ', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return CacheKind.None;

        var moment = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (parts[0] == "fail")
            return _clock() < moment ? CacheKind.RecentFailure : CacheKind.None;

        return parts[0] == "ok" && _clock() - moment <= Fresh ? CacheKind.Logo : CacheKind.None;
    }

    /// <summary>Mały obraz z pamięci podręcznej, bez dużego.</summary>
    private (CacheKind Kind, Bitmap? List) ReadListCache(string key)
    {
        try
        {
            var kind = ReadCacheKind(key);
            if (kind != CacheKind.Logo) return (kind, null);

            using var listFile = File.OpenRead(Path.Combine(_directory, key + ".list.png"));
            return (kind, new Bitmap(listFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidOperationException or NotSupportedException)
        {
            return default;
        }
    }

    private CacheEntry ReadCache(string key)
    {
        try
        {
            var kind = ReadCacheKind(key);
            if (kind != CacheKind.Logo) return new CacheEntry(kind);

            var stagePath = Path.Combine(_directory, key + ".stage.png");
            using var listFile = File.OpenRead(Path.Combine(_directory, key + ".list.png"));
            using var stageFile = File.OpenRead(stagePath);
            return new CacheEntry(CacheKind.Logo, new RadioLogo(new Bitmap(listFile), new Bitmap(stageFile), stagePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidOperationException or NotSupportedException)
        {
            // Plik uszkodzony albo wycofany w trakcie odczytu: wpis jest jak nieobecny i zostanie pobrany od nowa.
            return default;
        }
    }

    private void WriteFailure(string key, TimeSpan duration)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, key + ".meta"), "fail " + Stamp(_clock() + duration), Encoding.ASCII);
            File.Delete(Path.Combine(_directory, key + ".list.png"));
            File.Delete(Path.Combine(_directory, key + ".stage.png"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Key(Uri uri) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();

    /// <summary>Łączny rozmiar plików w pamięci podręcznej, w bajtach.</summary>
    public long SizeBytes()
    {
        try
        {
            return System.IO.Directory.Exists(_directory)
                ? new DirectoryInfo(_directory).EnumerateFiles().Sum(f => f.Length)
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Usuwa wszystkie logo z dysku; kolejne wyświetlenie pobierze je od nowa.</summary>
    public void Clear()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;

            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
                file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Usuwa porzucone pliki tymczasowe, a po przekroczeniu limitu najstarsze wpisy, aż rozmiar
    /// spadnie poniżej limitu.
    /// </summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles().ToList();

            // Plik tymczasowy zostaje po zapisie przerwanym zamknięciem programu; świeży może
            // należeć do zapisu, który właśnie trwa, także w drugiej kopii programu.
            foreach (var orphan in files.Where(f => f.Extension == ".tmp" && DateTime.UtcNow - f.LastWriteTimeUtc > OrphanAge).ToList())
            {
                orphan.Delete();
                files.Remove(orphan);
            }

            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                total -= file.Length;
                file.Delete();
                if (total <= MaxCacheBytes * 0.8) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _owned?.Dispose();
    }
}
