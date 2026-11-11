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
    /// Zwraca wzmocnienie albo null, gdy jeszcze nieznane. Wołane z wątku dekodującego przy
    /// otwieraniu utworu, więc musi wrócić od razu; wszystko powolne idzie w tle.
    /// </summary>
    TrackGain? GetTrackGain(QueueEntry entry);
}

/// <summary>
/// Tor sygnału, w kolejności: wyrównanie głośności, kompensacja głośności, korektor ze
/// wzmocnieniem wstępnym, efekty, analiza, głośność, limiter.
///
/// <para>Pułapka: kolejność ma znaczenie. Wyrównanie idzie przed każdą korekcją, analiza przed
/// głośnością, żeby fala na ekranie nie zależała od suwaka, a limiter za głośnością, bo chroni
/// rzeczywiste wyjście.</para>
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

    /// <summary>Wyciszenie miękkiej pauzy; za głośnością, przed limiterem.</summary>
    public Fader PauseFade { get; } = new();

    public SpectrumAnalyser Analyser { get; } = new();

    /// <summary>Dane dźwięku dla wizualizacji sceny: jedyny punkt odczytu.</summary>
    public SceneAudio Scene { get; }

    public AudioGraph() => Scene = new SceneAudio(Analyser);

    /// <summary>Wyłączone sprowadza wzmocnienie wyrównania do jedności.</summary>
    public bool NormalisationEnabled { get; set; } = true;

    /// <summary>Poziom, do którego dociąga się stacje; plik wyrównuje <see cref="LoudnessService"/>.</summary>
    public double LiveTargetLufs { get; set; } = LoudnessService.ReferenceLufs;

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
        PauseFade.Prepare(sampleRate, channels);
        Analyser.Prepare(sampleRate, channels);
    }

    /// <summary>Runs on the audio thread for every buffer.</summary>
    public void Process(Span<float> buffer, int frames)
    {
        // Jedna próbka NaN albo nieskończona z uszkodzonego pliku zatrułaby stan filtrów na stałe.
        var samples = buffer[..(frames * _channels)];
        for (var i = 0; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) samples[i] = 0;

        Normalisation.Process(buffer, frames);

        // Kompensacja zależy od suwaka i poziomu programu, a oba zna tylko ten obiekt.
        Loudness.SetListeningLevel(Volume.TargetDecibels, Volatile.Read(ref _programmeLevelDb));
        Loudness.Process(buffer, frames);

        Equaliser.Process(buffer, frames);
        VirtualBass.Process(buffer, frames);
        StereoWidth.Process(buffer, frames);
        Crossfeed.Process(buffer, frames);
        DynamicRange.Process(buffer, frames);

        Analyser.Process(buffer, frames);
        Volume.Process(buffer, frames);
        PauseFade.Process(buffer, frames);
        Limiter.Process(buffer, frames);
    }

    /// <summary>
    /// Ustawia od razu wzmocnienie utworu, który już gra. Na zmianę ustawień wyrównania;
    /// przy zmianie utworu wzmocnienie zmienia wątek dźwiękowy przez <see cref="ApplyTrackGain"/>.
    /// </summary>
    public void OnTrackChanged(QueueEntry entry) => ApplyTrackGain(TrackGainFor(entry), snap: false);

    /// <summary>
    /// Wzmocnienie utworu ze źródła, niezależnie od tego, czy wyrównanie jest włączone.
    /// Wołane przez wątek dekodujący przy otwieraniu pliku, bo źródło może czytać plik.
    /// </summary>
    public TrackGain? TrackGainFor(QueueEntry entry) => GainSource?.GetTrackGain(entry);

    /// <summary>
    /// Ustawia wzmocnienie wyrównania i poziom programu, gdy utwór dociera do wyjścia; przy
    /// otwarciu pliku objęłyby końcówkę poprzedniego, która czeka jeszcze w buforze.
    /// Wątek dźwiękowy, bez alokacji.
    /// </summary>
    /// <param name="gain">Wartość z <see cref="TrackGainFor"/>; null, gdy nieznana.</param>
    /// <param name="snap">True po opróżnieniu bufora: nowy utwór zaczyna się od właściwego poziomu, bez dochodzenia do niego.</param>
    public void ApplyTrackGain(TrackGain? gain, bool snap)
    {
        var applied = NormalisationEnabled ? gain : null;

        // Utwór bez znanego wzmocnienia gra tak, jak go nagrano; przyjmuje się poziom odniesienia.
        Normalisation.TargetDecibels = applied?.GainDecibels ?? 0;
        Volatile.Write(ref _programmeLevelDb, applied?.ProgrammeLevelDecibels ?? 0);
        if (snap) Normalisation.SnapToTarget();
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
        PauseFade.Reset();
        Analyser.Reset();
    }
}
