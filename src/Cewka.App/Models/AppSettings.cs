using System.Text.Json;
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
/// Skaluje naraz wszystko, co bierze barwę z okładki: nasycenie barw, krycie tła, poświatę pod
/// płytą i akcent, żeby te miejsca nie rozjeżdżały się ze sobą.
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
    /// <summary>Błękit i fiolet.</summary>
    BlueViolet,

    Turquoise,
    Amber,
    Lime,
    Graphite,

    // Każda z kolejnych idzie w innym kierunku: ciepły zachód, zimna głębia, purpura, pastel,
    // ziemia i czerwień.
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

/// <summary>Skąd gra odtwarzacz: z plików w kolejce albo ze stacji radiowej.</summary>
public enum SourceMode
{
    Files,
    Radio,
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

/// <summary>Wizualizacja pełnego ekranu: co zajmuje scenę obok tytułu i sterowania.</summary>
public enum SceneVisual
{
    /// <summary>Obracająca się płyta z okładką. Wygląd dotychczasowy.</summary>
    Disc,

    /// <summary>Okładka bez płyty.</summary>
    Cover,

    /// <summary>Przyciemniona scena nocna.</summary>
    Night,

    /// <summary>Analizator widma: 48 słupków.</summary>
    Spectrum,

    /// <summary>Oscyloskop: przebieg ostatnich milisekund.</summary>
    Waveform,

    /// <summary>Obwiednia całego utworu z głowicą postępu. Niedostępna dla stacji radiowych.</summary>
    Horizon,

    /// <summary>
    /// Wolne wstęgi światła w barwach okładki (shader). Wymaga GPU i zasilania sieciowego; bez nich,
    /// przy ograniczonym ruchu, scena rysuje Płytę, a zapisany wybór zostaje.
    /// </summary>
    Aurora,

    /// <summary>
    /// Wycofane wskaźniki wysterowania. Nazwa zostaje tylko po to, żeby plik ustawień z tym wyborem dał się
    /// przeczytać; odczyt zamienia ją na <see cref="Disc"/>, a żadne miejsce programu jej nie oferuje.
    /// </summary>
    Meters,
}

/// <summary>Wielkość tekstu sceny: kolumny z tytułem, karty „Następny” i bloku Nocy.</summary>
public enum SceneTextSize
{
    /// <summary>Mnożnik 1,0.</summary>
    Normal,

    /// <summary>Mnożnik 1,15.</summary>
    Large,

    /// <summary>Mnożnik 1,3.</summary>
    ExtraLarge,
}

/// <summary>Położenie kolumny napisów w Płycie, Okładce i radiu.</summary>
public enum SceneTextPosition
{
    /// <summary>Kolumna od górnego marginesu.</summary>
    Top,

    /// <summary>Wspólny środek z płytą, jak dotąd.</summary>
    Middle,

    /// <summary>Kolumna przy dolnym marginesie; karta „Następny” przechodzi wtedy na górę.</summary>
    Bottom,
}

/// <summary>Marginesy kolumny napisów sceny.</summary>
public enum SceneTextInset
{
    /// <summary>Kolumna jak dotąd.</summary>
    Normal,

    /// <summary>Kolumna o 60 px węższa z każdej strony.</summary>
    Wide,
}

/// <summary>Wielkość płyty, okładki i logo stacji w scenie.</summary>
public enum SceneArtSize
{
    /// <summary>Mnożnik 0,85.</summary>
    Small,

    /// <summary>Mnożnik 1,0.</summary>
    Normal,

    /// <summary>Mnożnik 1,1 — większy nie mieści się obok kolumny napisów.</summary>
    Large,
}

/// <summary>Siła efektu tła sceny.</summary>
public enum SceneEffectStrength
{
    Subtle,
    Normal,
    Strong,
}

/// <summary>Tempo obrotu płyty, w oknie i w scenie.</summary>
public enum DiscSpin
{
    /// <summary>Jeden obrót na 9 sekund. Wygląd dotychczasowy.</summary>
    Normal,

    /// <summary>Jeden obrót na 15 sekund.</summary>
    Slow,

    /// <summary>Jeden obrót na 5 sekund.</summary>
    Fast,

    /// <summary>Płyta stoi okładką prosto.</summary>
    Still,
}

/// <summary>Tempo ruchu tła: plam barw, przesuwania okładki i drobinek.</summary>
public enum BackdropMotion
{
    Normal,

    /// <summary>Połowa tempa dotychczasowego.</summary>
    Slow,
}

/// <summary>Szybkość opadania słupków i szczytów widma.</summary>
public enum SpectrumResponse
{
    Normal,

    /// <summary>Opadanie prawie dwa razy wolniejsze.</summary>
    Smooth,

    /// <summary>Opadanie dwa razy szybsze.</summary>
    Fast,
}

/// <summary>Margines ekranu sceny, liczony z każdej strony.</summary>
public enum SceneMargin
{
    /// <summary>Kadr zajmuje cały ekran.</summary>
    None,

    /// <summary>2,5% szerokości i wysokości z każdej strony.</summary>
    Small,

    /// <summary>5% szerokości i wysokości z każdej strony.</summary>
    Large,
}

/// <summary>Kształt widma na scenie.</summary>
public enum SpectrumShape
{
    Bars,
    Curve,
}

/// <summary>Rodzaj obrazu fali na scenie.</summary>
public enum WaveformKind
{
    Line,
    Stereo,
}

/// <summary>Jak wysuwa się nieprzypięty panel kolejki i stacji.</summary>
public enum QueueReveal
{
    /// <summary>Po najechaniu kursorem na uchwyt przy prawej krawędzi okna.</summary>
    Hover,

    /// <summary>Dopiero po kliknięciu uchwytu, które przypina panel.</summary>
    Click,
}

/// <summary>Po jakim czasie wysunięty panel chowa się, gdy kursor z niego zjedzie.</summary>
public enum QueueHideDelay
{
    HalfSecond,
    OneSecond,
    TwoSeconds,
    FourSeconds,

    /// <summary>Panel nie chowa się sam: zamyka go Esc, klawisz L albo kliknięcie obok.</summary>
    Never,
}

/// <summary>Zwłoka między najechaniem na uchwyt a wysunięciem panelu.</summary>
public enum QueueRevealDelay
{
    /// <summary>150 ms.</summary>
    Short,

    /// <summary>300 ms.</summary>
    Normal,

    /// <summary>600 ms.</summary>
    Long,
}

/// <summary>Persisted window placement. Stored in logical units, not device pixels.</summary>
public sealed class WindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public WindowGeometry Clone() => new() { X = X, Y = Y, Width = Width, Height = Height, Maximized = Maximized };
}

/// <summary>
/// Everything the application remembers between runs, except the playback queue,
/// which lives in its own file because it changes far more often.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Wersja układu pliku. Zmienia się, gdy znaczenie zapisanej wartości się zmienia, a nie
    /// gdy dochodzi nowe pole: 1 — głośność jako położenie suwaka i osobne wyciszenie.
    /// </summary>
    public const int CurrentSchema = 1;

    /// <summary>
    /// Wersja, w której zapisano plik. Brak pola oznacza plik sprzed numerowania, czyli zero;
    /// <see cref="Services.SettingsStore"/> przenosi taki plik do bieżącej wersji przy odczycie.
    /// </summary>
    public int SchemaVersion { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Interface language: a two-letter code, or <c>auto</c> to follow the operating system.
    /// </summary>
    public string Language { get; set; } = "auto";

    public WindowControlsPosition WindowControls { get; set; } = WindowControlsPosition.Right;

    public WindowGeometry? Window { get; set; }

    /// <summary>Położenie okna ustawień; rozmiar i maksymalizacja nie są w nim używane.</summary>
    public WindowGeometry? SettingsWindow { get; set; }

    /// <summary>
    /// Położenie i rozmiar okna „Korektor i efekty”. Samo okno po uruchomieniu jest zamknięte; dawne pole
    /// panelOpen (pas korektora w oknie głównym) z starszego pliku jest przy odczycie pomijane.
    /// </summary>
    public WindowGeometry? SoundWindow { get; set; }

    /// <summary>
    /// Panel kolejki (w radiu: stacji) przypięty: stoi w kolumnie po prawej albo, w wąskim oknie, jako
    /// nakładka, i nie chowa się po zjechaniu kursorem. Nieprzypięty wysuwa się z uchwytu przy krawędzi.
    /// </summary>
    public bool QueuePinned { get; set; }

    /// <summary>
    /// Dawne pole „kolejka otwarta” sprzed wysuwanego panelu. Odczyt przenosi jego wartość do
    /// <see cref="QueuePinned"/> i zeruje je, więc zapis go już nie zawiera.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QueueOpen { get; set; }

    /// <summary>Jak wysuwa się nieprzypięty panel kolejki: po najechaniu na uchwyt albo po kliknięciu.</summary>
    public QueueReveal QueueReveal { get; set; } = QueueReveal.Hover;

    /// <summary>Czas, po którym wysunięty panel chowa się po zjechaniu z niego kursorem.</summary>
    public QueueHideDelay QueueHideDelay { get; set; } = QueueHideDelay.TwoSeconds;

    /// <summary>Zwłoka między najechaniem na uchwyt a wysunięciem panelu.</summary>
    public QueueRevealDelay QueueRevealDelay { get; set; } = QueueRevealDelay.Normal;

    /// <summary>
    /// Szerokość panelu kolejki ustawiona przeciąganiem krawędzi; <c>null</c> to szerokość zależna od
    /// szerokości okna. Zapisywana bez obcinania do bieżącego okna, obcinana dopiero przy wyświetlaniu.
    /// </summary>
    public double? QueueWidth { get; set; }

    /// <summary>
    /// Położenie suwaka głośności od zera do jedności, w skali <see cref="VolumeCurve"/>.
    /// Pliki zapisane przed <see cref="CurrentSchema"/> trzymały tu wzmocnienie liniowe.
    /// </summary>
    public double Volume { get; set; } = 0.95;

    /// <summary>Wyciszenie, niezależne od położenia suwaka: przywrócenie dźwięku wraca do niego.</summary>
    public bool IsMuted { get; set; }

    public bool EqualiserEnabled { get; set; } = true;

    /// <summary>
    /// Korektor domyślnie płaski: program ma grać wiernie, dopóki nikt nie poprosi inaczej.
    /// Płaski korektor jest przezroczysty, więc może pozostać włączony.
    /// </summary>
    public double Preamp { get; set; }

    /// <summary>Gain per band in decibels, ordered from 32 Hz to 16 kHz.</summary>
    public double[] EqualiserGains { get; set; } = new double[10];

    public bool LimiterEnabled { get; set; } = true;

    public bool NormalisationEnabled { get; set; } = true;

    public EffectsMode Effects { get; set; } = EffectsMode.Auto;

    // ---------- Efekty dźwiękowe ----------
    //
    // Każdy z pięciu ma przełącznik i siłę od zera do jedności, jak suwak. Domyślnie wyłączone;
    // siła jest zapamiętana, żeby ponowne włączenie wracało do poprzedniego ustawienia.

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

    /// <summary>Wizualizacja pełnego ekranu; klawisz V przełącza ją w scenie.</summary>
    public SceneVisual SceneVisual { get; set; } = SceneVisual.Disc;

    /// <summary>Kształt widma: słupki albo krzywa.</summary>
    public SpectrumShape SpectrumShape { get; set; } = SpectrumShape.Bars;

    /// <summary>Rodzaj fali: linia albo stereo (wektorskop).</summary>
    public WaveformKind WaveformKind { get; set; } = WaveformKind.Line;

    /// <summary>Pierścień widma wokół płyty.</summary>
    public bool DiscRing { get; set; }

    /// <summary>Czy pełny ekran pokazuje kartę następnego utworu z kolejki.</summary>
    public bool SceneShowNext { get; set; } = true;

    /// <summary>
    /// Ochrona ekranu OLED w scenie: powolny dryf obrazu, przygaszanie stałych napisów po 3 minutach
    /// bez obsługi i zmiana strony układu przy każdym utworze. W Nocy dryf działa zawsze.
    /// </summary>
    public bool SceneOledCare { get; set; } = true;

    /// <summary>Wielkość tekstu w scenie; działa od razu w otwartej scenie.</summary>
    public SceneTextSize SceneTextSize { get; set; } = SceneTextSize.Normal;

    /// <summary>Położenie kolumny napisów w Płycie, Okładce i radiu.</summary>
    public SceneTextPosition SceneTextPosition { get; set; } = SceneTextPosition.Middle;

    /// <summary>Napisy wyśrodkowane w kolumnie zamiast wyrównanych do lewej.</summary>
    public bool SceneTextCentred { get; set; }

    public SceneTextInset SceneTextInset { get; set; } = SceneTextInset.Normal;

    public SceneArtSize SceneArtSize { get; set; } = SceneArtSize.Normal;

    /// <summary>Wizualizacje pomijane przez klawisz V; Płyty nie da się pominąć.</summary>
    public List<SceneVisual> SceneCycleOff { get; set; } = [];

    /// <summary>Plamy barw okładki w tle sceny (dotychczasowy wygląd).</summary>
    public bool SceneBlobs { get; set; } = true;

    public SceneEffectStrength SceneBlobsStrength { get; set; } = SceneEffectStrength.Normal;

    /// <summary>Poświata w barwach okładki za płytą, okładką i logo.</summary>
    public bool SceneGlow { get; set; }

    public SceneEffectStrength SceneGlowStrength { get; set; } = SceneEffectStrength.Normal;

    /// <summary>Gradient i winieta w barwach okładki na rozmytym tle.</summary>
    public bool SceneTint { get; set; }

    public SceneEffectStrength SceneTintStrength { get; set; } = SceneEffectStrength.Normal;

    /// <summary>Powoli unoszące się drobinki światła w barwach okładki.</summary>
    public bool SceneParticles { get; set; }

    public SceneEffectStrength SceneParticlesStrength { get; set; } = SceneEffectStrength.Normal;

    /// <summary>Tempo obrotu płyty.</summary>
    public DiscSpin DiscSpin { get; set; } = DiscSpin.Normal;

    /// <summary>Tempo ruchu tła w oknie i w scenie.</summary>
    public BackdropMotion BackdropMotion { get; set; } = BackdropMotion.Normal;

    /// <summary>Opadanie słupków widma w scenie.</summary>
    public SpectrumResponse SpectrumResponse { get; set; } = SpectrumResponse.Normal;

    /// <summary>
    /// Margines sceny osobno dla każdego ekranu: klucz ekranu (<see cref="SceneScreens.KeyOf"/>) na
    /// wybór. Ekran bez wpisu ma 2,5%.
    /// </summary>
    public Dictionary<string, SceneMargin> SceneMargins { get; set; } = new();

    /// <summary>
    /// Klucz ekranu, na którym otwiera się scena (<see cref="SceneScreens.KeyOf"/>); <c>null</c> to ekran
    /// okna. Zapisany, a niepodłączony ekran zastępuje ekran okna.
    /// </summary>
    public string? SceneScreen { get; set; }

    /// <summary>Czy ekran nie wygasa, gdy scena jest otwarta i gra muzyka.</summary>
    public bool KeepScreenAwake { get; set; } = true;

    /// <summary>
    /// Nazwa urządzenia wyjściowego, dokładnie jak podaje ją system; <c>null</c> to urządzenie
    /// domyślne. Zapisywana nazwa, bo numery zmieniają się przy odłączaniu sprzętu.
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

    /// <summary>Pauza wycisza dźwięk płynnie, a wznowienie podgłaśnia; dotyczy plików.</summary>
    public bool SoftPause { get; set; }

    /// <summary>Whether the multimedia keys on the keyboard control playback.</summary>
    public bool MediaKeys { get; set; } = true;

    /// <summary>Whether opening a file hands it to the running copy instead of starting another.</summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>
    /// Czy program przy uruchomieniu pyta serwis GitHuba o najnowsze wydanie.
    ///
    /// <para>Domyślnie wyłączone, żeby odtwarzacz plików nie łączył się z siecią bez prośby.
    /// Sprawdzenie na żądanie działa niezależnie od tego ustawienia.</para>
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>
    /// Kiedy sprawdzano ostatni raz. Zapisywane wyłącznie po to, żeby sprawdzanie automatyczne
    /// nie odpytywało serwisu częściej niż raz na dobę.
    /// </summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Ostatnie źródło dźwięku; po uruchomieniu wraca bez samoczynnej gry.</summary>
    public SourceMode Source { get; set; } = SourceMode.Files;

    /// <summary>Kraj ostatnio wybrany na liście stacji (kod ISO); null, dopóki słuchacz nie wybierał.</summary>
    public string? RadioCountry { get; set; }

    /// <summary>Klucz kategorii ostatnio wybranej na liście stacji; null oznacza wszystkie.</summary>
    public string? RadioCategory { get; set; }

    /// <summary>Radio internetowe włączone; wyłączone ukrywa przełącznik źródła i skróty, i wyklucza połączenia radia.</summary>
    public bool RadioEnabled { get; set; } = true;

    /// <summary>Pobieranie logo stacji z serwerów nadawców; wyłączone zostawia wszędzie obraz zastępczy.</summary>
    public bool RadioLogos { get; set; } = true;

    /// <summary>Baner o połączeniach radia został zamknięty albo stacja została już uruchomiona.</summary>
    public bool RadioPrivacyAcknowledged { get; set; }

    /// <summary>Ile stacji pamięta lista ostatnich: 5, 10 albo 20.</summary>
    public int RadioRecentLimit { get; set; } = 10;

    /// <summary>Program uruchomiony w trybie radia od razu włącza ostatnią stację.</summary>
    public bool RadioPlayAtStart { get; set; }

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Theme = Theme,
        Language = Language,
        WindowControls = WindowControls,
        Window = Window?.Clone(),
        SettingsWindow = SettingsWindow?.Clone(),
        SoundWindow = SoundWindow?.Clone(),
        QueuePinned = QueuePinned,
        QueueOpen = QueueOpen,
        QueueReveal = QueueReveal,
        QueueHideDelay = QueueHideDelay,
        QueueRevealDelay = QueueRevealDelay,
        QueueWidth = QueueWidth,
        Volume = Volume,
        IsMuted = IsMuted,
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
        SceneVisual = SceneVisual,
        SpectrumShape = SpectrumShape,
        WaveformKind = WaveformKind,
        DiscRing = DiscRing,
        SceneShowNext = SceneShowNext,
        SceneOledCare = SceneOledCare,
        SceneTextSize = SceneTextSize,
        SceneTextPosition = SceneTextPosition,
        SceneTextCentred = SceneTextCentred,
        SceneTextInset = SceneTextInset,
        SceneArtSize = SceneArtSize,
        SceneCycleOff = [.. SceneCycleOff ?? []],
        SceneBlobs = SceneBlobs,
        SceneBlobsStrength = SceneBlobsStrength,
        SceneGlow = SceneGlow,
        SceneGlowStrength = SceneGlowStrength,
        SceneTint = SceneTint,
        SceneTintStrength = SceneTintStrength,
        SceneParticles = SceneParticles,
        SceneParticlesStrength = SceneParticlesStrength,
        DiscSpin = DiscSpin,
        BackdropMotion = BackdropMotion,
        SpectrumResponse = SpectrumResponse,
        SceneMargins = new Dictionary<string, SceneMargin>(SceneMargins ?? []),
        SceneScreen = SceneScreen,
        KeepScreenAwake = KeepScreenAwake,
        OutputDevice = OutputDevice,
        ResamplerQuality = ResamplerQuality,
        OutputLatency = OutputLatency,
        LoudnessTarget = LoudnessTarget,
        AlwaysAnalyse = AlwaysAnalyse,
        RestoreSession = RestoreSession,
        SeekStep = SeekStep,
        SoftPause = SoftPause,
        MediaKeys = MediaKeys,
        SingleInstance = SingleInstance,
        CheckForUpdates = CheckForUpdates,
        LastUpdateCheck = LastUpdateCheck,
        Source = Source,
        RadioCountry = RadioCountry,
        RadioCategory = RadioCategory,
        RadioEnabled = RadioEnabled,
        RadioLogos = RadioLogos,
        RadioPrivacyAcknowledged = RadioPrivacyAcknowledged,
        RadioRecentLimit = RadioRecentLimit,
        RadioPlayAtStart = RadioPlayAtStart,
    };
}

// Source-generated serialisation keeps the single-file build trim-safe.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
