namespace Cewka.Audio.Playback;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>How the queue behaves once a track ends.</summary>
public enum RepeatMode
{
    /// <summary>Stop after the last track.</summary>
    None,

    /// <summary>Start the queue again from the beginning.</summary>
    Queue,

    /// <summary>Repeat the current track.</summary>
    Track,
}
