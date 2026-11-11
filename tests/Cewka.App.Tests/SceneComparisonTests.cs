using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Zrzut planszy do porównania z obrazami wzoru. Tylko lokalnie: okładki albumu ze wzoru nie ma
/// w repozytorium, więc bez pliku wskazanego w zmiennej <c>CEWKA_PLANSZA_OKLADKA</c> przebieg pokazuje
/// test jako pominięty.
/// </summary>
public sealed class PlanszaFactAttribute : FactAttribute
{
    public PlanszaFactAttribute()
    {
        var cover = Environment.GetEnvironmentVariable(SceneComparisonTests.CoverVariable);
        if (string.IsNullOrEmpty(cover) || !File.Exists(cover))
            Skip = $"Brak okładki wzoru planszy ({SceneComparisonTests.CoverVariable}).";
    }
}

/// <summary>
/// Plansza 1920 × 1080 z okładką, tekstami i położeniem ze wzoru, w aktywności i w spoczynku,
/// zapisana do plików PNG w katalogu ze zmiennej <c>CEWKA_PLANSZA_WYJSCIE</c>. Płyta stoi prosto,
/// plamy w ustalonym punkcie, bez przenikań.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SceneComparisonTests
{
    public const string CoverVariable = "CEWKA_PLANSZA_OKLADKA";

    public const string OutputVariable = "CEWKA_PLANSZA_WYJSCIE";

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Save(MainWindow window, string path)
    {
        Settle();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    [PlanszaFact]
    public void PlanszaZOkladkaWzoruWAktywnosciIWSpoczynku()
    {
        // Podpisy po polsku jak na wzorze: szerokość rzędu zależy od języka.
        SceneTests.InLanguage("pl", () =>
        {
            var output = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } folder
                ? folder
                : TestEnvironment.NewDirectory();
            Directory.CreateDirectory(output);

            App.Settings.Current.Window = null;
            using var h = new PlayerHarness(s =>
            {
                s.QueuePinned = false;
                s.WindowControls = WindowControlsPosition.Right;
                s.Effects = EffectsMode.Full;
            });

            var now = TimeSpan.FromSeconds(1000);
            var window = new MainWindow(false, h.Player) { Width = 1920, Height = 1080 };
            window.Scene.Clock = () => now;
            window.Show();

            try
            {
                var player = h.Player;
                player.OpenPaths([h.TaggedTrack("katarakta", "Katarakta", "Daniel Bloom feat. Mela Koteluk", "Lovely Fear", seconds: 239)]);
                Assert.True(h.PumpUntil(() => player.IsPlaying && player.HasAlbum), $"tytuł „{player.Title}”");
                Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));

                player.ShowCover(MainViewModel.DecodeCover(File.ReadAllBytes(Environment.GetEnvironmentVariable(CoverVariable)!)));
                player.ShuffleEnabled = true;
                SceneTests.SeekTo(h, 39);

                window.FreezeAnimationsForCapture();
                window.OpenSceneForCapture(showControls: true);
                Assert.True(window.Scene.Chrome.IsShown);
                Assert.True(player.IsPlaying);
                Assert.Equal(0, window.Scene.Disc.Angle);
                Save(window, Path.Combine(output, "plansza-aktywnosc-cewka.png"));

                now += SceneChrome.HideDelay + TimeSpan.FromSeconds(1);
                window.Scene.AdvanceTimers();
                Assert.False(window.Scene.Chrome.IsShown);
                Save(window, Path.Combine(output, "plansza-spoczynek-cewka.png"));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
