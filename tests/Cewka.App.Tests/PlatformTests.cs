using Cewka.Platform;
using Cewka.Platform.Linux;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Sprawdzenia warstwy systemowej, które nie wymagają okna ani urządzenia dźwiękowego.</summary>
public sealed class PlatformTests
{
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
