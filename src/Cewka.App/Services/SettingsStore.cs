using System.Text.Json;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Writes are debounced because the UI
/// touches settings on every slider move, and they are atomic so that a crash
/// mid-write cannot leave an unreadable file behind.
/// </summary>
public sealed class SettingsStore
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(700);

    private readonly string _path;
    private readonly DispatcherTimer _debounce;
    private bool _dirty;

    public SettingsStore() : this(AppPaths.SettingsFile)
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(path);

        _debounce = new DispatcherTimer { Interval = SaveDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_dirty) SaveNow();
        };
    }

    public AppSettings Current { get; private set; }

    /// <summary>
    /// Reads the single-instance preference before the framework starts.
    /// <para>
    /// The decision has to be made in <c>Main</c>, before Avalonia is initialised, because a
    /// copy that is handing its files over must never build a window. Constructing the whole
    /// store there is not an option — it owns a dispatcher timer, and the dispatcher does not
    /// exist yet.
    /// </para>
    /// </summary>
    public static bool ReadSingleInstanceFlag()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (!File.Exists(path)) return new AppSettings().SingleInstance;

            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);

            return loaded?.SingleInstance ?? true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Marks the settings as changed; the file is written a moment later.</summary>
    public void Touch()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Writes immediately. Called on shutdown, where a debounce would be lost.</summary>
    public void SaveNow()
    {
        _debounce.Stop();
        _dirty = false;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);

            // Write beside the target then swap, so a partial write never replaces good data.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Settings are a convenience, never a reason to take the application down.
            Console.Error.WriteLine($"[cewka] nie udało się zapisać ustawień: {ex.Message}");
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            return Sanitise(loaded ?? new AppSettings());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] ustawienia nieczytelne, użyto domyślnych: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Repairs values that a hand-edited or older file could get wrong.</summary>
    private static AppSettings Sanitise(AppSettings s)
    {
        s.Volume = Math.Clamp(s.Volume, 0, 1);
        s.Preamp = Math.Clamp(s.Preamp, -12, 12);

        if (s.EqualiserGains.Length != 10)
        {
            var fixedGains = new double[10];
            for (var i = 0; i < 10; i++)
                fixedGains[i] = i < s.EqualiserGains.Length ? s.EqualiserGains[i] : 0;
            s.EqualiserGains = fixedGains;
        }

        for (var i = 0; i < s.EqualiserGains.Length; i++)
            s.EqualiserGains[i] = Math.Clamp(s.EqualiserGains[i], -12, 12);

        s.SeekStep = AudioPreferences.NearestSeekStep(s.SeekStep);

        return s;
    }
}
