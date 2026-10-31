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
    /// <para>Pułapka: sam styl kontrolek nie wystarcza. Avalonia osobno pyta system o domyślną
    /// rodzinę pisma i w systemie bez fontów (goły kontener) przerywa działanie komunikatem
    /// „Default font family name can't be null or empty”. Narzędzie zrzutów używa tych samych
    /// ustawień.</para>
    /// </summary>
    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = "avares://Cewka/Assets/Fonts#Cantarell",
    };

    private static MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;

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

    /// <summary>
    /// Ostatnia siatka dla wyjątków z obsługi zdarzeń interfejsu. Każdy trafia do dziennika;
    /// zatrzymywany jest tylko błąd warstwy dźwięku, bo nie narusza stanu programu, a reszta
    /// kończy program, żeby nie zostawić go w stanie niespójnym.
    /// </summary>
    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Console.Error.WriteLine($"[cewka] nieobsłużony wyjątek: {e.Exception}");

        if (e.Exception is Cewka.Audio.AudioException) e.Handled = true;
    }

    // ---------- Zakończenie z zewnątrz ----------

    private static readonly List<IDisposable> SignalHandlers = [];

    /// <summary>
    /// Zamyka program zwykłą drogą, gdy system każe mu skończyć (wylogowanie, koniec sesji).
    /// Bez tego proces ginąłby od razu, a kolejka i geometria okna zapisują się przy zamknięciu
    /// okna. W Windows te sygnały dostają tylko programy konsolowe.
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
        // Ścieżki przysłane w trakcie budowania okna czekały w kanale; trafiają do kolejki
        // dopiero po jego otwarciu, bo Post wykona się już w pętli komunikatów.
        Instance?.Attach(paths => Dispatcher.UIThread.Post(() => OnPathsHandedOver(paths)));
    }

    private static bool _closing;

    /// <summary>
    /// Przestaje przyjmować ścieżki od innych kopii, gdy okno zaczyna się zamykać. Kopia
    /// uruchomiona w trakcie zapisu dostaje odmowę i czeka na rolę, zamiast oddać plik oknu,
    /// które go już nie doda.
    /// </summary>
    internal static void BeginClosing()
    {
        _closing = true;
        Instance?.StopListening();
    }

    /// <summary>
    /// Oddaje rolę działającej kopii dopiero po zapisaniu kolejki i ustawień, bo następna kopia
    /// czyta je z dysku.
    /// </summary>
    internal static void ReleaseSingleInstance()
    {
        _closing = true;

        var instance = Instance;
        Instance = null;
        instance?.Dispose();
    }

    /// <summary>
    /// Dokłada pliki od innej kopii do kolejki i wysuwa okno. Dokłada, bo zaznaczenie kilkunastu
    /// plików uruchamia kilkanaście kopii po jednej ścieżce.
    /// </summary>
    private static void OnPathsHandedOver(string[] paths)
    {
        if (_mainWindow is null || _closing) return;

        _mainWindow.ReceivePaths(paths);

        if (_mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            _mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;

        _mainWindow.Activate();
    }

    // ---------- Klawisze multimedialne ----------

    /// <summary>
    /// Zaczep zakłada dopiero okno, po próbie podłączenia panelu systemowego: gdy panel działa,
    /// zaczep nie jest potrzebny.
    /// </summary>
    private void AttachMediaKeys()
    {
        MediaKeys.Pressed += key => Dispatcher.UIThread.Post(() => _mainWindow?.HandleMediaKey(key));
    }

    /// <summary>
    /// Installs or removes the keyboard hook to match the setting and the state of the system
    /// media panel. Called from the settings window, so the change takes effect on the spot.
    /// </summary>
    public static void ApplyMediaKeysSetting()
    {
        if (MediaKeys.ShouldHook(Settings.Current.MediaKeys, MediaPanelActive)) MediaKeys.Install();
        else MediaKeys.Uninstall();
    }

    /// <summary>
    /// Dla narzędzia zrzutów, które buduje aplikację bez cyklu życia pulpitu.
    /// </summary>
    /// <param name="startupPaths">
    /// Pliki do wczytania, jak podane w wierszu polecenia.
    /// </param>
    public void InitialiseServicesForHeadless(SettingsStore settings, string[]? startupPaths = null)
    {
        Settings = settings;
        Theme = new ThemeManager(this, settings.Current.Theme);
        Localisation.Strings.Current.SetLanguage(settings.Current.Language);

        if (startupPaths is { Length: > 0 }) StartupPaths = startupPaths;
    }
}
