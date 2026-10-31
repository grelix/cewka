using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cewka.Platform.Linux;

namespace Cewka.Platform;

/// <summary>
/// Czy system ma włączone animacje interfejsu: w Windows „Pokaż animacje w systemie Windows”,
/// w GNOME klucz <c>org.gnome.desktop.interface enable-animations</c> odczytany przez portal.
/// Wyłączenie ich w systemie to prośba o ograniczenie ruchu także w programach.
/// </summary>
public static class SystemAnimations
{
    /// <summary>
    /// Fałsz, gdy system prosi o ograniczenie ruchu. Brak odpowiedzi systemu liczy się jako
    /// zgoda na animacje.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsEnabled();
                if (OperatingSystem.IsLinux()) return LinuxEnabled();
            }
            catch
            {
                // Ustawienie nieczytelne zostawia animacje włączone, tak jak brak systemu.
            }

            return true;
        }
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [SupportedOSPlatform("windows")]
    private static bool WindowsEnabled()
    {
        var enabled = 1;
        return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);

    // Zapytanie do portalu idzie przez szynę sesji, więc wykonuje się w tle.
    private static readonly PolledValue LinuxAnimations =
        new(initial: true, ReadLinux, TimeSpan.FromSeconds(5));

    [SupportedOSPlatform("linux")]
    private static bool LinuxEnabled() => LinuxAnimations.Value;

    private static bool? ReadLinux()
    {
        if (!OperatingSystem.IsLinux()) return null;

        // Bez adresu szyny libdbus próbowałby ją uruchomić (autolaunch) przy każdym odczycie.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return null;

        return PortalSettings.ReadBoolean("org.gnome.desktop.interface", "enable-animations");
    }
}
