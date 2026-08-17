using System.Runtime.InteropServices;
using Cewka.Audio.Decoding.Linux;
using Cewka.Audio.Decoding.Windows;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Formats the managed decoders do not cover — AAC, M4A and ALAC — handled by whatever the
/// operating system already provides.
///
/// <para><b>Dlaczego nie własny dekoder.</b> Napisanie dekodera AAC od zera to praca na
/// miesiące i stałe źródło błędów, a dołączenie FFmpeg zostało odrzucone razem z jego
/// licencją. Windows ma Media Foundation w każdej wspieranej wersji, więc pełny zadeklarowany
/// zakres formatów działa tam bez instalowania czegokolwiek. W systemie Linux tę samą rolę
/// pełni GStreamer — obecność jego bibliotek jest tu wykrywana, a dekodowaniem zajmuje się
/// <see cref="Linux.GStreamerDecoder"/>. Sam dekoder AAC dostarcza tam osobny pakiet
/// (<c>gstreamer1.0-libav</c>), którego brak jest zgłaszany razem z jego nazwą.</para>
/// </summary>
public static class SystemCodecs
{
    private static readonly Lazy<Availability> State = new(Detect);

    /// <summary>True when the system can decode the formats the managed layer cannot.</summary>
    public static bool IsAvailable => State.Value.Available;

    /// <summary>Name of the system decoder in use, for the settings window and diagnostics.</summary>
    public static string Name => State.Value.Name;

    /// <summary>
    /// Explains what is missing when <see cref="IsAvailable"/> is false, naming the package to
    /// install. A file that simply refuses to play without saying why is the worst outcome.
    /// </summary>
    public static string? UnavailableReason => State.Value.Reason;

    /// <summary>Formats routed to the system decoder.</summary>
    public static bool Handles(AudioFileFormat format) => format == AudioFileFormat.Mp4;

    public static IAudioDecoder Open(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter)
    {
        if (!IsAvailable)
            throw new AudioException(UnavailableReason ?? "Brak kodeków systemowych.");

        if (OperatingSystem.IsWindows())
            return new MediaFoundationDecoder(path, outputSampleRate, outputChannels, meter);

        if (OperatingSystem.IsLinux())
            return new GStreamerDecoder(path, outputSampleRate, outputChannels, meter);

        throw new AudioException(UnavailableReason ?? "Brak kodeków systemowych.");
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
