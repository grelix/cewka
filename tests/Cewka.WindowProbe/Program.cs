using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App;
using Cewka.App.Controls;
using Cewka.App.ViewModels;
using Cewka.App.Views;
using Cewka.Audio.Devices;
using Cewka.Platform;

namespace Cewka.WindowProbe;

/// <summary>
/// Stawia okno główne i okno ustawień poza obszarem ekranów, bez aktywacji i bez przycisku na pasku
/// zadań, wysyła WM_NCHITTEST na środek każdego elementu paska tytułu i wypisuje wyniki wierszami
/// „okno|element|wynik”. Okna zamyka od razu po pomiarze.
/// </summary>
internal static class Program
{
    /// <summary>Położenie poza każdym ekranem, z dala od −32000, którym system oznacza okna zminimalizowane.</summary>
    private static readonly PixelPoint Offscreen = new(-20000, -20000);

    private static readonly PixelPoint SettingsOffscreen = new(-20000, -18000);

    private static int _exitCode;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Pomiar dotyczy wyłącznie Windows.");
            return 3;
        }

        // Ustawienia i kolejka w katalogu tymczasowym, dźwięk na wyjście puste.
        var root = Path.Combine(Path.GetTempPath(), "cewka-pomiar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppPaths.Redirect(root);
        AudioDevice.UseNullOutput = true;

        // Bez względu na wynik proces kończy się sam, żeby test nie wisiał na oknie.
        var watchdog = new Timer(_ => Environment.Exit(4), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

        AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .With(App.App.FontOptions)
            .SetupWithoutStarting();

        // Bez globalnego przechwytywania klawiszy multimedialnych.
        App.App.Settings.Current.MediaKeys = false;
        App.App.Settings.Current.CheckForUpdates = false;

        var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await MeasureAsync(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                _exitCode = 2;
            }
            finally { done.Cancel(); }
        });

        Dispatcher.UIThread.MainLoop(done.Token);
        watchdog.Dispose();

        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return _exitCode;
    }

    private static async Task MeasureAsync()
    {
        var main = new MainWindow(systemFrame: true) { AttachDesktopIntegration = false };
        PlaceOffscreen(main);

        try
        {
            await ShowHiddenAsync(main, Offscreen);

            Report("glowne", "styl", Native.GetWindowLong(Handle(main), Native.GWL_STYLE));
            ReportRect("glowne", main);
            ReportSystemMenuClose(main);

            var buttons = main.GetVisualDescendants().OfType<WindowButtons>().First(b => b.IsEffectivelyVisible);
            Probe(main, "Korektor", main.PanelToggle);
            Probe(main, "Kolejka", main.QueueToggle);
            Probe(main, "Motyw", main.ThemeButton);
            Probe(main, "Ustawienia", main.SettingsButton);
            Probe(main, "Minimalizuj", Named(buttons, "SquareMinimise"));
            Probe(main, "Maksymalizuj", Named(buttons, "SquareMaximise"));
            Probe(main, "Zamknij", Named(buttons, "SquareClose"));
            ProbeEmptyBar(main, main.TitleBar);
            Probe(main, "Odtworz", main.PlayButton);

            var settings = new SettingsWindow(new SettingsViewModel(App.App.Settings, (MainViewModel)main.DataContext!));
            PlaceOffscreen(settings, SettingsOffscreen);

            try
            {
                await ShowHiddenAsync(settings, SettingsOffscreen);

                Report("ustawienia", "styl", Native.GetWindowLong(Handle(settings), Native.GWL_STYLE));
                ReportRect("ustawienia", settings);

                var close = settings.GetVisualDescendants().OfType<WindowButtons>().First(b => b.IsEffectivelyVisible);
                Probe(settings, "Zamknij", Named(close, "SquareClose"));
                ProbeEmptyBar(settings, settings.TitleBar);
            }
            finally
            {
                settings.Close();
            }
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>
    /// Przygotowuje okno przed pokazaniem: ukrywa je w kompozytorze (DWMWA_CLOAK — okno działa,
    /// ale nie jest rysowane na pulpicie), ustawia poza ekranami, bez aktywacji i z właścicielem,
    /// czyli bez przycisku na pasku zadań. Sprawdza to na uchwycie systemowym i przerywa pomiar,
    /// zanim cokolwiek się pokaże.
    /// </summary>
    private static void PlaceOffscreen(Window window, PixelPoint? at = null)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = at ?? Offscreen;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;

        var handle = Handle(window);
        var cloak = 1;
        var cloaked = Native.DwmSetWindowAttribute(handle, Native.DWMWA_CLOAK, ref cloak, sizeof(int)) == 0
                      && Native.DwmGetWindowAttribute(handle, Native.DWMWA_CLOAKED, out var state, sizeof(int)) == 0
                      && state != 0;

        Native.GetWindowRect(handle, out var rect);
        var exStyle = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);

        if (handle == 0 || !cloaked || rect.Right > -10000 || rect.Bottom > -10000
            || Native.GetWindow(handle, Native.GW_OWNER) == 0
            || (exStyle & Native.WS_EX_APPWINDOW) != 0
            || Native.IsWindowVisible(handle))
            throw new InvalidOperationException(
                $"Okno nie jest ukryte, nie leży poza ekranem albo trafiłoby na pasek zadań: {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, ukryte {cloaked}, styl rozszerzony {exStyle:X}.");
    }

    /// <summary>
    /// Pokazuje ukryte okno i odstawia je z powrotem poza ekrany. Avalonia przy pierwszym pokazaniu
    /// ustala rozmiar przez SetWindowPlacement, a ta przesuwa okno na najbliższy ekran; ukrycie
    /// w kompozytorze sprawia, że i wtedy nic się nie pokazuje.
    /// </summary>
    private static async Task ShowHiddenAsync(Window window, PixelPoint at)
    {
        window.Show();
        EnsureCloaked(window);
        window.Position = at;
        await SettleAsync(window);
        window.Position = at;
        EnsureStillHidden(window);
    }

    private static void EnsureCloaked(Window window)
    {
        if (Native.DwmGetWindowAttribute(Handle(window), Native.DWMWA_CLOAKED, out var state, sizeof(int)) != 0 || state == 0)
            throw new InvalidOperationException("Okno po pokazaniu nie jest ukryte w kompozytorze.");
    }

    /// <summary>Po pokazaniu: okno nadal ukryte w kompozytorze i poza ekranami, inaczej koniec pomiaru.</summary>
    private static void EnsureStillHidden(Window window)
    {
        var handle = Handle(window);
        Native.GetWindowRect(handle, out var rect);
        var cloaked = Native.DwmGetWindowAttribute(handle, Native.DWMWA_CLOAKED, out var state, sizeof(int)) == 0 && state != 0;

        if (!cloaked || rect.Right > -10000 || rect.Bottom > -10000)
            throw new InvalidOperationException(
                $"Okno po pokazaniu nie jest ukryte albo leży na ekranie: {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, ukryte {cloaked}.");
    }

    /// <summary>Daje oknu czas na rozszerzenie obszaru klienta i pierwszy układ.</summary>
    private static async Task SettleAsync(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            await DelayAsync(25);
            window.UpdateLayout();
        }
    }

    private static Task DelayAsync(int milliseconds)
    {
        var finished = new TaskCompletionSource();
        DispatcherTimer.RunOnce(() => finished.SetResult(), TimeSpan.FromMilliseconds(milliseconds));
        return finished.Task;
    }

    private static Control Named(Control scope, string name) =>
        scope.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    private static void Probe(Window window, string element, Control control)
    {
        var centre = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        Report(WindowName(window), element, HitTest(window, centre));
    }

    /// <summary>Puste miejsce paska: w jednej czwartej szerokości, z dala od nazwy i przycisków.</summary>
    private static void ProbeEmptyBar(Window window, Control bar)
    {
        var point = bar.PointToScreen(new Point(bar.Bounds.Width / 4, bar.Bounds.Height / 2));
        Report(WindowName(window), "PustyPasek", HitTest(window, point));
    }

    private static string WindowName(Window window) => window is MainWindow ? "glowne" : "ustawienia";

    private static long HitTest(Window window, PixelPoint screen)
    {
        var lParam = (nint)(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF));
        return Native.SendMessage(Handle(window), Native.WM_NCHITTEST, 0, lParam);
    }

    private static nint Handle(Window window) => window.TryGetPlatformHandle()?.Handle ?? 0;

    private static void ReportRect(string window, Window w)
    {
        Native.GetWindowRect(Handle(w), out var rect);
        Console.WriteLine($"{window}|prostokat|{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        Console.WriteLine($"{window}|widoczne|{(Native.IsWindowVisible(Handle(w)) ? 1 : 0)}");
        Native.DwmGetWindowAttribute(Handle(w), Native.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        Console.WriteLine($"{window}|ukryte|{(cloaked != 0 ? 1 : 0)}");
    }

    /// <summary>
    /// Stan „Zamknij” w menu systemowym (0 = dostępne, −1 = brak menu): po pokazaniu okna oraz po
    /// wyszarzeniu go tak, jak robi to Avalonia, i otwarciu menu (WM_INITMENU).
    /// </summary>
    private static void ReportSystemMenuClose(Window window)
    {
        var handle = Handle(window);
        Report("glowne", "menuZamknij", CloseState(handle));

        var menu = Native.GetSystemMenu(handle, false);
        if (menu != 0)
        {
            Native.EnableMenuItem(menu, Native.SC_CLOSE, Native.MF_GRAYED | Native.MF_DISABLED);
            Native.SendMessage(handle, Native.WM_INITMENU, menu, 0);
        }

        Report("glowne", "menuZamknijPoOtwarciu", CloseState(handle));
    }

    private static long CloseState(nint handle)
    {
        var menu = Native.GetSystemMenu(handle, false);
        if (menu == 0) return -1;
        var state = Native.GetMenuState(menu, Native.SC_CLOSE, Native.MF_BYCOMMAND);
        return state == uint.MaxValue ? -1 : state & (Native.MF_GRAYED | Native.MF_DISABLED);
    }

    private static void Report(string window, string element, long value) =>
        Console.WriteLine($"{window}|{element}|{value}");

    private static class Native
    {
        public const uint WM_NCHITTEST = 0x0084;
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const uint GW_OWNER = 4;

        public const int DWMWA_CLOAK = 13;
        public const int DWMWA_CLOAKED = 14;

        public const uint WM_INITMENU = 0x0116;
        public const uint SC_CLOSE = 0xF060;
        public const uint MF_BYCOMMAND = 0x0000;
        public const uint MF_GRAYED = 0x0001;
        public const uint MF_DISABLED = 0x0002;

        [DllImport("user32.dll")]
        public static extern nint GetSystemMenu(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

        [DllImport("user32.dll")]
        public static extern uint GetMenuState(nint menu, uint id, uint flags);

        [DllImport("user32.dll")]
        public static extern int EnableMenuItem(nint menu, uint id, uint flags);

        [DllImport("user32.dll")]
        public static extern nint GetWindow(nint hWnd, uint command);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        public static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(nint hWnd, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hWnd, out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(nint hWnd);
    }
}
