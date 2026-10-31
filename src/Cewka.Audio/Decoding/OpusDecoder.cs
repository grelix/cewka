using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Opus inside an Ogg container, decoded by Concentus.
/// <para>
/// Opus always runs at 48 kHz, so the only conversion this decoder ever needs is a channel
/// map plus a resample down to whatever the device asked for. The Ogg reader of Concentus
/// only unpacks the container; packets are decoded here, because the reader accepts nothing
/// but a single-stream decoder and ignores the header fields RFC 7845 requires a player
/// to honour: pre-skip, output gain and the channel mapping.
/// </para>
/// </summary>
internal sealed class OpusDecoder : ManagedDecoderBase
{
    private const int OpusSampleRate = 48000;

    /// <summary>Najdłuższy pakiet Opus: 120 ms przy 48 kHz.</summary>
    private const int MaxPacketFrames = 5760;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly OpusHead _head;
    private readonly IOpusDecoder? _single;
    private readonly IOpusMultiStreamDecoder? _multi;
    private OpusOggReadStream _reader;

    private readonly float[] _packet;
    private int _packetOffset;
    private int _packetFrames;
    private int _skip;
    private bool _exhausted;

    public OpusDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _head = OpusHead.Read(stream);

        try
        {
            if (_head.MappingFamily == 0)
                _single = OpusCodecFactory.CreateDecoder(OpusSampleRate, _head.Channels);
            else
                _multi = OpusCodecFactory.CreateMultiStreamDecoder(
                    OpusSampleRate, _head.Channels, _head.Streams, _head.CoupledStreams, _head.Mapping);
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            DecoderFailures.Log($"Concentus nie utworzył dekodera Opus: {ex.Message}");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile, ex);
        }

        // Wzmocnienie z nagłówka w tych samych jednostkach (Q7.8 dB), których oczekuje dekoder.
        if (_single is not null) _single.Gain = _head.OutputGain;
        if (_multi is not null) _multi.Gain = _head.OutputGain;

        _packet = new float[MaxPacketFrames * _head.Channels];
        _skip = _head.PreSkip;
        _reader = CreateReader();

        Configure(OpusSampleRate, _head.Channels, outputSampleRate, outputChannels, meter,
            ChannelLayouts.Vorbis(_head.Channels));

        var granules = _reader.GranuleCount - _head.PreSkip;
        TotalFrames = granules > 0 ? (long)Math.Round(granules * (double)outputSampleRate / OpusSampleRate) : 0;
    }

    public override bool CanSeek => _stream.CanSeek && _reader.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        if (_exhausted) return 0;

        var channels = SourceChannels;
        var written = 0;

        while (written < frames)
        {
            if (_packetFrames == 0 && !DecodeNextPacket()) break;

            var take = Math.Min(frames - written, _packetFrames);

            _packet.AsSpan(_packetOffset * channels, take * channels)
                .CopyTo(destination[(written * channels)..]);

            written += take;
            _packetOffset += take;
            _packetFrames -= take;
        }

        return written;
    }

    /// <summary>
    /// Decodes the next packet, dropping what is left of the pre-skip. A damaged packet is
    /// skipped rather than ending the track, the way a player treats a lost one.
    /// </summary>
    private bool DecodeNextPacket()
    {
        while (_reader.HasNextPacket)
        {
            var raw = _reader.ReadNextRawPacket();
            if (raw is null) return false;
            if (raw.Length == 0) continue;

            int decoded;
            try
            {
                decoded = _single is not null
                    ? _single.Decode(raw, _packet, MaxPacketFrames, false)
                    : _multi!.DecodeMultistream(raw, _packet, MaxPacketFrames, false);
            }
            catch (Exception ex)
            {
                DecoderFailures.Log($"Concentus pominął uszkodzony pakiet Opus: {ex.Message}");
                continue;
            }

            if (decoded <= 0) continue;

            var dropped = Math.Min(_skip, decoded);
            _skip -= dropped;

            _packetOffset = dropped;
            _packetFrames = decoded - dropped;
            if (_packetFrames > 0) return true;
        }

        return false;
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

        _single?.ResetState();
        _multi?.ResetState();

        if (sourceFrame <= 0)
        {
            _skip = _head.PreSkip;
            return;
        }

        _skip = 0;

        // Pozycja w granulach obejmuje pre-skip. Czytnik odrzuca cel dalszy niż TotalTime;
        // takty liczone całkowicie zaokrąglają w dół, więc obcięty cel nigdy go nie przekracza.
        var granule = Math.Min(sourceFrame + _head.PreSkip, _reader.GranuleCount);
        var target = TimeSpan.FromTicks(granule * TimeSpan.TicksPerSecond / OpusSampleRate);
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
            // Czytnik wymaga dekodera, choć tu służy wyłącznie do rozpakowania kontenera.
            var reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(OpusSampleRate, 1), _stream);

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

    protected override void DisposeCore()
    {
        _reader?.Close();
        if (_ownsStream) _stream.Dispose();
    }

    /// <summary>Pola nagłówka OpusHead (RFC 7845, rozdział 5.1), które wpływają na dekodowanie.</summary>
    private sealed record OpusHead(
        int Channels, int PreSkip, short OutputGain, int MappingFamily, int Streams, int CoupledStreams, byte[] Mapping)
    {
        private static readonly OpusHead Stereo = new(2, 0, 0, 0, 1, 1, [0, 1]);

        /// <summary>
        /// Reads the header from the first Ogg page, leaving the stream where it was. A stream
        /// that cannot seek is assumed to be plain stereo, the only guess the reader can recover from.
        /// </summary>
        public static OpusHead Read(Stream stream)
        {
            if (!stream.CanSeek) return Stereo;

            var origin = stream.Position;
            Span<byte> page = stackalloc byte[27 + 255 + 21 + 255];
            int read;

            try
            {
                read = stream.ReadAtLeast(page, page.Length, throwOnEndOfStream: false);
            }
            finally
            {
                stream.Position = origin;
            }

            if (read < 27 || page[0] != 'O' || page[1] != 'g' || page[2] != 'g' || page[3] != 'S')
                throw Damaged("brak strony Ogg");

            var payload = 27 + page[26];
            if (payload + 19 > read) throw Damaged("strona za krótka na nagłówek");

            var head = page[payload..read];
            if (!head[..8].SequenceEqual("OpusHead"u8)) throw Damaged("brak nagłówka OpusHead");

            var channels = head[9];
            var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
            var gain = BinaryPrimitives.ReadInt16LittleEndian(head[16..]);
            var family = head[18];

            if (channels == 0) throw Damaged("zero kanałów");

            if (family == 0)
            {
                if (channels > 2) throw Damaged("rodzina 0 z więcej niż dwoma kanałami");
                return new OpusHead(channels, preSkip, gain, 0, 1, channels - 1, channels == 1 ? [0] : [0, 1]);
            }

            // Rodzina 1 to układy Vorbis do 7.1; pozostałe (ambisonia, kanały bez położeń)
            // nie mają sensownego miksu do stereo.
            if (family != 1 || channels > 8)
            {
                DecoderFailures.Log($"Opus: nieobsługiwana rodzina mapowania {family} ({channels} kanałów).");
                throw DecoderFailures.Create(DecoderFailure.UnsupportedChannelLayout);
            }

            if (head.Length < 21 + channels) throw Damaged("niepełna tablica mapowania");

            var streams = head[19];
            var coupled = head[20];
            var mapping = head.Slice(21, channels).ToArray();

            if (streams == 0 || coupled > streams || streams + coupled > 255 ||
                mapping.Any(m => m != 255 && m >= streams + coupled))
                throw Damaged("niespójna tablica mapowania");

            return new OpusHead(channels, preSkip, gain, family, streams, coupled, mapping);
        }

        private static AudioException Damaged(string detail)
        {
            DecoderFailures.Log($"Opus: {detail}.");
            return DecoderFailures.Create(DecoderFailure.DamagedFile);
        }
    }
}
