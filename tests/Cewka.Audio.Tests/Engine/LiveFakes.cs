using System.Diagnostics.CodeAnalysis;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;
using Cewka.Audio.Tests.Live;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Sesja stacji bez sieci: stany i zatrzymanie widać z testu.</summary>
internal sealed class FakeLiveSession(LiveSource source) : ILiveSession
{
    private int _started;
    private int _stopped;

    public LiveSource Source { get; } = source;
    public FakeByteSource FakeBytes { get; } = new();
    public ILiveByteSource Bytes => FakeBytes;
    public LiveState State { get; private set; } = LiveState.Connecting;
    public LiveFailure? Failure { get; set; }
    public LiveStreamInfo? Info { get; private set; }
    public int Started => Volatile.Read(ref _started);
    public int Stopped => Volatile.Read(ref _stopped);

    public event Action<LiveState>? StateChanged;
    public event Action<LiveStreamInfo>? InfoChanged;

    public void Start() => Interlocked.Increment(ref _started);

    public void Stop() => Interlocked.Increment(ref _stopped);

    public void Dispose() => Stop();

    public void RaiseState(LiveState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void RaiseInfo(LiveStreamInfo info)
    {
        Info = info;
        InfoChanged?.Invoke(info);
    }
}

/// <summary>Dekoder na żywo o stałej wartości próbki; test dokłada ramki, tytuły i koniec.</summary>
internal sealed class ScriptedLiveDecoder(float value = 0.25f, int channels = 2, Func<int, float>? signal = null) : ILiveDecoder
{
    private readonly Lock _lock = new();
    private readonly Queue<(long Frame, StreamMarker Marker)> _markers = new();
    private long _available;
    private long _position;
    private bool _ended;
    private bool _throw;

    public int DisposeCount { get; private set; }
    public int SampleRate => EngineHarness.Rate;
    public int Channels => channels;
    public long TotalFrames => 0;
    public bool CanSeek => false;
    public int SourceSampleRate => SampleRate;
    public int SourceChannels => channels;
    public int InstantaneousBitrate => 128;
    public LiveCodec Codec => LiveCodec.Mp3;
    public LiveFailure? EndFailure { get; private set; }

    public long Position
    {
        get { lock (_lock) return _position; }
    }

    public ScriptedLiveDecoder Feed(long frames)
    {
        lock (_lock) _available += frames;
        return this;
    }

    /// <summary>Tytuł obowiązujący od ramki <paramref name="frame"/>.</summary>
    public ScriptedLiveDecoder Title(string? title, long frame)
    {
        lock (_lock) _markers.Enqueue((frame, new StreamTitle(title)));
        return this;
    }

    /// <summary>Koniec po wydaniu dołożonych już ramek.</summary>
    public void End(LiveFailure? failure = null)
    {
        lock (_lock)
        {
            _ended = true;
            EndFailure = failure;
        }
    }

    public void ThrowOnRead()
    {
        lock (_lock) _throw = true;
    }

    public LiveReadStatus ReadLive(Span<float> destination, out int frames)
    {
        lock (_lock)
        {
            if (_throw) throw new InvalidDataException("Uszkodzona ramka.");

            frames = (int)Math.Min(_available, destination.Length / channels);
            if (frames == 0)
                return _ended ? LiveReadStatus.Ended : LiveReadStatus.Waiting;

            for (var i = 0; i < frames; i++)
                destination.Slice(i * channels, channels).Fill(signal is null ? value : signal((int)(_position + i)));

            _available -= frames;
            _position += frames;
            return LiveReadStatus.Data;
        }
    }

    public bool TryTakeMarker([MaybeNullWhen(false)] out StreamMarker marker, out long outputFrame)
    {
        lock (_lock)
        {
            if (_markers.TryPeek(out var next) && next.Frame <= _position)
            {
                _markers.Dequeue();
                marker = next.Marker;
                outputFrame = next.Frame;
                return true;
            }

            marker = null;
            outputFrame = _position;
            return false;
        }
    }

    public int Read(Span<float> destination)
    {
        ReadLive(destination, out var frames);
        return frames;
    }

    public void Seek(long frame) { }

    public void Dispose() => DisposeCount++;
}
