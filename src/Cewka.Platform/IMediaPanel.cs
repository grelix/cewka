namespace Cewka.Platform;

/// <summary>
/// To, co pulpit pokazuje o muzyce: nakładka i panel głośności w Windows, panel multimediów
/// pulpitu Linuksa przez MPRIS.
/// </summary>
public interface IMediaPanel : IDisposable
{
    void SetTrack(string title, string? artist, string? album, TimeSpan length);

    void SetStatus(MediaPanelStatus status);

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
