namespace Cewka.Audio.Streaming;

/// <summary>Nie udało się otworzyć strumienia z rozpoznanej przyczyny.</summary>
/// <param name="transient">Ponowienie ma sens, choć sama przyczyna zwykle jest stała.</param>
internal sealed class LiveOpenException(LiveFailure failure, TimeSpan? retryAfter = null, bool transient = false)
    : Exception(failure.Detail ?? failure.Reason.ToString())
{
    public LiveFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool Transient { get; } = transient;
}
