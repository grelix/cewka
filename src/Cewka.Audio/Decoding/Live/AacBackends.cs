using Cewka.Audio.Decoding.Linux;
using Cewka.Audio.Decoding.Windows;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Live;

internal static class AacBackends
{
    public static IAacBackend Create(AdtsHeader header)
    {
        if (OperatingSystem.IsWindows()) return new MediaFoundationAacBackend(header);
        if (OperatingSystem.IsLinux()) return new GStreamerAacBackend();

        throw new AacBackendException(LiveFailureReason.CodecUnavailable, "Ten system nie ma dekodera AAC.");
    }
}
