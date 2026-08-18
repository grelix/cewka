using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Platform;

// "Cewka.App" is both a namespace and the Application subclass inside it,
// so the type needs an alias to be usable here.
using CewkaApplication = Cewka.App.App;

namespace Cewka.Snapshots;

/// <summary>
/// Renders the main window off-screen with the real Skia backend and writes PNGs.
/// This is how I check the interface against the design without opening a window at all,
/// and it is deterministic enough to diff one version against the next.
///
/// Uzycie:  dotnet run --project tools/Cewka.Snapshots -- [katalog-wyjsciowy] [plik-audio]
///
/// Podanie pliku audio wpuszcza go do okna tak samo, jak zrobiloby to otwarcie pliku
/// z menedzera plikow - z tytulem, wykonawca, okladka i czasem trwania. Bez niego okno
/// fotografuje sie z pusta kolejka.
/// </summary>
internal static class Program
{
    private sealed record Shot(string Name, int Width, int Height, ThemeVariant Variant, bool PanelOpen,
        WindowControlsPosition Controls = WindowControlsPosition.Right, string Language = "pl",
        string Section = "Appearance",
        ColourIntensity Intensity = ColourIntensity.Recommended,
        bool QueueOpen = true,
        PlaceholderPalette Palette = PlaceholderPalette.BlueViolet);

    private static readonly Shot[] Shots =
    [
        new("ciemny-1180", 1200, 680, ThemeVariant.Dark, true),
        new("jasny-1180", 1200, 680, ThemeVariant.Light, true),
        new("ciemny-zwiniety", 1180, 448, ThemeVariant.Dark, false, QueueOpen: false),
        new("jasny-zwiniety", 1180, 448, ThemeVariant.Light, false, QueueOpen: false),

        // Waskie okna z kolejka — tam, gdzie blok odtwarzania wchodzil na liste utworow.
        // 1160 px to zmierzona szerokosc minimalna z kolejka, wiec przypadek najciasniejszy
        // z mozliwych; 1200 to ten sam uklad z niewielkim zapasem.
        new("waskie-z-kolejka", 1200, 665, ThemeVariant.Dark, true),
        new("waskie-z-kolejka-zapas", 1260, 680, ThemeVariant.Dark, true),
        new("waskie-z-kolejka-bez-pasa", 1200, 480, ThemeVariant.Dark, false),

        // Cztery mozliwe stany dwoch niezaleznych obszarow. Odkad pas dolny i kolejka wlaczaja
        // sie osobno, kazde z tych zestawien uzytkownik moze zobaczyc — wiec kazde musi wygladac.
        new("obszary-oba", 1200, 680, ThemeVariant.Dark, true),
        new("obszary-tylko-pas", 1180, 680, ThemeVariant.Dark, true, QueueOpen: false),
        new("obszary-tylko-kolejka", 1180, 680, ThemeVariant.Dark, false),
        new("obszary-zadne", 1180, 448, ThemeVariant.Dark, false, QueueOpen: false),
        new("obszary-tylko-kolejka-jasny", 1180, 680, ThemeVariant.Light, false),

        // Pary o skrajnych barwach najdalszych od siebie. Odkad tlo bierze barwy wprost z pary,
        // a nie z analizy narysowanej spirali, wlasnie na nich roznica jest najwieksza — wczesniej
        // oba te zestawy dawaly na tle jeden zamglony odcien.
        new("para-turkus", 1180, 680, ThemeVariant.Dark, true, Palette: PlaceholderPalette.Turquoise),
        new("para-wisnia", 1180, 680, ThemeVariant.Dark, true, Palette: PlaceholderPalette.Cherry),
        new("para-mieta-jasny", 1180, 680, ThemeVariant.Light, true, Palette: PlaceholderPalette.Mint),
        // Waskie okna maja kolejke schowana, bo z nia widoczna okno tak wezsze byc nie moze —
        // szerokosc minimalna z kolejka to 1200 px. Zrzut stanu nieosiagalnego nic nie mowi.
        new("ciemny-waski", 920, 660, ThemeVariant.Dark, true, QueueOpen: false),
        new("ciemny-szeroki", 1600, 820, ThemeVariant.Dark, true),
        new("ciemny-pelnyekran", 2560, 1400, ThemeVariant.Dark, true),
        new("przyciski-lewo", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.Left),
        new("przyciski-macos", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.MacOs),

        // Najciasniejsze przypadki: dokladnie wysokosc minimalna dla obu stanow panelu, w tym
        // przy najwezszym dopuszczalnym oknie. Tu wychodzi kazde nachodzenie elementow na siebie.
        new("minimum-panel", 1200, 665, ThemeVariant.Dark, true),
        new("minimum-panel-waskie", 1000, 665, ThemeVariant.Dark, true, QueueOpen: false),
        new("minimum-zwiniety", 900, 360, ThemeVariant.Dark, false, QueueOpen: false),

        // Interfejs po angielsku: sprawdzenie, ze przelaczenie jezyka faktycznie dociera
        // do wszystkich napisow, a nie tylko do tych zlozonych na nowo.
        new("angielski", 1180, 680, ThemeVariant.Dark, true, Language: "en"),

        // Skrajne intensywnosci barw. Wariant zalecany jest juz sfotografowany jako
        // "ciemny-1180" i sluzy za odniesienie: musi wyjsc identyczny jak przed dodaniem
        // tego ustawienia, bo mnozniki wynosza tam dokladnie jeden.
        new("barwy-subtelne", 1180, 680, ThemeVariant.Dark, true, Intensity: ColourIntensity.Subtle),
        new("barwy-intensywne", 1180, 680, ThemeVariant.Dark, true, Intensity: ColourIntensity.Intense),
        new("barwy-subtelne-jasny", 1180, 680, ThemeVariant.Light, true, Intensity: ColourIntensity.Subtle),
        new("barwy-intensywne-jasny", 1180, 680, ThemeVariant.Light, true, Intensity: ColourIntensity.Intense),
    ];

    /// <summary>
    /// Samo tlo, z paleta o wyraznie roznych barwach.
    ///
    /// Paleta domyslnej okladki jest praktycznie jednobarwna, wiec na zwyklym zrzucie nie widac,
    /// czy tlo sklada sie z osobnych plam, czy z jednego nalotu - kilka odcieni tego samego
    /// blekitu zleje sie tak czy owak. Dopiero rozne barwy pokazuja ksztalt i polozenie plam.
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
    /// The settings window, which is a window of its own and needs its own pass. Zawartosc jest
    /// podzielona na zakladki, wiec kazda wymaga osobnego zrzutu - inaczej sprawdzona zostalaby
    /// tylko ta, ktora otwiera sie pierwsza.
    /// </summary>
    private static readonly Shot[] SettingsShots =
    [
        new("ustawienia-wyglad", 820, 640, ThemeVariant.Dark, true),
        new("ustawienia-dzwiek", 820, 640, ThemeVariant.Dark, true, Section: "Audio"),
        new("ustawienia-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Section: "Playback"),
        new("ustawienia-system", 820, 640, ThemeVariant.Dark, true, Section: "Integration"),
        // Zakladka informacji urosla o wpis o repozytorium i o sprawdzanie nowszego wydania,
        // wiec doszedl zrzut wyzszy, pokazujacy ja calosciowo.
        new("ustawienia-informacje", 820, 640, ThemeVariant.Dark, true, Section: "About"),
        new("ustawienia-informacje-cala", 820, 820, ThemeVariant.Dark, true, Section: "About"),
        new("ustawienia-informacje-jasny", 820, 820, ThemeVariant.Light, true, Section: "About"),
        new("ustawienia-jasny", 820, 640, ThemeVariant.Light, true),

        // Okno w rozmiarze najmniejszym dopuszczalnym, na zakladce o najbogatszej zawartosci.
        new("ustawienia-minimum", 700, 520, ThemeVariant.Dark, true, Section: "Audio"),

        // Zakladka wygladu jest teraz najwyzsza z wszystkich — doszly intensywnosc barw,
        // przelacznik plakietki i piec probek barw domyslnej okladki. Osobny, wysoki zrzut
        // pokazuje ja calosciowo; ten w rozmiarze 820x640 pokazuje, co widac bez przewijania.
        new("ustawienia-wyglad-cala", 820, 1000, ThemeVariant.Dark, true),
        new("ustawienia-wyglad-cala-jasny", 820, 1000, ThemeVariant.Light, true),

        // Kazdy jezyk osobno, na zakladce dzwieku: tam etykiety paskow segmentowych sa
        // najdluzsze. To zarazem jedyne miejsce, ktore sprawdza, czy jezyk jest w ogole
        // zarejestrowany w klasie Strings — plik jezykowy bez wpisu na liscie kodow nie
        // zmienilby tutaj niczego i zrzut wyszedlby po polsku.
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

        // Wietnamski takze w zakladce wygladu: tam stoja nazwy par barw, a wietnamski zapis
        // rozbija wyrazy na sylaby, wiec akurat te etykiety sa tam najbardziej narazone.
        new("ustawienia-wietnamski-wyglad", 820, 1000, ThemeVariant.Dark, true, Language: "vi"),

        // Zakladka efektow: piec wpisow z opisem, przelacznikiem i suwakiem. Najdluzsza
        // z zakladek, wiec obok widoku bez przewijania stoi zrzut calosciowy.
        new("ustawienia-efekty", 820, 640, ThemeVariant.Dark, true, Section: "Effects"),
        new("ustawienia-efekty-cala", 820, 1180, ThemeVariant.Dark, true, Section: "Effects"),
        new("ustawienia-efekty-jasny", 820, 1180, ThemeVariant.Light, true, Section: "Effects"),
        new("ustawienia-efekty-wloski", 820, 1180, ThemeVariant.Dark, true, Language: "it", Section: "Effects"),

        // Zakladka jezyka: lista trzynastu pozycji i nota o tlumaczeniu maszynowym.
        new("ustawienia-jezyk", 820, 640, ThemeVariant.Dark, true, Section: "Language"),
        new("ustawienia-jezyk-jasny", 820, 640, ThemeVariant.Light, true, Section: "Language"),
        new("ustawienia-jezyk-waski", 700, 640, ThemeVariant.Dark, true, Section: "Language"),
        new("ustawienia-jezyk-rosyjski", 820, 640, ThemeVariant.Dark, true, Language: "ru", Section: "Language"),
        new("ustawienia-jezyk-wietnamski", 820, 640, ThemeVariant.Dark, true, Language: "vi", Section: "Language"),
        new("ustawienia-jezyk-grecki", 820, 640, ThemeVariant.Dark, true, Language: "el", Section: "Language"),

        // Jezyki dolozone w 0.7.0. Grecki jest wsrod nich przypadkiem osobnym: to pierwsze
        // w programie pismo inne niz lacinskie i cyrylica.
        new("ustawienia-grecki", 820, 640, ThemeVariant.Dark, true, Language: "el", Section: "Audio"),
        new("ustawienia-grecki-wyglad", 820, 1000, ThemeVariant.Dark, true, Language: "el"),
        new("ustawienia-niderlandzki", 820, 640, ThemeVariant.Dark, true, Language: "nl", Section: "Audio"),
        new("ustawienia-rumunski", 820, 640, ThemeVariant.Dark, true, Language: "ro", Section: "Audio"),
        new("ustawienia-wegierski", 820, 640, ThemeVariant.Dark, true, Language: "hu", Section: "Audio"),
        new("ustawienia-wegierski-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Language: "hu", Section: "Playback"),

        // Cyrylica takze w zakladce wygladu: tam stoja nazwy par barw.
        new("ustawienia-rosyjski-wyglad", 820, 940, ThemeVariant.Dark, true, Language: "ru"),
        new("ustawienia-ukrainski-odtwarzanie", 820, 640, ThemeVariant.Dark, true, Language: "uk", Section: "Playback"),
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        var pozycyjne = new List<string>();
        string? materialDo = null;
        string? odniesienieW = null;
        var tylkoSprawdzenia = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // Wytwarza material dzwiekowy i konczy prace. Osobny tryb, bo material powstaje
                // raz, a zrzuty moga potem korzystac z niego wielokrotnie.
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

                // Same sprawdziany zachowania, bez rysowania czegokolwiek. Trwaja sekundy zamiast
                // minut, wiec nadaja sie na pierwsza brame przebiegu — i na powtarzanie ich
                // w kolko, gdy trzeba rozstrzygnac, czy sprawdzian jest chwiejny.
                case "--sprawdzenia":
                    tylkoSprawdzenia = true;
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

        // Trzeci argument, opcjonalny: drugi plik dzwiekowy o innym czasie trwania. Sluzy
        // wylacznie sprawdzeniu, czy zastapienie kolejki plikiem z zewnatrz faktycznie
        // przelacza dekoder.
        var second = pozycyjne.Count > 2 ? pozycyjne[2] : null;
        if (second is not null && !File.Exists(second))
        {
            Console.Error.WriteLine($"Nie ma pliku: {second}");
            return 1;
        }

        // Everything the running player would write goes to a throwaway directory instead of the
        // real one. Photographing the interface closes a genuine MainWindow, and closing it saves
        // the queue — so without this, taking snapshots would replace my own saved queue with
        // whatever track this run happened to be given.
        var scratchRoot = Path.Combine(Path.GetTempPath(), "cewka-zrzuty");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        AppPaths.Redirect(scratchRoot);

        AppBuilder.Configure<CewkaApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(CewkaApplication.FontOptions)
            .SetupWithoutStarting();

        // No desktop lifetime runs here, so the shared services must be created by hand.
        ((CewkaApplication)Application.Current!).InitialiseServicesForHeadless(
            new SettingsStore(AppPaths.SettingsFile), track is null ? null : [track]);

        // Sprawdziany zachowania okna idą przed zrzutami: obracają stanem panelu i rozmiarem,
        // a zrzuty mają zastać ustawienia takie, jakie sobie ustawiają same.
        var zachowanieOk = CheckPanelHeightMemory();
        zachowanieOk &= CheckQueueWidthMemory();
        zachowanieOk &= CheckPlaceholderBackdrop();

        if (track is not null && second is not null)
            zachowanieOk &= CheckReplaceQueue(track, second);

        if (tylkoSprawdzenia) return zachowanieOk ? 0 : 1;

        CaptureCoils(outputDirectory);
        CaptureBackdropPulse(outputDirectory);
        CaptureWaveform(outputDirectory);

        foreach (var shot in Shots.Concat(SettingsShots).Concat(BackdropShots))
        {
            try
            {
                if (SettingsShots.Contains(shot)) CaptureSettings(shot, outputDirectory);
                else if (BackdropShots.Contains(shot)) CaptureBackdrop(shot, outputDirectory);
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

        // Zadne z ponizszych sprawdzen nie przerywa pozostalych: przebieg w chmurze ma powiedziec
        // wszystko, co znalazl, a nie pierwsza rzecz, na ktora trafil.
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

        if (_worstQueueOverlap > 0)
        {
            Console.Error.WriteLine(
                $"BLAD ukladu: blok odtwarzania wchodzi na kolumne kolejki o " +
                $"{_worstQueueOverlap:F1} px. Trzeba albo scisnac zawartosc, albo podniesc " +
                "szerokosc minimalna z kolejka.");
            zachowanieOk = false;
        }
        else if (double.IsNegativeInfinity(_worstQueueOverlap))
        {
            Console.WriteLine("Nachodzenia na kolejke nie mierzono: w tym przebiegu nie bylo jej widac.");
        }
        else
        {
            Console.WriteLine(
                $"Blok odtwarzania miesci sie obok kolejki (najmniejszy zapas " +
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
    /// Renders the settings window. It needs a live player behind it, because most of what it
    /// shows — devices, decoders, the state of the effects — is read from the running engine
    /// rather than from the settings file.
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

    /// <summary>Position the track is photographed at, in seconds.</summary>
    private const double SnapshotPositionSeconds = 79;

    /// <summary>
    /// Brings the loaded track to the state it should be photographed in: tags and cover read,
    /// playback moved into the track and then stopped.
    /// </summary>
    private static void PrepareTrack(MainWindow window)
    {
        if (window.DataContext is not MainViewModel player || player.Queue.Count == 0) return;

        WaitForMetadata(player);

        // Pasek postepu stojacy na zerze wyglada jak program, ktorego nikt jeszcze nie
        // uruchomil. Przewiniecie w glab utworu pokazuje interfejs w uzyciu.
        //
        // Kolejnosc jest istotna: przewijanie uzgadniaja miedzy soba trzy watki, a jednym z nich
        // jest watek dzwiekowy. Przewiniecie po zatrzymaniu odtwarzania zostaloby w zawieszeniu,
        // bo nie ma kto go dokonczyc - dlatego najpierw przewiniecie, dopiero potem pauza.
        player.SeekToPosition(TimeSpan.FromSeconds(SnapshotPositionSeconds));
        SettleAfterSeek(player);
        player.Pause();

        // Obrot plyty, dryf plam tla i faza fali liczyly sie od czasu rzeczywistego, wiec dwa
        // przebiegi tego samego kodu dawaly rozne obrazy - a wtedy zrzuty nie nadaja sie do
        // porownywania wersji, czyli do tego, po co istnieja. Tu wszystkie trzy staja.
        window.FreezeAnimationsForCapture();

        Console.WriteLine(player.AudioFailure.Length > 0
            ? $"       uwaga: {player.AudioFailure}"
            : $"       {player.Title} — {player.Elapsed} / {player.Total}");
    }

    /// <summary>
    /// Checks that replacing the queue with a file opened from outside actually plays that file.
    ///
    /// <para>Sprawdzane jest czas trwania, a nie numer pozycji ani tytuł. Numer i tytuł biorą się
    /// z listy kolejki i po podmianie są poprawne nawet wtedy, gdy dekoder trzyma jeszcze
    /// poprzedni plik — a to był właśnie błąd. Czas trwania czytany jest z otwartego dekodera,
    /// więc mówi, co gra naprawdę.</para>
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
                // Do tego miejsca dochodzi sie wylacznie po wyczerpaniu calego oczekiwania:
                // petla konczy sie wczesniej dopiero wtedy, gdy czas trwania sie zmieni.
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
    /// <para>Zwracany jest także czas oczekiwania. Sprawdzian raz nie przeszedł na maszynie
    /// obciążonej innym zadaniem, a przy samym werdykcie „przeszedł / nie przeszedł" nie sposób
    /// odróżnić błędu w programie od zbyt krótkiego oczekiwania. Zapas jest więc duży, a rzeczywisty
    /// czas trafia do dziennika — jeśli otwarcie pliku zacznie kiedyś trwać sekundy zamiast
    /// milisekund, będzie to widać, zanim sprawdzian zacznie zapalać się bez powodu.</para>
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
    /// Ile czekać na czas trwania z dekodera. Otwarcie pliku trwa milisekundy, więc piętnaście
    /// sekund to zapas na maszynę zajętą czym innym, a nie realny czas działania.
    /// </summary>
    private const int DurationWaitMilliseconds = 15_000;

    /// <summary>
    /// Checks that expanding the panel in a window too short for it is reversible: the height
    /// from before comes back when the panel is collapsed again, unless the user has resized
    /// the window in between.
    /// <para>
    /// Three cases, because only the first two are obvious and the third is the one that used to
    /// be wrong in the opposite direction — restoring a height the user had since overruled.
    /// </para>
    /// </summary>
    /// <summary>
    /// Czy plamy tla przy okladce domyslnej biora skrajne barwy pary, a nie ich usrednienie.
    ///
    /// <para>Odczyt barw z narysowanego obrazka przechodzi przez kubelkowanie i usrednianie,
    /// wiec para „turkus i roz" dawala na tle dwa odcienie jednego zamglonego fioletu. Tutaj
    /// sprawdzane jest wprost, czy paleta tla rowna sie skrajnym barwom pary — porownanie
    /// z wartosciami, a nie ogladanie obrazka, bo roznicy miedzy fioletem a fioletem nie widac.</para>
    /// </summary>
    private static bool CheckPlaceholderBackdrop()
    {
        // Turkus i roz: para o skrajnych barwach najdalszych od siebie, wiec usrednienie
        // rzucaloby sie w oczy najbardziej.
        const PlaceholderPalette wybrana = PlaceholderPalette.Turquoise;

        CewkaApplication.Settings.Current.PlaceholderPalette = wybrana;
        CewkaApplication.Settings.Current.ColourIntensity = ColourIntensity.Recommended;

        var player = new MainViewModel(CewkaApplication.Settings);

        try
        {
            Dispatcher.UIThread.RunJobs();

            var oczekiwane = CoilCover.RampColours(wybrana, Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
            var paleta = player.Palette;

            // Porownywane sa odcienie, a nie barwy w calosci: nasycenie skaluje ustawienie
            // intensywnosci, wiec dokladne barwy zaleza od niego, a odcien nie. Sedno sprawdzianu
            // dotyczy wlasnie odcienia — czy na tle sa dwa konce pary, czy jeden kolor po srodku.
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

            // Odcien barwy srodkowej nie ma prawa pojawic sie na tle — to on powstawal
            // z usrednienia i to on zamienial pare w jeden kolor.
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

    /// <summary>
    /// To samo dla szerokosci, ktora pilnuje kolumna kolejki.
    ///
    /// <para>Odkad pas dolny i kolejka wlaczaja sie niezaleznie, kazdy z nich odpowiada za swoj
    /// wymiar: pas za wysokosc, kolejka za szerokosc. Wspolny warunek przywracania cofalby
    /// szerokosc przy chowaniu korektora, ktory z szerokoscia nie ma nic wspolnego.</para>
    /// </summary>
    private static bool CheckQueueWidthMemory()
    {
        CewkaApplication.Settings.Current.Window = null;
        CewkaApplication.Settings.Current.PanelOpen = false;
        CewkaApplication.Settings.Current.QueueOpen = false;

        // Ponizej minimum z kolejka (1000), powyzej minimum bez niej (900).
        const double startingWidth = 950;
        var window = new MainWindow { Width = startingWidth, Height = 500 };
        var player = (MainViewModel)window.DataContext!;
        var dobrze = true;

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            player.ToggleQueue();
            Dispatcher.UIThread.RunJobs();
            var zKolejka = window.Width;

            player.ToggleQueue();
            Dispatcher.UIThread.RunJobs();
            var bezKolejki = window.Width;

            Console.WriteLine($"       szerokosc: start {startingWidth}  -> kolejka {zKolejka}" +
                              $"  -> po schowaniu {bezKolejki}");

            if (zKolejka <= startingWidth)
            {
                Console.Error.WriteLine("  BLAD: pokazanie kolejki nie poszerzylo okna.");
                dobrze = false;
            }

            if (Math.Abs(bezKolejki - startingWidth) > 0.5)
            {
                Console.Error.WriteLine(
                    $"  BLAD: po schowaniu kolejki okno ma {bezKolejki}, a powinno wrocic " +
                    $"do {startingWidth}.");
                dobrze = false;
            }

            // Pas dolny nie ma prawa ruszyc szerokosci — to osobny wymiar i osobny obszar.
            var przedPasem = window.Width;
            player.TogglePanel();
            Dispatcher.UIThread.RunJobs();

            if (Math.Abs(window.Width - przedPasem) > 0.5)
            {
                Console.Error.WriteLine(
                    $"  BLAD: pokazanie pasa dolnego zmienilo szerokosc z {przedPasem} " +
                    $"na {window.Width}; obszary maja byc niezalezne.");
                dobrze = false;
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        if (dobrze) Console.WriteLine("  ok   pamiec szerokosci okna i niezaleznosc obszarow");
        return dobrze;
    }

    private static bool CheckPanelHeightMemory()
    {
        // Zadnej zapamietanej geometrii: inaczej odtworzenie rozmiaru z ustawien nadpisalo by
        // wysokosc, ktora ten sprawdzian wlasnie ustawia.
        CewkaApplication.Settings.Current.Window = null;
        CewkaApplication.Settings.Current.PanelOpen = false;
        CewkaApplication.Settings.Current.QueueOpen = false;

        const double startingHeight = 500;
        var window = new MainWindow { Width = 1180, Height = startingHeight };
        var player = (MainViewModel)window.DataContext!;
        var dobrze = true;

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            player.TogglePanel();
            Dispatcher.UIThread.RunJobs();
            var rozwiniete = window.Height;

            player.TogglePanel();
            Dispatcher.UIThread.RunJobs();
            var zwiniete = window.Height;

            Console.WriteLine($"       wysokosc: start {startingHeight}  -> panel {rozwiniete}" +
                              $"  -> po schowaniu {zwiniete}");

            if (rozwiniete <= startingHeight)
            {
                Console.Error.WriteLine("  BLAD: rozwiniecie panelu nie podnioslo okna.");
                dobrze = false;
            }

            if (Math.Abs(zwiniete - startingHeight) > 0.5)
            {
                Console.Error.WriteLine(
                    $"  BLAD: po schowaniu panelu okno ma {zwiniete}, a powinno wrocic " +
                    $"do {startingHeight}.");
                dobrze = false;
            }

            // Trzeci przypadek: uzytkownik sam zmienia rozmiar, gdy panel jest rozwiniety.
            // Wtedy zapamietana wysokosc traci waznosc i okno ma zostac tam, gdzie je postawil.
            player.TogglePanel();
            Dispatcher.UIThread.RunJobs();

            const double wybranaPrzezUzytkownika = 760;
            window.Height = wybranaPrzezUzytkownika;
            Dispatcher.UIThread.RunJobs();

            player.TogglePanel();
            Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"       po recznej zmianie na {wybranaPrzezUzytkownika}: {window.Height}");

            if (Math.Abs(window.Height - wybranaPrzezUzytkownika) > 0.5)
            {
                Console.Error.WriteLine(
                    $"  BLAD: okno cofnelo reczna zmiane rozmiaru — ma {window.Height}, " +
                    $"a powinno zostac na {wybranaPrzezUzytkownika}.");
                dobrze = false;
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        if (dobrze) Console.WriteLine("  ok   pamiec wysokosci okna przy zwijaniu panelu");
        return dobrze;
    }

    /// <summary>
    /// Checks that the two panel headings sit on the same line, and reports by how much they
    /// miss if they do not.
    /// <para>
    /// The two sit in separate grids on either side of a divider, so nothing in the layout makes
    /// them agree — they only look aligned as long as both rows happen to be the same height,
    /// and the equaliser row is driven by the switch and the mode buttons beside it. Measuring
    /// is the only way to know; by eye a few pixels read as "roughly right".
    /// </para>
    /// </summary>
    private static double MeasureHeadingOffset(MainWindow window)
    {
        // Odkad kolejka wyprowadzila sie do wlasnej kolumny, sasiadami w pasie dolnym sa
        // naglowki korektora i efektow — i to one musza stac na jednej wysokosci.
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

    /// <summary>Lets the refresh timer pick the new position up before the frame is captured.</summary>
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
    /// Waits for the tags and the cover to arrive. Both are read off the decode thread, so a
    /// snapshot taken straight after opening the file would show the placeholder disc and the
    /// "no track" caption — which is exactly what the picture is meant not to show.
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

        // Okladka dekoduje sie osobno od tagow; chwila zapasu, zeby nie trafic w moment miedzy.
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
    }

    private static SettingsSection SectionOf(SettingsViewModel viewModel, string name) => name switch
    {
        "Language" => viewModel.LanguageSection,
        "Effects" => viewModel.EffectsSection,
        "Audio" => viewModel.Audio,
        "Playback" => viewModel.Playback,
        "Integration" => viewModel.Integration,
        "About" => viewModel.About,
        _ => viewModel.Appearance,
    };

    /// <summary>
    /// Renders the moving colour field on its own, under the same scrim the player puts over it.
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
    /// <para>Każda plama ma własny okres, więc na nieruchomym obrazie nie widać, że w ogóle
    /// jaśnieją. Dopiero kilka chwil obok siebie pokazuje, że każda idzie swoim rytmem — i że
    /// nie robią tego wszystkie razem, co wyglądałoby jak migotanie całego okna.</para>
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
    /// <para>Na zwykłym zrzucie okna fali praktycznie nie widać: przed zdjęciem odtwarzanie jest
    /// zatrzymywane, a wtedy wykres opada do linii spoczynkowej. Amplitudy nie dało się więc
    /// obejrzeć ani porównać między wersjami. Tutaj kontrolka rysowana jest osobno, z podanym
    /// poziomem i w stanie czynnym.</para>
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
    /// Writes the five default-cover colour pairs in both themes, straight from the drawing code.
    /// <para>
    /// These used to be two files in the repository, so looking at them meant opening the files.
    /// Now they are drawn at run time and the only way to see what they became is to render them.
    /// </para>
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
        CewkaApplication.Settings.Current.PanelOpen = shot.PanelOpen;
        CewkaApplication.Settings.Current.QueueOpen = shot.QueueOpen;
        CewkaApplication.Settings.Current.PlaceholderPalette = shot.Palette;
        CewkaApplication.Settings.Current.WindowControls = shot.Controls;
        CewkaApplication.Settings.Current.Language = shot.Language;
        CewkaApplication.Settings.Current.ColourIntensity = shot.Intensity;
        Cewka.App.Localisation.Strings.Current.SetLanguage(shot.Language);

        // Rozmiar nigdy poniżej tego, co okno dopuszcza. Przypisanie Width wprost omija
        // ograniczenie, którego pilnuje ApplyPanelConstraints, i zrzut pokazywałby wtedy układ
        // nieosiągalny dla użytkownika — a wraz z nim usterki, których w programie nie ma.
        var window = new MainWindow();
        window.Width = Math.Max(shot.Width, window.MinWidth);
        window.Height = Math.Max(shot.Height, window.MinHeight);

        // Plik wskazany w wierszu polecen wczytuje samo okno, ale dopiero w OnOpened - czyli
        // przy pokazaniu, nie przy utworzeniu. Dlatego przygotowanie utworu do zdjecia idzie
        // wywolaniem zwrotnym z wnetrza Render, po Show.
        // Modelu widoku nie zwalniamy tutaj: robi to MainWindow.OnClosed, a drugie zwolnienie
        // konczy sie wyjatkiem.
        Render(window, shot, outputDirectory,
            afterShow: () => PrepareTrack(window),
            beforeCapture: () =>
            {
                MeasureQueueOverlap(window);

                if (!shot.PanelOpen) return;

                var offset = Math.Abs(MeasureHeadingOffset(window));
                if (offset > _worstHeadingOffset) _worstHeadingOffset = offset;
            });
    }

    /// <summary>Largest heading misalignment seen in this run, in device-independent pixels.</summary>
    private static double _worstHeadingOffset;

    /// <summary>
    /// Najglebsze wejscie bloku odtwarzania na kolumne kolejki, w pikselach.
    ///
    /// <para>Rzad transportu ma naturalna szerokosc okolo 440 px i nic go nie sciska. Przy oknie
    /// wezszym niz suma plyty, odstepu i tego rzedu zawartosc wychodzila poza swoj obszar
    /// i rysowala sie na kolumnie kolejki — pasek glosnosci lezal na liscie utworow. Golym okiem
    /// widac to tylko przy niektorych szerokosciach, wiec mierzone jest przy kazdym zrzucie.</para>
    /// </summary>
    /// <remarks>
    /// Zaczyna od minus nieskonczonosci, zeby zapas nad krawedzia dal sie odczytac tak samo jak
    /// nachodzenie: wartosc ujemna znaczy „tyle pikseli wolnego", dodatnia „tyle za krawedzia".
    /// </remarks>
    private static double _worstQueueOverlap = double.NegativeInfinity;

    /// <summary>Nazwy elementow, ktore nie maja prawa siegnac kolumny kolejki.</summary>
    private static readonly string[] MustNotReachQueue = ["VolumeSlider", "TrackInfo", "Seek"];

    private static void MeasureQueueOverlap(MainWindow window)
    {
        var queue = window.FindControl<Border>("QueueColumn");
        if (queue is null || !queue.IsVisible) return;

        var queueLeft = queue.TranslatePoint(new Point(0, 0), window)?.X;
        if (queueLeft is null) return;

        foreach (var name in MustNotReachQueue)
        {
            var control = window.FindControl<Control>(name);
            if (control is null || !control.IsVisible || control.Bounds.Width <= 0) continue;

            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)?.X;
            if (right is null) continue;

            var overlap = right.Value - queueLeft.Value;
            if (overlap > _worstQueueOverlap) _worstQueueOverlap = overlap;

            if (overlap > 0)
            {
                Console.WriteLine(
                    $"       nachodzi: {name} konczy sie {overlap:F1} px za krawedzia kolejki");
            }
        }

        if (_worstQueueOverlap > 0)
        {
            // Rozbicie szerokosci na skladniki: bez tego widac tylko, ze nie miesci sie,
            // a nie wiadomo, co zabiera miejsce.
            var centre = window.FindControl<StackPanel>("CentreArea");
            var playing = window.FindControl<Grid>("NowPlaying");
            var disc = window.FindControl<Panel>("DiscHost");
            var volume = window.FindControl<Control>("VolumeSlider");

            Console.WriteLine(
                $"       szerokosci: okno {window.Bounds.Width:F0}  kolejka od {queueLeft:F0}" +
                $"  srodek {centre?.Bounds.Width ?? -1:F0}  blok {playing?.Bounds.Width ?? -1:F0}" +
                $"  plyta {disc?.Bounds.Width ?? -1:F0}  glosnosc {volume?.Bounds.Width ?? -1:F0}");
        }
    }

    /// <summary>
    /// Half a pixel: anything below that is rounding in the layout pass, not a mistake in it.
    /// </summary>
    private const double HeadingTolerance = 0.5;

    private static void Render(Window window, Shot shot, string outputDirectory,
        Action? afterShow = null, Action? beforeCapture = null)
    {
        window.Show();

        // Okno wczytuje zawartosc w OnOpened, wiec wszystko, co dotyczy tej zawartosci, moze
        // wydarzyc sie dopiero tutaj.
        Dispatcher.UIThread.RunJobs();
        afterShow?.Invoke();

        // Let layout, bindings and the first render pass settle before capturing.
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
