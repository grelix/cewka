namespace Cewka.RadioProbe;

/// <param name="Host">Host adresu wejściowego; przekierowania wychodzą poza tę regułę, bo znamy je dopiero po fakcie.</param>
internal sealed record ProbeJob(string Host, Func<CancellationToken, Task> Run);

/// <summary>Jedno zapytanie naraz do hosta i ograniczona równoległość: narzędzie nie może obciążać nadawców.</summary>
internal static class ProbeRunner
{
    public static Task RunAsync(IReadOnlyList<ProbeJob> jobs, int parallel, CancellationToken ct)
    {
        var hosts = jobs.GroupBy(job => job.Host, StringComparer.OrdinalIgnoreCase);
        return Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
            async (host, token) =>
            {
                foreach (var job in host)
                    await job.Run(token).ConfigureAwait(false);
            });
    }
}
