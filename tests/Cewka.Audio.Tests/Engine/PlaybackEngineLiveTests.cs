using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Stacja w silniku na atrapach sesji i dekodera: zdarzenia, polecenia, urządzenie.</summary>
public class PlaybackEngineLiveTests
{
    private const float A = 0.25f;
    private const float B = 0.5f;

    // ---------- start ----------

    [Fact]
    public void StartReportsOnceWithoutIndexOrDuration()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.PumpPeriods(20);

        Assert.Equal(1, rig.Started);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal("s1", rig.Engine.CurrentLive!.StationId);
        Assert.Equal(-1, rig.Engine.CurrentIndex);
        Assert.Equal(TimeSpan.Zero, rig.Engine.Duration);
        Assert.True(rig.Engine.Position > TimeSpan.Zero);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Equal(LiveCodec.Mp3, rig.Engine.LiveCodec);
        Assert.Equal(128, rig.Engine.InstantaneousBitrate);
        Assert.Empty(h.TrackChanges);
        Assert.Equal(1, rig.Session.Started);
    }

    /// <summary>Pierwszy dźwięk ma minąć wyjście; samo otwarcie dekodera to jeszcze nie start.</summary>
    [Fact]
    public void StartWaitsForTheFirstSoundToPassTheOutput()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder() };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(5);
        Assert.Equal(0, rig.Started);

        rig.Decoder.Feed(4096);
        EngineHarness.WaitUntil(() => rig.Engine.BufferedFrames > 0);
        Thread.Sleep(30);
        Assert.Equal(0, rig.Started);

        h.PumpUntil(() => rig.Started > 0);
    }

    /// <summary>Brak danych w strumieniu to czekanie, a nie koniec kolejki ani błąd pliku.</summary>
    [Fact]
    public void LongWaitingIsNeitherTheEndOfTheQueueNorAFailure()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder() };
        var h = rig.Harness;
        h.Engine.Enqueue([h.Track("a", 0.3, A)]);

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpPeriods(40);

        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Equal(0, h.Finished);
        Assert.Empty(h.Failures);
        Assert.Equal(0, rig.Failed);
        Assert.Equal(0, rig.Started);
    }

    /// <summary>Plik gra, stacja go zastępuje przez wyciszenie, a kolejka zostaje nietknięta.</summary>
    [Fact]
    public void StationFadesTheFileOutAndLeavesTheQueue()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(B).Feed(long.MaxValue / 2) };
        var h = rig.Harness;
        var a = h.Track("a", 30, A);
        var b = h.Track("b", 30, A);
        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => EngineHarness.CountFrames(h.Output.Samples, A) > 8192);

        rig.Engine.PlayLive(LiveRig.Station());
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => rig.Started > 0 && EngineHarness.CountFrames(h.Output.Samples, B) > 4096);

        Assert.Equal([a, b], h.Engine.QueuePaths);
        Assert.Equal(-1, h.Engine.CurrentIndex);
        Assert.True(Signals.LargestStep(h.Output.Samples, 0) < 0.02, "skok próbki przy zmianie źródła");
    }

    [Fact]
    public void DeviceThatCannotOpenThrowsAndLeavesNoStation()
    {
        using var engine = new PlaybackEngine((_, _) => throw new AudioException("Brak urządzenia."));

        Assert.Throws<AudioException>(() => engine.PlayLive(LiveRig.Station()));

        Assert.False(engine.IsLive);
        Assert.Null(engine.LiveState);
    }

    // ---------- zdarzenia sesji ----------

    [Fact]
    public void SessionStateAndInfoReachTheEngine()
    {
        using var rig = new LiveRig();
        var info = new LiveStreamInfo(LiveCodec.Mp3, 128, "Nazwa", null, new Uri("http://127.0.0.1:1/"), true);

        rig.Engine.PlayLive(LiveRig.Station());
        rig.Session.RaiseState(LiveState.Buffering);
        rig.Session.RaiseInfo(info);

        Assert.Equal(LiveState.Buffering, rig.Engine.LiveState);
        Assert.Equal(info, rig.Engine.LiveInfo);
        EngineHarness.WaitUntil(() => rig.States.Any(change => change.State == LiveState.Buffering));
        EngineHarness.WaitUntil(() => rig.Infos.Any(change => change.Info == info));
    }

    /// <summary>Stara stacja, która właśnie się łączyła, nie może już nic zgłosić.</summary>
    [Fact]
    public void ChangingStationWhileConnectingSilencesTheOldOne()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station("stara"));
        var old = rig.Session;
        rig.Engine.PlayLive(LiveRig.Station("nowa"));
        var fresh = rig.Session;

        old.RaiseState(LiveState.Failed);
        old.RaiseInfo(new LiveStreamInfo(LiveCodec.Aac, null, null, null, new Uri("http://127.0.0.1:1/"), false));
        fresh.RaiseState(LiveState.Playing);

        Assert.Equal(1, old.Stopped);
        Assert.Equal(0, fresh.Stopped);
        Assert.Equal("nowa", rig.Engine.CurrentLive!.StationId);
        Assert.Equal(LiveState.Playing, rig.Engine.LiveState);
        Assert.DoesNotContain(rig.States, change => change.Source.StationId == "stara");
        Assert.Empty(rig.Infos);

        h.PumpUntil(() => rig.Started > 0);
        Assert.Equal(1, rig.Started);
    }

    // ---------- tytuły ----------

    /// <summary>Tytuł z ramki N nie może wyprzedzić dźwięku, który czeka jeszcze w pierścieniu.</summary>
    [Fact]
    public void TitleWaitsForItsFrameToPassTheOutput()
    {
        const int titleFrame = 12000;
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(B).Feed(20000).Title("Utwór", titleFrame) };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position == 20000);
        Thread.Sleep(30);
        Assert.Empty(rig.Titles);
        Assert.Null(rig.Engine.LiveTitle);

        h.PumpPeriods(6);
        Thread.Sleep(30);
        Assert.Empty(rig.Titles);

        h.PumpUntil(() => !rig.Titles.IsEmpty);

        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= titleFrame - 1024);
        Assert.Equal("Utwór", rig.Titles.Single().Title);
        Assert.Equal("s1", rig.Titles.Single().Source.StationId);
        Assert.Equal("Utwór", rig.Engine.LiveTitle);
        Assert.Equal(-1, rig.Engine.CurrentIndex);
        Assert.Empty(h.TrackChanges);
    }

    [Fact]
    public void StationThatStopsNamingTheSongClearsTheTitle()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2).Title("Utwór", 100).Title(null, 8000),
        };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Titles.Count == 2);

        Assert.Null(rig.Engine.LiveTitle);
        Assert.Null(rig.Titles.Last().Title);
    }

    /// <summary>Tytuł czekający w pierścieniu starej stacji przepada razem z nim.</summary>
    [Fact]
    public void TitleOfThePreviousStationNeverArrives()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2).Title("Stary", 5000),
        };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station("stara"));
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position > 5000);
        rig.NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2);

        rig.Engine.PlayLive(LiveRig.Station("nowa"));
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => rig.Started > 0);
        h.PumpPeriods(30);

        Assert.Empty(rig.Titles);
        Assert.Null(rig.Engine.LiveTitle);
    }

    /// <summary>Tytuł czekający na swoją koleję za wolnym odbiorcą traci ważność razem ze stacją.</summary>
    [Fact]
    public void TitleWaitingBehindASlowReceiverDoesNotArriveAfterTheChange()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2).Title("pierwszy", 100).Title("drugi", 6000),
        };
        var h = rig.Harness;
        using var release = new ManualResetEventSlim();
        rig.Engine.LiveTitleChanged += (_, title) =>
        {
            if (title == "pierwszy") release.Wait(TimeSpan.FromSeconds(10));
        };

        rig.Engine.PlayLive(LiveRig.Station("stara"));
        h.PumpUntil(() => rig.Engine.LiveTitle == "drugi");

        rig.NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2);
        rig.Engine.PlayLive(LiveRig.Station("nowa"));
        release.Set();
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => rig.Started > 0 || rig.Titles.Count > 1);
        Thread.Sleep(30);

        Assert.DoesNotContain(rig.Titles, change => change.Title == "drugi");
    }

    /// <summary>Wolny odbiorca nie może zamienić kolejności: po starym tytule zostałby na ekranie stary.</summary>
    [Fact]
    public void SlowReceiverGetsTheEventsInTheOrderTheyHappened()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2).Title("pierwszy", 100).Title("drugi", 6000),
        };
        var h = rig.Harness;
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        rig.Engine.LiveTitleChanged += (_, title) =>
        {
            if (title == "pierwszy") Thread.Sleep(100);
            seen.Enqueue(title);
        };

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => seen.Count == 2);

        Assert.Equal(["pierwszy", "drugi"], seen);
    }

    // ---------- koniec i porażka ----------

    /// <summary>Porażka czeka, aż bufor wybrzmi; plik z kolejki nie zaczyna grać sam.</summary>
    [Fact]
    public void FailureIsReportedOnceAfterTheBufferHasPlayed()
    {
        var failure = new LiveFailure(LiveFailureReason.StreamEnded);
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(B).Feed(20000) };
        var h = rig.Harness;
        h.Engine.Enqueue([h.Track("a", 30, A)]);

        rig.Engine.PlayLive(LiveRig.Station());
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position == 20000);
        rig.Decoder.End(failure);
        Thread.Sleep(30);
        Assert.Equal(0, rig.Failed);

        h.PumpUntil(() => rig.Failed > 0);
        h.PumpPeriods(10);

        Assert.Equal(1, rig.Failed);
        Assert.Equal(failure, rig.Failures.Single().Failure);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 20000 - 1024);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
        Assert.Equal(1, rig.Session.Stopped);
        Assert.Equal(0, h.Finished);
        Assert.Empty(h.Failures);
        Assert.Empty(h.Opened);
        Assert.Equal(-1, rig.Engine.CurrentIndex);
    }

    /// <summary>Sesja poddała się, ale bufor jeszcze gra: interfejs nie może pokazać błędu nad dźwiękiem.</summary>
    [Fact]
    public void SessionFailureShowsOnlyAfterTheBufferHasPlayed()
    {
        var failure = new LiveFailure(LiveFailureReason.NotFound);
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(B).Feed(20000) };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position == 20000);
        rig.Session.RaiseState(LiveState.Reconnecting);
        rig.Session.RaiseState(LiveState.Failed);
        EngineHarness.WaitUntil(() => rig.States.Any(change => change.State == LiveState.Reconnecting));
        Thread.Sleep(30);

        Assert.Equal(LiveState.Reconnecting, rig.Engine.LiveState);
        Assert.DoesNotContain(rig.States, change => change.State == LiveState.Failed);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);

        rig.Decoder.End(failure);
        h.PumpUntil(() => rig.Failed > 0);

        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
        Assert.Single(rig.States, change => change.State == LiveState.Failed);
    }

    [Fact]
    public void CodecWithoutADecoderIsReportedWithItsReason()
    {
        var failure = new LiveFailure(LiveFailureReason.CodecNotSupportedYet, Codec: LiveCodec.Aac);
        using var rig = new LiveRig
        {
            NextDecoder = () =>
            {
                var decoder = new ScriptedLiveDecoder();
                decoder.End(failure);
                return decoder;
            },
        };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Failed > 0);

        Assert.Equal(failure, rig.Failures.Single().Failure);
        Assert.Equal(0, rig.Started);
        Assert.Equal(0, h.Finished);
    }

    [Fact]
    public void ExceptionInTheDecoderEndsTheStationWithoutAFileFailure()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.Enqueue([h.Track("a", 30, A)]);

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        rig.Decoder.ThrowOnRead();
        h.PumpUntil(() => rig.Failed > 0);
        h.PumpPeriods(5);

        Assert.Equal(LiveFailureReason.UndecodableData, rig.Failures.Single().Failure.Reason);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Decoder.DisposeCount > 0);
        Assert.Empty(h.Failures);
        Assert.Empty(h.Opened);
        Assert.Equal(0, h.Finished);
    }

    [Fact]
    public void DecoderThatCannotOpenEndsTheStationWithoutAFileFailure()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.OpenLiveDecoder = (_, _, _) => throw new InvalidOperationException("Brak biblioteki.");

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Failed > 0);

        Assert.Equal(LiveFailureReason.Unspecified, rig.Failures.Single().Failure.Reason);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Empty(h.Failures);
        Assert.Equal(0, h.Finished);
    }

    /// <summary>Zwykły koniec, bez przyczyny: pauza bez komunikatu.</summary>
    [Fact]
    public void EndWithoutAReasonPausesSilently()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder().Feed(4096) };
        var h = rig.Harness;

        rig.Engine.PlayLive(LiveRig.Station());
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position == 4096);
        rig.Decoder.End();

        h.PumpUntil(() => rig.Session.Stopped > 0);

        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Equal(LiveState.Stopped, rig.Engine.LiveState);
        Assert.Equal(0, rig.Failed);
    }

    // ---------- pauza i urządzenie ----------

    /// <summary>Pauza w radiu to rozłączenie; wznowienie łączy od bieżącego miejsca transmisji.</summary>
    [Fact]
    public void PauseDisconnectsAndPlayConnectsAgain()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        var first = rig.Session;

        rig.Engine.Pause();

        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(1, first.Stopped);
        Assert.Equal(LiveState.Stopped, rig.Engine.LiveState);
        EngineHarness.WaitUntil(() => rig.States.Any(change => change.State == LiveState.Stopped));
        EngineHarness.WaitUntil(() => !rig.Engine.IsSeeking);
        Assert.Single(rig.Decoders);

        rig.Engine.Play();
        h.PumpUntil(() => rig.Started > 1);

        Assert.Equal(2, rig.Sessions.Count);
        Assert.NotSame(first, rig.Session);
        Assert.Equal(1, rig.Session.Started);
        Assert.Equal(0, rig.Session.Stopped);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
    }

    [Fact]
    public void TogglePlayDisconnectsAndConnects()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.TogglePlay();
        Assert.Equal(1, rig.Session.Stopped);

        rig.Engine.TogglePlay();
        h.PumpUntil(() => rig.Started > 1);
        Assert.Equal(2, rig.Sessions.Count);
    }

    [Fact]
    public void PlayAfterAFailureTriesAgain()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () =>
            {
                var decoder = new ScriptedLiveDecoder();
                decoder.End(new LiveFailure(LiveFailureReason.ConnectionFailed));
                return decoder;
            },
        };
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Failed > 0);

        rig.NextDecoder = () => new ScriptedLiveDecoder().Feed(long.MaxValue / 2);
        rig.Engine.Play();
        Assert.Equal(LiveState.Connecting, rig.Engine.LiveState);
        h.PumpUntil(() => rig.Started > 0);

        Assert.Equal(2, rig.Sessions.Count);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Equal(1, rig.Failed);
    }

    /// <summary>Awaria samego wyjścia nie może zostawić otwartego połączenia z nikim, kto go słucha.</summary>
    [Fact]
    public void OutputThatWillNotStartDropsTheConnection()
    {
        using var rig = new LiveRig();
        rig.Engine.Initialise();
        rig.Harness.Output.FailStart = true;

        Assert.Throws<AudioException>(() => rig.Engine.PlayLive(LiveRig.Station()));

        Assert.Equal(1, rig.Session.Stopped);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);

        rig.Harness.Output.FailStart = false;
        rig.Engine.Play();
        rig.Harness.PumpUntil(() => rig.Started > 0);
        Assert.Equal(2, rig.Sessions.Count);
    }

    /// <summary>Błąd zatrzymania wyjścia nie może zabić procesu ani zostawić połączenia bez słuchacza.</summary>
    [Fact]
    public void OutputThatWillNotStopAtTheEndOfTheStationStillEndsIt()
    {
        var failure = new LiveFailure(LiveFailureReason.StreamEnded);
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder().Feed(4096) };
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        EngineHarness.WaitUntil(() => rig.Decoders.Count > 0 && rig.Decoder.Position == 4096);
        h.Output.FailStop = true;

        rig.Decoder.End(failure);
        h.PumpUntil(() => rig.Failed > 0);

        Assert.Equal(failure, rig.Failures.Single().Failure);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
        Assert.Equal(1, rig.Session.Stopped);
    }

    [Fact]
    public void OutputThatWillNotStopStillPausesTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Output.FailStop = true;

        rig.Engine.Pause();

        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(1, rig.Session.Stopped);
    }

    [Fact]
    public void OutputThatWillNotStopStillEndsTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Output.FailStop = true;

        rig.Engine.StopLive();

        Assert.False(rig.Engine.IsLive);
        Assert.Equal(PlaybackState.Stopped, rig.Engine.State);
        Assert.Equal(1, rig.Session.Stopped);
    }

    /// <summary>Poprzednia sesja nie ma już właściciela także wtedy, gdy wyjście nie rusza dla następnej.</summary>
    [Fact]
    public void OutputThatWillNotStartForTheNextStationStillStopsThePreviousOne()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station("a"));
        h.PumpUntil(() => rig.Started > 0);
        var previous = rig.Session;
        h.Output.Stop();
        h.Output.FailStart = true;

        Assert.Throws<AudioException>(() => rig.Engine.PlayLive(LiveRig.Station("b")));

        Assert.Equal(1, previous.Stopped);
        Assert.Equal(1, rig.Session.Stopped);
    }

    [Fact]
    public void OutputThatWillNotStartForAFileStillStopsTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.Enqueue([h.Track("a", 30, A)]);
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Output.Stop();
        h.Output.FailStart = true;

        Assert.Throws<AudioException>(() => rig.Engine.PlayIndex(0));

        Assert.Equal(1, rig.Session.Stopped);
        Assert.False(rig.Engine.IsLive);
    }

    [Fact]
    public void SwitchingDeviceConnectsAgainAndKeepsPlaying()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        var first = rig.Session;

        rig.Engine.SwitchDevice("inne");
        h.PumpUntil(() => rig.Started > 1);

        Assert.Equal(2, h.Outputs.Count);
        Assert.Equal(1, first.Stopped);
        Assert.Equal(1, rig.Session.Started);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Empty(h.Failures);
        Assert.Equal(0, h.Finished);
    }

    [Fact]
    public void SwitchingDeviceInPauseStaysDisconnected()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        rig.Engine.Pause();

        rig.Engine.SwitchDevice("inne");

        Assert.Single(rig.Sessions);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);

        rig.Engine.Play();
        h.PumpUntil(() => rig.Started > 1);
    }

    /// <summary>Utrata urządzenia to pauza bez samoczynnego startu; miejsce pliku sprzed stacji zostaje.</summary>
    [Fact]
    public void LosingTheDeviceDisconnectsAndWaitsForPlay()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        var first = rig.Session;

        h.Output.Lose();

        Assert.Equal(1, h.Lost);
        Assert.Equal(1, first.Stopped);
        Assert.False(rig.Engine.IsDeviceOpen);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Single(rig.Sessions);
        Assert.Empty(h.Failures);
        Assert.Equal(0, h.Finished);

        rig.Engine.Play();
        h.PumpUntil(() => rig.Started > 1);
        Assert.Equal(2, h.Outputs.Count);

        rig.Engine.StopLive();
        Assert.Equal(0, rig.Engine.CurrentIndex);
    }

    [Fact]
    public void StationEndedWithoutADeviceReturnsToTheFileOnNextPlay()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, B)]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Output.Lose();

        rig.Engine.StopLive();
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Equal(0, rig.Engine.CurrentIndex);

        rig.Engine.Play();
        h.PumpUntil(() => !rig.Engine.IsSeeking && EngineHarness.CountFrames(h.Output.Samples, B) > 2048);
        Assert.InRange(rig.Engine.Position.TotalSeconds, 0.1, 2);
    }

    /// <summary>Stan po porażce czeka na ponowienie: zmiana urządzenia go nie kasuje i nic nie zgłasza.</summary>
    [Fact]
    public void SwitchingDeviceAfterAFailureKeepsTheFailedState()
    {
        using var rig = new LiveRig
        {
            NextDecoder = () =>
            {
                var decoder = new ScriptedLiveDecoder();
                decoder.End(new LiveFailure(LiveFailureReason.NotFound));
                return decoder;
            },
        };
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Failed > 0);

        rig.Engine.SwitchDevice("inne");
        Thread.Sleep(30);

        Assert.Equal(LiveState.Failed, rig.Engine.LiveState);
        Assert.DoesNotContain(rig.States, change => change.State == LiveState.Stopped);
        Assert.Single(rig.Sessions);
    }

    [Fact]
    public void LosingTheDeviceInPauseDoesNotReportTheStopAgain()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        rig.Engine.Pause();
        EngineHarness.WaitUntil(() => rig.States.Any(change => change.State == LiveState.Stopped));

        h.Output.Lose();
        Thread.Sleep(30);

        Assert.Single(rig.States, change => change.State == LiveState.Stopped);
        Assert.Equal(LiveState.Stopped, rig.Engine.LiveState);
    }

    /// <summary>
    /// Utrata potwierdzona w chwili startu stacji: albo stacja ma urządzenie i gra, albo jej sesja
    /// jest zatrzymana. Sesji pobierającej przy zamkniętym wyjściu nie miałby kto zatrzymać.
    /// </summary>
    [Fact]
    public async Task DeviceLossWhileStartingTheStationLeavesNoConnectionBehind()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;

        for (var i = 0; i < 300; i++)
        {
            rig.Engine.Initialise();
            var output = h.Output;
            using var together = new Barrier(2);
            var spins = i % 64;
            var lose = Task.Run(() =>
            {
                together.SignalAndWait();
                Thread.SpinWait(spins);
                output.Lose();
            });

            together.SignalAndWait();
            rig.Engine.PlayLive(LiveRig.Station());
            await lose;

            var session = rig.Session;
            Assert.True(session.Stopped > 0 || (rig.Engine.IsDeviceOpen && rig.Engine.State == PlaybackState.Playing),
                $"runda {i}: stan {rig.Engine.State}, urządzenie {rig.Engine.IsDeviceOpen}, sesja bez zatrzymania");

            h.Output.Stop();
            rig.Engine.StopLive();
        }
    }

    [Fact]
    public void DisposeStopsTheSession()
    {
        var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        var session = rig.Session;

        rig.Dispose();

        Assert.Equal(1, session.Stopped);
        Assert.False(rig.Engine.IsLive);
    }

    [Fact]
    public void StoppingTheStationIsReportedAsAState()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.StopLive();

        EngineHarness.WaitUntil(() => rig.States.Any(change => change.State == LiveState.Stopped));
        Assert.Null(rig.Engine.LiveState);
    }

    // ---------- losowa seria poleceń ----------

    /// <summary>
    /// Polecenia w dowolnej kolejności, przy pracującym wątku dźwiękowym: żadne nie zawiesza
    /// silnika ani nie zostawia otwartego połączenia, a na końcu stacja i plik nadal grają.
    /// </summary>
    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void RandomCommandsLeaveNoConnectionBehind(int seed)
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        var random = new Random(seed);
        var paths = Enumerable.Range(0, 4).Select(i => h.Track("t" + i, 30, A)).ToArray();
        rig.Engine.Enqueue(paths);
        rig.Engine.Initialise();

        var pumped = 0;
        void KeepPumping()
        {
            if (h.Outputs.Count == pumped) return;
            pumped = h.Outputs.Count;
            h.Output.StartAutoPump();
        }

        KeepPumping();

        Action[] commands =
        [
            () => rig.Engine.PlayLive(LiveRig.Station("s" + random.Next(3))),
            () => rig.Engine.StopLive(),
            () => rig.Engine.Stop(),
            () => rig.Engine.Pause(),
            () => rig.Engine.Play(),
            () => rig.Engine.TogglePlay(),
            () => rig.Engine.Next(),
            () => rig.Engine.Previous(),
            () => rig.Engine.SeekTo(TimeSpan.FromSeconds(random.Next(20))),
            () => rig.Engine.PlayIndex(random.Next(rig.Engine.Queue.Count + 1)),
            () => rig.Engine.Move(random.Next(4), random.Next(4)),
            () => rig.Engine.Shuffle = random.Next(2) == 0,
            () => rig.Engine.SwitchDevice("urządzenie" + random.Next(2)),
            () => h.Output.Lose(),
            () => rig.Engine.RefreshTrackGain(),
        ];

        for (var i = 0; i < 200; i++)
        {
            commands[random.Next(commands.Length)]();
            KeepPumping();
            if (random.Next(4) == 0) Thread.Sleep(1);
        }

        // Odczyt przed włączeniem: LiveStarted może przyjść, zanim wróci PlayLive.
        var started = rig.Started;
        rig.Engine.PlayLive(LiveRig.Station("koniec"));
        KeepPumping();
        EngineHarness.WaitUntil(() => rig.Started > started);

        Assert.Equal(1, rig.Session.Started);
        Assert.All(rig.Sessions.Where(session => session != rig.Session), session => Assert.True(session.Stopped > 0));

        rig.Engine.PlayIndex(0);
        KeepPumping();
        EngineHarness.WaitUntil(() => !rig.Engine.IsSeeking && rig.Engine.Position > TimeSpan.Zero);

        Assert.False(rig.Engine.IsLive);
        Assert.True(rig.Session.Stopped > 0);
        Assert.Empty(h.Failures);
        Assert.Equal(0, rig.Failed);
        Assert.Equal(0, h.Finished);
    }

    // ---------- kolejka w trakcie stacji ----------

    [Fact]
    public void NextPreviousAndSeekDoNothing()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        var a = h.Track("a", 30, A);
        var b = h.Track("b", 30, A);
        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Opened.Clear();
        h.TrackChanges.Clear();

        rig.Engine.Next();
        rig.Engine.Previous();
        rig.Engine.SeekTo(TimeSpan.FromSeconds(10));
        h.PumpPeriods(10);

        Assert.True(rig.Engine.IsLive);
        Assert.Empty(h.Opened);
        Assert.Empty(h.TrackChanges);
        Assert.Equal(-1, rig.Engine.CurrentIndex);
        Assert.True(rig.Engine.Position < TimeSpan.FromSeconds(5));
        Assert.Equal(0, rig.Session.Stopped);
    }

    /// <summary>Stacja włączona między sprawdzeniem a krokiem nie może oddać miejsca utworowi z kolejki.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StepRacingWithPlayLiveLeavesTheStationInPlace(bool forward)
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A), h.Track("b", 30, A), h.Track("c", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.Output.StartAutoPump();

        for (var round = 0; round < 100; round++)
        {
            var finished = false;
            using var stepping = new ManualResetEventSlim();
            var stepper = Task.Run(() =>
            {
                stepping.Set();
                while (!Volatile.Read(ref finished))
                {
                    if (forward) rig.Engine.Next();
                    else rig.Engine.Previous();
                }
            });

            stepping.Wait();
            rig.Engine.PlayLive(LiveRig.Station());
            Volatile.Write(ref finished, true);
            await stepper;

            Assert.Equal(-1, rig.Engine.CurrentIndex);

            rig.Engine.StopLive();
        }
    }

    // ---------- start z zatrzymanego urządzenia ----------

    private const float Negative = -0.3f;

    /// <summary>Pierwsze okresy po starcie urządzenia: bez czasu dla wątku dekodującego, który opróżnia bufor.</summary>
    private static float[] SamplesRightAfterStart(EngineHarness h)
    {
        h.Output.Pump();
        h.PumpUntil(() => !h.Engine.IsSeeking);
        h.PumpPeriods(5);
        return h.Output.Samples;
    }

    [Fact]
    public void PlayRightAfterStopLiveDoesNotSoundTheStation()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(Negative).Feed(long.MaxValue / 2) };
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        EngineHarness.WaitUntil(() => rig.Engine.BufferedFrames > 1024);

        rig.Engine.StopLive();
        h.Output.ClearSamples();
        rig.Engine.Play();

        Assert.DoesNotContain(SamplesRightAfterStart(h), sample => sample < -0.001f);
    }

    [Fact]
    public void PlayLiveFromPauseDoesNotSoundThePausedFile()
    {
        using var rig = new LiveRig { NextDecoder = () => new ScriptedLiveDecoder(Negative).Feed(long.MaxValue / 2) };
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.Pause();
        EngineHarness.WaitUntil(() => rig.Engine.BufferedFrames > 1024);
        h.Output.ClearSamples();

        rig.Engine.PlayLive(LiveRig.Station());

        Assert.DoesNotContain(SamplesRightAfterStart(h), sample => sample > 0.001f);
    }

    [Fact]
    public void PlayIndexFromPauseDoesNotSoundThePausedFile()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A), h.Track("b", 30, Negative)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.Pause();
        EngineHarness.WaitUntil(() => rig.Engine.BufferedFrames > 1024);
        h.Output.ClearSamples();

        rig.Engine.PlayIndex(1);

        Assert.DoesNotContain(SamplesRightAfterStart(h), sample => sample > 0.001f);
    }

    /// <summary>Otwarcie pliku w trakcie stacji (Ctrl+O, upuszczenie) kończy stację zwykłą drogą pliku.</summary>
    [Fact]
    public void PlayIndexEndsTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.Enqueue([h.Track("a", 30, A), h.Track("b", 30, B)]);
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.PlayIndex(1);
        h.WaitForSeekToBeTaken();
        h.PumpUntil(() => !rig.Engine.IsSeeking && EngineHarness.CountFrames(h.Output.Samples, B) > 2048);

        Assert.False(rig.Engine.IsLive);
        Assert.Null(rig.Engine.LiveState);
        Assert.Equal(1, rig.Session.Stopped);
        Assert.Equal(1, rig.Engine.CurrentIndex);
        Assert.True(rig.Decoder.DisposeCount > 0);
    }

    [Fact]
    public void SetQueueEndsTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);

        Assert.False(rig.Engine.IsLive);
        Assert.Equal(1, rig.Session.Stopped);
        Assert.Equal(0, rig.Engine.CurrentIndex);
    }

    /// <summary>Jak w wieży: po wyłączeniu radia płyta czeka w tym samym miejscu.</summary>
    [Fact]
    public void StopLiveThenPlayReturnsToTheFileAndPosition()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        var a = h.Track("a", 30, A);
        var b = h.Track("b", 30, B);
        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.Next();
        rig.Engine.SeekTo(TimeSpan.FromSeconds(7));
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        h.PumpPeriods(5);
        var position = rig.Engine.Position;

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.PumpPeriods(5);

        rig.Engine.StopLive();

        Assert.False(rig.Engine.IsLive);
        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Equal(1, rig.Engine.CurrentIndex);
        Assert.Equal(position.TotalSeconds, rig.Engine.Position.TotalSeconds, precision: 3);
        Assert.Equal(1, rig.Session.Stopped);

        rig.Engine.Play();
        h.Output.ClearSamples();
        var positions = h.PositionsAfterSeek();

        Assert.All(positions, seconds => Assert.InRange(seconds, position.TotalSeconds - 0.01, position.TotalSeconds + 1));
        h.PumpUntil(() => EngineHarness.CountFrames(h.Output.Samples, B) > 2048);
        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
    }

    /// <summary>Zmiana stacji na inną nie nadpisuje miejsca pliku sprzed pierwszej.</summary>
    [Fact]
    public void ChangingStationsKeepsTheBookmarkFromBeforeTheFirstOne()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));

        rig.Engine.PlayLive(LiveRig.Station("jeden"));
        h.PumpUntil(() => rig.Started > 0);
        rig.Engine.PlayLive(LiveRig.Station("dwa"));
        h.PumpUntil(() => rig.Started > 1);
        h.PumpPeriods(30);

        rig.Engine.StopLive();

        Assert.Equal(0, rig.Engine.CurrentIndex);
        Assert.InRange(rig.Engine.Position.TotalSeconds, 0.1, 1.5);
    }

    [Fact]
    public void RemovedBookmarkLeavesNothingToReturnTo()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A), h.Track("b", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.RemoveAt(0);
        h.PumpPeriods(5);

        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Equal(0, rig.Session.Stopped);

        rig.Engine.StopLive();

        Assert.Equal(PlaybackState.Stopped, rig.Engine.State);
        Assert.Equal(-1, rig.Engine.CurrentIndex);
    }

    /// <summary>Cofnięcie usunięcia oddaje wpis razem z miejscem powrotu.</summary>
    [Fact]
    public void RestoringTheBookmarkEntryBringsThePlaceBack()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A), h.Track("b", 30, A)]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.2));
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        var entry = rig.Engine.Queue[0];

        rig.Engine.RemoveAt(0);
        rig.Engine.Restore(entry, 0);
        h.PumpPeriods(5);

        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.Equal(0, rig.Session.Stopped);

        rig.Engine.StopLive();

        Assert.Equal(PlaybackState.Paused, rig.Engine.State);
        Assert.Equal(0, rig.Engine.CurrentIndex);
        Assert.InRange(rig.Engine.Position.TotalSeconds, 0.2, 2);
    }

    [Fact]
    public void QueueThatEndedHasNoBookmark()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 0.3, A)]);
        h.PumpUntil(() => h.Finished > 0);

        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        rig.Engine.Stop();

        Assert.False(rig.Engine.IsLive);
        Assert.Equal(PlaybackState.Stopped, rig.Engine.State);

        rig.Engine.Play();
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        Assert.Equal(0, rig.Engine.CurrentIndex);
    }

    [Fact]
    public void ClearingTheQueueLeavesTheStationPlaying()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);

        rig.Engine.ClearQueue();
        h.PumpPeriods(5);

        Assert.Equal(PlaybackState.Playing, rig.Engine.State);
        Assert.True(rig.Engine.IsLive);
        Assert.Equal(0, rig.Session.Stopped);
        Assert.Empty(rig.Engine.Queue);

        rig.Engine.StopLive();
        Assert.Equal(PlaybackState.Stopped, rig.Engine.State);
    }

    /// <summary>Zmiany kolejki idą swoją drogą, ale nie mogą zlecić otwarcia pliku w miejsce stacji.</summary>
    [Fact]
    public void QueueChangesDoNotTouchTheStation()
    {
        using var rig = new LiveRig();
        var h = rig.Harness;
        h.Engine.SetQueue([h.Track("a", 30, A), h.Track("b", 30, A), h.Track("c", 30, A)]);
        h.PumpUntil(() => !rig.Engine.IsSeeking);
        rig.Engine.PlayLive(LiveRig.Station());
        h.PumpUntil(() => rig.Started > 0);
        h.Opened.Clear();

        rig.Engine.Enqueue([h.Track("d", 30, A)]);
        rig.Engine.Move(0, 2);
        rig.Engine.Repeat = RepeatMode.Queue;
        rig.Engine.Shuffle = true;
        rig.Engine.Shuffle = false;
        h.PumpPeriods(10);

        Assert.Empty(h.Opened);
        Assert.False(rig.Engine.IsSeeking);
        Assert.Equal(0, rig.Session.Stopped);
        Assert.Equal(4, rig.Engine.Queue.Count);

        // Przesunięty wpis jest dalej tym samym miejscem powrotu.
        rig.Engine.StopLive();
        Assert.Equal(2, rig.Engine.CurrentIndex);
    }

    /// <summary>Stacja w środku kolejki losowej nie zmienia kolejności, jaką zagrałaby bez niej.</summary>
    [Fact]
    public void ShuffleOrderIsTheSameWithAStationInTheMiddle()
    {
        static List<int> Walk(bool withStation)
        {
            using var rig = new LiveRig(seed: 77);
            var h = rig.Harness;
            rig.Engine.Shuffle = true;
            rig.Engine.SetQueue(Enumerable.Range(0, 8).Select(i => h.Track("t" + i, 30, A)).ToList());
            h.PumpUntil(() => !rig.Engine.IsSeeking);

            var visited = new List<int> { rig.Engine.CurrentIndex };
            for (var i = 0; i < 6; i++)
            {
                if (withStation && i == 3)
                {
                    rig.Engine.PlayLive(LiveRig.Station());
                    h.PumpUntil(() => rig.Started > 0);
                    rig.Engine.StopLive();
                    rig.Engine.Play();
                    h.PumpUntil(() => !rig.Engine.IsSeeking);
                }

                rig.Engine.Next();
                h.PumpUntil(() => !rig.Engine.IsSeeking);
                visited.Add(rig.Engine.CurrentIndex);
            }

            return visited;
        }

        Assert.Equal(Walk(withStation: false), Walk(withStation: true));
    }
}
