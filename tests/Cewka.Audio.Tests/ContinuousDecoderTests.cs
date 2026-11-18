using Cewka.Audio.Decoding;
using Cewka.Audio.Dsp;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Końcówka filtru w dekoderze: wypłukiwana sama na końcu źródła, wstrzymywana na życzenie
/// i przejmowana przez następny dekoder w tym samym formacie.
/// </summary>
public sealed class ContinuousDecoderTests
{
    private const int SourceRate = 44100;
    private const int TargetRate = 48000;

    private static float[] Noise(int frames, int seed, int channels = 2)
    {
        var random = new Random(seed);
        var data = new float[frames * channels];
        for (var i = 0; i < data.Length; i++) data[i] = (float)(random.NextDouble() - 0.5);
        return data;
    }

    private static float[] ReadAll(IAudioDecoder decoder, int chunk = 1000)
    {
        var result = new List<float>();
        var buffer = new float[chunk * decoder.Channels];
        int read;
        while ((read = decoder.Read(buffer)) > 0) result.AddRange(buffer.AsSpan(0, read * decoder.Channels));
        return result.ToArray();
    }

    private static float[] DrainAll(IContinuousDecoder decoder, int channels)
    {
        var result = new List<float>();
        var buffer = new float[700 * channels];
        int read;
        while ((read = decoder.DrainTail(buffer)) > 0) result.AddRange(buffer.AsSpan(0, read * channels));
        return result.ToArray();
    }

    private static float[] Reference(float[] data, int sourceRate, int targetRate, ResamplingLevel level = ResamplingLevel.Standard) =>
        SincResamplerTests.Run(new SincResampler(2, sourceRate, targetRate, level), data, 2, 1234);

    [Fact]
    public void TailComesOutAtTheEndOfTheSource()
    {
        var data = Noise(20011, 1);
        using var decoder = new ArraySource(data, SourceRate, TargetRate);

        var output = ReadAll(decoder);

        Assert.Equal(SincResampler.OutputFrames(20011, SourceRate, TargetRate), output.Length / 2);
        Assert.Equal(output.Length / 2, decoder.Position);
        Assert.Equal(Reference(data, SourceRate, TargetRate), output);
    }

    [Fact]
    public void HeldTailWaitsForDrainTail()
    {
        var data = Noise(20011, 2);
        using var decoder = new ArraySource(data, SourceRate, TargetRate) { HoldTail = true };

        var played = ReadAll(decoder);
        var tail = DrainAll(decoder, 2);

        Assert.NotEmpty(tail);

        // Wstrzymana jest tylko końcówka filtru, a nie wyjście, które dało się już policzyć.
        var lookahead = new SincResampler(2, SourceRate, TargetRate, ResamplingLevel.Standard).Lookahead;
        Assert.InRange(tail.Length / 2, 1, (lookahead + 1) * TargetRate / SourceRate + 1);
        Assert.Equal(Reference(data, SourceRate, TargetRate), played.Concat(tail).ToArray());
        Assert.Equal((played.Length + tail.Length) / 2, decoder.Position);
        Assert.Equal(0, decoder.Read(new float[100]));
    }

    /// <summary>Dwa dekodery w tym samym formacie dają razem dokładnie to, co jeden z całym sygnałem.</summary>
    [Fact]
    public void NextDecoderContinuesTheStream()
    {
        var data = Noise(30000, 3);
        var cut = 17321 * 2;

        using var first = new ArraySource(data[..cut], SourceRate, TargetRate) { HoldTail = true };
        using var second = new ArraySource(data[cut..], SourceRate, TargetRate);

        var head = ReadAll(first);
        Assert.True(second.TryContinueFrom(first));
        Assert.Empty(DrainAll(first, 2));

        var rest = ReadAll(second);

        Assert.Equal(Reference(data, SourceRate, TargetRate), head.Concat(rest).ToArray());
    }

    [Fact]
    public void TakeOverIsRefusedWhenTheStreamsDoNotJoin()
    {
        var data = Noise(5000, 4);

        // Inna częstotliwość źródła.
        using (var first = Finished(data, SourceRate))
        using (var other = new ArraySource(data, 32000, TargetRate))
            Assert.False(other.TryContinueFrom(first));

        // Następny już coś przeczytał.
        using (var first = Finished(data, SourceRate))
        using (var started = new ArraySource(data, SourceRate, TargetRate))
        {
            started.Read(new float[10 * 2]);
            Assert.False(started.TryContinueFrom(first));
        }

        // Poprzedni nie doszedł do końca.
        using (var playing = new ArraySource(data, SourceRate, TargetRate) { HoldTail = true })
        using (var next = new ArraySource(data, SourceRate, TargetRate))
        {
            playing.Read(new float[100 * 2]);
            Assert.False(next.TryContinueFrom(playing));
        }

        // Inny poziom konwersji.
        using (var first = Finished(data, SourceRate))
        {
            ArraySource economy;
            using (AudioQuality.UseInThisThread(ResamplingLevel.Economy)) economy = new ArraySource(data, SourceRate, TargetRate);
            using (economy) Assert.False(economy.TryContinueFrom(first));
        }

        // Bez zmiany częstotliwości nie ma końcówki do przejęcia.
        using (var first = Finished(data, TargetRate))
        using (var same = new ArraySource(data, TargetRate, TargetRate))
            Assert.False(same.TryContinueFrom(first));
    }

    private static ArraySource Finished(float[] data, int rate)
    {
        var decoder = new ArraySource(data, rate, TargetRate) { HoldTail = true };
        ReadAll(decoder);
        return decoder;
    }

    /// <summary>
    /// Zmiana parametrów strumienia (stacja zmienia format, HE-AAC podwaja częstotliwość) zaczyna
    /// konwersję od zera: dalszy ciąg jest dokładnie taki, jak osobno przeliczona druga część.
    /// </summary>
    [Fact]
    public void ReconfigureStartsTheConversionFromScratch()
    {
        var firstPart = Noise(9000, 5);
        var secondPart = Noise(12000, 6);

        using var decoder = new SwitchingArraySource(TargetRate, (firstPart, SourceRate), (secondPart, SourceRate));
        var output = ReadAll(decoder, 777);

        // Część pierwsza bez końcówki: wszystko, czego filtr nie musiał dopełniać przyszłymi ramkami.
        var withoutTail = new SincResampler(2, SourceRate, TargetRate, ResamplingLevel.Standard);
        var head = withoutTail.Process(firstPart, 9000, new float[20000 * 2], out _);
        var expected = Reference(secondPart, SourceRate, TargetRate);

        Assert.Equal(expected, output[(head * 2)..]);
    }

    /// <summary>Analiza w tle otwiera dekodery na poziomie oszczędnym, nie zmieniając ustawienia innych wątków.</summary>
    [Fact]
    public void ThreadScopeChoosesTheLevelOnlyForThisThread()
    {
        var data = Noise(100, 7);
        var global = AudioQuality.Resampling;

        ArraySource scoped;
        ResamplingLevel? elsewhere = null;
        using (AudioQuality.UseInThisThread(ResamplingLevel.Economy))
        {
            scoped = new ArraySource(data, SourceRate, TargetRate);
            var thread = new Thread(() =>
            {
                using var other = new ArraySource(data, SourceRate, TargetRate);
                elsewhere = other.ResamplingLevel;
            });
            thread.Start();
            thread.Join();
        }

        using var after = new ArraySource(data, SourceRate, TargetRate);

        Assert.Equal(ResamplingLevel.Economy, scoped.ResamplingLevel);
        Assert.Equal(global, elsewhere);
        Assert.Equal(global, after.ResamplingLevel);
        Assert.Equal(global, AudioQuality.Resampling);
        scoped.Dispose();
    }

    /// <summary>Źródło z tablicy w zadanej częstotliwości.</summary>
    private sealed class ArraySource : ManagedDecoderBase
    {
        private readonly float[] _data;
        private int _cursor;

        public ArraySource(float[] data, int sourceRate, int targetRate)
        {
            _data = data;
            Configure(sourceRate, 2, targetRate, 2, null, ChannelLayouts.Wave(2));
            TotalFrames = SincResampler.OutputFrames(data.Length / 2, sourceRate, targetRate);
        }

        public override bool CanSeek => true;

        protected override int ReadSource(Span<float> destination, int frames)
        {
            var take = Math.Min(frames, _data.Length / 2 - _cursor);
            _data.AsSpan(_cursor * 2, take * 2).CopyTo(destination);
            _cursor += take;
            return take;
        }

        protected override void SeekSource(long sourceFrame) => _cursor = (int)Math.Min(sourceFrame, _data.Length / 2);
    }

    /// <summary>Kolejne części w swoich częstotliwościach; zmianę zapowiada jak dekoder systemowy.</summary>
    private sealed class SwitchingArraySource : ManagedDecoderBase
    {
        private readonly (float[] Data, int Rate)[] _parts;
        private int _part;
        private int _cursor;
        private bool _changePending;

        public SwitchingArraySource(int targetRate, params (float[] Data, int Rate)[] parts)
        {
            _parts = parts;
            Configure(parts[0].Rate, 2, targetRate, 2, null, ChannelLayouts.Wave(2));
        }

        public override bool CanSeek => false;

        protected override int ReadSource(Span<float> destination, int frames)
        {
            if (_part >= _parts.Length) return 0;

            if (_changePending)
            {
                _changePending = false;
                Reconfigure(_parts[_part].Rate, 2, ChannelLayouts.Wave(2));
                return 0;
            }

            var data = _parts[_part].Data;
            var take = Math.Min(frames, data.Length / 2 - _cursor);
            data.AsSpan(_cursor * 2, take * 2).CopyTo(destination);
            _cursor += take;

            if (_cursor == data.Length / 2)
            {
                _part++;
                _cursor = 0;
                _changePending = _part < _parts.Length;
            }

            return take;
        }

        protected override void SeekSource(long sourceFrame) { }
    }
}
