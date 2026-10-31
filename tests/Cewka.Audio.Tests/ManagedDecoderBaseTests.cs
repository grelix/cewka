using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Zmiana formatu w trakcie strumienia, jak w HE-AAC z niejawnym SBR: kodek podaje
/// częstotliwość rdzenia, a pierwsza zdekodowana próbka przychodzi w dwa razy wyższej.
/// </summary>
public sealed class ManagedDecoderBaseTests
{
    [Fact]
    public void FormatChangeMidStreamKeepsPlaying()
    {
        using var decoder = new SwitchingSource(48000, 2, (22050, 1, 22050), (44100, 2, 44100));

        var buffer = new float[4096 * 2];
        long frames = 0;
        long audibleAfterChange = 0;
        int read;

        while ((read = decoder.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                // Druga część ma inną wartość w każdym kanale, więc widać, że kanały nie są pomieszane.
                if (Math.Abs(buffer[2 * i] - 0.25f) < 0.01f && Math.Abs(buffer[2 * i + 1] - 0.5f) < 0.01f)
                    audibleAfterChange++;
            }
            frames += read;
        }

        Assert.InRange(frames, 2 * 48000 - 256, 2 * 48000 + 256);
        Assert.True(audibleAfterChange > 47000, $"ramek po zmianie: {audibleAfterChange}");
        Assert.Equal(44100, decoder.SourceSampleRate);
        Assert.Equal(2, decoder.SourceChannels);
    }

    [Fact]
    public void ChangeAtTheVeryStartIsNotTakenForTheEnd()
    {
        using var decoder = new SwitchingSource(48000, 2, (24000, 2, 0), (48000, 2, 4800));

        var buffer = new float[4800 * 2];
        Assert.Equal(4800, decoder.Read(buffer));
    }

    /// <summary>
    /// Źródło złożone z części o różnych formatach. Kolejną część zapowiada tak jak dekoder
    /// systemowy: przebudowa konwertera dopiero przy pustym buforze, potem zero i ponowne pytanie.
    /// </summary>
    private sealed class SwitchingSource : ManagedDecoderBase
    {
        private readonly (int Rate, int Channels, int Frames)[] _parts;
        private int _part;
        private int _offset;
        private bool _changePending;

        public SwitchingSource(int targetRate, int targetChannels, params (int Rate, int Channels, int Frames)[] parts)
        {
            _parts = parts;
            Configure(parts[0].Rate, parts[0].Channels, targetRate, targetChannels, null,
                ChannelLayouts.Wave(parts[0].Channels));
            _changePending = parts[0].Frames == 0 && Advance();
        }

        public override bool CanSeek => false;

        protected override int ReadSource(Span<float> destination, int frames)
        {
            var written = 0;

            while (written < frames && _part < _parts.Length)
            {
                if (_changePending)
                {
                    if (written > 0) break;

                    _changePending = false;
                    var next = _parts[_part];
                    Reconfigure(next.Rate, next.Channels, ChannelLayouts.Wave(next.Channels));
                    return 0;
                }

                var (_, channels, total) = _parts[_part];
                var take = Math.Min(frames - written, total - _offset);

                for (var i = 0; i < take; i++)
                    for (var c = 0; c < channels; c++)
                        destination[(written + i) * channels + c] = 0.25f * (c + 1);

                written += take;
                _offset += take;

                if (_offset == total) _changePending = Advance();
            }

            return written;
        }

        private bool Advance()
        {
            _part++;
            _offset = 0;
            return _part < _parts.Length;
        }

        protected override void SeekSource(long sourceFrame) { }
    }
}
