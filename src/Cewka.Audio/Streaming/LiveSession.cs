using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Channels;

namespace Cewka.Audio.Streaming;

public sealed record LiveStreamInfo(
    LiveCodec Codec, int? HeaderBitrateKbps, string? IcyName, string? IcyGenre, Uri FinalUrl, bool HasMetadata);

internal interface ILiveSession : IDisposable
{
    ILiveByteSource Bytes { get; }
    LiveState State { get; }
    LiveFailure? Failure { get; }
    LiveStreamInfo? Info { get; }

    /// <summary>Kolejno, z jednej pętli zdarzeń sesji.</summary>
    event Action<LiveState>? StateChanged;
    event Action<LiveStreamInfo>? InfoChanged;

    void Start();

    /// <summary>Nie blokuje.</summary>
    void Stop();
}

/// <summary>
/// Jedno słuchanie stacji: łączy, wypełnia bufor i po zerwaniu ponawia. Bufor gra dalej w czasie
/// ponawiania, więc krótka przerwa w sieci może nie być słyszalna.
/// </summary>
internal sealed class LiveSession : ILiveSession, IIcySink
{
    private const int ChunkBytes = 8 * 1024;

    /// <summary>Część przepływności, poniżej której strumień liczy się jako zastój.</summary>
    private const double MinStreamRate = 0.05;

    private readonly LiveSource _source;
    private readonly LiveOptions _options;
    private readonly CompressedBuffer _buffer;
    private readonly StreamHttp _http;
    private readonly StreamOpener _opener;
    private readonly RetryPolicy _retry;
    private readonly Encoding _titleFallback;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _stateLock = new();
    private LiveState _state = LiveState.Connecting;
    private LiveFailure? _failure;
    private LiveStreamInfo? _info;
    private bool _connected;
    private bool _everConnected;
    private LiveFailure? _undecodable;
    private int _started;
    private int _stopRequested;
    private OpenedStream? _current;
    private TaskCompletionSource? _wake;
    private DateTimeOffset _nextWakeAllowed;

    // Tytuły: tylko wątek zadania sesji.
    private string? _lastTitle;
    private string? _pendingTitle;
    private bool _hasPendingTitle;
    private long _audioSinceTitle = long.MaxValue / 2;
    private long _audioSinceProgress;

    public LiveSession(LiveSource source, LiveOptions options)
    {
        if (source.Urls.Count == 0)
            throw new ArgumentException("Stacja bez adresu", nameof(source));

        _source = source;
        _options = options;
        _buffer = new CompressedBuffer(options);
        _buffer.GateChanged += _ => OnGateChanged();
        _buffer.Undecodable += OnUndecodable;
        _http = new StreamHttp(options);
        _titleFallback = IcyMetadataParser.FallbackFor(source.CountryCode);
        _opener = new StreamOpener(_http, new AddressPolicy(options), options, _titleFallback);
        _retry = new RetryPolicy(options, source.Urls.Count);
    }

    public ILiveByteSource Bytes => _buffer;

    /// <summary>Kończy się po zamknięciu połączenia i po dostarczeniu ostatniego zdarzenia.</summary>
    public Task Completion => _completion.Task;

    public LiveState State
    {
        get { lock (_stateLock) return _state; }
    }

    public LiveFailure? Failure
    {
        get { lock (_stateLock) return _failure; }
    }

    public LiveStreamInfo? Info
    {
        get { lock (_stateLock) return _info; }
    }

    public event Action<LiveState>? StateChanged;
    public event Action<LiveStreamInfo>? InfoChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _events.Writer.TryWrite(() => StateChanged?.Invoke(LiveState.Connecting));
        _ = Task.Run(EventLoopAsync);
        _ = Task.Run(RunAsync);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return;

        SetState(LiveState.Stopped);
        _buffer.Complete(null);
        _ = _stop.CancelAsync();

        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _events.Writer.TryComplete();
            _http.Dispose();
            _ = Task.Run(EventLoopAsync);
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => Volatile.Read(ref _current)?.Dispose());
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Tylko przerywa czekanie na kolejną próbę: po zmianie sieci warto spróbować od razu. Zmiany
    /// przychodzą seriami, więc liczy się pierwsza; czas podany przez serwer obowiązuje mimo nich.
    /// </summary>
    internal void NotifyNetworkChanged()
    {
        var now = _options.Time.GetUtcNow();
        lock (_stateLock)
        {
            if (_wake is null || now < _nextWakeAllowed)
                return;

            _nextWakeAllowed = now + _options.NetworkWakeInterval;
            _wake.TrySetResult();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => NotifyNetworkChanged();

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Exception error;
                try
                {
                    await ConnectAndPumpAsync(token).ConfigureAwait(false);
                    return;
                }
                catch (Exception e)
                {
                    error = e;
                }

                if (token.IsCancellationRequested)
                    return;

                var (failure, permanent) = TakeUndecodable() is { } undecodable
                    ? (undecodable, true)
                    : LiveErrors.Classify(error);
                var retryAfter = (error as LiveOpenException)?.RetryAfter;
                var decision = _retry.OnFailure(failure, permanent, retryAfter);
                OnDisconnected();
                if (decision.Action == RetryAction.GiveUp)
                {
                    Fail(decision.Failure);
                    return;
                }

                await WaitBeforeRetryAsync(decision.Delay, wakeOnNetworkChange: retryAfter is null, token).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // Inaczej czytelnik czekałby bez końca na dane, których nikt już nie dostarczy.
            Fail(new LiveFailure(LiveFailureReason.Unspecified, Detail: e.Message));
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _http.Dispose();
            _events.Writer.TryComplete();
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken token)
    {
        var url = _source.Urls[_retry.CurrentUrl];
        using var opened = await _opener.OpenAsync(url, _source.AllowLocalAddress, token).ConfigureAwait(false);
        Volatile.Write(ref _current, opened);
        try
        {
            token.ThrowIfCancellationRequested();

            var slept = _buffer.LastWrite is { } last && _options.Time.GetUtcNow() - last > TimeSpan.FromSeconds(_options.MaxSeconds);
            _buffer.BeginStream(opened.Codec, slept);
            if (opened.Headers.BitrateKbps is { } kilobits)
                _buffer.SetBitrate(kilobits);
            Publish(new LiveStreamInfo(opened.Codec, opened.Headers.BitrateKbps, opened.Headers.Name, opened.Headers.Genre,
                opened.FinalUrl, opened.Demuxer is not null));
            OnConnected();

            ReplayHead(opened);
            await PumpAsync(opened, token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _current, null);
        }
    }

    private void ReplayHead(OpenedStream opened)
    {
        var at = 0;
        foreach (var block in opened.HeadMetadata)
        {
            OnAudio(opened.Head.AsSpan(at, block.Offset - at));
            OnMetadata(block.Block);
            at = block.Offset;
        }

        OnAudio(opened.Head.AsSpan(at));
        if (opened.Demuxer is { } demuxer)
            demuxer.Sink = this;
    }

    private async Task PumpAsync(OpenedStream opened, CancellationToken token)
    {
        var chunk = new byte[ChunkBytes];
        var stallTimeout = opened.StallTimeout ?? _options.StallTimeout;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(stallTimeout);
        _audioSinceProgress = 0;
        while (true)
        {
            int read;
            try
            {
                read = await opened.Body.ReadAsync(chunk, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.Stalled));
            }

            if (read == 0)
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.StreamEnded, Detail: "Serwer zamknął połączenie"));

            if (opened.Demuxer is { } demuxer)
                demuxer.Push(chunk.AsSpan(0, read));
            else
                OnAudio(chunk.AsSpan(0, read));

            // Limit odnawia się dopiero po porcji dźwięku: pojedyncze bajty nie odsuwają zastoju.
            if (_audioSinceProgress >= stallTimeout.TotalSeconds * _buffer.AudioBytesPerSecond * MinStreamRate)
            {
                _audioSinceProgress = 0;
                stall.CancelAfter(stallTimeout);
            }
        }
    }

    private async Task WaitBeforeRetryAsync(TimeSpan delay, bool wakeOnNetworkChange, CancellationToken token)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
            _wake = wakeOnNetworkChange ? wake : null;

        await Task.WhenAny(Task.Delay(delay, token), wake.Task).ConfigureAwait(false);

        lock (_stateLock)
            _wake = null;

        // Ta próba nie była zaplanowana, więc jej porażka nie zbliża do limitu.
        if (wake.Task.IsCompleted)
            _retry.ForgiveNextFailure();
    }

    public void OnAudio(ReadOnlySpan<byte> audio)
    {
        _audioSinceTitle += audio.Length;
        _audioSinceProgress += audio.Length;
        if (_hasPendingTitle && _audioSinceTitle >= _buffer.AudioBytesPerSecond)
            EmitTitle(_pendingTitle);
        _buffer.Write(audio);
    }

    /// <summary>Znacznik tylko przy zmianie i najwyżej jeden na sekundę dźwięku, liczoną w bajtach.</summary>
    public void OnMetadata(ReadOnlySpan<byte> block)
    {
        var fields = IcyMetadataParser.Parse(block, _titleFallback);
        if (fields.IsAd || fields.Title is null)
            return;

        var title = IcyMetadataParser.Normalise(fields.Title, _source.Name);
        if (title == _lastTitle)
        {
            _hasPendingTitle = false;
        }
        else if (_audioSinceTitle >= _buffer.AudioBytesPerSecond)
        {
            EmitTitle(title);
        }
        else
        {
            _pendingTitle = title;
            _hasPendingTitle = true;
        }
    }

    private void EmitTitle(string? title)
    {
        _buffer.AddTitle(title);
        _lastTitle = title;
        _hasPendingTitle = false;
        _audioSinceTitle = 0;
    }

    private void OnConnected()
    {
        var gateOpen = _buffer.IsGateOpen;
        lock (_stateLock)
        {
            _connected = true;
            _everConnected = true;
            _undecodable = null;
        }

        if (gateOpen)
            _retry.OnPlaying();
        SetState(gateOpen ? LiveState.Playing : LiveState.Buffering);
    }

    private void OnDisconnected()
    {
        bool everConnected;
        lock (_stateLock)
        {
            _connected = false;
            everConnected = _everConnected;
        }

        if (everConnected)
            SetState(LiveState.Reconnecting);
    }

    /// <summary>Bierze bramkę taką, jaka jest teraz: zgłoszenia z dwóch wątków mogą przyjść nie po kolei.</summary>
    internal void OnGateChanged()
    {
        lock (_stateLock)
        {
            // Rozłączenie w tym samym czasie: stan ponawiania nie może przejść w Rebuffering.
            if (!_connected)
                return;

            var open = _buffer.IsGateOpen;

            // Otwarcie przychodzi z zadania sesji, zamknięcie także z wątku czytelnika.
            if (open)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                    return;
                _retry.OnPlaying();
            }

            SetState(open ? LiveState.Playing : LiveState.Rebuffering);
        }
    }

    /// <summary>Wątek czytelnika. Zerwanie jak przy zatrzymaniu; dalej idzie zwykła droga porażki stałej.</summary>
    private void OnUndecodable(LiveFailure failure)
    {
        lock (_stateLock)
        {
            if (!_connected)
                return;
            _undecodable = failure;
        }

        ThreadPool.QueueUserWorkItem(_ => Volatile.Read(ref _current)?.Dispose());
    }

    private LiveFailure? TakeUndecodable()
    {
        lock (_stateLock)
        {
            var failure = _undecodable;
            _undecodable = null;
            return failure;
        }
    }

    private void Fail(LiveFailure failure)
    {
        lock (_stateLock)
            _failure = failure;
        _buffer.Complete(failure);
        SetState(LiveState.Failed);
    }

    private void SetState(LiveState state)
    {
        lock (_stateLock)
        {
            if (_state == state || _state is LiveState.Failed or LiveState.Stopped)
                return;
            _state = state;
            _events.Writer.TryWrite(() => StateChanged?.Invoke(state));
        }
    }

    private void Publish(LiveStreamInfo info)
    {
        lock (_stateLock)
        {
            if (info == _info)
                return;
            _info = info;
            _events.Writer.TryWrite(() => InfoChanged?.Invoke(info));
        }
    }

    private async Task EventLoopAsync()
    {
        await foreach (var raise in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                raise();
            }
            catch (Exception)
            {
                // Błąd odbiorcy zdarzenia nie może zatrzymać sesji ani kolejnych zdarzeń.
            }
        }

        _completion.TrySetResult();
    }
}
