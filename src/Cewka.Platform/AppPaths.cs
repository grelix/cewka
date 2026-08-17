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

    private static string? _overrideRoot;

    /// <summary>Directory holding user settings. Created on first access.</summary>
    public static string ConfigDirectory => LazyConfig.Value;

    /// <summary>Directory holding regenerable data such as loudness analysis results.</summary>
    public static string CacheDirectory => LazyCache.Value;

    /// <summary>Full path of the main settings file.</summary>
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Full path of the persisted playback queue.</summary>
    public static string QueueFile => Path.Combine(ConfigDirectory, "queue.json");

    /// <summary>
    /// Moves both directories under a root of the caller's choosing.
    /// <para>
    /// This exists for the development tools. The snapshot renderer opens and closes the real
    /// main window, and closing it persists the queue — so without this, photographing the
    /// interface overwrites the queue of whoever ran it.
    /// </para>
    /// <para>
    /// Must be called before anything reads a path: the resolved values are cached for the
    /// lifetime of the process, and a late call would leave the two halves disagreeing.
    /// </para>
    /// </summary>
    public static void Redirect(string root)
    {
        if (LazyConfig.IsValueCreated || LazyCache.IsValueCreated)
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
