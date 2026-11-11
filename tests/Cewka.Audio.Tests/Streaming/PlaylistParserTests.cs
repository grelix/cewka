using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

public class PlaylistParserTests
{
    /// <summary>Otwarcie i tak próbuje najwyżej tylu adresów, a kolejne kosztowałyby rdzeń i pamięć bez przerwania.</summary>
    [Fact]
    public void LongListStopsAtTheLimitOfEntries()
    {
        var lines = string.Concat(Enumerable.Range(0, 5_000).Select(i => $"{i}\n"));

        var entries = PlaylistParser.Parse(Encoding.UTF8.GetBytes(lines), PlaylistKind.M3u, new Uri("http://stacja.test/lista/"));

        Assert.Equal(32, entries.Count);
        Assert.Equal(new Uri("http://stacja.test/lista/0"), entries[0]);
    }

    /// <summary>Powtórzony wpis na długim adresie bazowym: każda próba to kopia całego adresu.</summary>
    [Fact]
    public void RepeatedEntriesOnALongBaseAddressStayCheap()
    {
        var baseUrl = new Uri("http://stacja.test/" + new string('a', 15 * 1024) + "/");
        var lines = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("b\n", 100_000)));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var entries = PlaylistParser.Parse(lines, PlaylistKind.M3u, baseUrl);

        Assert.Single(entries);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128 * 1024 * 1024);
    }

    private static readonly Uri Base = new("http://stacja.test/listy/radio.pls");

    private static string[] Parse(string text, PlaylistKind kind) =>
        PlaylistParser.Parse(Encoding.UTF8.GetBytes(text), kind, Base).Select(u => u.ToString()).ToArray();

    [Fact]
    public void PlsIsOrderedByFileNumber()
    {
        var urls = Parse(
            "[playlist]\r\nNumberOfEntries=3\r\nFile10=http://c/\r\nTitle10=C\r\nfile2=http://b/\r\nFILE1=http://a/\r\nVersion=2\r\n",
            PlaylistKind.Pls);

        Assert.Equal(["http://a/", "http://b/", "http://c/"], urls);
    }

    [Fact]
    public void PlsIgnoresLinesWithoutNumber() =>
        Assert.Equal(["http://a/"], Parse("[playlist]\nFile=http://z/\nFilex=http://y/\nFile1=http://a/\n", PlaylistKind.Pls));

    [Fact]
    public void M3uWithBomAndCrLf()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("#EXTM3U\r\n#EXTINF:-1,Nazwa\r\nhttp://a/\r\n\r\n  https://b/  \r\n")];

        var urls = PlaylistParser.Parse(data, PlaylistKind.M3u, Base);

        Assert.Equal([new Uri("http://a/"), new Uri("https://b/")], urls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Utf16WithEitherByteOrder(bool littleEndian)
    {
        var encoding = littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
        byte[] data = [.. encoding.GetPreamble(), .. encoding.GetBytes("[playlist]\r\nFile1=http://a/\r\n")];

        Assert.Equal([new Uri("http://a/")], PlaylistParser.Parse(data, PlaylistKind.Pls, Base));
    }

    [Fact]
    public void RelativeEntriesResolveFromBase() =>
        Assert.Equal(
            ["http://stacja.test/listy/s.mp3", "http://stacja.test/k/s.aac"],
            Parse("s.mp3\n/k/s.aac\n", PlaylistKind.M3u));

    [Fact]
    public void AsxWithInvalidXml()
    {
        const string text = """
            <ASX version="3.0"><Entry><Title>A & B</title>
            <REF HREF = "http://a/s?x=1&amp;y=2" /><ref class='x' href='http://b/' >
            <entryref href="http://nie/" /></asx
            """;

        Assert.Equal(["http://a/s?x=1&y=2", "http://b/"], Parse(text, PlaylistKind.Asx));
    }

    [Fact]
    public void Xspf()
    {
        const string text = """
            <?xml version="1.0" encoding="UTF-8"?>
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <location>http://nie-wpis/</location>
              <trackList>
                <track><title>A</title><location>http://a/</location></track>
                <track><location>rel/b.mp3</location></track>
              </trackList>
            </playlist>
            """;

        Assert.Equal(["http://a/", "http://stacja.test/listy/rel/b.mp3"], Parse(text, PlaylistKind.Xspf));
    }

    [Fact]
    public void XspfWithDtdIsRefused()
    {
        const string text = """
            <?xml version="1.0"?>
            <!DOCTYPE playlist [<!ENTITY u "http://zly/">]>
            <playlist xmlns="http://xspf.org/ns/0/"><trackList><track><location>&u;</location></track></trackList></playlist>
            """;

        Assert.Empty(Parse(text, PlaylistKind.Xspf));
    }

    [Fact]
    public void OnlyHttpSchemesPass() =>
        Assert.Equal(
            ["http://a/"],
            Parse("mms://x/y\nrtsp://x/y\nfile:///etc/passwd\nhttp://a/\nC:\\muzyka\\a.mp3\njavascript:alert(1)\n", PlaylistKind.M3u));

    [Fact]
    public void DuplicatesAreDropped() =>
        Assert.Equal(["http://a/"], Parse("http://a/\nhttp://a/\n", PlaylistKind.M3u));

    [Theory]
    [InlineData((int)PlaylistKind.M3u, "#EXTM3U\n#EXTINF:-1,x\n")]
    [InlineData((int)PlaylistKind.Pls, "[playlist]\nNumberOfEntries=0\n")]
    [InlineData((int)PlaylistKind.Asx, "<asx></asx>")]
    [InlineData((int)PlaylistKind.Xspf, "<playlist xmlns='http://xspf.org/ns/0/'><trackList/></playlist>")]
    public void EmptyListGivesNoEntries(int kind, string text) =>
        Assert.Empty(Parse(text, (PlaylistKind)kind));

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    public void MutationsNeverThrow(int seed)
    {
        var random = new Random(seed);
        (PlaylistKind Kind, string Text)[] samples =
        [
            (PlaylistKind.M3u, "#EXTM3U\r\n#EXTINF:-1,x\r\nhttp://a/b\r\nwzgledny.mp3\r\n"),
            (PlaylistKind.Pls, "[playlist]\r\nFile1=http://a/\r\nFile2=http://b/\r\n"),
            (PlaylistKind.Asx, "<asx><entry><ref href=\"http://a/\"/></entry></asx>"),
            (PlaylistKind.Xspf, "<playlist xmlns='http://xspf.org/ns/0/'><trackList><track><location>http://a/</location></track></trackList></playlist>"),
        ];

        for (var round = 0; round < 3000; round++)
        {
            var (kind, text) = samples[random.Next(samples.Length)];
            var data = Encoding.UTF8.GetBytes(text);
            for (var change = random.Next(1, 6); change > 0; change--)
                data[random.Next(data.Length)] = (byte)random.Next(256);

            var urls = PlaylistParser.Parse(data.AsSpan(0, random.Next(data.Length + 1)), kind, Base);

            Assert.All(urls, u => Assert.True(u.Scheme is "http" or "https"));
        }
    }
}
