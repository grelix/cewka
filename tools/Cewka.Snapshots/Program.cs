using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Devices;
using Cewka.Platform;

// „Cewka.App” to zarazem przestrzeń nazw i klasa aplikacji w niej, stąd alias.
using CewkaApplication = Cewka.App.App;

namespace Cewka.Snapshots;

/// <summary>
/// Renderuje okna poza ekranem prawdziwym zapleczem Skia i zapisuje PNG, bez otwierania okna.
/// Wynik jest powtarzalny, więc zrzuty da się porównywać.
///
/// Użycie:  dotnet run --project tools/Cewka.Snapshots -- [katalog-wyjściowy] [plik-audio]
///
/// Plik audio trafia do okna tak, jak przy otwarciu z menedżera plików: z tytułem, wykonawcą,
/// okładką i czasem trwania. Bez niego zrzuty pokazują pustą kolejkę.
/// </summary>
internal static class Program
{
    private sealed record Shot(string Name, int Width, int Height, ThemeVariant Variant, bool PanelOpen,
        WindowControlsPosition Controls = WindowControlsPosition.Right, string Language = "pl",
        string Section = "Appearance",
        ColourIntensity Intensity = ColourIntensity.Recommended,
        bool QueueOpen = true,
        PlaceholderPalette Palette = PlaceholderPalette.BlueViolet,
        bool QueueOverlay = false,
        string? Focus = null,
        bool SceneChrome = false,
        string? Search = null);

    private static readonly Shot[] Shots =
    [
        new("ciemny-1180", 1200, 680, ThemeVariant.Dark, true),
        new("jasny-1180", 1200, 680, ThemeVariant.Light, true),
        new("ciemny-zwiniety", 1180, 520, ThemeVariant.Dark, false, QueueOpen: false),
        new("jasny-zwiniety", 1180, 520, ThemeVariant.Light, false, QueueOpen: false),

        // Kolumna kolejki tuż nad progiem nakładki: najciaśniej dla bloku odtwarzania obok kolumny.
        new("waskie-z-kolejka", 1040, 680, ThemeVariant.Dark, true),
        new("waskie-z-kolejka-zapas", 1260, 680, ThemeVariant.Dark, true),
        new("waskie-z-kolejka-bez-pasa", 1040, 520, ThemeVariant.Dark, false),

        // Kolejka jako panel nakładany nad treść w oknie węższym niż próg kolumny.
        new("nakladka-ciemny", 900, 620, ThemeVariant.Dark, true, QueueOpen: false, QueueOverlay: true),
        new("nakladka-jasny", 900, 620, ThemeVariant.Light, true, QueueOpen: false, QueueOverlay: true),
        new("nakladka-minimum", 760, 520, ThemeVariant.Dark, false, QueueOpen: false, QueueOverlay: true),

        // Niskie okna z pasem: pas się przewija, a blok odtwarzania dostaje mniejszą płytę albo żadnej.
        new("niskie-z-pasem", 1200, 560, ThemeVariant.Dark, true),
        new("niskie-z-pasem-jasny", 1200, 560, ThemeVariant.Light, true),
        new("niskie-waskie-z-pasem", 900, 520, ThemeVariant.Dark, true, QueueOpen: false),

        // Szerokie okno: blok wyśrodkowany z granicą szerokości, pasek postępu niezależny od tytułu.
        new("szeroki-1920", 1920, 1040, ThemeVariant.Dark, true),

        // Cztery zestawienia pasa dolnego i kolejki, które włącza się osobno.
        new("obszary-oba", 1200, 680, ThemeVariant.Dark, true),
        new("obszary-tylko-pas", 1180, 680, ThemeVariant.Dark, true, QueueOpen: false),
        new("obszary-tylko-kolejka", 1180, 680, ThemeVariant.Dark, false),
        new("obszary-zadne", 1180, 520, ThemeVariant.Dark, false, QueueOpen: false),
        new("obszary-tylko-kolejka-jasny", 1180, 680, ThemeVariant.Light, false),

        // Pary o skrajnych barwach najdalszych od siebie; tło ma pokazać oba końce pary.
        new("para-turkus", 1180, 680, ThemeVariant.Dark, true, Palette: PlaceholderPalette.Turquoise),
        new("para-wisnia", 1180, 680, ThemeVariant.Dark, true, Palette: PlaceholderPalette.Cherry),
        new("para-mieta-jasny", 1180, 680, ThemeVariant.Light, true, Palette: PlaceholderPalette.Mint),
        // Wąskie okno poniżej progu kolumny: kolejka schowana, blok odtwarzania na całą szerokość.
        new("ciemny-waski", 920, 660, ThemeVariant.Dark, true, QueueOpen: false),
        new("ciemny-szeroki", 1600, 820, ThemeVariant.Dark, true),
        new("ciemny-pelnyekran", 2560, 1400, ThemeVariant.Dark, true),
        new("przyciski-lewo", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.Left),
        new("przyciski-macos", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.MacOs),

        // Najciaśniejsze przypadki: okno w rozmiarze minimalnym, z pasem i bez niego. Rozmiar
        // minimalny jest stały, więc tu wychodzi każde nachodzenie elementów na siebie.
        new("minimum-panel", 760, 520, ThemeVariant.Dark, true, QueueOpen: false),
        new("minimum-panel-jasny", 760, 520, ThemeVariant.Light, true, QueueOpen: false),
        new("minimum-zwiniety", 760, 520, ThemeVariant.Dark, false, QueueOpen: false),

        // Najdłuższe etykiety przełączników w nagłówku korektora (Auto-Limiter, Normalisierung) przy oknie minimalnym.
        new("minimum-panel-niemiecki", 760, 520, ThemeVariant.Dark, true, Language: "de", QueueOpen: false),

        // Interfejs po angielsku: zmiana języka ma objąć wszystkie napisy.
        new("angielski", 1180, 680, ThemeVariant.Dark, true, Language: "en"),

        // Skrajne intensywności barw; wariant zalecany to zrzut „ciemny-1180”.
        new("barwy-subtelne", 1180, 680, ThemeVariant.Dark, true, Intensity: ColourIntensity.Subtle),
        new("barwy-intensywne", 1180, 680, ThemeVariant.Dark, true, Intensity: ColourIntensity.Intense),
        new("barwy-subtelne-jasny", 1180, 680, ThemeVariant.Light, true, Intensity: ColourIntensity.Subtle),
        new("barwy-intensywne-jasny", 1180, 680, ThemeVariant.Light, true, Intensity: ColourIntensity.Intense),

        // Fokus z klawiatury: pierścień wokół przycisku, fadera i wiersza kolejki w obu motywach.
        new("fokus-odtwarzanie-ciemny", 1200, 680, ThemeVariant.Dark, true, Focus: "PlayButton"),
        new("fokus-odtwarzanie-jasny", 1200, 680, ThemeVariant.Light, true, Focus: "PlayButton"),
        new("fokus-kolejka-ciemny", 1200, 680, ThemeVariant.Dark, true, Focus: "QueueRow"),
        new("fokus-przelacznik-jasny", 1200, 680, ThemeVariant.Light, true, Focus: "PanelToggle"),
    ];

    /// <summary>
    /// Plansza sceny w rozmiarze telewizora: spoczynek (bez rzędu przycisków, uchwytu paska
    /// i wyjścia), aktywność, jasny motyw okna, pod którym scena zostaje ciemna, i ekran 21:9
    /// z kadrem 1920 × 1080 w środku. Płyta stoi prosto, plamy w ustalonym punkcie, bez przenikań.
    /// </summary>
    private static readonly Shot[] SceneShots =
    [
        new("scena-ciemna", 1920, 1080, ThemeVariant.Dark, false, QueueOpen: false),
        new("scena-sterowanie", 1920, 1080, ThemeVariant.Dark, false, QueueOpen: false, SceneChrome: true),
        new("scena-jasny-motyw", 1920, 1080, ThemeVariant.Light, false, QueueOpen: false, SceneChrome: true),
        new("scena-ultraszeroka", 2560, 1080, ThemeVariant.Dark, false, QueueOpen: false, SceneChrome: true),
    ];

    /// <summary>
    /// Samo tło z paletą o wyraźnie różnych barwach, żeby było widać kształt i położenie plam.
    /// </summary>
    private static readonly Shot[] BackdropShots =
    [
        new("tlo-ciemne", 1200, 680, ThemeVariant.Dark, true),
        new("tlo-jasne", 1200, 680, ThemeVariant.Light, true),
    ];

    private static readonly Color[] DarkPalette =
    [
        Color.FromRgb(0x3E, 0x5B, 0x8C), Color.FromRgb(0x7A, 0x3B, 0x52),
        Color.FromRgb(0x2F, 0x6B, 0x5E), Color.FromRgb(0x5A, 0x4A, 0x78),
    ];

    private static readonly Color[] LightPalette =
    [
        Color.FromRgb(0x8F, 0xB2, 0xE0), Color.FromRgb(0xE0, 0xA6, 0xB6),
        Color.FromRgb(0x9A, 0xD0, 0xC0), Color.FromRgb(0xBC, 0xAE, 0xDE),
    ];

    /// <summary>
    /// Okno ustawień, po jednym zrzucie na zakładkę.
    /// </summary>
    private static readonly Shot[] SettingsShots =
    [
        new("ustawienia-wyglad", 820, 640, ThemeVariant.Dark, true),
        new("ustawienia-dzwiek", 820, 640, ThemeVariant.Dark, true, Section: "Audio"),
        new("ustawienia-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Section: "Playback"),
        new("ustawienia-system", 820, 640, ThemeVariant.Dark, true, Section: "Integration"),
        // Zakładka informacji w całości, w wyższym oknie.
        new("ustawienia-informacje", 820, 640, ThemeVariant.Dark, true, Section: "About"),
        new("ustawienia-informacje-cala", 820, 820, ThemeVariant.Dark, true, Section: "About"),
        new("ustawienia-informacje-jasny", 820, 820, ThemeVariant.Light, true, Section: "About"),
        new("ustawienia-jasny", 820, 640, ThemeVariant.Light, true),

        // Okno w rozmiarze najmniejszym dopuszczalnym, na zakladce o najbogatszej zawartosci.
        new("ustawienia-minimum", 700, 520, ThemeVariant.Dark, true, Section: "Audio"),

        // Zakładka wyglądu jest najwyższa: wysoki zrzut pokazuje ją w całości, a 820x640 to,
        // co widać bez przewijania.
        new("ustawienia-wyglad-cala", 820, 1000, ThemeVariant.Dark, true),
        new("ustawienia-wyglad-cala-jasny", 820, 1000, ThemeVariant.Light, true),

        // Każdy język osobno, na zakładce dźwięku, gdzie etykiety pasków segmentowych są
        // najdłuższe. Język niezarejestrowany w klasie Strings dałby tu zrzut po polsku.
        new("ustawienia-niemiecki", 820, 640, ThemeVariant.Dark, true, Language: "de"),
        new("ustawienia-niemiecki-dzwiek", 820, 640, ThemeVariant.Dark, true, Language: "de", Section: "Audio"),
        new("ustawienia-hiszpanski", 820, 640, ThemeVariant.Dark, true, Language: "es", Section: "Audio"),
        new("ustawienia-francuski", 820, 640, ThemeVariant.Dark, true, Language: "fr", Section: "Audio"),
        new("ustawienia-wloski", 820, 640, ThemeVariant.Dark, true, Language: "it", Section: "Audio"),
        new("ustawienia-portugalski", 820, 640, ThemeVariant.Dark, true, Language: "pt", Section: "Audio"),
        new("ustawienia-rosyjski", 820, 640, ThemeVariant.Dark, true, Language: "ru", Section: "Audio"),
        new("ustawienia-ukrainski", 820, 640, ThemeVariant.Dark, true, Language: "uk", Section: "Audio"),
        new("ustawienia-czeski", 820, 640, ThemeVariant.Dark, true, Language: "cs", Section: "Audio"),
        new("ustawienia-indonezyjski", 820, 640, ThemeVariant.Dark, true, Language: "id", Section: "Audio"),
        new("ustawienia-turecki", 820, 640, ThemeVariant.Dark, true, Language: "tr", Section: "Audio"),
        new("ustawienia-wietnamski", 820, 640, ThemeVariant.Dark, true, Language: "vi", Section: "Audio"),

        // Wietnamski także w zakładce wyglądu, bo zapis sylabami najbardziej wydłuża nazwy par barw.
        new("ustawienia-wietnamski-wyglad", 820, 1000, ThemeVariant.Dark, true, Language: "vi"),

        // Zakładka dźwięku w całości: na jej końcu odsyłacz do korektora w oknie głównym.
        new("ustawienia-dzwiek-cala", 820, 1000, ThemeVariant.Dark, true, Section: "Audio"),
        new("ustawienia-dzwiek-jasny", 820, 1000, ThemeVariant.Light, true, Section: "Audio"),

        // Zakładka języka: lista języków i nota o tłumaczeniu maszynowym.
        new("ustawienia-jezyk", 820, 640, ThemeVariant.Dark, true, Section: "Language"),
        new("ustawienia-jezyk-jasny", 820, 640, ThemeVariant.Light, true, Section: "Language"),
        new("ustawienia-jezyk-waski", 700, 640, ThemeVariant.Dark, true, Section: "Language"),
        new("ustawienia-jezyk-rosyjski", 820, 640, ThemeVariant.Dark, true, Language: "ru", Section: "Language"),
        new("ustawienia-jezyk-wietnamski", 820, 640, ThemeVariant.Dark, true, Language: "vi", Section: "Language"),
        new("ustawienia-chinski", 832, 640, ThemeVariant.Dark, true, Language: "zh-hans", Section: "Audio"),
        new("ustawienia-japonski", 832, 640, ThemeVariant.Dark, true, Language: "ja", Section: "Audio"),
        new("ustawienia-brazylijski", 832, 640, ThemeVariant.Dark, true, Language: "pt-br", Section: "Audio"),
        new("ustawienia-japonski-wyglad", 832, 1000, ThemeVariant.Dark, true, Language: "ja"),
        new("ustawienia-jezyk-grecki", 820, 640, ThemeVariant.Dark, true, Language: "el", Section: "Language"),

        // Pisma inne niż łacińskie: grecki i cyrylica.
        new("ustawienia-grecki", 820, 640, ThemeVariant.Dark, true, Language: "el", Section: "Audio"),
        new("ustawienia-grecki-wyglad", 820, 1000, ThemeVariant.Dark, true, Language: "el"),
        new("ustawienia-niderlandzki", 820, 640, ThemeVariant.Dark, true, Language: "nl", Section: "Audio"),
        new("ustawienia-rumunski", 820, 640, ThemeVariant.Dark, true, Language: "ro", Section: "Audio"),
        new("ustawienia-wegierski", 820, 640, ThemeVariant.Dark, true, Language: "hu", Section: "Audio"),
        new("ustawienia-wegierski-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Language: "hu", Section: "Playback"),

        // Cyrylica także w zakładce wyglądu, przy nazwach par barw.
        new("ustawienia-rosyjski-wyglad", 820, 940, ThemeVariant.Dark, true, Language: "ru"),
        new("ustawienia-ukrainski-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Language: "uk", Section: "Playback"),

        // Podkategorie dodane z podziałem ustawień na kategorie główne.
        new("ustawienia-korektor", 820, 1000, ThemeVariant.Dark, true, Section: "Processing"),
        new("ustawienia-korektor-jasny", 820, 1000, ThemeVariant.Light, true, Section: "Processing"),
        new("ustawienia-pelny-ekran", 820, 1000, ThemeVariant.Dark, true, Section: "SceneFullScreen"),
        new("ustawienia-scena", 820, 1400, ThemeVariant.Dark, true, Section: "SceneVisuals"),
        new("ustawienia-scena-jasny", 820, 1400, ThemeVariant.Light, true, Section: "SceneVisuals"),
        new("ustawienia-telewizor", 820, 640, ThemeVariant.Dark, true, Section: "SceneTelevision"),
        new("ustawienia-radio", 820, 640, ThemeVariant.Dark, true, Section: "RadioSection"),
        new("ustawienia-logo", 820, 640, ThemeVariant.Dark, true, Section: "RadioLogos"),
        new("ustawienia-licencje", 820, 640, ThemeVariant.Dark, true, Section: "Licences"),
        new("ustawienia-licencje-jasny", 820, 640, ThemeVariant.Light, true, Section: "Licences"),

        // Wyszukiwanie: wyniki zamiast paska bocznego.
        new("ustawienia-szukaj", 820, 640, ThemeVariant.Dark, true, Search: "efekt"),
        new("ustawienia-szukaj-jasny", 820, 640, ThemeVariant.Light, true, Search: "efekt"),
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        // Zrzuty i sprawdziany odtwarzają materiał, ale nie mogą grać przez głośniki ani
        // zależeć od tego, czy maszyna ma kartę dźwiękową.
        AudioDevice.UseNullOutput = true;

        var pozycyjne = new List<string>();
        string? materialDo = null;
        string? odniesienieW = null;
        var tylkoSprawdzenia = false;
        var tylkoUstawienia = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // Wytwarza materiał dźwiękowy i kończy pracę; materiał powstaje raz dla wielu przebiegów.
                case "--material":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--material wymaga katalogu.");
                        return 1;
                    }

                    materialDo = args[i];
                    break;

                case "--porownaj":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--porownaj wymaga katalogu z obrazami odniesienia.");
                        return 1;
                    }

                    odniesienieW = args[i];
                    break;

                // Same sprawdziany zachowania, bez rysowania. Trwają sekundy, więc nadają się na
                // pierwszą bramkę i na powtarzanie przy podejrzeniu niestabilności.
                case "--sprawdzenia":
                    tylkoSprawdzenia = true;
                    break;

                // Same zrzuty okna ustawień, bez sprawdzianów i pozostałych zrzutów.
                case "--ustawienia":
                    tylkoUstawienia = true;
                    break;

                default:
                    pozycyjne.Add(args[i]);
                    break;
            }
        }

        if (materialDo is not null)
        {
            var (dluga, krotka) = Material.Write(materialDo);
            Console.WriteLine("Material dzwiekowy zapisany:");
            Console.WriteLine($"  {dluga}");
            Console.WriteLine($"  {krotka}");
            return 0;
        }

        var outputDirectory = pozycyjne.Count > 0
            ? pozycyjne[0]
            : Path.Combine(AppContext.BaseDirectory, "snapshots");

        Directory.CreateDirectory(outputDirectory);

        var track = pozycyjne.Count > 1 ? pozycyjne[1] : null;
        if (track is not null && !File.Exists(track))
        {
            Console.Error.WriteLine($"Nie ma pliku: {track}");
            return 1;
        }

        // Trzeci argument, opcjonalny: drugi plik o innym czasie trwania, do sprawdzenia, czy
        // zastąpienie kolejki plikiem z zewnątrz przełącza dekoder.
        var second = pozycyjne.Count > 2 ? pozycyjne[2] : null;
        if (second is not null && !File.Exists(second))
        {
            Console.Error.WriteLine($"Nie ma pliku: {second}");
            return 1;
        }

        // Wszystko, co zapisuje odtwarzacz, trafia do katalogu tymczasowego: zamknięcie
        // prawdziwego MainWindow zapisuje kolejkę i nadpisałoby kolejkę użytkownika.
        var scratchRoot = Path.Combine(Path.GetTempPath(), "cewka-zrzuty");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        AppPaths.Redirect(scratchRoot);

        AppBuilder.Configure<CewkaApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(CewkaApplication.FontOptions)
            .SetupWithoutStarting();

        // Wynik sprawdzania ścieżek w tle wraca na ten wątek, jak w działającym programie.
        AvaloniaSynchronizationContext.InstallIfNeeded();

        // Bez cyklu życia pulpitu wspólne usługi trzeba utworzyć ręcznie.
        ((CewkaApplication)Application.Current!).InitialiseServicesForHeadless(
            new SettingsStore(AppPaths.SettingsFile), track is null ? null : [track]);

        if (tylkoUstawienia)
        {
            foreach (var shot in SettingsShots)
            {
                CaptureSettings(shot, outputDirectory);
                Console.WriteLine($"  ok   {shot.Name}.png  ({shot.Width}x{shot.Height})");
            }

            Console.WriteLine($"Zrzuty zapisane w: {outputDirectory}");
            return 0;
        }

        // Sprawdziany zachowania okna idą przed zrzutami: obracają stanem panelu i rozmiarem,
        // a zrzuty mają zastać ustawienia takie, jakie sobie ustawiają same.
        var zachowanieOk = CheckAreasKeepWindowSize();
        zachowanieOk &= CheckQueueModes();
        zachowanieOk &= CheckLowWindows();
        zachowanieOk &= CheckPlaceholderBackdrop();

        if (track is not null && second is not null)
            zachowanieOk &= CheckReplaceQueue(track, second);

        if (tylkoSprawdzenia) return zachowanieOk ? 0 : 1;

        CaptureCoils(outputDirectory);
        CaptureBackdropPulse(outputDirectory);
        CaptureWaveform(outputDirectory);

        foreach (var shot in Shots.Concat(SettingsShots).Concat(BackdropShots).Concat(SceneShots))
        {
            try
            {
                if (SettingsShots.Contains(shot)) CaptureSettings(shot, outputDirectory);
                else if (BackdropShots.Contains(shot)) CaptureBackdrop(shot, outputDirectory);
                else if (SceneShots.Contains(shot)) CaptureScene(shot, outputDirectory);
                else Capture(shot, outputDirectory);

                Console.WriteLine($"  ok   {shot.Name}.png  ({shot.Width}x{shot.Height})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  BLAD {shot.Name}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine($"Zrzuty zapisane w: {outputDirectory}");

        // Żadne sprawdzenie nie przerywa pozostałych, żeby przebieg zgłosił wszystko naraz.
        if (_worstHeadingOffset > HeadingTolerance)
        {
            Console.Error.WriteLine(
                $"BLAD wyrownania: naglowki Korektor i Kolejka rozjezdzaja sie o " +
                $"{_worstHeadingOffset:F2} px (dopuszczalne {HeadingTolerance:F2}).");
            zachowanieOk = false;
        }
        else
        {
            Console.WriteLine(
                $"Naglowki panelu wyrownane w pionie (najwiekszy rozjazd {_worstHeadingOffset:F2} px).");
        }

        if (_worstQueueOverlap > 0.5)
        {
            Console.Error.WriteLine(
                $"BLAD ukladu: blok odtwarzania wchodzi na kolumne kolejki albo za prawa krawedz okna o " +
                $"{_worstQueueOverlap:F1} px. Trzeba scisnac zawartosc albo podniesc prog kolumny kolejki.");
            zachowanieOk = false;
        }
        else
        {
            Console.WriteLine(
                $"Blok odtwarzania miesci sie w swoim obszarze (najmniejszy zapas " +
                $"{-_worstQueueOverlap:F1} px).");
        }

        if (odniesienieW is not null)
        {
            zachowanieOk &= Odniesienie.Porownaj(
                outputDirectory, odniesienieW, Path.Combine(outputDirectory, "roznice"));
        }

        return zachowanieOk ? 0 : 1;
    }

    /// <summary>
    /// Renderuje okno ustawień. Potrzebuje działającego odtwarzacza, bo urządzenia, dekodery
    /// i stan efektów czyta z silnika.
    /// </summary>
    private static void CaptureSettings(Shot shot, string outputDirectory)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;
        CewkaApplication.Settings.Current.WindowControls = shot.Controls;
        CewkaApplication.Settings.Current.Language = shot.Language;
        CewkaApplication.Settings.Current.ColourIntensity = shot.Intensity;
        Cewka.App.Localisation.Strings.Current.SetLanguage(shot.Language);

        var player = new MainViewModel(CewkaApplication.Settings);

        try
        {
            var viewModel = new SettingsViewModel(CewkaApplication.Settings, player);
            viewModel.SelectSection(SectionOf(viewModel, shot.Section));
            if (shot.Search is not null)
                viewModel.Search.Text = shot.Search;

            var window = new SettingsWindow(viewModel)
            {
                Width = shot.Width,
                Height = shot.Height,
            };

            Render(window, shot, outputDirectory);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>Pozycja w utworze na zrzucie, w sekundach.</summary>
    private const double SnapshotPositionSeconds = 79;

    /// <summary>
    /// Przygotowuje utwór do zrzutu: tagi i okładka wczytane, pozycja w głębi utworu, pauza.
    /// </summary>
    private static void PrepareTrack(MainWindow window)
    {
        if (window.DataContext is not MainViewModel player || player.Queue.Count == 0) return;

        WaitForMetadata(player);

        // Pułapka: najpierw przewinięcie, potem pauza. Przewijanie uzgadniają trzy wątki, w tym
        // dźwiękowy, i po zatrzymaniu odtwarzania nie miałby go kto dokończyć.
        player.SeekToPosition(TimeSpan.FromSeconds(SnapshotPositionSeconds));
        SettleAfterSeek(player);
        player.Pause();

        // Obrót płyty, dryf plam i faza fali idą według zegara; zatrzymane dają powtarzalny zrzut.
        window.FreezeAnimationsForCapture();

        Console.WriteLine(player.AudioFailure.Length > 0
            ? $"       uwaga: {player.AudioFailure}"
            : $"       {player.Title} — {player.Elapsed} / {player.Total}");
    }

    /// <summary>
    /// Sprawdza, czy zastąpienie kolejki plikiem z zewnątrz odtwarza ten plik.
    ///
    /// <para>Pułapka: numer i tytuł pochodzą z kolejki i są poprawne, nawet gdy dekoder trzyma
    /// poprzedni plik. Dlatego sprawdzany jest czas trwania z otwartego dekodera.</para>
    /// </summary>
    private static bool CheckReplaceQueue(string first, string second)
    {
        CewkaApplication.Settings.Current.Window = null;
        CewkaApplication.Settings.Current.FileOpenAction = FileOpenAction.ReplaceAndPlay;
        CewkaApplication.Settings.Current.RestoreSession = false;

        var player = new MainViewModel(CewkaApplication.Settings);

        try
        {
            player.OpenFromOutside([first]);
            var (pierwszy, czasPierwszego) = WaitForDuration(player);

            player.OpenFromOutside([second]);
            var (drugi, czasDrugiego) = WaitForDuration(player, avoid: pierwszy);

            Console.WriteLine($"       zastapienie kolejki: {Path.GetFileName(first)} -> {pierwszy}" +
                              $" ({czasPierwszego} ms)   {Path.GetFileName(second)} -> {drugi}" +
                              $" ({czasDrugiego} ms)");

            if (player.Queue.Count != 1)
            {
                Console.Error.WriteLine($"  BLAD: po zastapieniu kolejka ma {player.Queue.Count} pozycji, a ma miec 1.");
                return false;
            }

            if (pierwszy == drugi)
            {
                Console.Error.WriteLine(
                    "  BLAD: po zastapieniu kolejki gra nadal poprzedni plik — czas trwania sie nie zmienil " +
                    $"({drugi}). Numer pozycji zostal ten sam, wiec dekoder nie zostal wymieniony.");
                // Tutaj tylko po wyczerpaniu oczekiwania; wcześniej pętlę kończy zmiana czasu trwania.
                Console.Error.WriteLine(
                    $"       czekano pelne {czasDrugiego} ms; w kolejce stoi \"{player.Queue[0].Title}\", " +
                    $"oczekiwano pliku {Path.GetFileName(second)}.");
                return false;
            }

            Console.WriteLine("  ok   zastapienie kolejki plikiem z zewnatrz odtwarza ten plik");
            return true;
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>
    /// Czeka, aż dekoder poda czas trwania. <paramref name="avoid"/> pozwala odczekać na zmianę:
    /// bez tego odczyt mógłby trafić w moment, w którym nowy plik jeszcze się nie otworzył.
    ///
    /// <para>Zwraca też czas oczekiwania, który trafia do dziennika, żeby odróżnić błąd programu
    /// od zbyt krótkiego czekania na obciążonej maszynie.</para>
    /// </summary>
    private static (string Czas, long Milisekund) WaitForDuration(MainViewModel player, string? avoid = null)
    {
        var zegar = System.Diagnostics.Stopwatch.StartNew();

        while (zegar.ElapsedMilliseconds < DurationWaitMilliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            player.RefreshForCapture();

            var total = player.Total;
            if (total != "0:00" && total != avoid) return (total, zegar.ElapsedMilliseconds);

            Thread.Sleep(10);
        }

        return (player.Total, zegar.ElapsedMilliseconds);
    }

    /// <summary>
    /// Ile czekać na czas trwania z dekodera; otwarcie trwa milisekundy, reszta to zapas na
    /// obciążoną maszynę.
    /// </summary>
    private const int DurationWaitMilliseconds = 15_000;

    /// <summary>
    /// Czy plamy tła przy okładce domyślnej biorą skrajne barwy pary, a nie ich uśrednienie.
    /// Porównanie wartości, bo na obrazku różnicy między dwoma fioletami nie widać.
    /// </summary>
    private static bool CheckPlaceholderBackdrop()
    {
        // Turkus i róż: skrajne barwy najdalej od siebie, więc uśrednienie widać najbardziej.
        const PlaceholderPalette wybrana = PlaceholderPalette.Turquoise;

        CewkaApplication.Settings.Current.PlaceholderPalette = wybrana;
        CewkaApplication.Settings.Current.ColourIntensity = ColourIntensity.Recommended;

        var player = new MainViewModel(CewkaApplication.Settings);

        try
        {
            Dispatcher.UIThread.RunJobs();

            var oczekiwane = CoilCover.RampColours(wybrana, Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
            var paleta = player.Palette;

            // Porównywane są odcienie, bo nasycenie zależy od ustawienia intensywności.
            Console.WriteLine(
                $"       tlo okladki domyslnej: {Odcienie(paleta)}   para: {Odcienie(oczekiwane)}");

            if (paleta.Count < 2)
            {
                Console.Error.WriteLine($"  BLAD: paleta tla ma {paleta.Count} barw, a ma miec cztery.");
                return false;
            }

            const double tolerancja = 1.0;
            var pierwszyOk = Math.Abs(paleta[0].ToHsl().H - oczekiwane[0].ToHsl().H) < tolerancja;
            var drugiOk = Math.Abs(paleta[1].ToHsl().H - oczekiwane[^1].ToHsl().H) < tolerancja;

            if (!pierwszyOk || !drugiOk)
            {
                Console.Error.WriteLine(
                    "  BLAD: plamy tla nie biora odcieni skrajnych barw pary. Oczekiwano " +
                    $"{oczekiwane[0].ToHsl().H:F1} i {oczekiwane[^1].ToHsl().H:F1}, " +
                    $"jest {paleta[0].ToHsl().H:F1} i {paleta[1].ToHsl().H:F1}.");
                return false;
            }

            // Odcień barwy środkowej nie może się pojawić na tle, bo to on powstaje z uśrednienia.
            var srodkowy = oczekiwane[oczekiwane.Length / 2].ToHsl().H;
            if (paleta.Any(c => Math.Abs(c.ToHsl().H - srodkowy) < tolerancja))
            {
                Console.Error.WriteLine(
                    $"  BLAD: na tle pojawil sie odcien barwy srodkowej ({srodkowy:F1}).");
                return false;
            }

            Console.WriteLine("  ok   plamy tla biora odcienie skrajnych barw pary, bez srodkowej");
            return true;
        }
        finally
        {
            player.Dispose();
        }
    }

    private static string Odcienie(IReadOnlyList<Color> colours) =>
        string.Join(" ", colours.Select(c => $"{c}({c.ToHsl().H:F0})"));

    /// <summary>Plik z wiersza poleceń sprawdza się w tle, a zrzut ma go zastać już w kolejce.</summary>
    private static void WaitForOpens(Window window)
    {
        if (window.DataContext is not MainViewModel player) return;

        var zegar = System.Diagnostics.Stopwatch.StartNew();

        while (!player.OpensFinished.IsCompleted && zegar.ElapsedMilliseconds < DurationWaitMilliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    /// <summary>Daje układowi okna ustalić się przy zadanym rozmiarze, bez robienia zdjęcia.</summary>
    private static void Settle(Window window, double width, double height)
    {
        WaitForOpens(window);

        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
        }
    }

    /// <summary>
    /// Pokazanie i schowanie pasa dolnego oraz kolejki nie zmienia rozmiaru okna. Treść oddaje
    /// miejsce sama: pas się przewija, kolejka w wąskim oknie staje się nakładką.
    /// </summary>
    private static bool CheckAreasKeepWindowSize()
    {
        CewkaApplication.Settings.Current.Window = null;
        CewkaApplication.Settings.Current.QueuePinned = false;

        const double width = 1000, height = 560;
        var window = new MainWindow { Width = width, Height = height };
        var player = (MainViewModel)window.DataContext!;
        var dobrze = true;

        try
        {
            window.Show();
            Settle(window, width, height);

            foreach (var (nazwa, przelacz) in new (string, Action)[]
                     {
                         ("pas", player.TogglePanel), ("kolejka", player.ToggleQueue),
                         ("pas", player.TogglePanel), ("kolejka", player.ToggleQueue),
                     })
            {
                przelacz();
                Settle(window, width, height);

                if (Math.Abs(window.Width - width) > 0.5 || Math.Abs(window.Height - height) > 0.5)
                {
                    Console.Error.WriteLine(
                        $"  BLAD: przelaczenie obszaru „{nazwa}” zmienilo rozmiar okna z {width}x{height} " +
                        $"na {window.Width}x{window.Height}.");
                    dobrze = false;
                    break;
                }
            }

            Console.WriteLine($"       rozmiar minimalny okna: {window.MinWidth}x{window.MinHeight}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        if (dobrze) Console.WriteLine("  ok   pokazanie i schowanie pasa i kolejki nie zmienia rozmiaru okna");
        return dobrze;
    }

    /// <summary>Szerokości okna, przy których sprawdzany jest układ kolejki i bloku odtwarzania.</summary>
    private static readonly int[] QueueSweep =
        [760, 820, 900, 960, 1000, 1020, 1030, 1040, 1060, 1080, 1100, 1140, 1200, 1300, 1500, 1900];

    /// <summary>
    /// Kolejka w kolumnie, gdy okno jest dość szerokie, a w węższym jako nakładka. Dla każdej
    /// szerokości mierzone jest, czy blok odtwarzania nie sięga kolumny kolejki (albo prawej
    /// krawędzi okna przy nakładce). Nakładka nie otwiera się sama przy zwężeniu okna, a Esc ją zamyka.
    /// Najmniejsza szerokość z kolumną to zmierzony próg.
    /// </summary>
    private static bool CheckQueueModes()
    {
        var dobrze = true;
        int? prog = null;

        foreach (var width in QueueSweep)
        {
            CewkaApplication.Settings.Current.Window = null;
            CewkaApplication.Settings.Current.QueuePinned = true;

            const double height = 680;
            var window = new MainWindow { Width = width, Height = height };
            var player = (MainViewModel)window.DataContext!;

            try
            {
                window.Show();
                Settle(window, width, height);

                var queue = window.FindControl<Border>("QueueColumn")!;
                var overlay = queue.Classes.Contains("queue-overlay");

                if (overlay)
                {
                    if (prog is not null)
                    {
                        Console.Error.WriteLine($"  BLAD: przy {width} px kolejka znow jest nakladka, choc przy {prog} px byla kolumna.");
                        dobrze = false;
                    }

                    if (player.QueuePinned)
                    {
                        Console.Error.WriteLine($"  BLAD: przy {width} px nakladka kolejki otworzyla sie sama.");
                        dobrze = false;
                    }

                    player.QueuePinned = true;
                    Settle(window, width, height);
                }
                else
                {
                    prog ??= width;
                }

                var zapas = -MeasureRightEdgeOverlap(window, report: false);
                var disc = window.FindControl<Panel>("DiscHost")!;
                Console.WriteLine(
                    $"       {width,5} px: {(overlay ? "nakladka" : "kolumna "),-8}  plyta {(disc.IsVisible ? disc.Bounds.Width : 0),4:F0}" +
                    $"  zapas {zapas,6:F1} px");

                if (zapas < -0.5)
                {
                    Console.Error.WriteLine($"  BLAD: przy {width} px blok odtwarzania wychodzi o {-zapas:F1} px za swoj obszar.");
                    dobrze = false;
                }

                if (overlay)
                {
                    window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    Settle(window, width, height);

                    if (player.QueuePinned)
                    {
                        Console.Error.WriteLine($"  BLAD: przy {width} px Esc nie zamknal nakladki kolejki.");
                        dobrze = false;
                    }
                }
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        Console.WriteLine(prog is null
            ? "       kolejka nie stanela w kolumnie przy zadnej szerokosci"
            : $"       prog kolumny kolejki: {prog} px");

        if (dobrze) Console.WriteLine("  ok   kolejka w kolumnie albo w nakladce, bez nachodzenia na blok odtwarzania");
        return dobrze;
    }

    /// <summary>
    /// Niskie okna: rząd transportu mieści się w obszarze bloku odtwarzania (pas korektora jest już
    /// osobnym oknem).
    /// </summary>
    private static bool CheckLowWindows()
    {
        var dobrze = true;

        foreach (var (width, height) in new[] { (760, 520), (900, 520), (1200, 520), (1200, 560), (1200, 680), (1600, 600) })
        {
            CewkaApplication.Settings.Current.Window = null;
            CewkaApplication.Settings.Current.QueuePinned = false;

            var window = new MainWindow { Width = width, Height = height };

            try
            {
                window.Show();
                Settle(window, width, height);

                var host = window.FindControl<Panel>("CentreHost")!;
                var transport = window.FindControl<Grid>("TransportRow")!;
                var info = window.FindControl<StackPanel>("TrackInfo")!;
                var disc = window.FindControl<Panel>("DiscHost")!;

                var hostTop = host.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                var hostBottom = host.TranslatePoint(new Point(0, host.Bounds.Height), window)!.Value.Y;
                var infoTop = info.TranslatePoint(new Point(0, 0), window)!.Value.Y;
                var transportBottom = transport.TranslatePoint(new Point(0, transport.Bounds.Height), window)!.Value.Y;

                var zapas = Math.Min(hostBottom - transportBottom, infoTop - hostTop);

                Console.WriteLine(
                    $"       {width}x{height}: obszar {host.Bounds.Height:F0}  plyta {(disc.IsVisible ? disc.Bounds.Width : 0):F0}  zapas {zapas:F1} px");

                if (zapas < -0.5)
                {
                    Console.Error.WriteLine(
                        $"  BLAD: przy {width}x{height} blok odtwarzania wychodzi o {-zapas:F1} px poza obszar nad pasem.");
                    dobrze = false;
                }
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        if (dobrze) Console.WriteLine("  ok   blok odtwarzania miesci sie nad pasem takze w niskim oknie");
        return dobrze;
    }
    /// <summary>
    /// Sprawdza, czy nagłówki obu części pasa stoją na jednej linii, i podaje odchyłkę.
    /// Stoją w osobnych siatkach, więc zgadzają się tylko przy równej wysokości wierszy.
    /// </summary>
    private static double MeasureHeadingOffset(MainWindow window)
    {
        // W pasie dolnym sąsiadują nagłówki korektora i efektów.
        var eq = window.FindControl<TextBlock>("EqHeading");
        var effects = window.FindControl<TextBlock>("EffectsHeading");
        if (eq is null || effects is null || !eq.IsVisible || !effects.IsVisible) return 0;

        var eqY = eq.TranslatePoint(new Point(0, 0), window)?.Y;
        var effectsY = effects.TranslatePoint(new Point(0, 0), window)?.Y;
        if (eqY is null || effectsY is null) return 0;

        var offset = effectsY.Value - eqY.Value;
        Console.WriteLine(
            $"       naglowki: Korektor y={eqY.Value:F2}  Efekty y={effectsY.Value:F2}  roznica={offset:F2} px");

        return offset;
    }

    /// <summary>Daje zegarowi odświeżania przejąć nową pozycję przed zrzutem.</summary>
    private static void SettleAfterSeek(MainViewModel player)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            player.RefreshForCapture();

            if (player.Elapsed != "0:00") break;
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Czeka na tagi i okładkę, czytane poza wątkiem interfejsu; zrzut tuż po otwarciu pliku
    /// pokazałby okładkę domyślną i brak utworu.
    /// </summary>
    private static void WaitForMetadata(MainViewModel player)
    {
        var placeholder = Cewka.App.Localisation.Strings.Current["NoTrack"];

        for (var i = 0; i < 60; i++)
        {
            Dispatcher.UIThread.RunJobs();
            player.RefreshForCapture();

            if (player.Title != placeholder && player.Cover is not null && player.Total != "0:00")
                break;

            Thread.Sleep(50);
        }

        // Okładka dekoduje się osobno od tagów; chwila zapasu na moment pomiędzy.
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
    }

    private static SettingsSection SectionOf(SettingsViewModel viewModel, string name) => name switch
    {
        "Language" => viewModel.LanguageSection,
        "Audio" => viewModel.Audio,
        "Processing" => viewModel.Processing,
        "Playback" => viewModel.Playback,
        "Integration" => viewModel.Integration,
        "SceneFullScreen" => viewModel.SceneFullScreen,
        "SceneVisuals" => viewModel.SceneVisuals,
        "SceneTelevision" => viewModel.SceneTelevision,
        "RadioSection" => viewModel.RadioSection,
        "RadioLogos" => viewModel.RadioLogos,
        "About" => viewModel.About,
        "Licences" => viewModel.Licences,
        _ => viewModel.Appearance,
    };

    /// <summary>
    /// Renderuje samo ruchome tło pod tym samym przyciemnieniem co w odtwarzaczu.
    /// </summary>
    private static void CaptureBackdrop(Shot shot, string outputDirectory, double atSeconds = 0)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;

        var (minimum, maximum) = ColourPreferences.BackdropRange(shot.Intensity);

        var backdrop = new LiveBackdrop
        {
            Palette = shot.Variant == ThemeVariant.Dark ? DarkPalette : LightPalette,
            BaseBrush = Brush(shot.Variant == ThemeVariant.Dark ? "#FF111113" : "#FFE9E9EC"),
            MinimumStrength = minimum,
            MaximumStrength = maximum,
        };

        var window = new Window
        {
            Width = shot.Width,
            Height = shot.Height,
            WindowDecorations = WindowDecorations.None,
            Content = new Panel
            {
                Children =
                {
                    backdrop,
                    new Border
                    {
                        Background = Brush(shot.Variant == ThemeVariant.Dark ? "#73101013" : "#2EFAFAFC"),
                    },
                },
            },
        };

        Render(window, shot, outputDirectory, beforeCapture: () => backdrop.FreezeForCapture(atSeconds));
    }

    /// <summary>
    /// Kilka chwil jednego cyklu jasnienia, na jednym tle i przy jednej palecie.
    ///
    /// <para>Dopiero kilka chwil obok siebie pokazuje, że każda plama jaśnieje własnym rytmem.</para>
    /// </summary>
    private static void CaptureBackdropPulse(string outputDirectory)
    {
        foreach (var intensity in Enum.GetValues<ColourIntensity>())
        {
            foreach (var seconds in (double[])[0, 4, 8, 12, 16])
            {
                var nazwa = $"puls-{intensity.ToString().ToLowerInvariant()}-{seconds:0}s";
                var shot = new Shot(nazwa, 640, 400, ThemeVariant.Dark, true, Intensity: intensity);

                CaptureBackdrop(shot, outputDirectory, seconds);
                Console.WriteLine($"  ok   {nazwa}.png");
            }
        }
    }

    /// <summary>
    /// Sama fala nad paskiem postępu, przy kilku poziomach sygnału.
    ///
    /// <para>Na zwykłym zrzucie odtwarzanie jest wstrzymane i fala leży na linii spoczynkowej,
    /// więc tu kontrolka rysowana jest osobno, w stanie czynnym.</para>
    /// </summary>
    private static void CaptureWaveform(string outputDirectory)
    {
        foreach (var level in (double[])[0.25, 0.5, 0.75, 1.0])
        {
            var nazwa = $"fala-{level * 100:0}";
            var shot = new Shot(nazwa, 620, 44, ThemeVariant.Dark, true);

            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            var wave = new WaveformView
            {
                Level = level,
                IsActive = true,
                Stroke = Brush("#FFBFD4F2"),
            };

            var window = new Window
            {
                Width = shot.Width,
                Height = shot.Height,
                WindowDecorations = WindowDecorations.None,
                Content = new Panel
                {
                    Children = { new Border { Background = Brush("#FF14141A") }, wave },
                },
            };

            Render(window, shot, outputDirectory, beforeCapture: wave.FreezeForCapture);
            Console.WriteLine($"  ok   {nazwa}.png");
        }
    }

    private static IBrush Brush(string colour) => new SolidColorBrush(Color.Parse(colour));

    /// <summary>
    /// Zapisuje pary barw okładki domyślnej w obu motywach, prosto z kodu rysującego.
    /// </summary>
    private static void CaptureCoils(string outputDirectory)
    {
        var katalog = Path.Combine(outputDirectory, "okladki");
        Directory.CreateDirectory(katalog);

        foreach (var palette in Enum.GetValues<PlaceholderPalette>())
        {
            foreach (var (motyw, ciemny) in new[] { ("ciemny", true), ("jasny", false) })
            {
                using var bitmap = CoilCover.Render(palette, ciemny);
                var nazwa = $"okladka-{palette.ToString().ToLowerInvariant()}-{motyw}.png";
                bitmap.Save(Path.Combine(katalog, nazwa), new PngBitmapEncoderOptions());
                Console.WriteLine($"  ok   okladki/{nazwa}");
            }
        }
    }

    private static void Capture(Shot shot, string outputDirectory)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;
        CewkaApplication.Settings.Current.QueuePinned = shot.QueueOpen;
        CewkaApplication.Settings.Current.PlaceholderPalette = shot.Palette;
        CewkaApplication.Settings.Current.WindowControls = shot.Controls;
        CewkaApplication.Settings.Current.Language = shot.Language;
        CewkaApplication.Settings.Current.ColourIntensity = shot.Intensity;
        Cewka.App.Localisation.Strings.Current.SetLanguage(shot.Language);

        // Rozmiar nigdy poniżej minimalnego okna: zrzut pokazywałby wtedy układ nieosiągalny dla
        // użytkownika, a wraz z nim usterki, których w programie nie ma.
        var window = new MainWindow();
        window.Width = Math.Max(shot.Width, window.MinWidth);
        window.Height = Math.Max(shot.Height, window.MinHeight);

        // Okno wczytuje plik z wiersza poleceń dopiero w OnOpened, więc utwór przygotowuje
        // wywołanie zwrotne z Render, po Show. Model widoku zwalnia MainWindow.OnClosed;
        // drugie zwolnienie kończy się wyjątkiem.
        Render(window, shot, outputDirectory,
            afterShow: () =>
            {
                PrepareTrack(window);

                // Nakładka kolejki otwiera się tylko na żądanie, więc zrzut otwiera ją sam.
                // Fokus na wierszu kolejki wymaga panelu kolejki na ekranie, więc ten zrzut też go przypina.
                if ((shot.QueueOverlay || shot.Focus == "QueueRow") && window.DataContext is MainViewModel player)
                    player.QueuePinned = true;
            },
            beforeCapture: () =>
            {
                if (shot.Focus is not null) FocusForCapture(window, shot);

                MeasureQueueOverlap(window);

                if (!shot.PanelOpen) return;

                var offset = Math.Abs(MeasureHeadingOffset(window));
                if (offset > _worstHeadingOffset) _worstHeadingOffset = offset;
            });
    }

    /// <summary>Pierwszy wiersz kolejki; lista dopiero co przypiętego panelu tworzy wiersze w kolejnym przebiegu układu.</summary>
    private static InputElement? QueueRow(MainWindow window)
    {
        var list = window.FindControl<ListBox>("QueueList");
        if (list is null) return null;

        for (var i = 0; i < 4 && list.ContainerFromIndex(0) is null; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine($"       wiersze kolejki: {list.ItemCount}, widoczna: {list.IsEffectivelyVisible}");
        return list.ContainerFromIndex(0);
    }

    /// <summary>Fokus z klawiatury na wskazanej kontrolce, żeby zrzut pokazał pierścień fokusu.</summary>
    private static void FocusForCapture(MainWindow window, Shot shot)
    {
        var descendants = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window);
        InputElement? target = shot.Focus switch
        {
            "EqFader" => descendants.OfType<EqFader>().Skip(3).FirstOrDefault(),
            "QueueRow" => QueueRow(window),
            var name => window.FindControl<Control>(name!),
        };

        if (target is null) throw new InvalidOperationException($"brak kontrolki do fokusu: {shot.Focus}");

        target.Focus(NavigationMethod.Tab);

        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(shot.Width, shot.Height));
            window.Arrange(new Rect(0, 0, shot.Width, shot.Height));
        }
    }

    /// <summary>Największa odchyłka nagłówków w tym przebiegu, w pikselach logicznych.</summary>
    private static double _worstHeadingOffset;

    /// <summary>
    /// Najgłębsze wejście bloku odtwarzania na kolumnę kolejki, w pikselach.
    ///
    /// <para>Rząd transportu ma około 440 px i nic go nie ściska, więc w zbyt wąskim oknie
    /// wchodziłby na kolejkę. Gołym okiem widać to tylko przy niektórych szerokościach.</para>
    /// </summary>
    /// <remarks>
    /// Zaczyna od minus nieskończoności, żeby zapas dało się odczytać tak samo jak nachodzenie:
    /// wartość ujemna to wolne piksele, dodatnia to piksele za krawędzią.
    /// </remarks>
    private static double _worstQueueOverlap = double.NegativeInfinity;

    /// <summary>Elementy, które nie mogą sięgać kolumny kolejki ani prawej krawędzi okna.</summary>
    private static readonly string[] MustNotReachQueue = ["VolumeSlider", "TrackInfo", "Seek", "TransportRow"];

    private static void MeasureQueueOverlap(MainWindow window)
    {
        var overlap = MeasureRightEdgeOverlap(window, report: true);
        if (overlap > _worstQueueOverlap) _worstQueueOverlap = overlap;
    }

    /// <summary>
    /// Najgłębsze wejście bloku odtwarzania za jego prawą krawędź: lewą krawędź kolumny kolejki,
    /// a przy nakładce albo bez kolejki prawą krawędź okna. Wartość ujemna to zapas.
    /// </summary>
    private static double MeasureRightEdgeOverlap(MainWindow window, bool report)
    {
        var queue = window.FindControl<Border>("QueueColumn");
        var column = queue is { IsVisible: true } && !queue.Classes.Contains("queue-overlay");

        var edge = column ? queue!.TranslatePoint(new Point(0, 0), window)?.X ?? window.Bounds.Width : window.Bounds.Width;
        var worst = double.NegativeInfinity;

        foreach (var name in MustNotReachQueue)
        {
            var control = window.FindControl<Control>(name);
            if (control is null || !control.IsEffectivelyVisible || control.Bounds.Width <= 0) continue;

            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)?.X;
            if (right is null) continue;

            var overlap = right.Value - edge;
            if (overlap > worst) worst = overlap;

            if (report && overlap > 0.5)
            {
                Console.WriteLine(
                    $"       nachodzi: {name} konczy sie {overlap:F1} px za krawedzia " +
                    (column ? "kolejki" : "okna"));
            }
        }

        if (report && worst > 0.5)
        {
            // Rozbicie szerokości na składniki, żeby było widać, co zabiera miejsce.
            var centre = window.FindControl<StackPanel>("CentreArea");
            var playing = window.FindControl<Grid>("NowPlaying");
            var disc = window.FindControl<Panel>("DiscHost");
            var volume = window.FindControl<Control>("VolumeSlider");

            Console.WriteLine(
                $"       szerokosci: okno {window.Bounds.Width:F0}  krawedz {edge:F0}" +
                $"  srodek {centre?.Bounds.Width ?? -1:F0}  blok {playing?.Bounds.Width ?? -1:F0}" +
                $"  plyta {disc?.Bounds.Width ?? -1:F0}  glosnosc {volume?.Bounds.Width ?? -1:F0}");
        }

        return worst;
    }
    /// <summary>
    /// Pół piksela; mniejsze odchyłki to zaokrąglenia układu.
    /// </summary>
    private const double HeadingTolerance = 0.5;

    /// <summary>
    /// Okno główne ze sceną otwartą na wierzchu. Zamrożenie idzie przed otwarciem sceny, bo
    /// wyłącza też przenikania, które zrzut złapałby w połowie.
    /// </summary>
    private static void CaptureScene(Shot shot, string outputDirectory)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;
        CewkaApplication.Settings.Current.QueuePinned = shot.QueueOpen;
        CewkaApplication.Settings.Current.PlaceholderPalette = shot.Palette;
        CewkaApplication.Settings.Current.WindowControls = shot.Controls;
        CewkaApplication.Settings.Current.Language = shot.Language;
        CewkaApplication.Settings.Current.ColourIntensity = shot.Intensity;
        Cewka.App.Localisation.Strings.Current.SetLanguage(shot.Language);

        var window = new MainWindow { Width = shot.Width, Height = shot.Height };

        Render(window, shot, outputDirectory,
            afterShow: () =>
            {
                PrepareTrack(window);
                window.FreezeAnimationsForCapture();
                window.OpenSceneForCapture(shot.SceneChrome);
            });
    }

    private static void Render(Window window, Shot shot, string outputDirectory,
        Action? afterShow = null, Action? beforeCapture = null)
    {
        window.Show();

        // Okno wczytuje zawartość w OnOpened, więc wszystko, co jej dotyczy, dzieje się tutaj.
        WaitForOpens(window);
        Dispatcher.UIThread.RunJobs();
        afterShow?.Invoke();

        // Układ, wiązania i pierwsze rysowanie mają się ustalić przed zrzutem.
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(shot.Width, shot.Height));
            window.Arrange(new Rect(0, 0, shot.Width, shot.Height));
        }

        Console.WriteLine(
            $"       diag: Width={window.Width} ClientSize={window.ClientSize} Bounds={window.Bounds}");

        beforeCapture?.Invoke();

        var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("renderer nie zwrócił klatki");

        using (frame)
        {
            frame.Save(Path.Combine(outputDirectory, shot.Name + ".png"), new PngBitmapEncoderOptions());
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
