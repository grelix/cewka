using System.Text.Json.Serialization;
using Cewka.Audio.Playback;

namespace Cewka.App.Models;

/// <summary>
/// Kolejka z chwili ostatniego zamknięcia. Osobno od <see cref="AppSettings"/>, bo zmienia się
/// znacznie częściej i bywa długa na tysiące pozycji.
/// </summary>
public sealed class QueueState
{
    /// <summary>
    /// Pliki w kolejności kolejki. Utwór arkusza CUE stoi tu ścieżką swojego pliku, a wycinek
    /// i znaczniki niesie <see cref="Sheets"/>, więc starsza wersja programu dostaje zwykłe pliki.
    /// </summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>
    /// Utwory arkuszy CUE: numer pozycji w <see cref="Paths"/>, wycinek i znaczniki z arkusza.
    /// Zapisane w całości, więc kolejka wraca taka sama także wtedy, gdy arkusza już nie ma.
    /// Pominięte w pliku, gdy kolejka nie ma takich utworów.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SheetEntryState>? Sheets { get; set; }

    /// <summary>Pozycje kolejki z wycinkami arkuszy; wpis arkusza, który nie przeszedł sprawdzenia, jest całym plikiem.</summary>
    public List<QueueItem> Items()
    {
        var sheets = new Dictionary<int, SheetTrack>();
        foreach (var entry in Sheets ?? [])
            if (entry?.ToTrack() is { } track && entry.Index >= 0 && entry.Index < Paths.Count) sheets.TryAdd(entry.Index, track);

        var items = new List<QueueItem>(Paths.Count);
        for (var i = 0; i < Paths.Count; i++) items.Add(new QueueItem(Paths[i], sheets.GetValueOrDefault(i)));
        return items;
    }

    /// <summary>Zapisuje pozycje: ścieżki w <see cref="Paths"/>, wycinki arkuszy w <see cref="Sheets"/>.</summary>
    public void SetItems(IEnumerable<QueueItem> items)
    {
        var paths = new List<string>();
        var sheets = new List<SheetEntryState>();

        foreach (var item in items)
        {
            if (item.Sheet is { } sheet) sheets.Add(SheetEntryState.From(paths.Count, sheet));
            paths.Add(item.Path);
        }

        Paths = paths;
        Sheets = sheets.Count > 0 ? sheets : null;
    }

    /// <summary>Index of the track that was playing, or −1.</summary>
    public int CurrentIndex { get; set; } = -1;

    /// <summary>Position within that track, in seconds.</summary>
    public double PositionSeconds { get; set; }

    public bool Shuffle { get; set; }

    /// <summary>
    /// Repeat mode by name: <c>None</c>, <c>Queue</c> or <c>Track</c>.
    /// </summary>
    public string? Repeat { get; set; }

    /// <summary>
    /// Dwustanowa postać ze starszych plików, czytana tylko dla zgodności, żeby kolejka zapisana
    /// przed powtarzaniem jednego utworu nie straciła ustawienia.
    /// </summary>
    public bool RepeatQueue { get; set; }

    /// <summary>
    /// Numer tego zapisu listy, inny przy każdym zapisie pliku kolejki. Plik pozycji
    /// (<see cref="QueuePosition"/>) obowiązuje tylko dla listy o tym samym numerze. Zero w pliku
    /// sprzed wprowadzenia numerów: do takiego żaden plik pozycji nie pasuje.
    ///
    /// <para>Pierwszy w pliku, żeby dało się go odczytać z początku bez czytania listy ścieżek
    /// (<c>QueueStateStore.ReadRevision</c>).</para>
    /// </summary>
    [JsonPropertyOrder(-1)]
    public long Revision { get; set; }

    /// <summary>
    /// Otwarcia niedokończone przed zamknięciem okna, w kolejności przybycia; następne uruchomienie
    /// je dokańcza. Pominięte w pliku, gdy ich nie ma; starsze wersje pomijają nieznane pole.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PendingOpenState>? PendingOpens { get; set; }

    /// <summary>Resolves the mode from whichever form the file happens to carry.</summary>
    public RepeatMode ReadRepeat() => Repeat switch
    {
        "Track" => RepeatMode.Track,
        "Queue" => RepeatMode.Queue,
        "None" => RepeatMode.None,
        _ => RepeatQueue ? RepeatMode.Queue : RepeatMode.None,
    };
}

/// <summary>Utwór arkusza CUE w zapisie kolejki: pozycja w liście ścieżek, wycinek i znaczniki.</summary>
public sealed class SheetEntryState
{
    public int Index { get; set; }

    /// <summary>Ścieżka arkusza; wyznacza album, a przy eksporcie listy staje w miejscu utworów.</summary>
    public string? Sheet { get; set; }

    public int Track { get; set; }

    /// <summary>Początek w klatkach arkusza (1/75 s).</summary>
    public long Start { get; set; }

    /// <summary>Koniec w klatkach arkusza; brak, gdy utwór gra do końca pliku.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? End { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Performer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Album { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlbumPerformer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Year { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Disc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrackGain { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrackPeak { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AlbumGain { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AlbumPeak { get; set; }

    /// <summary>Najdłuższy przyjmowany tekst; dłuższy to nie znacznik, tylko zepsuty plik.</summary>
    private const int MaxText = 4096;

    public static SheetEntryState From(int index, SheetTrack track) => new()
    {
        Index = index,
        Sheet = track.SheetPath,
        Track = track.Number,
        Start = track.Start,
        End = track.End,
        Title = track.Title,
        Performer = track.Performer,
        Album = track.Album,
        AlbumPerformer = track.AlbumPerformer,
        Year = track.Year,
        Disc = track.Disc,
        TrackGain = track.TrackGain,
        TrackPeak = track.TrackPeak,
        AlbumGain = track.AlbumGain,
        AlbumPeak = track.AlbumPeak,
    };

    /// <summary>Wycinek z zapisu albo null, gdy zapis jest niewiarygodny: plik kolejki mógł zmienić ktoś inny.</summary>
    public SheetTrack? ToTrack()
    {
        if (string.IsNullOrWhiteSpace(Sheet) || Track is < 1 or > Cewka.Audio.Cue.CueSheet.MaxTracks) return null;
        // Ta sama granica czasu co w parserze arkusza; większa liczba to nie czas z płyty.
        if (Start < 0 || Start > Cewka.Audio.Cue.CueSheet.MaxTime) return null;
        if (End is { } end && (end <= Start || end > Cewka.Audio.Cue.CueSheet.MaxTime)) return null;

        return new SheetTrack
        {
            SheetPath = Sheet,
            Number = Track,
            Start = Start,
            End = End,
            Title = Text(Title),
            Performer = Text(Performer),
            Album = Text(Album),
            AlbumPerformer = Text(AlbumPerformer),
            Year = Year is > 0 and < 10000 ? Year : null,
            Disc = Disc is > 0 and < 1000 ? Disc : null,
            TrackGain = Gain(TrackGain),
            TrackPeak = Peak(TrackPeak),
            AlbumGain = Gain(AlbumGain),
            AlbumPeak = Peak(AlbumPeak),
        };
    }

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > MaxText ? value[..MaxText] : value;

    private static double? Gain(double? value) => value is >= -60 and <= 60 ? value : null;

    private static double? Peak(double? value) => value is > 0 and <= 10 ? value : null;
}

/// <summary>Otwarcie plików przerwane zamknięciem okna.</summary>
public sealed class PendingOpenState
{
    /// <summary>Ścieżki w postaci przysłanej: pliki i foldery, a przy <c>Playlist</c> plik listy.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Akcja otwierania: <c>Outside</c>, <c>Add</c>, <c>Replace</c>, <c>Append</c> albo <c>Playlist</c>.</summary>
    public string? Action { get; set; }
}

/// <summary>
/// Miejsce odtwarzania zapisywane w trakcie grania do małego pliku obok pliku kolejki. Lista
/// ścieżek zmienia się rzadko, a miejsce co chwilę; zapisywanie z nim całej listy co pół minuty
/// przy dużej kolejce oznaczało megabajty na dysk tylko po to, żeby zapamiętać sekundę utworu.
/// </summary>
public sealed class QueuePosition
{
    public long Revision { get; set; }

    public int CurrentIndex { get; set; } = -1;

    public double PositionSeconds { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QueueState))]
[JsonSerializable(typeof(SheetEntryState))]
[JsonSerializable(typeof(QueuePosition))]
internal sealed partial class QueueStateJsonContext : JsonSerializerContext;
