namespace Cewka.Audio.Decoding;

/// <summary>
/// Przyczyna, dla której pliku nie udało się otworzyć ani zdekodować. Kod służy do lokalizacji
/// komunikatu; do tego czasu na ekranie widać opis zastępczy z <see cref="DecoderFailures.Describe"/>.
/// Szczegóły techniczne (tekst wyjątku biblioteki, HRESULT, GError) trafiają wyłącznie do dziennika.
/// </summary>
public enum DecoderFailure
{
    /// <summary>Błąd spoza dekoderów albo bez przypisanej przyczyny.</summary>
    Unspecified,

    /// <summary>Zawartość pliku nie odpowiada żadnemu obsługiwanemu formatowi.</summary>
    UnrecognisedFormat,

    /// <summary>Format rozpoznany, ale poza zakresem odtwarzacza (WMA).</summary>
    OutOfScopeFormat,

    /// <summary>Brak kodeków systemowych (Media Foundation, GStreamer) dla AAC, M4A i ALAC.</summary>
    SystemCodecsUnavailable,

    /// <summary>Kodeki systemowe są, ale brakuje dekodera tego konkretnego kodeka.</summary>
    CodecMissing,

    /// <summary>Plik jest uszkodzony lub nieczytelny.</summary>
    DamagedFile,

    /// <summary>Kontener nie zawiera ścieżki dźwiękowej.</summary>
    NoAudioStream,

    /// <summary>Układ kanałów, którego dekoder nie obsługuje.</summary>
    UnsupportedChannelLayout,
}

/// <summary>Stałe opisy przyczyn i zapis szczegółów do dziennika.</summary>
public static class DecoderFailures
{
    /// <summary>
    /// Opis zastępczy, który interfejs pokazuje, dopóki komunikaty nie pochodzą ze słownika.
    /// Nie zawiera szczegółów technicznych.
    /// </summary>
    public static string Describe(DecoderFailure reason) => reason switch
    {
        DecoderFailure.UnrecognisedFormat => "Nierozpoznany format pliku.",
        DecoderFailure.OutOfScopeFormat => "Format WMA jest poza zakresem odtwarzacza.",
        DecoderFailure.SystemCodecsUnavailable =>
            SystemCodecs.UnavailableReason ?? "Brak kodeków systemowych dla formatów AAC, M4A i ALAC.",
        DecoderFailure.CodecMissing => OperatingSystem.IsLinux()
            ? "W GStreamerze brakuje wtyczki dekodującej ten plik. Dla AAC, M4A i ALAC dostarcza ją pakiet gstreamer1.0-libav."
            : "W systemie brakuje dekodera dla tego pliku.",
        DecoderFailure.DamagedFile => "Plik jest uszkodzony lub nieczytelny.",
        DecoderFailure.NoAudioStream => "Plik nie zawiera ścieżki dźwiękowej.",
        DecoderFailure.UnsupportedChannelLayout => "Układ kanałów tego pliku nie jest obsługiwany.",
        _ => "Formatu nie udało się odtworzyć.",
    };

    internal static AudioException Create(DecoderFailure reason, Exception? inner = null) =>
        new(reason, Describe(reason), inner);

    /// <summary>Szczegóły techniczne idą na standardowe wyjście błędów, nigdy na ekran.</summary>
    internal static void Log(string detail) => Console.Error.WriteLine($"[cewka] {detail}");
}
