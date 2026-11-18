using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Opus w kontenerze Ogg, dekodowany przez Concentus.
/// <para>
/// Czytnik Ogg z Concentus tylko rozpakowuje kontener; pakiety dekodowane są tutaj, bo
/// czytnik przyjmuje wyłącznie dekoder jednostrumieniowy i pomija pola nagłówka wymagane przez
/// RFC 7845: pre-skip, wzmocnienie wyjściowe i mapowanie kanałów.
/// </para>
/// </summary>
internal sealed class OpusDecoder : ManagedDecoderBase
{
    private const int OpusSampleRate = 48000;

    /// <summary>Najdłuższy pakiet Opus: 120 ms przy 48 kHz.</summary>
    private const int MaxPacketFrames = 5760;

    /// <summary>Trzydzieści dni dźwięku; dalsza granula końca to uszkodzony plik, nie długość.</summary>
    private const long MaxGranules = 30L * 24 * 3600 * OpusSampleRate;

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

    // Miejsce w strumieniu w granulach (próbkach 48 kHz razem z pre-skip), potrzebne do obcięcia
    // dopełnienia ostatniego pakietu. Granula strony mówi, gdzie strona się kończy, więc początek
    // strony to granula poprzedniej; po przewinięciu jest nieznany (−1) do pierwszej zmiany strony.
    private long _pageGranule;
    private long _pageStart;
    private long _decodedInPage;

    /// <summary>
    /// Pakiety odczytane po przewinięciu na zapas, do końca strony, na której czytnik stanął: dopiero
    /// koniec strony mówi, w którym miejscu strumienia stoi pierwszy z nich.
    /// </summary>
    private readonly Queue<(byte[] Raw, long Page)> _ahead = new();

    public OpusDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _head = OpusHead.Read(stream);

        if (OggPacketScan.FindOpusHazard(stream) is { } hazard)
        {
            DecoderFailures.Log($"Opus: {hazard}.");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile);
        }

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

        // Granula końca pochodzi z ostatniej strony pliku; niedorzeczna oznacza długość nieznaną.
        var granules = _reader.GranuleCount - _head.PreSkip;
        TotalFrames = granules is > 0 and <= MaxGranules
            ? (long)Math.Round(granules * (double)outputSampleRate / OpusSampleRate)
            : 0;
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
        while (NextRaw(out var raw, out var page))
        {
            if (page >= 0 && page != _pageGranule)
            {
                _pageStart = _pageGranule;
                _pageGranule = page;
                _decodedInPage = 0;
            }

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

            decoded = TrimEndPadding(decoded);
            if (decoded <= 0) continue;

            var dropped = Math.Min(_skip, decoded);
            _skip -= dropped;

            _packetOffset = dropped;
            _packetFrames = decoded - dropped;
            if (_packetFrames > 0) return true;
        }

        return false;
    }

    /// <summary>Następny niepusty pakiet i granula jego strony: najpierw z zapasu po przewinięciu, potem z czytnika.</summary>
    private bool NextRaw(out byte[] raw, out long page)
    {
        if (_ahead.TryDequeue(out var queued))
        {
            (raw, page) = queued;
            return true;
        }

        while (_reader.HasNextPacket)
        {
            // Czytnik trzyma następny pakiet w zapasie, więc przed odczytem podaje stronę tego,
            // który zaraz odda.
            page = _reader.PageGranulePosition;
            var next = _reader.ReadNextRawPacket();
            if (next is null) break;
            if (next.Length == 0) continue;

            raw = next;
            return true;
        }

        raw = [];
        page = -1;
        return false;
    }

    /// <summary>
    /// Liczba próbek 48 kHz w pakiecie, odczytana z bajtu TOC bez dekodowania (RFC 6716,
    /// rozdział 3.1). W pakiecie wielostrumieniowym pierwszy bajt opisuje wszystkie strumienie.
    /// </summary>
    internal static int PacketSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return 0;

        var toc = packet[0];
        var config = toc >> 3;

        var frame = config switch
        {
            < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => 120 << (config & 3),
        };

        var frames = (toc & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };

        return frame * frames;
    }

    /// <summary>
    /// Ostatni pakiet jest dopełniony do pełnej ramki, a granula ostatniej strony mówi, ile próbek
    /// jest prawdziwych (RFC 7845, rozdział 4.4). Bez obcięcia dopełnienie grałoby na końcu utworu,
    /// także między utworami granymi bez przerwy. Zwraca liczbę próbek pakietu do oddania.
    /// </summary>
    private int TrimEndPadding(int decoded)
    {
        var before = _decodedInPage;
        _decodedInPage += decoded;

        var end = _reader.GranuleCount;
        if (end <= 0 || _pageGranule != end || _pageStart < 0) return decoded;

        var left = end - _pageStart - before;
        return (int)Math.Clamp(left, 0, decoded);
    }

    /// <summary>
    /// Pułapka: czytnik Ogg z Concentus po minięciu ostatniego pakietu nie wraca do pracy
    /// po <c>SeekTo</c>, więc przy przewinięciu powstaje nowy.
    /// </summary>
    protected override void SeekSource(long sourceFrame)
    {
        _packetOffset = 0;
        _packetFrames = 0;
        _exhausted = false;
        _ahead.Clear();

        _reader.Close();
        _stream.Position = 0;
        _reader = CreateReader();

        _single?.ResetState();
        _multi?.ResetState();

        _decodedInPage = 0;

        if (sourceFrame <= 0)
        {
            _skip = _head.PreSkip;
            _pageGranule = 0;
            _pageStart = 0;
            return;
        }

        _skip = 0;
        _pageGranule = -1;
        _pageStart = -1;

        // Pozycja w granulach obejmuje pre-skip. Czytnik odrzuca cel dalszy niż TotalTime;
        // takty liczone całkowicie zaokrąglają w dół, więc obcięty cel nigdy go nie przekracza.
        // Cel i koniec obcięte do rozsądnej długości: z granulą końca wpisaną w uszkodzonym pliku
        // przeliczenie na takty przewijało się, a TotalTime czytnika rzucał wyjątek.
        var end = Math.Clamp(_reader.GranuleCount, 0, MaxGranules);
        var granule = Math.Clamp(sourceFrame + _head.PreSkip, 0, end);
        var target = TimeSpan.FromTicks(granule * TimeSpan.TicksPerSecond / OpusSampleRate);

        try
        {
            _reader.SeekTo(target);
            SkipToTarget(granule);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            // OverflowException: czytnik sam liczy czas całości z granuli końca.
            DecoderFailures.Log($"Concentus nie przewinął pliku Opus: {ex.Message}");
            _exhausted = true;
        }
    }

    /// <summary>
    /// Czytnik staje po przewinięciu przed celem, nawet o całą stronę (w prawdziwych plikach około
    /// sekundy), i nie mówi, gdzie dokładnie. Miejsce pierwszego pakietu wynika z granuli końca
    /// strony i długości pakietów do jej końca, więc pakiety tej strony są czytane na zapas, a próbki
    /// sprzed celu odrzucane po zdekodowaniu. Dekoder dostaje przy tym rozbieg, którego potrzebuje
    /// po wyzerowaniu stanu.
    /// </summary>
    private void SkipToTarget(long targetGranule)
    {
        // Czytnik trzyma w zapasie pakiet odczytany przed przewinięciem, czyli pierwszy pakiet pliku,
        // i oddałby go jako pierwszy: 20 ms z początku utworu w miejscu, do którego przewinięto.
        if (_reader.HasNextPacket) _reader.ReadNextRawPacket();

        long landingPage = -1;
        long samples = 0;

        while (_reader.HasNextPacket)
        {
            var page = _reader.PageGranulePosition;

            // Pakiet następnej strony zostaje w czytniku.
            if (landingPage >= 0 && page >= 0 && page != landingPage) break;

            var raw = _reader.ReadNextRawPacket();
            if (raw is null) break;
            if (raw.Length == 0) continue;

            if (landingPage < 0) landingPage = page;
            _ahead.Enqueue((raw, page));
            samples += PacketSamples(raw);
        }

        if (landingPage < 0) return;

        // Na ostatniej stronie suma długości pakietów obejmuje dopełnienie, więc początek wypada
        // za wcześnie o jego długość, najwyżej o jeden pakiet.
        var start = landingPage - samples;

        if (start <= 0)
        {
            // Pierwsza strona od pierwszego pakietu: jej początek jest znany, więc także obcięcie
            // dopełnienia, gdy jest zarazem ostatnią.
            start = 0;
            _pageGranule = landingPage;
            _pageStart = 0;
        }

        _skip = (int)Math.Clamp(targetGranule - start, 0, int.MaxValue);
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
