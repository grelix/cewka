using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Cewka.App.Services;

/// <summary>Katalog, do którego host wypakowuje ten plik wykonywalny, i katalog bieżącej wersji w nim.</summary>
internal readonly record struct ExtractionLayout(string AppDirectory, string CurrentDirectory);

/// <summary>Dane procesu, z których host .NET wyznacza katalog wypakowania; w testach podstawiane.</summary>
internal sealed record ExtractionFacts(
    string? ProcessPath,
    string BaseDirectory,
    string? ExtractBaseVariable,
    string TempPath,
    string? Home,
    string WorkingDirectory)
{
    public static ExtractionFacts Current() => new(
        Environment.ProcessPath,
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
        Path.GetTempPath(),
        Environment.GetEnvironmentVariable("HOME"),
        Environment.CurrentDirectory);
}

/// <summary>
/// Usuwa katalogi wypakowania poprzednich wersji. Wydanie jednoplikowe wypakowuje się przy pierwszym
/// starcie każdej wersji do <c>&lt;baza&gt;/&lt;nazwa pliku&gt;/&lt;identyfikator&gt;</c>, a .NET starych
/// katalogów nie usuwa, więc każda aktualizacja zostawiała ok. 120 MB.
/// <para>
/// Baza: <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c> (względna od katalogu bieżącego), a bez niej
/// <c>%TEMP%\.net</c> w Windows i <c>$HOME/.net</c> w Linuksie. Sprzątanie działa tylko wtedy, gdy program
/// rzeczywiście uruchomił się z tak wyznaczonego miejsca; przy <c>dotnet run</c> i w testach nic nie robi.
/// </para>
/// <para>
/// Usuwane są wyłącznie katalogi ze znacznikami wersji Cewki. Katalog innej działającej kopii zostaje
/// w całości: w Windows pomijany jest katalog z plikiem, którego nie da się otworzyć do zapisu na wyłączność,
/// a w Linuksie, gdzie usuwanie się zawsze udaje, katalog, z którego jakiś proces ma odwzorowany plik.
/// </para>
/// </summary>
internal static class BundleExtractionCleanup
{
    /// <summary>Przedrostek nazwy katalogu w trakcie usuwania; resztki po przerwaniu znikają przy kolejnym starcie.</summary>
    internal const string TrashPrefix = "cewka-usuwane-";

    /// <summary>Pierwsze sekundy po starcie należą do wczytywania okna i kolejki.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Uruchamia sprzątanie w tle po krótkim opóźnieniu. Błędy są pomijane.</summary>
    public static void StartInBackground()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (Locate(ExtractionFacts.Current()) is not { } layout) return;

                await Task.Delay(Delay).ConfigureAwait(false);
                Clean(layout);
            }
            catch (Exception)
            {
                // Sprzątanie jest grzecznością wobec dysku; nie może zaszkodzić programowi.
            }
        });
    }

    /// <summary>
    /// Katalog wypakowania tego programu albo <c>null</c>, gdy program nie działa z miejsca, które
    /// wyznaczyłby host .NET.
    /// </summary>
    internal static ExtractionLayout? Locate(ExtractionFacts facts)
    {
        if (string.IsNullOrEmpty(facts.ProcessPath)) return null;

        // Host odcina tylko „.exe” i tylko w Windows.
        var hostName = Path.GetFileName(facts.ProcessPath);
        if (OperatingSystem.IsWindows() && hostName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            hostName = hostName[..^4];
        if (hostName.Length == 0) return null;

        string extractBase;
        if (!string.IsNullOrEmpty(facts.ExtractBaseVariable))
            extractBase = Path.GetFullPath(facts.ExtractBaseVariable, facts.WorkingDirectory);
        else if (OperatingSystem.IsWindows())
            extractBase = Path.Combine(facts.TempPath, ".net");
        else if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(facts.Home))
            extractBase = Path.Combine(facts.Home, ".net");
        else
            return null;

        var appDirectory = Normalise(Path.Combine(extractBase, hostName));
        var current = Normalise(facts.BaseDirectory);

        if (!string.Equals(Path.GetDirectoryName(current), appDirectory, PathComparison)) return null;

        // Plik wykonywalny w samym katalogu programu to zwykła budowa, nie wydanie jednoplikowe.
        var executableDirectory = Path.GetDirectoryName(Normalise(facts.ProcessPath));
        if (string.Equals(executableDirectory, current, PathComparison)) return null;

        return new ExtractionLayout(appDirectory, current);
    }

    /// <summary>Usuwa katalogi innych wersji obok bieżącego. Każdy błąd pomija dany katalog.</summary>
    internal static void Clean(ExtractionLayout layout)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(layout.AppDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            try
            {
                CleanOne(layout, directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Katalog w użyciu albo bez uprawnień: zostaje do następnego razu.
            }
        }
    }

    private static void CleanOne(ExtractionLayout layout, string directory)
    {
        if (string.Equals(Normalise(directory), layout.CurrentDirectory, PathComparison)) return;

        var info = new DirectoryInfo(directory);
        if (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        var name = info.Name;

        if (name.StartsWith(TrashPrefix, StringComparison.Ordinal))
        {
            Directory.Delete(directory, recursive: true);
            return;
        }

        if (IsLiveWorkingDirectory(name)) return;
        if (!HasVersionMarkers(directory)) return;

        // Nowsza albo ta sama wersja to nie pozostałość po aktualizacji: druga kopia programu
        // w innej wersji (na przykład przenośna) wypakowywałaby się wtedy od nowa przy każdym starcie.
        if (!IsOlderVersion(ReadVersion(directory), CurrentVersion)) return;

        // Po zmianie nazwy katalog znika spod starej nazwy w całości, więc nowo startująca kopia tej
        // wersji wypakuje się od nowa, zamiast trafić na katalog w połowie usunięty.
        var trash = Path.Combine(layout.AppDirectory, TrashPrefix + Guid.NewGuid().ToString("N"));

        // Działająca kopia trzyma swoje biblioteki odwzorowane przez cały czas pracy, więc sprawdzenie ją
        // wykrywa. Windows zmienia nazwę katalogu nawet z załadowaną biblioteką, a usuwanie zabrałoby wtedy
        // wszystko poza nią, dlatego sprawdzenie idzie przed zmianą nazwy.
        if (OperatingSystem.IsLinux() ? IsMappedByAnyProcess(directory) : HasFileInUse(directory)) return;

        Directory.Move(directory, trash);
        Directory.Delete(trash, recursive: true);
    }

    /// <summary>
    /// Czy któryś plik katalogu odmawia otwarcia do zapisu na wyłączność: biblioteka załadowana przez
    /// działającą kopię albo plik przez nią otwarty.
    /// </summary>
    private static bool HasFileInUse(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Host wypakowuje do katalogu nazwanego szesnastkowym numerem swojego procesu.</summary>
    private static bool IsLiveWorkingDirectory(string name)
    {
        if (!int.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pid)) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Version? CurrentVersion => typeof(BundleExtractionCleanup).Assembly.GetName().Version;

    /// <summary>Wersja programu w katalogu; null, gdy pliku nie da się odczytać jako zestawu.</summary>
    private static Version? ReadVersion(string directory)
    {
        var assembly = typeof(BundleExtractionCleanup).Assembly.GetName().Name ?? "Cewka";

        try
        {
            return System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(directory, assembly + ".dll")).Version;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException
                                      or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Katalog jest pozostałością, gdy jego wersja jest starsza od bieżącej. Wersja nie do odczytania
    /// (uszkodzony plik po przerwanym wypakowaniu) też nią jest: działającej kopii taki katalog nie służy.
    /// </summary>
    internal static bool IsOlderVersion(Version? other, Version? current) =>
        other is null || current is null || other < current;

    private static bool HasVersionMarkers(string directory)
    {
        var assembly = typeof(BundleExtractionCleanup).Assembly.GetName().Name ?? "Cewka";
        return File.Exists(Path.Combine(directory, assembly + ".dll"))
            && File.Exists(Path.Combine(directory, assembly + ".deps.json"))
            && File.Exists(Path.Combine(directory, assembly + ".runtimeconfig.json"));
    }

    /// <summary>Czy któryś proces ma odwzorowany plik spod katalogu (biblioteki działającej kopii).</summary>
    private static bool IsMappedByAnyProcess(string directory)
    {
        var prefix = Normalise(directory) + "/";

        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out _)) continue;

            string maps;
            try
            {
                maps = File.ReadAllText(Path.Combine(entry, "maps"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Proces zakończony albo cudzy; cudze procesy nie korzystają z katalogu w domowym.
                continue;
            }

            if (maps.Contains(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Pełna ścieżka bez końcowego separatora; w Linuksie z rozwiązanymi dowiązaniami, jak u hosta.</summary>
    private static string Normalise(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsLinux()) return full;

        var resolved = RealPath(full);
        return resolved ?? full;
    }

    private static string? RealPath(string path)
    {
        var result = realpath(path, nint.Zero);
        if (result == nint.Zero) return null;

        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            free(result);
        }
    }

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern nint realpath(string path, nint resolved);

    [DllImport("libc")]
    private static extern void free(nint pointer);
}
