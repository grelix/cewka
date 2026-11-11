using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

public class ForbiddenUrlPartsTests
{
    private static IReadOnlyList<string> Find(string url) => ForbiddenUrlParts.Find(new Uri(url));

    [Theory]
    [InlineData("listenerid")]
    [InlineData("awparams")]
    [InlineData("amsparams")]
    [InlineData("preroll")]
    [InlineData("aggregator")]
    [InlineData("dist")]
    [InlineData("source")]
    [InlineData("origem")]
    [InlineData("origine")]
    [InlineData("devicename")]
    [InlineData("playerid")]
    [InlineData("playsessionid")]
    [InlineData("sabc")]
    [InlineData("gdpr")]
    [InlineData("gdpr_consent")]
    [InlineData("rj-tok")]
    [InlineData("zt")]
    [InlineData("aw_0_1st.skey")]
    [InlineData("aw_0_req.gdpr")]
    public void Listener_and_advertising_parameters_are_found(string name)
    {
        Assert.Contains(name, Find($"https://example.invalid/stream?a=1&{name}=x"));
        Assert.Contains(name, Find($"https://example.invalid/stream?{name.ToUpperInvariant()}=x"));
    }

    [Theory]
    [InlineData("tunein")]
    [InlineData("airable")]
    [InlineData("mytuner")]
    [InlineData("radiogarden")]
    [InlineData("radioplayer")]
    [InlineData("homepage")]
    [InlineData("adsonline")]
    public void Path_parts_named_after_other_platforms_are_found(string part)
    {
        Assert.Contains("/" + part, Find($"https://example.invalid/stream_for/{part}/aac"));
        Assert.Contains("/" + part, Find($"https://example.invalid/{part.ToUpperInvariant()}"));
    }

    [Fact]
    public void Server_playlist_path_is_not_a_platform_name()
    {
        Assert.Empty(Find("https://example.invalid/tunein/maryja.pls"));
        Assert.Contains("/tunein", Find("https://example.invalid/tunein/a/b.pls"));
        Assert.Contains("/tunein", Find("https://example.invalid/tunein/a.mp3"));
    }

    [Fact]
    public void Web_token_is_found_in_path_and_query()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

        Assert.Contains("JWT", Find($"https://example.invalid/{token}/stream"));
        Assert.Contains("JWT", Find($"https://example.invalid/stream?t={token}"));
    }

    [Theory]
    [InlineData("https://example.invalid/stream.mp3")]
    [InlineData("https://example.invalid/stream.mp3?type=.mp3&sid=1")]
    [InlineData("https://example.invalid/resource/stream")]
    [InlineData("https://example.invalid/tuneinfo/distance")]
    [InlineData("https://zt.example.invalid/source/?distance=1")]
    public void Clean_addresses_have_nothing(string url)
    {
        Assert.Empty(Find(url));
    }

    [Fact]
    public void Each_part_is_reported_once()
    {
        var found = Find("https://example.invalid/airable/x/airable?zt=1&zt=2&listenerid=3");

        Assert.Equal(["zt", "listenerid", "/airable"], found);
    }
}
