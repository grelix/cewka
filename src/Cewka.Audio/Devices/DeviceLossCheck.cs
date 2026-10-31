using System.Diagnostics;

namespace Cewka.Audio.Devices;

/// <summary>
/// Rozstrzyga, czy zatrzymanie urządzenia, którego nie zlecił program, jest utratą.
///
/// <para>Pułapka: przy zmianie urządzenia domyślnego w Windows (podłączenie słuchawek,
/// połączenie Bluetooth) miniaudio samo zatrzymuje strumień, przekierowuje go i uruchamia
/// ponownie. Zgłoszenie zatrzymania przychodzi wtedy tak samo jak przy odłączeniu, więc
/// o utracie świadczy dopiero urządzenie, które w czasie karencji nie ruszyło.</para>
/// </summary>
internal static class DeviceLossCheck
{
    /// <param name="isStarted">Czy urządzenie faktycznie pracuje.</param>
    /// <param name="stillExpected">Czy program nadal chce, żeby grało; false przerywa sprawdzanie.</param>
    /// <param name="grace">Czas, w którym urządzenie może ruszyć samo.</param>
    /// <param name="pollMilliseconds">Odstęp między odczytami stanu.</param>
    public static bool Confirm(Func<bool> isStarted, Func<bool> stillExpected, TimeSpan grace, int pollMilliseconds = 20)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (!stillExpected() || isStarted()) return false;
            if (clock.Elapsed >= grace) return true;

            Thread.Sleep(pollMilliseconds);
        }
    }
}
