using System.Text.Json;
using Cewka.App.Models;
using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Reads and writes the persisted playback queue. Separate from <see cref="SettingsStore"/>
/// because it is written once, on shutdown, rather than continuously.
/// </summary>
public static class QueueStateStore
{
    public static QueueState? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.QueueFile)) return null;

            var json = File.ReadAllText(AppPaths.QueueFile);
            return JsonSerializer.Deserialize(json, QueueStateJsonContext.Default.QueueState);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] zapisana kolejka nieczytelna: {ex.Message}");
            return null;
        }
    }

    public static void Save(QueueState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);

            var json = JsonSerializer.Serialize(state, QueueStateJsonContext.Default.QueueState);

            // Zapis obok i podmiana, tak jak dla ustawien: przerwany zapis nie moze
            // zostawic pliku nie do odczytania.
            var temporary = AppPaths.QueueFile + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, AppPaths.QueueFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zapisać kolejki: {ex.Message}");
        }
    }
}
