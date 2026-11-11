using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Cewka.Audio.Streaming;
using SkiaSharp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zrzuty do README tylko na żądanie: katalog wyjściowy i katalog materiału w zmiennych środowiska.</summary>
public sealed class ReadmeShotsFactAttribute : FactAttribute
{
    public ReadmeShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReadmeShotsTests.OutputVariable))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReadmeShotsTests.MaterialVariable)))
            Skip = $"Brak katalogów zrzutów README ({ReadmeShotsTests.OutputVariable}, {ReadmeShotsTests.MaterialVariable}).";
    }
}

/// <summary>
/// Zrzuty do README w interfejsie polskim i angielskim, bez okna na ekranie. Materiał leży poza
/// repozytorium: <c>rhapsody-in-blue.wav</c> (ze znacznikami), <c>rhapsody-in-blue-1924.jpg</c> i katalog
/// <c>kolejka</c> z nagraniami z <see cref="QueueTracks"/>. Wizualizacje dostają dane z prawdziwego dekodera
/// i grafu dźwięku w tej samej chwili nagrania co pozostałe zrzuty.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ReadmeShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_README";
    public const string MaterialVariable = "CEWKA_README_MATERIAL";

    private const string Recording = "rhapsody-in-blue.wav";
    private const string Cover = "rhapsody-in-blue-1924.jpg";

    /// <summary>Chwila nagrania na zrzutach okna i sceny z płytą.</summary>
    private static readonly TimeSpan At = TimeSpan.FromSeconds(79);

    /// <summary>
    /// Nagranie na zrzutach wizualizacji. Plik z „Rhapsody in Blue” to próbka syntetyczna opisana znacznikami
    /// nagrania; okno pokazuje z niej tylko znaczniki, ale wizualizacje mają pokazać prawdziwy dźwięk.
    /// </summary>
    private const string VisualTrack = "semper-fidelis-1909.ogg";

    /// <summary>Chwila na zrzutach wizualizacji: najgłośniejszy z sondowanych fragmentów nagrania.</summary>
    private static readonly TimeSpan VisualAt = TimeSpan.FromSeconds(62);

    /// <summary>Nagrania kolejki z domeny publicznej (Wikimedia Commons) i znaczniki, których pliki nie mają.</summary>
    private static readonly (string File, string Title, string Artist)[] QueueTracks =
    [
        ("charleston-1925.ogg", "The Charleston", "Golden Gate Orchestra"),
        ("semper-fidelis-1909.ogg", "Semper Fidelis", "U.S. Marine Band"),
        ("maple-leaf-rag-1916.ogg", "Maple Leaf Rag", "Scott Joplin"),
        ("gymnopedie-1.ogg", "Gymnopédie No. 1", "Robin Alciatore"),
    ];

    /// <summary>Krzywa korektora na zrzucie okna „Korektor i efekty”: lekko podniesiony bas i góra.</summary>
    private static readonly double[] Curve = [3, 2.5, 1.5, 0.5, 0, -0.5, 0, 1, 2, 2.5];

    private static string Output => Environment.GetEnvironmentVariable(OutputVariable)!;

    private static string Material => Environment.GetEnvironmentVariable(MaterialVariable)!;

    private static void Save(TopLevel window, string path)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    private static string Name(string name, string language) => language == "pl" ? name : $"{name}-{language}";

    private static PlayerHarness Harness(string language, Action<AppSettings>? configure = null, bool radio = false)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(
            s =>
            {
                s.Language = language;
                s.WindowControls = WindowControlsPosition.Right;
                s.Effects = EffectsMode.Full;
                s.QueuePinned = false;
                s.RadioCountry = "PL";
                s.RadioLogos = false;
                s.RadioPrivacyAcknowledged = true;
                configure?.Invoke(s);
            },
            radioCatalog: radio ? RadioCatalog.LoadBuiltIn : null);

        // Prawdziwe dekodowanie zamiast dźwięku syntetycznego uprzęży: widmo i fala są z nagrania.
        h.Engine.OpenDecoder = DecoderFactory.Open;
        return h;
    }

    /// <summary>Kopie nagrań kolejki ze znacznikami; materiał źródłowy zostaje nietknięty.</summary>
    private static List<string> TaggedQueue()
    {
        var directory = TestEnvironment.NewDirectory();
        var paths = new List<string>();
        foreach (var (file, title, artist) in QueueTracks)
        {
            var path = Path.Combine(directory, file);
            File.Copy(Path.Combine(Material, "kolejka", file), path);
            var track = new ATL.Track(path) { Title = title, Artist = artist };
            if (!track.Save()) throw new IOException($"Nie zapisano znaczników: {path}");
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>Kolejka z nagraniem i czterema utworami, pozycja <see cref="At"/>, okładka i znaczniki wczytane.</summary>
    private static void Load(PlayerHarness h, IReadOnlyList<string> queue, bool pause)
    {
        h.OpenPaths([Path.Combine(Material, Recording), .. queue]);
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero, 20000), "nagranie nie zaczęło grać");

        h.Player.SeekToPosition(At);
        Assert.True(h.PumpUntil(() => h.Engine.Position >= At, 20000), "przewinięcie nie doszło do celu");
        if (pause) h.Player.Pause();

        Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted), "okładka się nie wczytała");
        h.Player.ShowCover(MainViewModel.DecodeCover(File.ReadAllBytes(Path.Combine(Material, Cover))));
        Assert.True(h.PumpUntil(() => h.Player.Queue.All(e => e.DurationSeconds > 0), 20000), "znaczniki kolejki się nie wczytały");
    }

    private static void InTheme(ThemeMode mode, Action shot)
    {
        var before = App.Theme.Mode;
        try
        {
            App.Theme.SetMode(mode);
            Dispatcher.UIThread.RunJobs();
            shot();
        }
        finally
        {
            App.Theme.SetMode(before);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void MainWindowShot(string language, IReadOnlyList<string> queue, string name, ThemeMode theme, int width, int height, bool pinned) =>
        InTheme(theme, () =>
        {
            using var h = Harness(language, s => s.QueuePinned = pinned);
            var window = new MainWindow(false, h.Player) { Width = width, Height = height };
            window.Show();
            try
            {
                Load(h, queue, pause: true);
                window.FreezeAnimationsForCapture();
                Save(window, Path.Combine(Output, Name(name, language) + ".png"));
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Scena na ekranie 1920 x 1080 bez warstwy sterowania.</summary>
    private static void SceneShot(string language, IReadOnlyList<string> queue, string path, SceneVisual visual, Action<AppSettings>? configure = null) =>
        InTheme(ThemeMode.Dark, () =>
        {
            using var h = Harness(language, s =>
            {
                s.SceneVisual = visual;
                s.SceneGlow = true;
                configure?.Invoke(s);
            });

            if (visual == SceneVisual.Aurora)
            {
                // Headless nie ma GPU ani baterii, więc stan wymagany przez Zorzę ustawia się jawnie.
                h.Player.BatteryProbe = () => false;
                h.Player.PinGpu(true);
            }

            var window = new MainWindow(false, h.Player) { Width = 1920, Height = 1080 };
            window.Show();
            try
            {
                if (visual == SceneVisual.Disc)
                {
                    // Płyta stoi w pauzie, jak na zrzutach okna.
                    Load(h, queue, pause: true);
                    window.FreezeAnimationsForCapture();
                    window.OpenSceneForCapture(showControls: false);
                }
                else
                {
                    // Wizualizacje dźwięku: prawdziwe nagranie z wyrównaniem głośności, jak w programie
                    // przy domyślnych ustawieniach. Bez pomiaru stare, ciche nagranie dałoby ledwo widoczne słupki.
                    var measured = new TaskCompletionSource();
                    h.Loudness.Measured += (_, _) => measured.TrySetResult();
                    h.Measurement = MeasureFile;

                    var track = queue.Single(p => Path.GetFileName(p) == VisualTrack);
                    h.OpenPaths([track]);
                    Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero, 20000), "nagranie nie zaczęło grać");
                    Assert.True(h.PumpUntil(() => measured.Task.IsCompleted, 120000), "pomiar głośności się nie skończył");

                    window.FreezeAnimationsForCapture();
                    window.OpenSceneForCapture(showControls: false);
                    if (visual == SceneVisual.Horizon)
                        Assert.True(h.PumpUntil(() => h.Player.SceneEnvelope is not null, 120000), "obwiednia nie została policzona");

                    // Analizator słucha dopiero przy otwartej scenie z wizualizacją dźwięku, więc dane
                    // z chwili VisualAt powstają przez odegranie ostatnich półtorej sekundy przed nią.
                    h.Player.SeekToPosition(VisualAt - TimeSpan.FromSeconds(1.5));
                    Assert.True(h.PumpUntil(() => h.Engine.Position >= VisualAt - TimeSpan.FromSeconds(2), 20000), "przewinięcie nie zostało przyjęte");
                    Assert.True(h.PumpUntil(() => h.Engine.Position >= VisualAt, 20000), "nagranie nie doszło do chwili zrzutu");
                }

                foreach (var view in new SceneAudioView[] { window.Scene.AudioView, window.Scene.RingView })
                    for (var i = 0; i < 90; i++) view.Step(1.0 / 60);

                if (visual == SceneVisual.Aurora)
                    for (var i = 0; i < 180; i++) window.Scene.AuroraView.Step(1.0 / 60);

                Save(window, path);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Pomiar głośności i obwiedni tym samym kodem co w programie.</summary>
    private static LoudnessMeasurement? MeasureFile(string path, CancellationToken token)
    {
        var method = typeof(LoudnessService).GetMethod("MeasureFile", BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(nameof(LoudnessService), "MeasureFile");
        return (LoudnessMeasurement?)method.Invoke(null, [path, token]);
    }

    /// <summary>Cztery wizualizacje w siatce 2 x 2, z odstępem w barwie tła sceny.</summary>
    private static void Collage(IReadOnlyList<string> tiles, string path)
    {
        const int tileWidth = 956, tileHeight = 538, gap = 8;
        var info = new SKImageInfo(tileWidth * 2 + gap, tileHeight * 2 + gap);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Black);

        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        for (var i = 0; i < tiles.Count; i++)
        {
            using var image = SKImage.FromEncodedData(tiles[i]);
            var x = (i % 2) * (tileWidth + gap);
            var y = (i / 2) * (tileHeight + gap);
            surface.Canvas.DrawImage(image, new SKRect(x, y, x + tileWidth, y + tileHeight), sampling);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void RadioShot(string language) => InTheme(ThemeMode.Dark, () =>
    {
        using var h = Harness(language, s => s.QueuePinned = true, radio: true);
        var window = new MainWindow(false, h.Player) { Width = 1200, Height = 680 };
        window.Show();
        try
        {
            h.Player.SetSource(SourceMode.Radio);
            window.FreezeAnimationsForCapture();

            var radio = h.Player.Radio;
            // Stacja publiczna nadająca w MP3 128 kbps, czyli z tym samym opisem jakości, jaki daje atrapa dekodera uprzęży.
            var station = radio.Rows.Select(r => r.Station).First(s => s.Id == "pl-radio-opole");
            Assert.Equal(("mp3", 128), (station.Streams[0].Codec, station.Streams[0].Kbps));
            radio.Play(station);
            var source = h.Sessions[0].Source;
            radio.OnLiveState(source, LiveState.Playing);
            h.Player.SyncPlayingState();
            Assert.True(h.PumpUntil(() =>
            {
                radio.RefreshBadge();
                return radio.Badge.Length > 0;
            }), "dekoder stacji nie podał kodeka");

            Save(window, Path.Combine(Output, Name("cewka-radio", language) + ".png"));
        }
        finally
        {
            window.Close();
        }
    });

    private static void SoundShot(string language) => InTheme(ThemeMode.Dark, () =>
    {
        using var h = Harness(language, s =>
        {
            s.EqualiserEnabled = true;
            s.EqualiserGains = [.. Curve];
        });

        var window = new SoundWindow(h.Player) { Width = 900, Height = 470 };
        window.Show();
        try
        {
            Save(window, Path.Combine(Output, Name("cewka-korektor", language) + ".png"));
        }
        finally
        {
            window.Close();
        }
    });

    private static void SettingsShot(string language) => InTheme(ThemeMode.Dark, () =>
    {
        using var h = Harness(language);
        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.SelectSection(settings.SceneVisuals);

        var window = new SettingsWindow(settings) { Width = 832, Height = 640 };
        window.Show();
        try
        {
            Save(window, Path.Combine(Output, Name("cewka-ustawienia", language) + ".png"));
        }
        finally
        {
            window.Close();
            settings.Dispose();
        }
    });

    private static void AllShots(string language)
    {
        Directory.CreateDirectory(Output);
        var queue = TaggedQueue();

        MainWindowShot(language, queue, "cewka-ciemny", ThemeMode.Dark, 1180, 520, pinned: false);
        MainWindowShot(language, queue, "cewka-jasny", ThemeMode.Light, 1180, 520, pinned: false);
        MainWindowShot(language, queue, "cewka-kolejka", ThemeMode.Dark, 1200, 680, pinned: true);

        SceneShot(language, queue, Path.Combine(Output, Name("cewka-scena", language) + ".png"), SceneVisual.Disc);

        var tiles = Path.Combine(TestEnvironment.NewDirectory(), "kafle");
        Directory.CreateDirectory(tiles);
        var visuals = new (string Name, SceneVisual Visual, Action<AppSettings>? Configure)[]
        {
            ("widmo", SceneVisual.Spectrum, s => s.SpectrumShape = SpectrumShape.Bars),
            // Nagranie jest mono, więc obraz stereo byłby pionową linią.
            ("fala", SceneVisual.Waveform, s => s.WaveformKind = WaveformKind.Line),
            ("horyzont", SceneVisual.Horizon, null),
            ("zorza", SceneVisual.Aurora, null),
        };

        foreach (var (name, visual, configure) in visuals)
            SceneShot(language, queue, Path.Combine(tiles, name + ".png"), visual, configure);

        Collage([.. visuals.Select(v => Path.Combine(tiles, v.Name + ".png"))],
            Path.Combine(Output, Name("cewka-wizualizacje", language) + ".png"));

        RadioShot(language);
        SoundShot(language);
        SettingsShot(language);
    }

    [ReadmeShotsFact]
    public void ZrzutyReadmePoPolsku() => SceneTests.InLanguage("pl", () => AllShots("pl"));

    [ReadmeShotsFact]
    public void ZrzutyReadmePoAngielsku() => SceneTests.InLanguage("en", () => AllShots("en"));
}
