using System.Globalization;
using Cewka.Audio.Cue;
using Cewka.Audio.Metadata;

namespace Cewka.Audio.Playback;

/// <summary>
/// Utwór arkusza CUE: wycinek pliku dźwiękowego i znaczniki, które podał arkusz. Czasy
/// w klatkach płyty (1/75 s), bo tak zapisuje je arkusz; przy częstotliwościach płyty i studia
/// klatka to całkowita liczba próbek, więc granice wypadają dokładnie.
/// </summary>
public sealed record SheetTrack
{
    /// <summary>Arkusz, z którego pochodzi utwór; wpisy jednego arkusza tworzą jeden album.</summary>
    public required string SheetPath { get; init; }

    /// <summary>Numer ścieżki w arkuszu.</summary>
    public required int Number { get; init; }

    /// <summary>Początek w pliku, w klatkach (<c>INDEX 01</c>).</summary>
    public required long Start { get; init; }

    /// <summary>Koniec w pliku, w klatkach; null, gdy utwór gra do końca pliku.</summary>
    public long? End { get; init; }

    public string? Title { get; init; }
    public string? Performer { get; init; }
    public string? Album { get; init; }
    public string? AlbumPerformer { get; init; }
    public int? Year { get; init; }
    public int? Disc { get; init; }
    public double? TrackGain { get; init; }
    public double? TrackPeak { get; init; }
    public double? AlbumGain { get; init; }
    public double? AlbumPeak { get; init; }

    /// <summary>Wycinek obejmuje plik od początku do końca, czyli utwór to cały plik.</summary>
    public bool IsWholeFile => Start == 0 && End is null;

    /// <summary>
    /// Ramka w częstotliwości <paramref name="sampleRate"/> odpowiadająca klatce arkusza. Klatki
    /// przychodzą także z pliku kolejki i z klucza pamięci, więc są nasycane do zakresu parsera
    /// arkusza; iloczyn z dowolną częstotliwością mieści się wtedy w liczbie 64-bitowej.
    /// </summary>
    public static long ToFrames(long sheetFrames, int sampleRate) =>
        (Saturate(sheetFrames) * Math.Max(0, sampleRate) + CueSheet.FramesPerSecond / 2) / CueSheet.FramesPerSecond;

    private static TimeSpan ToTime(long sheetFrames) =>
        TimeSpan.FromTicks(Saturate(sheetFrames) * TimeSpan.TicksPerSecond / CueSheet.FramesPerSecond);

    private static long Saturate(long sheetFrames) => Math.Clamp(sheetFrames, 0, CueSheet.MaxTime);

    /// <summary>
    /// Znaczniki pliku z tym, co podaje arkusz, w miejscu znaczników pliku. Wykonawca ścieżki ma
    /// zapas w wykonawcy albumu, album to tytuł arkusza. Wzmocnienie utworu z pliku obejmuje cały
    /// plik, więc wycinek bierze je tylko wtedy, gdy jest całym plikiem.
    /// </summary>
    public TrackMetadata Apply(TrackMetadata file)
    {
        var duration = End is { } end
            ? (ToTime(end) > ToTime(Start) ? ToTime(end) - ToTime(Start) : TimeSpan.Zero)
            : file.Duration > ToTime(Start) ? file.Duration - ToTime(Start) : TimeSpan.Zero;

        return new TrackMetadata
        {
            Path = file.Path,
            Title = Title ?? (IsWholeFile ? file.Title : $"{file.Title} ({Number.ToString("00", CultureInfo.InvariantCulture)})"),
            Artist = Performer ?? AlbumPerformer ?? file.Artist,
            Album = Album ?? file.Album,
            AlbumArtist = AlbumPerformer ?? file.AlbumArtist,
            Year = Year ?? file.Year,
            TrackNumber = Number,
            DiscNumber = Disc ?? file.DiscNumber,
            Duration = duration,
            Bitrate = file.Bitrate,
            SampleRate = file.SampleRate,
            BitDepth = file.BitDepth,
            Channels = file.Channels,
            IsVariableBitrate = file.IsVariableBitrate,
            Codec = file.Codec,
            ReplayGainTrack = TrackGain ?? (IsWholeFile ? file.ReplayGainTrack : null),
            ReplayGainTrackPeak = TrackPeak ?? (IsWholeFile ? file.ReplayGainTrackPeak : null),
            ReplayGainAlbum = AlbumGain ?? file.ReplayGainAlbum,
            ReplayGainAlbumPeak = AlbumPeak ?? file.ReplayGainAlbumPeak,
        };
    }

    /// <summary>
    /// Klucz pamięci podręcznych (pomiar głośności, obwiednia) dla wycinka pliku <paramref name="path"/>.
    /// Znak zerowy nie występuje w ścieżkach żadnego systemu, więc klucz wycinka nie zderzy się
    /// z kluczem całego pliku, którym jest sama ścieżka.
    /// </summary>
    public string CacheKey(string path) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}\0{Start}-{End}");

    /// <summary>Plik i zakres z klucza wycinka; fałsz dla klucza całego pliku albo zepsutego.</summary>
    public static bool TryParseCacheKey(string key, out string path, out long start, out long? end)
    {
        path = key;
        start = 0;
        end = null;

        var mark = key.IndexOf('\0');
        if (mark < 0) return false;

        path = key[..mark];
        var range = key.AsSpan(mark + 1);
        var dash = range.IndexOf('-');
        if (dash < 0 || !long.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || start > CueSheet.MaxTime)
            return false;

        if (dash + 1 < range.Length)
        {
            if (!long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                || last <= start || last > CueSheet.MaxTime)
                return false;
            end = last;
        }

        return true;
    }

    /// <summary>Plik, którego dotyczy klucz pamięci podręcznej: sama ścieżka albo jej część przed zakresem.</summary>
    public static string FileOfCacheKey(string key)
    {
        var mark = key.IndexOf('\0');
        return mark < 0 ? key : key[..mark];
    }
}

/// <summary>Pozycja do kolejki: cały plik albo utwór arkusza CUE w pliku <see cref="Path"/>.</summary>
public sealed record QueueItem(string Path, SheetTrack? Sheet = null);
