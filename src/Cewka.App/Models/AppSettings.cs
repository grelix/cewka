using System.Text.Json.Serialization;

namespace Cewka.App.Models;

/// <summary>How the interface picks between the light and dark palettes.</summary>
public enum ThemeMode
{
    /// <summary>Follow the operating system setting and react to changes at runtime.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How aggressively the decorative effects may be scaled back.</summary>
public enum EffectsMode
{
    /// <summary>Full effects on mains power, reduced automatically while running on battery.</summary>
    Auto,

    /// <summary>Always full quality, regardless of cost.</summary>
    Full,

    /// <summary>Always reduced: no rotation, no cursor glow, static background.</summary>
    Reduced,
}

/// <summary>
/// Jakość konwersji częstotliwości próbkowania — rząd filtru dolnoprzepustowego resamplera.
/// Ma znaczenie tylko dla plików, których częstotliwość różni się od częstotliwości urządzenia.
/// </summary>
public enum ResamplerQuality
{
    /// <summary>Bez filtrowania. Najtańsze, kosztem aliasingu.</summary>
    Off,

    /// <summary>Filtr czwartego rzędu — wartość domyślna miniaudio.</summary>
    Standard,

    /// <summary>Filtr ósmego rzędu, maksimum obsługiwane przez miniaudio.</summary>
    High,
}

/// <summary>
/// Rozmiar bufora urządzenia wyjściowego, czyli opóźnienie między decyzją a dźwiękiem.
/// Mniejszy bufor znaczy szybszą reakcję i większe ryzyko przerywania.
/// </summary>
public enum OutputLatency
{
    /// <summary>Około 256 ramek. Najkrótsze opóźnienie, najmniejszy zapas na obciążenie systemu.</summary>
    Low,

    /// <summary>Rozmiar dobrany przez miniaudio pod dany sterownik.</summary>
    Balanced,

    /// <summary>Około 2048 ramek. Odporne na obciążenie kosztem opóźnienia.</summary>
    Safe,
}

/// <summary>Poziom, do którego wyrównywana jest głośność utworów.</summary>
public enum LoudnessTarget
{
    /// <summary>−23 LUFS, norma nadawcza EBU R128.</summary>
    Broadcast,

    /// <summary>−18 LUFS, poziom odniesienia ReplayGain 2.0.</summary>
    Reference,

    /// <summary>−14 LUFS, poziom przyjęty przez serwisy strumieniowe.</summary>
    Streaming,
}

/// <summary>
/// Jak mocno barwa okładki przenosi się na wygląd okna.
/// <para>
/// Skaluje wszystko, co bierze barwę z okładki naraz — nasycenie wyciągniętych barw, krycie
/// tła, poświatę pod płytą i barwę akcentu. Osobne pokrętła dla każdego z tych miejsc dałyby
/// zestawy, w których tło jest intensywne, a akcent blady, czyli wynik wyglądający na
/// niedokończony.
/// </para>
/// </summary>
public enum ColourIntensity
{
    /// <summary>Barwa okładki ledwie zaznaczona; tło bliżej neutralnego.</summary>
    Subtle,

    /// <summary>Wartości, z jakimi program był projektowany.</summary>
    Recommended,

    /// <summary>Barwa okładki wyraźnie mocniejsza, tło bardziej nasycone.</summary>
    Intense,
}

/// <summary>
/// Para barw domyślnej okładki — spirali rysowanej dla plików bez własnej okładki.
/// Spirala przechodzi od pierwszej barwy do drugiej wzdłuż zwoju.
/// </summary>
public enum PlaceholderPalette
{
    /// <summary>Błękit i fiolet. Najbliższa wyglądowi z poprzednich wydań.</summary>
    BlueViolet,

    Turquoise,
    Amber,
    Lime,
    Graphite,

    // Sześć par dołożonych w 0.8.0. Wybrane tak, żeby każda szła w innym kierunku niż
    // poprzednie: ciepły zachód, zimna głębia, purpura, pastel, ziemia i czerwień.
    Sunset,
    Ocean,
    Plum,
    Mint,
    Sand,
    Cherry,

    /// <summary>
    /// Para dobierana losowo przy każdym wczytaniu utworu. Ten sam plik odtworzony ponownie
    /// dostanie inną parę — także przy przełączeniu motywu, bo okładka jest wtedy rysowana od nowa.
    /// </summary>
    Random,
}

/// <summary>
/// Co program robi z plikiem otwartym poza nim — z menedżera plików, z wiersza polecenia
/// albo upuszczonym na okno.
/// </summary>
public enum FileOpenAction
{
    /// <summary>Dołącza na koniec kolejki i nie przerywa tego, co gra.</summary>
    Append,

    /// <summary>Dołącza na koniec kolejki i od razu przechodzi do tego pliku.</summary>
    AppendAndPlay,

    /// <summary>Czyści kolejkę i odtwarza wyłącznie ten plik.</summary>
    ReplaceAndPlay,
}

/// <summary>Where the minimise, maximise and close buttons sit.</summary>
public enum WindowControlsPosition
{
    /// <summary>Right-hand side, as on Windows. The default.</summary>
    Right,

    /// <summary>Left-hand side, keeping the same square glyphs.</summary>
    Left,

    /// <summary>
    /// Left-hand side as three coloured circles, in the manner of macOS. Useful on a Linux
    /// desktop themed after macOS, or simply for anyone who prefers it.
    /// </summary>
    MacOs,
}

/// <summary>Persisted window placement. Stored in logical units, not device pixels.</summary>
public sealed class WindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Everything the application remembers between runs, except the playback queue,
/// which lives in its own file because it changes far more often.
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Interface language: a two-letter code, or <c>auto</c> to follow the operating system.
    /// </summary>
    public string Language { get; set; } = "auto";

    public WindowControlsPosition WindowControls { get; set; } = WindowControlsPosition.Right;

    public WindowGeometry? Window { get; set; }

    /// <summary>
    /// Whether the bottom strip — the equaliser and the effects — is expanded.
    ///
    /// <para>Od 0.8.0 dotyczy wyłącznie pasa dolnego. Kolejka ma własny stan
    /// (<see cref="QueueOpen"/>), bo stoi we własnej kolumnie i jedno z drugim nie musi się
    /// pojawiać razem. Plik ustawień sprzed tej wersji wnosi tu swoją wartość, a kolejka
    /// przyjmuje domyślną — czyli widoczną, tak jak było.</para>
    /// </summary>
    public bool PanelOpen { get; set; } = true;

    /// <summary>Whether the queue column on the right is shown.</summary>
    public bool QueueOpen { get; set; } = true;

    public double Volume { get; set; } = 0.72;

    public bool EqualiserEnabled { get; set; } = true;

    public double Preamp { get; set; } = 1.5;

    /// <summary>Gain per band in decibels, ordered from 32 Hz to 16 kHz.</summary>
    public double[] EqualiserGains { get; set; } = [2.5, 2, 0.5, 1, -1.5, -1, 0.5, 1.5, 2.5, 3];

    public bool LimiterEnabled { get; set; } = true;

    public bool NormalisationEnabled { get; set; } = true;

    public EffectsMode Effects { get; set; } = EffectsMode.Auto;

    // ---------- Efekty dźwiękowe ----------
    //
    // Każdy z pięciu ma przełącznik i siłę zapisaną w zakresie od zera do jedności — tak samo,
    // jak przekazuje ją suwak. Wszystkie są domyślnie wyłączone: program ma grać wiernie,
    // dopóki nikt nie poprosi inaczej. Wartości siły są zapamiętane mimo to, żeby ponowne
    // włączenie efektu wracało tam, gdzie użytkownik go zostawił.

    /// <summary>Domieszka kanału przeciwnego dla odsłuchu w słuchawkach.</summary>
    public bool CrossfeedEnabled { get; set; }

    public double CrossfeedStrength { get; set; } = 0.5;

    /// <summary>Podbicie basu i góry przy cichym słuchaniu.</summary>
    public bool LoudnessEnabled { get; set; }

    /// <summary>Domyślnie pełna: korekta i tak wynika z tego, jak cicho gra muzyka.</summary>
    public double LoudnessStrength { get; set; } = 1.0;

    /// <summary>Harmoniczne najniższych tonów dla małych przetworników.</summary>
    public bool VirtualBassEnabled { get; set; }

    public double VirtualBassStrength { get; set; } = 0.5;

    /// <summary>Zawężenie rozpiętości dynamicznej.</summary>
    public bool DynamicRangeEnabled { get; set; }

    public double DynamicRangeStrength { get; set; } = 0.5;

    /// <summary>Poszerzenie bazy stereo dla odsłuchu na głośnikach.</summary>
    public bool StereoWidthEnabled { get; set; }

    public double StereoWidthStrength { get; set; } = 0.5;

    public ColourIntensity ColourIntensity { get; set; } = ColourIntensity.Recommended;

    public PlaceholderPalette PlaceholderPalette { get; set; } = PlaceholderPalette.BlueViolet;

    /// <summary>Whether the codec, bit depth, bitrate and sample rate are shown beside the record.</summary>
    public bool ShowFormatBadge { get; set; } = true;

    public FileOpenAction FileOpenAction { get; set; } = FileOpenAction.Append;

    /// <summary>
    /// Name of the output device, exactly as the system reports it; <c>null</c> follows the
    /// system default. Stored by name rather than by index because indices are only meaningful
    /// while the same devices are plugged in.
    /// </summary>
    public string? OutputDevice { get; set; }

    public ResamplerQuality ResamplerQuality { get; set; } = ResamplerQuality.Standard;

    public OutputLatency OutputLatency { get; set; } = OutputLatency.Balanced;

    public LoudnessTarget LoudnessTarget { get; set; } = LoudnessTarget.Reference;

    /// <summary>Whether ReplayGain tags are ignored in favour of the player's own measurement.</summary>
    public bool AlwaysAnalyse { get; set; }

    /// <summary>Whether the queue and playback position from the previous run come back.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Seconds the arrow keys move playback by.</summary>
    public int SeekStep { get; set; } = 5;

    /// <summary>Whether the multimedia keys on the keyboard control playback.</summary>
    public bool MediaKeys { get; set; } = true;

    /// <summary>Whether opening a file hands it to the running copy instead of starting another.</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>
    /// Czy program przy uruchomieniu pyta serwis GitHuba o najnowsze wydanie.
    ///
    /// <para>Domyślnie wyłączone i to jest rozstrzygnięcie celowe: odtwarzacz plików z dysku nie
    /// powinien łączyć się z niczym, dopóki nikt go o to nie poprosi. Przycisk sprawdzenia
    /// na żądanie działa niezależnie od tego ustawienia.</para>
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>
    /// Kiedy sprawdzano ostatni raz. Zapisywane wyłącznie po to, żeby sprawdzanie automatyczne
    /// nie odpytywało serwisu częściej niż raz na dobę.
    /// </summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        Language = Language,
        WindowControls = WindowControls,
        Window = Window is null ? null : new WindowGeometry
        {
            X = Window.X, Y = Window.Y, Width = Window.Width, Height = Window.Height, Maximized = Window.Maximized,
        },
        PanelOpen = PanelOpen,
        QueueOpen = QueueOpen,
        Volume = Volume,
        EqualiserEnabled = EqualiserEnabled,
        Preamp = Preamp,
        EqualiserGains = (double[])EqualiserGains.Clone(),
        LimiterEnabled = LimiterEnabled,
        NormalisationEnabled = NormalisationEnabled,
        Effects = Effects,
        CrossfeedEnabled = CrossfeedEnabled,
        CrossfeedStrength = CrossfeedStrength,
        LoudnessEnabled = LoudnessEnabled,
        LoudnessStrength = LoudnessStrength,
        VirtualBassEnabled = VirtualBassEnabled,
        VirtualBassStrength = VirtualBassStrength,
        DynamicRangeEnabled = DynamicRangeEnabled,
        DynamicRangeStrength = DynamicRangeStrength,
        StereoWidthEnabled = StereoWidthEnabled,
        StereoWidthStrength = StereoWidthStrength,
        ColourIntensity = ColourIntensity,
        PlaceholderPalette = PlaceholderPalette,
        ShowFormatBadge = ShowFormatBadge,
        FileOpenAction = FileOpenAction,
        OutputDevice = OutputDevice,
        ResamplerQuality = ResamplerQuality,
        OutputLatency = OutputLatency,
        LoudnessTarget = LoudnessTarget,
        AlwaysAnalyse = AlwaysAnalyse,
        RestoreSession = RestoreSession,
        SeekStep = SeekStep,
        MediaKeys = MediaKeys,
        SingleInstance = SingleInstance,
        CheckForUpdates = CheckForUpdates,
        LastUpdateCheck = LastUpdateCheck,
    };
}

// Source-generated serialisation keeps the single-file build trim-safe.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
