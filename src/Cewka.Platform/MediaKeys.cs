using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Platform;

/// <summary>The four transport keys found on keyboards and headsets.</summary>
public enum MediaKey
{
    PlayPause,
    Next,
    Previous,
    Stop,
}

/// <summary>
/// Global handling of the multimedia keys, used only where the system media panel is missing.
///
/// <para>Gdy działa panel multimediów systemu (<see cref="SystemMediaControls"/>), Windows sam
/// kieruje klawisze do ostatnio aktywnej sesji odtwarzania i zaczep nie jest potrzebny. Zostaje
/// drogą zapasową: przechwytuje klawisz tylko wtedy, gdy Cewka gra albo jej okno jest na
/// wierzchu. W pozostałych przypadkach klawisz idzie dalej do innych programów.</para>
///
/// <para>Pułapka: zaczep niskiego poziomu system wywołuje w wątku, który go założył, i każde
/// naciśnięcie klawisza w całej sesji czeka na ten wątek. Dlatego zaczep ma własny wątek
/// z pętlą komunikatów. W wątku interfejsu dłuższe zajęcie okna opóźniałoby klawiaturę całego
/// systemu, a po przekroczeniu limitu czasu Windows zdejmuje zaczep bez powiadomienia.</para>
/// </summary>
public static unsafe class MediaKeys
{
    private const int WhKeyboardLowLevel = 13;
    private const int HcAction = 0;
    private const nint WmKeyDown = 0x0100;
    private const nint WmKeyUp = 0x0101;
    private const nint WmSysKeyDown = 0x0104;
    private const nint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0;

    private const uint VkMediaNextTrack = 0xB0;
    private const uint VkMediaPreviousTrack = 0xB1;
    private const uint VkMediaStop = 0xB2;
    private const uint VkMediaPlayPause = 0xB3;

    private static readonly object Gate = new();
    private static readonly MediaKeyFilter Filter = new();
    private static readonly uint ProcessId = (uint)Environment.ProcessId;

    private static Thread? _thread;
    private static uint _threadId;
    private static nint _hook;
    private static volatile bool _playing;

    /// <summary>Raised on the hook's own thread.</summary>
    public static event Action<MediaKey>? Pressed;

    /// <summary>False on systems where the keys are delivered some other way.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsInstalled => Volatile.Read(ref _hook) != nint.Zero;

    /// <summary>
    /// Whether the player is playing. Set by the application on every change; the hook reads it
    /// to decide whether a key press belongs to the player.
    /// </summary>
    public static bool Playing
    {
        get => _playing;
        set => _playing = value;
    }

    /// <summary>
    /// Whether the hook should be installed: only when the user wants the keys and the system
    /// media panel is not there to deliver them.
    /// </summary>
    public static bool ShouldHook(bool enabled, bool systemPanelActive) =>
        enabled && !systemPanelActive && IsSupported;

    /// <summary>
    /// Installs the hook on a thread of its own. Returns false when the platform does not
    /// support it or the system refused.
    /// </summary>
    public static bool Install()
    {
        if (!OperatingSystem.IsWindows()) return false;

        lock (Gate)
        {
            if (_thread is not null)
            {
                if (_thread.IsAlive) return IsInstalled;
                _thread = null;
            }

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                if (OperatingSystem.IsWindows()) Run(ready);
            })
            {
                IsBackground = true,
                Name = "Cewka.MediaKeys",
            };

            thread.Start();
            ready.Wait();

            if (!IsInstalled)
            {
                thread.Join();
                return false;
            }

            _thread = thread;
            return true;
        }
    }

    public static void Uninstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (Gate)
        {
            if (_thread is null) return;

            // Zaczep zdejmuje wątek, który go założył, po wyjściu z pętli komunikatów. Wątek,
            // który nie zdążył skończyć, zostaje zapamiętany, żeby Install nie założył obok
            // drugiego zaczepu.
            Native.PostThreadMessageW(_threadId, WmQuit, 0, 0);
            if (_thread.Join(TimeSpan.FromSeconds(1))) _thread = null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Run(ManualResetEventSlim ready)
    {
        nint hook;

        try
        {
            // Kolejka komunikatów wątku powstaje przy pierwszym zapytaniu o nią. Bez niej
            // polecenie zakończenia wysłane z Uninstall nie miałoby dokąd trafić.
            Native.Msg message;
            Native.PeekMessageW(&message, 0, 0, 0, PmNoRemove);

            _threadId = Native.GetCurrentThreadId();
            Filter.Reset();

            hook = Native.SetWindowsHookExW(
                WhKeyboardLowLevel, &OnKeyboardEvent, Native.GetModuleHandleW(null), 0);

            if (hook == nint.Zero)
            {
                Console.Error.WriteLine(
                    $"[cewka] nie udało się przejąć klawiszy multimedialnych (kod {Marshal.GetLastWin32Error()}).");
                return;
            }

            Volatile.Write(ref _hook, hook);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] klawisze multimedialne niedostępne: {ex.Message}");
            return;
        }
        finally
        {
            ready.Set();
        }

        try
        {
            // Zaczep niskiego poziomu dostaje zdarzenia w trakcie oczekiwania w GetMessage.
            Native.Msg message;
            while (Native.GetMessageW(&message, 0, 0, 0) > 0)
            {
            }
        }
        finally
        {
            // Własny uchwyt: _hook mógł już przejąć zaczep założony po tym.
            Native.UnhookWindowsHookEx(hook);
            if (Interlocked.CompareExchange(ref _hook, nint.Zero, hook) == hook) Filter.Reset();
        }
    }

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("windows")]
    private static nint OnKeyboardEvent(int code, nint message, nint data)
    {
        // Warunek jak w dokumentacji zaczepów: przy kodzie ujemnym nie wolno analizować
        // zdarzenia, tylko przekazać je dalej.
        if (code != HcAction) return Native.CallNextHookEx(_hook, code, message, data);

        try
        {
            var input = (KeyboardInput*)data;
            var key = ToMediaKey(input->VirtualKey);
            if (key is null) return Native.CallNextHookEx(_hook, code, message, data);

            var verdict = message switch
            {
                WmKeyDown or WmSysKeyDown => Filter.KeyDown(key.Value, ShouldCapture(), input->Time),
                WmKeyUp or WmSysKeyUp => Filter.KeyUp(key.Value),
                _ => KeyVerdict.PassOn,
            };

            if (verdict == KeyVerdict.PassOn) return Native.CallNextHookEx(_hook, code, message, data);
            if (verdict == KeyVerdict.SwallowAndRaise) Pressed?.Invoke(key.Value);

            // Wartość niezerowa zatrzymuje zdarzenie: klawisz należy do Cewki.
            return 1;
        }
        catch
        {
            // Wyjątek wypuszczony do systemu z wywołania zwrotnego kończy proces.
            return Native.CallNextHookEx(_hook, code, message, data);
        }
    }

    private static MediaKey? ToMediaKey(uint virtualKey) => virtualKey switch
    {
        VkMediaPlayPause => MediaKey.PlayPause,
        VkMediaNextTrack => MediaKey.Next,
        VkMediaPreviousTrack => MediaKey.Previous,
        VkMediaStop => MediaKey.Stop,
        _ => null,
    };

    /// <summary>
    /// Klawisz należy do Cewki, gdy ona gra albo jej okno jest na wierzchu. Inaczej Cewka
    /// wstrzymana w tle odbierałaby przeglądarce i innym odtwarzaczom każde naciśnięcie.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool ShouldCapture()
    {
        if (_playing) return true;

        var window = Native.GetForegroundWindow();
        if (window == nint.Zero) return false;

        Native.GetWindowThreadProcessId(window, out var owner);
        return owner == ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInformation;
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookExW(
            int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(Msg* message, nint window, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        public static extern int PeekMessageW(Msg* message, nint window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandleW(string? moduleName);
    }
}

/// <summary>What the hook does with one keyboard event.</summary>
internal enum KeyVerdict
{
    /// <summary>Hand the event on to other programs.</summary>
    PassOn,

    /// <summary>Stop the event without acting on it, as for an auto-repeat of a captured key.</summary>
    Swallow,

    /// <summary>Stop the event and act on it.</summary>
    SwallowAndRaise,
}

/// <summary>
/// Decides the fate of each press and release of a multimedia key.
///
/// <para>Decyzja zapada przy pierwszym wciśnięciu i obowiązuje do puszczenia klawisza:
/// powtórzenia z przytrzymania nie wywołują kolejnych poleceń, a puszczenie idzie tam, dokąd
/// poszło wciśnięcie. Inny program nie dostaje więc samego puszczenia klawisza.</para>
///
/// <para>Pułapka: zgubione puszczenie zostawiłoby klawisz na zawsze „wciśnięty”, a każde
/// następne naciśnięcie uchodziłoby za powtórzenie. Dlatego wciśnięcie po przerwie dłuższej niż
/// najdłuższe opóźnienie powtarzania klawiatury liczy się jako nowe.</para>
/// </summary>
internal sealed class MediaKeyFilter
{
    /// <summary>Longer than the slowest keyboard repeat delay Windows allows, which is one second.</summary>
    internal const uint FreshPressGapMilliseconds = 1500;

    private readonly bool?[] _held = new bool?[4];
    private readonly uint[] _lastDown = new uint[4];

    public KeyVerdict KeyDown(MediaKey key, bool capture, uint time)
    {
        var index = (int)key;
        var previous = _lastDown[index];
        _lastDown[index] = time;

        if (_held[index] is { } captured && unchecked(time - previous) <= FreshPressGapMilliseconds)
            return captured ? KeyVerdict.Swallow : KeyVerdict.PassOn;

        _held[index] = capture;
        return capture ? KeyVerdict.SwallowAndRaise : KeyVerdict.PassOn;
    }

    public KeyVerdict KeyUp(MediaKey key)
    {
        var index = (int)key;
        var captured = _held[index] == true;
        _held[index] = null;

        return captured ? KeyVerdict.Swallow : KeyVerdict.PassOn;
    }

    public void Reset()
    {
        Array.Clear(_held);
        Array.Clear(_lastDown);
    }
}
