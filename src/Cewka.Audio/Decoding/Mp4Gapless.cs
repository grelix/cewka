using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cewka.Audio.Decoding;

/// <summary>Skąd pochodzą opóźnienie i długość nagrania w pliku MP4.</summary>
internal enum Mp4GaplessSource
{
    /// <summary>Tylko suma czasów próbek: koniec znany, opóźnienie kodera nie.</summary>
    SampleTable,

    /// <summary>Lista edycji ścieżki (<c>elst</c>), tak zapisują m.in. ffmpeg i afconvert.</summary>
    EditList,

    /// <summary>Znacznik iTunes <c>iTunSMPB</c>.</summary>
    ITunSmpb,
}

/// <summary>
/// Część pliku MP4, która ma zagrać: od <see cref="Delay"/> przez <see cref="Length"/> ramek
/// w jednostkach <see cref="Timescale"/> ścieżki. Koder AAC poprzedza nagranie próbkami
/// rozbiegowymi i dopełnia ostatnią ramkę; bez przycięcia obie wstawki grają jako cisza
/// między utworami albumu.
/// </summary>
internal readonly record struct Mp4Gapless(int Timescale, long Delay, long Length, Mp4GaplessSource Source)
{
    public double StartSeconds => Delay / (double)Timescale;
    public double EndSeconds => (Delay + Length) / (double)Timescale;
    public double LengthSeconds => Length / (double)Timescale;
}

/// <summary>
/// Czytnik atomów MP4 ograniczony do tego, czego wymaga przycięcie: skala czasu i tablica
/// czasów pierwszej ścieżki dźwiękowej, jej lista edycji i znacznik <c>iTunSMPB</c>.
/// Plik jest niezaufany: każdy rozmiar sprawdzany jest względem rodzica, głębokość i liczba
/// atomów mają granice, a tablice czytane są porcjami, nie według liczby z pliku.
/// </summary>
internal static class Mp4GaplessReader
{
    private const int MaxDepth = 12;
    private const int MaxAtoms = 20000;
    private const int MaxTimescale = 1_000_000;

    /// <summary>Rozbieg dłuższy niż minuta nie jest rozbiegiem kodera, tylko uszkodzeniem.</summary>
    private const long MaxDelaySeconds = 60;

    /// <summary>Trzydzieści dni dźwięku; dalsza suma czasów to uszkodzony plik.</summary>
    private const long MaxDurationSeconds = 30L * 24 * 3600;

    private const int MaxSmpbBytes = 1024;
    private const int SttsChunkEntries = 4096;

    /// <summary>Null, gdy plik nie jest MP4 albo nie ma ścieżki dźwiękowej z tablicą czasów.</summary>
    public static Mp4Gapless? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        return Read(stream);
    }

    /// <exception cref="InvalidDataException">Struktura atomów jest sprzeczna albo wykracza poza granice.</exception>
    public static Mp4Gapless? Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 16) return null;

        var state = new State(stream);
        stream.Position = 0;

        // Bez ftyp na początku to nie jest MP4: dekoder systemu dostaje plik bez przycinania.
        Span<byte> head = stackalloc byte[8];
        stream.ReadExactly(head);
        if (!head[4..8].SequenceEqual("ftyp"u8)) return null;

        state.Walk(0, stream.Length, 0, Level.File);
        return state.Result();
    }

    private enum Level { File, Movie, Track, Media, MediaInfo, SampleTable, Edits, UserData, Meta, Items, Freeform }

    private sealed class State(Stream stream)
    {
        private int _atoms;
        private bool _movieDone;

        private uint _movieTimescale;
        private string? _smpb;

        private bool _trackIsSound;
        private uint _trackTimescale;
        private long _trackSamples = -1;
        private (long Duration, long MediaTime)? _trackEdit;
        private bool _trackEditUsable = true;

        private bool _haveSound;
        private uint _soundTimescale;
        private long _soundSamples = -1;
        private (long Duration, long MediaTime)? _soundEdit;

        private string? _freeformMean;
        private string? _freeformName;
        private string? _freeformData;

        public void Walk(long start, long end, int depth, Level level)
        {
            if (depth > MaxDepth) throw Damaged("za głębokie zagnieżdżenie atomów");

            Span<byte> header = stackalloc byte[16];
            var position = start;
            while (end - position >= 8 && !_movieDone)
            {
                if (++_atoms > MaxAtoms) throw Damaged("za dużo atomów");

                stream.Position = position;
                stream.ReadExactly(header[..8]);

                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header[4..8]);
                var headerSize = 8;

                if (size == 1)
                {
                    if (end - position < 16) throw Damaged("niepełny nagłówek atomu 64-bitowego");
                    stream.ReadExactly(header[8..16]);
                    var large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                    if (large > long.MaxValue) throw Damaged("rozmiar atomu poza zakresem");
                    size = (long)large;
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    // Zero oznacza „do końca pliku” i ma sens tylko na najwyższym poziomie.
                    if (level != Level.File) throw Damaged("atom bez rozmiaru wewnątrz innego");
                    size = end - position;
                }

                if (size < headerSize || size > end - position)
                    throw Damaged($"atom {Printable(type)} wychodzi poza rodzica");

                Visit(type, position + headerSize, position + size, depth, level);
                position += size;
            }
        }

        private void Visit(string type, long body, long end, int depth, Level level)
        {
            switch (level, type)
            {
                case (Level.File, "moov"):
                    Walk(body, end, depth + 1, Level.Movie);

                    // Dalej stoją już tylko dane albo fragmenty, których przycinanie nie dotyczy.
                    _movieDone = true;
                    break;

                case (Level.Movie, "mvhd"): _movieTimescale = ReadTimescale(body, end); break;
                case (Level.Movie, "trak"): Track(body, end, depth); break;
                case (Level.Movie, "udta"): Walk(body, end, depth + 1, Level.UserData); break;

                case (Level.Track, "edts"): Walk(body, end, depth + 1, Level.Edits); break;
                case (Level.Track, "mdia"): Walk(body, end, depth + 1, Level.Media); break;
                case (Level.Edits, "elst"): ReadEditList(body, end); break;

                case (Level.Media, "mdhd"): _trackTimescale = ReadTimescale(body, end); break;
                case (Level.Media, "hdlr"): _trackIsSound = ReadHandler(body, end) == "soun"; break;
                case (Level.Media, "minf"): Walk(body, end, depth + 1, Level.MediaInfo); break;
                case (Level.MediaInfo, "stbl"): Walk(body, end, depth + 1, Level.SampleTable); break;
                case (Level.SampleTable, "stts"): _trackSamples = ReadSampleDurations(body, end); break;

                case (Level.UserData, "meta"): Walk(MetaChildren(body, end), end, depth + 1, Level.Meta); break;
                case (Level.Meta, "ilst"): Walk(body, end, depth + 1, Level.Items); break;
                case (Level.Items, "----"): Freeform(body, end, depth); break;
                case (Level.Freeform, "mean"): _freeformMean = ReadText(body + 4, end); break;
                case (Level.Freeform, "name"): _freeformName = ReadText(body + 4, end); break;
                case (Level.Freeform, "data"): _freeformData = ReadText(body + 8, end); break;
            }
        }

        private void Track(long body, long end, int depth)
        {
            _trackIsSound = false;
            _trackTimescale = 0;
            _trackSamples = -1;
            _trackEdit = null;
            _trackEditUsable = true;

            Walk(body, end, depth + 1, Level.Track);

            // Liczy się pierwsza ścieżka dźwiękowa, ta sama, którą wybiera dekoder systemu.
            if (_haveSound || !_trackIsSound) return;

            _haveSound = true;
            _soundTimescale = _trackTimescale;
            _soundSamples = _trackSamples;
            _soundEdit = _trackEditUsable ? _trackEdit : null;
        }

        private void Freeform(long body, long end, int depth)
        {
            _freeformMean = _freeformName = _freeformData = null;
            Walk(body, end, depth + 1, Level.Freeform);

            if (_freeformMean == "com.apple.iTunes" && _freeformName == "iTunSMPB" && _freeformData is not null)
                _smpb ??= _freeformData;
        }

        public Mp4Gapless? Result()
        {
            if (!_haveSound || _soundTimescale is 0 or > MaxTimescale || _soundSamples <= 0) return null;

            var timescale = (int)_soundTimescale;
            var samples = _soundSamples;
            if (samples > MaxDurationSeconds * timescale) return null;

            var maxDelay = MaxDelaySeconds * timescale;

            if (ParseSmpb(_smpb) is { } smpb && smpb.Delay <= maxDelay && smpb.Length > 0
                && smpb.Length <= samples - smpb.Delay)
                return new Mp4Gapless(timescale, smpb.Delay, smpb.Length, Mp4GaplessSource.ITunSmpb);

            if (_soundEdit is { } edit && edit.MediaTime <= maxDelay && edit.MediaTime < samples
                && EditLength(edit, timescale, samples) is { } length)
                return new Mp4Gapless(timescale, edit.MediaTime, length, Mp4GaplessSource.EditList);

            return new Mp4Gapless(timescale, 0, samples, Mp4GaplessSource.SampleTable);
        }

        /// <summary>
        /// Długość edycji zapisana jest w skali czasu filmu, zwykle znacznie grubszej niż
        /// skala ścieżki (u GStreamera 1800 na sekundę). Gdy zgadza się z końcem tablicy
        /// czasów z dokładnością do jednego kroku tej skali, dokładny jest koniec tablicy.
        /// </summary>
        private long? EditLength((long Duration, long MediaTime) edit, int timescale, long samples)
        {
            var available = samples - edit.MediaTime;

            // Zero to edycja do końca ścieżki.
            if (edit.Duration == 0) return available;
            if (_movieTimescale == 0) return null;

            var length = edit.Duration * (double)timescale / _movieTimescale;
            var step = Math.Ceiling(timescale / (double)_movieTimescale) + 1;

            if (Math.Abs(available - length) <= step) return available;

            var rounded = (long)Math.Round(Math.Min(length, available));
            return rounded > 0 ? rounded : null;
        }

        private uint ReadTimescale(long body, long end)
        {
            Span<byte> data = stackalloc byte[24];
            if (end - body < 4) throw Damaged("pusty nagłówek czasu");

            stream.Position = body;
            stream.ReadExactly(data[..4]);

            // Wersja 1 ma 64-bitowe daty, więc skala stoi dalej.
            var offset = data[0] == 1 ? 20 : 12;
            if (end - body < offset + 4) throw Damaged("niepełny nagłówek czasu");

            stream.Position = body + offset;
            stream.ReadExactly(data[..4]);
            return BinaryPrimitives.ReadUInt32BigEndian(data);
        }

        private string? ReadHandler(long body, long end)
        {
            if (end - body < 12) return null;

            Span<byte> data = stackalloc byte[4];
            stream.Position = body + 8;
            stream.ReadExactly(data);
            return Encoding.ASCII.GetString(data);
        }

        /// <summary>Suma czasów próbek, czyli długość ścieżki z rozbiegiem i dopełnieniem.</summary>
        private long ReadSampleDurations(long body, long end)
        {
            if (end - body < 8) throw Damaged("niepełna tablica czasów");

            Span<byte> head = stackalloc byte[8];
            stream.Position = body;
            stream.ReadExactly(head);

            var count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (count > (end - body - 8) / 8) throw Damaged("tablica czasów dłuższa niż atom");

            var chunk = new byte[SttsChunkEntries * 8];
            long total = 0;
            var left = (long)count;

            while (left > 0)
            {
                var entries = (int)Math.Min(left, SttsChunkEntries);
                stream.ReadExactly(chunk.AsSpan(0, entries * 8));

                for (var i = 0; i < entries; i++)
                {
                    var samples = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(i * 8));
                    var duration = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(i * 8 + 4));
                    total += (long)samples * duration;

                    // Suma z dwóch liczb 32-bitowych na wpis; granica chroni przed przepełnieniem.
                    if (total > long.MaxValue / 4) throw Damaged("suma czasów próbek poza zakresem");
                }

                left -= entries;
            }

            return total;
        }

        /// <summary>
        /// Przycinanie opisuje tylko lista z jedną edycją bez zmiany tempa. Pusta edycja
        /// (cisza przed nagraniem) albo kilka fragmentów to montaż, nie rozbieg kodera.
        /// </summary>
        private void ReadEditList(long body, long end)
        {
            if (end - body < 8) throw Damaged("niepełna lista edycji");

            Span<byte> data = stackalloc byte[20];
            stream.Position = body;
            stream.ReadExactly(data[..8]);

            var version = data[0];
            var count = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            var entrySize = version == 1 ? 20 : 12;

            if (count > (end - body - 8) / entrySize) throw Damaged("lista edycji dłuższa niż atom");

            if (count != 1 || version > 1)
            {
                _trackEditUsable = false;
                return;
            }

            stream.ReadExactly(data[..entrySize]);

            long duration, mediaTime;
            int rate;
            if (version == 1)
            {
                var raw = BinaryPrimitives.ReadUInt64BigEndian(data);
                if (raw > long.MaxValue) throw Damaged("długość edycji poza zakresem");
                duration = (long)raw;
                mediaTime = BinaryPrimitives.ReadInt64BigEndian(data[8..]);
                rate = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
            }
            else
            {
                duration = BinaryPrimitives.ReadUInt32BigEndian(data);
                mediaTime = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                rate = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
            }

            // Tempo 1,0 w zapisie 16.16; czas −1 oznacza pustą edycję.
            if (rate != 0x10000 || mediaTime < 0)
            {
                _trackEditUsable = false;
                return;
            }

            _trackEdit = (duration, mediaTime);
        }

        /// <summary>
        /// Atom <c>meta</c> w iTunes ma pola wersji i flag, w zapisie QuickTime nie ma;
        /// rozróżnia je to, czy zaraz po nagłówku stoi atom <c>hdlr</c>.
        /// </summary>
        private long MetaChildren(long body, long end)
        {
            if (end - body < 8) return body;

            Span<byte> data = stackalloc byte[4];
            stream.Position = body + 4;
            stream.ReadExactly(data);
            return data.SequenceEqual("hdlr"u8) ? body : body + 4;
        }

        private string? ReadText(long start, long end)
        {
            var length = end - start;
            if (length < 0 || length > MaxSmpbBytes) return null;

            var bytes = new byte[length];
            stream.Position = start;
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Tekst szesnastkowy: pole zerowe, opóźnienie, dopełnienie, liczba próbek, dalej pola
    /// bez znaczenia dla odtwarzania.
    /// </summary>
    internal static (long Delay, long Padding, long Length)? ParseSmpb(string? text)
    {
        if (text is null) return null;

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4) return null;

        if (!Hex(fields[1], 8, out var delay) || !Hex(fields[2], 8, out var padding) || !Hex(fields[3], 16, out var length))
            return null;

        return (delay, padding, length);
    }

    private static bool Hex(string field, int maxDigits, out long value)
    {
        value = 0;
        if (field.Length > maxDigits) return false;

        return long.TryParse(field, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static InvalidDataException Damaged(string detail) => new($"MP4: {detail}.");

    private static string Printable(string type) =>
        type.All(c => c is >= ' ' and <= '~') ? type : "?";
}
