using System.Net.Sockets;
using System.Security.Authentication;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

public class RetryPolicyTests
{
    private static readonly LiveFailure Transient = new(LiveFailureReason.StreamEnded);
    private static readonly LiveFailure Gone = new(LiveFailureReason.NotFound, 404);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static RetryPolicy Policy(int urls = 1, ManualClock? clock = null, int seed = 1) =>
        new(new LiveOptions { Time = clock ?? new ManualClock(), Random = new Random(seed) }, urls);

    /// <summary>Zmiana sieci przebaczyła porażkę, której nie było, a potem stabilna gra wyzerowała licznik.</summary>
    [Fact]
    public void FailureAfterStablePlayFollowingANetworkChangeStillRetries()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);
        policy.OnFailure(Transient, false, null);
        policy.ForgiveNextFailure();
        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(61);

        var decision = policy.OnFailure(Transient, false, null);

        Assert.Equal(RetryAction.RetryNextUrl, decision.Action);
        Assert.InRange(decision.Delay.TotalSeconds, 0.4, 0.6);
    }

    /// <summary>Stabilna gra zamyka sprawę: przebaczenie z poprzedniej serii nie dotyczy następnej.</summary>
    [Fact]
    public void StablePlayClearsTheForgiveness()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);
        policy.OnFailure(Transient, false, null);
        policy.ForgiveNextFailure();
        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(61);
        policy.OnFailure(Transient, false, null);

        var second = policy.OnFailure(Transient, false, null);

        Assert.InRange(second.Delay.TotalSeconds, 0.8, 1.2);
    }

    /// <summary>Pierwsza przejściowa porażka po trwałej i zmianie sieci nie ma czego przebaczać.</summary>
    [Fact]
    public void ForgivenessWithNothingCountedStillGivesTheFirstDelay()
    {
        var policy = Policy(urls: 2);
        policy.OnFailure(Gone, true, null);
        policy.ForgiveNextFailure();

        var decision = policy.OnFailure(Transient, false, null);

        Assert.InRange(decision.Delay.TotalSeconds, 0.4, 0.6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DelaysFollowBackoffWithinJitter(int seed)
    {
        var policy = Policy(seed: seed);
        double[] steps = [0.5, 1, 2, 4, 8, 16, 30];

        foreach (var step in steps)
        {
            var decision = policy.OnFailure(Transient, false, null);

            Assert.Equal(RetryAction.RetryNextUrl, decision.Action);
            Assert.InRange(decision.Delay.TotalSeconds, step * 0.8, step * 1.2);
        }
    }

    [Fact]
    public void EighthFailureGivesUpWithLastReason()
    {
        var policy = Policy();
        for (var i = 0; i < 7; i++)
            Assert.Equal(RetryAction.RetryNextUrl, policy.OnFailure(Transient, false, null).Action);

        var last = new LiveFailure(LiveFailureReason.Stalled);
        var decision = policy.OnFailure(last, false, null);

        Assert.Equal(RetryAction.GiveUp, decision.Action);
        Assert.Equal(last, decision.Failure);
    }

    [Fact]
    public void ThreeMinutesGiveUpWhateverTheCount()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);

        policy.OnFailure(Transient, false, null);
        clock.Now += TimeSpan.FromMinutes(2.9);
        Assert.Equal(RetryAction.RetryNextUrl, policy.OnFailure(Transient, false, null).Action);

        clock.Now += TimeSpan.FromMinutes(0.1);
        Assert.Equal(RetryAction.GiveUp, policy.OnFailure(Transient, false, null).Action);
    }

    [Fact]
    public void SixtySecondsOfPlayingResets()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);
        for (var i = 0; i < 6; i++)
            policy.OnFailure(Transient, false, null);

        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(60);
        var decision = policy.OnFailure(Transient, false, null);

        Assert.InRange(decision.Delay.TotalSeconds, 0.4, 0.6);
    }

    [Fact]
    public void ShortPlayingDoesNotReset()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);
        for (var i = 0; i < 7; i++)
            policy.OnFailure(Transient, false, null);

        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(59);

        Assert.Equal(RetryAction.GiveUp, policy.OnFailure(Transient, false, null).Action);
    }

    [Fact]
    public void PlayingCountsFromFirstCallAfterFailure()
    {
        var clock = new ManualClock();
        var policy = Policy(clock: clock);
        policy.OnFailure(Transient, false, null);

        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(40);
        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(20);
        policy.OnFailure(Transient, false, null);
        clock.Now += TimeSpan.FromSeconds(1);

        // Po porażce licznik grania znów stoi.
        policy.OnPlaying();
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.InRange(policy.OnFailure(Transient, false, null).Delay.TotalSeconds, 0.8, 1.2);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(120, 60)]
    public void RetryAfterIsHonouredUpToSixtySeconds(int requested, int expected)
    {
        var decision = Policy().OnFailure(new LiveFailure(LiveFailureReason.ServerError, 503), false, TimeSpan.FromSeconds(requested));

        Assert.Equal(TimeSpan.FromSeconds(expected), decision.Delay);
    }

    [Fact]
    public void RetryAfterShorterThanBackoffDoesNotShorten() =>
        Assert.True(Policy().OnFailure(Transient, false, TimeSpan.FromSeconds(0.1)).Delay.TotalSeconds >= 0.4);

    [Fact]
    public void TransientMovesToNextUrlEachTime()
    {
        var policy = Policy(urls: 3);
        var seen = new List<int> { policy.CurrentUrl };

        for (var i = 0; i < 4; i++)
        {
            policy.OnFailure(Transient, false, null);
            seen.Add(policy.CurrentUrl);
        }

        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, seen);
    }

    [Fact]
    public void PermanentOnSingleUrlGivesUpAtOnce()
    {
        var decision = Policy().OnFailure(Gone, true, null);

        Assert.Equal(RetryAction.GiveUp, decision.Action);
        Assert.Equal(Gone, decision.Failure);
    }

    [Fact]
    public void PermanentMovesToSpareWithoutDelay()
    {
        var policy = Policy(urls: 2);

        var decision = policy.OnFailure(Gone, true, null);

        Assert.Equal(RetryAction.RetryNextUrl, decision.Action);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
        Assert.Equal(1, policy.CurrentUrl);
    }

    [Fact]
    public void AllPermanentGiveReasonOfFirstUrl()
    {
        var policy = Policy(urls: 2);

        policy.OnFailure(Gone, true, null);
        var decision = policy.OnFailure(new LiveFailure(LiveFailureReason.Forbidden, 403), true, null);

        Assert.Equal(RetryAction.GiveUp, decision.Action);
        Assert.Equal(Gone, decision.Failure);
    }

    [Fact]
    public void DeadUrlIsSkippedByLaterRetries()
    {
        var policy = Policy(urls: 3);

        policy.OnFailure(Gone, true, null);
        Assert.Equal(1, policy.CurrentUrl);
        policy.OnFailure(Transient, false, null);
        Assert.Equal(2, policy.CurrentUrl);
        policy.OnFailure(Transient, false, null);
        Assert.Equal(1, policy.CurrentUrl);
    }

    [Fact]
    public void LiveOpenExceptionKeepsReasonAndPermanence()
    {
        Assert.Equal(
            (new LiveFailure(LiveFailureReason.NotFound, 404), true),
            LiveErrors.Classify(new LiveOpenException(new LiveFailure(LiveFailureReason.NotFound, 404))));
        Assert.Equal(
            (new LiveFailure(LiveFailureReason.ServerError, 503), false),
            LiveErrors.Classify(new LiveOpenException(new LiveFailure(LiveFailureReason.ServerError, 503))));
        Assert.False(LiveErrors.Classify(new LiveOpenException(new LiveFailure(LiveFailureReason.Stalled))).Permanent);
    }

    [Fact]
    public void ExceptionsAreClassified()
    {
        void Check(Exception e, LiveFailureReason reason, bool permanent)
        {
            var (failure, isPermanent) = LiveErrors.Classify(e);
            Assert.Equal(reason, failure.Reason);
            Assert.Equal(permanent, isPermanent);
        }

        Check(new HttpRequestException(HttpRequestError.ConnectionError, "x", new AddressRefusedException("h")),
            LiveFailureReason.AddressRefused, true);
        Check(new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new AuthenticationException()),
            LiveFailureReason.TlsFailed, true);
        Check(new AuthenticationException(), LiveFailureReason.TlsFailed, true);
        Check(new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new IOException()),
            LiveFailureReason.ConnectionFailed, false);
        Check(new HttpRequestException(HttpRequestError.InvalidResponse), LiveFailureReason.NotAStream, true);
        Check(new HttpRequestException(HttpRequestError.NameResolutionError), LiveFailureReason.ConnectionFailed, false);
        Check(new HttpRequestException(HttpRequestError.ConnectionError), LiveFailureReason.ConnectionFailed, false);
        Check(new TaskCanceledException(), LiveFailureReason.ConnectionFailed, false);
        Check(new HttpRequestException(HttpRequestError.ResponseEnded), LiveFailureReason.StreamEnded, false);
        Check(new TimeoutException(), LiveFailureReason.ConnectionFailed, false);
        Check(new SocketException((int)SocketError.ConnectionRefused), LiveFailureReason.ConnectionFailed, false);
        Check(new HttpIOException(HttpRequestError.ResponseEnded), LiveFailureReason.StreamEnded, false);
        Check(new IOException(), LiveFailureReason.StreamEnded, false);
        Check(new InvalidOperationException(), LiveFailureReason.Unspecified, true);
    }
}
