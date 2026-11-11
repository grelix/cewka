namespace Cewka.Platform;

/// <summary>
/// To, co pulpit pokazuje o muzyce: nakładka i panel głośności w Windows, panel multimediów
/// pulpitu Linuksa przez MPRIS.
/// </summary>
public interface IMediaPanel : IDisposable
{
    void SetTrack(string title, string? artist, string? album, TimeSpan length);

    void SetStatus(MediaPanelStatus status);

    /// <summary>
    /// Marks what plays as a live station: no length and no seeking. <paramref name="artFile"/> is a local
    /// image (the station logo from the cache) or null. Only MPRIS uses it; the default does nothing.
    /// </summary>
    void SetLive(bool live, string? artFile)
    {
    }

    /// <summary>
    /// Whether the panel's Next and Previous buttons make sense (a list of more than one station).
    /// The default leaves the panel as it is.
    /// </summary>
    void SetNavigation(bool enabled)
    {
    }

    /// <summary>Empties the panel, used when the queue is cleared.</summary>
    void Clear();

    /// <summary>
    /// Reports the playing position. Only MPRIS asks for it; the Windows panel keeps its own
    /// timeline, so the default does nothing.
    /// </summary>
    void SetPosition(TimeSpan position)
    {
    }

    /// <summary>
    /// Reports the volume, between 0 and 1. Only MPRIS shows it; the Windows panel has none of
    /// its own, so the default does nothing.
    /// </summary>
    void SetVolume(double volume)
    {
    }
}
