using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.App.Services;
using Cewka.App.ViewModels;
using Cewka.Audio;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Cewka.Audio.Tests.Engine;

namespace Cewka.App.Tests;

/// <summary>
/// Model widoku okna głównego na prawdziwym silniku, ale z atrapą wyjścia dźwięku
/// i dekoderami syntetycznymi. Ustawienia, kolejka i pamięć głośności leżą w katalogu
/// tymczasowym testu. Tworzony i używany wyłącznie wewnątrz <see cref="Ui.Run(System.Action)"/>.
/// </summary>
internal sealed class PlayerHarness : IDisposable
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private readonly Dictionary<string, double> _lengths = new(StringComparer.Ordinal);

    /// <param name="configure">Ustawienia przed utworzeniem modelu widoku.</param>
    /// <param name="directory">Katalog poprzedniego uruchomienia; bez niego nowy.</param>
    /// <param name="random">Źródło losowości silnika; z ziarnem porażkę losowej kolejności da się powtórzyć.</param>
    /// <param name="realDisk">Model widoku sprawdza ścieżki sondą produkcyjną, bez <see cref="PathChecking"/>.</param>
    public PlayerHarness(Action<AppSettings>? configure = null, string? directory = null, Random? random = null, bool realDisk = false, Func<RadioCatalog>? radioCatalog = null, RadioLogoService? radioLogos = null)
    {
        Directory = directory ?? TestEnvironment.NewDirectory();

        Settings = new SettingsStore(System.IO.Path.Combine(Directory, "settings.json"));
        configure?.Invoke(Settings.Current);

        Engine = new PlaybackEngine((name, _) =>
        {
            if (FailAll || (name is not null && name == FailingDevice))
                throw new AudioException("Urządzenie nie przyjęło formatu.");

            var output = new TestOutput(1024, Rate, Channels);
            Outputs.Add(output);
            return output;
        }, random);

        Engine.OpenDecoder = (path, _, _) => _lengths.TryGetValue(path, out var seconds)
            ? new SyntheticDecoder((long)(seconds * Rate), 0.1f, Channels, Rate)
            : throw new FileNotFoundException("Brak pliku.", path);

        // Stacje bez sieci: sesja i dekoder z atrap, zdarzenia podaje test.
        Engine.OpenLiveSession = source =>
        {
            var session = new FakeLiveSession(source);
            Sessions.Add(session);
            return session;
        };
        Engine.OpenLiveDecoder = (_, _, _) => new ScriptedLiveDecoder().Feed(long.MaxValue / 2);

        Loudness = new LoudnessService(System.IO.Path.Combine(Directory, "loudness.json"), (path, token) => Measurement?.Invoke(path, token));
        Player = new MainViewModel(Settings, Engine, Loudness, QueueFile, realDisk ? null : new PathProbe(
            path =>
            {
                PathChecking?.Invoke(path);
                return File.Exists(path);
            },
            path =>
            {
                PathChecking?.Invoke(path);
                return System.IO.Directory.Exists(path);
            }),
            RadioFile, radioCatalog ?? DefaultRadioCatalog, radioLogos);
        Player.Radio.StepDelay = TimeSpan.Zero;
    }

    public string RadioFile => System.IO.Path.Combine(Directory, "radio.json");

    /// <summary>Sesje stacji otwarte przez silnik, w kolejności otwarcia.</summary>
    public List<FakeLiveSession> Sessions { get; } = [];

    /// <summary>Mała lista: dwie stacje w Polsce, jedna o tej samej nazwie w USA i jedna w Wielkiej Brytanii.</summary>
    public static RadioCatalog DefaultRadioCatalog() => new(
    [
        Tests.Radio.RadioTestData.Station("pl-a", "Alfa FM", "PL", categories: [RadioCategories.Hits]),
        Tests.Radio.RadioTestData.Station("pl-b", "Beta FM", "PL", categories: [RadioCategories.NewsTalk]),
        Tests.Radio.RadioTestData.Station("us-a", "Alfa FM", "US", categories: [RadioCategories.Rock]),
        Tests.Radio.RadioTestData.Station("gb-a", "Gamma FM", "GB", categories: [RadioCategories.Hits]),
    ]);

    public string Directory { get; }

    public string QueueFile => System.IO.Path.Combine(Directory, "queue.json");

    public SettingsStore Settings { get; }

    public PlaybackEngine Engine { get; }

    public LoudnessService Loudness { get; }

    /// <summary>Pomiar głośności i obwiedni dla plików testu; domyślnie brak, więc analiza niczego nie zapisuje.</summary>
    public Func<string, CancellationToken, LoudnessMeasurement?>? Measurement { get; set; }

    public MainViewModel Player { get; }

    /// <summary>Wołane przed każdym sprawdzeniem ścieżki, np. żeby udać wolny dysk sieciowy.</summary>
    public Action<string>? PathChecking { get; set; }

    public List<TestOutput> Outputs { get; } = [];

    /// <summary>Nazwa urządzenia, którego otwarcie się nie udaje.</summary>
    public string? FailingDevice { get; set; }

    /// <summary>Żadne urządzenie się nie otwiera.</summary>
    public bool FailAll { get; set; }

    /// <summary>Plik WAV na dysku; dźwięk o zadanej długości daje dekoder syntetyczny.</summary>
    public string Track(string name, double seconds = 60)
    {
        var path = System.IO.Path.Combine(Directory, name + ".wav");
        WriteWav(path);
        _lengths[path] = seconds;
        return path;
    }

    /// <summary>
    /// Jak <see cref="Track"/>, ale długość z tagów zgadza się z dźwiękiem. Plik ma tę długość
    /// naprawdę, więc zapisany jest jako 8 kHz, mono, 8 bitów, żeby był mały.
    /// </summary>
    public string TrackWithTaggedLength(string name, double seconds = 60)
    {
        var path = System.IO.Path.Combine(Directory, name + ".wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int rate = 8000;
            var dataBytes = (int)(seconds * rate);
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            writer.Write(Enumerable.Repeat((byte)0x80, dataBytes).ToArray());
        }

        _lengths[path] = seconds;
        return path;
    }

    /// <summary>Jak <see cref="Track"/>, z tytułem, wykonawcą i albumem w tagach.</summary>
    public string TaggedTrack(string name, string title, string artist, string album, double seconds = 60)
    {
        var path = Track(name, seconds);
        var track = new ATL.Track(path) { Title = title, Artist = artist, Album = album };
        if (!track.Save()) throw new IOException($"Nie zapisano tagów: {path}");
        return path;
    }

    private static void WriteWav(string path)
    {
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
    }

    /// <summary>Otwiera i czeka na dołożenie, bo ścieżki sprawdzają się poza wątkiem interfejsu.</summary>
    public void OpenPaths(IEnumerable<string> paths, bool replace = true) => Settle(Player.OpenPaths(paths, replace));

    /// <inheritdoc cref="OpenPaths"/>
    public void AddPaths(IEnumerable<string> paths) => Settle(Player.AddPaths(paths));

    /// <inheritdoc cref="OpenPaths"/>
    public void OpenFromOutside(IEnumerable<string> paths) => Settle(Player.OpenFromOutside(paths));

    /// <summary>Przywraca kolejkę i czeka, aż wróci.</summary>
    public void RestoreQueueState() => Settle(Player.RestoreQueueState());

    /// <summary>Wczytuje listę i czeka, aż zastąpi kolejkę.</summary>
    public void LoadPlaylist(string path) => Settle(Player.LoadPlaylist(path));

    private static void Settle(Task task)
    {
        if (!Ui.WaitUntil(() => task.IsCompleted, 20000)) throw new TimeoutException("Otwieranie się nie skończyło.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Pompuje wyjście i dyspozytora, aż warunek będzie spełniony.</summary>
    public bool PumpUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (true)
        {
            if (Outputs.Count > 0) Outputs[^1].Pump();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// Pompuje wyjście i dyspozytora przez stałą liczbę okresów, bez zegara: do kontroli, że coś się
    /// nie stało, niezależnie od szybkości komputera.
    /// </summary>
    public void PumpPeriods(int periods)
    {
        for (var i = 0; i < periods; i++)
        {
            if (Outputs.Count > 0) Outputs[^1].Pump();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    public void Dispose() => Player.Dispose();
}
