using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Controls;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.Views;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zrzuty Widma, Fali, Horyzontu, Zorzy i pierścienia płyty bez okna na ekranie; tylko gdy ustawiono katalog.</summary>
[Collection(UiCollection.Name)]
public sealed class SceneAudioShotsTests
{
    private static readonly TimeSpan Now = TimeSpan.FromSeconds(1000);

    private static void Save(MainWindow window, string name)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(Environment.GetEnvironmentVariable(SceneStageShotsTests.OutputVariable)!, name + ".png"),
            new PngBitmapEncoderOptions());
    }

    private static FakeSceneAudio Music()
    {
        var source = new FakeSceneAudio { Mono = false, Levels = new ChannelLevels(0.16f, 0.12f, 0.5f, 0.4f) };
        for (var i = 0; i < SpectrumBars.Count; i++)
            source.Spectrum[i] = (float)(0.45 * Math.Exp(-i / 22.0) * (0.65 + 0.35 * Math.Sin(i * 0.9)));
        return source;
    }

    /// <summary>Okładka z dwóch barw: na zrzutach widać, że gradient słupków bierze obie.</summary>
    internal static byte[] TwoColourCover(Color first, Color second)
    {
        const int size = 800;
        using var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(first), new Rect(0, 0, size, size / 2.0));
            context.FillRectangle(new SolidColorBrush(second), new Rect(0, size / 2.0, size, size / 2.0));
        }

        using var stream = new MemoryStream();
#pragma warning disable CS0618 // zapis PNG z domyślnymi ustawieniami wystarcza do testu
        target.Save(stream);
#pragma warning restore CS0618
        return stream.ToArray();
    }

    private static FileEnvelope Music30()
    {
        var builder = new EnvelopeBuilder();
        const int rate = 48000;
        var buffer = new float[rate * 2];
        for (var second = 0; second < 120; second++)
        {
            var level = (float)(0.25 + 0.6 * Math.Abs(Math.Sin(second * 0.11)) * (second % 30 < 24 ? 1 : 0.3));
            for (var i = 0; i < rate; i++)
                buffer[2 * i] = buffer[2 * i + 1] = level * (float)Math.Sin(i * 0.05) * (0.6f + 0.4f * (float)Math.Sin(i * 0.0004));
            builder.Add(buffer, rate, 2);
        }

        return builder.Build()!;
    }

    private static void Shot(string name, SceneVisual visual, Action<AppSettings> configure, bool envelope = false, bool aurora = false)
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.QueuePinned = false;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Full;
            s.SceneVisual = visual;
            configure(s);
        });

        h.Player.SceneAudioData = Music();

        // Headless nie ma GPU ani baterii, więc stan wymagany przez Zorzę ustawia się jawnie.
        if (aurora)
        {
            h.Player.BatteryProbe = () => false;
            h.Player.PinGpu(true);
        }
        if (envelope)
        {
            var music = Music30();
            h.Measurement = (_, _) => new LoudnessMeasurement(-18, 0.9, music);
        }

        var window = new MainWindow(false, h.Player) { Width = 1920, Height = 1080 };
        window.Scene.Clock = () => Now;
        window.Show();

        try
        {
            h.OpenPaths([h.TaggedTrack("zrzut", "Utwór przykładowy", "Wykonawca przykładowy", "Album przykładowy", seconds: 120)]);
            Assert.True(h.PumpUntil(() => h.Player.IsPlaying && h.Engine.Duration > TimeSpan.Zero));
            Assert.True(Ui.WaitUntil(() => h.Player.CoverTask.IsCompleted));
            h.Player.ShowCover(ViewModels.MainViewModel.DecodeCover(TwoColourCover(Colors.DarkOrange, Colors.Teal)));

            window.FreezeAnimationsForCapture();
            window.OpenSceneForCapture(showControls: false);
            if (envelope)
            {
                Assert.True(h.PumpUntil(() => h.Player.SceneEnvelope is not null), "obwiednia nie została policzona");
                h.Player.Progress = 0.4;
                h.PumpUntil(() => h.Player.Progress > 0.35);
            }

            foreach (var view in new SceneAudioView[] { window.Scene.AudioView, window.Scene.RingView })
                for (var i = 0; i < 90; i++) view.Step(1.0 / 60);

            // Zorza: ta sama klasa rysująca co w scenie, w stałym punkcie czasu i przy ustalonych pasmach.
            if (aurora)
                for (var i = 0; i < 180; i++) window.Scene.AuroraView.Step(1.0 / 60);

            Save(window, name);
        }
        finally
        {
            window.Close();
        }
    }

    [SceneShotsFact]
    public void ZrzutyWizualizacjiDzwieku() => SceneTests.InLanguage("pl", () =>
    {
        Directory.CreateDirectory(Environment.GetEnvironmentVariable(SceneStageShotsTests.OutputVariable)!);
        Shot("widmo-slupki", SceneVisual.Spectrum, s => s.SpectrumShape = SpectrumShape.Bars);
        Shot("widmo-krzywa", SceneVisual.Spectrum, s => s.SpectrumShape = SpectrumShape.Curve);
        Shot("fala-linia", SceneVisual.Waveform, s => s.WaveformKind = WaveformKind.Line);
        Shot("fala-stereo", SceneVisual.Waveform, s => s.WaveformKind = WaveformKind.Stereo);
        Shot("horyzont", SceneVisual.Horizon, _ => { }, envelope: true);
        Shot("plyta-pierscien", SceneVisual.Disc, s => s.DiscRing = true);
        Shot("zorza", SceneVisual.Aurora, _ => { }, aurora: true);
    });
}
