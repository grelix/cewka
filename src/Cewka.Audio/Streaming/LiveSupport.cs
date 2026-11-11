using Cewka.Audio.Decoding.Live;

namespace Cewka.Audio.Streaming;

/// <summary>Co ten komputer umie zagrać na żywo; do ułożenia adresów stacji według dostępnych kodeków.</summary>
public static class LiveSupport
{
    /// <summary>Czy jest dekoder tego kodeka: MP3 zawsze, AAC gdy system ma dekoder (Media Foundation, GStreamer).</summary>
    public static bool IsPlayable(LiveCodec codec) => LiveFrameDecoders.IsPlayable(codec);
}
