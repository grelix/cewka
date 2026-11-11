using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wybór ekranu sceny, przeniesienie okna na ten ekran i reakcja na odłączenie ekranu.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneScreenTests
{
    private static ScreenInfo Screen(string name, PixelRect bounds, PixelRect work, bool primary) =>
        new(SceneScreens.KeyOf(name, bounds), name, bounds, work, 1.0, primary);

    private static readonly ScreenInfo Desk =
        Screen("Monitor", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), primary: true);

    private static readonly ScreenInfo Tv =
        Screen("Telewizor", new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 0, 3840, 2120), primary: false);

    private static readonly ScreenInfo Side =
        Screen("Boczny", new PixelRect(-1280, 0, 1280, 1024), new PixelRect(-1280, 0, 1280, 1024), primary: false);

    private sealed class Rig : IDisposable
    {
        public required MainWindow Window { get; init; }

        public required PlayerHarness Harness { get; init; }

        public required List<ScreenInfo> Screens { get; init; }

        /// <summary>Ekran, na którym według testu stoi okno.</summary>
        public ScreenInfo Here { get; set; }

        public SceneView Scene => Window.Scene;

        public MainViewModel Player => Harness.Player;

        public void Dispose() => Window.Close();
    }

    private static Rig Open(string? savedScreen = null, params ScreenInfo[] screens)
    {
        App.Settings.Current.Window = null;
        var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.SceneScreen = savedScreen;
        });
        var window = new MainWindow(false, h.Player) { MinWidth = 0, MinHeight = 0, Width = 640, Height = 360 };
        var rig = new Rig { Window = window, Harness = h, Screens = [.. screens.Length == 0 ? [Desk, Tv] : screens], Here = Desk };
        window.ScreenSource = () => rig.Screens;
        window.WindowScreenSource = () => rig.Here;
        window.Show();
        window.Position = new PixelPoint(100, 80);
        Dispatcher.UIThread.RunJobs();

        h.OpenPaths(Enumerable.Range(0, 2).Select(i => h.Track("e" + i)));
        Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero));
        return rig;
    }

    // ---------- Funkcje czyste ----------

    [Fact]
    public void WyborEkranuToZapisanyGdyPodlaczonyInaczejEkranOkna()
    {
        var screens = new[] { Desk, Tv };

        Assert.Equal(Tv, SceneScreens.Pick(screens, Tv.Key, Desk));
        Assert.Equal(Desk, SceneScreens.Pick(screens, "Nieznany|1x1", Desk));
        Assert.Equal(Desk, SceneScreens.Pick(screens, null, Desk));
        Assert.Equal(Tv, SceneScreens.Pick(screens, null, Tv));
        Assert.Null(SceneScreens.Pick(screens, null, null));
        Assert.Null(SceneScreens.Pick([], "x|1x1", null));
    }

    [Fact]
    public void EkranPoOdlaczeniuToGlownyAlboPierwszy()
    {
        Assert.Equal(Desk, SceneScreens.Home([Tv, Desk]));
        Assert.Equal(Tv, SceneScreens.Home([Tv, Side]));
        Assert.Null(SceneScreens.Home([]));
    }

    [Fact]
    public void OknoSrodkujeSieWObszarzeRoboczym()
    {
        Assert.Equal(new PixelPoint(640, 340), SceneScreens.Centre(Desk.WorkingArea, new PixelSize(640, 360)));
        Assert.Equal(new PixelPoint(1920 + 1600, 880), SceneScreens.Centre(Tv.WorkingArea, new PixelSize(640, 360)));

        // Okno większe od obszaru staje przy jego lewym górnym rogu.
        Assert.Equal(new PixelPoint(0, 0), SceneScreens.Centre(Desk.WorkingArea, new PixelSize(4000, 3000)));
    }

    [Fact]
    public void OpisEkranuPochodziZKlucza()
    {
        Assert.Equal("Telewizor (3840×2160)", SceneScreens.Describe(Tv.Key));
        Assert.Equal("1920×1080", SceneScreens.Describe(SceneScreens.KeyOf(null, new PixelRect(0, 0, 1920, 1080))));
        Assert.Equal("1920×1080", Desk.Resolution);
    }

    [Fact]
    public void JednakoweMonitoryDostajaNumerWedlugPolozenia()
    {
        var right = Screen("DELL", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1400), false);
        var left = Screen("DELL", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), true);

        var screens = SceneScreens.Distinguish([right, Tv, left]);

        // Pierwszy od lewej zachowuje klucz bez numeru: wybór zapisany przy jednym takim monitorze dalej działa.
        Assert.Equal(left.Key, screens[2].Key);
        Assert.Equal("DELL 1", screens[2].Name);
        Assert.Equal(left.Key + "#2", screens[0].Key);
        Assert.Equal("DELL 2", screens[0].Name);
        Assert.Equal(Tv, screens[1]);
        Assert.Equal("DELL 2 (2560×1440)", SceneScreens.Describe(screens[0].Key));
        Assert.Equal(screens[0], SceneScreens.At(screens, right.Bounds));
        Assert.Null(SceneScreens.At(screens, new PixelRect(0, 0, 1, 1)));
    }

    [Fact]
    public void JednakoweMonitorySaZamienneGdyZostalJeden()
    {
        var left = Screen("DELL", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), true);
        var right = Screen("DELL", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1400), false);
        var twins = SceneScreens.Distinguish([left, right]);
        var second = twins[1].Key;

        Assert.Equal(left.Key, SceneScreens.Model(second));
        Assert.Equal(left.Key, SceneScreens.Model(left.Key));

        // Po odłączeniu jednego z bliźniaków numer znika; wybór „DELL 2” trafia na pozostały, a nie na ekran okna.
        var alone = SceneScreens.Distinguish([right]);
        Assert.Equal(alone[0], SceneScreens.Pick(alone, second, Desk));
        Assert.Equal(Desk, SceneScreens.Pick(alone, "Inny|2560x1440#2", Desk));

        // Margines bliźniaka bez własnego wpisu pochodzi z wpisu modelu.
        var margins = new Dictionary<string, SceneMargin> { [left.Key] = SceneMargin.Large };
        Assert.Equal(SceneMargin.Large, ScreenMargin.For(margins, second));
        margins[second] = SceneMargin.None;
        Assert.Equal(SceneMargin.None, ScreenMargin.For(margins, second));
    }

    [Fact]
    public void OdlaczenieJednegoZJednakowychMonitorowNieZamykaSceny() => Ui.Run(() =>
    {
        var left = Screen("DELL", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), true);
        var right = Screen("DELL", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1400), false);
        var twins = SceneScreens.Distinguish([left, right]);
        using var rig = Open(twins[1].Key, twins);
        rig.Here = twins[0];
        rig.Window.EnterScene();
        Assert.Equal(twins[1].Key, rig.Scene.ScreenKey);

        // Zostaje prawy, już bez numeru: scena trwa, bez komunikatu o odłączeniu.
        rig.Screens.Clear();
        rig.Screens.AddRange(SceneScreens.Distinguish([right]));
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.True(rig.Scene.IsOpen);
        Assert.False(rig.Player.HasNotice);

        // Scena przejmuje nowy klucz i granice, więc margines ustawiony teraz trafi tam, gdzie go odczyta ustawienie.
        Assert.Equal(rig.Screens[0].Key, rig.Scene.ScreenKey);
        Assert.Equal(rig.Screens[0].Bounds, rig.Scene.ScreenBounds);

        // Odłączenie i tego monitora zamyka scenę jak zwykle.
        rig.Screens.Clear();
        rig.Screens.Add(Desk);
        rig.Window.HandleScreensChanged(rig.Screens);
        Assert.False(rig.Scene.IsOpen);
    });

    [Fact]
    public void ScenaPrzechodziNaPozostalyJednakowyMonitorGdySystemPrzeniosloOknoGdzieIndziej() => Ui.Run(() =>
    {
        var left = Screen("DELL", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), false);
        var right = Screen("DELL", new PixelRect(4480, 0, 2560, 1440), new PixelRect(4480, 0, 2560, 1400), false);
        var screens = SceneScreens.Distinguish([Desk, left, right]);
        using var rig = Open(screens[2].Key, screens);
        rig.Window.EnterScene();
        Assert.Equal(right.Bounds.Position, rig.Window.Position);

        // Odłączono prawy; system przeniósł okno pełnoekranowe na ekran główny, a lewy stracił numer.
        rig.Screens.Clear();
        rig.Screens.AddRange(SceneScreens.Distinguish([Desk, left]));
        rig.Here = Desk;
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(WindowState.FullScreen, rig.Window.WindowState);
        Assert.Equal(left.Bounds.Position, rig.Window.Position);
        Assert.Equal(left.Key, rig.Scene.ScreenKey);
        Assert.False(rig.Player.HasNotice);
    });

    [Fact]
    public void PrzyTrzechJednakowychScenaZostajeNaSwoimMonitorzeMimoPrzesunieciaNumerow() => Ui.Run(() =>
    {
        var a = Screen("DELL", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), true);
        var b = Screen("DELL", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1400), false);
        var c = Screen("DELL", new PixelRect(5120, 0, 2560, 1440), new PixelRect(5120, 0, 2560, 1400), false);
        var screens = SceneScreens.Distinguish([a, b, c]);
        using var rig = Open(screens[1].Key, screens);
        rig.Window.EnterScene();
        var position = rig.Window.Position;

        // Odłączono pierwszy: monitor sceny traci numer, a dawny „#3” dostaje jego „#2”.
        rig.Screens.Clear();
        rig.Screens.AddRange(SceneScreens.Distinguish([b, c]));
        rig.Here = rig.Screens[0];
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(b.Key, rig.Scene.ScreenKey);
        Assert.Equal(b.Bounds, rig.Scene.ScreenBounds);
        Assert.Equal(position, rig.Window.Position);
        Assert.False(rig.Player.HasNotice);

        // Margines w ustawieniach celuje w ekran sceny, a nie w zapisany „#2”, który wskazuje już trzeci monitor.
        Assert.Equal(b.Key, rig.Window.SceneHost().ScreenKey());
    });

    [Fact]
    public void PrzestawienieEkranuScenyPrzenosiJegoGranice() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        rig.Window.EnterScene();
        var moved = Tv with { Bounds = new PixelRect(-3840, 0, 3840, 2160), WorkingArea = new PixelRect(-3840, 0, 3840, 2120) };

        rig.Screens.Remove(Tv);
        rig.Screens.Add(moved);
        rig.Here = moved;
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(Tv.Key, rig.Scene.ScreenKey);
        Assert.Equal(moved.Bounds, rig.Scene.ScreenBounds);
    });

    [Fact]
    public void JednakoweMonitoryBezNazwyRozniaSieKluczem()
    {
        var a = Screen(string.Empty, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true);
        var b = Screen(string.Empty, new PixelRect(0, 1080, 1920, 1080), new PixelRect(0, 1080, 1920, 1040), false);

        var screens = SceneScreens.Distinguish([b, a]);

        Assert.Equal(["|1920x1080#2", "|1920x1080"], screens.Select(s => s.Key).ToArray());
        Assert.All(screens, s => Assert.Equal(string.Empty, s.Name));
    }

    // ---------- Wejście do sceny ----------

    [Fact]
    public void ScenaOtwieraSieNaWybranymEkranieIWracaNaMiejsceOkna() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        var before = rig.Window.Position;
        PixelPoint? atFullScreen = null;
        rig.Window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && e.GetNewValue<WindowState>() == WindowState.FullScreen)
                atFullScreen = rig.Window.Position;
        };

        rig.Window.EnterScene();

        // Okno stoi już na ekranie sceny, kiedy zaczyna się pełny ekran.
        Assert.Equal(Tv.Bounds.Position, atFullScreen);
        Assert.Equal(Tv.Bounds.Position, rig.Window.Position);
        Assert.Equal(WindowState.FullScreen, rig.Window.WindowState);
        Assert.Equal(Tv.Key, rig.Scene.ScreenKey);
        Assert.Equal(Tv.Bounds, rig.Scene.ScreenBounds);

        rig.Window.ExitScene();

        Assert.Equal(before, rig.Window.Position);
        Assert.Equal(WindowState.Normal, rig.Window.WindowState);
    });

    [Fact]
    public void ZeStanuZmaksymalizowanegoNajpierwZwyklyPotemPelnyEkranAPoWyjsciuZNowuMaksymalizacja() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        rig.Window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        var states = new List<WindowState>();
        rig.Window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) states.Add(e.GetNewValue<WindowState>());
        };

        rig.Window.EnterScene();
        Assert.Equal([WindowState.Normal, WindowState.FullScreen], states);
        Assert.Equal(Tv.Bounds.Position, rig.Window.Position);

        states.Clear();
        rig.Window.ExitScene();
        Assert.Equal([WindowState.Normal, WindowState.Maximized], states);
        Assert.Equal(WindowState.Maximized, rig.Window.WindowState);
    });

    [Fact]
    public void BezWyboruScenaZostajeNaEkranieOkna() => Ui.Run(() =>
    {
        using var rig = Open();
        var before = rig.Window.Position;

        rig.Window.EnterScene();

        Assert.Equal(before, rig.Window.Position);
        Assert.Equal(Desk.Key, rig.Scene.ScreenKey);
        rig.Window.ExitScene();
        Assert.Equal(before, rig.Window.Position);
    });

    [Fact]
    public void ZapisanyAleNiepodlaczonyEkranZastepujeEkranOkna() => Ui.Run(() =>
    {
        using var rig = Open("Zniknal|1920x1080");
        var before = rig.Window.Position;

        rig.Window.EnterScene();

        Assert.Equal(before, rig.Window.Position);
        Assert.Equal(Desk.Key, rig.Scene.ScreenKey);
    });

    [Fact]
    public void PelnyEkranNadanyZZewnatrzNalezyDoEkranuOknaANieDoZapisanego() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);

        rig.Window.WindowState = WindowState.FullScreen;

        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(Desk.Key, rig.Scene.ScreenKey);
        Assert.Equal(Desk.Bounds, rig.Scene.ScreenBounds);

        // Odłączenie zapisanego ekranu nie dotyczy sceny; odłączenie ekranu okna już tak.
        rig.Window.HandleScreensChanged([Desk]);
        Assert.True(rig.Scene.IsOpen);
        rig.Window.HandleScreensChanged([Tv]);
        Assert.False(rig.Scene.IsOpen);
    });

    [Fact]
    public void MarginesNalezyDoEkranuSceny() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        rig.Player.SetSceneMargin(Tv.Key, SceneMargin.Large);

        rig.Window.EnterScene();

        Assert.Equal(SceneMargin.Large, rig.Player.GetSceneMargin(rig.Scene.ScreenKey));
        Assert.Equal(ScreenMargin.Default, rig.Player.GetSceneMargin(Desk.Key));
    });

    // ---------- Odłączenie ekranu ----------

    [Fact]
    public void OdlaczenieEkranuScenyZamykaSceneSprowadzaOknoNaEkranGlownyIPokazujeKomunikat() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        rig.Window.EnterScene();
        Assert.True(rig.Scene.IsOpen);

        rig.Screens.Remove(Tv);
        rig.Here = Desk;
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.False(rig.Scene.IsOpen);
        Assert.Equal(WindowState.Normal, rig.Window.WindowState);
        Assert.Equal(SceneScreens.Centre(Desk.WorkingArea, new PixelSize(640, 360)), rig.Window.Position);
        Assert.True(rig.Player.HasNotice);
        Assert.Equal(Strings.Current["SceneScreenLost"], rig.Player.Notice);
    });

    [Fact]
    public void PoOdlaczeniuOknoWracaDoStanuZmaksymalizowanego() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        rig.Window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        rig.Window.EnterScene();

        rig.Screens.Remove(Tv);
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.False(rig.Scene.IsOpen);
        Assert.Equal(WindowState.Maximized, rig.Window.WindowState);
    });

    [Fact]
    public void OdlaczenieInnegoEkranuNicNieRobi() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key, Desk, Tv, Side);
        rig.Window.EnterScene();
        var position = rig.Window.Position;

        rig.Screens.Remove(Side);
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.True(rig.Scene.IsOpen);
        Assert.Equal(WindowState.FullScreen, rig.Window.WindowState);
        Assert.Equal(position, rig.Window.Position);
        Assert.False(rig.Player.HasNotice);
    });

    [Fact]
    public void ZmianaEkranowPrzyZamknietejScenieNicNieRobi() => Ui.Run(() =>
    {
        using var rig = Open(Tv.Key);
        var position = rig.Window.Position;

        rig.Screens.Remove(Tv);
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.Equal(position, rig.Window.Position);
        Assert.False(rig.Player.HasNotice);
    });

    [Fact]
    public void OdlaczenieEkranuOknaZScenaNaTymSamymEkranieSprowadzaOknoNaPozostalyEkran() => Ui.Run(() =>
    {
        using var rig = Open(null, Desk, Tv);
        rig.Window.EnterScene();

        rig.Screens.Remove(Desk);
        rig.Window.HandleScreensChanged(rig.Screens);

        Assert.False(rig.Scene.IsOpen);
        Assert.Equal(SceneScreens.Centre(Tv.WorkingArea, new PixelSize(640, 360)), rig.Window.Position);
        Assert.True(rig.Player.HasNotice);
    });

    // ---------- Ustawienia ----------

    [Fact]
    public void ListaEkranowPokazujeNazweIRozdzielczoscIZapisujeWybor() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var host = new SceneSettingsHost
        {
            ScreenKey = () => h.Player.SceneScreen ?? Desk.Key,
            Screens = () => [Desk, Tv, Screen(string.Empty, new PixelRect(0, 0, 1280, 720), new PixelRect(0, 0, 1280, 720), false)],
        };
        using var settings = new SettingsViewModel(h.Settings, h.Player, host);

        Assert.Equal(
            [Strings.Current["SceneScreenWindow"], "Monitor (1920×1080)", "Telewizor (3840×2160)",
                $"{string.Format(Strings.Current["SceneScreenUnnamed"], 3)} (1280×720)"],
            settings.ScreenOptions.Select(option => option.Label).ToArray());
        Assert.True(settings.ScreenOptions[0].IsSelected);

        settings.ChooseScreen(settings.ScreenOptions[2]);

        Assert.Equal(Tv.Key, h.Player.SceneScreen);
        Assert.Single(settings.ScreenOptions, option => option.IsSelected);
        Assert.True(settings.ScreenOptions[2].IsSelected);

        settings.ChooseScreen(settings.ScreenOptions[0]);
        Assert.Null(h.Player.SceneScreen);
    });

    [Fact]
    public void ZapisanyNiepodlaczonyEkranZostajeNaLiscieZDopiskiem() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s => s.SceneScreen = "Dawny|1920x1080");
        using var settings = new SettingsViewModel(h.Settings, h.Player, new SceneSettingsHost { Screens = () => [Desk] });

        var missing = settings.ScreenOptions.Last();

        Assert.Equal($"Dawny (1920×1080) — {Strings.Current["DeviceMissing"]}", missing.Label);
        Assert.True(missing.IsSelected);
        Assert.Equal("Dawny|1920x1080", missing.Key);
    });
}
