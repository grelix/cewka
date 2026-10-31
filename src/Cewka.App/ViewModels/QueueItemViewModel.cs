using Cewka.Audio.Playback;

namespace Cewka.App.ViewModels;

/// <summary>One row of the playback queue.</summary>
public sealed class QueueItemViewModel : ObservableObject
{
    private bool _isCurrent;
    private bool _isUnsupported;
    private string _duration = "0:00";
    private string? _unsupportedReason;

    private string _title = string.Empty;
    private string _artist = string.Empty;

    private int _number;

    /// <summary>Pozycja w kolejce, od jedynki; zmienia się przy przesuwaniu i usuwaniu wierszy.</summary>
    public required int Number { get => _number; set => Set(ref _number, value); }

    /// <summary>Wpis silnika, któremu odpowiada wiersz; po nim przychodzą odczytane znaczniki.</summary>
    public QueueEntry? Entry { get; init; }

    /// <summary>Starts as the file name and is replaced once the tags have been read.</summary>
    public required string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) Raise(nameof(AccessibleName)); }
    }

    /// <summary>Performer alone, shown under the title in the queue row.</summary>
    public required string Artist
    {
        get => _artist;
        set { if (Set(ref _artist, value)) Raise(nameof(AccessibleName)); }
    }


    /// <summary>Full path, used for the tooltip and by the engine.</summary>
    public required string Path { get; init; }

    /// <summary>Set after the tags have been read, which happens as the track is opened.</summary>
    public required string Duration
    {
        get => _duration;
        set { if (Set(ref _duration, value)) Raise(nameof(AccessibleName)); }
    }

    /// <summary>Nazwa wiersza dla czytnika ekranu: tytuł, wykonawca i czas trwania.</summary>
    public string AccessibleName => $"{_title}, {_artist}, {_duration}";

    public double DurationSeconds { get; set; }

    /// <summary>The track currently loaded into the player.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (!Set(ref _isCurrent, value)) return;
            Raise(nameof(NumberOpacity));
            Raise(nameof(MarkerOpacity));
        }
    }

    /// <summary>
    /// Set for files no codec on this system can decode. The row stays visible and greyed
    /// out rather than vanishing, so nothing disappears without explanation.
    /// </summary>
    public bool IsUnsupported
    {
        get => _isUnsupported;
        set
        {
            if (!Set(ref _isUnsupported, value)) return;
            Raise(nameof(RowOpacity));
        }
    }

    public string? UnsupportedReason
    {
        get => _unsupportedReason;
        set
        {
            if (!Set(ref _unsupportedReason, value)) return;
            Raise(nameof(Tooltip));
        }
    }

    /// <summary>Wiersz jest wyszarzony z powodu chwilowego błędu, a nie formatu pliku.</summary>
    private bool _failureIsTemporary;

    /// <summary>
    /// Oznacza wiersz po nieudanym otwarciu albo odtworzeniu. Format nieobsługiwany zostaje
    /// oznaczony na stałe; inne przyczyny (brak dysku, plik zajęty) znikają po udanym odtworzeniu.
    /// </summary>
    /// <param name="formatDescription">Opis z wykrywania formatu; używany tylko przy formacie nieobsługiwanym.</param>
    /// <param name="otherwise">Tekst dla pozostałych przyczyn.</param>
    public void ShowFailure(TrackFailureReason reason, string? formatDescription, string otherwise)
    {
        _failureIsTemporary = reason != TrackFailureReason.UnsupportedFormat;
        UnsupportedReason = _failureIsTemporary ? otherwise : formatDescription ?? otherwise;
        IsUnsupported = true;
    }

    /// <summary>Utwór zagrał: oznaczenie chwilowego błędu przestaje obowiązywać.</summary>
    public void ClearTemporaryFailure()
    {
        if (!_failureIsTemporary) return;

        _failureIsTemporary = false;
        IsUnsupported = false;
        UnsupportedReason = null;
    }

    /// <summary>Path normally; the reason instead when the file cannot be played.</summary>
    public string Tooltip => _unsupportedReason ?? Path;

    public double RowOpacity => _isUnsupported ? 0.45 : 1.0;

    /// <summary>The number gives way to the playing marker on the current row.</summary>
    public double NumberOpacity => IsCurrent ? 0 : 1;

    public double MarkerOpacity => IsCurrent ? 1 : 0;
}
