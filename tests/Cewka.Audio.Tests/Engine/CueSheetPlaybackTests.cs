using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Utwory arkusza CUE w silniku: płyta w jednym pliku gra przez granice ścieżek tak jak cały
/// plik, a pozycja, długość, przewijanie, „Poprzedni”, powtarzanie i losowanie dotyczą wycinka.
/// </summary>
public sealed class CueSheetPlaybackTests
{
    private const int Period = 1024;

    /// <summary>Klatek arkusza na sekundę.</summary>
    private const int Cd = 75;

    /// <summary>Suma tonów w całym paśmie: ciągła, więc każdy szew na granicy jest widoczny.</summary>
    private static float[] Tones(int frames, int rate)
    {
        var data = new float[frames * EngineHarness.Channels];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / rate;
            var value = 0.3 * Math.Sin(2 * Math.PI * 997 * t) + 0.2 * Math.Sin(2 * Math.PI * 7321 * t)
                + 0.1 * Math.Sin(2 * Math.PI * 15013 * t + 1);
            data[2 * i] = (float)value;
            data[2 * i + 1] = (float)(-0.8 * value);
        }

        return data;
    }

    private static Func<IAudioDecoder> WavDecoder(float[] data, int rate)
    {
        var bytes = TestAudioFiles.Wav(data, EngineHarness.Channels, rate);
        return () => new MiniaudioDecoder(new MemoryStream(bytes), true, EngineHarness.Rate, EngineHarness.Channels);
    }

    private static SheetTrack Track(int number, long start, long? end) =>
        new() { SheetPath = "koncert.cue", Number = number, Start = start, End = end, Album = "Koncert" };

    /// <summary>Gra kolejkę do końca, okres po okresie, gdy dekoder go przygotował.</summary>
    private static float[] PlayToEnd(EngineHarness h)
    {
        var total = Stopwatch.StartNew();
        while (h.Finished == 0)
        {
            if (total.ElapsedMilliseconds > 30000) throw new TimeoutException("Kolejka nie doszła do końca.");

            var waited = Stopwatch.StartNew();
            while (h.Engine.BufferedFrames < Period && waited.ElapsedMilliseconds < 1000) Thread.Sleep(1);
            h.Output.Pump();
        }

        return h.Output.Samples;
    }

    private static (float[] Whole, float[] Sheet, int Opened) Concert(int fileRate)
    {
        var signal = Tones(fileRate * 2, fileRate);
        var factory = WavDecoder(signal, fileRate);

        float[] whole;
        using (var h = new EngineHarness(Period))
        {
            h.Engine.SetQueue([h.Track("calosc", factory)]);
            whole = PlayToEnd(h);
        }

        using (var h = new EngineHarness(Period))
        {
            var path = h.Track("koncert", factory);

            // Granice w środku dźwięku, nie na wielokrotności okresu ani porcji dekodera.
            h.Engine.SetQueue(new[]
            {
                new QueueItem(path, Track(1, 0, 45)),
                new QueueItem(path, Track(2, 45, 103)),
                new QueueItem(path, Track(3, 103, null)),
            });

            var sheet = PlayToEnd(h);
            EngineHarness.WaitUntil(() => h.TrackChanges.Distinct().Count() == 3);
            Assert.Equal(new[] { 0, 1, 2 }, h.TrackChanges.Distinct().Order());

            return (whole, sheet, h.Opened.Count);
        }
    }

    [Fact]
    public void PlytaWJednymPlikuGraCoDoProbkiJakCalyPlik()
    {
        var (whole, sheet, opened) = Concert(EngineHarness.Rate);

        // Jeden dekoder przez wszystkie ścieżki: bez zamykania, przewijania i ponownego otwierania.
        Assert.Equal(1, opened);
        Assert.Equal(whole.Length, sheet.Length);
        Assert.Equal(whole, sheet);
    }

    [Fact]
    public void PlytaZKonwersjaGraJakCalyPlik()
    {
        var (whole, sheet, opened) = Concert(44100);

        Assert.Equal(1, opened);
        Assert.InRange(sheet.Length, whole.Length - 2 * EngineHarness.Channels, whole.Length + 2 * EngineHarness.Channels);

        var largest = 0.0;
        for (var i = 0; i < Math.Min(whole.Length, sheet.Length); i++) largest = Math.Max(largest, Math.Abs(whole[i] - sheet[i]));

        Assert.True(Signals.Peak(sheet) > 0.2);
        Assert.True(Signals.Db(largest) <= -90, $"różnica {Signals.Db(largest):F1} dB");
    }

    /// <summary>Wartość próbki to numer ramki pliku, więc wyjście mówi, które miejsce pliku gra.</summary>
    private const float Step = 1f / (1 << 20);

    private sealed class Ramp(long totalFrames) : IAudioDecoder
    {
        private long _position;

        public int SampleRate => EngineHarness.Rate;
        public int Channels => EngineHarness.Channels;
        public long TotalFrames => totalFrames;
        public long Position => _position;
        public bool CanSeek => true;
        public int SourceSampleRate => SampleRate;
        public int SourceChannels => Channels;
        public int InstantaneousBitrate => 0;

        public int Read(Span<float> destination)
        {
            var frames = (int)Math.Clamp(totalFrames - _position, 0, destination.Length / Channels);
            for (var i = 0; i < frames; i++)
                destination.Slice(i * Channels, Channels).Fill((_position + i) * Step);
            _position += frames;
            return frames;
        }

        public void Seek(long frame) => _position = Math.Clamp(frame, 0, totalFrames);

        public void Dispose() { }
    }

    /// <summary>
    /// Ramki pliku w kolejności, w jakiej zagrały, bez ciszy i bez ramek z wyciszeń. Próbka
    /// przyciszona bywa przypadkiem całkowitym numerem ramki, ale nie ciągiem kolejnych numerów,
    /// więc liczą się tylko próbki, których sąsiad jest ramką obok.
    /// </summary>
    private static List<long> PlayedFrames(float[] samples)
    {
        var count = samples.Length / EngineHarness.Channels;
        var candidates = new long[count];

        for (var i = 0; i < count; i++)
        {
            var sample = samples[i * EngineHarness.Channels];
            var frame = sample / Step;
            candidates[i] = sample > 0 && Math.Abs(frame - Math.Round(frame)) < 1e-3 ? (long)Math.Round(frame) : -1;
        }

        var frames = new List<long>();
        for (var i = 0; i < count; i++)
        {
            if (candidates[i] < 0) continue;

            var joined = (i > 0 && candidates[i - 1] == candidates[i] - 1) || (i + 1 < count && candidates[i + 1] == candidates[i] + 1);
            if (joined) frames.Add(candidates[i]);
        }

        return frames;
    }

    private static long Seconds(double seconds) => (long)(seconds * EngineHarness.Rate);

    [Fact]
    public void DlugoscPozycjaIPrzewijanieWzgledemWycinka()
    {
        using var h = new EngineHarness(Period);
        var path = h.Track("plyta", () => new Ramp(Seconds(10)));

        h.Engine.SetQueue(new[] { new QueueItem(path, Track(2, 3 * Cd, 7 * Cd)) });
        h.PumpUntil(() => h.Engine.Duration > TimeSpan.Zero && !h.Engine.IsSeeking);

        Assert.Equal(4.0, h.Engine.Duration.TotalSeconds, 6);

        h.PumpPeriods(20);
        var played = PlayedFrames(h.Output.Samples);
        Assert.Equal(Seconds(3) + 400, played.First(frame => frame >= Seconds(3) + 400));
        Assert.True(played.Min() >= Seconds(3), "zagrał dźwięk sprzed wycinka");

        var position = h.Engine.Position.TotalSeconds;
        Assert.InRange(position, 0.1, 1.0);

        h.Engine.SeekTo(TimeSpan.FromSeconds(2));
        var after = h.PositionsAfterSeek();
        Assert.InRange(after[0], 2.0, 2.2);

        var heard = h.Output.Samples.Length;
        PlayToEnd(h);

        var rest = PlayedFrames(h.Output.Samples[heard..]);
        Assert.True(rest.Min() >= Seconds(5) - 1, $"po przewinięciu zagrało {rest.Min()}");
        Assert.Equal(Seconds(7) - 1, rest.Max());
    }

    [Fact]
    public void PoprzedniPoTrzechSekundachWracaNaPoczatekWycinka()
    {
        using var h = new EngineHarness(Period);
        var path = h.Track("plyta", () => new Ramp(Seconds(20)));

        h.Engine.SetQueue(new[]
        {
            new QueueItem(path, Track(1, 0, 4 * Cd)),
            new QueueItem(path, Track(2, 4 * Cd, 15 * Cd)),
        }, startIndex: 1);

        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position > TimeSpan.FromSeconds(3.5));

        var heard = h.Output.Samples.Length;
        h.Engine.Previous();
        var positions = h.PositionsAfterSeek();

        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.True(positions[0] < 0.2, $"pozycja {positions[0]}");
        Assert.Contains(Seconds(4) + 500, PlayedFrames(h.Output.Samples[heard..]));

        // W pierwszych trzech sekundach „Poprzedni” przechodzi do poprzedniej ścieżki.
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.5));
        heard = h.Output.Samples.Length;
        h.Engine.Previous();
        h.PumpUntil(() => h.Engine.CurrentIndex == 0 && !h.Engine.IsSeeking);
        h.PumpPeriods(10);

        Assert.InRange(PlayedFrames(h.Output.Samples[heard..]).Where(f => f < Seconds(4)).Min(), 0, Seconds(0.3));
    }

    [Fact]
    public void PowtarzanieUtworuGraTenSamWycinekOdNowa()
    {
        using var h = new EngineHarness(Period);
        var path = h.Track("plyta", () => new Ramp(Seconds(5)));

        h.Engine.Repeat = RepeatMode.Track;
        h.Engine.SetQueue(new[]
        {
            new QueueItem(path, Track(1, 0, Cd)),
            new QueueItem(path, Track(2, Cd, 2 * Cd)),
            new QueueItem(path, Track(3, 2 * Cd, null)),
        }, startIndex: 1);

        h.PumpUntil(() => PlayedFrames(h.Output.Samples).Count(frame => frame == Seconds(1) + 300) >= 3, 20000);

        var played = PlayedFrames(h.Output.Samples);
        Assert.All(played, frame => Assert.InRange(frame, Seconds(1), Seconds(2) - 1));
        Assert.Equal(1, h.Engine.CurrentIndex);
    }

    [Fact]
    public void WycinekNiestykajacySieZPoprzednimZaczynaWeWlasnymMiejscu()
    {
        using var h = new EngineHarness(Period);
        var path = h.Track("plyta", () => new Ramp(Seconds(5)));

        // Kolejka ułożona inaczej niż arkusz: po pierwszej ścieżce trzecia.
        h.Engine.SetQueue(new[]
        {
            new QueueItem(path, Track(1, 0, Cd)),
            new QueueItem(path, Track(3, 2 * Cd, 3 * Cd)),
        });

        var played = PlayedFrames(PlayToEnd(h));

        Assert.DoesNotContain(played, frame => frame >= Seconds(1) && frame < Seconds(2));
        Assert.Contains(Seconds(1) - 1, played);
        Assert.Contains(Seconds(2) + 1, played);
        Assert.Equal(Seconds(3) - 1, played.Max());
        Assert.Equal(2, h.Opened.Count);
    }

    [Fact]
    public void LosowanieGraKazdyWycinekRazIWCalosci()
    {
        using var h = new EngineHarness(Period, seed: 77);
        var path = h.Track("plyta", () => new Ramp(Seconds(3)));

        h.Engine.Shuffle = true;
        h.Engine.SetQueue(new[]
        {
            new QueueItem(path, Track(1, 0, 40)),
            new QueueItem(path, Track(2, 40, 95)),
            new QueueItem(path, Track(3, 95, 160)),
            new QueueItem(path, Track(4, 160, null)),
        });

        var played = PlayedFrames(PlayToEnd(h));

        // Każda ramka pliku najwyżej raz; poza wyciszeniem na starcie zagrała cała płyta.
        Assert.Equal(played.Count, played.Distinct().Count());
        Assert.InRange(played.Count, Seconds(3) - 400, Seconds(3));
        Assert.Equal(Seconds(3) - 1, played.Max());
    }
}
