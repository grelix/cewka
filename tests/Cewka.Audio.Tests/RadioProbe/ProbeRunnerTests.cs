using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

public class ProbeRunnerTests
{
    /// <summary>Licznik zadań trwających jednocześnie, razem i dla każdego hosta.</summary>
    private sealed class Concurrency
    {
        private readonly Dictionary<string, int> _perHost = [];
        private int _running;

        public int MaxRunning { get; private set; }
        public int MaxPerHost { get; private set; }

        public async Task Run(string host, CancellationToken ct)
        {
            lock (this)
            {
                _running++;
                _perHost[host] = _perHost.GetValueOrDefault(host) + 1;
                MaxRunning = Math.Max(MaxRunning, _running);
                MaxPerHost = Math.Max(MaxPerHost, _perHost[host]);
            }

            await Task.Delay(30, ct);

            lock (this)
            {
                _running--;
                _perHost[host]--;
            }
        }
    }

    [Fact]
    public async Task One_request_per_host_and_bounded_parallelism()
    {
        var counter = new Concurrency();
        var jobs = Enumerable.Range(0, 24)
            .Select(i => $"host{i % 6}")
            .Select(host => new ProbeJob(host, ct => counter.Run(host, ct)))
            .ToList();

        await ProbeRunner.RunAsync(jobs, 3, CancellationToken.None);

        Assert.Equal(1, counter.MaxPerHost);
        Assert.Equal(3, counter.MaxRunning);
    }

    [Fact]
    public async Task Host_case_does_not_make_a_second_host()
    {
        var counter = new Concurrency();
        var jobs = new[] { "Radio.example", "radio.example", "RADIO.EXAMPLE" }
            .Select(host => new ProbeJob(host, ct => counter.Run("jeden", ct)))
            .ToList();

        await ProbeRunner.RunAsync(jobs, 3, CancellationToken.None);

        Assert.Equal(1, counter.MaxPerHost);
    }

    [Fact]
    public async Task Every_job_runs_once_and_keeps_order_within_a_host()
    {
        var seen = new List<int>();
        var jobs = Enumerable.Range(0, 10)
            .Select(i => new ProbeJob("h", _ =>
            {
                seen.Add(i);
                return Task.CompletedTask;
            }))
            .ToList();

        await ProbeRunner.RunAsync(jobs, 4, CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 10), seen);
    }
}
