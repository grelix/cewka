using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Po przewinięciu dekoder oddaje wyłącznie dźwięk z nowego miejsca. Resampler pamięta
/// ostatnie próbki i stan filtru, więc bez wyczyszczenia początek nowego miejsca byłby
/// mieszaniną ze starym.
/// </summary>
public sealed class ManagedDecoderSeekTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void SeekIntoSilenceAfterSignalGivesSilence(int sourceRate)
    {
        using var decoder = new SignalThenSilence(sourceRate, 48000);

        var buffer = new float[4800 * 2];
        Assert.Equal(4800, decoder.Read(buffer));
        Assert.Contains(buffer, sample => Math.Abs(sample) > 0.1f);

        decoder.Seek(48000 * 3 / 2);

        var after = new float[512 * 2];
        Assert.Equal(512, decoder.Read(after));
        Assert.All(after, sample => Assert.Equal(0f, sample));
    }

    /// <summary>Sekunda sinusa 1 kHz, potem sekunda dokładnej ciszy; stereo.</summary>
    private sealed class SignalThenSilence : ManagedDecoderBase
    {
        private readonly int _rate;
        private long _cursor;

        public SignalThenSilence(int sourceRate, int targetRate)
        {
            _rate = sourceRate;
            Configure(sourceRate, 2, targetRate, 2, null, ChannelLayouts.Wave(2));
            TotalFrames = 2L * targetRate;
        }

        public override bool CanSeek => true;

        protected override int ReadSource(Span<float> destination, int frames)
        {
            var take = (int)Math.Min(frames, 2L * _rate - _cursor);

            for (var i = 0; i < take; i++)
            {
                var frame = _cursor + i;
                var value = frame < _rate ? (float)(0.9 * Math.Sin(2 * Math.PI * 1000 * frame / _rate)) : 0f;
                destination[2 * i] = value;
                destination[2 * i + 1] = value;
            }

            _cursor += take;
            return take;
        }

        protected override void SeekSource(long sourceFrame) => _cursor = sourceFrame;
    }
}
