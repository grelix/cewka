using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Sprawdzenia warstwy systemowej, które nie wymagają okna ani urządzenia dźwiękowego.</summary>
public sealed class PlatformTests
{
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
