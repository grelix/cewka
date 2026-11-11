using System.Text.Json;
using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Streaming;
using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

/// <summary>Całe narzędzie od argumentów po raport, na atrapie stacji.</summary>
public sealed class ProbeAppTests : IDisposable
{
    private const string Head = "ICY 200 OK\r\nicy-br:128\r\ncontent-type:audio/mpeg\r\n";

    private static readonly byte[] Noise = TestAudioFiles.Mp3NoiseFrames(120, 5);
    private static readonly LiveOptions PublicLoopback = new() { IsLocalAddress = _ => false };
    private static readonly TestCertificates Certificates = new();
    private static readonly LiveOptions PublicSecureLoopback = PublicLoopback with { TrustedRootForTests = Certificates.Root };

    private readonly string _directory = Directory.CreateTempSubdirectory("cewka-radioprobe-").FullName;
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();

    public void Dispose() => Directory.Delete(_directory, true);

    private string ReportPath => Path.Combine(_directory, "wynik.json");

    private static Func<FakeRequest, Stream, CancellationToken, Task> Good =>
        async (_, stream, ct) =>
        {
            await stream.RespondAsync(Head, Noise, ct);
            await FakeRadio.HoldAsync(stream, ct);
        };

    private Task<int> Run(LiveOptions? options, params string[] args) =>
        ProbeApp.RunAsync(args, _output, _errors, options, CancellationToken.None);

    private string StationFile(params (string Id, string Country, string[] Streams, string[] Logos)[] stations)
    {
        var path = Path.Combine(_directory, "stacje.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            stacje = stations.Select(s => new
            {
                id = s.Id,
                nazwa = s.Id,
                kraj = s.Country,
                strumienie = s.Streams.Select(url => new { url }),
                logo = s.Logos.Select(url => new { url }),
            }),
        }));
        return path;
    }

    private JsonElement Report() => JsonDocument.Parse(File.ReadAllText(ReportPath)).RootElement;

    [Fact]
    public async Task Exit_code_is_zero_when_every_address_works_and_reports_are_written()
    {
        await using var server = new FakeRadioServer(Good);

        var code = await Run(null, "--url", server.Url().ToString(), "--max-kb", "32", "--measured-from", "PL", "--report", ReportPath);

        Assert.Equal(0, code);
        var report = Report();
        Assert.Equal("PL", report.GetProperty("MeasuredFrom").GetString());
        var stream = Assert.Single(report.GetProperty("Streams").EnumerateArray());
        Assert.Equal("Mp3", stream.GetProperty("Codec").GetString());
        Assert.Equal(200, stream.GetProperty("HttpStatus").GetInt32());
        Assert.True(stream.GetProperty("IcyStatusLine").GetBoolean());

        var text = File.ReadAllText(Path.ChangeExtension(ReportPath, ".txt"));
        Assert.Contains("ICY 200 OK", text);
        Assert.Contains("Pomiar z: PL", text);
        Assert.Contains(server.Url().ToString(), text);
    }

    [Fact]
    public async Task Exit_code_is_one_when_part_of_the_stations_fail_and_codes_are_summed_up()
    {
        await using var good = new FakeRadioServer(Good);
        await using var bad = new FakeRadioServer(FakeRadio.Respond("HTTP/1.1 404 Nie\r\nconnection:close\r\n"), "127.0.0.2");
        var stations = StationFile(("a", "PL", [good.Url().ToString()], []), ("b", "PL", [bad.Url().ToString()], []));

        var code = await Run(PublicLoopback, "--stations", stations, "--max-kb", "32", "--report", ReportPath);

        Assert.Equal(1, code);
        var report = Report();
        Assert.Equal(2, report.GetProperty("Streams").GetArrayLength());
        Assert.Equal(1, report.GetProperty("Summary").GetProperty("Failed").GetInt32());
        Assert.Equal(1, report.GetProperty("Summary").GetProperty("Reasons").GetProperty("NotFound").GetInt32());
        Assert.Contains("NotFound", File.ReadAllText(Path.ChangeExtension(ReportPath, ".txt")));
    }

    [Fact]
    public async Task Catalogue_station_on_a_local_address_fails_with_real_rules()
    {
        await using var server = new FakeRadioServer(Good);
        var stations = StationFile(("a", "PL", [server.Url().ToString()], []));

        var code = await Run(null, "--stations", stations, "--report", ReportPath);

        Assert.Equal(1, code);
        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal("AddressRefused",
            Report().GetProperty("Streams")[0].GetProperty("Failure").GetProperty("Reason").GetString());
    }

    [Theory]
    [InlineData("--max-kb", "500")]
    [InlineData("--seconds", "7")]
    public async Task Limits_over_the_maximum_are_refused_without_any_connection(string option, string value)
    {
        await using var server = new FakeRadioServer(Good);

        var code = await Run(null, "--url", server.Url().ToString(), option, value, "--report", ReportPath);

        Assert.Equal(2, code);
        Assert.Contains(option, _errors.ToString());
        Assert.Equal(0, server.ConnectionCount);
        Assert.False(File.Exists(ReportPath));
    }

    [Fact]
    public async Task Unreadable_station_list_is_a_tool_error()
    {
        Assert.Equal(2, await Run(null, "--stations", Path.Combine(_directory, "nie-ma.json"), "--report", ReportPath));
    }

    [Fact]
    public async Task Empty_selection_is_a_tool_error()
    {
        var stations = StationFile(("a", "PL", ["http://example.invalid/"], []));

        Assert.Equal(2, await Run(null, "--stations", stations, "--country", "CZ", "--report", ReportPath));
    }

    [Fact]
    public async Task Report_inside_a_repository_is_refused()
    {
        await using var server = new FakeRadioServer(Good);
        var repository = Directory.CreateDirectory(Path.Combine(_directory, "repo"));
        repository.CreateSubdirectory(".git");
        var nested = repository.CreateSubdirectory("wyniki");
        var path = Path.Combine(nested.FullName, "wynik.json");

        var code = await Run(null, "--url", server.Url().ToString(), "--report", path);

        Assert.Equal(2, code);
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(nested.GetFiles());
    }

    [Fact]
    public async Task Report_inside_a_linked_worktree_is_refused()
    {
        await using var server = new FakeRadioServer(Good);
        var repository = Directory.CreateDirectory(Path.Combine(_directory, "drzewo"));
        File.WriteAllText(Path.Combine(repository.FullName, ".git"), "gitdir: gdzie-indziej");

        Assert.Equal(2, await Run(null, "--url", server.Url().ToString(), "--report", Path.Combine(repository.FullName, "w.json")));
    }

    [Fact]
    public async Task Default_report_goes_to_the_system_temp_directory()
    {
        await using var server = new FakeRadioServer(Good);

        Assert.Equal(0, await Run(null, "--url", server.Url().ToString(), "--max-kb", "32"));

        var line = _output.ToString().Split('\n').Single(l => l.StartsWith("Raport: ", StringComparison.Ordinal));
        var path = line["Raport: ".Length..].Trim();
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path);
        Assert.True(File.Exists(path));
        File.Delete(path);
        File.Delete(Path.ChangeExtension(path, ".txt"));
    }

    [Fact]
    public async Task Nothing_from_the_stream_is_written_to_disk()
    {
        await using var server = new FakeRadioServer(Good);

        Assert.Equal(0, await Run(null, "--url", server.Url().ToString(), "--max-kb", "32", "--report", ReportPath));

        var files = new DirectoryInfo(_directory).GetFiles();
        Assert.Equal(["wynik.json", "wynik.txt"], files.Select(f => f.Name).Order());
        Assert.All(files, f => Assert.True(f.Length < 8 * 1024, $"{f.Name}: {f.Length} B"));
    }

    [Fact]
    public async Task Addresses_of_one_host_go_one_at_a_time()
    {
        var running = 0;
        var most = 0;
        await using var server = new FakeRadioServer(async (_, stream, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            try
            {
                // Zapytania naraz na pewno by się tu nałożyły.
                await Task.Delay(100, ct);
                await stream.RespondAsync(Head, Noise, ct);
            }
            finally
            {
                // Przed podtrzymaniem: serwer zauważa rozłączenie klienta później, niż klient otwiera następne połączenie.
                Interlocked.Decrement(ref running);
            }

            await FakeRadio.HoldAsync(stream, ct);
        });
        var stations = StationFile(
            ("a", "PL", [server.Url("/1").ToString(), server.Url("/2").ToString()], []),
            ("b", "PL", [server.Url("/3").ToString()], []));

        var code = await Run(PublicLoopback, "--stations", stations, "--parallel", "3", "--max-kb", "32", "--report", ReportPath);

        Assert.Equal(0, code);
        Assert.Equal(3, server.ConnectionCount);
        Assert.Equal(1, most);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact]
    public async Task Loudness_is_measured_only_for_the_first_stations()
    {
        await using var first = new FakeRadioServer(Good);
        await using var second = new FakeRadioServer(Good, "127.0.0.2");
        var stations = StationFile(("a", "PL", [first.Url().ToString()], []), ("b", "PL", [second.Url().ToString()], []));

        await Run(PublicLoopback, "--stations", stations, "--loudness", "1", "--max-kb", "32", "--report", ReportPath);

        var streams = Report().GetProperty("Streams");
        Assert.True(streams[0].TryGetProperty("Lufs", out var lufs) && lufs.ValueKind == JsonValueKind.Number);
        Assert.False(streams[1].TryGetProperty("Lufs", out _));
    }

    [Fact]
    public async Task Logos_are_probed_only_when_asked()
    {
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/logo.png"
            ? stream.RespondAsync("HTTP/1.1 200 OK\r\n", ImageHeaderTests.Png(100, 50), ct)
            : Good(request, stream, ct), certificate: Certificates.Server);
        var stations = StationFile(("a", "PL", [server.Url().ToString()], [server.Url("/logo.png").ToString()]));

        await Run(PublicSecureLoopback, "--stations", stations, "--max-kb", "32", "--report", ReportPath);
        Assert.Equal(0, Report().GetProperty("Logos").GetArrayLength());

        await Run(PublicSecureLoopback, "--stations", stations, "--logos", "--max-kb", "32", "--report", ReportPath);
        var logo = Assert.Single(Report().GetProperty("Logos").EnumerateArray());
        Assert.Equal("png", logo.GetProperty("Format").GetString());
        Assert.Equal(100, logo.GetProperty("Width").GetInt32());
        Assert.Equal(50, logo.GetProperty("Height").GetInt32());
    }

    [Fact]
    public async Task Failed_logo_counts_towards_the_exit_code()
    {
        await using var server = new FakeRadioServer((request, stream, ct) => request.Path == "/logo.png"
            ? stream.RespondAsync("HTTP/1.1 404 Nie\r\nconnection:close\r\n", [], ct)
            : Good(request, stream, ct), certificate: Certificates.Server);
        var stations = StationFile(("a", "PL", [server.Url().ToString()], [server.Url("/logo.png").ToString()]));

        Assert.Equal(1, await Run(PublicSecureLoopback, "--stations", stations, "--logos", "--max-kb", "32", "--report", ReportPath));
    }

    [Fact]
    public async Task Invalid_address_in_the_list_is_a_station_failure()
    {
        var stations = StationFile(("a", "PL", ["to nie jest adres", "mms://example.invalid/x"], []));

        Assert.Equal(1, await Run(null, "--stations", stations, "--report", ReportPath));

        var streams = Report().GetProperty("Streams");
        Assert.Equal(2, streams.GetArrayLength());
        Assert.All(streams.EnumerateArray(), s =>
            Assert.Equal("UrlBlocked", s.GetProperty("Failure").GetProperty("Reason").GetString()));
    }

    [Fact]
    public async Task Country_and_limit_narrow_the_catalogue()
    {
        await using var server = new FakeRadioServer(Good);
        var url = server.Url().ToString();
        var stations = StationFile(("a", "CZ", [url + "?1"], []), ("b", "PL", [url + "?2"], []), ("c", "PL", [url + "?3"], []));

        await Run(PublicLoopback, "--stations", stations, "--country", "PL", "--limit", "1", "--max-kb", "32", "--report", ReportPath);

        var stream = Assert.Single(Report().GetProperty("Streams").EnumerateArray());
        Assert.Equal("b", stream.GetProperty("StationId").GetString());
    }
}
