using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Platform;

/// <summary>
/// Whether the machine is running on its battery.
///
/// <para>Używane wyłącznie do trybu automatycznego efektów: animowane tło i obracająca się płyta
/// odświeżają się trzydzieści razy na sekundę, co na zasilaniu bateryjnym jest kosztem
/// odczuwalnym, a przy zamkniętej pokrywie lub w tle — bezcelowym.</para>
/// </summary>
public static class PowerStatus
{
    /// <summary>
    /// True when the computer is on battery. A desktop machine, or any system that does not
    /// report power state, counts as mains powered — the visible effects stay on.
    /// </summary>
    public static bool IsOnBattery
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsOnBattery();
                if (OperatingSystem.IsLinux()) return LinuxOnBattery();
            }
            catch
            {
                // Nieznany stan zasilania traktowany jest jak sieć: lepiej pokazać pełne efekty
                // niż po cichu obciąć wygląd na komputerze stacjonarnym.
            }

            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsOnBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return false;

        // 0 oznacza pracę z baterii, 1 zasilanie sieciowe, 255 stan nieznany.
        return status.AcLineStatus == 0;
    }

    private static bool LinuxOnBattery() => LinuxOnBattery("/sys/class/power_supply");

    /// <summary>
    /// Na baterii jest komputer, którego bateria systemowa się rozładowuje, a żadne źródło
    /// zasilania nie jest podłączone.
    ///
    /// <para>Pułapka: sam wpis zasilacza sieciowego nie wystarcza. Przy ładowaniu przez USB-C
    /// bywa on wyłączony, a zasilanie płynie wpisem typu USB. Bateria myszy czy słuchawek ma
    /// zakres „Device” i nie mówi nic o komputerze.</para>
    /// </summary>
    internal static bool LinuxOnBattery(string root)
    {
        if (!Directory.Exists(root)) return false;

        var discharging = false;

        foreach (var supply in Directory.EnumerateDirectories(root))
        {
            var type = Read(supply, "type");

            if (type == "Battery")
            {
                if (Read(supply, "scope") != "Device" && Read(supply, "status") == "Discharging")
                    discharging = true;
            }
            else if (type is not null && Read(supply, "online") == "1")
            {
                return false;
            }
        }

        return discharging;
    }

    private static string? Read(string supply, string name)
    {
        var file = Path.Combine(supply, name);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
