using System.Text.Json.Serialization;

namespace Cewka.App.Models;

/// <summary>How the interface picks between the light and dark palettes.</summary>
public enum ThemeMode
{
    /// <summary>Follow the operating system setting and react to changes at runtime.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How aggressively the decorative effects may be scaled back.</summary>
public enum EffectsMode
{
    /// <summary>Full effects on mains power, reduced automatically while running on battery.</summary>
    Auto,

    /// <summary>Always full quality, regardless of cost.</summary>
    Full,

    /// <summary>Always reduced: no rotation, no cursor glow, static background.</summary>
    Reduced,
}

/// <summary>
/// Jakość konwersji częstotliwości próbkowania — rząd filtru dolnoprzepustowego resamplera.
/// Ma znaczenie tylko dla plików, których częstotliwość różni się od częstotliwości urządzenia.
/// </summary>
public enum ResamplerQuality
{
    /// <summary>Bez filtrowania. Najtańsze, kosztem aliasingu.</summary>
    Off,

    /// <summary>Filtr czwartego rzędu — wartość domyślna miniaudio.</summary>
    Standard,

    /// <summary>Filtr ósmego rzędu, maksimum obsługiwane przez miniaudio.</summary>
    High,
}

/// <summary>
/// Rozmiar bufora urządzenia wyjściowego, czyli opóźnienie między decyzją a dźwiękiem.
/// Mniejszy bufor znaczy szybszą reakcję i większe ryzyko przerywania.
/// </summary>
public enum OutputLatency
{
    /// <summary>Około 256 ramek. Najkrótsze opóźnienie, najmniejszy zapas na obciążenie systemu.</summary>
    Low,

    /// <summary>Rozmiar dobrany przez miniaudio pod dany sterownik.</summary>
    Balanced,

    /// <summary>Około 2048 ramek. Odporne na obciążenie kosztem opóźnienia.</summary>
    Safe,
}

/// <summary>Poziom, do którego wyrównywana jest głośność utworów.</summary>
public enum LoudnessTarget
{
    /// <summary>−23 LUFS, norma nadawcza EBU R128.</summary>
    Broadcast,

    /// <summary>−18 LUFS, poziom odniesienia ReplayGain 2.0.</summary>
    Reference,

    /// <summary>−14 LUFS, poziom przyjęty przez serwisy strumieniowe.</summary>
    Streaming,
}

/// <summary>Where the minimise, maximise and close buttons sit.</summary>
public enum WindowControlsPosition
{
    /// <summary>Right-hand side, as on Windows. The default.</summary>
    Right,

    /// <summary>Left-hand side, keeping the same square glyphs.</summary>
    Left,

    /// <summary>
    /// Left-hand side as three coloured circles, in the manner of macOS. Useful on a Linux
    /// desktop themed after macOS, or simply for anyone who prefers it.
    /// </summary>
    MacOs,
}

/// <summary>Persisted window placement. Stored in logical units, not device pixels.</summary>
public sealed class WindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Everything the application remembers between runs, except the playback queue,
/// which lives in its own file because it changes far more often.
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// Interface language: a two-letter code, or <c>auto</c> to follow the operating system.
    /// </summary>
    public string Language { get; set; } = "auto";

    public WindowControlsPosition WindowControls { get; set; } = WindowControlsPosition.Right;

    public WindowGeometry? Window { get; set; }

    /// <summary>Whether the equaliser and queue panel is expanded.</summary>
    public bool PanelOpen { get; set; } = true;

    public double Volume { get; set; } = 0.72;

    public bool EqualiserEnabled { get; set; } = true;

    public double Preamp { get; set; } = 1.5;

    /// <summary>Gain per band in decibels, ordered from 32 Hz to 16 kHz.</summary>
    public double[] EqualiserGains { get; set; } = [2.5, 2, 0.5, 1, -1.5, -1, 0.5, 1.5, 2.5, 3];

    public bool LimiterEnabled { get; set; } = true;

    public bool NormalisationEnabled { get; set; } = true;

    public EffectsMode Effects { get; set; } = EffectsMode.Auto;

    /// <summary>
    /// Name of the output device, exactly as the system reports it; <c>null</c> follows the
    /// system default. Stored by name rather than by index because indices are only meaningful
    /// while the same devices are plugged in.
    /// </summary>
    public string? OutputDevice { get; set; }

    public ResamplerQuality ResamplerQuality { get; set; } = ResamplerQuality.Standard;

    public OutputLatency OutputLatency { get; set; } = OutputLatency.Balanced;

    public LoudnessTarget LoudnessTarget { get; set; } = LoudnessTarget.Reference;

    /// <summary>Whether ReplayGain tags are ignored in favour of the player's own measurement.</summary>
    public bool AlwaysAnalyse { get; set; }

    /// <summary>Whether the queue and playback position from the previous run come back.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Seconds the arrow keys move playback by.</summary>
    public int SeekStep { get; set; } = 5;

    /// <summary>Whether the multimedia keys on the keyboard control playback.</summary>
    public bool MediaKeys { get; set; } = true;

    /// <summary>Whether opening a file hands it to the running copy instead of starting another.</summary>
    public bool SingleInstance { get; set; } = true;

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        Language = Language,
        WindowControls = WindowControls,
        Window = Window is null ? null : new WindowGeometry
        {
            X = Window.X, Y = Window.Y, Width = Window.Width, Height = Window.Height, Maximized = Window.Maximized,
        },
        PanelOpen = PanelOpen,
        Volume = Volume,
        EqualiserEnabled = EqualiserEnabled,
        Preamp = Preamp,
        EqualiserGains = (double[])EqualiserGains.Clone(),
        LimiterEnabled = LimiterEnabled,
        NormalisationEnabled = NormalisationEnabled,
        Effects = Effects,
        OutputDevice = OutputDevice,
        ResamplerQuality = ResamplerQuality,
        OutputLatency = OutputLatency,
        LoudnessTarget = LoudnessTarget,
        AlwaysAnalyse = AlwaysAnalyse,
        RestoreSession = RestoreSession,
        SeekStep = SeekStep,
        MediaKeys = MediaKeys,
        SingleInstance = SingleInstance,
    };
}

// Source-generated serialisation keeps the single-file build trim-safe.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
