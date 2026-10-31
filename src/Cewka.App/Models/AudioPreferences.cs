using Cewka.Audio;
using Cewka.Audio.Dsp;

namespace Cewka.App.Models;

/// <summary>
/// Przekłada wybory z okna ustawień na liczby, którymi posługuje się warstwa dźwięku.
///
/// <para>W jednym miejscu, bo te same wartości potrzebne są przy starcie i przy zmianie w oknie;
/// dwie drogi mogłyby się rozjechać.</para>
/// </summary>
public static class AudioPreferences
{
    /// <summary>Wartości kroku przewijania oferowane w ustawieniach.</summary>
    public static readonly int[] SeekSteps = [5, 10, 30];

    /// <summary>Rząd filtru resamplera dla wybranej jakości.</summary>
    public static int FilterOrder(ResamplerQuality quality) => quality switch
    {
        ResamplerQuality.Off => 0,
        ResamplerQuality.High => AudioQuality.MaximumFilterOrder,
        _ => AudioQuality.DefaultFilterOrder,
    };

    /// <summary>
    /// Żądany rozmiar okresu w ramkach; zero oznacza wybór miniaudio. Wartość jest podpowiedzią
    /// dla sterownika, a nie ustaleniem — przyjęty rozmiar trzeba odczytać z urządzenia.
    /// </summary>
    public static int PeriodFrames(OutputLatency latency) => latency switch
    {
        OutputLatency.Low => 256,
        OutputLatency.Safe => 2048,
        _ => 0,
    };

    /// <summary>Poziom docelowy wyrównania głośności w LUFS.</summary>
    public static double Lufs(LoudnessTarget target) => target switch
    {
        LoudnessTarget.Broadcast => -23.0,
        LoudnessTarget.Streaming => -14.0,
        _ => LoudnessService.ReferenceLufs,
    };

    /// <summary>
    /// Najbliższy oferowany krok przewijania. Plik ustawień można poprawić ręcznie, a wartość
    /// spoza listy zostawiłaby pasek wyboru bez zaznaczenia — albo, przy zerze, martwe strzałki.
    /// </summary>
    public static int NearestSeekStep(int seconds)
    {
        var best = SeekSteps[0];
        foreach (var step in SeekSteps)
            if (Math.Abs(step - seconds) < Math.Abs(best - seconds)) best = step;

        return best;
    }
}
