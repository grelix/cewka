using System.Reflection;
using Avalonia.Headless;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Zegar renderowania testów tyka sam najwyżej raz na sekundę, a klatki wymuszają testy.
/// <para>
/// Przy 60 klatkach na sekundę i programowym Skia na wolniejszej maszynie (WSL) tyknięcia
/// z priorytetem wyższym niż zwykłe zadania przychodziły szybciej, niż wątek interfejsu je
/// wykonywał, a <c>Dispatcher.RunJobs()</c> w pomocnikach wejścia nie wracał, dopóki płyta
/// zamawiała klatki: minutę na zdarzenie, do końca utworu testowego, a w pełnym zestawie bez końca.
/// Na szybszej maszynie objawu nie ma, więc test pilnuje zegara, którego platforma naprawdę używa.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RenderTimerTests
{
    [Fact]
    public void ZegarRenderowaniaPlatformyTestowTykaSamRazNaSekunde() => Ui.Run(() =>
    {
        // Zegar platformy bez ekranu (HeadlessRenderTimer) nie ma publicznego dostępu; nazwy pól
        // pochodzą z Avalonia.Headless 12.1. Po aktualizacji, która je zmieni, test pada głośno.
        var timer = typeof(AvaloniaHeadlessPlatform)
            .GetField("s_renderTimer", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null);
        Assert.NotNull(timer);

        var fps = timer.GetType().GetField("_framesPerSecond", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer);
        Assert.True(Equals(fps, 1), $"{timer.GetType().Name}: {fps ?? "brak pola _framesPerSecond"} klatek na sekundę");
    });
}
