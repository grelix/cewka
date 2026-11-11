using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Wysuwanie i chowanie nieprzypiętego panelu kolejki, bez okna i z zegarem testu.</summary>
[Collection(UiCollection.Name)]
public sealed class QueuePeekTests
{
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    private QueuePeek Peek(QueueRevealDelay reveal = QueueRevealDelay.Normal, QueueHideDelay hide = QueueHideDelay.TwoSeconds)
    {
        var peek = new QueuePeek(() => _now)
        {
            RevealDelay = QueuePanelTimes.RevealDelay(reveal),
            HideDelay = QueuePanelTimes.HideDelay(hide),
        };
        peek.Enabled = true;
        return peek;
    }

    private void Wait(QueuePeek peek, double seconds)
    {
        _now += TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        peek.Update();
    }

    /// <summary>Wysunięty podgląd po wejściu na uchwyt i odczekaniu zwłoki.</summary>
    private QueuePeek Shown(QueueHideDelay hide = QueueHideDelay.TwoSeconds)
    {
        var peek = Peek(hide: hide);
        peek.HandleEntered();
        Wait(peek, 0.3);
        peek.PanelEntered();
        peek.HandleExited();
        Assert.True(peek.IsShown);
        return peek;
    }

    [Theory]
    [InlineData(QueueRevealDelay.Short, 150)]
    [InlineData(QueueRevealDelay.Normal, 300)]
    [InlineData(QueueRevealDelay.Long, 600)]
    public void PanelWysuwaSiePoZwloceNadUchwytem(QueueRevealDelay delay, int milliseconds)
    {
        var peek = Peek(reveal: delay);

        peek.HandleEntered();
        Assert.Equal(_now + TimeSpan.FromMilliseconds(milliseconds), peek.NextDeadline);

        Wait(peek, (milliseconds - 10) / 1000.0);
        Assert.False(peek.IsShown);

        Wait(peek, 0.01);
        Assert.True(peek.IsShown);
    }

    [Fact]
    public void PrzelotNadUchwytemNieWysuwaPanelu()
    {
        var peek = Peek();

        peek.HandleEntered();
        Wait(peek, 0.2);
        peek.HandleExited();
        Wait(peek, 0.5);

        Assert.False(peek.IsShown);
        Assert.Null(peek.NextDeadline);
    }

    [Fact]
    public void NacisniecieUchwytuKasujeOdliczanieDoWyjsciaZeStrefy()
    {
        var peek = Peek();

        peek.HandleEntered();
        peek.HandlePressed();
        Wait(peek, 1);
        Assert.False(peek.IsShown);

        peek.HandleExited();
        peek.HandleEntered();
        Wait(peek, 0.3);
        Assert.True(peek.IsShown);
    }

    [Fact]
    public void PanelZostajeDopokiKursorJestNadNimAChowaSieDwieIPolSekundyPoZjechaniu()
    {
        var peek = Shown();

        Wait(peek, 60);
        Assert.True(peek.IsShown);

        peek.PanelExited();
        Wait(peek, 2.49);
        Assert.True(peek.IsShown);

        Wait(peek, 0.01);
        Assert.False(peek.IsShown);
    }

    [Fact]
    public void PowrotKursoraPrzedCzasemNicNieZmienia()
    {
        var peek = Shown();

        peek.PanelExited();
        Wait(peek, 0.4);
        peek.PanelEntered();
        Wait(peek, 10);
        Assert.True(peek.IsShown);

        // Po ponownym zjechaniu czas liczy się od początku.
        peek.PanelExited();
        Wait(peek, 2.4);
        Assert.True(peek.IsShown);
        Wait(peek, 0.1);
        Assert.False(peek.IsShown);
    }

    [Fact]
    public void KursorNadUchwytemTrzymaWysunietyPanel()
    {
        var peek = Shown();

        peek.PanelExited();
        peek.HandleEntered();
        Wait(peek, 30);

        Assert.True(peek.IsShown);
    }

    [Theory]
    [InlineData((int)QueuePeek.Hold.Menu)]
    [InlineData((int)QueuePeek.Hold.Pointer)]
    [InlineData((int)QueuePeek.Hold.Focus)]
    [InlineData((int)QueuePeek.Hold.DragOver)]
    [InlineData((int)QueuePeek.Hold.Inactive)]
    public void BlokadaWstrzymujeChowanieAPoNiejCzasLiczySieOdPoczatku(int value)
    {
        var hold = (QueuePeek.Hold)value;
        var peek = Shown();

        peek.PanelExited();
        Wait(peek, 2);
        peek.SetHold(hold, true);
        Wait(peek, 30);
        Assert.True(peek.IsShown);
        Assert.Null(peek.NextDeadline);

        peek.SetHold(hold, false);
        Wait(peek, 2.4);
        Assert.True(peek.IsShown);
        Wait(peek, 0.1);
        Assert.False(peek.IsShown);
    }

    [Theory]
    [InlineData((int)QueuePeek.Hold.DragOver)]
    [InlineData((int)QueuePeek.Hold.Inactive)]
    public void PrzeciaganiePlikowINieaktywneOknoNieWysuwajaPanelu(int value)
    {
        var hold = (QueuePeek.Hold)value;
        var peek = Peek();
        peek.SetHold(hold, true);

        peek.HandleEntered();
        Wait(peek, 5);
        Assert.False(peek.IsShown);

        // Po ustaniu blokady kursor wciąż nad uchwytem: zwłoka liczy się od tej chwili.
        peek.SetHold(hold, false);
        Wait(peek, 0.29);
        Assert.False(peek.IsShown);
        Wait(peek, 0.01);
        Assert.True(peek.IsShown);
    }

    [Theory]
    [InlineData(QueueHideDelay.HalfSecond, 1.0)]
    [InlineData(QueueHideDelay.OneSecond, 1.5)]
    [InlineData(QueueHideDelay.FourSeconds, 4.5)]
    public void CzasChowaniaZUstawienDochodziDoTolerancji(QueueHideDelay hide, double total)
    {
        var peek = Shown(hide);

        peek.PanelExited();
        Wait(peek, total - 0.01);
        Assert.True(peek.IsShown);
        Wait(peek, 0.01);
        Assert.False(peek.IsShown);
    }

    [Fact]
    public void PanelBezChowaniaZostajeDoZamkniecia()
    {
        var peek = Shown(QueueHideDelay.Never);

        peek.PanelExited();
        Wait(peek, 3600);
        Assert.True(peek.IsShown);
        Assert.Null(peek.NextDeadline);

        peek.Close();
        Assert.False(peek.IsShown);
    }

    [Fact]
    public void ZamkniecieNadUchwytemNieWysuwaPaneluOdNowa()
    {
        var peek = Peek();
        peek.HandleEntered();
        Wait(peek, 0.3);

        peek.Close();
        Wait(peek, 5);
        Assert.False(peek.IsShown);

        peek.HandleExited();
        peek.HandleEntered();
        Wait(peek, 0.3);
        Assert.True(peek.IsShown);
    }

    [Fact]
    public void WylaczenieChowaPodgladINieWysuwaGo()
    {
        var peek = Shown();
        var changes = 0;
        peek.Changed += () => changes++;

        peek.Enabled = false;
        Assert.False(peek.IsShown);
        Assert.Equal(1, changes);

        peek.HandleEntered();
        Wait(peek, 5);
        Assert.False(peek.IsShown);

        // Show przy wyłączonym podglądzie nic nie robi (np. panel przypięty).
        peek.Show();
        Assert.False(peek.IsShown);
    }

    [Fact]
    public void TerminZmieniaSieZPowiadomieniem()
    {
        var peek = Peek();
        var deadlines = new List<TimeSpan?>();
        peek.DeadlineChanged += () => deadlines.Add(peek.NextDeadline);

        peek.HandleEntered();
        Wait(peek, 0.3);

        Assert.Equal([_now, null], deadlines);
    }

    // ---------- Ustawienia ----------

    private static SettingsStore Store(string json)
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    [Fact]
    public void NowaInstalacjaMaUchwytBezPrzypieciaIWysuwaniePoNajechaniu()
    {
        var settings = new AppSettings();

        Assert.False(settings.QueuePinned);
        Assert.Null(settings.QueueOpen);
        Assert.Equal(QueueReveal.Hover, settings.QueueReveal);
        Assert.Equal(QueueHideDelay.TwoSeconds, settings.QueueHideDelay);
        Assert.Equal(QueueRevealDelay.Normal, settings.QueueRevealDelay);
        Assert.Null(settings.QueueWidth);
        Assert.Equal(TimeSpan.FromMilliseconds(300), QueuePanelTimes.RevealDelay(settings.QueueRevealDelay));
        Assert.Equal(TimeSpan.FromSeconds(2), QueuePanelTimes.HideDelay(settings.QueueHideDelay));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DawnaKolejkaOtwartaPrzechodziWPrzypiecie(bool open) => Ui.Run(() =>
    {
        var store = Store($$"""{ "schemaVersion": 1, "queueOpen": {{(open ? "true" : "false")}} }""");

        Assert.Equal(open, store.Current.QueuePinned);
        Assert.Null(store.Current.QueueOpen);

        // Zapis nie zawiera już dawnego pola.
        var json = System.Text.Json.JsonSerializer.Serialize(store.Current, SettingsJsonContext.Default.AppSettings);
        Assert.DoesNotContain("queueOpen", json);
        Assert.Contains("queuePinned", json);
    });

    [Theory]
    [InlineData("100", 240.0)]
    [InlineData("241.5", 241.5)]
    [InlineData("5000", 5000.0)]
    public void SzerokoscPonizejNajmniejszejRosnieAWiekszaZostajeBezZmian(string saved, double expected) => Ui.Run(() =>
    {
        var store = Store($$"""{ "queueWidth": {{saved}} }""");
        Assert.Equal(expected, store.Current.QueueWidth);
    });

    [Fact]
    public void WartosciSpozaWyliczenWracajaDoDomyslnych() => Ui.Run(() =>
    {
        var store = Store("""{ "queueReveal": 7, "queueHideDelay": 99, "queueRevealDelay": -1 }""");

        Assert.Equal(QueueReveal.Hover, store.Current.QueueReveal);
        Assert.Equal(QueueHideDelay.TwoSeconds, store.Current.QueueHideDelay);
        Assert.Equal(QueueRevealDelay.Normal, store.Current.QueueRevealDelay);
    });

    [Theory]
    [InlineData(null, 280, 1300, true, 280)]
    [InlineData(500.0, 280, 1300, true, 500)]
    [InlineData(900.0, 280, 1300, true, 560)]
    [InlineData(900.0, 280, 1000, true, 500)]
    [InlineData(900.0, 280, 900, false, 820)]
    [InlineData(100.0, 280, 900, false, 240)]
    public void SzerokoscWyswietlanaJestPrzycinanaDoOkna(double? chosen, double fallback, double window, bool column, double expected) =>
        Assert.Equal(expected, QueuePanelWidth.Shown(chosen, fallback, window, column));
}
