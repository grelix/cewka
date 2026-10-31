using Cewka.App.Models;
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

    public PlayerHarness(Action<AppSettings>? configure = null, string? directory = null)
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
        });

        Engine.OpenDecoder = (path, _, _) => _lengths.TryGetValue(path, out var seconds)
            ? new SyntheticDecoder((long)(seconds * Rate), 0.1f, Channels, Rate)
            : throw new FileNotFoundException("Brak pliku.", path);

        Loudness = new LoudnessService(System.IO.Path.Combine(Directory, "loudness.json"), (_, _) => null);
        Player = new MainViewModel(Settings, Engine, Loudness, QueueFile);
    }

    public string Directory { get; }

    public string QueueFile => System.IO.Path.Combine(Directory, "queue.json");

    public SettingsStore Settings { get; }

    public PlaybackEngine Engine { get; }

    public LoudnessService Loudness { get; }

    public MainViewModel Player { get; }

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

    public void Dispose() => Player.Dispose();
}
