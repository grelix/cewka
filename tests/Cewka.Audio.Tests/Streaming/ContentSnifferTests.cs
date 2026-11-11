using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Rozpoznanie po bajtach, niezależnie od nagłówków stacji.</summary>
public class ContentSnifferTests
{
    private static (SniffKind Kind, LiveCodec Codec, PlaylistKind List) Sniff(byte[] head, string? type = null, bool final = false)
    {
        var kind = ContentSniffer.Classify(head, type, out var codec, out var list, final);
        return (kind, codec, list);
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Mp3FramesAreRecognised() =>
        Assert.Equal((SniffKind.Audio, LiveCodec.Mp3, PlaylistKind.None), Sniff(FakeRadio.Mp3Frames(3), "audio/mpeg"));

    [Fact]
    public void OneMp3FrameIsNotEnough() =>
        Assert.Equal(SniffKind.NeedMore, Sniff(FakeRadio.Mp3Frames(1)).Kind);

    [Fact]
    public void AdtsFramesAreRecognised() =>
        Assert.Equal((SniffKind.Audio, LiveCodec.Aac, PlaylistKind.None), Sniff(FakeRadio.AdtsFrames(3)));

    [Fact]
    public void ContentTypeDoesNotDecideCodec() =>
        Assert.Equal(LiveCodec.Aac, Sniff(FakeRadio.AdtsFrames(3), "audio/mpeg").Codec);

    [Fact]
    public void Id3TagIsSkippedByLength()
    {
        // Rozmiar 300 zakodowany po 7 bitów; w znaczniku leży fałszywa synchronizacja.
        var tag = new byte[10 + 300];
        "ID3"u8.CopyTo(tag);
        tag[3] = 4;
        tag[8] = 300 >> 7;
        tag[9] = 300 & 0x7F;
        Array.Fill(tag, (byte)0xFF, 20, 50);

        Assert.Equal(LiveCodec.Mp3, Sniff([.. tag, .. FakeRadio.Mp3Frames(3)]).Codec);
        Assert.Equal(SniffKind.NeedMore, Sniff(tag[..200]).Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(416)]
    public void StartInTheMiddleOfFrameFindsSync(int skip) =>
        Assert.Equal(LiveCodec.Mp3, Sniff(FakeRadio.Mp3Frames(4)[skip..]).Codec);

    [Fact]
    public void AdtsStartInTheMiddleOfFrameFindsSync() =>
        Assert.Equal(LiveCodec.Aac, Sniff(FakeRadio.AdtsFrames(4)[100..]).Codec);

    [Fact]
    public void OggAndFlacSignatures()
    {
        Assert.Equal(LiveCodec.Ogg, Sniff([.. "OggS\0\u0002"u8, .. new byte[100]]).Codec);
        Assert.Equal(LiveCodec.Flac, Sniff([.. "fLaC"u8, 0, 0, 0, 34, .. new byte[100]]).Codec);
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6,\nseg1.ts\n", "audio/x-mpegurl")]
    [InlineData("#EXT-X-VERSION:3\n", null)]
    [InlineData("﻿ \r\n#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1\r\nx.m3u8\r\n", "audio/mpeg")]
    [InlineData("#komentarz\n#EXT-X-TARGETDURATION:6\n", null)]
    public void HlsIsRecognisedWhateverTheType(string text, string? type) =>
        Assert.Equal(SniffKind.Hls, Sniff(Text(text), type).Kind);

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:-1,Stacja\nhttp://x/y\n", (int)PlaylistKind.M3u)]
    [InlineData("#EXTM3U", (int)PlaylistKind.M3u)]
    [InlineData("http://x/y\n", (int)PlaylistKind.M3u)]
    [InlineData("HTTPS://x/y\r\n", (int)PlaylistKind.M3u)]
    [InlineData("#komentarz\nhttp://x/y\n", (int)PlaylistKind.M3u)]
    [InlineData("\r\n[Playlist]\r\nFile1=http://x\r\n", (int)PlaylistKind.Pls)]
    [InlineData("<ASX version=\"3.0\"><entry>", (int)PlaylistKind.Asx)]
    [InlineData("<?xml version=\"1.0\"?>\n<asx>", (int)PlaylistKind.Asx)]
    [InlineData("<?xml version=\"1.0\"?><!-- c --><playlist version=\"1\" xmlns=\"http://xspf.org/ns/0/\">", (int)PlaylistKind.Xspf)]
    [InlineData("<playlist xmlns='http://xspf.org/ns/0/'>", (int)PlaylistKind.Xspf)]
    public void PlaylistsAreRecognised(string text, int expected)
    {
        var result = Sniff(Text(text));

        Assert.Equal(SniffKind.Playlist, result.Kind);
        Assert.Equal((PlaylistKind)expected, result.List);
    }

    [Fact]
    public void Utf16PlaylistIsRecognised()
    {
        byte[] data = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[playlist]\r\nFile1=http://x\r\n")];

        Assert.Equal(PlaylistKind.Pls, Sniff(data).List);
    }

    [Theory]
    [InlineData("<html><body>", "audio/mpeg")]
    [InlineData("  <!DOCTYPE html>", "audio/mpeg")]
    [InlineData("<?xml version=\"1.0\"?><rss>", "audio/mpeg")]
    [InlineData("<playlist version=\"1\"><track>", null)]
    public void HtmlAndForeignXmlAreNotAStream(string text, string? type) =>
        Assert.Equal(SniffKind.Html, Sniff(Text(text), type).Kind);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("#")]
    [InlineData("#EXT")]
    [InlineData("#EXT-X")]
    [InlineData("[pla")]
    [InlineData("<")]
    [InlineData("<ht")]
    [InlineData("<?xml version")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<?xml version=\"1.0\"?><!-- ")]
    [InlineData("<playlist version=\"1\" ")]
    [InlineData("htt")]
    [InlineData("#komentarz")]
    public void ShortPrefixesNeedMore(string text) =>
        Assert.Equal(SniffKind.NeedMore, Sniff(Text(text)).Kind);

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void RandomBytesWithSeedAreUnknownAtEnd(int seed)
    {
        var data = new byte[16 * 1024];
        new Random(seed).NextBytes(data);
        data[0] = 0; // bez przypadkowego znacznika kolejności bajtów

        Assert.Equal(SniffKind.NeedMore, Sniff(data).Kind);
        Assert.Equal(SniffKind.Unknown, Sniff(data, final: true).Kind);
    }

    [Fact]
    public void PlainTextAtEndIsUnknownButHtmlTypeIsHtml()
    {
        var data = Text("Serwer zajety");

        Assert.Equal(SniffKind.Unknown, Sniff(data, "text/plain", true).Kind);
        Assert.Equal(SniffKind.Html, Sniff(data, "text/html; charset=utf-8", true).Kind);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void MutationsNeverThrow(int seed)
    {
        var random = new Random(seed);
        byte[][] samples =
        [
            FakeRadio.Mp3Frames(6), FakeRadio.AdtsFrames(6), Text("#EXTM3U\n#EXT-X-VERSION:3\nhttp://x/\n"),
            Text("<?xml version=\"1.0\"?><playlist xmlns=\"http://xspf.org/ns/0/\"><!-- x -->"), Text("[playlist]\nFile1=x\n"),
            [.. "ID3\u0004\0\0\0\0\0 "u8, .. new byte[64]],
        ];

        for (var round = 0; round < 5000; round++)
        {
            var data = samples[random.Next(samples.Length)].ToArray();
            for (var change = random.Next(1, 6); change > 0; change--)
                data[random.Next(data.Length)] = (byte)random.Next(256);

            ContentSniffer.Classify(data.AsSpan(0, random.Next(data.Length + 1)), null, out _, out _, round % 2 == 0);
        }
    }
}
