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
    public static QueueState? Load() => Load(AppPaths.QueueFile);

    public static QueueState? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize(json, QueueStateJsonContext.Default.QueueState);

            // Poprawny składniowo plik z "paths": null albo pustymi pozycjami nie może wywrócić startu.
            if (state is not null)
            {
                state.Paths = state.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];

                // Tak samo otwarcia oczekujące: bez pustych wpisów i ścieżek.
                foreach (var open in state.PendingOpens ?? [])
                    if (open is not null) open.Paths = open.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];

                var opens = state.PendingOpens?.Where(open => open is not null && open.Paths.Count > 0).ToList();
                state.PendingOpens = opens is { Count: > 0 } ? opens : null;
            }

            return state;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] zapisana kolejka nieczytelna: {ex.Message}");
            return null;
        }
    }

    public static void Save(QueueState state) => Save(state, AppPaths.QueueFile);

    public static void Save(QueueState state, string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, QueueStateJsonContext.Default.QueueState);
            AtomicFile.Write(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać kolejki: {ex.Message}");
        }
    }
}
