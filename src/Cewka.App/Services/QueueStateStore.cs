using System.Text.Json;
using Cewka.App.Models;
using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Reads and writes the persisted playback queue. Separate from <see cref="SettingsStore"/>
/// because it is far larger and changes with every edit of the queue.
/// </summary>
public static class QueueStateStore
{
    /// <summary>Najdłuższa pozycja wznowienia przyjmowana z pliku: dłuższego nagrania nikt nie słucha.</summary>
    private static readonly double MaxResumeSeconds = TimeSpan.FromDays(30).TotalSeconds;

    public static QueueState? Load() => Load(AppPaths.QueueFile);

    public static QueueState? Load(string path) => Load(path, out _);

    /// <param name="unreadable">
    /// Plik istnieje, ale chwilowo nie dał się przeczytać (blokada, uprawnienia). Jego treść jest
    /// zwykle poprawna, więc w tej sesji nie wolno go nadpisać.
    /// </param>
    public static QueueState? Load(string path, out bool unreadable) => Load(path, out unreadable, KeepCopy);

    /// <param name="keepCopy">Zachowuje kopię uszkodzonego pliku; fałsz, gdy się nie udało.</param>
    internal static QueueState? Load(string path, out bool unreadable, Func<string, bool> keepCopy)
    {
        unreadable = false;

        string json;
        try
        {
            if (!File.Exists(path)) return null;

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cewka] zapisana kolejka niedostępna: {ex.Message}");
            // Blokada (antywirus, kopia zapasowa) mija, więc plik zostaje. Brak uprawnień nie mija:
            // taki plik zapis podmienia nowym, inaczej kolejka nie zapisałaby się już nigdy.
            unreadable = ex is IOException and not (FileNotFoundException or DirectoryNotFoundException);
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(json, QueueStateJsonContext.Default.QueueState);

            // Poprawny składniowo plik z "paths": null albo pustymi pozycjami nie może wywrócić startu.
            if (state is not null)
            {
                // Puste pozycje wypadają razem ze swoimi wycinkami arkuszy; numery wycinków idą za ścieżkami.
                state.Paths ??= [];
                state.Paths = state.Paths.Select(p => p ?? string.Empty).ToList();
                state.SetItems(state.Items().Where(item => !string.IsNullOrWhiteSpace(item.Path)));

                // Tak samo otwarcia oczekujące: bez pustych wpisów i ścieżek.
                foreach (var open in state.PendingOpens ?? [])
                    if (open is not null) open.Paths = open.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];

                var opens = state.PendingOpens?.Where(open => open is not null && open.Paths.Count > 0).ToList();
                state.PendingOpens = opens is { Count: > 0 } ? opens : null;

                // Pozycja poza zakresem TimeSpan kończyłaby każde wznowienie wyjątkiem, a przy
                // nieznanej długości utworu nic dalej jej nie przycina.
                ApplyPosition(state, path);

                if (!double.IsFinite(state.PositionSeconds) || state.PositionSeconds < 0
                    || state.PositionSeconds > MaxResumeSeconds)
                    state.PositionSeconds = 0;
            }

            return state;
        }
        catch (Exception ex)
        {
            // Uszkodzonej treści nikt już nie odczyta; oryginał zostaje obok, a kolejka zapisuje się od nowa.
            // Bez kopii oryginał jest jedynym egzemplarzem, więc wtedy nie wolno go nadpisać.
            Console.Error.WriteLine($"[cewka] zapisana kolejka nieczytelna: {ex.Message}");
            unreadable = !keepCopy(path);
            return null;
        }
    }

    private static bool KeepCopy(string path)
    {
        try
        {
            File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zachować kopii kolejki: {ex.Message}");
            return false;
        }
    }

    /// <summary>Plik pozycji leżący obok pliku kolejki <paramref name="path"/>.</summary>
    internal static string PositionFile(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + ".position.json");

    /// <summary>
    /// Nakłada miejsce odtwarzania z pliku pozycji, jeśli dotyczy tej samej listy i wskazuje wpis,
    /// który w niej jest. Plik pozycji jest dodatkiem: brak, uszkodzenie albo niezgodność oznacza
    /// po prostu miejsce zapisane razem z listą.
    /// </summary>
    private static void ApplyPosition(QueueState state, string path)
    {
        if (state.Revision == 0) return;

        try
        {
            var file = PositionFile(path);
            if (!File.Exists(file)) return;

            var position = JsonSerializer.Deserialize(File.ReadAllText(file), QueueStateJsonContext.Default.QueuePosition);
            if (position is null || position.Revision != state.Revision) return;
            if (position.CurrentIndex < -1 || position.CurrentIndex >= state.Paths.Count) return;

            state.CurrentIndex = position.CurrentIndex;
            state.PositionSeconds = position.PositionSeconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"[cewka] plik pozycji kolejki pominięty: {ex.Message}");
        }
    }

    private static ReadOnlySpan<byte> RevisionPrefix => "{\"revision\":"u8;

    /// <summary>
    /// Numer zapisu listy z początku pliku kolejki, bez czytania reszty. Zero, gdy pliku nie ma,
    /// nie da się go odczytać albo nie zaczyna się od numeru (plik starszej wersji, cudzy, uszkodzony).
    /// </summary>
    internal static long ReadRevision(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[64];
            int read;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            ReadOnlySpan<byte> text = head[..read];
            if (text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) text = text[3..];
            if (!text.StartsWith(RevisionPrefix)) return 0;

            text = text[RevisionPrefix.Length..];
            var digits = 0;
            while (digits < text.Length && text[digits] is >= (byte)'0' and <= (byte)'9') digits++;

            // Numer musi się kończyć w odczytanym fragmencie, inaczej to nie jest cały numer.
            if (digits == 0 || digits == text.Length) return 0;

            return System.Buffers.Text.Utf8Parser.TryParse(text[..digits], out long revision, out var used) && used == digits
                ? revision
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    public static bool Save(QueueState state) => Save(state, AppPaths.QueueFile);

    /// <returns>Fałsz, gdy pliku nie udało się zapisać.</returns>
    public static bool Save(QueueState state, string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, QueueStateJsonContext.Default.QueueState);
            AtomicFile.Write(path, json);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać kolejki: {ex.Message}");
            return false;
        }
    }

    /// <summary>Zapisuje samo miejsce odtwarzania dla listy o numerze <paramref name="revision"/>.</summary>
    internal static void SavePosition(QueueState state, long revision, string path)
    {
        try
        {
            var position = new QueuePosition
            {
                Revision = revision,
                CurrentIndex = state.CurrentIndex,
                PositionSeconds = state.PositionSeconds,
            };

            AtomicFile.Write(PositionFile(path), JsonSerializer.Serialize(position, QueueStateJsonContext.Default.QueuePosition));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać pozycji kolejki: {ex.Message}");
        }
    }
}
