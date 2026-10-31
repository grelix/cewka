using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Test tylko dla Windows; gdzie indziej pomijany.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Dotyczy wyłącznie Windows.";
    }
}

/// <summary>
/// Odpowiedzi prawdziwego okna Windows na WM_NCHITTEST nad paskiem tytułu. Pomiar robi osobny
/// proces (Cewka.WindowProbe), bo w tym procesie Avalonia działa na silniku bez okien.
/// <para>
/// Okna pomiaru są ukryte w kompozytorze, leżą poza ekranami, nie dostają fokusu ani przycisku na
/// pasku zadań i zamykają się od razu po pomiarze. Sam ten test: <c>dotnet test
/// tests/Cewka.App.Tests --filter Category=OknoWindows</c>.
/// </para>
/// </summary>
[Trait("Category", "OknoWindows")]
public sealed class TitleBarHitTestTests
{
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtMaxButton = 9;
    private const long WsSysMenu = 0x00080000;

    [WindowsFact]
    public void PrzyciskiPaskaSaKlikalneAPustyPasekPrzesuwaOkno()
    {
        var results = Measure();

        // Okno nie mogło pokazać się na pulpicie.
        foreach (var window in new[] { "glowne", "ustawienia" })
        {
            Assert.Equal("1", results[$"{window}|ukryte"]);
            var rect = results[$"{window}|prostokat"].Split(',').Select(int.Parse).ToArray();
            Assert.True(rect[2] < -10000 && rect[3] < -10000, $"okno {window} na ekranie: {results[$"{window}|prostokat"]}");
        }

        var failures = new List<string>();
        void Expect(string key, int expected)
        {
            if (results.GetValueOrDefault(key) != expected.ToString()) failures.Add($"{key}: {results.GetValueOrDefault(key)}, oczekiwano {expected}");
        }

        foreach (var button in new[] { "Korektor", "Kolejka", "Motyw", "Ustawienia", "Minimalizuj", "Zamknij" })
            Expect($"glowne|{button}", HtClient);

        Expect("glowne|Maksymalizuj", HtMaxButton);
        Expect("glowne|PustyPasek", HtCaption);
        Expect("glowne|Odtworz", HtClient);
        Expect("ustawienia|Zamknij", HtClient);
        Expect("ustawienia|PustyPasek", HtCaption);

        // Bez menu systemowego system nie uznaje maksymalizacji za dostępną i nie pokazuje układów przyciągania.
        if ((long.Parse(results["glowne|styl"]) & WsSysMenu) == 0) failures.Add("glowne|styl: brak WS_SYSMENU");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static Dictionary<string, string> Measure()
    {
        var probe = typeof(TitleBarHitTestTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "WindowProbePath").Value!;
        Assert.True(File.Exists(probe), $"Brak programu pomiaru: {probe}");

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { probe },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CEWKA_NULL_AUDIO"] = "1";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(90)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Pomiar nie skończył się w 90 s.");
        }

        Assert.True(process.ExitCode == 0, $"Pomiar zakończył się kodem {process.ExitCode}: {errors.Result}");

        return output.Result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length == 3)
            .ToDictionary(parts => $"{parts[0]}|{parts[1]}", parts => parts[2]);
    }
}
