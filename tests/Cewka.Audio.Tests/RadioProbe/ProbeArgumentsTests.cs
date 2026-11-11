using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

public class ProbeArgumentsTests
{
    private static ProbeArguments Parsed(params string[] args)
    {
        Assert.True(ProbeArguments.TryParse(args, out var parsed, out var error), error);
        return parsed!;
    }

    private static string Rejected(params string[] args)
    {
        Assert.False(ProbeArguments.TryParse(args, out _, out var error));
        return error!;
    }

    [Fact]
    public void Defaults_stay_within_the_limits()
    {
        var parsed = Parsed("--stations", "lista.json");

        Assert.Equal("lista.json", parsed.Stations);
        Assert.Equal(6, parsed.Seconds);
        Assert.Equal(200, parsed.MaxKb);
        Assert.Equal(2, parsed.Parallel);
        Assert.Equal(0, parsed.LoudnessStations);
        Assert.False(parsed.Logos);
    }

    [Fact]
    public void All_options_are_read()
    {
        var parsed = Parsed(
            "--url", "http://example.invalid/a", "--country", "pl", "--measured-from", "pl", "--loudness", "15",
            "--logos", "--parallel", "3", "--seconds", "1,5", "--max-kb", "64", "--limit", "5", "--report", "wynik.json");

        Assert.Equal("http://example.invalid/a", parsed.Url);
        Assert.Equal("PL", parsed.Country);
        Assert.Equal("PL", parsed.MeasuredFrom);
        Assert.Equal(15, parsed.LoudnessStations);
        Assert.True(parsed.Logos);
        Assert.Equal(3, parsed.Parallel);
        Assert.Equal(1.5, parsed.Seconds);
        Assert.Equal(64, parsed.MaxKb);
        Assert.Equal(5, parsed.Limit);
        Assert.Equal("wynik.json", parsed.Report);
    }

    [Theory]
    [InlineData("--max-kb", "500")]
    [InlineData("--max-kb", "201")]
    [InlineData("--max-kb", "abc")]
    [InlineData("--seconds", "6,5")]
    [InlineData("--seconds", "0")]
    [InlineData("--parallel", "0")]
    [InlineData("--parallel", "100")]
    [InlineData("--loudness", "-1")]
    [InlineData("--limit", "0")]
    [InlineData("--country", "Polska")]
    [InlineData("--measured-from", "P")]
    public void Values_outside_the_limits_are_rejected(string option, string value)
    {
        Assert.Contains(option, Rejected("--stations", "lista.json", option, value));
    }

    [Fact]
    public void Nothing_to_probe_is_an_error()
    {
        Assert.NotEmpty(Rejected());
        Assert.NotEmpty(Rejected("--logos"));
    }

    [Fact]
    public void Unknown_option_and_missing_value_are_errors()
    {
        Assert.Contains("--play", Rejected("--stations", "a.json", "--play"));
        Assert.Contains("--report", Rejected("--stations", "a.json", "--report"));
    }
}
