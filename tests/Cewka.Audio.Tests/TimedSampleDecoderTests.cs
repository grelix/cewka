using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Przycinanie rozbiegu nad atrapami źródła, które oddają próbki tak, jak robią to różne
/// wydania Media Foundation. Plik: ramki AAC po 1024, rozbieg 1024 albo 2112 (koder Apple),
/// nagranie 70 000 ramek, lista edycji od rozbiegu. Wartość ramki to jej numer w nagraniu;
/// rozbieg ma −1, dopełnienie −2, więc wynik mówi wprost, co zagrało i w jakiej kolejności.
/// </summary>
public sealed class TimedSampleDecoderTests
{
    private const int Rate = 44100;
    private const int Frame = 1024;
    private const int Length = 70000;

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

        /// <summary>Jak wyżej, z czasami zaokrąglanymi jak na maszynach GitHuba: [0, 0, 232200, …].</summary>
        EditedClampedRounded,

        /// <summary>Oś przesunięta o 500 ramek: koniec nie pasuje do żadnego układu.</summary>
        Ambiguous,
    }

    private static int Blocks(int delay) => (delay + Length + Frame - 1) / Frame;

    private static Mp4Gapless File(int delay) => new(Rate, delay, Length, Mp4GaplessSource.EditList)
    {
        Samples = delay + Length,
        SampleCount = Blocks(delay),
        FrameDuration = Frame,
        EditDelay = delay,
    };

    private sealed class FakeSource : ITimedSampleSource
    {
        private readonly int _delay;
        private readonly bool _round;
        private readonly List<(long Start, int Frames, long Time)> _blocks = [];
        private readonly float[] _samples = new float[Frame * 2];
        private int _next;
        private int _count;

        public FakeSource(Behaviour behaviour, int delay)
        {
            _delay = delay;
            _round = behaviour == Behaviour.EditedClampedRounded;

            for (var k = 0; k < Blocks(delay); k++)
            {
                long start = k * Frame;
                switch (behaviour)
                {
                    case Behaviour.Absolute: _blocks.Add((start, Frame, start)); break;
                    case Behaviour.EditedNegative: _blocks.Add((start, Frame, start - delay)); break;
                    case Behaviour.Ambiguous: _blocks.Add((start, Frame, start + 500)); break;
                    case Behaviour.EditedSkipped:
                        if (start + Frame <= delay) break;
                        if (start < delay) _blocks.Add((delay, (int)(start + Frame - delay), 0));
                        else _blocks.Add((start, Frame, start - delay));
                        break;
                    default: _blocks.Add((start, Frame, Math.Max(0, start - delay))); break;
                }
            }
        }

        public int Seeks { get; private set; }
        public int Rate => TimedSampleDecoderTests.Rate;
        public int Channels => 2;
        public uint Mask => 3;
        public TimeSpan? Duration => null;
        public ReadOnlySpan<float> Samples => _samples.AsSpan(0, _count * 2);

        private float Value(long fileFrame) =>
            fileFrame < _delay ? -1 : fileFrame < _delay + Length ? fileFrame - _delay : -2;

        private long Ticks(long frames)
        {
            var exact = frames * 10_000_000.0 / TimedSampleDecoderTests.Rate;
            return _round ? (long)Math.Round(exact) : (long)exact;
        }

        public bool Next(out TimedBlock block)
        {
            block = default;
            _count = 0;
            if (_next >= _blocks.Count) return false;

            var (start, frames, time) = _blocks[_next++];
            for (var i = 0; i < frames; i++) _samples[2 * i] = _samples[2 * i + 1] = Value(start + i);

            _count = frames;
            block = new TimedBlock(Ticks(time), Rate, 2, 3, frames);
            return true;
        }

        /// <summary>
        /// Staje na ostatniej porcji, która zaczyna się nie później niż cel; cel przed pierwszą
        /// porcją albo kilka porcji z tym samym czasem: pierwsza z nich.
        /// </summary>
        public void Seek(long ticks)
        {
            Seeks++;
            _count = 0;
            var target = ticks * (double)TimedSampleDecoderTests.Rate / 10_000_000;
            var landing = 0;
            for (var b = 1; b < _blocks.Count; b++)
                if (_blocks[b].Time <= target + 0.5 && _blocks[b].Time > _blocks[landing].Time) landing = b;

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
    [InlineData(Behaviour.Absolute, 1024)]
    [InlineData(Behaviour.Absolute, 2112)]
    [InlineData(Behaviour.EditedSkipped, 1024)]
    [InlineData(Behaviour.EditedSkipped, 2112)]
    [InlineData(Behaviour.EditedNegative, 1024)]
    [InlineData(Behaviour.EditedNegative, 2112)]
    [InlineData(Behaviour.EditedClamped, 1024)]
    [InlineData(Behaviour.EditedClamped, 2112)]
    [InlineData(Behaviour.EditedClampedRounded, 1024)]
    [InlineData(Behaviour.EditedClampedRounded, 2112)]
    public void RozpoznanaOsGraSamoNagranie(Behaviour behaviour, int delay)
    {
        using var decoder = new Decoder(new FakeSource(behaviour, delay), File(delay));
        var output = ReadAll(decoder);
        var report = decoder.TimelineReport;

        Assert.Equal(Length, decoder.TotalFrames);
        Assert.True(Length == output.Length, $"ramek {output.Length}; {report}");
        Assert.True(output[0] == 0, $"pierwsza ramka {output[0]}; {report}");
        AssertRecordingIntact(output, 0, report);
    }

    [Theory]
    [InlineData(Behaviour.Absolute, 1024, 30000)]
    [InlineData(Behaviour.EditedSkipped, 2112, 30000)]
    [InlineData(Behaviour.EditedNegative, 2112, 30000)]
    [InlineData(Behaviour.EditedClamped, 1024, 30000)]
    [InlineData(Behaviour.EditedClamped, 2112, 30000)]
    [InlineData(Behaviour.EditedClampedRounded, 1024, 30000)]
    [InlineData(Behaviour.EditedClampedRounded, 1024, 0)]
    [InlineData(Behaviour.EditedClampedRounded, 1024, 1)]
    [InlineData(Behaviour.EditedClampedRounded, 1024, 3000)]
    [InlineData(Behaviour.EditedClamped, 2112, 5000)]
    [InlineData(Behaviour.EditedClamped, 2112, 69999)]
    public void RozpoznanaOsPrzewijaCoDoRamki(Behaviour behaviour, int delay, int target)
    {
        using var decoder = new Decoder(new FakeSource(behaviour, delay), File(delay));
        ReadAll(decoder);
        decoder.Seek(target);
        var output = ReadAll(decoder);
        var report = decoder.TimelineReport;

        Assert.Equal(target, decoder.Position - output.Length);
        Assert.True(Length - target == output.Length, $"ramek {output.Length}; {report}");
        Assert.True(output[0] == target, $"pierwsza ramka {output[0]}; {report}");
        AssertRecordingIntact(output, target, report);
    }

    [Fact]
    public void NiejednoznacznaSondaNieObcinaNiczego()
    {
        using var decoder = new Decoder(new FakeSource(Behaviour.Ambiguous, 1024), File(1024));
        var report = decoder.TimelineReport;
        var output = ReadAll(decoder);

        Assert.Equal(Blocks(1024) * Frame, output.Length);
        Assert.Equal(-1, output[0]);
        AssertRecordingIntact(output, 0, report);
        Assert.Contains("nie pasuje", report);

        decoder.Seek(30000);
        AssertRecordingIntact(ReadAll(decoder), 30000, report);
    }

    /// <summary>
    /// Porcja, której czas nie zgadza się z licznikiem (system zgubił ramki albo zmienił oś),
    /// kończy przycinanie od tego miejsca; to, co już zagrało, zostaje nienaruszone.
    /// </summary>
    [Fact]
    public void NiezgodnyCzasPorcjiKonczyPrzycinanie()
    {
        var source = new GappySource(new FakeSource(Behaviour.Absolute, 1024), skipBlock: 30);
        using var decoder = new Decoder(source, File(1024) with { EditDelay = 0, Source = Mp4GaplessSource.ITunSmpb });
        var output = ReadAll(decoder);

        Assert.Equal(0, output[0]);
        Assert.Contains("nie zgadza się z licznikiem", decoder.TimelineReport);
        Assert.Equal(-2, output[^1]);
    }

    /// <summary>Atrapa gubiąca jedną porcję.</summary>
    private sealed class GappySource(FakeSource inner, int skipBlock) : ITimedSampleSource
    {
        private int _index;

        public int Rate => inner.Rate;
        public int Channels => inner.Channels;
        public uint Mask => inner.Mask;
        public TimeSpan? Duration => null;
        public ReadOnlySpan<float> Samples => inner.Samples;

        public bool Next(out TimedBlock block)
        {
            if (_index++ == skipBlock) inner.Next(out _);
            return inner.Next(out block);
        }

        public void Seek(long ticks)
        {
            _index = 0;
            inner.Seek(ticks);
        }

        public void Dispose() { }
    }

    /// <summary>Sam znacznik iTunes bez listy edycji: czas od początku ścieżki, bez sond.</summary>
    [Fact]
    public void ZnacznikBezListyEdycjiNieWymagaSondy()
    {
        var source = new FakeSource(Behaviour.Absolute, 1024);
        var smpb = File(1024) with { Source = Mp4GaplessSource.ITunSmpb, EditDelay = 0 };

        using var decoder = new Decoder(source, smpb);
        Assert.Equal(0, source.Seeks);

        var output = ReadAll(decoder);
        Assert.Equal(Length, output.Length);
        AssertRecordingIntact(output, 0, decoder.TimelineReport);
    }

    [Fact]
    public void BezOpisuGraWszystko()
    {
        using var decoder = new Decoder(new FakeSource(Behaviour.Absolute, 1024), null);

        Assert.Equal(Blocks(1024) * Frame, ReadAll(decoder).Length);
    }
}
