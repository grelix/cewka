using System.Collections.Concurrent;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Silnik na atrapach stacji i zapis wszystkich zdarzeń radia.</summary>
internal sealed class LiveRig : IDisposable
{
    private int _started;
    private int _failed;

    public LiveRig(int periodFrames = 1024, int seed = 1234)
    {
        Harness = new EngineHarness(periodFrames, seed);
        Engine.OpenLiveSession = source =>
        {
            var session = new FakeLiveSession(source);
            Sessions.Enqueue(session);
            return session;
        };
        Engine.OpenLiveDecoder = (_, _, _) =>
        {
            var decoder = NextDecoder?.Invoke() ?? new ScriptedLiveDecoder().Feed(long.MaxValue / 2);
            Decoders.Enqueue(decoder);
            return decoder;
        };

        Engine.LiveStarted += _ => Interlocked.Increment(ref _started);
        Engine.LiveStateChanged += (source, state) => States.Enqueue((source, state));
        Engine.LiveTitleChanged += (source, title) => Titles.Enqueue((source, title));
        Engine.LiveFailed += (source, failure) =>
        {
            Failures.Enqueue((source, failure));
            Interlocked.Increment(ref _failed);
        };
        Engine.LiveInfoChanged += (source, info) => Infos.Enqueue((source, info));
    }

    public EngineHarness Harness { get; }
    public PlaybackEngine Engine => Harness.Engine;

    /// <summary>Dekoder następnego otwarcia; bez niego dekoder grający bez końca.</summary>
    public Func<ScriptedLiveDecoder>? NextDecoder { get; set; }

    public ConcurrentQueue<FakeLiveSession> Sessions { get; } = new();
    public ConcurrentQueue<ScriptedLiveDecoder> Decoders { get; } = new();
    public ConcurrentQueue<(LiveSource Source, LiveState State)> States { get; } = new();
    public ConcurrentQueue<(LiveSource Source, string? Title)> Titles { get; } = new();
    public ConcurrentQueue<(LiveSource Source, LiveFailure Failure)> Failures { get; } = new();
    public ConcurrentQueue<(LiveSource Source, LiveStreamInfo Info)> Infos { get; } = new();

    public int Started => Volatile.Read(ref _started);
    public int Failed => Volatile.Read(ref _failed);

    public FakeLiveSession Session => Sessions.Last();

    public ScriptedLiveDecoder Decoder => Decoders.Last();

    public static LiveSource Station(string id = "s1") =>
        new(id, "Stacja " + id, [new Uri("http://127.0.0.1:1/" + id)]);

    public void Dispose() => Harness.Dispose();
}
