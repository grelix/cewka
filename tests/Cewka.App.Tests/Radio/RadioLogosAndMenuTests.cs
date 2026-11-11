using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Streaming;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Logo stacji, baner prywatności, ustawienia radia, panel systemu, menu w pasku tytułu i drobne zachowania listy.</summary>
[Collection(UiCollection.Name)]
public sealed class RadioLogosAndMenuTests
{
    private const string LogoA = "https://logo.example.org/alfa.png";
    private const string LogoB = "https://logo.example.org/beta.png";

    private static RadioStation Station(string id, string name, string country, string? logo, string? website = null, string[]? languages = null)
    {
        var station = RadioTestData.Station(id, name, country, languages: languages);
        if (logo is not null) station.Logos = [logo];
        station.Website = website;
        return station;
    }

    private static RadioCatalog Catalog(IEnumerable<string>? withdrawn = null) => new(
    [
        Station("pl-a", "Alfa FM", "PL", LogoA, "https://alfa.example.org/"),
        Station("pl-b", "Beta FM", "PL", LogoB),
        Station("pl-c", "Gamma FM", "PL", null),
        Station("gb-a", "Delta FM", "GB", null),
    ], withdrawn);

    private sealed class LogoServer
    {
        public int Calls;
        public bool Down;

        public Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Down
                ? new ImageFetchResult(null, ImageFetchProblem.Unavailable)
                : new ImageFetchResult(TestLogos.Png(48, 48), ImageFetchProblem.None, 200));
        }
    }

    private static (PlayerHarness Harness, LogoServer Server, RadioLogoService Logos) Create(
        Action<AppSettings>? configure = null, Func<RadioCatalog>? catalog = null)
    {
        var server = new LogoServer();
        var logos = new RadioLogoService(TestEnvironment.NewDirectory(), server.Fetch);
        var h = new PlayerHarness(configure, radioCatalog: catalog ?? (() => Catalog()), radioLogos: logos);
        return (h, server, logos);
    }

    private static void Pump(Func<bool> done) =>
        Assert.True(Ui.UntilAsync(done, 5000).GetAwaiter().GetResult(), "Warunek nie został spełniony w czasie.");

    private static StationRowViewModel Row(PlayerHarness h, string id) =>
        h.Player.Radio.Rows.First(r => r.Station.Id == id);

    // ---------- Logo i baner prywatności ----------

    [Fact]
    public void PrzedZamknieciemBaneruLogoNieSaPobierane_ZamkniecieJePobiera() => Ui.RunAsync(async () =>
    {
        var (h, server, _) = Create();
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        var row = Row(h, "pl-a");

        row.RequestLogo();
        await Task.Delay(100);

        Assert.True(h.Player.Radio.ShowsPrivacyBanner);
        Assert.False(h.Player.Radio.LogosAllowed);
        Assert.Equal(0, server.Calls);
        Assert.Null(row.Logo);
        Assert.True(row.ShowsPlaceholder);

        h.Player.Radio.AcknowledgePrivacy();

        Assert.True(await Ui.UntilAsync(() => row.HasLogo));
        Assert.False(h.Player.Radio.ShowsPrivacyBanner);
        Assert.True(h.Settings.Current.RadioPrivacyAcknowledged);
        Assert.False(row.ShowsPlaceholder);
        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void UruchomienieStacjiZamykaBanerIPobieraLogoStacji() => Ui.RunAsync(async () =>
    {
        var (h, server, _) = Create();
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);

        Assert.True(h.Player.Radio.Play(Row(h, "pl-a").Station));

        Assert.False(h.Player.Radio.ShowsPrivacyBanner);
        Assert.True(await Ui.UntilAsync(() => h.Player.Radio.HasStageLogo));
        Assert.NotNull(h.Player.Radio.StageLogo);
        Assert.True(File.Exists(h.Player.Radio.StageLogoPath));
        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void StacjaBezLogo_MaWspolnaIkoneRadia() => Ui.RunAsync(async () =>
    {
        var (h, server, _) = Create(s => s.RadioPrivacyAcknowledged = true);
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);

        h.Player.Radio.Play(Row(h, "pl-c").Station);
        Row(h, "pl-c").RequestLogo();
        await Task.Delay(100);

        Assert.False(h.Player.Radio.HasStageLogo);
        Assert.True(h.Player.Radio.ShowsStagePlaceholder);
        Assert.True(Row(h, "pl-c").ShowsPlaceholder);
        Assert.Equal(0, server.Calls);
    });

    [Fact]
    public void WylaczonePobieranieLogo_NieWychodziDoSieciIUsuwaPobrane() => Ui.RunAsync(async () =>
    {
        var (h, server, _) = Create(s => s.RadioPrivacyAcknowledged = true);
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        var row = Row(h, "pl-a");
        row.RequestLogo();
        Assert.True(await Ui.UntilAsync(() => row.HasLogo));

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.RadioLogosEnabled = false;

        Assert.False(h.Settings.Current.RadioLogos ? true : row.HasLogo);
        Assert.True(row.ShowsPlaceholder);

        var calls = server.Calls;
        var other = Row(h, "pl-b");
        other.RequestLogo();
        await Task.Delay(100);
        Assert.Equal(calls, server.Calls);
        Assert.True(other.ShowsPlaceholder);

        settings.RadioLogosEnabled = true;
        Assert.True(await Ui.UntilAsync(() => other.HasLogo));
    });

    [Fact]
    public void BrakSieciPrzyPobieraniuLogo_ZostawiaObrazZastepczyBezBledu() => Ui.RunAsync(async () =>
    {
        var (h, server, _) = Create(s => s.RadioPrivacyAcknowledged = true);
        using var _ = h;
        server.Down = true;
        h.Player.SetSource(SourceMode.Radio);

        var row = Row(h, "pl-a");
        row.RequestLogo();
        h.Player.Radio.Play(row.Station);
        await Task.Delay(200);

        Assert.True(row.ShowsPlaceholder);
        Assert.False(h.Player.Radio.HasError);
    });

    [Fact]
    public void WyczyscPamiecLogo_UsuwaPlikiAWidoczneLogoSaPobieraneOdNowa() => Ui.RunAsync(async () =>
    {
        var (h, server, logos) = Create(s => s.RadioPrivacyAcknowledged = true);
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        var row = Row(h, "pl-a");
        row.RequestLogo();
        Assert.True(await Ui.UntilAsync(() => row.HasLogo));
        Assert.True(logos.SizeBytes() > 0);
        var calls = server.Calls;

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.ClearRadioLogos();

        Assert.True(await Ui.UntilAsync(() => server.Calls == calls + 1 && row.HasLogo));
    });

    [Fact]
    public void WyczyscOstatnioSluchane_OproznaListeIZapisujeStan() => Ui.Run(() =>
    {
        var (h, _, _) = Create(s => s.RadioPrivacyAcknowledged = true);
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(Row(h, "pl-a").Station);
        Assert.True(h.Player.Radio.HasRecent);

        new SettingsViewModel(h.Settings, h.Player).ClearRadioRecent();

        Assert.False(h.Player.Radio.HasRecent);
        Assert.Empty(RadioStateStore.Load(h.RadioFile).Recent);
    });

    // ---------- Wyłączenie radia ----------

    [Fact]
    public void WylaczenieRadia_WracaDoPlikowZatrzymujeStacjeIBlokujePrzelacznik() => Ui.Run(() =>
    {
        var (h, _, _) = Create();
        using var _ = h;
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(Row(h, "pl-a").Station);
        Assert.True(h.Engine.IsLive);

        var settings = new SettingsViewModel(h.Settings, h.Player);
        settings.RadioEnabled = false;

        Assert.True(h.Player.IsFilesMode);
        Assert.False(h.Player.RadioAvailable);
        Assert.False(h.Settings.Current.RadioEnabled);
        Assert.False(h.Engine.IsLive);
        Assert.False(settings.RadioOptionsEnabled);

        h.Player.SetSource(SourceMode.Radio);
        Assert.True(h.Player.IsFilesMode);
        Assert.False(h.Player.Radio.LogosAllowed);

        settings.RadioEnabled = true;
        h.Player.SetSource(SourceMode.Radio);
        Assert.True(h.Player.IsRadioMode);
        Assert.Single(h.Sessions);
    });

    [Fact]
    public void ZapisanyTrybRadiaPrzyWylaczonymRadiu_StartujeWPlikach() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(s =>
        {
            s.Source = SourceMode.Radio;
            s.RadioEnabled = false;
        });

        Assert.True(h.Player.IsFilesMode);
    });

    // ---------- Stacja usunięta z listy ----------

    [Fact]
    public void UlubionaStacjaUsunietaNaZadanieNadawcy_ZostajeAleNieGraIPodajePowod() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(radioCatalog: () => Catalog(withdrawn: ["pl-b"]));
        h.Player.SetSource(SourceMode.Radio);
        var station = Row(h, "pl-b").Station;
        h.Player.Radio.ToggleFavourite(station);

        Assert.False(h.Player.Radio.Play(station));

        Assert.Empty(h.Sessions);
        Assert.True(h.Player.Radio.HasError);
        Assert.Equal(Strings.Current["RadioStationWithdrawn"], h.Player.Radio.ErrorText);
        Assert.False(h.Player.Radio.CanRetry);
        Assert.True(h.Player.Radio.State.IsFavourite("pl-b"));
    });

    [Fact]
    public void WycofaneStacjeSaCzytaneZPlikuListy()
    {
        var json = "{\"schema\":1,\"withdrawn\":[\"pl-x\"],\"stations\":[]}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var catalog = RadioCatalog.Load(stream);

        Assert.True(catalog.IsWithdrawn("pl-x"));
        Assert.False(catalog.IsWithdrawn("pl-y"));
    }

    // ---------- Wstecz i Dalej z opóźnieniem ----------

    private sealed class ManualScheduler
    {
        public readonly List<(TimeSpan Delay, Action Action, bool[] Cancelled)> Items = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var cancelled = new bool[1];
            Items.Add((delay, action, cancelled));
            return new Cancel(cancelled);
        }

        public void RunPending()
        {
            foreach (var (_, action, cancelled) in Items.ToList())
            {
                if (!cancelled[0])
                {
                    cancelled[0] = true;
                    action();
                }
            }
        }

        private sealed class Cancel(bool[] flag) : IDisposable
        {
            public void Dispose() => flag[0] = true;
        }
    }

    [Fact]
    public void WsteczIDalej_ZmieniajaStacjeNaEkranieAPolaczenieDopieroPoCzterechSetnychSekundy() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(radioCatalog: () => Catalog());
        var scheduler = new ManualScheduler();
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.StepDelay = RadioViewModel.DefaultStepDelay;
        h.Player.Radio.Scheduler = scheduler.Schedule;
        h.Player.Radio.Play(Row(h, "pl-a").Station);
        var opened = h.Sessions.Count;

        h.Player.Next();
        h.Player.Next();

        Assert.Equal(TimeSpan.FromMilliseconds(400), scheduler.Items[0].Delay);
        Assert.Equal("pl-c", h.Player.Radio.CurrentStation?.Id);
        Assert.True(h.Player.Radio.HasPendingStep);
        Assert.Equal(opened, h.Sessions.Count);

        scheduler.RunPending();

        Assert.False(h.Player.Radio.HasPendingStep);
        Assert.Equal(opened + 1, h.Sessions.Count);
        Assert.Equal("pl-c", h.Sessions[^1].Source.StationId);
    });

    [Fact]
    public void WyborStacjiWTrakcieOczekiwania_AnulujeOpozniony_Krok() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(radioCatalog: () => Catalog());
        var scheduler = new ManualScheduler();
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.SelectedCountry = h.Player.Radio.Countries.First(c => c.Key == "PL");
        h.Player.Radio.StepDelay = RadioViewModel.DefaultStepDelay;
        h.Player.Radio.Scheduler = scheduler.Schedule;
        h.Player.Radio.Play(Row(h, "pl-a").Station);

        h.Player.Previous();
        h.Player.Radio.Play(Row(h, "pl-b").Station);
        var opened = h.Sessions.Count;
        scheduler.RunPending();

        Assert.Equal(opened, h.Sessions.Count);
        Assert.Equal("pl-b", h.Sessions[^1].Source.StationId);
    });

    // ---------- Kraj startowy z regionu systemu ----------

    [Fact]
    public void KrajStartowyBierzeRegionSystemuWObrebieJezykaInterfejsu() => Ui.Run(() =>
    {
        var stations = new List<RadioStation>();
        foreach (var country in new[] { "PL", "GB" })
        {
            for (var i = 0; i < 3; i++)
                stations.Add(RadioTestData.Station($"{country.ToLowerInvariant()}-{i}", $"Stacja {country}{i}", country, languages: ["pl"]));
        }

        var previous = RadioViewModel.RegionProvider;
        try
        {
            RadioViewModel.RegionProvider = () => "GB";
            using (var h = new PlayerHarness(radioCatalog: () => new RadioCatalog(stations)))
            {
                h.Player.SetSource(SourceMode.Radio);
                Assert.Equal("GB", h.Player.Radio.SelectedCountry?.Key);
            }

            RadioViewModel.RegionProvider = () => null;
            using (var h = new PlayerHarness(radioCatalog: () => new RadioCatalog(stations)))
            {
                h.Player.SetSource(SourceMode.Radio);
                Assert.Equal("PL", h.Player.Radio.SelectedCountry?.Key);
            }
        }
        finally
        {
            RadioViewModel.RegionProvider = previous;
        }
    });

    // ---------- Strona stacji ----------

    [Fact]
    public void OtworzStroneStacji_PrzekazujeTylkoAdresHttpIHttps() => Ui.Run(() =>
    {
        using var h = new PlayerHarness(radioCatalog: () => Catalog());
        h.Player.SetSource(SourceMode.Radio);
        var opened = new List<string>();

        Assert.True(h.Player.Radio.OpenWebsite(Row(h, "pl-a").Station, opened.Add));
        Assert.False(h.Player.Radio.OpenWebsite(Row(h, "pl-b").Station, opened.Add));
        Assert.False(h.Player.Radio.OpenWebsite(new RadioStation { Website = "file:///c:/windows/system32/calc.exe" }, opened.Add));
        Assert.False(h.Player.Radio.OpenWebsite(new RadioStation { Website = "javascript:alert(1)" }, opened.Add));

        Assert.Equal(["https://alfa.example.org/"], opened);
    });

    // ---------- Panel multimediów systemu ----------

    private sealed class RecordingPanel : IMediaPanel
    {
        public (string Title, string? Artist, string? Album, TimeSpan Length)? Track;
        public bool Live;
        public string? Art;
        public bool Navigation = true;
        public MediaPanelStatus Status;
        public int Clears;

        public void SetTrack(string title, string? artist, string? album, TimeSpan length) => Track = (title, artist, album, length);

        public void SetStatus(MediaPanelStatus status) => Status = status;

        public void SetLive(bool live, string? artFile)
        {
            Live = live;
            Art = artFile;
        }

        public void SetNavigation(bool enabled) => Navigation = enabled;

        public void Clear() => Clears++;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void PanelSystemuWRadiu_PokazujeStacjeZKrajemTytulIPrzyciskiNawigacji() => Ui.RunAsync(async () =>
    {
        var (h, _, _) = Create();
        using var _ = h;
        var panel = new RecordingPanel();
        h.Player.AttachMediaPanel(panel);
        h.Player.SetSource(SourceMode.Radio);
        var station = Row(h, "pl-a").Station;

        h.Player.Radio.Play(station);

        Assert.True(panel.Live);
        Assert.True(panel.Navigation);
        Assert.Equal(h.Player.Radio.StationName, panel.Track!.Value.Title);
        Assert.Equal(h.Player.Radio.StationName, panel.Track.Value.Artist);
        Assert.Equal(h.Player.Radio.StationName, panel.Track.Value.Album);
        Assert.Equal(TimeSpan.Zero, panel.Track.Value.Length);
        Assert.Contains("(", panel.Track.Value.Title);

        h.Player.Radio.OnLiveTitle(h.Sessions[^1].Source, "Wykonawca - Utwór");
        Assert.Equal("Utwór", panel.Track.Value.Title);
        Assert.Equal("Wykonawca", panel.Track.Value.Artist);
        Assert.Equal(h.Player.Radio.StationName, panel.Track.Value.Album);

        Assert.True(await Ui.UntilAsync(() => panel.Art is not null));
        Assert.True(File.Exists(panel.Art));
    });

    [Fact]
    public void PowrotDoPlikow_PrzywracaPanelUtworuBezTrybuNaZywo() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var panel = new RecordingPanel();
        h.Player.AttachMediaPanel(panel);
        h.Player.SetSource(SourceMode.Radio);
        h.Player.Radio.Play(Row(h, "pl-a").Station);
        Assert.True(panel.Live);

        h.Player.SetSource(SourceMode.Files);

        Assert.False(panel.Live);
        Assert.Null(panel.Art);
    });

    [Fact]
    public void RadioBezStacji_CzysciPanel() => Ui.Run(() =>
    {
        using var h = new PlayerHarness();
        var panel = new RecordingPanel();
        h.Player.AttachMediaPanel(panel);
        var before = panel.Clears;

        h.Player.SetSource(SourceMode.Radio);

        Assert.True(panel.Clears > before);
        Assert.Equal(MediaPanelStatus.Closed, panel.Status);
    });

    // ---------- Menu w pasku tytułu ----------

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainWindow Window, PlayerHarness Harness) OpenWindow(Action<AppSettings>? configure = null)
    {
        App.Settings.Current.Window = null;
        var (h, _, _) = Create(s =>
        {
            s.QueuePinned = true;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Reduced;
            configure?.Invoke(s);
        });

        var window = new MainWindow(false, h.Player) { Width = 1300, Height = 760 };
        window.Show();
        Settle();
        return (window, h);
    }

    private static void ClickCentre(MainWindow window, Control control)
    {
        Settle();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Settle();
    }

    [Fact]
    public void MenuJestZwinieteAIkonyNieBioraFokusu() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow();
        using var _ = h;
        try
        {
            Assert.False(window.IsMenuOpen);
            Assert.Equal(0, window.FindControl<Control>("MenuTray")!.Width);
            Assert.False(window.FindControl<Control>("MenuTrayItems")!.IsEnabled);
            Assert.False(window.FindControl<ToggleButton>("QueueToggle")!.Focus(NavigationMethod.Tab));
            Assert.False(window.FindControl<Control>("MenuModeMark")!.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void KlikniecieWPrzyciskMenuRozsuwaIkonyAPonowneJeZwija() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow();
        using var _ = h;
        try
        {
            var button = window.FindControl<ToggleButton>("MenuButton")!;

            ClickCentre(window, button);

            Assert.True(window.IsMenuOpen);
            Assert.True(button.IsChecked);
            var tray = window.FindControl<Control>("MenuTray")!;
            Assert.True(tray.Width > 100);
            Assert.True(window.FindControl<Control>("MenuTrayItems")!.IsEnabled);
            Assert.NotNull(window.FindControl<Control>("SourceRadioButton"));
            Assert.True(window.FindControl<Control>("QueueToggle")!.Focusable);

            ClickCentre(window, button);

            Assert.False(window.IsMenuOpen);
            Assert.Equal(0, tray.Width);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MenuZwijaSiePoUzyciuIkonyPoEscIPoKlikniecuPozaNim() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow();
        using var _ = h;
        try
        {
            window.SetMenuOpen(true);
            var queue = window.FindControl<ToggleButton>("QueueToggle")!;
            var before = h.Player.QueuePinned;
            ClickCentre(window, queue);
            Assert.NotEqual(before, h.Player.QueuePinned);
            Assert.False(window.IsMenuOpen);

            window.SetMenuOpen(true);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Settle();
            Assert.False(window.IsMenuOpen);

            window.SetMenuOpen(true);
            ClickCentre(window, window.FindControl<Control>("TrackTitle")!);
            Assert.False(window.IsMenuOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WyborRadiaZMenuPrzelaczaZrodloIZwijaMenu_ZnakTrybuPokazujeRadio() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow();
        using var _ = h;
        try
        {
            window.SetMenuOpen(true);
            ClickCentre(window, window.FindControl<Button>("SourceRadioButton")!);

            Assert.True(h.Player.IsRadioMode);
            Assert.False(window.IsMenuOpen);
            Assert.True(window.FindControl<Control>("MenuModeMark")!.IsVisible);

            window.SetMenuOpen(true);
            Assert.False(window.FindControl<Control>("MenuModeMark")!.IsVisible);

            ClickCentre(window, window.FindControl<Button>("SourceFilesButton")!);
            Assert.True(h.Player.IsFilesMode);
            Assert.False(window.FindControl<Control>("MenuModeMark")!.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RozsuwanieMaAnimacjeIBezNiejWTrybieOgraniczonychEfektow() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow(s => s.Effects = EffectsMode.Full);
        using var _ = h;
        try
        {
            window.SetMenuOpen(true);
            var tray = window.FindControl<Border>("MenuTray")!;
            Assert.NotNull(tray.Transitions);
            Assert.Equal(2, tray.Transitions!.Count);
            window.SetMenuOpen(false);
        }
        finally { window.Close(); }

        var (reduced, h2) = OpenWindow(s => s.Effects = EffectsMode.Reduced);
        using var _2 = h2;
        try
        {
            Assert.True(h2.Player.ReducedEffects);
            reduced.SetMenuOpen(true);
            Assert.Null(reduced.FindControl<Border>("MenuTray")!.Transitions);
        }
        finally { reduced.Close(); }
    });

    [Fact]
    public void WylaczoneRadio_UkrywaPrzelacznikZrodlaISkrotCtrlDwa() => Ui.Run(() =>
    {
        var (window, h) = OpenWindow(s => s.RadioEnabled = false);
        using var _ = h;
        try
        {
            Assert.False(window.FindControl<Control>("SourceSwitch")!.IsVisible);

            window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.None, null);
            window.KeyRelease(Key.D2, RawInputModifiers.Control, PhysicalKey.None, null);
            Settle();

            Assert.True(h.Player.IsFilesMode);
        }
        finally { window.Close(); }
    });

    // ---------- Okno główne: logo, baner, menu kontekstowe ----------

    [Fact]
    public void OknoWTrybieRadia_NieMaPlytyAleMaRamkeZLogoIBaner() => Ui.RunAsync(async () =>
    {
        var (window, h) = OpenWindow();
        using var _ = h;
        try
        {
            h.Player.SetSource(SourceMode.Radio);
            Settle();

            Assert.True(window.FindControl<Control>("RadioLogoFrame")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("Disc")!.IsVisible);
            Assert.True(window.FindControl<Control>("RadioPrivacyBanner")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Control>("RadioLogoPlaceholder")!.IsEffectivelyVisible);

            var dismiss = window.FindControl<Button>("RadioPrivacyDismiss")!;
            ClickCentre(window, dismiss);

            Assert.False(window.FindControl<Control>("RadioPrivacyBanner")!.IsVisible);
            Assert.True(h.Settings.Current.RadioPrivacyAcknowledged);

            h.Player.Radio.Play(h.Player.Radio.Rows.First(r => r.Station.Logos.Count > 0).Station);
            Assert.True(await Ui.UntilAsync(() => h.Player.Radio.HasStageLogo));
            Settle();
            Assert.True(window.FindControl<Control>("RadioLogoImage")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("RadioLogoPlaceholder")!.IsVisible);

            h.Player.SetSource(SourceMode.Files);
            Settle();
            Assert.True(window.FindControl<Control>("Disc")!.IsVisible);
            Assert.False(window.FindControl<Control>("RadioLogoFrame")!.IsVisible);
        }
        finally { window.Close(); }
    });

    // ---------- Ustawienia ----------

    [Fact]
    public void SekcjaRadiaWUstawieniach_MaPrzelacznikiIPrzyciski() => Ui.Run(() =>
    {
        var (h, _, _) = Create();
        using var _ = h;
        var settings = new SettingsViewModel(h.Settings, h.Player);
        var window = new SettingsWindow(settings);
        window.Show();
        try
        {
            settings.SelectSection(settings.RadioSection);
            Settle();

            // Radio jest kategorią główną z dwiema podkategoriami: stacje oraz logo i prywatność.
            Assert.Contains(settings.Categories, c => c.Key == "SectionRadio" && c.Sections.Contains(settings.RadioSection)
                && c.Sections.Contains(settings.RadioLogos));
            Assert.True(window.FindControl<Control>("RadioSettings")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Control>("RadioEnabledSwitch")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Control>("RadioClearRecentButton")!.IsEffectivelyVisible);

            settings.SelectSection(settings.RadioLogos);
            Settle();
            Assert.True(window.FindControl<Control>("RadioLogoSettings")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Control>("RadioLogosSwitch")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Control>("RadioClearLogosButton")!.IsEffectivelyVisible);
            Assert.Equal(Strings.Current["RadioLogoCacheSize"].Replace("{0}", "0 B"), settings.RadioLogoCacheText);
        }
        finally { window.Close(); }
    });
}
