using System.Net.Sockets;
using System.Security.Authentication;

namespace Cewka.Audio.Streaming;

internal enum RetryAction { RetryNextUrl, GiveUp }

internal readonly record struct RetryDecision(RetryAction Action, TimeSpan Delay, LiveFailure Failure);

/// <summary>
/// Ponawianie po kolejnych adresach stacji. Porażka przejściowa czeka coraz dłużej i ma limit
/// prób i czasu; stała usuwa adres z obiegu, bo ponowienie nic by nie dało.
/// </summary>
internal sealed class RetryPolicy(LiveOptions options, int urlCount)
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly LiveFailure?[] _permanent = new LiveFailure?[urlCount];
    private int _attempts;
    private DateTimeOffset? _firstFailure;
    private DateTimeOffset? _playingSince;
    private bool _forgiveNext;

    /// <summary>Adres, z którym ma łączyć następna próba.</summary>
    public int CurrentUrl { get; private set; }

    public RetryDecision OnFailure(LiveFailure failure, bool permanent, TimeSpan? retryAfter)
    {
        var now = options.Time.GetUtcNow();
        if (_playingSince is { } since && now - since >= options.StableAfter)
        {
            _attempts = 0;
            _firstFailure = null;
            _forgiveNext = false;
        }

        _playingSince = null;

        if (permanent)
        {
            _permanent[CurrentUrl] ??= failure;
            if (!MoveToNextLiveUrl())
                return new RetryDecision(RetryAction.GiveUp, TimeSpan.Zero, _permanent[0] ?? failure);
            return new RetryDecision(RetryAction.RetryNextUrl, TimeSpan.Zero, failure);
        }

        _firstFailure ??= now;

        // Bez policzonej próby nie ma czego przebaczać, a opóźnienie liczy się od pierwszej.
        if (!_forgiveNext || _attempts == 0)
            _attempts++;
        _forgiveNext = false;
        if (_attempts >= options.MaxAttempts || now - _firstFailure.Value >= options.MaxRetryTime)
            return new RetryDecision(RetryAction.GiveUp, TimeSpan.Zero, failure);

        MoveToNextLiveUrl();
        return new RetryDecision(RetryAction.RetryNextUrl, Delay(retryAfter), failure);
    }

    /// <summary>Strumień gra; po StableAfter nieprzerwanego grania następna porażka liczy od nowa.</summary>
    public void OnPlaying() => _playingSince ??= options.Time.GetUtcNow();

    /// <summary>Limit czasu obowiązuje dalej, więc ciągłe zmiany sieci nie ponawiają w nieskończoność.</summary>
    public void ForgiveNextFailure() => _forgiveNext = true;

    private TimeSpan Delay(TimeSpan? retryAfter)
    {
        var step = options.Backoff[Math.Min(_attempts - 1, options.Backoff.Count - 1)];
        var spread = 1 + (options.Random.NextDouble() * 2 - 1) * options.Jitter;
        var delay = step * spread;

        if (retryAfter is { } requested && requested > delay)
            delay = requested < MaxRetryAfter ? requested : MaxRetryAfter;
        return delay;
    }

    private bool MoveToNextLiveUrl()
    {
        for (var step = 1; step <= urlCount; step++)
        {
            var candidate = (CurrentUrl + step) % urlCount;
            if (_permanent[candidate] is null)
            {
                CurrentUrl = candidate;
                return true;
            }
        }

        return false;
    }
}

internal static class LiveErrors
{
    /// <summary>Przyczyna i czy ponawianie ma sens. Stałe to wszystko poza siecią, serwerem i zastojem.</summary>
    public static (LiveFailure Failure, bool Permanent) Classify(Exception exception)
    {
        if (exception is LiveOpenException open)
            return (open.Failure, !open.Transient && !IsTransient(open.Failure.Reason));

        if (Find<AddressRefusedException>(exception) is not null)
            return Permanent(LiveFailureReason.AddressRefused, exception);
        if (Find<AuthenticationException>(exception) is not null)
            return Permanent(LiveFailureReason.TlsFailed, exception);

        // Certyfikat i protokół dają AuthenticationException, sprawdzony wyżej; błąd uzgadniania
        // bez niego to urwane połączenie. HttpIOException dziedziczy po IOException: urwanie w trakcie treści.
        switch (exception)
        {
            case HttpRequestException request:
                return request.HttpRequestError switch
                {
                    HttpRequestError.InvalidResponse or HttpRequestError.ConfigurationLimitExceeded
                        or HttpRequestError.HttpProtocolError
                        => Permanent(LiveFailureReason.NotAStream, exception),
                    HttpRequestError.ResponseEnded => Transient(LiveFailureReason.StreamEnded, exception),
                    _ => Transient(LiveFailureReason.ConnectionFailed, exception),
                };
            case TimeoutException or SocketException or OperationCanceledException:
                return Transient(LiveFailureReason.ConnectionFailed, exception);
            case IOException:
                return Transient(LiveFailureReason.StreamEnded, exception);
            default:
                return Permanent(LiveFailureReason.Unspecified, exception);
        }
    }

    private static bool IsTransient(LiveFailureReason reason) => reason is LiveFailureReason.ConnectionFailed
        or LiveFailureReason.ServerError or LiveFailureReason.StreamEnded or LiveFailureReason.Stalled;

    private static (LiveFailure, bool) Transient(LiveFailureReason reason, Exception exception) =>
        (new LiveFailure(reason, Detail: exception.Message), false);

    private static (LiveFailure, bool) Permanent(LiveFailureReason reason, Exception exception) =>
        (new LiveFailure(reason, Detail: exception.Message), true);

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
