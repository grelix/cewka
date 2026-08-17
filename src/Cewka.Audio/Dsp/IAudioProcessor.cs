namespace Cewka.Audio.Dsp;

/// <summary>
/// One stage of the signal chain. Every implementation is called from the audio thread and
/// must process in place without allocating.
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Prepares internal state for a given format. Called before playback starts and
    /// whenever the device format changes — never from the audio thread.
    /// </summary>
    void Prepare(int sampleRate, int channels);

    /// <summary>Processes <paramref name="frames"/> interleaved frames in place.</summary>
    void Process(Span<float> buffer, int frames);

    /// <summary>Clears history: filter state, envelopes, delay lines.</summary>
    void Reset();
}
