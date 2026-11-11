using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Cewka.Audio.Streaming;
using Cewka.Platform;

namespace Cewka.App.Radio;

/// <summary>Logo stacji w dwóch rozmiarach: do listy (64 px) i do pola w oknie (512 px), z plikiem tego drugiego.</summary>
public sealed class RadioLogo(Bitmap list, Bitmap stage, string stagePath) : IDisposable
{
    public Bitmap List { get; } = list;

    public Bitmap Stage { get; } = stage;

    /// <summary>Plik PNG w pamięci podręcznej; z niego korzysta panel multimediów systemu.</summary>
    public string StagePath { get; } = stagePath;

    public void Dispose()
    {
        List.Dispose();
        Stage.Dispose();
    }
}

/// <summary>
/// Logo stacji pobierane w trakcie działania z adresów z listy (paczka nie zawiera cudzych logo), tylko po HTTPS,
/// z limitem bajtów i wymiarów. Wynik trafia do pamięci podręcznej na dysku: PNG 64 i 512 px pod kluczem
/// SHA-256 adresu, ważny 30 dni; porażki zapamiętane krócej (brak obrazu 7 dni, błąd sieci i serwera 1 dzień),
/// żeby martwy adres nie był odpytywany przy każdym wierszu. Uszkodzony obraz kończy się obrazem zastępczym,
/// nigdy wyjątkiem.
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

    private readonly string _directory;
    private readonly Func<Uri, CancellationToken, Task<ImageFetchResult>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(3);
    private readonly ConcurrentDictionary<string, Lazy<Task<RadioLogo?>>> _loading = new();
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
            var logo = await LoadAsync(uri, ct).ConfigureAwait(false);
            if (logo is not null) return logo;
        }

        return null;
    }

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

    private Task<RadioLogo?> LoadAsync(Uri uri, CancellationToken ct)
    {
        var key = Key(uri);
        var token = _cancel.Token;
        var lazy = _loading.GetOrAdd(key, _ => new Lazy<Task<RadioLogo?>>(() => Task.Run(() => FetchAndStoreAsync(uri, key, token))));
        if (lazy.IsValueCreated && lazy.Value.IsCompleted)
        {
            // Wynik skończonego pobrania nie jest już współdzielony: usunięcie wpisu przez ContinueWith może się spóźnić,
            // a kolejne żądanie ma ocenić pamięć podręczną od nowa (np. po wygaśnięciu wpisu).
            _loading.TryRemove(new KeyValuePair<string, Lazy<Task<RadioLogo?>>>(key, lazy));
            lazy = _loading.GetOrAdd(key, _ => new Lazy<Task<RadioLogo?>>(() => Task.Run(() => FetchAndStoreAsync(uri, key, token))));
        }

        var task = lazy.Value;
        task.ContinueWith(_ => _loading.TryRemove(key, out Lazy<Task<RadioLogo?>>? _), TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private async Task<RadioLogo?> FetchAndStoreAsync(Uri uri, string key, CancellationToken token)
    {
        var cached = ReadCache(key);
        if (cached.Kind == CacheKind.Logo) return cached.Logo;
        if (cached.Kind == CacheKind.RecentFailure) return null;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
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
                WriteFailure(key, result.Problem == ImageFetchProblem.Unavailable ? UnavailableFor : MissingFor);
                return null;
            }

            var logo = Render(result.Bytes!, key);
            if (logo is null)
            {
                WriteFailure(key, MissingFor);
                return null;
            }

            Prune();
            return logo;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sprawdza format i wymiary w nagłówku, dopiero potem dekoduje; zapisuje oba rozmiary jako PNG.</summary>
    private RadioLogo? Render(byte[] data, string key)
    {
        try
        {
            var format = LogoSniffer.Detect(data);
            if (format == LogoFormat.Ico)
            {
                data = LogoSniffer.IcoLargestPng(data) ?? [];
                format = LogoSniffer.Detect(data);
            }

            // SVG i nieznane formaty dostają obraz zastępczy: dekodera wektorów w programie nie ma.
            if (format is LogoFormat.Unknown or LogoFormat.Svg) return null;

            if (LogoSniffer.Dimensions(data, format) is not { } size
                || size.Width < 1 || size.Height < 1
                || size.Width > MaxDimension || size.Height > MaxDimension)
                return null;

            var wide = size.Width >= size.Height;
            using var input = new MemoryStream(data);
            var list = Decode(input, wide, ListSide);
            input.Position = 0;
            var stage = Decode(input, wide, StageSide);

            System.IO.Directory.CreateDirectory(_directory);
            SavePng(list, Path.Combine(_directory, key + ".list.png"));
            var stagePath = Path.Combine(_directory, key + ".stage.png");
            SavePng(stage, stagePath);
            File.WriteAllText(Path.Combine(_directory, key + ".meta"), "ok " + Stamp(_clock()), Encoding.ASCII);
            return new RadioLogo(list, stage, stagePath);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Uszkodzony albo nietypowy obraz: dekodery rzucają przy tym różnymi wyjątkami.
            return null;
        }
    }

    private static Bitmap Decode(Stream input, bool wide, int side) => wide
        ? Bitmap.DecodeToWidth(input, side, BitmapInterpolationMode.HighQuality)
        : Bitmap.DecodeToHeight(input, side, BitmapInterpolationMode.HighQuality);

    private static void SavePng(Bitmap bitmap, string path)
    {
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
            bitmap.Save(file, new PngBitmapEncoderOptions());
        File.Move(temp, path, true);
    }

    // ---------- Pamięć podręczna ----------

    private enum CacheKind { None, Logo, RecentFailure }

    private readonly record struct CacheEntry(CacheKind Kind, RadioLogo? Logo = null);

    private CacheEntry ReadCache(string key)
    {
        try
        {
            var meta = Path.Combine(_directory, key + ".meta");
            if (!File.Exists(meta)) return default;

            var parts = File.ReadAllText(meta).Split(' ', 2);
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return default;

            var moment = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (parts[0] == "fail")
                return _clock() < moment ? new CacheEntry(CacheKind.RecentFailure) : default;

            if (parts[0] != "ok" || _clock() - moment > Fresh) return default;

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

    /// <summary>Po przekroczeniu limitu usuwa najstarsze wpisy, aż rozmiar spadnie poniżej limitu.</summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles().ToList();
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
