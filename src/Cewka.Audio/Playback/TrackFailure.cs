namespace Cewka.Audio.Playback;

/// <summary>
/// Przyczyna, dla której utworu nie dało się odtworzyć. Warstwa dźwięku podaje kod, a tekst
/// dla użytkownika dobiera interfejs w swoim języku.
/// </summary>
public enum TrackFailureReason
{
    /// <summary>Format pliku nie jest obsługiwany na tym komputerze.</summary>
    UnsupportedFormat,

    /// <summary>Pliku nie ma pod zapisaną ścieżką.</summary>
    FileMissing,

    /// <summary>Brak uprawnień do odczytu pliku.</summary>
    AccessDenied,

    /// <summary>Plik chwilowo niedostępny: błąd odczytu, zajęty przez inny program, odłączony dysk.</summary>
    FileUnavailable,

    /// <summary>Dekoder nie przyjął pliku: uszkodzony albo niezgodny z rozszerzeniem.</summary>
    DamagedFile,

    /// <summary>Odczyt przerwał się w trakcie odtwarzania.</summary>
    PlaybackInterrupted,
}

/// <summary>
/// Nieudane otwarcie albo odtworzenie utworu.
/// </summary>
/// <param name="Reason">Kod przyczyny, z którego interfejs buduje komunikat.</param>
/// <param name="Detail">
/// Szczegół do dziennika; dla formatu nieobsługiwanego opis z wykrywania formatu.
/// </param>
public readonly record struct TrackFailure(TrackFailureReason Reason, string? Detail);
