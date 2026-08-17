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

    private static bool LinuxOnBattery()
    {
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return false;

        var mains = false;

        foreach (var supply in Directory.EnumerateDirectories(root))
        {
            var typeFile = Path.Combine(supply, "type");
            if (!File.Exists(typeFile)) continue;

            if (File.ReadAllText(typeFile).Trim() != "Mains") continue;

            mains = true;

            var onlineFile = Path.Combine(supply, "online");
            if (File.Exists(onlineFile) && File.ReadAllText(onlineFile).Trim() == "1") return false;
        }

        // Brak jakiegokolwiek zasilacza w systemie plików to komputer stacjonarny.
        return mains;
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
