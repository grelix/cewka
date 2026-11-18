using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Zrzuty korektora parametrycznego tylko na żądanie: katalog wyjściowy w zmiennej środowiska.</summary>
public sealed class ParametricShotsFactAttribute : FactAttribute
{
    public ParametricShotsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ParametricShotsTests.OutputVariable)))
            Skip = $"Brak katalogu zrzutów korektora ({ParametricShotsTests.OutputVariable}).";
    }
}

/// <summary>
/// Okno „Korektor i efekty” i strona Ustawień w trybie parametrycznym z zestawem pasm, w motywie
/// ciemnym i jasnym, bez okna na ekranie. Do przeglądu wyglądu, nie do README.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ParametricShotsTests
{
    public const string OutputVariable = "CEWKA_ZRZUTY_KOREKTORA";

    private const string Preset = """
        Preamp: -6.1 dB
        Filter 1: ON LSC Fc 105 Hz Gain 6.4 dB Q 0.70
        Filter 2: ON PK Fc 8800 Hz Gain 5.1 dB Q 1.42
        Filter 3: ON HSC Fc 10000 Hz Gain -2.1 dB Q 0.70
        Filter 4: ON PK Fc 5332 Hz Gain -1.1 dB Q 5.75
        Filter 5: ON PK Fc 2690 Hz Gain -1.8 dB Q 2.39
        Filter 6: ON PK Fc 210 Hz Gain -1.4 dB Q 0.69
        Filter 7: ON PK Fc 3446 Hz Gain 2.3 dB Q 3.6
        Filter 8: ON PK Fc 1306 Hz Gain -0.9 dB Q 2.15
        Filter 9: ON PK Fc 46 Hz Gain 0.8 dB Q 1.23
        Filter 10: OFF PK Fc 250 Hz Gain -3 dB Q 2
        """;

    private static string Output => Environment.GetEnvironmentVariable(OutputVariable)!;

    [ParametricShotsFact]
    public void ZrzutyKorektoraParametrycznego() => Ui.Run(() =>
    {
        foreach (var (theme, suffix) in new[] { (ThemeMode.Dark, "ciemny"), (ThemeMode.Light, "jasny") })
        {
            var before = App.Theme.Mode;
            try
            {
                App.Theme.SetMode(theme);
                Dispatcher.UIThread.RunJobs();

                using var h = new PlayerHarness(s =>
                {
                    s.Language = "pl";
                    s.WindowControls = WindowControlsPosition.Right;
                });

                var preset = Path.Combine(h.Directory, "HD650 ParametricEQ.txt");
                File.WriteAllText(preset, Preset);
                h.Player.ImportEqualiser(preset);

                App.Settings.Current.SoundWindow = null;
                var window = new SoundWindow(h.Player) { Width = 900, Height = 470 };
                window.Show();
                try
                {
                    Save(window, Path.Combine(Output, $"korektor-parametryczny-{suffix}.png"));
                    window.Width = window.MinWidth;
                    Save(window, Path.Combine(Output, $"korektor-parametryczny-waski-{suffix}.png"));
                }
                finally
                {
                    window.Close();
                }

                var settings = new SettingsViewModel(h.Settings, h.Player);
                var settingsWindow = new SettingsWindow(settings) { Width = 900, Height = 1100 };
                settingsWindow.Show();
                try
                {
                    settings.SelectSection(settings.Processing);
                    Save(settingsWindow, Path.Combine(Output, $"ustawienia-korektor-{suffix}.png"));
                }
                finally
                {
                    settingsWindow.Close();
                }
            }
            finally
            {
                App.Theme.SetMode(before);
                Dispatcher.UIThread.RunJobs();
            }
        }
    });

    private static void Save(TopLevel window, string path)
    {
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
