using System.Text;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Devices;

/// <summary>One playback device reported by the operating system.</summary>
public sealed record AudioDeviceInfo(int Index, string Name, bool IsDefault);

/// <summary>Enumeration of playback devices, for the settings window and for diagnostics.</summary>
public static class AudioDeviceList
{
    /// <summary>Version of the bundled miniaudio build.</summary>
    public static string NativeVersion => NativeAudio.Version();

    /// <summary>
    /// Asks the platform for the current device list. Devices come and go — a headset is
    /// unplugged, a monitor with speakers is switched off — so this is deliberately not cached.
    /// </summary>
    public static unsafe IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        NativeAudio.ThrowIfFailed(NativeAudio.DevicesRefresh(out var count), "Odczyt listy urządzeń");

        var devices = new List<AudioDeviceInfo>(count);
        var buffer = stackalloc byte[512];

        for (var i = 0; i < count; i++)
        {
            var length = NativeAudio.DevicesName(i, buffer, 512);
            var name = length > 0
                ? Encoding.UTF8.GetString(buffer, length)
                : $"Urządzenie {i + 1}";

            devices.Add(new AudioDeviceInfo(i, name, NativeAudio.DevicesIsDefault(i) != 0));
        }

        return devices;
    }
}
