using Concentus;
using Concentus.Oggfile;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Opus inside an Ogg container, decoded by Concentus.
/// <para>
/// Opus always runs at 48 kHz, so the only conversion this decoder ever needs is a channel
/// map plus a resample down to whatever the device asked for. The channel count has to be
/// read out of the OpusHead packet before the decoder is created, because Concentus expects
/// it up front and its Ogg reader does not expose it.
/// </para>
/// </summary>
internal sealed class OpusDecoder : ManagedDecoderBase
{
    private const int OpusSampleRate = 48000;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _sourceChannels;
    private OpusOggReadStream _reader;

    private short[] _packet = [];
    private int _packetOffset;
    private int _packetFrames;
    private bool _exhausted;

    public OpusDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _sourceChannels = ReadChannelCount(stream);

        _reader = CreateReader();

        Configure(OpusSampleRate, _sourceChannels, outputSampleRate, outputChannels, meter);

        var totalSeconds = _reader.TotalTime.TotalSeconds;
        TotalFrames = totalSeconds > 0 ? (long)Math.Round(totalSeconds * outputSampleRate) : 0;
    }

    public override bool CanSeek => _stream.CanSeek && _reader.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        if (_exhausted) return 0;

        var written = 0;

        while (written < frames)
        {
            if (_packetFrames == 0)
            {
                if (!_reader.HasNextPacket) break;

                var decoded = _reader.DecodeNextPacket();
                if (decoded is null || decoded.Length == 0) break;

                _packet = decoded;
                _packetOffset = 0;
                _packetFrames = decoded.Length / SourceChannels;
            }

            var take = Math.Min(frames - written, _packetFrames);

            // Concentus returns 16-bit samples; scale into the pipeline's float range.
            for (var i = 0; i < take * SourceChannels; i++)
            {
                destination[written * SourceChannels + i] =
                    _packet[_packetOffset * SourceChannels + i] / 32768f;
            }

            written += take;
            _packetOffset += take;
            _packetFrames -= take;
        }

        return written;
    }

    /// <summary>
    /// Concentus's Ogg reader does not recover once it has run past the last packet — a
    /// later <c>SeekTo</c> leaves it reporting no further packets. Building a fresh reader
    /// sidesteps that entirely, and seeking is a rare, user-initiated action, so the cost
    /// of re-reading the header does not matter.
    /// </summary>
    protected override void SeekSource(long sourceFrame)
    {
        _packetOffset = 0;
        _packetFrames = 0;

        _exhausted = false;

        _reader.Close();
        _stream.Position = 0;
        _reader = CreateReader();

        if (sourceFrame <= 0) return;

        // Czytnik odrzuca cel dalszy niż TotalTime. Takty liczone całkowicie zaokrąglają
        // w dół, więc cel obcięty do długości nigdy jej nie przekracza.
        var target = TimeSpan.FromTicks(sourceFrame * TimeSpan.TicksPerSecond / OpusSampleRate);
        if (target > _reader.TotalTime) target = _reader.TotalTime;

        try
        {
            _reader.SeekTo(target);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            DecoderFailures.Log($"Concentus nie przewinął pliku Opus: {ex.Message}");
            _exhausted = true;
        }
    }

    private OpusOggReadStream CreateReader()
    {
        try
        {
            // Through the factory rather than the constructor: it can hand back a native
            // implementation where one is available, and the constructor is deprecated.
            var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, _sourceChannels);
            var reader = new OpusOggReadStream(decoder, _stream);

            // Czytnik nie rzuca wyjątku przy złym pliku, tylko zapisuje opis błędu.
            if (!string.IsNullOrEmpty(reader.LastError))
            {
                DecoderFailures.Log($"Concentus nie otworzył pliku: {reader.LastError}");
                throw DecoderFailures.Create(DecoderFailure.DamagedFile);
            }

            return reader;
        }
        catch (AudioException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DecoderFailures.Log($"Concentus nie otworzył pliku: {ex.Message}");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile, ex);
        }
    }

    /// <summary>
    /// Reads the channel count from the OpusHead packet, which sits in the first Ogg page:
    /// magic (8 bytes), version (1), channel count (1).
    /// </summary>
    private static int ReadChannelCount(Stream stream)
    {
        if (!stream.CanSeek) return 2;

        var origin = stream.Position;
        try
        {
            Span<byte> page = stackalloc byte[80];
            var read = stream.ReadAtLeast(page, page.Length, throwOnEndOfStream: false);
            if (read < 35) return 2;

            var segmentCount = page[26];
            var payload = 27 + segmentCount;
            if (payload + 10 > read) return 2;

            var head = page[payload..];
            if (head[0] != 'O' || head[1] != 'p' || head[2] != 'u' || head[3] != 's') return 2;

            var channels = head[9];
            return channels is >= 1 and <= 8 ? channels : 2;
        }
        catch
        {
            return 2;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    protected override void DisposeCore()
    {
        _reader.Close();
        if (_ownsStream) _stream.Dispose();
    }
}
