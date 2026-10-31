namespace Cewka.App.Models;

/// <summary>
/// Przełożenie położenia suwaka głośności na wzmocnienie sygnału.
/// <para>
/// Słuch ocenia głośność w skali logarytmicznej, więc suwak obejmuje 60 dB równomiernie:
/// każde pięć procent drogi to te same 3 dB, połowa suwaka to −30 dB, a skrajne lewe
/// położenie to cisza.
/// </para>
/// </summary>
public static class VolumeCurve
{
    /// <summary>Zakres suwaka od najcichszego słyszalnego położenia do pełnej głośności.</summary>
    public const double RangeDecibels = 60;

    public static double ToGain(double position)
    {
        if (position <= 0) return 0;

        return Math.Pow(10, (Math.Min(position, 1) - 1) * RangeDecibels / 20);
    }

    /// <summary>Odwrotność <see cref="ToGain"/>; wzmocnienie poniżej zakresu daje zero.</summary>
    public static double FromGain(double gain)
    {
        if (gain <= 0) return 0;

        return Math.Clamp(1 + 20 * Math.Log10(gain) / RangeDecibels, 0, 1);
    }
}
