using Cewka.Audio.Dsp;

namespace Cewka.App.Services;

/// <summary>
/// Dane dźwięku, które czytają wizualizacje sceny. Interfejs pozwala podstawić atrapę w testach
/// bez uruchamiania silnika dźwięku.
/// </summary>
public interface ISceneAudioSource
{
    /// <summary>Częstotliwość próbkowania bieżącego strumienia; 0, gdy nic nie gra.</summary>
    int SampleRate { get; }

    /// <summary>Kopiuje ostatnie ramki przebiegu L/P. False, gdy dane są niedostępne.</summary>
    bool ReadWaveform(Span<float> left, Span<float> right);

    /// <summary>RMS i szczyt kanałów z ostatnich ramek.</summary>
    ChannelLevels ReadLevels(int frames);

    /// <summary>Amplitudy liniowe w 48 pasmach; zakres ważny do następnego wywołania.</summary>
    ReadOnlySpan<float> ReadSpectrum();
}

/// <summary>Przejściówka na <see cref="SceneAudio"/> z grafu dźwięku.</summary>
public sealed class GraphSceneAudioSource(SceneAudio audio) : ISceneAudioSource
{
    public int SampleRate => audio.SampleRate;

    public bool ReadWaveform(Span<float> left, Span<float> right) => audio.ReadWaveform(left, right);

    public ChannelLevels ReadLevels(int frames) => audio.ReadLevels(frames);

    public ReadOnlySpan<float> ReadSpectrum() => audio.ReadSpectrum();
}
