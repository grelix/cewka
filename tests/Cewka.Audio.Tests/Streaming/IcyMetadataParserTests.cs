using System.Text;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Kodowania i niepoprawne bloki metadanych.</summary>
public class IcyMetadataParserTests
{
    private static string? TitleOf(byte[] titleBytes, string? country)
    {
        var block = "StreamTitle='"u8.ToArray().Concat(titleBytes).Concat("';StreamUrl='';"u8.ToArray()).ToArray();
        var padded = block.Concat(new byte[32 - block.Length % 16]).ToArray();
        return IcyMetadataParser.Parse(padded, IcyMetadataParser.FallbackFor(country)).Title;
    }

    private static Encoding Page(int number)
    {
        _ = IcyMetadataParser.FallbackFor(null);
        return Encoding.GetEncoding(number);
    }

    [Fact]
    public void Utf8WinsOverCountryPage() =>
        Assert.Equal("Żółć Ελ Привет", TitleOf(Encoding.UTF8.GetBytes("Żółć Ελ Привет"), "PL"));

    [Theory]
    [InlineData(1250, "PL", "Zażółć gęślą jaźń")]
    [InlineData(1250, "CZ", "Příliš žluťoučký kůň")]
    [InlineData(1250, "HU", "Árvíztűrő tükörfúrógép")]
    [InlineData(1250, "RO", "Pădure ţară")]
    [InlineData(1251, "UA", "Пісня про їжака")]
    [InlineData(1253, "GR", "Ελληνικό τραγούδι")]
    [InlineData(1253, "CY", "Ραδιόφωνο")]
    [InlineData(1254, "TR", "Türkçe şarkı ğİ")]
    [InlineData(1252, "DE", "Müller Straße")]
    [InlineData(1252, null, "Café déjà vu")]
    public void CountryPageDecodesNonUtf8(int page, string? country, string text) =>
        Assert.Equal(text, TitleOf(Page(page).GetBytes(text), country));

    [Fact]
    public void GreekWithLineBreakBecomesSingleLine()
    {
        var parsed = TitleOf(Page(1253).GetBytes("Ελληνικό\r\nτραγούδι"), "GR");

        Assert.Equal("Ελληνικό τραγούδι", IcyMetadataParser.Normalise(parsed, null));
    }

    [Fact]
    public void Latin1FallbackKeepsBytes() =>
        Assert.Equal("Müller", IcyMetadataParser.Parse(Encoding.Latin1.GetBytes("StreamTitle='Müller';"), Encoding.Latin1).Title);

    [Fact]
    public void BrokenUtf8FallsBackToPage()
    {
        // Prawidłowy początek UTF-8 z urwanym ciągiem.
        var bytes = new byte[] { 0xC5, 0x81, 0xC5 };
        Assert.Equal(Page(1250).GetString(bytes), TitleOf(bytes, "PL"));
    }

    [Fact]
    public void ApostropheAndSemicolonInsideTitle() =>
        Assert.Equal("Guns N' Roses; Don't Cry", TitleOf("Guns N' Roses; Don't Cry"u8.ToArray(), null));

    [Fact]
    public void KeyIgnoresCase() =>
        Assert.Equal("A", IcyMetadataParser.Parse("streamTITLE='A';"u8, Encoding.Latin1).Title);

    [Theory]
    [InlineData("StreamTitle='Bez konca", "Bez konca")]
    [InlineData("StreamTitle='Bez konca'", "Bez konca")]
    [InlineData("StreamTitle='Bez konca'\0\0\0", "Bez konca")]
    public void MissingTerminatorEndsAtBlockEnd(string block, string expected) =>
        Assert.Equal(expected, IcyMetadataParser.Parse(Encoding.Latin1.GetBytes(block), Encoding.Latin1).Title);

    [Fact]
    public void EmptyTitleIsEmptyAndMissingFieldIsNull()
    {
        Assert.Equal("", IcyMetadataParser.Parse("StreamTitle='';StreamUrl='x';\0\0"u8, Encoding.Latin1).Title);
        Assert.Null(IcyMetadataParser.Parse("StreamUrl='x';\0\0"u8, Encoding.Latin1).Title);
        Assert.Null(IcyMetadataParser.Parse(new byte[16], Encoding.Latin1).Title);
    }

    [Fact]
    public void AdvertMarkIsRecognised()
    {
        Assert.True(IcyMetadataParser.Parse("StreamTitle='Reklama';adw_ad='true';"u8, Encoding.Latin1).IsAd);
        Assert.False(IcyMetadataParser.Parse("StreamTitle='Utwor';adw_ad='false';"u8, Encoding.Latin1).IsAd);
    }

    [Fact]
    public void NormaliseRemovesDirectionAndControlCharacters() =>
        Assert.Equal("abc def", IcyMetadataParser.Normalise("a‮b⁦c\u0007 \u0001\r\n  d⁩e‪f", null));

    [Fact]
    public void NormaliseLimitsLength()
    {
        var result = IcyMetadataParser.Normalise(new string('a', 5000), null);

        Assert.Equal(300, result!.Length);
    }

    [Fact]
    public void NormaliseDoesNotSplitSurrogatePair()
    {
        var result = IcyMetadataParser.Normalise(new string('a', 299) + "\U0001F3B5", null);

        Assert.Equal(299, result!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("‮")]
    [InlineData("Radio Testowe")]
    [InlineData("  radio testowe ")]
    public void NormaliseGivesNullForEmptyOrStationName(string title) =>
        Assert.Null(IcyMetadataParser.Normalise(title, "Radio Testowe"));

    [Theory]
    [InlineData("Now Playing info goes here")]
    [InlineData("Now Playing")]
    [InlineData("NOW PLAYING...")]
    [InlineData("  now playing: ")]
    [InlineData("Now Playing info goes here!")]
    [InlineData("No Title")]
    [InlineData("Unknown")]
    [InlineData("unknown -")]
    public void NormaliseGivesNullForFillerTitles(string title) =>
        Assert.Null(IcyMetadataParser.Normalise(title, "Radio Testowe"));

    [Theory]
    [InlineData("Now Playing Tomorrow")]
    [InlineData("Unknown Pleasures")]
    [InlineData("No Title Needed")]
    public void NormaliseKeepsRealTitlesResemblingFillers(string title) =>
        Assert.Equal(title, IcyMetadataParser.Normalise(title, "Radio Testowe"));

    [Fact]
    public void UnknownCountryUsesWesternPage() =>
        Assert.Equal(1252, IcyMetadataParser.FallbackFor("XX").CodePage);

    [Fact]
    public void VietnameseCodePageIsAvailable() =>
        Assert.Equal(1258, IcyMetadataParser.FallbackFor("VN").CodePage);

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void MutatedBlocksNeverThrow(int seed)
    {
        var random = new Random(seed);
        var original = "StreamTitle='Tytuł; utworu';StreamUrl='http://x/';adw_ad='true';"u8.ToArray();
        var encoding = IcyMetadataParser.FallbackFor("PL");

        for (var round = 0; round < 3000; round++)
        {
            var block = original.ToArray();
            for (var change = random.Next(1, 8); change > 0; change--)
                block[random.Next(block.Length)] = (byte)random.Next(256);
            var cut = block.AsSpan(0, random.Next(block.Length + 1));

            var fields = IcyMetadataParser.Parse(cut, encoding);
            var title = IcyMetadataParser.Normalise(fields.Title, "Stacja");

            if (title is not null)
            {
                Assert.True(title.Length <= 300);
                Assert.DoesNotContain(title, char.IsControl);
            }
        }
    }
}
