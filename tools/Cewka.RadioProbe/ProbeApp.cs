using Cewka.Audio;
using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

/// <summary>Od argumentów po raport i kod wyjścia: 0 bez błędów, 1 część adresów z błędem, 2 błąd narzędzia.</summary>
internal static class ProbeApp
{
    private const int ExitOk = 0;
    private const int ExitSomeFailed = 1;
    private const int ExitToolError = 2;

    private sealed record Work(ProbeStation Station, bool AllowLocal, bool MeasureLoudness);

    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter errors, LiveOptions? options, CancellationToken ct)
    {
        if (!ProbeArguments.TryParse(args, out var parsed, out var error))
        {
            await errors.WriteLineAsync(error);
            await errors.WriteLineAsync(ProbeArguments.Usage);
            return ExitToolError;
        }

        var arguments = parsed!;
        var now = DateTimeOffset.UtcNow;
        var reportPath = arguments.Report ?? ProbeReport.DefaultPath(now);
        if (ProbeReport.FindRepository(reportPath) is { } repository)
        {
            await errors.WriteLineAsync($"Raport nie może leżeć w repozytorium: {repository}");
            return ExitToolError;
        }

        IReadOnlyList<Work> work;
        try
        {
            work = Plan(arguments);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await errors.WriteLineAsync(e.Message);
            return ExitToolError;
        }

        if (work.Count == 0)
        {
            await errors.WriteLineAsync("Nie ma żadnej stacji do sprawdzenia.");
            return ExitToolError;
        }

        var network = options ?? new LiveOptions();
        var limits = new ProbeLimits(TimeSpan.FromSeconds(arguments.Seconds), arguments.MaxKb * 1024);
        var streams = new List<Func<ProbeResult?>>();
        var logos = new List<Func<LogoResult?>>();
        var jobs = BuildJobs(work, arguments, network, limits, output, streams, logos);

        var finished = true;
        try
        {
            await ProbeRunner.RunAsync(jobs, arguments.Parallel, ct);
        }
        catch (OperationCanceledException)
        {
            finished = false;
            await errors.WriteLineAsync("Przerwano; raport obejmuje to, co zdążono sprawdzić.");
        }
        catch (Exception e) when (e is AudioException or DllNotFoundException)
        {
            await errors.WriteLineAsync($"Biblioteka natywna: {e.Message}");
            return ExitToolError;
        }

        var streamResults = streams.Select(get => get()).OfType<ProbeResult>().ToList();
        var logoResults = logos.Select(get => get()).OfType<LogoResult>().ToList();
        var summary = ProbeSummary.From(streamResults, logoResults);
        var report = new ProbeReport
        {
            Generated = now,
            MeasuredFrom = arguments.MeasuredFrom,
            UserAgent = network.UserAgent,
            LimitSeconds = arguments.Seconds,
            LimitKb = arguments.MaxKb,
            Summary = summary,
            Streams = streamResults,
            Logos = logoResults,
        };

        try
        {
            report.Write(reportPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await errors.WriteLineAsync($"Nie można zapisać raportu: {e.Message}");
            return ExitToolError;
        }

        await output.WriteLineAsync($"Raport: {Path.GetFullPath(reportPath)}");
        await output.WriteLineAsync(
            $"Adresy: {summary.Addresses}, z błędem {summary.Failed}; logo: {logoResults.Count}, z błędem {summary.LogosFailed}");

        if (!finished)
            return ExitToolError;
        return summary.Failed + summary.LogosFailed > 0 ? ExitSomeFailed : ExitOk;
    }

    private static List<Work> Plan(ProbeArguments arguments)
    {
        var stations = new List<(ProbeStation Station, bool AllowLocal)>();
        if (arguments.Url is { } url)
            stations.Add((new ProbeStation("adres", null, arguments.Country, [url], []), true));
        if (arguments.Stations is { } path)
        {
            var selected = StationList.Select(StationList.Load(path), arguments.Country, arguments.Limit);
            stations.AddRange(selected.Select(station => (station, false)));
        }

        return stations
            .Select((item, index) => new Work(item.Station, item.AllowLocal, index < arguments.LoudnessStations))
            .ToList();
    }

    /// <summary>Wyniki zostają w kolejności listy, choć zadania kończą się w dowolnej.</summary>
    private static List<ProbeJob> BuildJobs(
        IReadOnlyList<Work> work, ProbeArguments arguments, LiveOptions network, ProbeLimits limits, TextWriter output,
        List<Func<ProbeResult?>> streams, List<Func<LogoResult?>> logos)
    {
        var writer = TextWriter.Synchronized(output);
        var stationProbe = new StationProbe(network, limits);
        var logoProbe = new LogoProbe(network, limits.Time);
        var jobs = new List<ProbeJob>();

        foreach (var (station, allowLocal, measureLoudness) in work)
        {
            foreach (var text in station.StreamUrls)
            {
                if (!TryParse(text, out var url))
                {
                    var invalid = new ProbeResult { StationId = station.Id, Url = text, Failure = Blocked(text) };
                    streams.Add(() => invalid);
                    writer.WriteLine(ProbeReport.StreamLine(invalid));
                    continue;
                }

                ProbeResult? result = null;
                streams.Add(() => result);
                var target = new ProbeTarget(station.Id, url!)
                {
                    StationName = station.Name,
                    CountryCode = station.Country,
                    AllowLocal = allowLocal,
                };
                jobs.Add(new ProbeJob(url!.Host, async ct =>
                {
                    result = await stationProbe.ProbeAsync(target, measureLoudness, ct).ConfigureAwait(false);
                    writer.WriteLine(ProbeReport.StreamLine(result));
                }));
            }

            if (!arguments.Logos)
                continue;

            foreach (var text in station.LogoUrls)
            {
                if (!TryParse(text, out var url))
                {
                    var invalid = new LogoResult { StationId = station.Id, Url = text, Failure = Blocked(text) };
                    logos.Add(() => invalid);
                    continue;
                }

                LogoResult? result = null;
                logos.Add(() => result);
                jobs.Add(new ProbeJob(url!.Host, async ct =>
                {
                    result = await logoProbe.ProbeAsync(station.Id, url, allowLocal, ct).ConfigureAwait(false);
                    writer.WriteLine(ProbeReport.LogoLine(result));
                }));
            }
        }

        return jobs;
    }

    private static bool TryParse(string text, out Uri? url) => Uri.TryCreate(text, UriKind.Absolute, out url);

    private static LiveFailure Blocked(string url) => new(LiveFailureReason.UrlBlocked, Detail: url);
}
