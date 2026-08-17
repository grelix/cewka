using System.Text;

namespace Cewka.App.Services;

/// <summary>Jedna pozycja listy odtwarzania, w postaci gotowej do zapisania.</summary>
public sealed record PlaylistEntry(string Path, string Title, string? Artist, double DurationSeconds);

/// <summary>Wynik odczytu listy: ścieżki, które istnieją, i liczba tych, których zabrakło.</summary>
public sealed record PlaylistLoad(IReadOnlyList<string> Paths, int Missing);

/// <summary>
/// Zapisuje i czyta kolejkę jako listę M3U.
///
/// <para><b>Dlaczego M3U, a nie własny format.</b> Zapisana kolejka jest przydatna głównie
/// wtedy, gdy da się ją otworzyć czymkolwiek innym — wysłać komuś, wrzucić na pendrive'a,
/// wczytać w programie na telefonie. M3U czyta wszystko, co odtwarza muzykę, i jest to plik
/// tekstowy, w którym widać, co jest w środku. Własny format zapisany w katalogu konfiguracji
/// dałby to samo tylko w obrębie tego jednego programu na tym jednym komputerze.</para>
///
/// <para><b>Kodowanie.</b> Zapis w UTF-8 bez znacznika kolejności bajtów — tak zapisuje się
/// pliki <c>.m3u8</c> i tak je czytają inne programy. Przy odczycie znacznik jest pomijany,
/// jeśli się trafi, bo część programów go jednak dopisuje.</para>
/// </summary>
public static class PlaylistFile
{
    /// <summary>Rozszerzenia przyjmowane przy odczycie i proponowane przy zapisie.</summary>
    public static readonly string[] Extensions = [".m3u8", ".m3u"];

    /// <summary>Bez znacznika kolejności bajtów — inaczej pierwsza linia przestaje być „#EXTM3U".</summary>
    private static readonly UTF8Encoding Kodowanie = new(encoderShouldEmitUTF8Identifier: false);

    public static void Save(string path, IEnumerable<PlaylistEntry> entries)
    {
        var katalog = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var text = new StringBuilder();

        text.Append("#EXTM3U\n");

        foreach (var entry in entries)
        {
            var seconds = (int)Math.Round(Math.Max(0, entry.DurationSeconds));
            var label = string.IsNullOrWhiteSpace(entry.Artist)
                ? entry.Title
                : $"{entry.Artist} - {entry.Title}";

            // Opis pozycji jest tym, co inne programy pokazują na liście, dopóki nie otworzą
            // samego pliku. Znaki nowej linii w tagu rozerwałyby plik na dwie pozycje.
            text.Append("#EXTINF:").Append(seconds).Append(',')
                .Append(label.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');

            text.Append(Relative(entry.Path, katalog)).Append('\n');
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString(), Kodowanie);
        File.Move(temporary, path, overwrite: true);
    }

    public static PlaylistLoad Load(string path)
    {
        var katalog = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var found = new List<string>();
        var missing = 0;

        foreach (var raw in File.ReadAllLines(path, Kodowanie))
        {
            var line = raw.Trim();

            // Puste linie i komentarze — w tym cała informacja #EXTINF, której nie potrzebujemy:
            // tytuł i czas czytamy z samych plików, więc opis z listy byłby drugą, mogącą się
            // rozejść wersją tych samych danych.
            if (line.Length == 0 || line[0] == '#') continue;

            var full = Path.IsPathRooted(line)
                ? line
                : Path.GetFullPath(Path.Combine(katalog, line));

            if (File.Exists(full)) found.Add(full);
            else missing++;
        }

        return new PlaylistLoad(found, missing);
    }

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
            return relative.Replace('\\', '/');
        }
        catch
        {
            return file;
        }
    }
}
