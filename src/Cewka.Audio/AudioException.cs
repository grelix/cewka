namespace Cewka.Audio;

/// <summary>
/// Raised when the audio layer cannot carry out an operation: a device that will not open,
/// a damaged file, a format outside the player's scope. The message is written for the
/// person using the application, because it ends up on screen.
/// </summary>
public sealed class AudioException : Exception
{
    public AudioException(string message) : base(message) { }

    public AudioException(string message, Exception inner) : base(message, inner) { }
}
