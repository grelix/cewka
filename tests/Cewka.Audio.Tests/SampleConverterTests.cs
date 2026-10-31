using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Mapowanie kanałów przed resamplerem: miks w dół zależny od układu kanałów źródła.</summary>
public class SampleConverterTests
{
    private const int Rate = 48000;
    private const int Frames = 256;

    /// <summary>Jedna próbka o wartości <paramref name="value"/> w kanale <paramref name="channel"/>.</summary>
    private static float[] Impulse(int channels, int channel, float value = 0.5f)
    {
        var data = new float[Frames * channels];
        for (var i = 0; i < Frames; i++) data[i * channels + channel] = value;
        return data;
    }

    private static float[] Convert(float[] source, int sourceChannels, int targetChannels, IReadOnlyList<Speaker>? layout)
    {
        using var converter = new SampleConverter(sourceChannels, Rate, targetChannels, Rate, sourceLayout: layout);
        var output = new float[Frames * targetChannels];
        var written = converter.Process(source, Frames, output, out var used);

        Assert.Equal(Frames, written);
        Assert.Equal(Frames, used);
        return output;
    }

    [Fact]
    public void VorbisCentreGoesEquallyToBothSides()
    {
        // Vorbis 5.1: L, C, R, RL, RR, LFE.
        var output = Convert(Impulse(6, 1), 6, 2, ChannelLayouts.Vorbis(6));

        Assert.True(output[0] > 0.1f);
        Assert.Equal(output[0], output[1], 6);
    }

    [Fact]
    public void VorbisFrontRightStaysOnTheRight()
    {
        var output = Convert(Impulse(6, 2), 6, 2, ChannelLayouts.Vorbis(6));

        Assert.Equal(0f, output[0]);
        Assert.True(output[1] > 0.1f);
    }

    [Fact]
    public void WaveCentreGoesEquallyToBothSidesAndLfeIsDropped()
    {
        // WAVE 5.1: FL, FR, FC, LFE, BL, BR.
        var centre = Convert(Impulse(6, 2), 6, 2, ChannelLayouts.Wave(6));
        Assert.True(centre[0] > 0.1f);
        Assert.Equal(centre[0], centre[1], 6);

        var lfe = Convert(Impulse(6, 3), 6, 2, ChannelLayouts.Wave(6));
        Assert.Equal(0f, lfe[0]);
        Assert.Equal(0f, lfe[1]);
    }

    [Fact]
    public void SurroundGoesToItsOwnSide()
    {
        var left = Convert(Impulse(6, 4), 6, 2, ChannelLayouts.Wave(6));
        Assert.True(left[0] > 0.1f);
        Assert.Equal(0f, left[1]);

        var right = Convert(Impulse(8, 7), 8, 2, ChannelLayouts.Wave(8));
        Assert.Equal(0f, right[0]);
        Assert.True(right[1] > 0.1f);
    }

    [Fact]
    public void FullScaleInEveryChannelDoesNotClip()
    {
        var source = new float[Frames * 8];
        Array.Fill(source, 1f);

        var output = Convert(source, 8, 2, ChannelLayouts.Wave(8));

        Assert.All(output, sample => Assert.InRange(sample, 0f, 1.0001f));
    }

    [Fact]
    public void StereoToMonoAverages()
    {
        var output = Convert(Impulse(2, 0, 0.8f), 2, 1, ChannelLayouts.Wave(2));
        Assert.Equal(0.4f, output[0], 5);
    }

    [Fact]
    public void UnknownLayoutIsAveraged()
    {
        var layout = Enumerable.Repeat(Speaker.Other, 10).ToArray();
        var output = Convert(Impulse(10, 9, 1f), 10, 2, layout);

        Assert.Equal(0.1f, output[0], 5);
        Assert.Equal(0.1f, output[1], 5);
    }

    [Fact]
    public void WaveMaskOrdersSpeakersByBit()
    {
        // 5.1 z kanałami bocznymi zamiast tylnych: FL, FR, FC, LFE, SL, SR.
        var layout = ChannelLayouts.FromWaveMask(0x60F, 6);
        Assert.Equal(
            [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.Lfe, Speaker.SideLeft, Speaker.SideRight],
            layout);

        // Maska niezgodna z liczbą kanałów nie jest wiarygodna.
        Assert.Equal(ChannelLayouts.Wave(6), ChannelLayouts.FromWaveMask(0x3, 6));
    }

    [Theory]
    [InlineData(6, 1, 2)] // Vorbis 5.1: środek na trzeci kanał WAVE
    [InlineData(6, 2, 1)] // prawy przedni
    [InlineData(6, 5, 3)] // LFE
    [InlineData(8, 3, 6)] // Vorbis 7.1: lewy boczny
    [InlineData(8, 7, 3)]
    public void VorbisSurroundIsReorderedForAnEqualWaveDevice(int channels, int source, int expected)
    {
        var output = Convert(Impulse(channels, source), channels, channels, ChannelLayouts.Vorbis(channels));

        for (var c = 0; c < channels; c++)
            Assert.Equal(c == expected ? 0.5f : 0f, output[c]);
    }

    [Fact]
    public void WaveOrderedSurroundIsCopiedToAnEqualDevice()
    {
        // Boczne zamiast tylnych: urządzenie i tak dostaje je na tych samych pozycjach.
        var layout = ChannelLayouts.FromWaveMask(0x60F, 6);

        for (var channel = 0; channel < 6; channel++)
        {
            var output = Convert(Impulse(6, channel), 6, 6, layout);
            for (var c = 0; c < 6; c++) Assert.Equal(c == channel ? 0.5f : 0f, output[c]);
        }
    }

    [Fact]
    public void MonoIsDuplicated()
    {
        var output = Convert(Impulse(1, 0), 1, 2, null);
        Assert.Equal(0.5f, output[0]);
        Assert.Equal(0.5f, output[1]);
    }
}
