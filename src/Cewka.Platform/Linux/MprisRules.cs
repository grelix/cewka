namespace Cewka.Platform.Linux;

/// <summary>The parts of the MPRIS behaviour that need no bus, kept apart so they can be checked anywhere.</summary>
internal static class MprisRules
{
    /// <summary>
    /// Prefix of track identifiers. Specyfikacja zastrzega przestrzeń <c>/org/mpris</c> dla
    /// siebie, więc odtwarzacz nazywa utwory we własnej.
    /// </summary>
    public const string TrackIdPrefix = "/pl/maciejgrela/Cewka/Track/";

    public const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    /// <summary>
    /// Whether a <c>SetPosition</c> request applies: the track must be the current one and the
    /// position inside it, as the specification requires.
    /// </summary>
    public static bool IsPositionRequestValid(
        string track, long positionMicroseconds, string currentTrack, long lengthMicroseconds) =>
        track == currentTrack && currentTrack != NoTrack
                              && positionMicroseconds >= 0 && positionMicroseconds <= lengthMicroseconds;

    /// <summary>Accepts <c>file://</c> addresses only; the player plays local files.</summary>
    public static bool TryGetLocalPath(string uri, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return false;

        path = parsed.LocalPath;
        return path.Length > 0;
    }
}

/// <summary>
/// Tells a jump in the playing position from normal playback.
///
/// <para>Panele ekstrapolują pozycję z ostatniego odczytu, więc skok, którego zwykłe granie nie
/// tłumaczy, trzeba ogłosić sygnałem <c>Seeked</c>. Oczekiwana pozycja liczona jest z zegara,
/// a nie z liczby odświeżeń: przy zminimalizowanym oknie odświeżenia przychodzą rzadziej.</para>
/// </summary>
internal sealed class SeekDetector
{
    /// <summary>Larger than any drift between the refresh clock and the audio clock.</summary>
    public const long ToleranceMilliseconds = 1000;

    private TimeSpan? _last;
    private long _lastTime;

    /// <param name="position">Pozycja odtwarzania.</param>
    /// <param name="playing">Czy od poprzedniego odczytu trwało odtwarzanie.</param>
    /// <param name="now">Czas w milisekundach z zegara monotonicznego.</param>
    public bool Report(TimeSpan position, bool playing, long now)
    {
        var last = _last;
        var elapsed = now - _lastTime;

        _last = position;
        _lastTime = now;

        if (last is null) return false;

        var expected = last.Value + (playing ? TimeSpan.FromMilliseconds(elapsed) : TimeSpan.Zero);
        return Math.Abs((position - expected).TotalMilliseconds) > ToleranceMilliseconds;
    }

    public void Reset() => _last = null;
}
