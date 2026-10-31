using System.Numerics;

namespace Cewka.Audio.Decoding;

/// <summary>Położenie głośnika, któremu odpowiada kanał źródła.</summary>
internal enum Speaker
{
    FrontLeft,
    FrontRight,
    FrontCenter,
    Lfe,
    BackLeft,
    BackRight,
    FrontLeftOfCenter,
    FrontRightOfCenter,
    BackCenter,
    SideLeft,
    SideRight,

    /// <summary>Kanał bez znanego położenia; jego obecność wymusza zwykłe uśrednienie.</summary>
    Other,
}

/// <summary>
/// Kolejność kanałów zależy od formatu: Vorbis i Opus stawiają środek między lewym i prawym,
/// a WAVE, FLAC, Media Foundation i GStreamer po nich. Miks w dół musi wiedzieć, który kanał
/// jest który, inaczej przy 5.1 prawe wyjście dostaje środek albo środek znika.
/// </summary>
internal static class ChannelLayouts
{
    private const Speaker FL = Speaker.FrontLeft, FR = Speaker.FrontRight, FC = Speaker.FrontCenter,
        LFE = Speaker.Lfe, BL = Speaker.BackLeft, BR = Speaker.BackRight, BC = Speaker.BackCenter,
        SL = Speaker.SideLeft, SR = Speaker.SideRight;

    /// <summary>Kolejność ze specyfikacji Vorbis, wspólna z Opus (rodzina mapowania 1).</summary>
    public static Speaker[] Vorbis(int channels) => channels switch
    {
        1 => [FC],
        2 => [FL, FR],
        3 => [FL, FC, FR],
        4 => [FL, FR, BL, BR],
        5 => [FL, FC, FR, BL, BR],
        6 => [FL, FC, FR, BL, BR, LFE],
        7 => [FL, FC, FR, SL, SR, BC, LFE],
        8 => [FL, FC, FR, SL, SR, BL, BR, LFE],
        _ => Unknown(channels),
    };

    /// <summary>Domyślna kolejność WAVE i FLAC, gdy plik nie podaje maski kanałów.</summary>
    public static Speaker[] Wave(int channels) => channels switch
    {
        1 => [FC],
        2 => [FL, FR],
        3 => [FL, FR, FC],
        4 => [FL, FR, BL, BR],
        5 => [FL, FR, FC, BL, BR],
        6 => [FL, FR, FC, LFE, BL, BR],
        7 => [FL, FR, FC, LFE, BC, SL, SR],
        8 => [FL, FR, FC, LFE, BL, BR, SL, SR],
        _ => Unknown(channels),
    };

    /// <summary>
    /// Maska kanałów WAVE (taka sama w Media Foundation): kanały występują w kolejności
    /// ustawionych bitów. Maska z inną liczbą bitów niż kanałów nie jest wiarygodna.
    /// </summary>
    public static Speaker[] FromWaveMask(uint mask, int channels)
    {
        if (mask == 0 || BitOperations.PopCount(mask) != channels) return Wave(channels);

        var layout = new Speaker[channels];
        var index = 0;

        for (var bit = 0; bit < 32; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            layout[index++] = bit <= (int)Speaker.SideRight ? (Speaker)bit : Speaker.Other;
        }

        return layout;
    }

    private static Speaker[] Unknown(int channels) => Enumerable.Repeat(Speaker.Other, Math.Max(channels, 0)).ToArray();
}
