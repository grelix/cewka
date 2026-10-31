using Cewka.App.Localisation;
using Cewka.Audio;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;

namespace Cewka.App.ViewModels;

/// <summary>
/// Kody przyczyn z warstwy dźwięku zamienione na teksty w języku interfejsu. Szczegóły
/// techniczne (tekst wyjątku, kod błędu biblioteki) idą wyłącznie do dziennika.
/// </summary>
internal static class FailureText
{
    /// <summary>Dlaczego plik nie zagrał; dla formatu nieobsługiwanego przyczyna wynika z formatu.</summary>
    public static string ForTrack(TrackFailureReason reason, AudioFileFormat format) => reason switch
    {
        TrackFailureReason.UnsupportedFormat => ForDecoder(DecoderFactory.ClassifyUnsupported(format)),
        _ => Strings.Current["TrackFailure" + reason],
    };

    public static string ForDecoder(DecoderFailure reason) =>
        Strings.Current[reason == DecoderFailure.CodecMissing && OperatingSystem.IsLinux()
            ? "DecoderFailureCodecMissingLinux"
            : "DecoderFailure" + reason];

    /// <summary>Klucz komunikatu o wyjściu dźwięku, dobrany po rodzaju wyjątku, nie po jego tekście.</summary>
    public static string AudioFailureKey(Exception ex) => ex switch
    {
        // Brak biblioteki wychodzi zwykle opakowany w błąd inicjalizacji klasy, która ją wołała.
        TypeInitializationException { InnerException: { } inner } => AudioFailureKey(inner),
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException => "AudioFailureLibrary",
        AudioException { Reason: DecoderFailure.Unspecified } => "AudioFailureDevice",
        _ => "AudioFailure",
    };
}
