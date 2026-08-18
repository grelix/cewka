using Cewka.Audio.Playback;

namespace Cewka.Audio.Dsp;

/// <summary>Supplies a normalisation gain for a queue entry, in decibels.</summary>
public interface ITrackGainSource
{
    /// <summary>
    /// Returns the gain to apply, or null when it is not known yet. Called from the decode
    /// thread as a track is opened, so it must return promptly — anything slow belongs in
    /// the background.
    /// </summary>
    double? GetGainDecibels(QueueEntry entry);
}

/// <summary>
/// The signal chain, in order: loudness normalisation, equaliser with its preamp, limiter,
/// analysis tap, master volume.
///
/// <para><b>Dlaczego taka kolejność.</b> Normalizacja idzie pierwsza, bo wyrównuje materiał
/// przed jakąkolwiek korekcją — inaczej ten sam ruch suwaka dawałby inny efekt na cichym
/// i na głośnym nagraniu. Limiter stoi za korektorem, bo to korektor wytwarza przesterowanie,
/// przed którym limiter ma chronić. Analiza sygnału jest przed regulacją głośności, żeby
/// fala na ekranie odpowiadała muzyce, a nie ustawieniu pokrętła. Głośność jest ostatnia,
/// bo ma być zwykłym mnożnikiem, którego nic już nie modyfikuje.</para>
/// </summary>
public sealed class AudioGraph
{
    private int _channels = 2;

    /// <summary>Gain from ReplayGain tags or from loudness analysis.</summary>
    public GainStage Normalisation { get; } = new(smoothingSeconds: 0.05);

    /// <summary>Podbicie basu i góry przy cichym słuchaniu. Domyślnie wyłączone.</summary>
    public Loudness Loudness { get; } = new();

    public Equaliser Equaliser { get; } = new();

    /// <summary>Harmoniczne najniższych tonów dla małych przetworników. Domyślnie wyłączony.</summary>
    public VirtualBass VirtualBass { get; } = new();

    /// <summary>Poszerzenie bazy stereo dla odsłuchu na głośnikach. Domyślnie wyłączone.</summary>
    public StereoWidth StereoWidth { get; } = new();

    /// <summary>Domieszka kanału przeciwnego dla odsłuchu w słuchawkach. Domyślnie wyłączony.</summary>
    public Crossfeed Crossfeed { get; } = new();

    /// <summary>Zawężenie rozpiętości dynamicznej. Domyślnie wyłączone.</summary>
    public DynamicRange DynamicRange { get; } = new();

    public Limiter Limiter { get; } = new();

    /// <summary>Master volume, driven by the slider.</summary>
    public GainStage Volume { get; } = new(smoothingSeconds: 0.02);

    public SpectrumAnalyser Analyser { get; } = new();

    /// <summary>When false the normalisation stage is bypassed and the gain returns to unity.</summary>
    public bool NormalisationEnabled { get; set; } = true;

    /// <summary>Set by the application; consulted whenever a track starts.</summary>
    public ITrackGainSource? GainSource { get; set; }

    public void Prepare(int sampleRate, int channels)
    {
        _channels = channels;

        Normalisation.Prepare(sampleRate, channels);
        Loudness.Prepare(sampleRate, channels);
        Equaliser.Prepare(sampleRate, channels);
        VirtualBass.Prepare(sampleRate, channels);
        StereoWidth.Prepare(sampleRate, channels);
        Crossfeed.Prepare(sampleRate, channels);
        DynamicRange.Prepare(sampleRate, channels);
        Limiter.Prepare(sampleRate, channels);
        Volume.Prepare(sampleRate, channels);
        Analyser.Prepare(sampleRate, channels);
    }

    /// <summary>Runs on the audio thread for every buffer.</summary>
    public void Process(Span<float> buffer, int frames)
    {
        Normalisation.Process(buffer, frames);

        // Kompensacja jako jedyna potrzebuje wiedzieć, jak głośno naprawdę gra muzyka, a widok
        // na obie składowe — suwak i wzmocnienie normalizacji — ma tylko ten obiekt.
        Loudness.SetListeningLevel(Volume.TargetDecibels, Normalisation.TargetDecibels);
        Loudness.Process(buffer, frames);

        Equaliser.Process(buffer, frames);
        VirtualBass.Process(buffer, frames);
        StereoWidth.Process(buffer, frames);
        Crossfeed.Process(buffer, frames);
        DynamicRange.Process(buffer, frames);

        Limiter.Process(buffer, frames);
        Analyser.Process(buffer, frames);
        Volume.Process(buffer, frames);
    }

    /// <summary>
    /// Called from the decode thread as a track is opened. Picks up the track's gain so the
    /// change is already smoothed in by the time its audio reaches the output.
    /// </summary>
    public void OnTrackChanged(QueueEntry entry)
    {
        if (!NormalisationEnabled)
        {
            Normalisation.TargetDecibels = 0;
            return;
        }

        var gain = GainSource?.GetGainDecibels(entry);
        Normalisation.TargetDecibels = gain ?? 0;
    }

    public void Reset()
    {
        Normalisation.Reset();
        Loudness.Reset();
        Equaliser.Reset();
        VirtualBass.Reset();
        StereoWidth.Reset();
        Crossfeed.Reset();
        DynamicRange.Reset();
        Limiter.Reset();
        Volume.Reset();
        Analyser.Reset();
    }
}
