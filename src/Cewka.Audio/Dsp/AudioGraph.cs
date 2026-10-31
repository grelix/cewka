using Cewka.Audio.Playback;

namespace Cewka.Audio.Dsp;

/// <summary>Wzmocnienie wyrównania dla utworu i poziom, na jakim utwór po nim zagra.</summary>
/// <param name="GainDecibels">Wzmocnienie do nałożenia, w decybelach.</param>
/// <param name="ProgrammeLevelDecibels">
/// O ile utwór po wyrównaniu gra głośniej od poziomu odniesienia ReplayGain. Zwykle różnica
/// między poziomem docelowym a odniesieniem, mniejsza, gdy wzmocnienie ograniczył szczyt.
/// </param>
public readonly record struct TrackGain(double GainDecibels, double ProgrammeLevelDecibels);

/// <summary>Supplies a normalisation gain for a queue entry.</summary>
public interface ITrackGainSource
{
    /// <summary>
    /// Returns the gain to apply, or null when it is not known yet. Called from the decode
    /// thread as a track is opened, so it must return promptly — anything slow belongs in
    /// the background.
    /// </summary>
    TrackGain? GetTrackGain(QueueEntry entry);
}

/// <summary>
/// The signal chain, in order: loudness normalisation, loudness compensation, equaliser with
/// its preamp, effects, analysis tap, master volume, limiter.
///
/// <para>Normalizacja idzie pierwsza, bo wyrównuje materiał przed jakąkolwiek korekcją. Analiza
/// sygnału jest przed głośnością, żeby fala na ekranie odpowiadała muzyce, a nie pokrętłu.
/// Limiter stoi na samym końcu, za głośnością: chroni przed przesterowaniem rzeczywistego
/// wyjścia, a po ściszeniu podbicia korektora i kompensacji zwykle mieszczą się w skali.</para>
/// </summary>
public sealed class AudioGraph
{
    private int _channels = 2;
    private double _programmeLevelDb;

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

        // Kompensacja potrzebuje wiedzieć, jak głośno naprawdę gra muzyka, a widok na obie
        // składowe, suwak i poziom programu, ma tylko ten obiekt.
        Loudness.SetListeningLevel(Volume.TargetDecibels, Volatile.Read(ref _programmeLevelDb));
        Loudness.Process(buffer, frames);

        Equaliser.Process(buffer, frames);
        VirtualBass.Process(buffer, frames);
        StereoWidth.Process(buffer, frames);
        Crossfeed.Process(buffer, frames);
        DynamicRange.Process(buffer, frames);

        Analyser.Process(buffer, frames);
        Volume.Process(buffer, frames);
        Limiter.Process(buffer, frames);
    }

    /// <summary>
    /// Called from the decode thread as a track is opened. Picks up the track's gain so the
    /// change is already smoothed in by the time its audio reaches the output.
    /// </summary>
    public void OnTrackChanged(QueueEntry entry)
    {
        var gain = NormalisationEnabled ? GainSource?.GetTrackGain(entry) : null;

        // Utwór bez znanego wzmocnienia gra tak, jak go nagrano; przyjmuje się poziom odniesienia.
        Normalisation.TargetDecibels = gain?.GainDecibels ?? 0;
        Volatile.Write(ref _programmeLevelDb, gain?.ProgrammeLevelDecibels ?? 0);
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
