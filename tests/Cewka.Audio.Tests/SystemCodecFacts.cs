using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Test Media Foundation. W innym systemie albo bez Media Foundation jest pomijany,
/// żeby raport pokazywał brak sprawdzenia zamiast zaliczonego testu.
/// </summary>
public sealed class MediaFoundationFactAttribute : FactAttribute
{
    public MediaFoundationFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Media Foundation jest dostępne tylko w Windows.";
        else if (!SystemCodecs.IsAvailable) Skip = SystemCodecs.UnavailableReason ?? "Brak Media Foundation.";
    }
}

/// <summary>Odmiana <see cref="MediaFoundationFactAttribute"/> dla testów z danymi.</summary>
public sealed class MediaFoundationTheoryAttribute : TheoryAttribute
{
    public MediaFoundationTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Media Foundation jest dostępne tylko w Windows.";
        else if (!SystemCodecs.IsAvailable) Skip = SystemCodecs.UnavailableReason ?? "Brak Media Foundation.";
    }
}

/// <summary>
/// Plik M4A z AAC przez dekoder systemu, w Windows i pod Linuksem; pomijany bez dekodera AAC
/// (Windows N, Linux bez gstreamer1.0-libav).
/// </summary>
public sealed class SystemAacTheoryAttribute : TheoryAttribute
{
    public SystemAacTheoryAttribute()
    {
        if (!SystemCodecs.IsAvailable) Skip = SystemCodecs.UnavailableReason ?? "Brak kodeków systemu.";
        else if (!Decoding.Live.LiveFrameDecoders.IsPlayable(Cewka.Audio.Streaming.LiveCodec.Aac)) Skip = "System nie ma dekodera AAC.";
    }
}

/// <summary>Test GStreamera; poza systemem Linux albo bez bibliotek GStreamera pomijany.</summary>
public sealed class GStreamerFactAttribute : FactAttribute
{
    public GStreamerFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "GStreamer jest używany tylko w systemie Linux.";
        else if (!SystemCodecs.IsAvailable) Skip = SystemCodecs.UnavailableReason ?? "Brak GStreamera.";
    }
}
