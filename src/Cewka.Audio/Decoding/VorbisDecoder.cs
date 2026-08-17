using NVorbis;

namespace Cewka.Audio.Decoding;

/// <summary>Ogg Vorbis, decoded by NVorbis.</summary>
internal sealed class VorbisDecoder : ManagedDecoderBase
{
    private readonly VorbisReader _reader;
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public VorbisDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        try
        {
            // NVorbis closes the stream itself only when told to; ownership stays here.
            _reader = new VorbisReader(stream, closeOnDispose: false);
        }
        catch (Exception ex)
        {
            throw new AudioException($"Plik Ogg Vorbis jest uszkodzony lub nieczytelny — {ex.Message}", ex);
        }

        Configure(_reader.SampleRate, _reader.Channels, outputSampleRate, outputChannels, meter);

        TotalFrames = _reader.TotalSamples <= 0
            ? 0
            : (long)Math.Round(_reader.TotalSamples * (double)outputSampleRate / _reader.SampleRate);
    }

    public override bool CanSeek => _stream.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        // NVorbis counts in individual samples, not in frames.
        var samples = _reader.ReadSamples(destination[..(frames * SourceChannels)]);
        return samples / SourceChannels;
    }

    protected override void SeekSource(long sourceFrame)
    {
        var target = Math.Min(sourceFrame, _reader.TotalSamples > 0 ? _reader.TotalSamples - 1 : sourceFrame);
        _reader.SeekTo(Math.Max(0, target), SeekOrigin.Begin);
    }

    protected override void DisposeCore()
    {
        _reader.Dispose();
        if (_ownsStream) _stream.Dispose();
    }
}
