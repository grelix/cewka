using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Cewka.Audio.Cue;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;

namespace Cewka.App.Services;

/// <summary>Jedna pozycja listy odtwarzania, w postaci gotowej do zapisania.</summary>
/// <param name="DurationSeconds">Czas trwania; zero albo mniej, gdy nieznany.</param>
public sealed record PlaylistEntry(string Path, string Title, string? Artist, double DurationSeconds);

/// <summary>
/// Wynik odczytu listy: ścieżki, które istnieją, liczba tych, których zabrakło, i liczba
/// adresów strumieni internetowych, których odtwarzacz plików nie otwiera.
/// </summary>
public sealed record PlaylistLoad(IReadOnlyList<string> Paths, int Missing, int Streams = 0)
{
    /// <summary>Pozycje do kolejki w kolejności listy: arkusz CUE z listy rozwinięty na swoje utwory.</summary>
    public IReadOnlyList<QueueItem> Items { get; init; } = Paths.Select(path => new QueueItem(path)).ToList();
}

/// <summary>
/// Zapisuje i czyta kolejkę jako listę M3U.
///
/// <para>M3U, bo czyta go każdy odtwarzacz.</para>
///
/// <para>Zapis zawsze w UTF-8: <c>.m3u8</c> bez znacznika kolejności bajtów,
/// tak jak przyjęło się dla tego rozszerzenia, a <c>.m3u</c> ze znacznikiem, bo starsze programy
/// czytają takie pliki w stronie kodowej systemu, dopóki znacznik nie powie inaczej. Przy odczycie
/// <c>.m3u</c> bez znacznika i niebędący poprawnym UTF-8 jest czytany w stronie kodowej systemu
/// (Winamp, Windows Media Player i starsze foobar2000 zapisywały tak polskie nazwy).</para>
/// </summary>
public static partial class PlaylistFile
{
    /// <summary>Rozszerzenia przyjmowane przy odczycie i proponowane przy zapisie.</summary>
    public static readonly string[] Extensions = [".m3u8", ".m3u"];

    /// <summary>Bez znacznika kolejności bajtów — inaczej pierwsza linia przestaje być „#EXTM3U".</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly UTF8Encoding Utf8WithMarker = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Save(string path, IEnumerable<PlaylistEntry> entries)
    {
        var katalog = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var text = new StringBuilder();

        text.Append("#EXTM3U\n");

        foreach (var entry in entries)
        {
            // −1 to w M3U umówiony zapis nieznanego czasu; zero znaczyłoby utwór pusty.
            var seconds = entry.DurationSeconds > 0 ? (int)Math.Round(entry.DurationSeconds) : -1;
            var label = string.IsNullOrWhiteSpace(entry.Artist)
                ? entry.Title
                : $"{entry.Artist} - {entry.Title}";

            // Opis pozycji jest tym, co inne programy pokazują na liście, dopóki nie otworzą
            // samego pliku. Znaki nowej linii w tagu rozerwałyby plik na dwie pozycje.
            text.Append("#EXTINF:").Append(seconds).Append(',')
                .Append(label.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');

            text.Append(Relative(entry.Path, katalog)).Append('\n');
        }

        var m3u = string.Equals(Path.GetExtension(path), ".m3u", StringComparison.OrdinalIgnoreCase);
        AtomicFile.Write(path, text.ToString(), m3u ? Utf8WithMarker : Utf8);
    }

    /// <summary>Więcej wpisów jest obcinane: każdy to wiersz kolejki i odczyt znaczników.</summary>
    internal const int MaxEntries = 100_000;

    /// <summary>Większa lista idzie drogą błędu odczytu, zamiast trafić w całości do pamięci.</summary>
    internal const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Ścieżka lokalna albo na tym samym serwerze i udziale co sama lista. Samo sprawdzenie
    /// istnienia pliku na obcym serwerze łączy się z nim (w Windowsie z poświadczeniami
    /// słuchacza), a ścieżka urządzenia (<c>\\.\</c>, <c>\\?\</c>) nie jest plikiem dźwiękowym.
    /// </summary>
    internal static bool IsAllowedLocation(string fullPath, string playlistDirectory)
    {
        if (!StartsWithTwoSeparators(fullPath)) return true;

        var share = ShareOf(fullPath);
        return share is not null
            && string.Equals(share, ShareOf(playlistDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithTwoSeparators(string path) =>
        path.Length >= 2 && path[0] is ('\\' or '/') && path[1] is ('\\' or '/');

    /// <summary>„serwer\udział” ścieżki sieciowej; null dla ścieżki lokalnej i urządzenia.</summary>
    private static string? ShareOf(string path)
    {
        if (!StartsWithTwoSeparators(path)) return null;

        var parts = path[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or "?") return null;

        return parts[0] + "\\" + parts[1];
    }

    private static bool IsAudio(string path) =>
        AudioFileFormatDetector.SupportedExtensions
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Ścieżka urządzenia (<c>\\.\</c>, <c>\\?\</c>): potok, dysk surowy; nigdy plik z muzyką.</summary>
    private static bool IsDevicePath(string path) =>
        StartsWithTwoSeparators(path) && path.Length >= 4 && path[2] is '.' or '?' && path[3] is '\\' or '/';

    /// <summary>
    /// Czy wpis leży tam, dokąd lista może prowadzić. Lista ułożona przez słuchacza wskazuje, co chce,
    /// także serwer z muzyką. Lista pobrana z sieci (<paramref name="restrict"/>) tylko pliki lokalne
    /// i własny udział: obcy adres sieciowy w takiej liście to sposób na wyłudzenie poświadczeń.
    /// </summary>
    internal static bool IsReachable(string path, string katalog, bool restrict) =>
        !IsDevicePath(path) && (!restrict || IsAllowedLocation(path, katalog));

    /// <summary>Ścieżka, którą wolno sprawdzić na dysku: plik dźwiękowy albo arkusz CUE w dozwolonym miejscu.</summary>
    private static bool IsAcceptable(string path, string katalog, bool restrict) =>
        (IsAudio(path) || CueSheet.IsSheetPath(path)) && IsReachable(path, katalog, restrict);

    /// <summary>Adres strumienia (http://, rtsp:// i podobne), a nie ścieżka pliku.</summary>
    internal static bool IsStreamAddress(string line) =>
        StreamAddress().IsMatch(line) && !line.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows oznacza pliki pobrane przeglądarką, z poczty i wypakowane z pobranych archiwów
    /// strumieniem <c>Zone.Identifier</c>; strefa 3 i wyższe to internet. W innych systemach
    /// samo sprawdzenie pliku nie łączy się z niczym, więc nie ma czego ograniczać.
    /// </summary>
    internal static bool IsFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            foreach (var line in File.ReadLines(path + ":Zone.Identifier"))
            {
                if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.AsSpan(7).Trim(), out var zone))
                    return zone >= 3;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Brak strumienia: plik powstał na tym komputerze.
        }

        return false;
    }

    public static PlaylistLoad Load(string path) => Load(path, fallbackCodePage: null);

    /// <param name="fallbackCodePage">
    /// Strona kodowa dla <c>.m3u</c>, który nie jest poprawnym UTF-8; null oznacza stronę systemu.
    /// </param>
    /// <param name="fileExists">Sprawdzanie plików z listy; null oznacza dysk.</param>
    /// <param name="fromInternet">Czy lista jest pobrana z sieci; null oznacza sprawdzenie pliku.</param>
    internal static PlaylistLoad Load(
        string path, int? fallbackCodePage, Func<string, bool>? fileExists = null, bool? fromInternet = null)
    {
        fileExists ??= File.Exists;
        var restrict = fromInternet ?? IsFromInternet(path);

        var katalog = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var found = new List<QueueItem>();
        var missing = 0;
        var streams = 0;
        var entries = 0;

        foreach (var raw in Decode(path, fallbackCodePage).Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Puste linie i komentarze — w tym cała informacja #EXTINF, której nie potrzebujemy:
            // tytuł i czas czytamy z samych plików, więc opis z listy byłby drugą, mogącą się
            // rozejść wersją tych samych danych. Komentarzem jest tylko krzyżyk w pierwszej
            // kolumnie: nazwa pliku też może się od niego zaczynać i wtedy zapis poprzedza ją „./”.
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;

            if (++entries > MaxEntries) break;

            if (StreamAddress().IsMatch(line) && !line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                streams++;
                continue;
            }

            var resolved = Resolve(line, katalog, fileExists, restrict);
            if (resolved is null)
            {
                missing++;
            }
            else if (CueSheet.IsSheetPath(resolved))
            {
                // Arkusz bez żadnego istniejącego pliku liczy się jak brakujący wpis listy. Ograniczenie
                // pobranej listy przechodzi na arkusz, a arkusz pobrany ma swoje bez względu na listę.
                var tracks = CueSheetFile.Load(resolved, fileExists, listFromInternet: restrict);
                if (tracks.Count == 0) missing++;
                found.AddRange(tracks);
            }
            else
            {
                found.Add(new QueueItem(resolved));
            }
        }

        return new PlaylistLoad(found.Select(item => item.Path).ToList(), missing, streams) { Items = found };
    }

    /// <summary>Ścieżka istniejącego pliku dla jednej linii listy albo null.</summary>
    private static string? Resolve(string line, string katalog, Func<string, bool> fileExists, bool restrict)
    {
        try
        {
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // Adres pliku (VLC, Rhythmbox, Strawberry): znaki zakodowane procentowo.
                return Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri) && uri.IsFile
                    && IsAcceptable(uri.LocalPath, katalog, restrict) && fileExists(uri.LocalPath)
                    ? uri.LocalPath
                    : null;
            }

            // Linia niedozwolona odrzucona od razu: wersje zapasowe w Linuksie zrobiłyby z niej ścieżkę lokalną.
            var trimmed = line.Trim();
            if (!IsReachable(trimmed, katalog, restrict)) return null;

            foreach (var candidate in Candidates(line))
            {
                var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(katalog, candidate));

                if (IsAcceptable(full, katalog, restrict) && fileExists(full)) return full;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            // Jedna dziwna linia nie może przerwać wczytania całej listy.
        }

        return null;
    }

    /// <summary>
    /// Najpierw linia dosłownie, potem jej wersje zapasowe: bez znaków białych na brzegach
    /// i, w systemach z ukośnikiem zwykłym, z ukośnikiem odwrotnym zamienionym na zwykły — tak
    /// zapisują ścieżki względne programy z Windowsa. Wersja dosłowna idzie pierwsza, bo nazwa
    /// pliku może zawierać i spację na końcu, i ukośnik odwrotny.
    /// </summary>
    private static IEnumerable<string> Candidates(string line)
    {
        yield return line;

        var trimmed = line.Trim();
        if (trimmed != line) yield return trimmed;

        if (Path.DirectorySeparatorChar == '/' && trimmed.Contains('\\'))
            yield return trimmed.Replace('\\', '/');
    }

    /// <summary>Schemat adresu: http://, https://, rtsp:// i podobne.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+://")]
    private static partial Regex StreamAddress();

    private static string Decode(string path, int? fallbackCodePage)
    {
        byte[] bytes;
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length > MaxBytes)
                throw new IOException($"lista odtwarzania ma ponad {MaxBytes} bajtów");

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        var marker = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (marker) return Utf8.GetString(bytes, 3, bytes.Length - 3);

        if (!string.Equals(Path.GetExtension(path), ".m3u", StringComparison.OrdinalIgnoreCase))
            return Utf8.GetString(bytes);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return LegacyEncoding(fallbackCodePage).GetString(bytes);
        }
    }

    /// <summary>
    /// Strona kodowa systemu dla starych list. Przy niezmiennej globalizacji CultureInfo podaje
    /// zawsze 1252, więc w Windowsie strona pochodzi wprost z systemu; w Linuksie listy są
    /// w praktyce w UTF-8, a Latin-1 jest tylko ostatnią deską ratunku.
    /// </summary>
    private static Encoding LegacyEncoding(int? codePage)
    {
        var chosen = codePage ?? (OperatingSystem.IsWindows() ? (int)GetACP() : 0);
        if (chosen == 0) return Encoding.Latin1;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(chosen);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    /// <summary>
    /// Ścieżka względna, jeśli plik leży w drzewie pod katalogiem listy; w przeciwnym razie
    /// bezwzględna.
    ///
    /// <para>Względna przenosi się razem z katalogiem — lista zapisana w folderze albumu
    /// zadziała po skopiowaniu całego folderu gdziekolwiek. Wyjście w górę drzewa
    /// (<c>../../Muzyka</c>) byłoby zgodne z formatem, ale przestaje działać, gdy przeniesie
    /// się sam plik listy, a to zdarza się częściej niż przenoszenie całego drzewa.</para>
    /// </summary>
    private static string Relative(string file, string playlistDirectory)
    {
        if (playlistDirectory.Length == 0) return file;

        try
        {
            var relative = Path.GetRelativePath(playlistDirectory, file);

            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return file;

            // Ukośnik zgodny z formatem, nie z systemem: lista zapisana w Windowsie ma się
            // czytać w Linuksie i odwrotnie.
            relative = relative.Replace('\\', '/');

            // Krzyżyk na początku zrobiłby z nazwy komentarz, a znak biały mógłby zostać obcięty.
            return relative[0] == '#' || char.IsWhiteSpace(relative[0]) ? "./" + relative : relative;
        }
        catch
        {
            return file;
        }
    }
}
