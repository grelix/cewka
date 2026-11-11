namespace Cewka.Audio.Decoding.Live;

/// <summary>
/// Nagłówek ramki ADTS. Każda ramka niosie go w całości, więc wejście w strumień radia w dowolnym
/// miejscu wymaga tylko znalezienia pierwszego nagłówka.
/// </summary>
/// <param name="Profile">Pole profilu: 0 Main, 1 LC, 2 SSR, 3 LTP. HE-AAC przedstawia się jako LC.</param>
/// <param name="ChannelConfiguration">Zero: układ kanałów podany dopiero w treści ramki.</param>
/// <param name="FrameLength">Długość całej ramki razem z nagłówkiem.</param>
internal readonly record struct AdtsHeader(int Profile, int SamplingIndex, int ChannelConfiguration, int FrameLength)
{
    public const int MinLength = 7;

    public const int LowComplexityProfile = 1;

    private static readonly int[] Rates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public int SampleRate => Rates[SamplingIndex];

    /// <summary>Dwa bajty AudioSpecificConfig opisujące ten strumień.</summary>
    public int AudioSpecificConfig => (Profile + 1) << 11 | SamplingIndex << 7 | ChannelConfiguration << 3;

    public bool SameStreamAs(AdtsHeader other) =>
        Profile == other.Profile
        && SamplingIndex == other.SamplingIndex
        && ChannelConfiguration == other.ChannelConfiguration;

    public static bool TryParse(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;

        if (data.Length < MinLength || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0) return false;

        var samplingIndex = (data[2] >> 2) & 0xF;
        if (samplingIndex >= Rates.Length) return false;

        // Bit protection_absent wyzerowany: za nagłówkiem stoją jeszcze dwa bajty sumy kontrolnej.
        var headerLength = (data[1] & 1) == 0 ? MinLength + 2 : MinLength;
        var frameLength = (data[3] & 3) << 11 | data[4] << 3 | data[5] >> 5;
        if (frameLength < headerLength) return false;

        header = new AdtsHeader(data[2] >> 6, samplingIndex, (data[2] & 1) << 2 | data[3] >> 6, frameLength);
        return true;
    }
}
