using Avalonia;
using Cewka.App.Services;
using Cewka.Platform;

namespace Cewka.App;

internal static class Program
{
    /// <summary>
    /// Gdzie kopie programu się szukają. Osobno dla każdego użytkownika, bo przestrzeń nazw
    /// potoków w Windows jest wspólna dla wszystkich zalogowanych.
    /// </summary>
    private static InstanceAddress Address => InstanceAddress.Default($"Cewka.{Environment.UserName}");

    // Avalonia requires an STA thread on Windows and must be initialised before
    // any control type is touched, so keep this method free of other work.
    [STAThread]
    public static void Main(string[] args)
    {
        // Raz, przed oboma odbiorcami: działającą już kopią i własnym oknem.
        args = CommandLinePaths.ToAbsolute(args);

        SingleInstance? instance = null;

        if (SettingsStore.ReadSingleInstanceFlag())
        {
            var claim = SingleInstance.Start(Address, args);

            // Rola była zajęta i tamta kopia przyjęła wiersz poleceń — ta nie ma nic do roboty.
            if (claim.HandedOver) return;

            instance = claim.Instance;
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
