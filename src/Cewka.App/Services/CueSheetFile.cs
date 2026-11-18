using Cewka.Audio.Cue;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;

namespace Cewka.App.Services;

/// <summary>
/// Arkusz CUE z dysku zamieniony na utwory do kolejki. Pliki z arkusza szuka się względem jego
/// katalogu; ścieżka bezwzględna z innego komputera zostaje zastąpiona samą nazwą pliku w tym
/// katalogu, a adresy sieciowe i ścieżki UNC przechodzą przez tę samą politykę co listy M3U.
/// </summary>
public static class CueSheetFile
{
    /// <summary>
    /// Rozszerzenia, które program otwiera: pliki dźwiękowe i arkusze CUE. Dla okna wyboru plików
    /// i skojarzeń plików; listy odtwarzania mają osobne polecenie.
    /// </summary>
    public static readonly string[] OpenableExtensions = [.. AudioFileFormatDetector.SupportedExtensions, CueSheet.Extension];

    /// <summary>
    /// Utwory arkusza z istniejącymi plikami; pusta lista, gdy arkusz jest niepoprawny, nieczytelny
    /// albo żaden jego plik nie istnieje. Taki arkusz nie jest arkuszem: pliki dźwiękowe z folderu
    /// ładują się wtedy zwyczajnie.
    /// </summary>
    /// <param name="fileExists">Sprawdzanie plików; null oznacza dysk.</param>
    /// <param name="listFromInternet">
    /// Arkusz wskazała lista pobrana z sieci. Ograniczenie listy i oznaczenie samego arkusza łączą
    /// się alternatywą: lista z tego komputera nie zdejmuje ograniczenia z pobranego arkusza.
    /// </param>
    public static IReadOnlyList<QueueItem> Load(string sheetPath, Func<string, bool>? fileExists = null, bool listFromInternet = false)
    {
        fileExists ??= File.Exists;

        try
        {
            var full = Path.GetFullPath(sheetPath);
            var sheet = CueSheet.Load(full);
            var restrict = listFromInternet || PlaylistFile.IsFromInternet(full);
            var directory = Path.GetDirectoryName(full) ?? string.Empty;

            return sheet.Tracks(full, name => Resolve(name, directory, fileExists, restrict));
        }
        catch (Exception ex) when (ex is CueSheetException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[cewka] arkusz „{Path.GetFileName(sheetPath)}” pominięty: {ex.Message}");
            return [];
        }
    }

    /// <summary>Istniejący plik dźwiękowy dla nazwy z polecenia <c>FILE</c> albo null.</summary>
    internal static string? Resolve(string name, string directory, Func<string, bool> fileExists, bool restrict)
    {
        try
        {
            if (name.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(name.Trim(), UriKind.Absolute, out var uri) || !uri.IsFile) return null;
                name = uri.LocalPath;
            }
            else if (PlaylistFile.IsStreamAddress(name))
            {
                return null;
            }

            foreach (var candidate in Candidates(name, directory))
            {
                var full = Path.GetFullPath(candidate);
                if (!PlaylistFile.IsReachable(full, directory, restrict)) continue;

                if (IsAudio(full) && fileExists(full)) return full;

                // Zgrywarka zapisuje w arkuszu nazwę pliku WAV, który potem zamienia się na FLAC.
                var stem = Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, Path.GetFileNameWithoutExtension(full));
                foreach (var extension in AudioFileFormatDetector.SupportedExtensions)
                {
                    var other = stem + extension;
                    if (!string.Equals(other, full, StringComparison.OrdinalIgnoreCase) && fileExists(other)) return other;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            // Dziwna nazwa w jednym poleceniu nie przekreśla reszty arkusza.
        }

        return null;
    }

    /// <summary>
    /// Najpierw ścieżka tak, jak ją zapisano (względna wobec katalogu arkusza), potem, dla ścieżki
    /// bezwzględnej, sama nazwa pliku w katalogu arkusza. W systemach z ukośnikiem zwykłym nazwa
    /// zapisana w Windowsie dostaje także wersję z ukośnikami zamienionymi.
    /// </summary>
    private static IEnumerable<string> Candidates(string name, string directory)
    {
        var trimmed = name.Trim();
        var native = Path.DirectorySeparatorChar == '/' ? trimmed.Replace('\\', '/') : trimmed;

        if (Path.IsPathRooted(trimmed)) yield return trimmed;
        else
        {
            yield return Path.Combine(directory, trimmed);
            if (native != trimmed) yield return Path.Combine(directory, native);
        }

        // Ścieżka z innego komputera, „C:\Muzyka\płyta.flac” albo „/home/ktoś/płyta.flac”: liczy się sama nazwa.
        var fileName = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        if (fileName.Length > 0 && fileName != trimmed) yield return Path.Combine(directory, fileName);
    }

    private static bool IsAudio(string path) =>
        AudioFileFormatDetector.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
