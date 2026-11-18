using NVorbis;
using IPacket = NVorbis.Contracts.IPacket;
using IPacketProvider = NVorbis.Contracts.IPacketProvider;
using GetPacketGranuleCount = NVorbis.Contracts.GetPacketGranuleCount;
using NVorbis.Ogg;

namespace Cewka.Audio.Decoding;

/// <summary>Ogg Vorbis, decoded by NVorbis.</summary>
internal sealed class VorbisDecoder : ManagedDecoderBase
{
    private readonly ContainerReader _container;
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    private StreamDecoder? _decoder;

    public VorbisDecoder(Stream stream, bool ownsStream, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        try
        {
            // NVorbis closes the stream itself only when told to; ownership stays here.
            _container = new ContainerReader(stream, closeOnDispose: false) { NewStreamCallback = AcceptStream };

            if (!_container.TryInit() || _decoder is null)
                throw new InvalidDataException("W pliku nie ma strumienia Vorbis.");
        }
        catch (Exception ex)
        {
            _decoder?.Dispose();
            _container?.Dispose();

            DecoderFailures.Log($"NVorbis nie otworzył pliku: {ex.Message}");
            throw DecoderFailures.Create(DecoderFailure.DamagedFile, ex);
        }

        Configure(_decoder.SampleRate, _decoder.Channels, outputSampleRate, outputChannels, meter,
            ChannelLayouts.Vorbis(_decoder.Channels));

        TotalFrames = _decoder.TotalSamples <= 0
            ? 0
            : (long)Math.Round(_decoder.TotalSamples * (double)outputSampleRate / _decoder.SampleRate);
    }

    /// <summary>
    /// Gra pierwszy strumień w pliku. Nagłówków następnych nikt nie czyta: nie byłyby odtwarzane,
    /// a każdy z nich to dane z pliku, które biblioteka rozbiera bez kontroli długości.
    /// </summary>
    private bool AcceptStream(IPacketProvider packets)
    {
        if (_decoder is not null) return false;

        _decoder = new StreamDecoder(new CheckedPackets(packets)) { ClipSamples = true };
        return true;
    }

    public override bool CanSeek => _stream.CanSeek;

    protected override int ReadSource(Span<float> destination, int frames)
    {
        // NVorbis counts in individual samples, not in frames.
        var samples = _decoder!.Read(destination, 0, frames * SourceChannels);
        return samples / SourceChannels;
    }

    protected override void SeekSource(long sourceFrame)
    {
        var total = _decoder!.TotalSamples;
        var target = Math.Min(sourceFrame, total > 0 ? total - 1 : sourceFrame);
        _decoder.SeekTo(Math.Max(0, target), SeekOrigin.Begin);
    }

    protected override void DisposeCore()
    {
        _decoder?.Dispose();
        _container.Dispose();
        if (_ownsStream) _stream.Dispose();
    }

    /// <summary>
    /// Nagłówki komentarzy i konfiguracji podają liczby, według których NVorbis przydziela pamięć,
    /// zanim sprawdzi, czy pakiet w ogóle je mieści: liczbę komentarzy i długość każdego z nich,
    /// liczbę wpisów i wymiarów każdej księgi kodowej. Plik kilku kilobajtów żądał tak kilku
    /// gigabajtów, a księga urwana w połowie zostawiała bibliotekę w pętli bez końca. Tutaj oba
    /// pakiety są przechodzone raz na sucho, tą samą drogą co w bibliotece: liczby wychodzące poza
    /// koniec pakietu albo poza rozsądek kończą otwieranie.
    /// </summary>
    private sealed class CheckedPackets(IPacketProvider inner) : IPacketProvider
    {
        /// <summary>Wpis dłuższy niż to nie zmieściłby się w liczniku bitów pakietu.</summary>
        private const uint MaxEntryBytes = int.MaxValue / 8;

        // Księgi z prawdziwych koderów mają do kilku tysięcy wpisów i tablice do kilkudziesięciu
        // tysięcy wartości; granice leżą o dwa rzędy wielkości dalej.
        private const int MaxBookEntries = 1 << 17;
        private const long MaxBookValues = 1 << 22;
        private const long MaxTotalEntries = 1 << 20;
        private const long MaxTotalValues = 1 << 24;

        /// <summary>Tyle brakujących wpisów na końcu nagłówka komentarzy to jeszcze błąd programu tagującego.</summary>
        private const uint MaxMissingComments = 4096;

        private const ulong VorbisName = 0x736962726F76;
        private const ulong BookSync = 0x564342;

        private bool _headersChecked;

        public bool CanSeek => inner.CanSeek;
        public int StreamSerial => inner.StreamSerial;

        public IPacket GetNextPacket() => Checked(inner.GetNextPacket());
        public IPacket PeekNextPacket() => Checked(inner.PeekNextPacket());

        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) =>
            inner.SeekTo(granulePos, preRoll, getPacketGranuleCount);

        public long GetGranuleCount() => inner.GetGranuleCount();

        private IPacket Checked(IPacket packet)
        {
            if (_headersChecked || packet is null) return packet!;

            try
            {
                // Typ pakietu i napis „vorbis”, czytane od najmłodszego bitu.
                var type = packet.ReadBits(8);
                if (packet.ReadBits(48) != VorbisName) return packet;

                if (type == 3)
                {
                    CheckComments(packet);
                }
                else if (type == 5)
                {
                    // Ostatni nagłówek; dalej idą już tylko pakiety dźwięku.
                    _headersChecked = true;
                    CheckBooks(packet);
                }
            }
            finally
            {
                packet.Reset();
            }

            return packet;
        }

        private static void CheckComments(IPacket packet)
        {
            if (!Skip(packet, "nazwa kodera")) return;

            var count = (uint)packet.ReadBits(32);

            for (uint i = 0; i < count; i++)
            {
                if (Skip(packet, "komentarz")) continue;

                // Pakiet skończył się przed zapowiedzianą liczbą wpisów. Biblioteka czyta wtedy
                // puste napisy, więc plik z licznikiem zawyżonym o kilka gra; odpada dopiero taki,
                // który zapowiada ich tyle, że sama tablica na nie jest pułapką.
                if (count - i > MaxMissingComments)
                    throw new InvalidDataException("Nagłówek komentarzy Vorbis: liczba wpisów wychodzi poza pakiet.");

                return;
            }
        }

        /// <summary>
        /// Księgi kodowe: znacznik, liczba wymiarów, liczba wpisów, długości słów i tablica wartości.
        /// Gdy znacznik się nie zgadza, biblioteka sama odrzuca nagłówek, więc tu nie ma czego pilnować.
        /// </summary>
        private static void CheckBooks(IPacket packet)
        {
            var books = (int)packet.ReadBits(8) + 1;
            long totalEntries = 0;
            long totalValues = 0;

            for (var book = 0; book < books; book++)
            {
                if (packet.ReadBits(24) != BookSync) return;

                var dimensions = (int)packet.ReadBits(16);
                var entries = (int)packet.ReadBits(24);

                if (entries > MaxBookEntries || (totalEntries += entries) > MaxTotalEntries)
                    throw new InvalidDataException("Nagłówek konfiguracji Vorbis: księga kodowa ma za dużo wpisów.");

                if (packet.ReadBits(1) == 1)
                {
                    // Długości rosnące: ile wpisów ma każdą kolejną. Zero wpisów nie robi postępu,
                    // więc pętlę kończy tutaj koniec pakietu; w bibliotece nie kończyło jej nic.
                    packet.SkipBits(5);

                    for (var done = 0; done < entries;)
                    {
                        done += (int)packet.ReadBits(BitsFor(entries - done));
                        EnsureWithin(packet);
                    }
                }
                else
                {
                    var sparse = packet.ReadBits(1) == 1;

                    for (var entry = 0; entry < entries; entry++)
                        if (!sparse || packet.ReadBits(1) == 1) packet.SkipBits(5);
                }

                EnsureWithin(packet);

                var map = (int)packet.ReadBits(4);
                if (map == 0) continue;

                var values = (long)entries * dimensions;

                if (dimensions == 0)
                    throw new InvalidDataException("Nagłówek konfiguracji Vorbis: tablica księgi kodowej bez wymiarów.");

                if (values > MaxBookValues || (totalValues += values) > MaxTotalValues)
                    throw new InvalidDataException("Nagłówek konfiguracji Vorbis: tablica księgi kodowej jest za duża.");

                packet.SkipBits(64);
                var bits = (int)packet.ReadBits(4) + 1;
                packet.SkipBits(1);

                var stored = map == 1 ? LatticeValues(entries, dimensions) : values;
                packet.SkipBits((int)(stored * bits));

                EnsureWithin(packet);
            }
        }

        /// <summary>Liczba bitów potrzebna na zapis liczby, tak jak liczy ją biblioteka.</summary>
        private static int BitsFor(int value)
        {
            var bits = 0;
            for (; value > 0; value >>= 1) bits++;
            return bits;
        }

        /// <summary>Liczba wartości tablicy kratowej: największa liczba, której potęga mieści się we wpisach.</summary>
        private static long LatticeValues(int entries, int dimensions)
        {
            var values = (int)Math.Floor(Math.Exp(Math.Log(entries) / dimensions));
            if (Math.Floor(Math.Pow(values + 1, dimensions)) <= entries) values++;
            return Math.Max(0, values);
        }

        private static void EnsureWithin(IPacket packet)
        {
            if (packet.IsShort)
                throw new InvalidDataException("Nagłówek konfiguracji Vorbis: księga kodowa wychodzi poza pakiet.");
        }

        /// <summary>
        /// Przechodzi przez napis poprzedzony długością. Fałsz, gdy pakiet skończył się już na
        /// samej długości: biblioteka czyta wtedy zero i pusty napis. Napis, który się zaczyna
        /// i nie mieści w pakiecie, odrzuca i ona.
        /// </summary>
        private static bool Skip(IPacket packet, string what)
        {
            var length = (uint)packet.ReadBits(32);

            if (packet.IsShort && length == 0) return false;

            if (length <= MaxEntryBytes)
            {
                packet.SkipBits((int)length * 8);
                if (!packet.IsShort) return true;
            }

            throw new InvalidDataException($"Nagłówek komentarzy Vorbis: {what} wychodzi poza pakiet.");
        }
    }
}
