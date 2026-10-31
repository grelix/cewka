using System.Collections.Concurrent;
using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Silnik odtwarzania na atrapie wyjścia i dekoderach syntetycznych. Pliki w katalogu
/// tymczasowym istnieją tylko po to, żeby wykrywanie formatu i odczyt znaczników miały
/// co otworzyć; dźwięk pochodzi z dekodera syntetycznego.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-silnik-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Func<IAudioDecoder>> _decoders = new(StringComparer.Ordinal);

    public EngineHarness(int periodFrames = 1024, int seed = 1234)
    {
        Directory.CreateDirectory(_directory);

        Engine = new PlaybackEngine((_, _) =>
        {
            var output = new TestOutput(periodFrames, Rate, Channels);
            Outputs.Add(output);
            return output;
        }, new Random(seed));

        Engine.OpenDecoder = (path, _, _) =>
        {
            Opened.Enqueue(Path.GetFileName(path));
            return _decoders.TryGetValue(path, out var factory)
                ? factory()
                : throw new FileNotFoundException("Brak pliku.", path);
        };

        // Tor przezroczysty: wartości próbek w wyjściu to wartości z dekodera razy wzmocnienia.
        Engine.Graph.Limiter.Enabled = false;
        Engine.Graph.Equaliser.Enabled = false;

        Engine.TrackChanged += index => TrackChanges.Enqueue(index);
        Engine.TrackFailed += (index, failure) => Failures.Enqueue((index, failure));
        Engine.QueueFinished += () => Interlocked.Increment(ref _finished);
        Engine.DeviceLost += () => Interlocked.Increment(ref _lost);
    }

    public PlaybackEngine Engine { get; }

    public List<TestOutput> Outputs { get; } = [];

    public TestOutput Output => Outputs[^1];

    public ConcurrentQueue<string> Opened { get; } = new();

    public ConcurrentQueue<int> TrackChanges { get; } = new();

    public ConcurrentQueue<(int Index, TrackFailure Failure)> Failures { get; } = new();

    private int _finished;
    public int Finished => Volatile.Read(ref _finished);

    private int _lost;
    public int Lost => Volatile.Read(ref _lost);

    public static long Frames(double seconds) => (long)(seconds * Rate);

    /// <summary>Utwór o stałej wartości próbki; zwraca ścieżkę do kolejki.</summary>
    public string Track(string name, double seconds, float value, Func<SyntheticDecoder, SyntheticDecoder>? configure = null)
    {
        var path = WriteWav(name + ".wav");
        _decoders[path] = () =>
        {
            var decoder = new SyntheticDecoder(Frames(seconds), value, Channels, Rate);
            return configure is null ? decoder : configure(decoder);
        };
        return path;
    }

    /// <summary>Plik w formacie, którego odtwarzacz nie obsługuje.</summary>
    public string Unsupported(string name)
    {
        var path = Path.Combine(_directory, name + ".wma");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        return path;
    }

    private string WriteWav(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        using var writer = new BinaryWriter(File.Create(path));

        const int dataBytes = 400;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)Channels);
        writer.Write(Rate);
        writer.Write(Rate * Channels * 2);
        writer.Write((short)(Channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }

    /// <summary>
    /// Pompuje wątek dźwiękowy, aż warunek będzie spełniony. Okres idzie dopiero wtedy, gdy
    /// wątek dekodujący zdążył go przygotować, albo po krótkim czekaniu, gdy danych nie
    /// będzie (koniec kolejki, opróżnianie bufora). Bez tego pompowanie szybsze od dekodera
    /// wstawiałoby ciszę niedoboru w środek dźwięku.
    /// </summary>
    public void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMilliseconds)
                throw new TimeoutException("Warunek nie został spełniony w wyznaczonym czasie.");

            PumpPaced();
        }
    }

    /// <summary>
    /// Pompuje, aż przewinięcie się wykona, a potem jeszcze przez <paramref name="periods"/>
    /// okresów, zapisując pozycję po każdym. Pozycja odczytana tylko raz na końcu nie odróżnia
    /// przewinięcia od odtwarzania od zera, bo pompowanie jest szybsze od czasu rzeczywistego.
    /// </summary>
    public double[] PositionsAfterSeek(int periods = 10)
    {
        PumpUntil(() => !Engine.IsSeeking);

        var positions = new double[periods];
        for (var i = 0; i < periods; i++)
        {
            PumpPaced();
            positions[i] = Engine.Position.TotalSeconds;
        }

        return positions;
    }

    /// <summary>Pompuje przez zadaną liczbę okresów.</summary>
    public void PumpPeriods(int periods)
    {
        for (var i = 0; i < periods; i++) PumpPaced();
    }

    /// <summary>
    /// Zapas, poniżej którego pompowanie czeka na dekoder. Wątek dekodujący dopełnia bufor
    /// porcjami po przerwie, a pompowanie bez zegara opróżniłoby go w tej przerwie.
    /// </summary>
    private const int ReserveFrames = 4096;

    private void PumpPaced()
    {
        var waited = Stopwatch.StartNew();
        while (Engine.BufferedFrames < ReserveFrames && waited.ElapsedMilliseconds < 20)
            Thread.Sleep(1);

        Output.Pump();
    }

    /// <summary>Czeka bez pompowania, aż warunek będzie spełniony.</summary>
    public static void WaitUntil(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMilliseconds)
                throw new TimeoutException("Warunek nie został spełniony w wyznaczonym czasie.");
            Thread.Sleep(1);
        }
    }

    /// <summary>Liczba ramek wyjścia, których pierwsza próbka jest bliska wartości.</summary>
    public static int CountFrames(float[] samples, float value, float tolerance = 1e-5f)
    {
        var count = 0;
        for (var i = 0; i < samples.Length; i += Channels)
            if (Math.Abs(samples[i] - value) <= tolerance) count++;
        return count;
    }

    public void Dispose()
    {
        Engine.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Źródło wzmocnienia wyrównania z tablicy, po nazwie pliku.</summary>
internal sealed class FixedGains(Dictionary<string, double> gains) : ITrackGainSource
{
    public TrackGain? GetTrackGain(QueueEntry entry) =>
        gains.TryGetValue(Path.GetFileNameWithoutExtension(entry.Path), out var gain) ? new TrackGain(gain, 0) : null;
}
