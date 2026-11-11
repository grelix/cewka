using System.Text;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Dsp;
using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

/// <summary>
/// Odbiorca strumienia w pamięci: dekoduje ramki, liczy z nich przepływność, format i głośność,
/// zbiera tytuł.
/// </summary>
internal sealed class FrameAnalysis(
    LiveCodec codec, bool measureLoudness, Encoding titleFallback, string? stationName, Action onFirstAudio)
    : IIcySink, IDisposable
{
    private const int WindowBytes = 32 * 1024;

    private readonly ILiveFrameDecoder? _decoder = LiveFrameDecoders.Create(codec);
    private readonly byte[] _window = new byte[WindowBytes];
    private float[]? _pcm;
    private int _start;
    private int _end;
    private long _frames;
    private long _kbpsFrames;
    private double _kbpsSum;
    private LoudnessAnalyser? _loudness;

    public bool HasDecoder => _decoder is not null;

    /// <summary>Dane są jeszcze do czegoś potrzebne; Ogg i FLAC poznaje się po pierwszej porcji.</summary>
    public bool WantsData => _decoder is not null;

    /// <summary>Dekoder odmówił tego strumienia, na przykład brakuje dekodera systemu.</summary>
    public LiveFailure? DecoderFailure => _decoder?.Failure;

    public long DecodedFrames => _frames;
    public int? SampleRate { get; private set; }
    public int? Channels { get; private set; }
    public string? Title { get; private set; }

    /// <summary>Null, gdy próbka była za krótka albo cicha, żeby ją zmierzyć.</summary>
    public double? Lufs => _loudness?.ComputeIntegratedLoudness();

    public int? MeasuredKbps
    {
        get
        {
            if (_kbpsFrames > 0)
                return (int)Math.Round(_kbpsSum / _kbpsFrames);
            return null;
        }
    }

    public void OnAudio(ReadOnlySpan<byte> audio)
    {
        if (!WantsData)
            return;

        while (!audio.IsEmpty)
        {
            Compact();
            var count = Math.Min(audio.Length, WindowBytes - _end);
            audio[..count].CopyTo(_window.AsSpan(_end));
            _end += count;
            audio = audio[count..];

            if (_decoder is not null)
                Decode(_decoder.MinInputBytes);
        }
    }

    public void OnMetadata(ReadOnlySpan<byte> block)
    {
        var fields = IcyMetadataParser.Parse(block, titleFallback);
        if (!fields.IsAd && IcyMetadataParser.Normalise(fields.Title, stationName) is { } title)
            Title = title;
    }

    /// <summary>Koniec próbki: dalszych bajtów nie będzie, więc reguła minimum wejścia już nie obowiązuje.</summary>
    public void Finish()
    {
        if (_decoder is null)
            return;

        Decode(1);

        _pcm ??= new float[_decoder.MaxOutputSamples];
        while (_decoder.Drain(_pcm, out var format) is > 0 and var frames)
            AddFrames(frames, format, _pcm);
    }

    private void Decode(int minBytes)
    {
        _pcm ??= new float[_decoder!.MaxOutputSamples];
        while (_end - _start >= minBytes)
        {
            var frames = _decoder!.Decode(_window.AsSpan(_start, _end - _start), _pcm, out var consumed, out var format);
            if (frames > 0)
                AddFrames(frames, format, _pcm);

            if (consumed == 0 && frames == 0)
            {
                // Dekoder czeka na resztę ramki; dopiero pełne okno oznacza dane nie do odczytania.
                if (minBytes == 1 || _end - _start < WindowBytes)
                    return;

                _start++;
                continue;
            }

            _start += consumed;
        }
    }

    private void AddFrames(int frames, LiveFrameFormat format, float[] pcm)
    {
        if (_frames == 0)
            onFirstAudio();

        // Filtry pomiaru zależą od formatu; po zmianie liczy się już tylko nowy.
        if (measureLoudness && (_loudness is null || format.SampleRate != SampleRate || format.Channels != Channels))
            _loudness = new LoudnessAnalyser(format.SampleRate, format.Channels);
        _loudness?.Add(pcm, frames);

        _frames += frames;
        SampleRate = format.SampleRate;
        Channels = format.Channels;
        if (format.BitrateKbps > 0)
        {
            _kbpsSum += (double)format.BitrateKbps * frames;
            _kbpsFrames += frames;
        }
    }

    private void Compact()
    {
        if (_start == 0)
            return;

        _window.AsSpan(_start, _end - _start).CopyTo(_window);
        _end -= _start;
        _start = 0;
    }

    public void Dispose() => _decoder?.Dispose();
}
