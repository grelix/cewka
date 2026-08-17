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

    public required int Number { get; init; }

    /// <summary>Starts as the file name and is replaced once the tags have been read.</summary>
    public required string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>Performer alone, shown under the title in the queue row.</summary>
    public required string Artist
    {
        get => _artist;
        set => Set(ref _artist, value);
    }

    /// <summary>Full "performer — album · year" line shown beside the record.</summary>
    public required string AlbumLine { get; init; }

    /// <summary>Technical description shown on the badge once the track starts playing.</summary>
    public required string Format { get; init; }

    /// <summary>Full path, used for the tooltip and by the engine.</summary>
    public required string Path { get; init; }

    /// <summary>Set after the tags have been read, which happens as the track is opened.</summary>
    public required string Duration
    {
        get => _duration;
        set => Set(ref _duration, value);
    }

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

    /// <summary>Path normally; the reason instead when the file cannot be played.</summary>
    public string Tooltip => _unsupportedReason ?? Path;

    public double RowOpacity => _isUnsupported ? 0.45 : 1.0;

    /// <summary>The number gives way to the playing marker on the current row.</summary>
    public double NumberOpacity => IsCurrent ? 0 : 1;

    public double MarkerOpacity => IsCurrent ? 1 : 0;
}
