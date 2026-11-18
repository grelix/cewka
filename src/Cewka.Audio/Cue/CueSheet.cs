using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cewka.Audio.Playback;

namespace Cewka.Audio.Cue;

/// <summary>Arkusz, którego nie da się przyjąć: składnia, czasy, granice. Pliki dźwiękowe grają wtedy zwyczajnie.</summary>
public sealed class CueSheetException(string message) : FormatException(message);

/// <summary>Ścieżka arkusza: numer, tytuł i wykonawca oraz miejsca w pliku w klatkach (1/75 s).</summary>
/// <param name="Start">Początek utworu, czyli <c>INDEX 01</c>.</param>
/// <param name="Pregap">Początek przerwy przed utworem (<c>INDEX 00</c>); należy do końca poprzedniego.</param>
/// <param name="IsAudio">Ścieżka danych nie gra, ale wyznacza koniec poprzedniej.</param>
public sealed record CueTrack(
    int Number, long Start, long? Pregap, bool IsAudio, string? Title, string? Performer,
    double? Gain, double? Peak);

/// <summary>Plik wskazany w arkuszu, tak jak go zapisano, z jego ścieżkami w kolejności.</summary>
/// <param name="IsAudio">Fałsz dla obrazów płyty (<c>BINARY</c>, <c>MOTOROLA</c>), których odtwarzacz nie czyta.</param>
public sealed record CueFile(string Name, bool IsAudio, IReadOnlyList<CueTrack> Tracks);

/// <summary>
/// Arkusz CUE zapisany obok obrazu płyty: jeden albo kilka plików dźwiękowych i podział na
/// ścieżki. Czytane są polecenia, które opisują utwory (<c>FILE</c>, <c>TRACK</c>, <c>INDEX</c>,
/// <c>TITLE</c>, <c>PERFORMER</c> i uwagi <c>REM</c> z datą, gatunkiem, numerem płyty
/// i wzmocnieniem ReplayGain); pozostałe są pomijane.
///
/// <para>Arkusz to plik od kogokolwiek. Ma granice rozmiaru i liczby ścieżek, a czasy w obrębie
/// pliku muszą rosnąć; inaczej cały arkusz jest odrzucany wyjątkiem <see cref="CueSheetException"/>.</para>
/// </summary>
public sealed partial class CueSheet
{
    public const string Extension = ".cue";

    /// <summary>Arkusz płyty ma kilka kilobajtów; większy nie jest czytany w całości do pamięci.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Tyle ścieżek mieści numer z trzech cyfr; płyta ma ich najwyżej 99.</summary>
    public const int MaxTracks = 999;

    /// <summary>Klatek na sekundę w zapisie czasu <c>mm:ss:ff</c>.</summary>
    public const int FramesPerSecond = 75;

    /// <summary>
    /// Największy czas w klatkach, jaki przyjmuje odczyt: <c>99999:59:74</c>, czyli pięć cyfr minut.
    /// Ta sama granica obowiązuje wycinki z zapisu kolejki i z klucza pamięci podręcznej.
    /// </summary>
    public const long MaxTime = (99999L * 60 + 59) * FramesPerSecond + FramesPerSecond - 1;

    public string? Title { get; private init; }
    public string? Performer { get; private init; }
    public string? Genre { get; private init; }
    public int? Year { get; private init; }
    public int? Disc { get; private init; }
    public double? AlbumGain { get; private init; }
    public double? AlbumPeak { get; private init; }
    public IReadOnlyList<CueFile> Files { get; private init; } = [];

    public static bool IsSheetPath(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <param name="fallbackCodePage">Strona kodowa arkusza, który nie jest UTF-8; null oznacza stronę systemu.</param>
    /// <exception cref="CueSheetException">Arkusz za duży albo niepoprawny.</exception>
    /// <exception cref="IOException">Pliku nie da się przeczytać.</exception>
    public static CueSheet Load(string path, int? fallbackCodePage = null)
    {
        if (!RegularFile.Is(path)) throw new CueSheetException("arkusz nie jest zwykłym plikiem");

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaxBytes) throw new CueSheetException($"arkusz ma ponad {MaxBytes} bajtów");

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        return Parse(bytes, fallbackCodePage);
    }

    /// <exception cref="CueSheetException">Arkusz za duży albo niepoprawny.</exception>
    public static CueSheet Parse(ReadOnlySpan<byte> bytes, int? fallbackCodePage = null)
    {
        if (bytes.Length > MaxBytes) throw new CueSheetException($"arkusz ma ponad {MaxBytes} bajtów");
        return Parse(Decode(bytes, fallbackCodePage));
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Znacznik kolejności bajtów UTF-8 albo UTF-16, potem poprawny UTF-8, na końcu strona kodowa
    /// systemu: tak zapisywały arkusze programy do zgrywania płyt w Windowsie.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, int? fallbackCodePage)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return LegacyEncoding(fallbackCodePage).GetString(bytes);
        }
    }

    /// <summary>
    /// Strona kodowa systemu. Przy niezmiennej globalizacji kultura podaje zawsze 1252, więc
    /// w Windowsie strona pochodzi wprost z systemu, tak jak przy listach M3U.
    /// </summary>
    private static Encoding LegacyEncoding(int? codePage)
    {
        var chosen = codePage ?? (OperatingSystem.IsWindows() ? (int)GetACP() : 0);
        if (chosen == 0) return Encoding.Latin1;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(chosen);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    private sealed class FileBuilder(string name, bool isAudio)
    {
        public readonly string Name = name;
        public readonly bool IsAudio = isAudio;
        public readonly List<CueTrack> Tracks = [];
    }

    private sealed class TrackBuilder(int number, bool isAudio)
    {
        public readonly int Number = number;
        public readonly bool IsAudio = isAudio;
        public long? Index0, Index1;
        public string? Title, Performer;
        public double? Gain, Peak;
    }

    private static CueSheet Parse(string text)
    {
        string? title = null, performer = null, genre = null;
        int? year = null, disc = null;
        double? albumGain = null, albumPeak = null;

        var files = new List<FileBuilder>();
        FileBuilder? file = null;
        TrackBuilder? track = null;
        var count = 0;

        void CloseTrack()
        {
            if (track is null) return;

            // Ścieżka bez INDEX 01 nie ma początku.
            if (track.Index1 is not { } start) throw new CueSheetException($"ścieżka {track.Number} bez INDEX 01");
            if (track.Index0 is { } pregap && pregap > start) throw new CueSheetException($"INDEX 00 po INDEX 01 w ścieżce {track.Number}");

            var previous = file!.Tracks.Count > 0 ? file.Tracks[^1] : null;
            if (previous is not null && (start <= previous.Start || track.Index0 < previous.Start))
                throw new CueSheetException($"czasy w pliku {file.Name} nie rosną");

            file.Tracks.Add(new CueTrack(track.Number, start, track.Index0, track.IsAudio, track.Title, track.Performer,
                track.Gain, track.Peak));
            track = null;
        }

        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            var (command, rest) = SplitWord(line);

            switch (command.ToUpperInvariant())
            {
                case "FILE":
                {
                    // Zgrywarka zapisująca przerwę na końcu poprzedniego pliku stawia INDEX 00 ścieżki
                    // jeszcze w nim, a INDEX 01 już w następnym. Ścieżka należy wtedy do następnego,
                    // a przerwa zostaje na końcu poprzedniego, jak każda przerwa przed utworem.
                    var carried = track is { Index1: null } ? track : null;
                    if (carried is not null) carried.Index0 = null;
                    else CloseTrack();

                    var (name, type) = FileArguments(rest);
                    file = new FileBuilder(name, type is not ("BINARY" or "MOTOROLA"));
                    files.Add(file);
                    track = carried;
                    break;
                }

                case "TRACK":
                {
                    if (file is null) throw new CueSheetException("TRACK przed FILE");
                    CloseTrack();

                    var (number, type) = SplitWord(rest);
                    if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > MaxTracks)
                        throw new CueSheetException($"numer ścieżki {number}");
                    if (++count > MaxTracks) throw new CueSheetException($"ponad {MaxTracks} ścieżek");

                    track = new TrackBuilder(n, type.Trim().Equals("AUDIO", StringComparison.OrdinalIgnoreCase));
                    break;
                }

                case "INDEX":
                {
                    if (track is null) throw new CueSheetException("INDEX poza ścieżką");

                    var (number, time) = SplitWord(rest);
                    if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        throw new CueSheetException($"numer indeksu {number}");

                    var frames = ParseTime(time.Trim());
                    if (index == 0) track.Index0 = frames;
                    else if (index == 1) track.Index1 = frames;
                    break;
                }

                case "TITLE":
                    if (track is not null) track.Title = Text(rest);
                    else title = Text(rest);
                    break;

                case "PERFORMER":
                    if (track is not null) track.Performer = Text(rest);
                    else performer = Text(rest);
                    break;

                case "REM":
                {
                    var (key, value) = SplitWord(rest);
                    switch (key.ToUpperInvariant())
                    {
                        case "DATE":
                            year = ParseYear(Text(value)) ?? year;
                            break;
                        case "GENRE":
                            genre = Text(value) ?? genre;
                            break;
                        case "DISCNUMBER":
                            disc = ParseNumber(Text(value)) ?? disc;
                            break;
                        case "REPLAYGAIN_ALBUM_GAIN":
                            albumGain = ParseGain(Text(value)) ?? albumGain;
                            break;
                        case "REPLAYGAIN_ALBUM_PEAK":
                            albumPeak = ParsePeak(Text(value)) ?? albumPeak;
                            break;
                        case "REPLAYGAIN_TRACK_GAIN" when track is not null:
                            track.Gain = ParseGain(Text(value)) ?? track.Gain;
                            break;
                        case "REPLAYGAIN_TRACK_PEAK" when track is not null:
                            track.Peak = ParsePeak(Text(value)) ?? track.Peak;
                            break;
                    }

                    break;
                }
            }
        }

        CloseTrack();

        return new CueSheet
        {
            Title = title,
            Performer = performer,
            Genre = genre,
            Year = year,
            Disc = disc,
            AlbumGain = albumGain,
            AlbumPeak = albumPeak,
            Files = files.Select(f => new CueFile(f.Name, f.IsAudio, f.Tracks)).ToList(),
        };
    }

    /// <summary>
    /// Utwory arkusza do kolejki: ścieżki dźwiękowe w plikach, które <paramref name="resolve"/>
    /// zamienia na istniejący plik dźwiękowy (albo null, gdy takiego nie ma). Utwór zaczyna się
    /// w <c>INDEX 01</c> i kończy w <c>INDEX 01</c> następnej ścieżki tego samego pliku, ostatni
    /// gra do końca pliku.
    /// </summary>
    public IReadOnlyList<QueueItem> Tracks(string sheetPath, Func<string, string?> resolve)
    {
        var items = new List<QueueItem>();

        foreach (var file in Files)
        {
            if (!file.IsAudio || file.Tracks.All(t => !t.IsAudio)) continue;

            var path = resolve(file.Name);
            if (path is null) continue;

            for (var i = 0; i < file.Tracks.Count; i++)
            {
                var track = file.Tracks[i];
                if (!track.IsAudio) continue;

                long? end = i + 1 < file.Tracks.Count ? file.Tracks[i + 1].Start : null;

                items.Add(new QueueItem(path, new SheetTrack
                {
                    SheetPath = sheetPath,
                    Number = track.Number,
                    Start = track.Start,
                    End = end,
                    Title = track.Title,
                    Performer = track.Performer,
                    Album = Title,
                    AlbumPerformer = Performer,
                    Year = Year,
                    Disc = Disc,
                    TrackGain = track.Gain,
                    TrackPeak = track.Peak,
                    AlbumGain = AlbumGain,
                    AlbumPeak = AlbumPeak,
                }));
            }
        }

        return items;
    }

    /// <summary>Pierwsze słowo wiersza i reszta po nim.</summary>
    private static (string Word, string Remainder) SplitWord(string text)
    {
        text = text.TrimStart();
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (text[..end], text[end..].TrimStart());
    }

    /// <summary>
    /// Nazwa pliku i jego typ. Bez cudzysłowów nazwa bywa zapisana ze spacjami, więc typem jest
    /// ostatnie słowo, a nazwą wszystko przed nim.
    /// </summary>
    private static (string Name, string Type) FileArguments(string rest)
    {
        string name, type;

        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close < 0) throw new CueSheetException("FILE bez zamykającego cudzysłowu");

            name = rest[1..close];
            type = rest[(close + 1)..].Trim();
        }
        else
        {
            var last = rest.LastIndexOfAny([' ', '\t']);
            if (last < 0) (name, type) = (rest, string.Empty);
            else (name, type) = (rest[..last].TrimEnd(), rest[(last + 1)..]);
        }

        if (name.Length == 0) throw new CueSheetException("FILE bez nazwy");
        return (name, type.ToUpperInvariant());
    }

    /// <summary>Tekst polecenia: zawartość cudzysłowów albo cała reszta wiersza; null, gdy pusty.</summary>
    private static string? Text(string rest)
    {
        rest = rest.Trim();
        if (rest.Length >= 2 && rest[0] == '"')
        {
            var close = rest.LastIndexOf('"');
            rest = close > 0 ? rest[1..close] : rest[1..];
        }

        rest = rest.Trim();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary><c>mm:ss:ff</c> w klatkach; sekundy do 59, klatki do 74, całość do <see cref="MaxTime"/>.</summary>
    internal static long ParseTime(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3) throw new CueSheetException($"czas {text}");

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || parts[0].Length > 9 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 59 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var frames) || frames >= FramesPerSecond)
            throw new CueSheetException($"czas {text}");

        var time = ((long)minutes * 60 + seconds) * FramesPerSecond + frames;
        return time <= MaxTime ? time : throw new CueSheetException($"czas {text}");
    }

    private static int? ParseYear(string? text) =>
        text is { Length: >= 4 } && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year > 1000
            ? year
            : null;

    private static int? ParseNumber(string? text)
    {
        if (text is null) return null;

        // „1/2” zapisuje płytę i liczbę płyt.
        var slash = text.IndexOf('/');
        var head = slash >= 0 ? text[..slash] : text;
        return int.TryParse(head.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1000
            ? value
            : null;
    }

    /// <summary>Jak w znacznikach plików: liczba z kropką i opcjonalnym „dB”, w granicach rozsądku.</summary>
    private static double? ParseGain(string? text)
    {
        if (text is null) return null;

        var number = text.Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= -60 and <= 60
            ? value
            : null;
    }

    private static double? ParsePeak(string? text) =>
        text is not null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value is > 0 and <= 10
            ? value
            : null;
}
