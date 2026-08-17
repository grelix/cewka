using Avalonia;
using Cewka.App.Services;
using Cewka.Platform;

namespace Cewka.App;

internal static class Program
{
    /// <summary>
    /// Name of the channel between copies of the application. Scoped to the user: the pipe
    /// namespace is shared by everyone logged in, and two people on one machine must not end
    /// up steering each other's music.
    /// </summary>
    private static string ChannelName => $"Cewka.{Environment.UserName}";

    // Avalonia requires an STA thread on Windows and must be initialised before
    // any control type is touched, so keep this method free of other work.
    [STAThread]
    public static void Main(string[] args)
    {
        SingleInstance? instance = null;

        if (SettingsStore.ReadSingleInstanceFlag())
        {
            instance = SingleInstance.TryAcquire(ChannelName);

            // Rola zajęta przez inną kopię: oddaj jej wiersz poleceń i zakończ pracę.
            // Nieudane przekazanie oznacza, że tamta kopia właśnie się zakończyła — wtedy
            // ta zwyczajnie otwiera własne okno.
            if (instance is null && SingleInstance.TryHandOff(ChannelName, args)) return;
        }

        App.Instance = instance;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(App.FontOptions)
            .LogToTrace();
}
