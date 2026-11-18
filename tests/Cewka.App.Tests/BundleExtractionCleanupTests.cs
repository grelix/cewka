using System.Diagnostics;
using System.Runtime.InteropServices;
using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprzątanie katalogów, do których wydanie jednoplikowe wypakowuje się przy pierwszym starcie
/// każdej wersji. Każdy test pracuje we własnym katalogu tymczasowym, nigdy w prawdziwym
/// <c>%TEMP%\.net</c> ani <c>~/.net</c>.
/// </summary>
public sealed class BundleExtractionCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cewka-testy-wypakowanie-" + Guid.NewGuid().ToString("N"));

    public BundleExtractionCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private ExtractionFacts Facts(string baseDirectory, string? variable = null, string? processPath = null,
        string? workingDirectory = null) => new(
        ProcessPath: processPath ?? Path.Combine(_root, "Pobrane", Executable("Cewka")),
        BaseDirectory: baseDirectory,
        ExtractBaseVariable: variable,
        TempPath: Path.Combine(_root, "temp"),
        Home: Path.Combine(_root, "dom"),
        WorkingDirectory: workingDirectory ?? Path.Combine(_root, "roboczy"));

    // ---------- Rozpoznanie katalogu wypakowania ----------

    [Fact]
    public void ZmiennaBazyWskazujeKatalogAplikacji()
    {
        var current = Path.Combine(_root, "baza", "Cewka", "ZSm7qZL655D6");

        var layout = BundleExtractionCleanup.Locate(Facts(current + Path.DirectorySeparatorChar, variable: Path.Combine(_root, "baza")));

        Assert.NotNull(layout);
        Assert.Equal(Path.Combine(_root, "baza", "Cewka"), layout.Value.AppDirectory);
        Assert.Equal(current, layout.Value.CurrentDirectory);
    }

    /// <summary>Host liczy względną wartość zmiennej od katalogu bieżącego procesu.</summary>
    [Fact]
    public void WzglednaZmiennaLiczonaOdKataloguBiezacego()
    {
        var current = Path.Combine(_root, "roboczy", "baza", "Cewka", "abc");

        var layout = BundleExtractionCleanup.Locate(Facts(current, variable: "baza"));

        Assert.NotNull(layout);
        Assert.Equal(Path.Combine(_root, "roboczy", "baza", "Cewka"), layout.Value.AppDirectory);
    }

    /// <summary>Katalog aplikacji nosi nazwę pliku wykonywalnego, nie zestawu.</summary>
    [Fact]
    public void NazwaPlikuWyznaczaKatalogAplikacji()
    {
        var renamed = Path.Combine(_root, "Pobrane", Executable("Cewka-nowa"));

        Assert.NotNull(BundleExtractionCleanup.Locate(Facts(
            Path.Combine(_root, "baza", "Cewka-nowa", "abc"), variable: Path.Combine(_root, "baza"), processPath: renamed)));
        Assert.Null(BundleExtractionCleanup.Locate(Facts(
            Path.Combine(_root, "baza", "Cewka", "abc"), variable: Path.Combine(_root, "baza"), processPath: renamed)));
    }

    /// <summary><c>dotnet run</c>: program działa z katalogu, w którym leży jego plik wykonywalny.</summary>
    [Fact]
    public void UruchomienieZKataloguBudowyNicNieRobi()
    {
        var output = Path.Combine(_root, "baza", "Cewka", "abc");

        Assert.Null(BundleExtractionCleanup.Locate(Facts(
            output, variable: Path.Combine(_root, "baza"), processPath: Path.Combine(output, Executable("Cewka")))));
    }

    [Fact]
    public void KatalogSpozaBazyNicNieRobi()
    {
        Assert.Null(BundleExtractionCleanup.Locate(Facts(
            Path.Combine(_root, "inne", "Cewka", "abc"), variable: Path.Combine(_root, "baza"))));
        Assert.Null(BundleExtractionCleanup.Locate(Facts(
            Path.Combine(_root, "baza", "abc"), variable: Path.Combine(_root, "baza"))));
    }

    [Fact]
    public void ProcesTestowyNieJestWydaniemJednoplikowym()
    {
        Assert.Null(BundleExtractionCleanup.Locate(ExtractionFacts.Current()));
    }

    [WindowsFact]
    public void DomyslnaBazaWindowsToTempNetBezWzgleduNaWielkoscLiter()
    {
        var facts = Facts(Path.Combine(_root, "TEMP", ".NET", "CEWKA", "abc"),
            processPath: Path.Combine(_root, "Pobrane", "Cewka.EXE"));

        var layout = BundleExtractionCleanup.Locate(facts);

        Assert.NotNull(layout);
    }

    [LinuxFact]
    public void DomyslnaBazaLinuksaToHomeNet()
    {
        var current = Path.Combine(_root, "dom", ".net", "Cewka", "abc");

        Assert.NotNull(BundleExtractionCleanup.Locate(Facts(current)));
        Assert.Null(BundleExtractionCleanup.Locate(Facts(Path.Combine(_root, "temp", ".net", "Cewka", "abc"))));
    }

    /// <summary>W Linuksie host rozwiązuje dowiązania w ścieżce bazy, a <c>HOME</c> może je zawierać.</summary>
    [LinuxFact]
    public void DowiazanyKatalogDomowyRozpoznany()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "prawdziwy-dom", ".net", "Cewka", "abc")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_root, "dom"), Path.Combine(_root, "prawdziwy-dom"));

        Assert.NotNull(BundleExtractionCleanup.Locate(Facts(real)));
    }

    [LinuxFact]
    public void RozszerzenieExeWLinuksieZostajeWNazwie()
    {
        var facts = Facts(Path.Combine(_root, "dom", ".net", "Cewka.exe", "abc"),
            processPath: Path.Combine(_root, "Pobrane", "Cewka.exe"));

        Assert.NotNull(BundleExtractionCleanup.Locate(facts));
    }

    // ---------- Sprzątanie ----------

    private string AppDirectory => Path.Combine(_root, "baza", "Cewka");

    private ExtractionLayout Layout(string current) => new(AppDirectory, Path.Combine(AppDirectory, current));

    /// <summary>Katalog z plikami, po których sprzątanie rozpoznaje wypakowaną wersję Cewki.</summary>
    private string Version(string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppDirectory, name)).FullName;
        foreach (var marker in new[] { "Cewka.dll", "Cewka.deps.json", "Cewka.runtimeconfig.json" })
            File.WriteAllText(Path.Combine(directory, marker), "x");
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(directory, "runtimes", "win-x64", "native", "zasob.bin"), "x");
        return directory;
    }

    [Fact]
    public void UsuwaStareWersjeIZostawiaBiezaca()
    {
        var current = Version("ZSm7qZL655D6");
        var older = Version("mMhD4IbR926J");
        var oldest = Version("jI3yycR5LvZV");

        BundleExtractionCleanup.Clean(Layout("ZSm7qZL655D6"));

        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(oldest));
        Assert.True(File.Exists(Path.Combine(current, "Cewka.dll")));
        Assert.True(File.Exists(Path.Combine(current, "runtimes", "win-x64", "native", "zasob.bin")));
        Assert.Single(Directory.GetDirectories(AppDirectory));
    }

    /// <summary>Gdy zmienna bazy wskaże np. katalog domowy z własnym folderem „Cewka”, cudze katalogi zostają.</summary>
    [Fact]
    public void KatalogBezZnacznikowWersjiZostaje()
    {
        Version("biezacy");
        var foreign = Directory.CreateDirectory(Path.Combine(AppDirectory, "Muzyka")).FullName;
        File.WriteAllText(Path.Combine(foreign, "Cewka.dll"), "tylko jeden znacznik");
        File.WriteAllText(Path.Combine(foreign, "utwor.flac"), "x");
        var file = Path.Combine(AppDirectory, "notatka.txt");
        File.WriteAllText(file, "x");

        BundleExtractionCleanup.Clean(Layout("biezacy"));

        Assert.True(File.Exists(Path.Combine(foreign, "utwor.flac")));
        Assert.True(File.Exists(file));
    }

    /// <summary>Host wypakowuje do katalogu nazwanego szesnastkowym numerem procesu i dopiero potem zmienia mu nazwę.</summary>
    [Fact]
    public void KatalogRoboczyDzialajacegoProcesuZostaje()
    {
        Version("biezacy");
        var working = Version(Environment.ProcessId.ToString("x"));
        var abandoned = Version(int.MaxValue.ToString("x"));

        BundleExtractionCleanup.Clean(Layout("biezacy"));

        Assert.True(File.Exists(Path.Combine(working, "Cewka.dll")));
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void ResztkaPoPrzerwanymUsuwaniuZnika()
    {
        Version("biezacy");
        var leftover = Directory.CreateDirectory(Path.Combine(AppDirectory, BundleExtractionCleanup.TrashPrefix + "0123")).FullName;
        File.WriteAllText(Path.Combine(leftover, "SkiaSharp.dll"), "x");

        BundleExtractionCleanup.Clean(Layout("biezacy"));

        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public void BrakKataloguAplikacjiNieJestBledem()
    {
        BundleExtractionCleanup.Clean(Layout("biezacy"));
    }

    /// <summary>Dowiązanie obok wersji i dowiązanie wewnątrz starej wersji: cele poza katalogiem zostają.</summary>
    [Fact]
    public void DowiazaniaNieSaPrzechodzone()
    {
        Version("biezacy");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "na-zewnatrz")).FullName;
        foreach (var marker in new[] { "Cewka.dll", "Cewka.deps.json", "Cewka.runtimeconfig.json" })
            File.WriteAllText(Path.Combine(outside, marker), "x");
        var sibling = Path.Combine(AppDirectory, "dowiazanie");
        CreateDirectoryLink(sibling, outside);
        var older = Version("starsza");
        CreateDirectoryLink(Path.Combine(older, "wewnatrz"), outside);

        BundleExtractionCleanup.Clean(Layout("biezacy"));

        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(sibling));
        Assert.True(File.Exists(Path.Combine(outside, "Cewka.dll")));
        Assert.True(File.Exists(Path.Combine(outside, "Cewka.runtimeconfig.json")));
    }

    /// <summary>
    /// Druga działająca kopia innej wersji ma załadowane biblioteki ze swojego katalogu. Katalog musi
    /// zostać w całości: częściowe usunięcie zabrałoby jej pliki ładowane później.
    /// </summary>
    [Fact]
    public void KatalogZZaladowanaBibliotekaZostajeNienaruszony()
    {
        Version("biezacy");
        var running = Version("dzialajaca");
        var library = Path.Combine(running, Path.GetFileName(NativeLibraryFile()));
        File.Copy(NativeLibraryFile(), library);

        var handle = NativeLibrary.Load(library);
        try
        {
            BundleExtractionCleanup.Clean(Layout("biezacy"));

            Assert.True(File.Exists(library));
            Assert.True(File.Exists(Path.Combine(running, "Cewka.dll")));
            Assert.True(File.Exists(Path.Combine(running, "runtimes", "win-x64", "native", "zasob.bin")));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static string NativeLibraryFile()
    {
        var (rid, name) = OperatingSystem.IsWindows() ? ("win-x64", "cewka_audio.dll") : ("linux-x64", "libcewka_audio.so");
        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", name);
    }

    /// <summary>Dowiązanie katalogu bez uprawnień administratora: w Windows punkt połączenia.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "Nie utworzono punktu połączenia: " + process.StandardError.ReadToEnd());
    }

    /// <summary>
    /// Sprzątane są tylko wersje starsze od bieżącej. Katalog tej samej albo nowszej wersji należy do
    /// innej kopii programu, a nie do aktualizacji, i zostaje.
    /// </summary>
    [Theory]
    [InlineData("1.0.0.0", "1.0.1.0", true)]
    [InlineData("0.9.0.0", "1.0.1.0", true)]
    [InlineData("1.0.1.0", "1.0.1.0", false)]
    [InlineData("1.0.2.0", "1.0.1.0", false)]
    [InlineData("1.1.0.0", "1.0.1.0", false)]
    public void TylkoStarszaWersjaJestPozostaloscia(string other, string current, bool leftover)
    {
        Assert.Equal(leftover, BundleExtractionCleanup.IsOlderVersion(System.Version.Parse(other), System.Version.Parse(current)));
    }

    [Fact]
    public void KatalogZNieczytelnaWersjaJestPozostaloscia()
    {
        Assert.True(BundleExtractionCleanup.IsOlderVersion(null, new System.Version(1, 0, 1, 0)));
    }
}
