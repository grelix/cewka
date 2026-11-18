using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Views;
using Cewka.Audio;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Cewka.Audio.Tests.Engine;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zrzuty toru sygnału tylko na żądanie: katalog wyjściowy w zmiennej środowiska.</summary>
public sealed class SignalPathShotsFactAttribute : FactAttribute
{
    public SignalPathShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SignalPathShotsTests.OutputVariable)))
            Skip = $"Brak katalogu zrzutów toru sygnału ({SignalPathShotsTests.OutputVariable}).";
    }
}

/// <summary>
/// Okno główne z otwartym okienkiem toru sygnału, w motywie ciemnym i jasnym, bez okna na ekranie: plik
/// z konwersją częstotliwości, normalizacją i korektorem oraz plik grany bez zmian. Do przeglądu wyglądu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SignalPathShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_TORU";

    private sealed class FixedGain(TrackGain gain) : ITrackGainSource
    {
        public TrackGain? GetTrackGain(QueueEntry entry) => gain;
    }

    private static string Output => Environment.GetEnvironmentVariable(OutputVariable)!;

    [SignalPathShotsFact]
    public void ZrzutyToruSygnalu() => Ui.Run(() =>
    {
        var language = Strings.Current.CurrentCode;
        var level = AudioQuality.Resampling;
        var before = App.Theme.Mode;

        try
        {
            Strings.Current.SetLanguage("pl");

            foreach (var (theme, suffix) in new[] { (ThemeMode.Dark, "ciemny"), (ThemeMode.Light, "jasny") })
            {
                App.Theme.SetMode(theme);
                Dispatcher.UIThread.RunJobs();

                Shot(processed: true, Path.Combine(Output, $"tor-sygnalu-{suffix}.png"));
                Shot(processed: false, Path.Combine(Output, $"tor-sygnalu-bez-zmian-{suffix}.png"));
            }
        }
        finally
        {
            App.Theme.SetMode(before);
            AudioQuality.Resampling = level;
            Strings.Current.SetLanguage(language);
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static void Shot(bool processed, string path)
    {
        App.Settings.Current.Window = null;
        using var h = new PlayerHarness(s =>
        {
            s.Language = "pl";
            s.QueuePinned = false;
            s.WindowControls = WindowControlsPosition.Right;
            s.Effects = EffectsMode.Reduced;
        });

        var sourceRate = processed ? 44100 : PlayerHarness.Rate;
        h.Engine.OpenDecoder = (_, rate, channels) =>
            new SyntheticDecoder((long)(540 * rate), 0.1f, channels, rate) { SourceSampleRate = sourceRate };

        h.Player.Volume = 1;
        h.Player.LimiterEnabled = false;
        h.Player.EqualiserEnabled = false;
        if (processed)
        {
            h.Player.SetResamplerQuality(ResamplerQuality.High);
            h.Engine.Graph.GainSource = new FixedGain(new TrackGain(-3.2, -14, TrackGainKind.Album));
            h.Player.EqualiserMode = EqualiserMode.Graphic;
            h.Player.EqualiserEnabled = true;
            h.Player.Bands[1].Gain = 3;
            h.Player.Bands[8].Gain = -2;
            h.Player.LimiterEnabled = true;
        }

        var window = new MainWindow(false, h.Player) { Width = 1180, Height = 520, AttachDesktopIntegration = false };
        window.Show();
        try
        {
            h.OpenPaths([h.TaggedTrack("a", "Rhapsody in Blue", "George Gershwin & Paul Whiteman Orchestra", "1924", seconds: 540)]);
            Assert.True(h.PumpUntil(() => h.Engine.SignalPath.Entry is not null && h.Player.HasFormatBadge));
            h.Player.Pause();
            h.Player.IsSignalPathOpen = true;

            Save(window, path);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Save(MainWindow window, string path)
    {
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        window.FreezeAnimationsForCapture();
        for (var i = 0; i < 6; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new PngBitmapEncoderOptions());
    }
}
