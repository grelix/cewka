using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Tests.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Cewka.Audio.Tests;

/// <summary>
/// Albumy AAC bez przerw: rozbieg kodera i dopełnienie ostatniej ramki nie grają. Pomiar
/// z 18.11.2026: Media Foundation oddaje wszystkie ramki AAC (1024 ramki rozbiegu przed
/// sygnałem, 656 dopełnienia za nim) bez względu na znacznik i listę edycji; GStreamer 1.24
/// przycina sam według znacznika albo listy edycji, a gdy plik ma oba, przycina dwa razy.
/// Media Foundation na maszynach GitHuba (windows-latest) przy liście edycji liczy czas od
/// początku edycji; dekoder rozpoznaje to sondą końca osi. Komunikaty porażek podają surowe
/// liczby rozpoznania, żeby porażka w innym środowisku mówiła, jak zachowuje się tamten system.
/// </summary>
public sealed class M4aGaplessTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-test-").FullName;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public enum Description { Smpb, EditList, Both }

    private static byte[] Described(byte[] file, int frames, Description description)
    {
        if (description is Description.EditList or Description.Both)
            file = Mp4Samples.WithEditList(file, Mp4Samples.Delay, frames);

        if (description is Description.Smpb or Description.Both)
            file = Mp4Samples.WithSmpb(file, Mp4Samples.Delay, Mp4Samples.Padding(frames), frames);

        return file;
    }

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var all = new List<float>();
        var buffer = new float[4096 * 2];
        int read;
        while ((read = decoder.Read(buffer)) > 0) all.AddRange(buffer.AsSpan(0, read * 2));
        return [.. all];
    }

    private static string Report(IAudioDecoder decoder) =>
        decoder is TimedSampleDecoder timed ? timed.TimelineReport : decoder.GetType().Name;

    /// <summary>Rozpoznanie osi czasu i liczba ramek wyniku dla pliku, do komunikatu porażki.</summary>
    private static string Diagnose(string path)
    {
        using var decoder = SystemCodecs.Open(path, Mp4Samples.Rate, 2, null);
        var frames = ReadAll(decoder).Length / 2;
        return $"{Path.GetFileName(path)}: wynik {frames} ramek; {Report(decoder)}";
    }

    private static readonly float[] Reference = Mp4Samples.Reference(Mp4Samples.FirstFrames + Mp4Samples.SecondFrames, Mp4Samples.Rate);

    [MediaFoundationTheory]
    [InlineData(Description.Smpb)]
    [InlineData(Description.EditList)]
    [InlineData(Description.Both)]
    public void MediaFoundationOddajeSamoNagranie(Description description)
    {
        var path = Write("a.m4a", Described(Mp4Samples.First, Mp4Samples.FirstFrames, description));

        using var decoder = SystemCodecs.Open(path, Mp4Samples.Rate, 2, null);
        var total = decoder.TotalFrames;
        var output = ReadAll(decoder);
        var report = $"długość {total}, wynik {output.Length / 2} ramek; {Report(decoder)}";

        Assert.True(total == Mp4Samples.FirstFrames, report);
        Assert.True(output.Length / 2 == Mp4Samples.FirstFrames, report);
        var signal = Mp4Samples.Offset(output, Reference, 5000, 40000, 3000);
        Assert.True(signal == 5000, $"ramka 5000 nagrania w wyniku na {signal}; {report}");
    }

    /// <summary>Bez opisu rozbiegu zostaje on w nagraniu, ale dopełnienie za końcem tablicy czasów nie gra.</summary>
    [MediaFoundationFact]
    public void MediaFoundationBezOpisuObcinaTylkoDopelnienie()
    {
        var path = Write("a.m4a", Mp4Samples.First);

        using var decoder = SystemCodecs.Open(path, Mp4Samples.Rate, 2, null);
        var output = ReadAll(decoder);

        Assert.Equal(Mp4Samples.Delay + Mp4Samples.FirstFrames, output.Length / 2);
        Assert.Equal(5000 + Mp4Samples.Delay, Mp4Samples.Offset(output, Reference, 5000, 40000, 3000));
    }

    /// <summary>Przewinięcie trafia w ramkę nagrania co do próbki, nie w początek ramki AAC.</summary>
    [MediaFoundationTheory]
    [InlineData(30000)]
    [InlineData(1)]
    [InlineData(50555)]
    public void MediaFoundationPrzewijaCoDoRamki(int target)
    {
        var path = Write("a.m4a", Described(Mp4Samples.First, Mp4Samples.FirstFrames, Description.Smpb));

        using var decoder = SystemCodecs.Open(path, Mp4Samples.Rate, 2, null);
        decoder.Seek(target);
        Assert.Equal(target, decoder.Position);

        var output = ReadAll(decoder);

        Assert.Equal(Mp4Samples.FirstFrames - target, output.Length / 2);
        Assert.Equal(4000, Mp4Samples.Offset(output, Reference[target..], 4000, 12000, 2000));
    }

    [MediaFoundationFact]
    public void MediaFoundationPrzewiniecieZaKoniecNicNieOddaje()
    {
        var path = Write("a.m4a", Described(Mp4Samples.First, Mp4Samples.FirstFrames, Description.Smpb));

        using var decoder = SystemCodecs.Open(path, Mp4Samples.Rate, 2, null);
        decoder.Seek(Mp4Samples.FirstFrames + 10);

        Assert.Empty(ReadAll(decoder));
    }

    /// <summary>
    /// Dwa pliki z sygnałem ciągłym grane po sobie w silniku (wyjście 48 kHz): bez wstawki ciszy
    /// na granicy i bez przesunięcia drugiego względem pierwszego.
    /// </summary>
    [SystemAacTheory]
    [InlineData(Description.Smpb)]
    [InlineData(Description.EditList)]
    public void DwaPlikiGrajaJakJedenSygnal(Description description) => AssertContinuous(description);

    /// <summary>GStreamer 1.24 przy obu opisach naraz przycina dwa razy i gubi początek nagrania.</summary>
    [MediaFoundationFact]
    public void DwaPlikiZObomaOpisamiGrajaJakJedenSygnal() => AssertContinuous(Description.Both);

    private void AssertContinuous(Description description)
    {
        var first = Write("a.m4a", Described(Mp4Samples.First, Mp4Samples.FirstFrames, description));
        var second = Write("b.m4a", Described(Mp4Samples.Second, Mp4Samples.SecondFrames, description));

        var output = Play(first, second);

        var rate = EngineHarness.Rate;
        var reference = Mp4Samples.Reference((int)((long)(Mp4Samples.FirstFrames + Mp4Samples.SecondFrames) * rate / Mp4Samples.Rate), rate);
        var boundary = (int)((long)Mp4Samples.FirstFrames * rate / Mp4Samples.Rate);

        // Ten sam odstęp wzorca przed granicą i za nią; rozbieg albo dopełnienie przesuwa drugi plik o ponad tysiąc ramek.
        var before = Mp4Samples.Offset(output, reference, boundary - 38000, 30000, 3500) - (boundary - 38000);
        var after = Mp4Samples.Offset(output, reference, boundary + 8000, 30000, 3500) - (boundary + 8000);
        Assert.True(Math.Abs(after - before) <= 3,
            $"drugi plik przesunięty o {after - before} ramek 48 kHz; {Diagnose(first)}; {Diagnose(second)}");

        // Sygnał ma wartość skuteczną około 0,2; wstawka ciszy schodzi do zera.
        var quietest = Mp4Samples.QuietestWindow(output, boundary + before - 3000, boundary + before + 3000);
        Assert.True(quietest > 0.08, $"najcichsze okno przy granicy {quietest:F3}");

        var expected = (Mp4Samples.FirstFrames + Mp4Samples.SecondFrames) * (double)rate / Mp4Samples.Rate;
        Assert.InRange(output.Length / 2 - before, expected - 64, expected + 64);
    }

    /// <summary>
    /// Nic nie sprawdza poza spójnością: wypisuje, jak system tego środowiska podaje czas
    /// próbek plików z każdym opisem rozbiegu.
    /// </summary>
    [MediaFoundationFact]
    public void MediaFoundationDiagnostyka()
    {
        var variants = new (string Name, byte[] File)[]
        {
            ("bez opisu", Mp4Samples.First),
            ("iTunSMPB", Described(Mp4Samples.First, Mp4Samples.FirstFrames, Description.Smpb)),
            ("lista edycji", Described(Mp4Samples.First, Mp4Samples.FirstFrames, Description.EditList)),
            ("oba", Described(Mp4Samples.First, Mp4Samples.FirstFrames, Description.Both)),
        };

        foreach (var (name, file) in variants)
        {
            var line = $"{name}: {Diagnose(Write("diagnostyka.m4a", file))}";
            log.WriteLine(line);
            Assert.DoesNotContain("wynik 0 ramek", line);
        }
    }

    private static float[] Play(params string[] files)
    {
        using var h = new EngineHarness(1024);
        var paths = files.Select((file, i) =>
            h.Track($"plik{i}", () => SystemCodecs.Open(file, EngineHarness.Rate, EngineHarness.Channels, null))).ToArray();

        h.Engine.SetQueue(paths);

        // Okres idzie dopiero, gdy dekoder go przygotował: cisza niedoboru na zajętej maszynie
        // wyglądałaby jak przerwa między utworami.
        var total = Stopwatch.StartNew();
        while (h.Finished == 0)
        {
            if (total.ElapsedMilliseconds > 30000) throw new TimeoutException("Kolejka nie doszła do końca.");

            var waited = Stopwatch.StartNew();
            while (h.Engine.BufferedFrames < 1024 && waited.ElapsedMilliseconds < 1000) Thread.Sleep(1);
            h.Output.Pump();
        }

        // Za końcem kolejki wyjście dostaje już tylko ciszę.
        var samples = h.Output.Samples;
        var end = samples.Length / 2;
        while (end > 0 && samples[2 * end - 2] == 0 && samples[2 * end - 1] == 0) end--;
        return samples[..(2 * end)];
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
