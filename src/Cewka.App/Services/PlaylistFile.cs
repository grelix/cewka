using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Cewka.App.Services;

/// <summary>Jedna pozycja listy odtwarzania, w postaci gotowej do zapisania.</summary>
/// <param name="DurationSeconds">Czas trwania; zero albo mniej, gdy nieznany.</param>
public sealed record PlaylistEntry(string Path, string Title, string? Artist, double DurationSeconds);

/// <summary>
/// Wynik odczytu listy: ścieżki, które istnieją, liczba tych, których zabrakło, i liczba
/// adresów strumieni internetowych, których odtwarzacz plików nie otwiera.
/// </summary>
public sealed record PlaylistLoad(IReadOnlyList<string> Paths, int Missing, int Streams = 0);

/// <summary>
/// Zapisuje i czyta kolejkę jako listę M3U.
///
/// <para>Zapisana kolejka jest przydatna głównie
/// wtedy, gdy da się ją otworzyć czymkolwiek innym — wysłać komuś, wrzucić na pendrive'a,
/// wczytać w programie na telefonie. M3U czyta wszystko, co odtwarza muzykę, i jest to plik
/// tekstowy, w którym widać, co jest w środku. Własny format zapisany w katalogu konfiguracji
/// dałby to samo tylko w obrębie tego jednego programu na tym jednym komputerze.</para>
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

    public static PlaylistLoad Load(string path) => Load(path, fallbackCodePage: null);

    /// <param name="fallbackCodePage">
    /// Strona kodowa dla <c>.m3u</c>, który nie jest poprawnym UTF-8; null oznacza stronę systemu.
    /// </param>
    internal static PlaylistLoad Load(string path, int? fallbackCodePage)
    {
        var katalog = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var found = new List<string>();
        var missing = 0;
        var streams = 0;

        foreach (var raw in Decode(path, fallbackCodePage).Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Puste linie i komentarze — w tym cała informacja #EXTINF, której nie potrzebujemy:
            // tytuł i czas czytamy z samych plików, więc opis z listy byłby drugą, mogącą się
            // rozejść wersją tych samych danych. Komentarzem jest tylko krzyżyk w pierwszej
            // kolumnie: nazwa pliku też może się od niego zaczynać i wtedy zapis poprzedza ją „./”.
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;

            if (StreamAddress().IsMatch(line) && !line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                streams++;
                continue;
            }

            var resolved = Resolve(line, katalog);
            if (resolved is not null) found.Add(resolved);
            else missing++;
        }

        return new PlaylistLoad(found, missing, streams);
    }

    /// <summary>Ścieżka istniejącego pliku dla jednej linii listy albo null.</summary>
    private static string? Resolve(string line, string katalog)
    {
        try
        {
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // Adres pliku (VLC, Rhythmbox, Strawberry): znaki zakodowane procentowo.
                return Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath)
                    ? uri.LocalPath
                    : null;
            }

            foreach (var candidate in Candidates(line))
            {
                var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(katalog, candidate));

                if (File.Exists(full)) return full;
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
        var bytes = File.ReadAllBytes(path);

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
