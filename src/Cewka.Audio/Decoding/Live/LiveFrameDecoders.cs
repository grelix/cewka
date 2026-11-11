using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Live;

internal static class LiveFrameDecoders
{
    /// <summary>Próba zbudowania dekodera: tylko tak wiadomo, czy system go ma.</summary>
    private static readonly Lazy<bool> AacAvailable = new(() =>
    {
        try
        {
            AacBackends.Create(new AdtsHeader(AdtsHeader.LowComplexityProfile, 4, 2, AdtsHeader.MinLength)).Dispose();
            return true;
        }
        catch (AacBackendException)
        {
            return false;
        }
    });

    /// <summary>Czy na tym komputerze da się zagrać ten kodek.</summary>
    public static bool IsPlayable(LiveCodec codec) => codec switch
    {
        LiveCodec.Mp3 => true,
        LiveCodec.Aac => AacAvailable.Value,
        _ => false,
    };

    /// <summary>Null, gdy kodek nie ma jeszcze dekodera.</summary>
    public static ILiveFrameDecoder? Create(LiveCodec codec) => codec switch
    {
        LiveCodec.Mp3 => new Mp3FrameDecoder(),
        LiveCodec.Aac => new AacFrameDecoder(AacBackends.Create),
        _ => null,
    };
}
