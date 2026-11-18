using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Przycinanie rozbiegu nad atrapami źródła, które oddają próbki tak, jak mogą to robić różne
/// wydania Media Foundation. Plik: 70 ramek AAC po 1024, rozbieg 1024, nagranie 70 000 ramek,
/// lista edycji od 1024. Wartość ramki to jej numer w nagraniu; rozbieg ma −1, dopełnienie −2,
/// więc wynik mówi wprost, co zagrało i w jakiej kolejności.
/// </summary>
public sealed class TimedSampleDecoderTests
{
    private const int Rate = 44100;
    private const int Frame = 1024;
    private const int Blocks = 70;
    private const int Delay = 1024;
    private const int Length = 70000;

    private static readonly Mp4Gapless EditList = new(Rate, Delay, Length, Mp4GaplessSource.EditList)
    {
        Samples = Delay + Length,
        SampleCount = Blocks,
        FrameDuration = Frame,
        EditDelay = Delay,
    };

    public enum Behaviour
    {
        /// <summary>Czas od początku ścieżki, rozbieg oddawany (Windows 11 26300).</summary>
        Absolute,

        /// <summary>Czas od początku edycji, rozbieg pominięty.</summary>
        EditedSkipped,

        /// <summary>Czas od początku edycji, rozbieg z czasem ujemnym.</summary>
        EditedNegative,

        /// <summary>Czas od początku edycji, rozbieg z czasem przyciętym do zera.</summary>
        EditedClamped,

        /// <summary>Oś przesunięta o 500 ramek: koniec nie pasuje do żadnego układu.</summary>
        Ambiguous,
    }

    private static float Value(long fileFrame) =>
        fileFrame < Delay ? -1 : fileFrame < Delay + Length ? fileFrame - Delay : -2;

    private sealed class FakeSource(Behaviour behaviour) : ITimedSampleSource
    {
        private readonly float[] _samples = new float[Frame * 2];
        private int _next;

        public int Seeks { get; private set; }
        public int Rate => TimedSampleDecoderTests.Rate;
        public int Channels => 2;
        public uint Mask => 3;
        public TimeSpan? Duration => null;
        public ReadOnlySpan<float> Samples => _samples;

        private bool Delivered(int block) => !(behaviour == Behaviour.EditedSkipped && block == 0);

        /// <summary>Czas porcji w ramkach na osi źródła.</summary>
        private long Time(int block)
        {
            long start = block * Frame;
            return behaviour switch
            {
                Behaviour.Absolute => start,
                Behaviour.EditedClamped => Math.Max(0, start - Delay),
                Behaviour.Ambiguous => start + 500,
                _ => start - Delay,
            };
        }

        // Jak Media Foundation: czas w 100 ns obcięty do całości.
        private static long Ticks(long frames) => (long)(frames * 10_000_000.0 / TimedSampleDecoderTests.Rate);

        public bool Next(out TimedBlock block)
        {
            block = default;
            while (_next < Blocks && !Delivered(_next)) _next++;
            if (_next >= Blocks) return false;

            for (var i = 0; i < Frame; i++)
                _samples[2 * i] = _samples[2 * i + 1] = Value((long)_next * Frame + i);

            block = new TimedBlock(Ticks(Time(_next)), Rate, 2, 3, Frame);
            _next++;
            return true;
        }

        /// <summary>Staje na ostatniej porcji, która zaczyna się nie później niż cel.</summary>
        public void Seek(long ticks)
        {
            Seeks++;
            var target = ticks * (double)TimedSampleDecoderTests.Rate / 10_000_000;
            // Cel przed pierwszą porcją: pierwsza porcja. Kilka porcji z tym samym czasem: pierwsza z nich.
            var landing = -1;
            for (var b = 0; b < Blocks; b++)
            {
                if (!Delivered(b)) continue;
                if (landing < 0 || (Time(b) <= target + 0.5 && Time(b) > Time(landing))) landing = b;
            }

            _next = landing;
        }

        public void Dispose() { }
    }

    private sealed class Decoder(ITimedSampleSource source, Mp4Gapless? gapless)
        : TimedSampleDecoder(source, gapless, Rate, 2, null);

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var all = new List<float>();
        var buffer = new float[1000 * 2];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
            for (var i = 0; i < read; i++) all.Add(buffer[2 * i]);
        return [.. all];
    }

    /// <summary>Wartości nagrania od <paramref name="from"/> do końca stoją w wyniku kolejno, bez luki.</summary>
    private static void AssertRecordingIntact(float[] output, int from, string report)
    {
        var start = Array.IndexOf(output, (float)from);
        Assert.True(start >= 0, $"brak ramki {from} nagrania; {report}");
        for (var i = 0; i < Length - from; i++)
            Assert.True(start + i < output.Length && output[start + i] == from + i,
                $"ramka {from + i} nagrania zgubiona albo nie na miejscu; {report}");
    }

    [Theory]
    [InlineData(Behaviour.Absolute)]
    [InlineData(Behaviour.EditedSkipped)]
    [InlineData(Behaviour.EditedNegative)]
    public void RozpoznanaOsGraSamoNagranie(Behaviour behaviour)
    {
        using var decoder = new Decoder(new FakeSource(behaviour), EditList);
        var report = decoder.TimelineReport;
        var output = ReadAll(decoder);

        Assert.Equal(Length, decoder.TotalFrames);
        Assert.True(Length == output.Length, $"ramek {output.Length}; {report}");
        AssertRecordingIntact(output, 0, report);
    }

    [Theory]
    [InlineData(Behaviour.Absolute)]
    [InlineData(Behaviour.EditedSkipped)]
    [InlineData(Behaviour.EditedNegative)]
    [InlineData(Behaviour.EditedClamped)]
    public void RozpoznanaOsPrzewijaCoDoRamki(Behaviour behaviour)
    {
        using var decoder = new Decoder(new FakeSource(behaviour), EditList);
        decoder.Seek(30000);
        var output = ReadAll(decoder);

        Assert.Equal(30000, decoder.Position - output.Length);
        Assert.Equal(Length - 30000, output.Length);
        AssertRecordingIntact(output, 30000, decoder.TimelineReport);
    }

    /// <summary>
    /// Rozbieg z czasem przyciętym do zera ma ten sam czas co pierwsza ramka nagrania: miejsca
    /// w pliku nie da się ustalić, więc gra wszystko. Rozbieg może zostać, nagranie nie może zginąć.
    /// </summary>
    [Fact]
    public void CzasPrzycietyDoZeraNieGubiNagrania()
    {
        using var decoder = new Decoder(new FakeSource(Behaviour.EditedClamped), EditList);
        var output = ReadAll(decoder);

        AssertRecordingIntact(output, 0, decoder.TimelineReport);
        Assert.Contains("cofają", decoder.TimelineReport);
    }

    [Fact]
    public void NiejednoznacznaSondaNieObcinaNiczego()
    {
        using var decoder = new Decoder(new FakeSource(Behaviour.Ambiguous), EditList);
        var report = decoder.TimelineReport;
        var output = ReadAll(decoder);

        Assert.Equal(Blocks * Frame, output.Length);
        Assert.Equal(-1, output[0]);
        AssertRecordingIntact(output, 0, report);
        Assert.Contains("nie pasuje", report);

        decoder.Seek(30000);
        AssertRecordingIntact(ReadAll(decoder), 30000, report);
    }

    /// <summary>Sam znacznik iTunes bez listy edycji: czas od początku ścieżki, bez sondy.</summary>
    [Fact]
    public void ZnacznikBezListyEdycjiNieWymagaSondy()
    {
        var source = new FakeSource(Behaviour.Absolute);
        var smpb = EditList with { Source = Mp4GaplessSource.ITunSmpb, EditDelay = 0 };

        using var decoder = new Decoder(source, smpb);
        Assert.Equal(0, source.Seeks);

        var output = ReadAll(decoder);
        Assert.Equal(Length, output.Length);
        AssertRecordingIntact(output, 0, decoder.TimelineReport);
    }

    [Fact]
    public void BezOpisuGraWszystko()
    {
        using var decoder = new Decoder(new FakeSource(Behaviour.Absolute), null);

        Assert.Equal(Blocks * Frame, ReadAll(decoder).Length);
    }
}
