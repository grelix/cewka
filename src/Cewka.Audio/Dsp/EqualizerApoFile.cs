using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cewka.Audio.Dsp;

/// <summary>Wynik odczytu pliku w formacie Equalizer APO.</summary>
/// <param name="Preamp">Suma wierszy <c>Preamp</c> w decybelach, bez przycięcia; null, gdy plik ich nie ma.</param>
/// <param name="Bands">Pasma w kolejności z pliku, z identyfikatorami 0, 1, 2…, po przycięciu do zakresów.</param>
/// <param name="Skipped">Wiersze <c>Filter</c> nieobsługiwanego rodzaju, nie do odczytania albo ponad limit pasm.</param>
public sealed record EqualizerApoPreset(double? Preamp, IReadOnlyList<ParametricBand> Bands, int Skipped);

/// <summary>
/// Odczyt i zapis pasm w tekstowym formacie konfiguracji Equalizer APO, w którym publikuje zestawy
/// między innymi AutoEq. Obsługiwane są wiersze <c>Preamp: -6.1 dB</c> oraz
/// <c>Filter 1: ON PK Fc 1000 Hz Gain -2.5 dB Q 1.41</c> z rodzajami PK, PEQ, LSC i HSC; pozostałe
/// polecenia programu są pomijane po cichu.
/// </summary>
public static class EqualizerApoFile
{
    /// <summary>Plik konfiguracji korektora jest mały; większy to pomyłka w wyborze pliku.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>Dobroć półki podanej bez Q: nachylenie Butterwortha.</summary>
    public const double DefaultShelfQ = 0.707;

    private const string Number = @"([+\-−]?(?:\d+(?:[.,]\d*)?|[.,]\d+))";

    private static readonly Regex PreampLine = new(
        $@"^preamp\s*:\s*{Number}\s*(?:db)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FilterLine = new(
        @"^filter\s*\d*\s*:\s*(on|off)\s+([a-z]+)\b\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FrequencyPart = new(
        $@"\bfc\s+{Number}\s*(?:hz)?(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GainPart = new(
        $@"\bgain\s+{Number}\s*(?:db)?(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QPart = new(
        $@"\bq\s+{Number}(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Wczytuje plik. Za duży albo nieczytelny kończy się wyjątkiem z opisem.</summary>
    /// <exception cref="InvalidDataException">Plik większy niż <see cref="MaxFileBytes"/>.</exception>
    /// <exception cref="IOException">Błąd odczytu.</exception>
    public static EqualizerApoPreset Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxFileBytes)
            throw new InvalidDataException($"Plik korektora ma {stream.Length} B, dopuszczalne jest {MaxFileBytes} B.");

        // Długość sprawdzona wyżej może urosnąć w trakcie odczytu; czytane jest najwyżej o bajt więcej.
        var buffer = new byte[MaxFileBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0) length += read;

        if (length > MaxFileBytes)
            throw new InvalidDataException($"Plik korektora przekracza {MaxFileBytes} B.");

        return Parse(Decode(buffer.AsSpan(0, length)));
    }

    /// <summary>UTF-8 albo UTF-16 ze znacznikiem BOM, potem poprawny UTF-8, na końcu jednobajtowy zapis ANSI.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Liczby i słowa kluczowe są w ASCII; strona kodowa ma znaczenie tylko dla komentarzy.
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static EqualizerApoPreset Parse(string text)
    {
        double? preamp = null;
        var bands = new List<ParametricBand>();
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;

            var preampMatch = PreampLine.Match(line);
            if (preampMatch.Success)
            {
                if (TryNumber(preampMatch.Groups[1].Value, out var value)) preamp = (preamp ?? 0) + value;
                continue;
            }

            if (!line.StartsWith("filter", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryFilter(line, out var band))
            {
                skipped++;
                continue;
            }

            if (bands.Count >= ParametricEqualiser.MaxBands)
            {
                skipped++;
                continue;
            }

            bands.Add((band with { Id = bands.Count }).Clamped());
        }

        return new EqualizerApoPreset(preamp, bands, skipped);
    }

    /// <summary>Tekst pliku: przedwzmacniacz i pasma w kolejności, kropka dziesiętna, wiersze zakończone LF.</summary>
    public static string Format(double preamp, IEnumerable<ParametricBand> bands)
    {
        var text = new StringBuilder();
        text.Append("Preamp: ").Append(FormatNumber(preamp, "0.##")).Append(" dB\n");

        var number = 1;
        foreach (var band in bands)
        {
            var type = band.Type switch
            {
                ParametricFilterType.LowShelf => "LSC",
                ParametricFilterType.HighShelf => "HSC",
                _ => "PK",
            };

            text.Append("Filter ").Append(number++).Append(": ").Append(band.Enabled ? "ON " : "OFF ")
                .Append(type)
                .Append(" Fc ").Append(FormatNumber(band.Frequency, "0.##")).Append(" Hz")
                .Append(" Gain ").Append(FormatNumber(band.Gain, "0.##")).Append(" dB")
                .Append(" Q ").Append(FormatNumber(band.Q, "0.###"))
                .Append('\n');
        }

        return text.ToString();
    }

    private static string FormatNumber(double value, string format)
    {
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static bool TryFilter(string line, out ParametricBand band)
    {
        band = default;

        var match = FilterLine.Match(line);
        if (!match.Success) return false;

        var enabled = match.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
        var type = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "PK" or "PEQ" => ParametricFilterType.Peak,
            "LSC" => ParametricFilterType.LowShelf,
            "HSC" => ParametricFilterType.HighShelf,
            _ => (ParametricFilterType?)null,
        };

        if (type is null) return false;

        var parameters = match.Groups[3].Value;
        if (!TryPart(FrequencyPart, parameters, out var frequency) || !TryPart(GainPart, parameters, out var gain))
            return false;

        if (!TryPart(QPart, parameters, out var q))
        {
            if (type == ParametricFilterType.Peak) return false;
            q = DefaultShelfQ;
        }

        if (frequency <= 0 || q <= 0) return false;

        band = new ParametricBand(0, type.Value, frequency, gain, q, enabled);
        return true;
    }

    private static bool TryPart(Regex pattern, string parameters, out double value)
    {
        value = 0;
        var match = pattern.Match(parameters);
        return match.Success && TryNumber(match.Groups[1].Value, out value);
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Replace('−', '-').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
