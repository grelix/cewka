using System.Globalization;
using System.Text;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Odczyt i zapis pasm korektora w formacie Equalizer APO.</summary>
public class EqualizerApoFileTests
{
    /// <summary>Plik AutoEq dla Sennheiser HD 650, w postaci, w jakiej go publikują.</summary>
    private const string AutoEqHd650 = """
        Preamp: -6.1 dB
        Filter 1: ON LSC Fc 105 Hz Gain 6.4 dB Q 0.70
        Filter 2: ON PK Fc 8800 Hz Gain 5.1 dB Q 1.42
        Filter 3: ON HSC Fc 10000 Hz Gain -2.1 dB Q 0.70
        Filter 4: ON PK Fc 5332 Hz Gain -1.1 dB Q 5.75
        """;

    [Fact]
    public void ReadsAnAutoEqPreset()
    {
        var preset = EqualizerApoFile.Parse(AutoEqHd650);

        Assert.Equal(-6.1, preset.Preamp);
        Assert.Equal(0, preset.Skipped);
        Assert.Equal(
            [
                new ParametricBand(0, ParametricFilterType.LowShelf, 105, 6.4, 0.70),
                new ParametricBand(1, ParametricFilterType.Peak, 8800, 5.1, 1.42),
                new ParametricBand(2, ParametricFilterType.HighShelf, 10000, -2.1, 0.70),
                new ParametricBand(3, ParametricFilterType.Peak, 5332, -1.1, 5.75),
            ],
            preset.Bands);
    }

    /// <summary>
    /// Wielkość liter i odstępy dowolne, numer filtru nieobowiązkowy, przecinek dziesiętny, komentarze
    /// i puste wiersze, wyłączone pasmo, półka bez dobroci, PEQ jako dzwon, końce wierszy CRLF.
    /// </summary>
    [Fact]
    public void AcceptsTheVariantsOfTheSyntax()
    {
        const string text = "# zestaw\r\n\r\n  preamp:-3,5 DB  \r\nfilter: on peq fc 1000 hz gain -2,5 db q 1,41 # dzwon\r\n" +
                            "FILTER 7 : OFF lsc FC 80 HZ GAIN 4 DB\r\nFilter 8: ON HSC Fc 12000 Hz Gain −3 dB\r\n";

        var preset = EqualizerApoFile.Parse(text);

        Assert.Equal(-3.5, preset.Preamp);
        Assert.Equal(0, preset.Skipped);
        Assert.Equal(
            [
                new ParametricBand(0, ParametricFilterType.Peak, 1000, -2.5, 1.41),
                new ParametricBand(1, ParametricFilterType.LowShelf, 80, 4, EqualizerApoFile.DefaultShelfQ, Enabled: false),
                new ParametricBand(2, ParametricFilterType.HighShelf, 12000, -3, EqualizerApoFile.DefaultShelfQ),
            ],
            preset.Bands);
    }

    /// <summary>
    /// Inne rodzaje filtrów i wiersze Filter nie do odczytania są liczone jako pominięte, pozostałe
    /// polecenia programu nie, a nadmiar ponad 20 pasm też jest liczony.
    /// </summary>
    [Fact]
    public void CountsSkippedFilters()
    {
        var lines = new List<string>
        {
            "Device: Speakers",
            "Channel: L",
            "Filter 1: ON LP Fc 100 Hz",
            "Filter 2: ON PK Fc 100 Hz Gain 3 dB",
            "Filter 3: ON PK Fc abc Hz Gain 3 dB Q 1",
            "Filter 4: ON NO",
            "Include: inny.txt",
        };
        lines.AddRange(Enumerable.Range(1, 22).Select(i => $"Filter: ON PK Fc {100 * i} Hz Gain 1 dB Q 1"));

        var preset = EqualizerApoFile.Parse(string.Join('\n', lines));

        Assert.Null(preset.Preamp);
        Assert.Equal(ParametricEqualiser.MaxBands, preset.Bands.Count);
        Assert.Equal(4 + 2, preset.Skipped);
        Assert.Equal(Enumerable.Range(0, 20), preset.Bands.Select(band => band.Id));
    }

    [Fact]
    public void FileWithoutFiltersHasNoBands()
    {
        var preset = EqualizerApoFile.Parse("To nie jest plik korektora.\nAni ten wiersz.");
        Assert.Empty(preset.Bands);
        Assert.Null(preset.Preamp);
        Assert.Equal(0, preset.Skipped);
    }

    /// <summary>Wartości spoza zakresów są przycinane, a przedwzmacniacz zostaje surowy: przycina go wołający z komunikatem.</summary>
    [Fact]
    public void ValuesOutsideTheRangesAreClampedExceptThePreamp()
    {
        var preset = EqualizerApoFile.Parse("Preamp: -20 dB\nFilter 1: ON PK Fc 30000 Hz Gain 40 dB Q 50");

        Assert.Equal(-20, preset.Preamp);
        Assert.Equal(new ParametricBand(0, ParametricFilterType.Peak, 20000, 24, 20), preset.Bands[0]);
    }

    [Fact]
    public void ExportWritesTheSameFormat()
    {
        ParametricBand[] bands =
        [
            new(5, ParametricFilterType.Peak, 1000, -2.5, 1.41),
            new(9, ParametricFilterType.LowShelf, 105.5, 6.4, 0.7, Enabled: false),
            new(2, ParametricFilterType.HighShelf, 10000, -0.0, 0.707),
        ];

        var text = EqualizerApoFile.Format(-6.1, bands);

        Assert.Equal(
            "Preamp: -6.1 dB\n" +
            "Filter 1: ON PK Fc 1000 Hz Gain -2.5 dB Q 1.41\n" +
            "Filter 2: OFF LSC Fc 105.5 Hz Gain 6.4 dB Q 0.7\n" +
            "Filter 3: ON HSC Fc 10000 Hz Gain 0 dB Q 0.707\n",
            text);
    }

    /// <summary>Zapis w kulturze z przecinkiem dziesiętnym nadal daje kropkę.</summary>
    [Fact]
    public void ExportUsesADecimalPointInAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture.Clone() is CultureInfo culture
                ? WithComma(culture)
                : previous;
            Assert.Contains("Gain -2.5 dB", EqualizerApoFile.Format(0, [new ParametricBand(0, ParametricFilterType.Peak, 1000, -2.5, 1)]));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        static CultureInfo WithComma(CultureInfo culture)
        {
            culture.NumberFormat.NumberDecimalSeparator = ",";
            return culture;
        }
    }

    [Theory]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8")]
    [InlineData("ansi")]
    public void ReadsEveryAcceptedEncoding(string encoding)
    {
        const string text = "# Słuchawki zamknięte, «zestaw»\nPreamp: -2 dB\nFilter 1: ON PK Fc 250 Hz Gain 2 dB Q 1";
        var bytes = encoding switch
        {
            "utf8bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            "ansi" => Encoding.Latin1.GetBytes(text.Replace("ł", "l").Replace("ę", "e")),
            _ => Encoding.UTF8.GetBytes(text),
        };

        var path = Path.Combine(Path.GetTempPath(), $"cewka-apo-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(path, bytes);
            var preset = EqualizerApoFile.Read(path);

            Assert.Equal(-2, preset.Preamp);
            Assert.Equal(new ParametricBand(0, ParametricFilterType.Peak, 250, 2, 1), Assert.Single(preset.Bands));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Plik większy niż 256 KiB nie jest czytany w całości, tylko odrzucany z opisem.</summary>
    [Fact]
    public void RejectsAFileOverTheLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cewka-apo-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllBytes(path, new byte[EqualizerApoFile.MaxFileBytes + 1]);
            Assert.Throws<InvalidDataException>(() => EqualizerApoFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Zapis i ponowny odczyt dają te same pasma.</summary>
    [Fact]
    public void ExportAndImportRoundTrip()
    {
        var random = new Random(2026);
        var bands = Enumerable.Range(0, 20).Select(i => new ParametricBand(i,
            (ParametricFilterType)random.Next(3),
            Math.Round(20 + random.NextDouble() * 19980, 2),
            Math.Round(-24 + random.NextDouble() * 48, 2),
            Math.Round(0.1 + random.NextDouble() * 19.9, 3),
            random.Next(2) == 0)).ToArray();

        var preset = EqualizerApoFile.Parse(EqualizerApoFile.Format(-7.25, bands));

        Assert.Equal(-7.25, preset.Preamp);
        Assert.Equal(bands, preset.Bands);
    }

    /// <summary>
    /// Losowe pliki ze stałym ziarnem: wiersze poprawne w losowym zapisie (wielkość liter, odstępy,
    /// separator, numer, komentarz) przeplecione śmieciami. Odczyt zgadza się z tym, co wygenerowano,
    /// i nigdy nie rzuca wyjątku.
    /// </summary>
    [Fact]
    public void RandomFilesParseAsGenerated()
    {
        var random = new Random(20261118);

        for (var round = 0; round < 500; round++)
        {
            var lines = new List<string>();
            var expected = new List<ParametricBand>();
            var expectedSkipped = 0;
            double? expectedPreamp = null;

            var count = random.Next(0, 30);
            for (var i = 0; i < count; i++)
            {
                switch (random.Next(6))
                {
                    case 0:
                    {
                        var value = Math.Round(-15 + random.NextDouble() * 30, 1);
                        expectedPreamp = (expectedPreamp ?? 0) + value;
                        lines.Add($"{Case(random, "Preamp")}{Space(random)}:{Space(random)}{Num(random, value)}{Space(random)}{Case(random, "dB")}");
                        break;
                    }
                    case 1:
                        lines.Add(Garbage(random));
                        break;
                    case 2:
                        lines.Add($"{Case(random, "Filter")} {random.Next(1, 30)}: ON {Case(random, "BP")} Fc 100 Hz Q 2");
                        expectedSkipped++;
                        break;
                    default:
                    {
                        var type = (ParametricFilterType)random.Next(3);
                        var frequency = Math.Round(20 + random.NextDouble() * 19980, 1);
                        var gain = Math.Round(-24 + random.NextDouble() * 48, 1);
                        var q = Math.Round(0.1 + random.NextDouble() * 10, 2);
                        var on = random.Next(4) > 0;
                        var name = type switch
                        {
                            ParametricFilterType.LowShelf => "LSC",
                            ParametricFilterType.HighShelf => "HSC",
                            _ => random.Next(2) == 0 ? "PK" : "PEQ",
                        };

                        var number = random.Next(2) == 0 ? string.Empty : $" {random.Next(1, 99)}";
                        var comment = random.Next(3) == 0 ? $" # {Garbage(random)}" : string.Empty;
                        lines.Add($"{Space(random)}{Case(random, "Filter")}{number}{Space(random)}:{Space(random)}" +
                                  $"{Case(random, on ? "ON" : "OFF")} {Case(random, name)} {Case(random, "Fc")} {Num(random, frequency)} {Case(random, "Hz")} " +
                                  $"{Case(random, "Gain")} {Num(random, gain)} {Case(random, "dB")} {Case(random, "Q")} {Num(random, q)}{comment}");

                        if (expected.Count < ParametricEqualiser.MaxBands)
                            expected.Add(new ParametricBand(expected.Count, type, frequency, gain, q, on));
                        else
                            expectedSkipped++;
                        break;
                    }
                }
            }

            var newline = random.Next(2) == 0 ? "\n" : "\r\n";
            var preset = EqualizerApoFile.Parse(string.Join(newline, lines));

            Assert.Equal(expected, preset.Bands);
            Assert.Equal(expectedSkipped, preset.Skipped);
            if (expectedPreamp is null) Assert.Null(preset.Preamp);
            else Assert.Equal(expectedPreamp.Value, preset.Preamp!.Value, 9);
        }
    }

    /// <summary>Losowe bajty jako plik: odczyt kończy się wynikiem, nigdy wyjątkiem ani zawieszeniem.</summary>
    [Fact]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(77);
        for (var round = 0; round < 300; round++)
        {
            var bytes = new byte[random.Next(0, 4096)];
            random.NextBytes(bytes);
            var preset = EqualizerApoFile.Parse(EqualizerApoFile.Decode(bytes));
            Assert.True(preset.Bands.Count <= ParametricEqualiser.MaxBands);
        }
    }

    private static string Case(Random random, string word) => random.Next(3) switch
    {
        0 => word.ToUpperInvariant(),
        1 => word.ToLowerInvariant(),
        _ => word,
    };

    private static string Space(Random random) => new(' ', random.Next(0, 3));

    private static string Num(Random random, double value)
    {
        var text = value.ToString("0.##", CultureInfo.InvariantCulture);
        return random.Next(2) == 0 ? text.Replace('.', ',') : text;
    }

    private static string Garbage(Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz :.,-+0123456789ąęłż";
        var length = random.Next(0, 40);
        var text = new char[length];
        for (var i = 0; i < length; i++) text[i] = alphabet[random.Next(alphabet.Length)];

        // Śmieć nie może przypadkiem zaczynać się od słowa kluczowego.
        return "x" + new string(text);
    }
}
