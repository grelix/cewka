namespace Cewka.Audio.Streaming;

/// <summary>
/// Bufor między siecią a dekoderem: pierścień bajtów ze znacznikami w pozycjach bezwzględnych.
/// Bramka pilnuje, żeby dekoder nie dostawał po kilka bajtów, a zbyt długa kolejka traci
/// najstarszy dźwięk, bo opóźnienie względem anteny nie może rosnąć bez końca.
/// </summary>
internal sealed class CompressedBuffer(LiveOptions options) : ILiveByteSource
{
    private const int DefaultKbps = 128;
    private const int MinPlausibleKbps = 8;
    private const int MaxPlausibleKbps = 1500;

    private readonly Lock _lock = new();
    private readonly byte[] _ring = new byte[options.HardLimitBytes];
    private Queue<(long Position, StreamMarker Marker)> _markers = new();
    private long _written;
    private long _read;
    private long _dropped;
    private bool _gateOpen;
    private bool _everOpened;
    private bool _completed;
    private bool _rejecting;
    private LiveFailure? _failure;
    private int _measuredKbps;
    private int _headerKbps;
    private LiveCodec _codec;
    private DateTimeOffset? _lastWrite;

    /// <summary>Otwarcie i zamknięcie bramki; poza blokadą, odbiornik nie blokuje.</summary>
    public event Action<bool>? GateChanged;

    /// <summary>Czytelnik nie umie zdekodować bieżącego połączenia. Z wątku czytelnika; odbiornik nie blokuje.</summary>
    public event Action<LiveFailure>? Undecodable;

    public LiveFailure? Failure
    {
        get { lock (_lock) return _failure; }
    }

    public double BufferedSeconds
    {
        get { lock (_lock) return (double)(_written - _read) / BytesPerSecond; }
    }

    public long DroppedBytes
    {
        get { lock (_lock) return _dropped; }
    }

    public bool IsGateOpen
    {
        get { lock (_lock) return _gateOpen; }
    }

    /// <summary>Czas ostatniego zapisu dźwięku wg zegara opcji; po uśpieniu bufor jest przeterminowany.</summary>
    public DateTimeOffset? LastWrite
    {
        get { lock (_lock) return _lastWrite; }
    }

    /// <summary>Na bajty liczy się też częstość tytułów.</summary>
    public int AudioBytesPerSecond
    {
        get { lock (_lock) return BytesPerSecond; }
    }

    /// <summary>Stacje podają przepływność także w bitach; taka wartość to brak wiedzy, a nie 16 MB/s.</summary>
    public static bool IsPlausibleBitrate(int kilobits) => kilobits is >= MinPlausibleKbps and <= MaxPlausibleKbps;

    private int BytesPerSecond =>
        (_measuredKbps > 0 ? _measuredKbps : _headerKbps > 0 ? _headerKbps : DefaultKbps) * 1000 / 8;

    private long OverflowLimit => Math.Min((long)(options.MaxSeconds * BytesPerSecond), _ring.Length);

    public void BeginStream(LiveCodec codec, bool discardPending)
    {
        var closed = false;
        lock (_lock)
        {
            if (_completed)
                return;

            _rejecting = false;

            // Inny adres to inna przepływność: progi nowego połączenia nie mogą czekać na starą.
            _measuredKbps = 0;
            _headerKbps = 0;

            if (discardPending)
            {
                _dropped += _written - _read;
                _read = _written;
                _markers.Clear();
                closed = _gateOpen;
                _gateOpen = false;
            }

            _markers.Enqueue((_written, new StreamBegin(codec)));
        }

        if (closed)
            GateChanged?.Invoke(false);
    }

    public void Write(ReadOnlySpan<byte> audio)
    {
        var opened = false;
        lock (_lock)
        {
            if (_completed || _rejecting)
                return;

            if (audio.Length > _ring.Length)
            {
                _dropped += audio.Length - _ring.Length;
                audio = audio[^_ring.Length..];
            }

            var start = (int)(_written % _ring.Length);
            var first = Math.Min(audio.Length, _ring.Length - start);
            audio[..first].CopyTo(_ring.AsSpan(start));
            audio[first..].CopyTo(_ring);
            _written += audio.Length;
            _lastWrite = options.Time.GetUtcNow();

            if (_written - _read > OverflowLimit)
                JumpToTarget();

            if (!_gateOpen && _written - _read >= OpenThreshold)
            {
                _gateOpen = true;
                _everOpened = true;
                opened = true;
            }
        }

        if (opened)
            GateChanged?.Invoke(true);
    }

    private long OpenThreshold
    {
        get
        {
            var (seconds, minimum) = _everOpened
                ? (options.RebufferSeconds, options.MinRebufferBytes)
                : (options.StartSeconds, options.MinStartBytes);
            return Math.Min(Math.Max((long)(seconds * BytesPerSecond), minimum), OverflowLimit);
        }
    }

    /// <summary>Głowa przeskakuje do docelowego zapasu; dekoder dostaje nieciągłość i ostatni pominięty tytuł.</summary>
    private void JumpToTarget()
    {
        var target = Math.Min((long)(options.TargetSeconds * BytesPerSecond), OverflowLimit);
        var newRead = _written - target;
        _dropped += newRead - _read;
        _read = newRead;

        StreamTitle? skippedTitle = null;
        while (_markers.TryPeek(out var head) && head.Position <= newRead)
        {
            _markers.Dequeue();
            if (head.Marker is StreamTitle title)
                skippedTitle = title;
            else if (head.Marker is StreamBegin begin)
                _codec = begin.Codec;
        }

        var markers = new Queue<(long, StreamMarker)>(_markers.Count + 2);
        markers.Enqueue((newRead, new StreamBegin(_codec)));
        if (skippedTitle is not null)
            markers.Enqueue((newRead, skippedTitle));
        foreach (var marker in _markers)
            markers.Enqueue(marker);
        _markers = markers;
    }

    public void AddTitle(string? title)
    {
        lock (_lock)
        {
            if (!_completed && !_rejecting)
                _markers.Enqueue((_written, new StreamTitle(title)));
        }
    }

    public void SetBitrate(int kilobits)
    {
        lock (_lock)
            _headerKbps = IsPlausibleBitrate(kilobits) ? kilobits : 0;
    }

    public void ReportBitrate(int kilobits)
    {
        lock (_lock)
        {
            // Znacznik początku jeszcze nieprzeczytany: pomiar dotyczy połączenia, które się skończyło.
            if (_markers.Any(marker => marker.Marker is StreamBegin))
                return;

            _measuredKbps = IsPlausibleBitrate(kilobits) ? kilobits : 0;
        }
    }

    /// <summary>Śmieci ze starszego połączenia wystarczy pominąć; z bieżącego decyduje właściciel bufora.</summary>
    public bool ReportUndecodable(LiveFailure failure)
    {
        lock (_lock)
        {
            if (_completed || Undecodable is null)
                return false;

            if (_markers.Any(marker => marker.Marker is StreamBegin))
            {
                while (_markers.Peek().Marker is not StreamBegin)
                    _markers.Dequeue();
                DropTo(_markers.Peek().Position);
                return true;
            }

            DropTo(_written);
            _markers.Clear();
            _rejecting = true;
        }

        Undecodable.Invoke(failure);
        return true;
    }

    private void DropTo(long position)
    {
        _dropped += position - _read;
        _read = position;
    }

    /// <summary>Koniec strumienia: czytelnik dostaje resztę, potem Ended. Pierwsza przyczyna obowiązuje.</summary>
    public void Complete(LiveFailure? failure)
    {
        var opened = false;
        lock (_lock)
        {
            if (_completed)
                return;
            _completed = true;
            _failure = failure;
            opened = !_gateOpen;
            _gateOpen = true;
        }

        if (opened)
            GateChanged?.Invoke(true);
    }

    public LiveReadStatus Read(Span<byte> destination, out int bytes, out StreamMarker? marker)
    {
        bytes = 0;
        marker = null;
        var closed = false;
        LiveReadStatus status;
        lock (_lock)
        {
            if (!_gateOpen)
                return LiveReadStatus.Waiting;

            if (_markers.TryPeek(out var head) && head.Position <= _read)
            {
                _markers.Dequeue();
                marker = head.Marker;
                if (marker is StreamBegin begin)
                    _codec = begin.Codec;
                return LiveReadStatus.Data;
            }

            var available = _written - _read;
            if (available == 0)
            {
                if (_completed)
                    return LiveReadStatus.Ended;

                _gateOpen = false;
                closed = true;
                status = LiveReadStatus.Waiting;
            }
            else
            {
                if (_markers.TryPeek(out head))
                    available = Math.Min(available, head.Position - _read);
                bytes = (int)Math.Min(available, destination.Length);

                var start = (int)(_read % _ring.Length);
                var first = Math.Min(bytes, _ring.Length - start);
                _ring.AsSpan(start, first).CopyTo(destination);
                _ring.AsSpan(0, bytes - first).CopyTo(destination[first..]);
                _read += bytes;
                status = LiveReadStatus.Data;
            }
        }

        if (closed)
            GateChanged?.Invoke(false);
        return status;
    }
}
