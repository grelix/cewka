namespace Cewka.Platform;

/// <summary>
/// What the desktop shows about the music: the overlay and volume flyout in Windows, the media
/// panel of a Linux desktop through MPRIS.
/// <para>
/// The two are built on completely different foundations — one on the binary interface of
/// WinRT, the other on the session bus — but the player has exactly the same thing to tell
/// them, so it talks to both through this.
/// </para>
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
}
