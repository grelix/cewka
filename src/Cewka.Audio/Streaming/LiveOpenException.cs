namespace Cewka.Audio.Streaming;

/// <summary>Nie udało się otworzyć strumienia z rozpoznanej przyczyny.</summary>
/// <param name="transient">Ponowienie ma sens, choć sama przyczyna zwykle jest stała.</param>
/// <param name="inner">Wyjątek, z którego powstała przyczyna; zostaje w łańcuchu dla tych, którzy po nim rozpoznają swoje.</param>
internal sealed class LiveOpenException(
    LiveFailure failure, TimeSpan? retryAfter = null, bool transient = false, Exception? inner = null)
    : Exception(failure.Detail ?? failure.Reason.ToString(), inner)
{
    public LiveFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool Transient { get; } = transient;
}
