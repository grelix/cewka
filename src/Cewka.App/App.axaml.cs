using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Cewka.App.Services;
using Cewka.App.Views;
using Cewka.Platform;

namespace Cewka.App;

public partial class App : Application
{
    /// <summary>Settings shared by the whole application; created once at start-up.</summary>
    public static SettingsStore Settings { get; private set; } = null!;

    /// <summary>Drives the light/dark palette.</summary>
    public static ThemeManager Theme { get; private set; } = null!;

    /// <summary>Files and folders named on the command line.</summary>
    public static string[] StartupPaths { get; private set; } = [];

    /// <summary>
    /// Set by <see cref="Program"/> when this copy took the role of the one running instance.
    /// Null when the setting is off, or when claiming it failed.
    /// </summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>
    /// Whether the system media panel took. Shown in the settings, because there is no other
    /// way to tell a panel that failed to attach from one the user simply has not looked at.
    /// </summary>
    public static bool MediaPanelActive { get; internal set; }

    /// <summary>
    /// Ustawienia fontów przekazywane Avalonii przy budowaniu aplikacji.
    ///
    /// <para>Program niesie własny font i przypisuje go kontrolkom w arkuszu stylów, ale to nie
    /// wystarcza: Avalonia osobno pyta system o <em>domyślną</em> rodzinę pisma i bez odpowiedzi
    /// przerywa działanie komunikatem „Default font family name can't be null or empty".
    /// Na pulpicie fonty są zawsze, więc nie wychodziło to nigdy — wyszło dopiero przy próbnej
    /// instalacji w gołym kontenerze, gdzie nie ma żadnego. Skoro cała zawartość programu jest
    /// w jednym pliku, to i pismo nie ma powodu pochodzić skądinąd.</para>
    ///
    /// <para>Ta sama wartość obowiązuje narzędzie zrzutów, które buduje aplikację samo. Inaczej
    /// obrazy odniesienia powstawałyby przy innych ustawieniach niż te, z którymi program
    /// naprawdę działa.</para>
    /// </summary>
    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = "avares://Cewka/Assets/Fonts#Cantarell",
    };

    private static MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = new SettingsStore();
        Theme = new ThemeManager(this, Settings.Current.Theme);

        // Język przed utworzeniem okna: teksty pobierane są przez wiązania, ale ustawienie
        // go wcześniej oszczędza jedno pełne odświeżenie interfejsu przy starcie.
        Localisation.Strings.Current.SetLanguage(Settings.Current.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ścieżki z wiersza poleceń: podstawa działania polecenia „Otwórz za pomocą"
            // i skojarzeń plików rejestrowanych w oknie ustawień.
            StartupPaths = desktop.Args ?? [];

            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += (_, _) =>
            {
                MediaKeys.Uninstall();
                Settings.SaveNow();
            };

            AttachSingleInstance();
            AttachMediaKeys();
            AttachShutdownSignals(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ---------- Zakończenie z zewnątrz ----------

    private static readonly List<IDisposable> SignalHandlers = [];

    /// <summary>
    /// Closes the application in an orderly way when the system asks it to stop.
    ///
    /// <para>Wylogowanie albo zamknięcie sesji wysyła programowi sygnał zakończenia. Bez
    /// obsługi proces ginie natychmiast, a że kolejka i geometria okna zapisywane są przy
    /// zamykaniu okna, przepadałyby za każdym razem. Tutaj sygnał jest wstrzymywany, a okno
    /// zamyka się normalną drogą — dokładnie tak, jakby użytkownik nacisnął krzyżyk.</para>
    ///
    /// <para>W systemie Windows sygnały te przychodzą tylko do programów konsolowych, więc
    /// rejestracja pozostaje tam bez skutku i nic nie kosztuje.</para>
    /// </summary>
    private static void AttachShutdownSignals(IClassicDesktopStyleApplicationLifetime desktop)
    {
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            try
            {
                SignalHandlers.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Nie każdy sygnał jest dostępny na każdej platformie; brak jednego z nich
                // nie jest powodem, żeby zrezygnować z pozostałych.
            }
        }
    }

    // ---------- Przekazywanie ścieżek z kolejnych uruchomień ----------

    private void AttachSingleInstance()
    {
        if (Instance is null) return;

        Instance.PathsReceived += paths => Dispatcher.UIThread.Post(() => OnPathsHandedOver(paths));
        Instance.StartListening();
    }

    /// <summary>
    /// Adds files sent by another copy to the queue and brings the window forward.
    /// <para>
    /// Appending rather than replacing is deliberate: selecting a dozen files in the file
    /// manager starts a dozen copies, each handing over one path. Replacing the queue would
    /// leave only whichever of them happened to arrive last.
    /// </para>
    /// </summary>
    private static void OnPathsHandedOver(string[] paths)
    {
        if (_mainWindow is null) return;

        _mainWindow.ReceivePaths(paths);

        if (_mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            _mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;

        _mainWindow.Activate();
    }

    // ---------- Klawisze multimedialne ----------

    private void AttachMediaKeys()
    {
        MediaKeys.Pressed += key => Dispatcher.UIThread.Post(() => _mainWindow?.HandleMediaKey(key));
        ApplyMediaKeysSetting();
    }

    /// <summary>
    /// Installs or removes the keyboard hook to match the setting. Called from the settings
    /// window, so the change takes effect on the spot rather than at the next start.
    /// </summary>
    public static void ApplyMediaKeysSetting()
    {
        if (Settings.Current.MediaKeys) MediaKeys.Install();
        else MediaKeys.Uninstall();
    }

    /// <summary>
    /// Used by the snapshot tool, which sets up the application without a desktop
    /// lifetime and therefore never reaches the branch above.
    /// </summary>
    /// <param name="startupPaths">
    /// Files the window should load, as if they had been named on the command line. The
    /// snapshot tool uses it to photograph the interface with a real track in it rather than
    /// with the empty queue — the same path a file opened from the file manager takes.
    /// </param>
    public void InitialiseServicesForHeadless(SettingsStore settings, string[]? startupPaths = null)
    {
        Settings = settings;
        Theme = new ThemeManager(this, settings.Current.Theme);
        Localisation.Strings.Current.SetLanguage(settings.Current.Language);

        if (startupPaths is { Length: > 0 }) StartupPaths = startupPaths;
    }
}
