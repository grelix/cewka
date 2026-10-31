using System.Runtime.InteropServices;
using Cewka.Audio.Decoding.Linux;
using Cewka.Audio.Decoding.Windows;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Formaty spoza dekoderów zarządzanych (AAC, M4A, ALAC) obsługiwane przez dekoder systemu:
/// Media Foundation w Windows, GStreamer pod Linuksem. FFmpeg odpada z powodu licencji.
///
/// <para>Pod Linuksem dekoder AAC dostarcza osobny pakiet (<c>gstreamer1.0-libav</c>); jego brak
/// zgłaszany jest z nazwą pakietu.</para>
/// </summary>
public static class SystemCodecs
{
    private static readonly Lazy<Availability> State = new(Detect);

    /// <summary>True when the system can decode the formats the managed layer cannot.</summary>
    public static bool IsAvailable => State.Value.Available;

    /// <summary>Name of the system decoder in use, for the settings window and diagnostics.</summary>
    public static string Name => State.Value.Name;

    /// <summary>
    /// Co brakuje, gdy <see cref="IsAvailable"/> jest fałszem, z nazwą pakietu do zainstalowania.
    /// </summary>
    public static string? UnavailableReason => State.Value.Reason;

    /// <summary>Formats routed to the system decoder.</summary>
    public static bool Handles(AudioFileFormat format) => format == AudioFileFormat.Mp4;

    public static IAudioDecoder Open(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter)
    {
        if (!IsAvailable)
            throw DecoderFailures.Create(DecoderFailure.SystemCodecsUnavailable);

        if (OperatingSystem.IsWindows())
            return new MediaFoundationDecoder(path, outputSampleRate, outputChannels, meter);

        if (OperatingSystem.IsLinux())
            return new GStreamerDecoder(path, outputSampleRate, outputChannels, meter);

        throw DecoderFailures.Create(DecoderFailure.SystemCodecsUnavailable);
    }

    private static Availability Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            // Media Foundation jest częścią systemu, ale wersje N i KN nie mają go bez
            // dodatku Media Feature Pack — dlatego sprawdzenie, a nie założenie.
            var present = NativeLibrary.TryLoad("mfplat.dll", out var handle);
            if (present) NativeLibrary.Free(handle);

            return present
                ? new Availability(true, "Media Foundation", null)
                : new Availability(false, "Media Foundation",
                    "Brak Media Foundation. W edycjach Windows N lub KN należy doinstalować " +
                    "dodatek Media Feature Pack, aby odtwarzać AAC, M4A i ALAC.");
        }

        if (OperatingSystem.IsLinux())
        {
            // Rdzeń i biblioteka odbiornika to dwa osobne pakiety; bez tej drugiej potok da się
            // zbudować, ale nie da się z niego wyjąć próbek.
            var core = NativeLibrary.TryLoad("libgstreamer-1.0.so.0", out var coreHandle);
            if (core) NativeLibrary.Free(coreHandle);

            var app = NativeLibrary.TryLoad("libgstapp-1.0.so.0", out var appHandle);
            if (app) NativeLibrary.Free(appHandle);

            if (core && app) return new Availability(true, "GStreamer", null);

            return new Availability(false, "GStreamer",
                "Brak GStreamera. Formaty AAC, M4A i ALAC wymagają pakietów " +
                "gstreamer1.0-plugins-base oraz gstreamer1.0-libav.");
        }

        return new Availability(false, "—", "Ten system nie udostępnia kodeków AAC, M4A ani ALAC.");
    }

    private readonly record struct Availability(bool Available, string Name, string? Reason);
}
