using Avalonia;
using Cewka.App.Services;
using Cewka.App.Views;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Warstwa sterowania sceny i blokada wygaszania, bez okna i z zegarem testu.</summary>
public sealed class SceneChromeTests
{
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    private SceneChrome Chrome(bool playing = true)
    {
        var chrome = new SceneChrome(() => _now);
        chrome.Reset(playing);
        return chrome;
    }

    private void Wait(SceneChrome chrome, double seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        chrome.Update();
    }

    [Fact]
    public void WarstwaGasniePoCzterechSekundachBezWejscia()
    {
        var chrome = Chrome();
        Assert.False(chrome.IsShown);

        chrome.RevealByPointer();
        Wait(chrome, 3.9);
        Assert.True(chrome.IsShown);

        Wait(chrome, 0.2);
        Assert.False(chrome.IsShown);
    }

    [Fact]
    public void KazdyKlawiszLiczyCzasOdNowa()
    {
        var chrome = Chrome();
        chrome.RevealByPointer();

        Wait(chrome, 3);
        chrome.KeyPressed();
        Wait(chrome, 3);
        Assert.True(chrome.IsShown);

        Wait(chrome, 1.1);
        Assert.False(chrome.IsShown);
    }

    /// <summary>Klawisz po myszy chowa kursor, a najechanie przestaje trzymać warstwę do następnego ruchu.</summary>
    [Fact]
    public void KlawiszChowaKursorINajechanieNieTrzymaWarstwy()
    {
        var chrome = Chrome();
        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(560, 500));
        chrome.SetPointerOver(true);
        Wait(chrome, 60);
        Assert.True(chrome.IsShown);

        chrome.KeyPressed();
        Assert.False(chrome.IsCursorShown);
        chrome.SetPointerOver(true);
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);

        chrome.PointerMoved(new Point(600, 500));
        chrome.SetPointerOver(true);
        Assert.True(chrome.IsCursorShown);
        Wait(chrome, 60);
        Assert.True(chrome.IsShown);
    }

    [Fact]
    public void LicznikStoiNadWarstwaIPrzyRegulacjiPaska()
    {
        var chrome = Chrome();
        chrome.RevealByPointer();

        chrome.SetPointerOver(true);
        Wait(chrome, 60);
        Assert.True(chrome.IsShown);

        // Po zejściu kursora licznik biegnie od początku.
        chrome.SetPointerOver(false);
        Wait(chrome, 3.5);
        Assert.True(chrome.IsShown);
        Wait(chrome, 1);
        Assert.False(chrome.IsShown);

        chrome.RevealByPointer();
        chrome.SetDragging(true);
        Wait(chrome, 60);
        Assert.True(chrome.IsShown);
        chrome.SetDragging(false);
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);
    }

    [Fact]
    public void OdslonieciMyszaNieDajeFokusuKlawiatury()
    {
        var chrome = Chrome();
        chrome.RevealByPointer();
        Assert.True(chrome.IsShown);
        Assert.False(chrome.HasKeyboardFocus);
    }

    /// <summary>Ruch myszy przy warstwie odsłoniętej klawiszem nie zdejmuje trybu klawiatury.</summary>
    [Fact]
    public void TracenieMyszyPrzyWidocznejWarstwieZostawiaTrybKlawiatury()
    {
        var chrome = Chrome();
        chrome.RevealByKeyboard();

        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(560, 500));
        Assert.True(chrome.IsShown);
        Assert.True(chrome.HasKeyboardFocus);

        chrome.RevealByPointer();
        Assert.True(chrome.HasKeyboardFocus);

        // Fokus przeniesiony myszą kończy tryb klawiatury.
        chrome.KeyboardFocusLeft();
        Assert.False(chrome.HasKeyboardFocus);
    }

    /// <summary>
    /// Najechanie trzyma warstwę tylko po rzeczywistym ruchu wskaźnika od odsłonięcia: nieruchomy
    /// kursor, nad którym warstwa wyrosła po klawiszu albo pauzie, jej nie trzyma.
    /// </summary>
    [Fact]
    public void NajechanieTrzymaWarstweTylkoPoRuchuWskaznika()
    {
        var chrome = Chrome();

        chrome.RevealByKeyboard();
        chrome.SetPointerOver(true);
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);

        chrome.SetPointerOver(false);
        chrome.PlayingChanged(false);
        chrome.SetPointerOver(true);
        Wait(chrome, 30.1);
        Assert.False(chrome.IsShown);

        // Ruch ponad próg po odsłonięciu: kursor nad warstwą ją trzyma.
        chrome.SetPointerOver(false);
        chrome.RevealByKeyboard();
        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(560, 500));
        chrome.SetPointerOver(true);
        Wait(chrome, 60);
        Assert.True(chrome.IsShown);
    }

    /// <summary>Kursor widać tylko po ruchu wskaźnika od odsłonięcia; klawisz i pauza go nie pokazują.</summary>
    [Fact]
    public void KursorWidacTylkoPoRuchuWskaznika()
    {
        var chrome = Chrome();
        var changes = 0;
        chrome.Changed += () => changes++;

        chrome.RevealByKeyboard();
        Assert.False(chrome.IsCursorShown);

        changes = 0;
        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(560, 500));
        Assert.True(chrome.IsCursorShown);
        Assert.Equal(1, changes);

        Wait(chrome, 4.1);
        Assert.False(chrome.IsCursorShown);

        chrome.PlayingChanged(false);
        Assert.True(chrome.IsShown);
        Assert.False(chrome.IsCursorShown);
    }

    [Fact]
    public void RuchPonizejProguIZdarzenieBezRuchuNieOdslaniaja()
    {
        var chrome = Chrome();

        // Pierwsze zdarzenie tylko zapamiętuje położenie.
        chrome.PointerMoved(new Point(500, 500));
        Assert.False(chrome.IsShown);

        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(505, 505));
        Assert.False(chrome.IsShown);

        chrome.PointerMoved(new Point(509, 500));
        Assert.True(chrome.IsShown);
    }

    /// <summary>
    /// Ruch kończy się 7 jednostek od punktu, w którym odsłonił warstwę, i warstwa gaśnie: próg liczy
    /// się od nowa od ostatniego położenia, więc trącenie o 2 jej nie odsłania.
    /// </summary>
    [Fact]
    public void PoZgaszeniuProgRuchuLiczySieOdOstatniegoPolozenia()
    {
        var chrome = Chrome();
        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(520, 500));
        Assert.True(chrome.IsShown);

        chrome.PointerMoved(new Point(527, 500));
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);

        chrome.PointerMoved(new Point(529, 500));
        Assert.False(chrome.IsShown);
    }

    /// <summary>Klawisz, który chowa kursor, też liczy próg ruchu od ostatniego położenia wskaźnika.</summary>
    [Fact]
    public void PoKlawiszuProgRuchuLiczySieOdOstatniegoPolozenia()
    {
        var chrome = Chrome();
        chrome.PointerMoved(new Point(500, 500));
        chrome.PointerMoved(new Point(520, 500));
        chrome.PointerMoved(new Point(527, 500));
        chrome.KeyPressed();
        Assert.False(chrome.IsCursorShown);

        chrome.PointerMoved(new Point(529, 500));
        Assert.False(chrome.IsCursorShown);
    }

    /// <summary>Po zapomnieniu położenia pierwsze zdarzenie wskaźnika znów tylko je zapamiętuje.</summary>
    [Fact]
    public void PoZapomnieniuPolozeniaPierwszeZdarzenieNieOdslania()
    {
        var chrome = Chrome();
        chrome.PointerMoved(new Point(500, 500));

        chrome.ForgetPointer();
        chrome.PointerMoved(new Point(800, 500));
        Assert.False(chrome.IsShown);

        chrome.PointerMoved(new Point(840, 500));
        Assert.True(chrome.IsShown);
    }

    [Fact]
    public void PierwszyKlikPrzyUkrytejWarstwieTylkoJaOdslania()
    {
        var chrome = Chrome();

        Assert.True(chrome.PointerPressed());
        Assert.True(chrome.IsShown);

        // Przy widocznej warstwie kliknięcie idzie dalej.
        Assert.False(chrome.PointerPressed());
    }

    /// <summary>Przy warstwie odsłoniętej klawiszem albo pauzą pierwszy klik tylko pokazuje kursor.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PierwszyKlikPrzyUkrytymKursorzeTylkoGoPokazuje(bool pause)
    {
        var chrome = Chrome();
        if (pause) chrome.PlayingChanged(false);
        else chrome.RevealByKeyboard();
        Assert.False(chrome.IsCursorShown);

        Assert.True(chrome.PointerPressed());
        Assert.True(chrome.IsCursorShown);
        Assert.False(chrome.PointerPressed());
    }

    [Fact]
    public void PauzaOdslaniaWarstweIChowaJaPoTrzydziestuSekundach()
    {
        var chrome = Chrome();
        Wait(chrome, 10);

        chrome.PlayingChanged(false);
        Assert.True(chrome.IsShown);
        Assert.False(chrome.HasKeyboardFocus);

        Wait(chrome, 29);
        Assert.True(chrome.IsShown);
        Wait(chrome, 1.1);
        Assert.False(chrome.IsShown);

        // Wznowienie wraca do zwykłego czasu.
        chrome.PlayingChanged(true);
        chrome.RevealByPointer();
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);
    }

    /// <summary>Jedyny termin to zgaszenie widocznej warstwy; ukryta warstwa niczego nie czeka.</summary>
    [Fact]
    public void TerminJestTylkoPrzyWidocznejWarstwieBezPrzytrzymania()
    {
        var chrome = Chrome();
        Assert.Null(chrome.NextDeadline);

        chrome.RevealByPointer();
        Assert.Equal(_now + SceneChrome.HideDelay, chrome.NextDeadline);

        chrome.SetPointerOver(true);
        Assert.Null(chrome.NextDeadline);

        chrome.SetPointerOver(false);
        Wait(chrome, 4.1);
        Assert.False(chrome.IsShown);
        Assert.Null(chrome.NextDeadline);
    }

    [Fact]
    public void KazdaZmianaTerminuJestZglaszanaTakzeBezWidocznejZmiany()
    {
        var chrome = Chrome();
        var reported = new List<TimeSpan?>();
        chrome.DeadlineChanged += () => reported.Add(chrome.NextDeadline);

        // Kursor nad warstwą dłużej niż licznik, potem zjeżdża: warstwa dalej widoczna, termin nowy.
        chrome.RevealByPointer();
        chrome.SetPointerOver(true);
        Wait(chrome, 5);
        reported.Clear();
        chrome.SetPointerOver(false);
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, Assert.Single(reported));

        // Klawisz przy widocznej warstwie liczy od nowa.
        Wait(chrome, 1);
        reported.Clear();
        chrome.KeyPressed();
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, Assert.Single(reported));

        // Wznowienie po pauzie wraca do zwykłego czasu, choć warstwa się nie zmienia.
        chrome.PlayingChanged(false);
        Wait(chrome, 5);
        reported.Clear();
        chrome.PlayingChanged(true);
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, Assert.Single(reported));

        // Odsłonięcie klawiszem ma zwykły termin, a fokus przeniesiony myszą liczy od nowa.
        chrome.RevealByKeyboard();
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, chrome.NextDeadline);
        Wait(chrome, 1);
        reported.Clear();
        chrome.KeyboardFocusLeft();
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, Assert.Single(reported));

        // Kliknięcie przy widocznym kursorze też liczy od nowa.
        chrome.RevealByPointer();
        Wait(chrome, 1);
        reported.Clear();
        Assert.False(chrome.PointerPressed());
        Assert.Equal<TimeSpan?>(_now + SceneChrome.HideDelay, Assert.Single(reported));
    }

    [Fact]
    public void PoczatekIKoniecRegulacjiPaskaSaWidocznaZmiana()
    {
        var chrome = Chrome();
        chrome.RevealByKeyboard();
        var changes = 0;
        chrome.Changed += () => changes++;

        chrome.SetAdjusting(true);
        Assert.Equal(1, changes);

        chrome.SetAdjusting(false);
        Assert.Equal(2, changes);
    }

    // ---------- Blokada wygaszania ----------

    private sealed class FakeWakeLock : IDisplayWakeLock
    {
        public bool IsHeld { get; private set; }

        public int Acquired { get; private set; }

        public int Released { get; private set; }

        public bool Disposed { get; private set; }

        public bool FailAcquire { get; set; }

        public void Acquire()
        {
            Acquired++;
            if (FailAcquire) throw new InvalidOperationException("system odmówił");
            IsHeld = true;
        }

        public void Release()
        {
            Released++;
            IsHeld = false;
        }

        public void Dispose()
        {
            Disposed = true;
            IsHeld = false;
        }
    }

    [Fact]
    public void BlokadaTylkoPrzyOtwartejScenieGraniuINiezminimalizowanymOknie()
    {
        var fake = new FakeWakeLock();
        var created = 0;
        using var policy = new SceneWakeLock(() => { created++; return fake; });

        // Bez sceny nic nie powstaje.
        policy.Update(sceneOpen: false, playing: true, minimised: false);
        Assert.Equal(0, created);
        Assert.False(policy.IsHeld);

        policy.Update(true, true, false);
        Assert.True(policy.IsHeld);

        // Pauza, stop i koniec kolejki to dla sceny brak grania.
        policy.Update(true, false, false);
        Assert.False(policy.IsHeld);

        policy.Update(true, true, false);
        Assert.True(policy.IsHeld);

        policy.Update(true, true, minimised: true);
        Assert.False(policy.IsHeld);

        policy.Update(true, true, false);
        policy.Update(sceneOpen: false, playing: true, minimised: false);
        Assert.False(policy.IsHeld);
        Assert.Equal(1, created);
    }

    [Fact]
    public void WylaczonaBlokadaNieZatrzymujeWygaszaniaMimoScenyIGrania()
    {
        var fake = new FakeWakeLock();
        using var policy = new SceneWakeLock(() => fake);

        policy.Update(true, true, false);
        Assert.True(policy.IsHeld);

        policy.Update(true, true, false, keepAwake: false);
        Assert.False(policy.IsHeld);

        policy.Update(true, true, false, keepAwake: true);
        Assert.True(policy.IsHeld);
    }

    [Fact]
    public void ZamkniecieZwalniaBlokadeIJaZamyka()
    {
        var fake = new FakeWakeLock();
        var policy = new SceneWakeLock(() => fake);

        policy.Update(true, true, false);
        policy.Dispose();

        Assert.False(fake.IsHeld);
        Assert.True(fake.Disposed);

        // Po zamknięciu nic już jej nie przejmuje.
        policy.Update(true, true, false);
        Assert.False(fake.IsHeld);
    }

    [Fact]
    public void WyjatekPrzyPrzejeciuZostawiaBlokadeZwolniona()
    {
        var fake = new FakeWakeLock { FailAcquire = true };
        using var policy = new SceneWakeLock(() => fake);

        policy.Update(true, true, false);
        Assert.False(policy.IsHeld);
        Assert.True(fake.Released > 0);

        // Nieudane utworzenie blokady też nie przerywa sceny.
        using var broken = new SceneWakeLock(() => throw new DllNotFoundException());
        broken.Update(true, true, false);
        Assert.False(broken.IsHeld);
    }
}
