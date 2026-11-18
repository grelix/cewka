using System.Buffers.Binary;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace Cewka.Audio.Tests;

/// <summary>
/// Pliki dźwiękowe budowane w pamięci na potrzeby testów, żeby repozytorium nie musiało
/// przechowywać próbek binarnych.
/// </summary>
internal static class TestAudioFiles
{
    public const int OpusRate = 48000;
    public const int OpusFrame = 960;

    /// <summary>
    /// WAV z próbkami float. Przy więcej niż dwóch kanałach albo podanej masce zapisuje
    /// nagłówek WAVE_FORMAT_EXTENSIBLE.
    /// </summary>
    public static byte[] Wav(float[] interleaved, int channels, int sampleRate, uint? channelMask = null)
    {
        var extensible = channels > 2 || channelMask is not null;
        var dataBytes = interleaved.Length * sizeof(float);
        var fmtSize = extensible ? 40 : 16;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write("RIFF"u8);
        w.Write(4 + 8 + fmtSize + 8 + dataBytes);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(fmtSize);
        w.Write((ushort)(extensible ? 0xFFFE : 3));
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * sizeof(float));
        w.Write((ushort)(channels * sizeof(float)));
        w.Write((ushort)32);

        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)32);
            w.Write(channelMask ?? DefaultWaveMask(channels));
            // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            w.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray());
        }

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var sample in interleaved) w.Write(sample);

        return ms.ToArray();
    }

    /// <summary>
    /// AIFF (bez <paramref name="compression"/>) albo AIFF-C z podanym rodzajem zapisu próbek:
    /// <c>NONE</c>, <c>twos</c>, <c>sowt</c>, <c>in24</c>, <c>in32</c>, <c>fl32</c>, <c>FL32</c>, <c>fl64</c>,
    /// <c>FL64</c> albo dowolny inny, wtedy próbki jak w <c>NONE</c>. <paramref name="before"/> trafia
    /// przed opis formatu, <paramref name="after"/> za dane.
    /// </summary>
    public static byte[] Aiff(
        float[] interleaved, int channels, int sampleRate, int bits = 16, string? compression = null,
        byte[]? before = null, byte[]? after = null, bool formatAfterData = false)
    {
        var kind = compression ?? "NONE";
        var encode = AiffSampleWriter(kind, bits);
        var frames = interleaved.Length / channels;

        using var data = new MemoryStream();
        foreach (var sample in interleaved) data.Write(encode(sample));

        var comm = new MemoryStream();
        var number = new byte[4];
        BinaryPrimitives.WriteInt16BigEndian(number, (short)channels);
        comm.Write(number, 0, 2);
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)frames);
        comm.Write(number, 0, 4);
        BinaryPrimitives.WriteInt16BigEndian(number, (short)bits);
        comm.Write(number, 0, 2);
        comm.Write(Extended(sampleRate));
        if (compression is not null)
        {
            comm.Write(Encoding.ASCII.GetBytes(kind));
            comm.Write(new byte[2]);
        }

        var ssnd = new MemoryStream();
        ssnd.Write(new byte[8]);
        data.Position = 0;
        data.CopyTo(ssnd);

        var chunks = new List<byte[]>();
        if (before is not null) chunks.Add(before);
        if (compression is not null) chunks.Add(AiffChunk("FVER", [0xA2, 0x80, 0x51, 0x40]));
        if (!formatAfterData) chunks.Add(AiffChunk("COMM", comm.ToArray()));
        chunks.Add(AiffChunk("SSND", ssnd.ToArray()));
        if (formatAfterData) chunks.Add(AiffChunk("COMM", comm.ToArray()));
        if (after is not null) chunks.Add(after);

        var body = Concat(Encoding.ASCII.GetBytes(compression is null ? "AIFF" : "AIFC"), chunks.SelectMany(c => c).ToArray());
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
        return Concat("FORM"u8.ToArray(), size, body);
    }

    /// <summary>Fragment AIFF: identyfikator, długość bez dopełnienia, treść dopełniona do parzystej długości.</summary>
    public static byte[] AiffChunk(string id, byte[] content)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)content.Length);
        return Concat(Encoding.ASCII.GetBytes(id), size, content, new byte[content.Length % 2]);
    }

    /// <summary>Zapis jednej próbki dla rodzaju kompresji AIFF-C.</summary>
    private static Func<float, byte[]> AiffSampleWriter(string kind, int bits)
    {
        switch (kind)
        {
            case "fl32" or "FL32":
                return s => { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, s); return b; };
            case "fl64" or "FL64":
                return s => { var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, s); return b; };
        }

        var width = kind switch { "in24" => 3, "in32" => 4, _ => (bits + 7) / 8 };
        var significant = kind switch { "in24" => 24, "in32" => 32, _ => bits };
        var little = kind == "sowt";

        return s =>
        {
            // Skala 2^(n−1), ta sama co przy odczycie, więc błąd to wyłącznie kwantyzacja.
            var full = 1L << (significant - 1);
            var value = Math.Clamp((long)Math.Round(s * (double)full), -full, full - 1) << (width * 8 - significant);
            var b = new byte[width];
            for (var i = 0; i < width; i++)
                b[little ? i : width - 1 - i] = (byte)(value >> (8 * i));
            return b;
        };
    }

    /// <summary>Częstotliwość jako 80-bitowa liczba rozszerzona, jak w nagłówku AIFF.</summary>
    public static byte[] Extended(double value)
    {
        var bytes = new byte[10];
        if (value <= 0) return bytes;

        var exponent = (int)Math.Floor(Math.Log2(value));
        var mantissa = (ulong)Math.Round(Math.ScaleB(value, 63 - exponent));
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)(exponent + 16383));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), mantissa);
        return bytes;
    }

    /// <summary>WAV z nieznanym kodekiem: kontener poprawny, ale nikt nie potrafi go zdekodować.</summary>
    public static byte[] WavWithUnknownCodec()
    {
        var bytes = Wav(new float[4800 * 2], 2, 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 0x7A7A);
        return bytes;
    }

    private static uint DefaultWaveMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x70F,
        8 => 0x63F,
        _ => 0,
    };

    /// <summary>Ton sinusoidalny tylko w jednym kanale; pozostałe kanały milczą.</summary>
    public static float[] ToneInChannel(int channels, int channel, int frames, int sampleRate, double frequency = 1000, float amplitude = 0.5f)
    {
        var data = new float[frames * channels];
        for (var i = 0; i < frames; i++)
            data[i * channels + channel] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        return data;
    }

    /// <summary>Parametry nagłówka OpusHead, które testy chcą ustawiać.</summary>
    public sealed record OpusOptions
    {
        public int Channels { get; init; } = 2;
        public int PreSkip { get; init; } = 312;
        public short OutputGainQ8 { get; init; }
        public int Frames { get; init; } = 50;

        /// <summary>O ile próbek ostatnia strona kończy się przed końcem ostatniego pakietu (dopełnienie kodera).</summary>
        public int EndTrim { get; init; }

        /// <summary>Pakietów na stronę Ogg. Prawdziwe kodery mieszczą ich na stronie kilkadziesiąt.</summary>
        public int PacketsPerPage { get; init; } = 1;
        public float Amplitude { get; init; } = 0.3f;
        public double Frequency { get; init; } = 440;

        /// <summary>Kanał, w którym gra ton; pozostałe milczą. Null oznacza ton we wszystkich.</summary>
        public int? ToneChannel { get; init; }

        /// <summary>Rodzina mapowania zapisana w nagłówku zamiast tej, której użył koder.</summary>
        public byte? MappingFamilyOverride { get; init; }

        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Ogg Opus złożony od podstaw: nagłówki, pakiety z kodera Concentus, po jednym pakiecie
    /// na stronę. Powyżej dwóch kanałów używa rodziny mapowania 1 i kodera wielostrumieniowego.
    /// </summary>
    public static byte[] Opus(OpusOptions options)
    {
        var channels = options.Channels;
        var family = channels > 2 ? 1 : 0;

        IOpusEncoder? single = null;
        IOpusMultiStreamEncoder? multi = null;
        var streams = 1;
        var coupled = channels == 2 ? 1 : 0;
        var mapping = new byte[channels];

        if (family == 0)
        {
            single = OpusCodecFactory.CreateEncoder(OpusRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            single.Bitrate = 64000 * channels;
            for (var c = 0; c < channels; c++) mapping[c] = (byte)c;
        }
        else
        {
            multi = OpusCodecFactory.CreateMultiStreamEncoder(
                OpusRate, channels, 1, out streams, out coupled, mapping, OpusApplication.OPUS_APPLICATION_AUDIO);
            multi.Bitrate = 64000 * channels;
        }

        var head = new List<byte>();
        head.AddRange("OpusHead"u8.ToArray());
        head.Add(1);
        head.Add((byte)channels);
        head.AddRange(BitConverter.GetBytes((ushort)options.PreSkip));
        head.AddRange(BitConverter.GetBytes((uint)OpusRate));
        head.AddRange(BitConverter.GetBytes(options.OutputGainQ8));
        head.Add(options.MappingFamilyOverride ?? (byte)family);
        if (family != 0)
        {
            head.Add((byte)streams);
            head.Add((byte)coupled);
            head.AddRange(mapping);
        }

        var tags = new List<byte>();
        tags.AddRange("OpusTags"u8.ToArray());
        var vendor = Encoding.UTF8.GetBytes("test");
        tags.AddRange(BitConverter.GetBytes(vendor.Length));
        tags.AddRange(vendor);
        tags.AddRange(BitConverter.GetBytes(options.Tags.Count));
        foreach (var (key, value) in options.Tags)
        {
            var entry = Encoding.UTF8.GetBytes($"{key}={value}");
            tags.AddRange(BitConverter.GetBytes(entry.Length));
            tags.AddRange(entry);
        }

        var ogg = new OggWriter();
        ogg.WritePage(head.ToArray(), 0, bos: true, eos: false);
        ogg.WritePage(tags.ToArray(), 0, bos: false, eos: false);

        var pcm = new float[OpusFrame * channels];
        var packet = new byte[4000];
        var page = new List<byte[]>();
        long granule = 0;
        long sample = 0;

        for (var f = 0; f < options.Frames; f++)
        {
            for (var i = 0; i < OpusFrame; i++, sample++)
            {
                var value = options.Amplitude * (float)Math.Sin(2 * Math.PI * options.Frequency * sample / OpusRate);
                for (var c = 0; c < channels; c++)
                    pcm[i * channels + c] = options.ToneChannel is null || options.ToneChannel == c ? value : 0f;
            }

            var length = single is not null
                ? single.Encode(pcm, OpusFrame, packet, packet.Length)
                : multi!.EncodeMultistream(pcm, OpusFrame, packet, packet.Length);

            granule += OpusFrame;
            var last = f == options.Frames - 1;
            page.Add(packet.AsSpan(0, length).ToArray());

            if (page.Count < options.PacketsPerPage && !last) continue;

            ogg.WritePage(page, last ? granule - options.EndTrim : granule, bos: false, eos: last);
            page.Clear();
        }

        return ogg.ToArray();
    }

    /// <summary>Minimalny zapis stron Ogg: całe pakiety na stronie, suma kontrolna wg specyfikacji.</summary>
    public sealed class OggWriter
    {
        private readonly MemoryStream _output = new();
        private uint _sequence;

        public void WritePage(byte[] packet, long granule, bool bos, bool eos) => WritePage([packet], granule, bos, eos);

        /// <summary>Strona z kilkoma całymi pakietami; granula opisuje koniec ostatniego.</summary>
        public void WritePage(IReadOnlyList<byte[]> packets, long granule, bool bos, bool eos)
        {
            var segments = new List<byte>();
            foreach (var part in packets)
            {
                var remaining = part.Length;
                while (remaining >= 255) { segments.Add(255); remaining -= 255; }
                segments.Add((byte)remaining);
            }

            if (segments.Count > 255) throw new ArgumentException("Za dużo pakietów na jedną stronę Ogg.");

            var packet = packets.SelectMany(part => part).ToArray();
            var page = new byte[27 + segments.Count + packet.Length];
            "OggS"u8.CopyTo(page);
            page[4] = 0;
            page[5] = (byte)((bos ? 0x02 : 0) | (eos ? 0x04 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), 0x43657721);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
            page[26] = (byte)segments.Count;
            segments.ToArray().CopyTo(page, 27);
            packet.CopyTo(page, 27 + segments.Count);

            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
            _output.Write(page);
        }

        public byte[] ToArray() => _output.ToArray();

        /// <summary>
        /// Przelicza sumy kontrolne stron po zmianie ich treści. Czytniki odrzucają stronę ze złą
        /// sumą, więc bez tego uszkodzone dane nie docierałyby do dekodera.
        /// </summary>
        public static void FixChecksums(Span<byte> data)
        {
            var at = 0;
            while (at + 27 <= data.Length)
            {
                if (!data.Slice(at, 4).SequenceEqual("OggS"u8)) { at++; continue; }

                var segments = data[at + 26];
                if (at + 27 + segments > data.Length) break;

                var body = 0;
                for (var i = 0; i < segments; i++) body += data[at + 27 + i];

                var length = Math.Min(27 + segments + body, data.Length - at);
                var page = data.Slice(at, length);
                page.Slice(22, 4).Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(22), Crc(page));

                at += length;
            }
        }

        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var r = i << 24;
                for (var b = 0; b < 8; b++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                table[i] = r;
            }
            return table;
        }

        private static uint Crc(ReadOnlySpan<byte> data)
        {
            uint crc = 0;
            foreach (var b in data) crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ b];
            return crc;
        }
    }

    /// <summary>Pierwszy pakiet Ogg z podaną zawartością, wystarczający do rozpoznania kodeka.</summary>
    public static byte[] OggFirstPacket(byte[] packet)
    {
        var ogg = new OggWriter();
        ogg.WritePage(packet, 0, bos: true, eos: false);
        ogg.WritePage(new byte[64], 0, bos: false, eos: true);
        return ogg.ToArray();
    }

    /// <summary>
    /// Początek pliku Ogg z kilkoma strumieniami logicznymi: strona BOS z pierwszym pakietem
    /// każdego z nich, a po nich strona danych.
    /// </summary>
    public static byte[] OggBeginningPages(params byte[][] firstPackets)
    {
        var ogg = new OggWriter();
        foreach (var packet in firstPackets) ogg.WritePage(packet, 0, bos: true, eos: false);
        ogg.WritePage(new byte[64], 0, bos: false, eos: true);
        return ogg.ToArray();
    }

    /// <summary>Znacznik ID3v2 o podanej długości treści, bez ramek.</summary>
    public static byte[] Id3Tag(int bodySize, bool footer = false)
    {
        var tag = new byte[10 + bodySize + (footer ? 10 : 0)];
        tag[0] = (byte)'I'; tag[1] = (byte)'D'; tag[2] = (byte)'3';
        tag[3] = 4;
        tag[5] = (byte)(footer ? 0x10 : 0);
        tag[6] = (byte)((bodySize >> 21) & 0x7F);
        tag[7] = (byte)((bodySize >> 14) & 0x7F);
        tag[8] = (byte)((bodySize >> 7) & 0x7F);
        tag[9] = (byte)(bodySize & 0x7F);
        if (footer)
        {
            var f = 10 + bodySize;
            tag[f] = (byte)'3'; tag[f + 1] = (byte)'D'; tag[f + 2] = (byte)'I';
        }
        return tag;
    }

    /// <summary>Nagłówek ramki ADTS (AAC-LC, 44,1 kHz, stereo) z zerowym ładunkiem.</summary>
    public static byte[] AdtsFrames(byte secondByte = 0xF1, int count = 4)
    {
        const int frameLength = 64;
        var output = new byte[frameLength * count];
        for (var i = 0; i < count; i++)
        {
            var f = output.AsSpan(i * frameLength, frameLength);
            f[0] = 0xFF;
            f[1] = secondByte;
            f[2] = 0x50; // profil LC, indeks częstotliwości 4 (44,1 kHz)
            f[3] = (byte)(0x80 | ((frameLength >> 11) & 0x03));
            f[4] = (byte)((frameLength >> 3) & 0xFF);
            f[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
            f[6] = 0xFC;
        }
        return output;
    }

    /// <summary>Nagłówek ramki MPEG-1 Layer III, 128 kbps, 44,1 kHz, z zerowym ładunkiem.</summary>
    public static byte[] Mp3Frames(int count = 4)
    {
        const int frameLength = 417;
        var output = new byte[frameLength * count];
        for (var i = 0; i < count; i++)
        {
            var f = output.AsSpan(i * frameLength, frameLength);
            f[0] = 0xFF;
            f[1] = 0xFB;
            f[2] = 0x90;
            f[3] = 0x64;
        }
        return output;
    }

    /// <summary>
    /// Ramki MPEG-1 Layer III o poprawnych danych bocznych i szumie w danych głównych: dekoder
    /// wydaje niezerowe próbki bez kodera i bez próbek binarnych. Tylko 44,1 kHz stereo oraz 48 kHz.
    /// </summary>
    public static byte[] Mp3NoiseFrames(int count = 4, int seed = 1, int sampleRate = 44100, int channels = 2, int kilobits = 128)
    {
        var (rateByte, frameLength) = (sampleRate, kilobits) switch
        {
            (44100, 128) when channels == 2 => ((byte)0x90, 417),
            (48000, 128) => ((byte)0x94, 384),
            (48000, 32) => ((byte)0x14, 96),
            (48000, 48) => ((byte)0x34, 144),
            (48000, 64) => ((byte)0x54, 192),
            (48000, 80) => ((byte)0x64, 240),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate)),
        };

        // Dane główne muszą się zmieścić w ramce, inaczej dekoder odrzuci ją razem ze stanem.
        var mainDataBits = Math.Min(600, (frameLength - 36) * 8 / 4);

        var random = new Random(seed);
        var output = new byte[frameLength * count];

        for (var i = 0; i < count; i++)
        {
            var f = output.AsSpan(i * frameLength, frameLength);
            random.NextBytes(f);

            f[0] = 0xFF;
            f[1] = 0xFB;
            f[2] = rateByte;
            f[3] = channels == 2 ? (byte)0x64 : (byte)0xE4;
            WriteNoiseSideInfo(f[4..], channels, mainDataBits);
        }

        return output;
    }

    private static void WriteNoiseSideInfo(Span<byte> target, int channels, int mainDataBits = 600)
    {
        target[..(channels == 2 ? 32 : 17)].Clear();

        var bit = 0;
        PutBits(target, ref bit, 0, 9);                      // main_data_begin
        PutBits(target, ref bit, 0, channels == 2 ? 3 : 5);  // bity prywatne
        PutBits(target, ref bit, 0, 4 * channels);           // scfsi

        for (var granuleChannel = 0; granuleChannel < 2 * channels; granuleChannel++)
        {
            PutBits(target, ref bit, mainDataBits, 12);  // part2_3_length
            PutBits(target, ref bit, 120, 9);   // big_values
            PutBits(target, ref bit, 170, 8);   // global_gain
            PutBits(target, ref bit, 0, 4);     // scalefac_compress
            PutBits(target, ref bit, 0, 1);     // window_switching
            PutBits(target, ref bit, 1, 5);     // table_select
            PutBits(target, ref bit, 2, 5);
            PutBits(target, ref bit, 3, 5);
            PutBits(target, ref bit, 3, 4);     // region0_count
            PutBits(target, ref bit, 2, 3);     // region1_count
            PutBits(target, ref bit, 0, 3);     // preflag, scalefac_scale, count1table
        }
    }

    private static void PutBits(Span<byte> target, ref int bit, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--, bit++)
        {
            if (((value >> i) & 1) != 0) target[bit / 8] |= (byte)(0x80 >> (bit % 8));
        }
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// Krótki, poprawny plik danego rodzaju: WAV i Opus składane w teście, pozostałe to tony
    /// z prawdziwych koderów leżące w <c>Dane</c>.
    /// </summary>
    public static byte[] Seed(string kind) => kind switch
    {
        "wav" => Wav(ToneInChannel(2, 0, 22050, 44100), 2, 44100),
        "opus" => Opus(new OpusOptions { Frames = 25, PreSkip = 312, PacketsPerPage = 5 }),
        "aiff" => Aiff(ToneInChannel(2, 0, 22050, 44100), 2, 44100),
        _ => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", "ton-440-stereo-44100." + kind)),
    };

    public const string TaggedTitle = "Tytuł próbny";
    public const int TaggedPictureBytes = 600;

    /// <summary>Ten sam plik ze znacznikami i okładką, zapisanymi przez bibliotekę znaczników.</summary>
    public static byte[] Tagged(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cewka-ziarno-{Guid.NewGuid():N}.{kind}");
        File.WriteAllBytes(path, Seed(kind));

        try
        {
            var track = new ATL.Track(path)
            {
                Title = TaggedTitle,
                Artist = "Wykonawca",
                Album = "Album",
                TrackNumber = 3,
            };
            track.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = "-6.48 dB";

            var picture = new byte[TaggedPictureBytes];
            new Random(7).NextBytes(picture);

            // Nagłówek JPEG: biblioteka rozpoznaje typ obrazu po pierwszych bajtach.
            picture[0] = 0xFF;
            picture[1] = 0xD8;
            picture[2] = 0xFF;
            picture[3] = 0xE0;
            track.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(picture, ATL.PictureInfo.PIC_TYPE.Front));

            if (!track.Save()) throw new InvalidOperationException($"Nie zapisano znaczników w pliku {kind}.");

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Plik z katalogu <c>Dane</c>.</summary>
    public static byte[] DataFile(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", name));

    /// <summary>
    /// Ton 440 Hz stereo 44,1 kHz w zakresie ±1: lewy kanał o amplitudzie 0,5, prawy 0,25
    /// z przesunięciem fazy. Z tych próbek powstały pliki WavPack w <c>Dane</c>: 16 bit pół
    /// sekundy, 24 bit i float ćwierć sekundy.
    /// </summary>
    public static double[] WavPackTone(int frames)
    {
        var data = new double[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var phase = 2 * Math.PI * 440 * i / 44100;
            data[2 * i] = 0.5 * Math.Sin(phase);
            data[2 * i + 1] = 0.25 * Math.Sin(phase + 1.0);
        }

        return data;
    }

    /// <summary>Próbki ±1 zaokrąglone do liczb całkowitych o podanej głębi.</summary>
    public static int[] Quantise(double[] data, int bits)
    {
        var full = (1 << (bits - 1)) - 1;
        return data.Select(x => (int)Math.Round(x * full)).ToArray();
    }

    /// <summary>WAV z próbkami całkowitymi 16 albo 24 bity; z maską zapisuje nagłówek WAVE_FORMAT_EXTENSIBLE.</summary>
    public static byte[] PcmWav(int[] interleaved, int channels, int sampleRate, int bits, uint? channelMask = null)
    {
        var bytesPerSample = bits / 8;
        var dataBytes = interleaved.Length * bytesPerSample;
        var extensible = channelMask is not null;
        var fmtSize = extensible ? 40 : 16;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write("RIFF"u8);
        w.Write(4 + 8 + fmtSize + 8 + dataBytes);
        w.Write("WAVE"u8);

        w.Write("fmt "u8);
        w.Write(fmtSize);
        w.Write((ushort)(extensible ? 0xFFFE : 1));
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bytesPerSample);
        w.Write((ushort)(channels * bytesPerSample));
        w.Write((ushort)bits);

        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(channelMask!.Value);
            // KSDATAFORMAT_SUBTYPE_PCM
            w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        }

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var sample in interleaved)
            for (var b = 0; b < bytesPerSample; b++) w.Write((byte)(sample >> (8 * b)));

        return ms.ToArray();
    }
}
