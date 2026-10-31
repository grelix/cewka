using Cewka.Platform;
using Cewka.Platform.Linux;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Sprawdzenia warstwy systemowej, które nie wymagają okna ani urządzenia dźwiękowego.</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sprzątanie katalogu tymczasowego nie jest powodem, żeby sprawdzenie upadło.
            }
        }
    }

    // ---------- Ścieżki z wiersza poleceń ----------

    /// <summary>
    /// <c>cewka utwor.mp3</c> w katalogu z muzyką ma trafić do działającej kopii jako pełna
    /// ścieżka, bo tamta kopia ma inny katalog bieżący.
    /// </summary>
    [Fact]
    public void SciezkiWzgledneStajaSiePelne()
    {
        var root = Path.Combine(Path.GetTempPath(), "muzyka");

        var result = CommandLinePaths.ToAbsolute(["utwor.mp3", ".", Path.Combine("album", "a.flac")], root);

        Assert.Equal(
            [Path.Combine(root, "utwor.mp3"), root, Path.Combine(root, "album", "a.flac")],
            result);
    }

    [Fact]
    public void SciezkiPelneIAdresyZostajaBezZmian()
    {
        var full = Path.Combine(Path.GetTempPath(), "a.mp3");

        var result = CommandLinePaths.ToAbsolute([full, "file:///home/u/a.mp3", ""], "/inny/katalog");

        Assert.Equal([full, "file:///home/u/a.mp3", ""], result);
    }

    // ---------- Język systemu ----------

    /// <summary>Angielski interfejs Windows z polskim formatem regionalnym daje Cewkę po angielsku.</summary>
    [Fact]
    public void JezykWindowsZListyJezykowWyswietlania()
    {
        Assert.Equal(["en", "pl"], SystemLanguage.ParseLanguageList("en-US\0pl-PL\0\0"));
        Assert.Empty(SystemLanguage.ParseLanguageList(null));
    }

    /// <summary>Lista z LANGUAGE jest przeglądana cała: pierwszy nieprzetłumaczony język nie przekreśla reszty.</summary>
    [Fact]
    public void LinuxPrzegladaCalaListeLanguage()
    {
        var result = SystemLanguage.FromEnvironment(Variables(("LANGUAGE", "be:ru:en"), ("LANG", "be_BY.UTF-8")));

        Assert.Equal(["be", "ru", "en"], result);
    }

    [Fact]
    public void LinuxBierzeLcAllPrzedLang()
    {
        var result = SystemLanguage.FromEnvironment(Variables(("LC_ALL", "de_DE.UTF-8"), ("LANG", "pl_PL.UTF-8")));

        Assert.Equal(["de"], result);
    }

    /// <summary>Reguła gettext: przy języku „C” zmienna LANGUAGE nie obowiązuje.</summary>
    [Theory]
    [InlineData("C")]
    [InlineData("POSIX")]
    [InlineData("C.UTF-8")]
    public void LinuxPomijaLanguageGdyJezykiemJestC(string locale)
    {
        var result = SystemLanguage.FromEnvironment(Variables(("LANGUAGE", "pl"), ("LANG", locale)));

        Assert.Empty(result);
    }

    [Fact]
    public void LinuxBezLangBierzeLanguage()
    {
        Assert.Equal(["uk"], SystemLanguage.FromEnvironment(Variables(("LANGUAGE", "uk"))));
    }

    [Fact]
    public void WindowsPodajeChocJedenJezyk()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.NotEmpty(SystemLanguage.Candidates());
    }

    private static Func<string, string?> Variables(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // ---------- Skojarzenia plików ----------

    /// <summary>Opis widoczny w ustawieniach Windows podlega standardowi tekstów: bez kropki i bez przymiotników.</summary>
    [Fact]
    public void DomyslneTekstySkojarzenZgodneZeStandardem()
    {
        var texts = AssociationTexts.Default;

        Assert.False(texts.Description.EndsWith('.'));
        Assert.DoesNotContain("Minimalistyczny", texts.Description);
        Assert.False(string.IsNullOrWhiteSpace(texts.FileTypeName));
    }

    /// <summary>Opis w metadanych pliku wykonywalnego jest tym samym zdaniem co opis pakietów.</summary>
    [Fact]
    public void OpisZestawuZgodnyZOpisemPakietow()
    {
        var description = typeof(Cewka.App.ViewModels.SettingsViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyDescriptionAttribute), false)
            .Cast<System.Reflection.AssemblyDescriptionAttribute>()
            .Single().Description;

        Assert.Equal("Odtwarzacz plików muzycznych z korektorem i wyrównaniem głośności", description);
    }

    // ---------- Zasilanie w Linuksie ----------

    /// <summary>Laptop ładowany przez USB-C z wyłączonym wpisem zasilacza sieciowego jest na zasilaniu.</summary>
    [Fact]
    public void LadowanieUsbCToNieBateria()
    {
        var root = PowerSupplies(
            ("ACAD", [("type", "Mains"), ("online", "0")]),
            ("ucsi-source-psy-USBC000-001", [("type", "USB"), ("online", "1")]),
            ("BAT0", [("type", "Battery"), ("status", "Charging")]));

        Assert.False(PowerStatus.LinuxOnBattery(root));
    }

    [Fact]
    public void RozladowujacaSieBateriaBezZasilaczaToBateria()
    {
        var root = PowerSupplies(
            ("AC", [("type", "Mains"), ("online", "0")]),
            ("BAT0", [("type", "Battery"), ("status", "Discharging")]));

        Assert.True(PowerStatus.LinuxOnBattery(root));
    }

    /// <summary>Wpis zasilacza bez pliku online to stan nieznany, a nieznany stan to sieć.</summary>
    [Fact]
    public void ZasilaczBezStanuIBezBateriiToSiec()
    {
        Assert.False(PowerStatus.LinuxOnBattery(PowerSupplies(("AC", [("type", "Mains")]))));
    }

    /// <summary>Rozładowująca się bateria myszy nie przełącza komputera stacjonarnego na tryb oszczędny.</summary>
    [Fact]
    public void BateriaUrzadzeniaNieLiczySie()
    {
        var root = PowerSupplies(
            ("hidpp_battery_0", [("type", "Battery"), ("scope", "Device"), ("status", "Discharging")]));

        Assert.False(PowerStatus.LinuxOnBattery(root));
    }

    private string PowerSupplies(params (string Name, (string File, string Value)[] Files)[] supplies)
    {
        var root = Path.Combine(Path.GetTempPath(), "cewka-zasilanie-" + Guid.NewGuid().ToString("N")[..8]);
        _directories.Add(root);

        foreach (var (name, files) in supplies)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            foreach (var (file, value) in files) File.WriteAllText(Path.Combine(directory, file), value + "\n");
        }

        return root;
    }

    // ---------- MPRIS ----------

    [Fact]
    public void ZwyklyBiegOdtwarzaniaToNiePrzewiniecie()
    {
        var detector = new SeekDetector();

        Assert.False(detector.Report(TimeSpan.FromSeconds(30), playing: true, now: 0));
        Assert.False(detector.Report(TimeSpan.FromSeconds(30.025), playing: true, now: 25));

        // Zminimalizowane okno odświeża rzadko, ale pozycja idzie z zegarem.
        Assert.False(detector.Report(TimeSpan.FromSeconds(35.025), playing: true, now: 5025));
    }

    /// <summary>Przewinięcie z 0:30 na 3:00 musi trafić do panelu, który inaczej liczy dalej od 0:30.</summary>
    [Fact]
    public void SkokPozycjiJestPrzewinieciem()
    {
        var detector = new SeekDetector();

        detector.Report(TimeSpan.FromSeconds(30), playing: true, now: 0);

        Assert.True(detector.Report(TimeSpan.FromSeconds(180), playing: true, now: 25));
        Assert.True(detector.Report(TimeSpan.FromSeconds(10), playing: true, now: 50));
    }

    [Fact]
    public void PrzewiniecieWPauzieTezJestPrzewinieciem()
    {
        var detector = new SeekDetector();

        detector.Report(TimeSpan.FromSeconds(30), playing: false, now: 0);

        Assert.False(detector.Report(TimeSpan.FromSeconds(30), playing: false, now: 60_000));
        Assert.True(detector.Report(TimeSpan.FromSeconds(90), playing: false, now: 60_025));
    }

    [Fact]
    public void NowyUtworNieJestPrzewinieciem()
    {
        var detector = new SeekDetector();

        detector.Report(TimeSpan.FromSeconds(200), playing: true, now: 0);
        detector.Reset();

        Assert.False(detector.Report(TimeSpan.Zero, playing: true, now: 25));
    }

    /// <summary>
    /// Odświeżenie pozycji potrafi przyjść przed metadanymi następnego utworu. Przejście z końca
    /// utworu na początek następnego to wtedy zwykłe granie, a nie przewinięcie starego.
    /// </summary>
    [Fact]
    public void PrzejscieDoNastepnegoPrzedMetadanymiNieJestPrzewinieciem()
    {
        var detector = new SeekDetector();
        var length = TimeSpan.FromSeconds(200);

        detector.Report(TimeSpan.FromSeconds(199.8), playing: true, now: 0, length);

        Assert.False(detector.Report(TimeSpan.FromSeconds(0.1), playing: true, now: 25, length));
    }

    /// <summary>Powrót na początek ze środka utworu pozostaje przewinięciem.</summary>
    [Fact]
    public void PowrotNaPoczatekZeSrodkaJestPrzewinieciem()
    {
        var detector = new SeekDetector();
        var length = TimeSpan.FromSeconds(200);

        detector.Report(TimeSpan.FromSeconds(120), playing: true, now: 0, length);

        Assert.True(detector.Report(TimeSpan.Zero, playing: true, now: 25, length));
    }

    /// <summary>Puszczenie suwaka w panelu w chwili zmiany utworu nie może przewinąć następnego.</summary>
    [Fact]
    public void PozycjaDlaInnegoUtworuJestPomijana()
    {
        const string current = MprisRules.TrackIdPrefix + "2";
        const long length = 180_000_000;

        Assert.True(MprisRules.IsPositionRequestValid(current, 60_000_000, current, length));
        Assert.False(MprisRules.IsPositionRequestValid(MprisRules.TrackIdPrefix + "1", 60_000_000, current, length));
        Assert.False(MprisRules.IsPositionRequestValid(current, -1, current, length));
        Assert.False(MprisRules.IsPositionRequestValid(current, length + 1, current, length));
        Assert.False(MprisRules.IsPositionRequestValid(MprisRules.NoTrack, 0, MprisRules.NoTrack, 0));
    }

    [Fact]
    public void IdentyfikatoryUtworowPozaPrzestrzeniaMpris()
    {
        Assert.False(MprisRules.TrackIdPrefix.StartsWith("/org/mpris", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenUriPrzyjmujeTylkoPlikiLokalne()
    {
        var file = Path.Combine(Path.GetTempPath(), "a b.mp3");

        Assert.True(MprisRules.TryGetLocalPath(new Uri(file).AbsoluteUri, out var path));
        Assert.Equal(file, path);

        Assert.False(MprisRules.TryGetLocalPath("https://example.com/a.mp3", out _));
        Assert.False(MprisRules.TryGetLocalPath("nie adres", out _));
    }

    // ---------- Klawisze multimedialne ----------

    /// <summary>Gdy panel systemowy działa, Windows sam kieruje klawisze do sesji odtwarzania.</summary>
    [Fact]
    public void ZaczepTylkoGdyBrakPaneluSystemowego()
    {
        Assert.False(MediaKeys.ShouldHook(enabled: true, systemPanelActive: true));
        Assert.False(MediaKeys.ShouldHook(enabled: false, systemPanelActive: false));
        Assert.Equal(OperatingSystem.IsWindows(), MediaKeys.ShouldHook(enabled: true, systemPanelActive: false));
    }

    /// <summary>Cewka wstrzymana w tle nie może odbierać klawisza przeglądarce ani innemu odtwarzaczowi.</summary>
    [Fact]
    public void KlawiszIdzieDalejGdyCewkaGoNieChce()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.PassOn, filter.KeyDown(MediaKey.PlayPause, capture: false, time: 1000));
        Assert.Equal(KeyVerdict.PassOn, filter.KeyUp(MediaKey.PlayPause));
    }

    [Fact]
    public void KlawiszPrzechwyconyGdyCewkaGra()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Next, capture: true, time: 1000));
        Assert.Equal(KeyVerdict.Swallow, filter.KeyUp(MediaKey.Next));
    }

    /// <summary>Przytrzymanie „Następny” ma przeskoczyć jeden utwór, a nie serię.</summary>
    [Fact]
    public void PowtorzeniaZPrzytrzymaniaNieWywolujaPolecen()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Next, capture: true, time: 1000));

        for (var time = 1500u; time < 3000; time += 33)
            Assert.Equal(KeyVerdict.Swallow, filter.KeyDown(MediaKey.Next, capture: true, time));

        Assert.Equal(KeyVerdict.Swallow, filter.KeyUp(MediaKey.Next));
        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Next, capture: true, time: 3100));
    }

    /// <summary>
    /// Decyzja z wciśnięcia obowiązuje do puszczenia: klawisz przekazany dalej nie zostaje
    /// w połowie przytrzymania przejęty, gdy Cewka akurat zacznie grać.
    /// </summary>
    [Fact]
    public void PowtorzeniaIdaTamGdziePierwszeWcisniecie()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.PassOn, filter.KeyDown(MediaKey.PlayPause, capture: false, time: 1000));
        Assert.Equal(KeyVerdict.PassOn, filter.KeyDown(MediaKey.PlayPause, capture: true, time: 1500));
        Assert.Equal(KeyVerdict.PassOn, filter.KeyUp(MediaKey.PlayPause));
    }

    /// <summary>Zgubione puszczenie nie może zablokować klawisza na zawsze.</summary>
    [Fact]
    public void WcisnieciePoDlugiejPrzerwieJestNowe()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Stop, capture: true, time: 1000));
        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Stop, capture: true, time: 10_000));
    }

    [Fact]
    public void KlawiszeNieWplywajaNaSiebieNawzajem()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Next, capture: true, time: 1000));
        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Previous, capture: true, time: 1010));
    }

    /// <summary>Licznik czasu systemu przekręca się po 49 dniach; przerwa liczy się wtedy dalej poprawnie.</summary>
    [Fact]
    public void PrzekrecenieLicznikaCzasuNieMyliPowtorzen()
    {
        var filter = new MediaKeyFilter();

        Assert.Equal(KeyVerdict.SwallowAndRaise, filter.KeyDown(MediaKey.Next, capture: true, time: uint.MaxValue - 10));
        Assert.Equal(KeyVerdict.Swallow, filter.KeyDown(MediaKey.Next, capture: true, time: 20));
    }
}
