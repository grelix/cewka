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
/// Global handling of the multimedia keys.
///
/// <para><b>Dlaczego zaczep niskiego poziomu, a nie <c>RegisterHotKey</c>.</b> Rejestracja
/// skrótu jest wyłączna: pierwszy program, który poprosi o klawisz odtwarzania, odbiera go
/// wszystkim pozostałym na stałe, a jeśli zakończy się awaryjnie, klawisz bywa zablokowany do
/// wylogowania. Zaczep klawiatury jest zdejmowany razem z procesem i pozwala zdecydować przy
/// każdym naciśnięciu, czy klawisz zostaje przechwycony.</para>
///
/// <para><b>Co widzi zaczep.</b> Wywołanie zwrotne dostaje wszystkie zdarzenia klawiatury
/// w systemie, dlatego sprowadza się do porównania jednego kodu i natychmiastowego oddania
/// sterowania. Klawisze inne niż multimedialne przechodzą dalej bez żadnej zwłoki. Przechwycone
/// zostają wyłącznie te cztery, na które program reaguje — pozostałe programy dostają resztę.</para>
///
/// <para><b>Wątek.</b> Zaczep niskiego poziomu wymaga wątku z pętlą komunikatów i wywołuje
/// zwrotnie właśnie ten wątek. Instalacja musi więc nastąpić z wątku interfejsu.</para>
/// </summary>
public static unsafe class MediaKeys
{
    private const int WhKeyboardLowLevel = 13;
    private const int HcAction = 0;
    private const nint WmKeyDown = 0x0100;
    private const nint WmSysKeyDown = 0x0104;

    private const uint VkMediaNextTrack = 0xB0;
    private const uint VkMediaPreviousTrack = 0xB1;
    private const uint VkMediaStop = 0xB2;
    private const uint VkMediaPlayPause = 0xB3;

    private static nint _hook;

    /// <summary>Raised on the interface thread, from inside the keyboard hook.</summary>
    public static event Action<MediaKey>? Pressed;

    /// <summary>False on systems where the keys are delivered some other way.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsInstalled => _hook != nint.Zero;

    /// <summary>
    /// Installs the hook. Must be called from the thread that runs the message loop.
    /// Returns false when the platform does not support it or the system refused.
    /// </summary>
    public static bool Install()
    {
        if (!OperatingSystem.IsWindows() || _hook != nint.Zero) return _hook != nint.Zero;

        try
        {
            _hook = Native.SetWindowsHookExW(
                WhKeyboardLowLevel, &OnKeyboardEvent, Native.GetModuleHandleW(null), 0);

            if (_hook == nint.Zero)
                Console.Error.WriteLine(
                    $"[cewka] nie udało się przejąć klawiszy multimedialnych (kod {Marshal.GetLastWin32Error()}).");

            return _hook != nint.Zero;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] klawisze multimedialne niedostępne: {ex.Message}");
            return false;
        }
    }

    public static void Uninstall()
    {
        if (!OperatingSystem.IsWindows() || _hook == nint.Zero) return;

        Native.UnhookWindowsHookEx(_hook);
        _hook = nint.Zero;
    }

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("windows")]
    private static nint OnKeyboardEvent(int code, nint message, nint data)
    {
        // Warunek jak w dokumentacji zaczepów: przy kodzie ujemnym nie wolno analizować
        // zdarzenia, tylko przekazać je dalej.
        if (code != HcAction || (message != WmKeyDown && message != WmSysKeyDown))
            return Native.CallNextHookEx(_hook, code, message, data);

        try
        {
            var key = ((KeyboardInput*)data)->VirtualKey switch
            {
                VkMediaPlayPause => (MediaKey?)MediaKey.PlayPause,
                VkMediaNextTrack => MediaKey.Next,
                VkMediaPreviousTrack => MediaKey.Previous,
                VkMediaStop => MediaKey.Stop,
                _ => null,
            };

            if (key is null) return Native.CallNextHookEx(_hook, code, message, data);

            Pressed?.Invoke(key.Value);

            // Wartość niezerowa zatrzymuje zdarzenie: inaczej klawisz zadziałałby również
            // w innym odtwarzaczu działającym w tle.
            return 1;
        }
        catch
        {
            // Wyjątek wypuszczony do systemu z wywołania zwrotnego kończy proces.
            return Native.CallNextHookEx(_hook, code, message, data);
        }
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
        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookExW(
            int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandleW(string? moduleName);
    }
}
