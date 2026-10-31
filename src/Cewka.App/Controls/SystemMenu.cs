using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Cewka.App.Controls;

/// <summary>
/// Menu systemowe okna z ramką systemową Windows bez paska tytułu.
/// <para>
/// Ramka bez paska tytułu nie ma menu systemowego (WS_SYSMENU), a bez niego system nie uznaje
/// przycisku maksymalizacji za dostępny i nie pokazuje nad nim układów przyciągania. Avalonia przy
/// każdym rozszerzeniu obszaru klienta wyszarza jednak w tym menu „Zamknij”, więc menu po prawym
/// kliknięciu w pasek albo Alt+Spacja pokazywałoby zamykanie jako niedostępne. Okno przywraca je
/// po każdej zmianie ramki i przed otwarciem menu.
/// </para>
/// </summary>
internal static class SystemMenu
{
    /// <summary>Styl okna Win32 z menu systemowym.</summary>
    internal const uint WsSysMenu = 0x00080000;

    private const uint WmWindowPosChanged = 0x0047;
    private const uint WmInitMenu = 0x0116;
    private const uint ScClose = 0xF060;
    private const uint MfByCommandEnabled = 0x0000;

    public static void Attach(Window window)
    {
        Win32Properties.AddWindowStylesCallback(window, (style, extended) => (style | WsSysMenu, extended));
        Win32Properties.AddWndProcHookCallback(window, OnMessage);
    }

    private static nint OnMessage(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // Avalonia wyszarza „Zamknij” tuż przed SetWindowPos ze zmianą ramki, która zawsze kończy
        // się WM_WINDOWPOSCHANGED; wiadomość idzie dalej bez zmian.
        if (msg is WmWindowPosChanged or WmInitMenu)
        {
            var menu = GetSystemMenu(hWnd, false);
            if (menu != 0) EnableMenuItem(menu, ScClose, MfByCommandEnabled);
        }

        return 0;
    }

    [DllImport("user32.dll")]
    private static extern nint GetSystemMenu(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll")]
    private static extern int EnableMenuItem(nint menu, uint id, uint flags);
}
