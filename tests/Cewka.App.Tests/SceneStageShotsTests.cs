using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.Views;
using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zrzuty tylko na żądanie: katalog wskazuje zmienna <c>CEWKA_ZRZUTY_SCENY</c>.</summary>
public sealed class SceneShotsFactAttribute : FactAttribute
{
    public SceneShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SceneStageShotsTests.OutputVariable)))
            Skip = $"Brak katalogu zrzutów ({SceneStageShotsTests.OutputVariable}).";
    }
}

/// <summary>
/// Zrzuty 1920 × 1080 do przeglądu sceny, w motywie ciemnym i jasnym: trzy wizualizacje z kartą
/// „Następny”, rząd okładek kolejki i koniec kolejki. Okładki są jednolitymi obrazami z testu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SceneStageShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_SCENY";

    private static readonly TimeSpan Now = TimeSpan.FromSeconds(1000);

    private static byte[] Cover(Color colour) => CoverTests.Png(800, 800, colour);

    private static void Save(MainWindow window, string name)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(Environment.GetEnvironmentVariable(OutputVariable)!, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static void Scenario(string name, SceneVisual visual, int tracks, Action<PlayerHarness, MainWindow> act)
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Full;
            s.SceneVisual = visual;
        });

        var window = new MainWindow(false, h.Player) { Width = 1920, Height = 1080 };
        window.Scene.Clock = () => Now;
        window.Show();

        try
        {
            var player = h.Player;
            player.OpenPaths(Enumerable.Range(0, tracks).Select(i =>
                h.TaggedTrack("zrzut" + i, "Utwór numer " + (i + 1), "Wykonawca przykładowy", "Album przykładowy", seconds: 120)));
            Assert.True(h.PumpUntil(() => player.IsPlaying && h.Engine.Duration > TimeSpan.Zero && player.HasAlbum));
            Assert.True(Ui.WaitUntil(() => player.CoverTask.IsCompleted));

            player.ReadLargeCover = (_, _) => MainViewModel.DecodeCover(Cover(Colors.IndianRed))!.Display;
            player.ShowCover(MainViewModel.DecodeCover(Cover(Colors.SteelBlue)));

            window.FreezeAnimationsForCapture();
            act(h, window);
            Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }

    private static void WithCard(PlayerHarness h, MainWindow window)
    {
        window.OpenSceneForCapture(showControls: false);
        SceneTests.SeekTo(h, 110);
        Assert.True(h.PumpUntil(() => h.Player.HasNextUp && h.Player.NextUpCoverTask.IsCompleted));
        Assert.True(Ui.WaitUntil(() => h.Player.LargeCoverTask.IsCompleted));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Scene.NextCard.IsVisible);
    }

    private static void WithRow(PlayerHarness h, MainWindow window)
    {
        window.OpenSceneForCapture(showControls: false);
        window.KeyPress(Key.Q, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Q, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Scene.IsQueueRowOpen);
        h.PumpUntil(() => false, 600);
    }

    private static void AtQueueEnd(PlayerHarness h, MainWindow window)
    {
        window.OpenSceneForCapture(showControls: false);
        h.Player.PlayItem(h.Player.Queue[1]);
        Assert.True(h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Duration > TimeSpan.Zero));
        SceneTests.SeekTo(h, 118);
        Assert.True(h.PumpUntil(() => h.Player.QueueEnded && !h.Player.IsPlaying, 15000));
    }

    [SceneShotsFact]
    public void ZrzutyEtapuWObuMotywach()
    {
        SceneTests.InLanguage("pl", () =>
        {
            Directory.CreateDirectory(Environment.GetEnvironmentVariable(OutputVariable)!);
            var before = App.Theme.Mode;

            try
            {
                foreach (var (mode, suffix) in new[] { (ThemeMode.Dark, "ciemny"), (ThemeMode.Light, "jasny") })
                {
                    App.Theme.SetMode(mode);
                    Dispatcher.UIThread.RunJobs();

                    Scenario("plyta-karta-" + suffix, SceneVisual.Disc, 4, WithCard);
                    Scenario("okladka-karta-" + suffix, SceneVisual.Cover, 4, WithCard);
                    Scenario("noc-karta-" + suffix, SceneVisual.Night, 4, WithCard);
                    Scenario("rzad-kolejki-" + suffix, SceneVisual.Disc, 12, WithRow);
                    Scenario("koniec-kolejki-" + suffix, SceneVisual.Disc, 2, AtQueueEnd);
                }
            }
            finally
            {
                App.Theme.SetMode(before);
            }
        });
    }
}
