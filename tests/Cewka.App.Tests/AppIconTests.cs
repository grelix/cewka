using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Platform;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using SkiaSharp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Ikona programu: plik ICO, obraz okna i obrazy pakietów Linuksa generowane skryptem
/// <c>tools/make-icon.ps1</c> z rysunków w <c>assets/ikona</c>.
/// <para>
/// Brakujący rozmiar w pliku ICO Windows zastępuje zmniejszonym dużym obrazem, a brakujący
/// plik w pakiecie przerywa instalację, więc komplet sprawdzany jest tutaj, a nie na oko.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public sealed partial class AppIconTests
{
    // Rozmiary i granicę rysunku uproszczonego podaje generator; test sprawdza pliki i skrypty względem niego.
    private static int[] IcoSizes => GeneratorValues("rozmiaryIco");

    private static int[] LinuxSizes => GeneratorValues("rozmiaryLinux");

    // Do tego rozmiaru włącznie obraz pochodzi z rysunku uproszczonego.
    private static int SimplifiedUpTo => GeneratorValues("granicaMalego").Single();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Typ barw 6 w nagłówku PNG: RGB z kanałem alfa.
    private const byte PngColourRgba = 6;

    // Koniec górnego wyprowadzenia w układzie 512 px: jasny w pełnym rysunku, w uproszczonym tło kafla.
    private const int LeadX = 72;
    private const int LeadY = 98;

    [GeneratedRegex(@"for \w+ in ([\d ]+); do")]
    private static partial Regex SizeLoop();

    [GeneratedRegex(@"url\(([^)]*)\)")]
    private static partial Regex UrlReference();

    [GeneratedRegex(@"^\$(?<name>\w+)\s*=\s*(?<values>\d+(?:\s*,\s*\d+)*)\s*$", RegexOptions.Multiline)]
    private static partial Regex GeneratorAssignment();

    /// <summary>Liczby przypisane zmiennej w <c>tools/make-icon.ps1</c>.</summary>
    private static int[] GeneratorValues(string variable)
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "make-icon.ps1"));
        var match = GeneratorAssignment().Matches(script).SingleOrDefault(m => m.Groups["name"].Value == variable);

        Assert.True(match != null, $"make-icon.ps1: brak przypisania liczb do ${variable}");
        return match.Groups["values"].Value.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public void GeneratorPodajeRozmiaryDlaWindowsILinuksa()
    {
        // 24 px to pasek zadań Windows 11 przy 100%, 22 px panele KDE i XFCE.
        Assert.Superset(new HashSet<int> { 16, 20, 24, 32, 256 }, IcoSizes.ToHashSet());
        Assert.Superset(new HashSet<int> { 16, 22, 24, 32, 256 }, LinuxSizes.ToHashSet());
        Assert.Equal(IcoSizes.Order(), IcoSizes);
        Assert.Equal(LinuxSizes.Order(), LinuxSizes);
        // Oba rysunki mają swoje rozmiary.
        Assert.InRange(SimplifiedUpTo, IcoSizes.Min(), IcoSizes.Max() - 1);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cewka.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Nie odnaleziono katalogu repozytorium nad " + AppContext.BaseDirectory);
    }

    /// <summary>Obrazy zapisane w pliku ICO jako pary rozmiar z tablicy wpisów i dane obrazu.</summary>
    private static List<(int Size, byte[] Data)> IcoEntries(byte[] ico)
    {
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));

        var entries = new List<(int, byte[])>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            // Rozmiar 256 zapisany jest jako zero, bo nie mieści się w bajcie.
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            Assert.Equal(width, height);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            entries.Add((width, ico.AsSpan(offset, length).ToArray()));
        }

        return entries;
    }

    /// <summary>
    /// Obraz PNG o podanym rozmiarze, z kanałem alfa, przezroczystymi narożnikami poza
    /// zaokrąglonym kaflem i kryjącym środkiem.
    /// </summary>
    private static void AssertIconImage(byte[] png, int size, string name)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngSignature), $"{name}: to nie jest PNG");
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.True(png[25] == PngColourRgba, $"{name}: typ barw {png[25]} zamiast RGBA");

        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);

        var last = size - 1;
        foreach (var (x, y) in new[] { (0, 0), (last, 0), (0, last), (last, last) })
            Assert.True(bitmap.GetPixel(x, y).Alpha == 0, $"{name}: narożnik ({x}, {y}) nie jest przezroczysty");
        Assert.True(bitmap.GetPixel(size / 2, size / 2).Alpha == 255, $"{name}: środek nie jest kryjący");
    }

    [Fact]
    public void PlikIcoMaKompletRozmiarowJakoPngZKanalemAlfa()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Cewka.App", "Assets", "cewka.ico");
        var entries = IcoEntries(File.ReadAllBytes(path));

        Assert.Equal(IcoSizes, entries.Select(e => e.Size).ToArray());
        foreach (var (size, data) in entries)
            AssertIconImage(data, size, $"cewka.ico {size} px");
    }

    [Fact]
    public void IkonaOknaMa256PikseliIKanalAlfa()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Cewka.App", "Assets", "cewka.png");

        AssertIconImage(File.ReadAllBytes(path), 256, "cewka.png");
    }

    [Fact]
    public void IkonaOknaWWindowsPochodziZPlikuIcoAWInnychSystemachZPng()
    {
        var assets = Path.Combine(RepositoryRoot(), "src", "Cewka.App", "Assets");

        Ui.Run(() =>
        {
            // Pasek zadań i Alt+Tab biorą z ICO obraz w swoim rozmiarze; z PNG tylko zmniejszony rysunek 256 px.
            Assert.Equal(File.ReadAllBytes(Path.Combine(assets, "cewka.ico")), ReadAsset(AppIcon.SourceFor(windows: true)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(assets, "cewka.png")), ReadAsset(AppIcon.SourceFor(windows: false)));
        });
    }

    [Fact]
    public void OknoGlowneIOknoUstawienMajaIkoneProgramuDlaBiezacegoSystemu()
    {
        Ui.Run(() =>
        {
            using var h = new PlayerHarness();
            var main = new MainWindow(false, h.Player);
            var settings = new SettingsWindow(new SettingsViewModel(h.Settings, h.Player));
            main.Show();
            settings.Show();

            try
            {
                Assert.Same(AppIcon.Window, main.Icon);
                Assert.Same(AppIcon.Window, settings.Icon);
                Assert.Equal(OperatingSystem.IsWindows() ? "cewka.ico" : "cewka.png", Path.GetFileName(AppIcon.Source.AbsolutePath));
            }
            finally
            {
                settings.Close();
                main.Close();
            }
        });
    }

    private static byte[] ReadAsset(Uri uri)
    {
        using var stream = AssetLoader.Open(uri);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void ObrazyPakietowLinuksaMajaSwojeRozmiaryIKanalAlfa()
    {
        var directory = Path.Combine(RepositoryRoot(), "packaging", "linux", "icons");
        var files = Directory.GetFiles(directory, "cewka-*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(LinuxSizes.Select(s => $"cewka-{s}.png").Order(StringComparer.Ordinal).ToArray(), files);
        foreach (var size in LinuxSizes)
            AssertIconImage(File.ReadAllBytes(Path.Combine(directory, $"cewka-{size}.png")), size, $"cewka-{size}.png");
    }

    [Fact]
    public void IkonaOknaIObrazyLinuksaToTeSameBajtyCoWpisyIco()
    {
        var root = RepositoryRoot();
        var assets = Path.Combine(root, "src", "Cewka.App", "Assets");
        var entries = IcoEntries(File.ReadAllBytes(Path.Combine(assets, "cewka.ico"))).ToDictionary(e => e.Size, e => e.Data);

        Assert.True(entries[256].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(assets, "cewka.png"))),
            "cewka.png różni się od wpisu 256 px w cewka.ico");
        foreach (var size in LinuxSizes.Where(entries.ContainsKey))
        {
            var png = File.ReadAllBytes(Path.Combine(root, "packaging", "linux", "icons", $"cewka-{size}.png"));
            Assert.True(entries[size].AsSpan().SequenceEqual(png), $"cewka-{size}.png różni się od wpisu {size} px w cewka.ico");
        }
    }

    [Fact]
    public void MaleRozmiaryPochodzaZRysunkuUproszczonegoAWiekszeZPelnego()
    {
        var root = RepositoryRoot();
        var ico = IcoEntries(File.ReadAllBytes(Path.Combine(root, "src", "Cewka.App", "Assets", "cewka.ico")))
            .Select(e => (e.Size, e.Data, Name: $"cewka.ico {e.Size} px"));
        var linux = LinuxSizes
            .Select(s => (Size: s, Data: File.ReadAllBytes(Path.Combine(root, "packaging", "linux", "icons", $"cewka-{s}.png")), Name: $"cewka-{s}.png"));

        foreach (var (size, data, name) in ico.Concat(linux))
        {
            using var bitmap = SKBitmap.Decode(data);
            var pixel = bitmap.GetPixel(LeadX * size / 512, LeadY * size / 512);
            var luminance = 0.2126 * pixel.Red + 0.7152 * pixel.Green + 0.0722 * pixel.Blue;

            // Piksel poza kaflem ma zerowe barwy i bez tego warunku uchodziłby za ciemne tło kafla.
            Assert.True(pixel.Alpha >= 250, $"{name}: piksel w miejscu wyprowadzeń ma krycie {pixel.Alpha}");

            if (size <= SimplifiedUpTo)
                Assert.True(luminance < 90, $"{name}: jasny piksel ({luminance:F0}) w miejscu wyprowadzeń, rysunek pełny zamiast uproszczonego");
            else
                Assert.True(luminance > 150, $"{name}: ciemny piksel ({luminance:F0}) w miejscu wyprowadzeń, rysunek uproszczony zamiast pełnego");
        }
    }

    /// <summary>Rozmiary z rysunku uproszczonego, w pliku ICO albo w pakietach Linuksa.</summary>
    public static TheoryData<int> SmallSizes()
    {
        var sizes = new TheoryData<int>();
        foreach (var size in IcoSizes.Union(LinuxSizes).Where(s => s <= SimplifiedUpTo).Order())
            sizes.Add(size);
        return sizes;
    }

    [Theory]
    [MemberData(nameof(SmallSizes))]
    public void KafelMalegoRysunkuKonczySieNaGranicyPikseli(int size)
    {
        var root = RepositoryRoot();
        var png = IcoSizes.Contains(size)
            ? IcoEntries(File.ReadAllBytes(Path.Combine(root, "src", "Cewka.App", "Assets", "cewka.ico"))).Single(e => e.Size == size).Data
            : File.ReadAllBytes(Path.Combine(root, "packaging", "linux", "icons", $"cewka-{size}.png"));
        using var bitmap = SKBitmap.Decode(png);

        // Margines kafla to pełne piksele (1 px poniżej 32 px, 2 px w 32 px), więc wzdłuż krawędzi
        // nie ma półprzezroczystego pasa. Wygładzanie w Edge zostawia poza kaflem ślad krycia do 3/255.
        const byte Trace = 8;
        const byte Opaque = 250;
        var margin = Math.Max(1, size / 16);
        var last = size - 1;
        for (var i = size / 3; i <= 2 * size / 3; i++)
        {
            for (var depth = 0; depth <= margin; depth++)
            {
                foreach (var (x, y) in new[] { (i, depth), (i, last - depth), (depth, i), (last - depth, i) })
                {
                    var alpha = bitmap.GetPixel(x, y).Alpha;
                    if (depth < margin)
                        Assert.True(alpha <= Trace, $"{size} px: piksel ({x}, {y}) poza kaflem ma krycie {alpha}");
                    else
                        Assert.True(alpha >= Opaque, $"{size} px: piksel ({x}, {y}) na skraju kafla ma krycie {alpha}");
                }
            }
        }
    }

    [Theory]
    [InlineData("tools/build-packages.sh")]
    [InlineData("tools/test-packages.sh")]
    [InlineData("packaging/arch/PKGBUILD")]
    public void SkryptyPakietowInstalujaWszystkieRozmiaryIkony(string script)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), script));
        var loop = SizeLoop().Match(text);

        Assert.True(loop.Success, $"{script}: brak pętli po rozmiarach ikony");
        var sizes = loop.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        Assert.Equal(LinuxSizes, sizes);
        Assert.Contains("hicolor/scalable/apps/cewka.svg", text);
    }

    [Theory]
    [InlineData("cewka.svg")]
    [InlineData("cewka-maly.svg")]
    public void RysunekIkonyJestCzystymWektorem(string file)
    {
        var path = Path.Combine(RepositoryRoot(), "assets", "ikona", file);
        var svg = XDocument.Load(path).Root!;

        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Equal("0 0 512 512", (string?)svg.Attribute("viewBox"));

        string[] forbidden = ["image", "text", "script", "style", "foreignObject", "use", "a"];
        var elements = svg.DescendantsAndSelf().Select(e => e.Name.LocalName).Distinct().ToList();
        Assert.Empty(elements.Intersect(forbidden));

        // Odwołania wyłącznie do definicji w tym samym pliku, żadnych adresów ani plików.
        foreach (var attribute in svg.DescendantsAndSelf().Attributes())
        {
            Assert.False(attribute.Name.LocalName == "href", $"{file}: atrybut href");
            var urls = UrlReference().Matches(attribute.Value);
            Assert.All(urls, m => Assert.StartsWith("#", m.Groups[1].Value));
        }
    }
}
