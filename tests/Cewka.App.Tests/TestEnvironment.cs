using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Cewka.App.Models;
using Cewka.Audio.Devices;
using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Ustawienia całego przebiegu testów, wykonywane przed pierwszym testem.
/// <para>
/// Ścieżki ustawień, kolejki i pamięci podręcznej trafiają do katalogu tymczasowego, a dźwięk
/// na wyjście puste. Test nie może dotknąć ustawień ani kolejki osoby, która go uruchamia,
/// ani zagrać przez jej głośniki.
/// </para>
/// </summary>
internal static class TestEnvironment
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "cewka-testy-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialise()
    {
        Directory.CreateDirectory(Root);
        AppPaths.Redirect(Root);
        AudioDevice.UseNullOutput = true;

        // Testy układu sceny liczą kadr na całym ekranie; margines ma własne testy.
        ScreenMargin.Default = SceneMargin.None;

        // Kraj startowy radia nie zależy od regionu maszyny; testy regionu podstawiają go same.
        Cewka.App.ViewModels.RadioViewModel.RegionProvider = () => null;
    }

    /// <summary>Nowy, pusty katalog na pliki jednego testu.</summary>
    public static string NewDirectory()
    {
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>Aplikacja bez okna i bez urządzenia graficznego, z prawdziwym rysowaniem Skia.</summary>
public static class TestApp
{
    /// <summary>
    /// Zegar renderowania tyka sam raz na sekundę, a nie 60 razy: klatki wymuszają testy
    /// (<c>ForceRenderTimerTick</c>). Przy 60 klatkach i wolnym rysowaniu programowym tyknięcia
    /// zalewały kolejkę dyspozytora szybciej, niż dało się je wykonać (zob. <c>RenderTimerTests</c>).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, Fps = 1 })
        .With(App.FontOptions);
}

/// <summary>
/// Wątek interfejsu dla testów modeli widoku. Jeden na cały przebieg: Avalonia nie daje się
/// zainicjować drugi raz w tym samym procesie.
/// </summary>
public static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(TestApp), AvaloniaTestIsolationLevel.PerAssembly);

        // Aplikacja bierze przy starcie język systemu. Testy zakładają polski niezależnie od maszyny;
        // testy innych języków przełączają go na czas testu.
        session.Dispatch(() => Localisation.Strings.Current.SetLanguage("pl"), CancellationToken.None).GetAwaiter().GetResult();
        return session;
    });

    public static void Run(Action action) =>
        Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    public static T Run<T>(Func<T> action) =>
        Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Test, który czeka przez <c>await</c>: w tym czasie pętla dyspozytora działa, więc
    /// prawdziwe zegary (<c>DispatcherTimer</c>) tykają same.
    /// </summary>
    public static void RunAsync(Func<Task> action) =>
        Session.Value.Dispatch<bool>(async () =>
        {
            await action();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Czeka z oddaniem pętli dyspozytora, dopóki warunek nie zostanie spełniony.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>
    /// Wykonuje zaległe zadania dyspozytora, dopóki warunek nie zostanie spełniony. Wołane
    /// z wnętrza <see cref="Run(Action)"/>, gdy model widoku czeka na pracę w tle.
    /// </summary>
    public static bool WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (true)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(5);
        }
    }
}

/// <summary>Testy na wątku interfejsu idą po kolei, bo dzielą jedną aplikację.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "Interfejs";
}
