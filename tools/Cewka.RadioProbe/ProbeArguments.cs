using System.Globalization;

namespace Cewka.RadioProbe;

/// <summary>Opcje wiersza poleceń. Limity 6 s i 200 KB są twarde: strumienia się tu nie słucha, tylko próbkuje.</summary>
internal sealed record ProbeArguments
{
    public const double MaxSeconds = 6;
    public const int MaxKilobytes = 200;
    public const int MinKilobytes = 32;
    public const int MaxParallel = 8;

    public const string Usage = """
        Cewka.RadioProbe --stations <plik|katalog> [--url <adres>] [--country PL] [--measured-from PL]
                         [--loudness 15] [--logos] [--parallel 2] [--seconds 6] [--max-kb 200] [--limit 5]
                         [--report wynik.json]

          --stations       lista stacji w pliku JSON albo katalog takich plików
          --url            pojedynczy adres; może prowadzić do sieci lokalnej
          --country        tylko stacje z tego kraju; przy --url strona kodowa tytułów
          --measured-from  kraj, z którego wykonano pomiar (geoblokady)
          --loudness       pomiar głośności dla tylu pierwszych stacji
          --logos          sprawdza też logo: status, format, wymiary
          --parallel       ile adresów naraz, do 8; do jednego hosta zawsze jedno zapytanie
          --seconds        limit czasu na adres, do 6
          --max-kb         limit danych na adres, od 32 do 200
          --limit          najwyżej tyle stacji
          --report         plik raportu JSON poza repozytorium; obok powstaje raport tekstowy

        Kod wyjścia: 0 bez błędów, 1 część adresów z błędem, 2 błąd narzędzia.
        """;

    public string? Stations { get; private init; }
    public string? Url { get; private init; }
    public string? Country { get; private init; }
    public string? MeasuredFrom { get; private init; }
    public int LoudnessStations { get; private init; }
    public bool Logos { get; private init; }
    public int Parallel { get; private init; } = 2;
    public double Seconds { get; private init; } = MaxSeconds;
    public int MaxKb { get; private init; } = MaxKilobytes;
    public int? Limit { get; private init; }
    public string? Report { get; private init; }

    public static bool TryParse(string[] args, out ProbeArguments? parsed, out string? error)
    {
        parsed = null;
        error = null;
        var result = new ProbeArguments();

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--logos")
            {
                result = result with { Logos = true };
                continue;
            }

            if (!IsKnownWithValue(option))
            {
                error = $"Nieznana opcja: {option}";
                return false;
            }

            if (++i == args.Length)
            {
                error = $"Brak wartości opcji {option}";
                return false;
            }

            var next = Apply(result, option, args[i]);
            if (next is null)
            {
                error = $"Niedozwolona wartość opcji {option}: {args[i]}";
                return false;
            }

            result = next;
        }

        if (result.Stations is null && result.Url is null)
        {
            error = "Podaj --stations albo --url.";
            return false;
        }

        parsed = result;
        return true;
    }

    private static bool IsKnownWithValue(string option) => option is "--stations" or "--url" or "--country"
        or "--measured-from" or "--loudness" or "--parallel" or "--seconds" or "--max-kb" or "--limit" or "--report";

    private static ProbeArguments? Apply(ProbeArguments a, string option, string value) => option switch
    {
        "--stations" => a with { Stations = value },
        "--url" => a with { Url = value },
        "--report" => a with { Report = value },
        "--country" => CountryCode(value) is { } code ? a with { Country = code } : null,
        "--measured-from" => CountryCode(value) is { } code ? a with { MeasuredFrom = code } : null,
        "--loudness" => Number(value, 0, int.MaxValue) is { } n ? a with { LoudnessStations = n } : null,
        "--parallel" => Number(value, 1, MaxParallel) is { } n ? a with { Parallel = n } : null,
        "--max-kb" => Number(value, MinKilobytes, MaxKilobytes) is { } n ? a with { MaxKb = n } : null,
        "--limit" => Number(value, 1, int.MaxValue) is { } n ? a with { Limit = n } : null,
        "--seconds" => ParseSeconds(value) is { } s ? a with { Seconds = s } : null,
        _ => null,
    };

    private static string? CountryCode(string value) =>
        value.Length == 2 && value.All(char.IsAsciiLetter) ? value.ToUpperInvariant() : null;

    private static int? Number(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : null;

    private static double? ParseSeconds(string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
        && s > 0 && s <= MaxSeconds ? s : null;
}
