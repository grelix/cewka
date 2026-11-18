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

    /// <summary>Czasy przenikania oferowane w ustawieniach, w sekundach.</summary>
    public static readonly int[] CrossfadeDurations = [2, 3, 5, 8, 12];

    /// <summary>Czas przenikania dla silnika; zero, gdy przenikanie jest wyłączone.</summary>
    public static TimeSpan CrossfadeLength(bool enabled, int seconds) =>
        enabled ? TimeSpan.FromSeconds(Nearest(CrossfadeDurations, seconds)) : TimeSpan.Zero;

    /// <summary>Najbliższy oferowany czas przenikania; wartość spoza listy zostawiłaby pasek wyboru bez zaznaczenia.</summary>
    public static int NearestCrossfade(int seconds) => Nearest(CrossfadeDurations, seconds);

    /// <summary>
    /// Poziom konwersji częstotliwości dla wybranej jakości. Nazwy wartości zostają ze względu
    /// na zapisane pliki ustawień: Off to poziom oszczędny.
    /// </summary>
    public static ResamplingLevel Resampling(ResamplerQuality quality) => quality switch
    {
        ResamplerQuality.Off => ResamplingLevel.Economy,
        ResamplerQuality.High => ResamplingLevel.High,
        _ => ResamplingLevel.Standard,
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

    /// <summary>Kiedy wyrównanie bierze wzmocnienie albumu.</summary>
    public static NormalisationScope Scope(NormalisationMode mode) => mode switch
    {
        NormalisationMode.Track => NormalisationScope.Track,
        NormalisationMode.Album => NormalisationScope.Album,
        _ => NormalisationScope.Auto,
    };

    /// <summary>
    /// Najbliższy oferowany krok przewijania. Plik ustawień można poprawić ręcznie, a wartość
    /// spoza listy zostawiłaby pasek wyboru bez zaznaczenia — albo, przy zerze, martwe strzałki.
    /// </summary>
    public static int NearestSeekStep(int seconds) => Nearest(SeekSteps, seconds);

    private static int Nearest(int[] options, int value)
    {
        var best = options[0];
        foreach (var option in options)
            if (Math.Abs((long)option - value) < Math.Abs((long)best - value)) best = option;

        return best;
    }
}
