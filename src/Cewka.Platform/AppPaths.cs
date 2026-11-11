namespace Cewka.Platform;

/// <summary>
/// Resolves per-user directories for configuration and cache, following the
/// conventions of each operating system: <c>%APPDATA%</c> on Windows and the
/// XDG base directory specification on Linux.
/// </summary>
public static class AppPaths
{
    /// <summary>Directory name used under the platform-specific roots.</summary>
    public const string WindowsFolderName = "Cewka";

    /// <summary>Lower-case variant expected by XDG-compliant desktops.</summary>
    public const string UnixFolderName = "cewka";

    private static readonly Lazy<string> LazyConfig = new(ResolveConfigDirectory);
    private static readonly Lazy<string> LazyCache = new(ResolveCacheDirectory);
    private static readonly Lazy<string> LazyRuntime = new(ResolveRuntimeDirectory);

    private static string? _overrideRoot;

    /// <summary>Directory holding user settings. Created on first access.</summary>
    public static string ConfigDirectory => LazyConfig.Value;

    /// <summary>Directory holding regenerable data such as loudness analysis results.</summary>
    public static string CacheDirectory => LazyCache.Value;

    /// <summary>
    /// Katalog na rzeczy ważne tylko w czasie sesji użytkownika. Pod Uniksem to
    /// <c>XDG_RUNTIME_DIR</c>: należy do jednego użytkownika, ma prawa 0700 i znika przy
    /// wylogowaniu, dlatego leży tam gniazdo przekazywania ścieżek.
    /// </summary>
    public static string RuntimeDirectory => LazyRuntime.Value;

    /// <summary>Full path of the main settings file.</summary>
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Full path of the persisted playback queue.</summary>
    public static string QueueFile => Path.Combine(ConfigDirectory, "queue.json");

    /// <summary>Full path of the radio state (favourites, recent stations, list context).</summary>
    public static string RadioFile => Path.Combine(ConfigDirectory, "radio.json");

    /// <summary>
    /// Plik, którego wyłączna blokada rozstrzyga, która kopia programu działa. Leży w katalogu
    /// ustawień, bo ten jest ten sam bez względu na to, skąd program uruchomiono.
    /// </summary>
    public static string InstanceLockFile => Path.Combine(ConfigDirectory, "instance.lock");

    /// <summary>Socket on which the running copy accepts paths from later ones (Unix only).</summary>
    public static string InstanceSocketFile => Path.Combine(RuntimeDirectory, "instance.sock");

    /// <summary>
    /// Przenosi oba katalogi pod wskazany korzeń. Dla narzędzi i testów: zamknięcie prawdziwego
    /// okna zapisuje kolejkę i bez tego nadpisałoby kolejkę użytkownika.
    /// <para>
    /// Pułapka: wywołać przed pierwszym odczytem ścieżki. Wartości są zapamiętywane na czas
    /// życia procesu, a późne wywołanie zostawiłoby obie połowy niezgodne.
    /// </para>
    /// </summary>
    public static void Redirect(string root)
    {
        if (LazyConfig.IsValueCreated || LazyCache.IsValueCreated || LazyRuntime.IsValueCreated)
            throw new InvalidOperationException(
                "AppPaths.Redirect musi zostać wywołane przed pierwszym odczytem ścieżek.");

        _overrideRoot = root;
    }

    private static string ResolveConfigDirectory()
    {
        string root;

        if (_overrideRoot is not null)
        {
            root = Path.Combine(_overrideRoot, "config");
        }
        else if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            root = Path.Combine(appData, WindowsFolderName);
        }
        else
        {
            // XDG_CONFIG_HOME, falling back to ~/.config as the specification requires.
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            root = Path.Combine(baseDir, UnixFolderName);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveRuntimeDirectory()
    {
        if (_overrideRoot is not null)
        {
            var overridden = Path.Combine(_overrideRoot, "runtime");
            Directory.CreateDirectory(overridden);
            return overridden;
        }

        if (!OperatingSystem.IsWindows())
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

            if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
            {
                var root = Path.Combine(runtime, UnixFolderName);

                Directory.CreateDirectory(
                    root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                return root;
            }
        }

        // Zapas na wypadek pulpitu bez XDG_RUNTIME_DIR. Katalog ustawień też należy do jednego
        // użytkownika; różnica jest taka, że nie znika przy wylogowaniu, więc plik gniazda
        // potrafi tam przeleżeć do następnego uruchomienia. Nie szkodzi: zakłada je dopiero ta
        // kopia, która zdobyła blokadę, i wtedy stary plik po prostu nadpisuje.
        return ConfigDirectory;
    }

    private static string ResolveCacheDirectory()
    {
        string root;

        if (_overrideRoot is not null)
        {
            root = Path.Combine(_overrideRoot, "cache");
        }
        else if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, WindowsFolderName, "Cache");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var baseDir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                : xdg;
            root = Path.Combine(baseDir, UnixFolderName);
        }

        Directory.CreateDirectory(root);
        return root;
    }
}
