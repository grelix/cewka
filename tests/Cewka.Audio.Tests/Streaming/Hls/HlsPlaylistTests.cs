using System.Text;
using Cewka.Audio.Streaming.Hls;
using Xunit;

namespace Cewka.Audio.Tests.Streaming.Hls;

public sealed class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://stacja.example/radio/live/master.m3u8?t=1");

    private static HlsPlaylist Parse(string text) => HlsPlaylistParser.Parse(Encoding.UTF8.GetBytes(text), Base);

    private static HlsMaster Master(string text) => Assert.IsType<HlsMaster>(Parse("#EXTM3U\n" + text));

    private static HlsMedia Media(string text) => Assert.IsType<HlsMedia>(Parse("#EXTM3U\n" + text));

    [Fact]
    public void MediaPlaylistGivesSegmentsWithTheirSequence()
    {
        var media = Media("#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:40\n#EXTINF:5.5,Tytuł, z przecinkiem\na.ts\n#EXTINF:6,\nb.ts?x=1\n");

        Assert.Equal(6, media.TargetSeconds);
        Assert.False(media.Ended);
        Assert.Null(media.Unsupported);
        Assert.Equal(
            [
                new HlsSegment(new Uri("https://stacja.example/radio/live/a.ts"), 5.5, 40),
                new HlsSegment(new Uri("https://stacja.example/radio/live/b.ts?x=1"), 6, 41),
            ],
            media.Segments);
    }

    [Fact]
    public void WindowsLineEndsAndByteOrderMarkAreAccepted()
    {
        var media = Media("#EXT-X-TARGETDURATION:4\r\n#EXTINF:4,\r\na.ts\r\n#EXT-X-ENDLIST\r\n");

        Assert.Single(media.Segments);
        Assert.True(media.Ended);

        Assert.IsType<HlsMedia>(Parse("﻿#EXTM3U\n#EXTINF:4,\na.ts\n"));
    }

    [Fact]
    public void MissingTargetDurationGetsADefault()
    {
        Assert.True(Media("#EXTINF:4,\na.ts\n").TargetSeconds > 0);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"\n")]
    [InlineData("#EXT-X-MAP:URI=\"init.mp4\"\n")]
    [InlineData("#EXT-X-BYTERANGE:1000@0\n")]
    public void EncryptionFragmentedMp4AndByteRangesAreUnsupported(string tag)
    {
        Assert.NotNull(Media(tag + "#EXTINF:4,\na.ts\n").Unsupported);
    }

    [Fact]
    public void KeyMethodNoneIsPlainAudio()
    {
        Assert.Null(Media("#EXT-X-KEY:METHOD=NONE\n#EXTINF:4,\na.ts\n").Unsupported);
    }

    [Fact]
    public void DiscontinuityDoesNotBreakTheSequence()
    {
        var media = Media("#EXTINF:4,\na.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:4,\nb.ts\n");

        Assert.Equal([0L, 1L], media.Segments.Select(s => s.Sequence));
    }

    [Fact]
    public void LinesOutsideHttpAreIgnored()
    {
        Assert.Empty(Media("#EXTINF:4,\nfile:///etc/passwd\n#EXTINF:4,\nftp://x/a.ts\n").Segments);
    }

    [Fact]
    public void QuotedCommasStayInsideAttributes()
    {
        var variant = Master("#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2,avc1.42\",RESOLUTION=256x144,AUDIO=\"a\"\nv.m3u8\n").Variants.Single();

        Assert.Equal(64000, variant.BandwidthBps);
        Assert.Equal("mp4a.40.2,avc1.42", variant.Codecs);
        Assert.True(variant.HasResolution);
        Assert.Equal("a", variant.AudioGroup);
        Assert.Equal(new Uri("https://stacja.example/radio/live/v.m3u8"), variant.Url);
    }

    [Fact]
    public void BestAudioVariantWinsOverVideo()
    {
        var choice = HlsPlaylistParser.Choose(Master(
            "#EXT-X-STREAM-INF:BANDWIDTH=48000,CODECS=\"mp4a.40.5\"\nn.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.2\"\nw.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,CODECS=\"avc1.42,mp4a.40.2\",RESOLUTION=640x360\nv.m3u8\n"));

        Assert.Equal(new HlsChoice(new Uri("https://stacja.example/radio/live/w.m3u8"), 128), choice);
    }

    [Fact]
    public void WithVideoOnlyTheCheapestVariantIsTakenWithoutABitrate()
    {
        var choice = HlsPlaylistParser.Choose(Master(
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\nd.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=256x144\nm.m3u8\n"));

        Assert.Equal(new HlsChoice(new Uri("https://stacja.example/radio/live/m.m3u8"), null), choice);
    }

    [Fact]
    public void SeparateAudioTrackOfAVideoVariantIsPreferred()
    {
        var choice = HlsPlaylistParser.Choose(Master(
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"x\",URI=\"inna.m3u8\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"y\",DEFAULT=YES,URI=\"glowna.m3u8\"\n" +
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"a\",NAME=\"z\",URI=\"napisy.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=256x144,AUDIO=\"a\"\nm.m3u8\n"));

        Assert.Equal(new Uri("https://stacja.example/radio/live/glowna.m3u8"), choice?.Url);
    }

    [Fact]
    public void VariantWithoutACodecListCountsAsAudio()
    {
        var choice = HlsPlaylistParser.Choose(Master("#EXT-X-STREAM-INF:BANDWIDTH=64000\na.m3u8\n"));

        Assert.Equal(64, choice?.BitrateKbps);
    }

    [Theory]
    [InlineData("ec-3")]
    [InlineData("ac-3")]
    [InlineData("opus")]
    public void VariantsWithOnlyOtherCodecsGiveNothing(string codec)
    {
        Assert.Null(HlsPlaylistParser.Choose(Master($"#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"{codec}\"\na.m3u8\n")));
    }

    [Fact]
    public void RandomContentNeverThrows()
    {
        var random = new Random(20261105);
        const string alphabet = "#EXTINF:,=\"\n-XSTREAM/.a0";

        for (var round = 0; round < 2000; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var playlist = Parse("#EXTM3U\n#EXT-X-STREAM-INF:" + text);
            if (playlist is HlsMaster master) HlsPlaylistParser.Choose(master);

            Parse(text);
        }
    }
}
