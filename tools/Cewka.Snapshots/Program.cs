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
        string Section = "Appearance");

    private static readonly Shot[] Shots =
    [
        new("ciemny-1180", 1180, 680, ThemeVariant.Dark, true),
        new("jasny-1180", 1180, 680, ThemeVariant.Light, true),
        new("ciemny-zwiniety", 1180, 448, ThemeVariant.Dark, false),
        new("jasny-zwiniety", 1180, 448, ThemeVariant.Light, false),
        new("ciemny-waski", 920, 660, ThemeVariant.Dark, true),
        new("ciemny-szeroki", 1600, 820, ThemeVariant.Dark, true),
        new("ciemny-pelnyekran", 2560, 1400, ThemeVariant.Dark, true),
        new("przyciski-lewo", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.Left),
        new("przyciski-macos", 1180, 680, ThemeVariant.Dark, false, WindowControlsPosition.MacOs),

        // Najciasniejsze przypadki: dokladnie wysokosc minimalna dla obu stanow panelu, w tym
        // przy najwezszym dopuszczalnym oknie. Tu wychodzi kazde nachodzenie elementow na siebie.
        new("minimum-panel", 1180, 620, ThemeVariant.Dark, true),
        new("minimum-panel-waskie", 900, 620, ThemeVariant.Dark, true),
        new("minimum-zwiniety", 900, 360, ThemeVariant.Dark, false),

        // Interfejs po angielsku: sprawdzenie, ze przelaczenie jezyka faktycznie dociera
        // do wszystkich napisow, a nie tylko do tych zlozonych na nowo.
        new("angielski", 1180, 680, ThemeVariant.Dark, true, Language: "en"),
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
        new("tlo-ciemne", 1180, 680, ThemeVariant.Dark, true),
        new("tlo-jasne", 1180, 680, ThemeVariant.Light, true),
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
        new("ustawienia-informacje", 820, 640, ThemeVariant.Dark, true, Section: "About"),
        new("ustawienia-jasny", 820, 640, ThemeVariant.Light, true),

        // Okno w rozmiarze najmniejszym dopuszczalnym, na zakladce o najbogatszej zawartosci.
        new("ustawienia-minimum", 700, 520, ThemeVariant.Dark, true, Section: "Audio"),

        // Jezyki dolozone: etykiety paskow segmentowych i nazwy zakladek sa tu najdluzsze,
        // a niemiecki jest wsrod nich przypadkiem najtrudniejszym.
        new("ustawienia-niemiecki", 820, 640, ThemeVariant.Dark, true, Language: "de"),
        new("ustawienia-niemiecki-dzwiek", 820, 640, ThemeVariant.Dark, true, Language: "de", Section: "Audio"),
        new("ustawienia-hiszpanski", 820, 640, ThemeVariant.Dark, true, Language: "es", Section: "Audio"),
        new("ustawienia-francuski", 820, 640, ThemeVariant.Dark, true, Language: "fr", Section: "Audio"),
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "snapshots");

        Directory.CreateDirectory(outputDirectory);

        var track = args.Length > 1 ? args[1] : null;
        if (track is not null && !File.Exists(track))
        {
            Console.Error.WriteLine($"Nie ma pliku: {track}");
            return 1;
        }

        AppBuilder.Configure<CewkaApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // No desktop lifetime runs here, so the shared services must be created by hand.
        // A throwaway settings file keeps my own configuration untouched.
        var scratchSettings = Path.Combine(Path.GetTempPath(), "cewka-snapshots-settings.json");
        if (File.Exists(scratchSettings)) File.Delete(scratchSettings);
        ((CewkaApplication)Application.Current!).InitialiseServicesForHeadless(
            new SettingsStore(scratchSettings), track is null ? null : [track]);

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
        return 0;
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
    private static void PrepareTrack(Window window)
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

        Console.WriteLine(player.AudioFailure.Length > 0
            ? $"       uwaga: {player.AudioFailure}"
            : $"       {player.Title} — {player.Elapsed} / {player.Total}");
    }

    /// <summary>Lets the refresh timer pick the new position up before the frame is captured.</summary>
    private static void SettleAfterSeek(MainViewModel player)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
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
        "Audio" => viewModel.Audio,
        "Playback" => viewModel.Playback,
        "Integration" => viewModel.Integration,
        "About" => viewModel.About,
        _ => viewModel.Appearance,
    };

    /// <summary>
    /// Renders the moving colour field on its own, under the same scrim the player puts over it.
    /// </summary>
    private static void CaptureBackdrop(Shot shot, string outputDirectory)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;

        var window = new Window
        {
            Width = shot.Width,
            Height = shot.Height,
            WindowDecorations = WindowDecorations.None,
            Content = new Panel
            {
                Children =
                {
                    new LiveBackdrop
                    {
                        Palette = shot.Variant == ThemeVariant.Dark ? DarkPalette : LightPalette,
                        BaseBrush = Brush(shot.Variant == ThemeVariant.Dark ? "#FF111113" : "#FFE9E9EC"),
                    },
                    new Border
                    {
                        Background = Brush(shot.Variant == ThemeVariant.Dark ? "#73101013" : "#2EFAFAFC"),
                    },
                },
            },
        };

        Render(window, shot, outputDirectory);
    }

    private static IBrush Brush(string colour) => new SolidColorBrush(Color.Parse(colour));

    private static void Capture(Shot shot, string outputDirectory)
    {
        Application.Current!.RequestedThemeVariant = shot.Variant;
        CewkaApplication.Settings.Current.PanelOpen = shot.PanelOpen;
        CewkaApplication.Settings.Current.WindowControls = shot.Controls;
        CewkaApplication.Settings.Current.Language = shot.Language;
        Cewka.App.Localisation.Strings.Current.SetLanguage(shot.Language);

        var window = new MainWindow
        {
            Width = shot.Width,
            Height = shot.Height,
        };

        // Plik wskazany w wierszu polecen wczytuje samo okno, ale dopiero w OnOpened - czyli
        // przy pokazaniu, nie przy utworzeniu. Dlatego przygotowanie utworu do zdjecia idzie
        // wywolaniem zwrotnym z wnetrza Render, po Show.
        // Modelu widoku nie zwalniamy tutaj: robi to MainWindow.OnClosed, a drugie zwolnienie
        // konczy sie wyjatkiem.
        Render(window, shot, outputDirectory, () => PrepareTrack(window));
    }

    private static void Render(Window window, Shot shot, string outputDirectory, Action? afterShow = null)
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
