using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Konwersja częstotliwości na granicy utworów. Filtr patrzy w przód o kilkadziesiąt ramek:
/// bez przejęcia stanu przez następny utwór końcówka poprzedniego i początek następnego
/// liczone byłyby osobno, z ciszą w miejscu sąsiada, a przy przejściu bez przerwy słychać
/// by było szew.
/// </summary>
public sealed class ResamplingContinuityTests
{
    private const int FileRate = 44100;

    /// <summary>Suma tonów w całym paśmie, ciągła przez granicę.</summary>
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

    private static float[] Constant(int frames, float value)
    {
        var data = new float[frames * EngineHarness.Channels];
        Array.Fill(data, value);
        return data;
    }

    private static Func<IAudioDecoder> WavDecoder(float[] data, int rate)
    {
        var bytes = TestAudioFiles.Wav(data, EngineHarness.Channels, rate);
        return () => new MiniaudioDecoder(new MemoryStream(bytes), true, EngineHarness.Rate, EngineHarness.Channels);
    }

    private static float[] Play(params (string Name, float[] Data, int Rate)[] tracks)
    {
        using var h = new EngineHarness(Period);
        var paths = tracks.Select(t => h.Track(t.Name, WavDecoder(t.Data, t.Rate))).ToArray();

        h.Engine.SetQueue(paths);

        // Okres idzie dopiero, gdy dekoder go przygotował. Cisza niedoboru w środku dźwięku,
        // możliwa na zajętej maszynie, przesunęłaby jedno nagranie względem drugiego.
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

    private const int Period = 1024;

    /// <summary>
    /// Sygnał rozcięty na dwa pliki 44,1 kHz gra przy 48 kHz tak samo jak jeden plik: różnica
    /// wokół granicy nie przekracza −90 dB względem pełnej skali.
    /// </summary>
    [Fact]
    public void SplitSignalSoundsLikeOneFile()
    {
        const int first = FileRate * 7 / 10;
        const int total = FileRate * 3 / 2;

        var signal = Tones(total, FileRate);
        var whole = Play(("calosc", signal, FileRate));
        var split = Play(
            ("poczatek", signal[..(first * 2)], FileRate),
            ("koniec", signal[(first * 2)..], FileRate));

        var boundary = (int)((long)first * EngineHarness.Rate / FileRate);
        var largest = 0.0;
        for (var frame = boundary - 4000; frame < boundary + 4000; frame++)
            for (var c = 0; c < EngineHarness.Channels; c++)
            {
                var i = frame * EngineHarness.Channels + c;
                largest = Math.Max(largest, Math.Abs(whole[i] - split[i]));
            }

        // Granica musi leżeć w porównywanym fragmencie: obie wersje grają tam ton.
        Assert.True(Signals.Peak(split[((boundary - 100) * 2)..((boundary + 100) * 2)]) > 0.2);
        Assert.True(Signals.Db(largest) <= -90, $"różnica na granicy {Signals.Db(largest):F1} dB");
    }

    /// <summary>
    /// Gdy przejęcie jest niemożliwe (inna częstotliwość), końcówka filtru trafia do bufora: nie
    /// ginie ani jedna ramka. Suma próbek składowej stałej jest miarą długości niezależną od
    /// wyciszenia przy starcie, bo to samo wyciszenie ma nagranie wzorcowe.
    /// <para>
    /// Na końcu kolejki gra cisza w częstotliwości wyjścia: tor ma stałą linię opóźnienia, a jej
    /// ostatnie ramki uprząż oddaje albo nie, zależnie od tempa maszyny. Przepada wtedy tylko cisza.
    /// Wypłukanie na samym końcu kolejki sprawdza osobny test.
    /// </para>
    /// </summary>
    [Fact]
    public void TailIsDrainedWhenTheNextTrackCannotTakeItOver()
    {
        const int firstFrames = FileRate / 2;
        const int secondFrames = 32000 / 2;
        var silence = ("cisza", Constant(EngineHarness.Rate / 5, 0f), EngineHarness.Rate);

        var output = Play(
            ("pierwszy", Constant(firstFrames, 0.5f), FileRate),
            ("drugi", Constant(secondFrames, 0.25f), 32000),
            silence);

        // Ten sam czas trwania w częstotliwości wyjścia, bez konwersji.
        var reference = Play(
            ("pierwszy", Constant((int)SincResampler.OutputFrames(firstFrames, FileRate, EngineHarness.Rate), 0.5f), EngineHarness.Rate),
            ("drugi", Constant((int)SincResampler.OutputFrames(secondFrames, 32000, EngineHarness.Rate), 0.25f), EngineHarness.Rate),
            silence);

        var difference = Sum(output) - Sum(reference);

        // Brak jednej końcówki to około 40 ramek po 0,5 albo 0,25.
        Assert.InRange(difference, -2.0, 2.0);
    }

    /// <summary>
    /// Ostatni wpis kolejki oddaje wstrzymaną końcówkę, zanim kolejka się skończy. Końcówka jest
    /// dużo dłuższa od linii opóźnienia toru, więc jej los nie zależy od ostatnich okresów.
    /// </summary>
    [Fact]
    public void LastTrackOfTheQueueDrainsItsTail()
    {
        const float body = 0.125f;
        const float tail = 0.375f;

        using var h = new EngineHarness();
        TailDecoder? decoder = null;
        var path = h.Track("ostatni", () => decoder = new TailDecoder(EngineHarness.Frames(0.2), body, 2000, tail));

        h.Engine.SetQueue([path]);
        h.PumpUntil(() => h.Finished > 0);

        var heard = EngineHarness.CountFrames(h.Output.Samples, tail);

        Assert.NotNull(decoder);
        Assert.True(decoder.HoldTail);
        Assert.True(decoder.DrainTailCalls > 0);
        Assert.True(heard >= 1500, $"ramek końcówki: {heard}");
    }

    /// <summary>
    /// Dekoder ze stałą wartością i końcówką innej wartości. Przy wstrzymanej końcówce oddaje ją
    /// wyłącznie <see cref="DrainTail"/>, jak dekoder z konwersją częstotliwości.
    /// </summary>
    private sealed class TailDecoder(long frames, float value, int tailFrames, float tailValue)
        : IAudioDecoder, IContinuousDecoder
    {
        private readonly int _tailFrames = tailFrames;
        private long _position;
        private int _tailLeft = tailFrames;

        public int DrainTailCalls { get; private set; }
        public bool HoldTail { get; set; }

        public int SampleRate => EngineHarness.Rate;
        public int Channels => EngineHarness.Channels;
        public long TotalFrames => frames;
        public long Position => _position;
        public bool CanSeek => true;
        public int SourceSampleRate => EngineHarness.Rate;
        public int SourceChannels => EngineHarness.Channels;
        public int InstantaneousBitrate => 0;

        public int Read(Span<float> destination)
        {
            var wanted = destination.Length / Channels;
            var count = (int)Math.Clamp(frames - _position, 0, wanted);

            if (count > 0)
            {
                destination[..(count * Channels)].Fill(value);
                _position += count;
                return count;
            }

            return HoldTail ? 0 : Tail(destination);
        }

        public int DrainTail(Span<float> destination)
        {
            DrainTailCalls++;
            return Tail(destination);
        }

        private int Tail(Span<float> destination)
        {
            var count = Math.Min(_tailLeft, destination.Length / Channels);
            destination[..(count * Channels)].Fill(tailValue);
            _tailLeft -= count;
            _position += count;
            return count;
        }

        public bool TryContinueFrom(IAudioDecoder previous) => false;

        public void Seek(long frame)
        {
            _position = Math.Clamp(frame, 0, frames);
            _tailLeft = _tailFrames;
        }

        public void Dispose() { }
    }

    private static double Sum(float[] samples)
    {
        var sum = 0.0;
        for (var i = 0; i < samples.Length; i += EngineHarness.Channels) sum += samples[i];
        return sum;
    }
}
