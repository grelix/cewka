using Cewka.Audio.Playback;

namespace Cewka.Audio.Decoding;

/// <summary>
/// Wycinek pliku jako osobny utwór: utwór arkusza CUE. Ustawia początek, kończy odczyt na końcu
/// wycinka, a pozycję, długość i przewijanie podaje względem wycinka. Gdy następny utwór jest
/// kolejnym wycinkiem tego samego pliku, ten sam dekoder gra dalej (<see cref="TryAdvance"/>):
/// bez przewinięcia i bez zerowania konwersji, jakby plik grał w całości.
/// </summary>
internal sealed class SliceDecoder : IAudioDecoder, IContinuousDecoder
{
    private readonly IAudioDecoder _inner;
    private long _start;
    private long _end;

    /// <param name="path">Plik, który czyta <paramref name="inner"/>; po nim rozpoznawany jest następny wycinek.</param>
    public SliceDecoder(IAudioDecoder inner, string path, SheetTrack track)
    {
        _inner = inner;
        Path = path;
        Track = track;
        (_start, _end) = Range(track);

        // Od początku pliku nie ma czego przewijać, a nieprzewinięty dekoder może przejąć konwersję poprzedniego utworu.
        if (_start > 0) _inner.Seek(_start);
    }

    public string Path { get; }

    public SheetTrack Track { get; private set; }

    /// <summary>Dekoder całego pliku pod wycinkiem.</summary>
    public IAudioDecoder Inner => _inner;

    private (long Start, long End) Range(SheetTrack track) =>
        (SheetTrack.ToFrames(track.Start, _inner.SampleRate),
         track.End is { } end ? SheetTrack.ToFrames(end, _inner.SampleRate) : long.MaxValue);

    public int SampleRate => _inner.SampleRate;
    public int Channels => _inner.Channels;
    public int SourceSampleRate => _inner.SourceSampleRate;
    public int SourceChannels => _inner.SourceChannels;
    public int InstantaneousBitrate => _inner.InstantaneousBitrate;
    public bool CanSeek => _inner.CanSeek;

    /// <summary>Do końca wycinka albo, dla ostatniego utworu, do końca pliku; zero, gdy plik nie zna długości.</summary>
    public long TotalFrames
    {
        get
        {
            var file = _inner.TotalFrames;
            var end = file > 0 ? Math.Min(file, _end) : _end;
            return end == long.MaxValue ? 0 : Math.Max(0, end - _start);
        }
    }

    public long Position => Math.Max(0, _inner.Position - _start);

    public int Read(Span<float> destination)
    {
        var left = _end - _inner.Position;
        if (left <= 0) return 0;

        var frames = destination.Length / Channels;
        if (frames > left) destination = destination[..(int)(left * Channels)];

        return _inner.Read(destination);
    }

    public void Seek(long frame)
    {
        // Cel z zewnątrz może być dowolnie duży; suma nie może przejść w liczbę ujemną.
        var target = _start + Math.Min(Math.Max(0, frame), long.MaxValue - _start);
        _inner.Seek(_end == long.MaxValue ? target : Math.Min(target, _end));
    }

    /// <summary>
    /// Przechodzi na <paramref name="next"/>, jeśli to wycinek tego samego pliku zaczynający się
    /// dokładnie tam, gdzie skończył się bieżący, a dekoder doszedł do tego miejsca.
    /// </summary>
    public bool TryAdvance(string path, SheetTrack next)
    {
        if (!string.Equals(path, Path, StringComparison.Ordinal)) return false;
        if (Track.End is not { } end || next.Start != end) return false;
        if (_inner.Position != _end) return false;

        Track = next;
        (_start, _end) = Range(next);
        return true;
    }

    public bool HoldTail
    {
        get => _inner is IContinuousDecoder continuous && continuous.HoldTail;
        set
        {
            if (_inner is IContinuousDecoder continuous) continuous.HoldTail = value;
        }
    }

    /// <summary>Tylko wycinek od początku pliku, jeszcze nieczytany, może przejąć konwersję poprzedniego utworu.</summary>
    public bool TryContinueFrom(IAudioDecoder previous) =>
        _start == 0 && _inner is IContinuousDecoder continuous && continuous.TryContinueFrom(Unwrap(previous));

    /// <summary>Końcówka filtru istnieje tylko na końcu pliku; wycinek ucięty wcześniej jej nie ma.</summary>
    public int DrainTail(Span<float> destination) =>
        _end == long.MaxValue && _inner is IContinuousDecoder continuous ? continuous.DrainTail(destination) : 0;

    /// <summary>Dekoder pliku pod wycinkiem albo sam dekoder, gdy wycinkiem nie jest.</summary>
    public static IAudioDecoder Unwrap(IAudioDecoder decoder) => decoder is SliceDecoder slice ? slice._inner : decoder;

    public void Dispose() => _inner.Dispose();
}
