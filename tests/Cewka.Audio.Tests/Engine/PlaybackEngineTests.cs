using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Zachowanie silnika odtwarzania na atrapie wyjścia: koniec kolejki, przewijanie, wyciszenia,
/// błędy plików i wyrównanie głośności na granicy utworów.
/// </summary>
public class PlaybackEngineTests
{
    private const float A = 0.25f;
    private const float B = 0.5f;
    private const float C = 0.125f;

    // ---------- koniec kolejki ----------

    /// <summary>
    /// Ostatni utwór gra do ostatniej próbki. Pułapka: dekoder dochodzi do końca pliku pół
    /// sekundy przed wyjściem, więc pauza zlecona w tej chwili ucięłaby zawartość bufora.
    /// </summary>
    [Fact]
    public void LastTrackPlaysToItsFinalFrame()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 0.6, A);
        var b = h.Track("b", 0.8, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished > 0);

        Assert.Equal(EngineHarness.Frames(0.8), EngineHarness.CountFrames(h.Output.Samples, B));
        Assert.Equal(PlaybackState.Paused, h.Engine.State);
    }

    /// <summary>
    /// Play po końcu kolejki zaczyna od pierwszego utworu, a kolejny koniec znów kończy
    /// odtwarzanie, zamiast puszczać ostatni utwór w kółko.
    /// </summary>
    [Fact]
    public void PlayAfterTheEndStartsOverAndFinishesAgain()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 0.3, A);
        var b = h.Track("b", 0.3, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished == 1);
        h.Output.ClearSamples();

        h.Engine.Play();
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.PumpUntil(() => h.Finished == 2);
        var samples = h.Output.Samples;

        Assert.Equal(EngineHarness.Frames(0.3), EngineHarness.CountFrames(samples, B));
        Assert.True(EngineHarness.CountFrames(samples, A) > EngineHarness.Frames(0.25));

        // Po drugim końcu nic już nie gra i nic się nie dekoduje.
        var opened = h.Opened.Count;
        h.PumpPeriods(50);
        Assert.Equal(opened, h.Opened.Count);
        Assert.Equal(0, EngineHarness.CountFrames(h.Output.Samples[samples.Length..], B));
    }

    // ---------- przewijanie ----------

    /// <summary>Przewinięcie zaraz po PlayIndex dotyczy wpisu, który dopiero zacznie grać.</summary>
    [Fact]
    public void SeekImmediatelyAfterPlayIndexIsKept()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));

        var before = h.TrackChanges.Count;
        h.Engine.PlayIndex(1);
        h.Engine.SeekTo(TimeSpan.FromSeconds(5));

        h.PumpUntil(() => h.TrackChanges.Count > before);
        Assert.Equal(1, h.TrackChanges.ElementAt(before));

        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(5.05));
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.InRange(h.Engine.Position.TotalSeconds, 5.0, 5.5);
    }

    [Fact]
    public void PlayIndexWithStartBeginsThere()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);

        h.Engine.SetQueue([a]);
        h.Engine.PlayIndex(0, TimeSpan.FromSeconds(7));

        AssertPlaysOnFrom(h.PositionsAfterSeek(), 7.0);
    }

    /// <summary>Zmiana urządzenia albo opóźnienia wraca do tego samego miejsca utworu.</summary>
    [Fact]
    public void ReopenKeepsTrackAndPosition()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);

        h.Engine.SetQueue([a, b], startIndex: 1);
        h.Engine.SeekTo(TimeSpan.FromSeconds(4));
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(4.05));

        h.Engine.Reopen();
        Assert.Equal(2, h.Outputs.Count);

        AssertPlaysOnFrom(h.PositionsAfterSeek(), 4.0);
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>
    /// Przewinięcie w ostatnim pół sekundy utworu, gdy dekoder otworzył już następny:
    /// pozycja zostaje przy celu, zamiast skoczyć na początek, gdy wątek dźwiękowy minie
    /// starą granicę.
    /// </summary>
    [Fact]
    public void SeekWithinTheLastHalfSecondKeepsTheTarget()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 1.0, A);
        var b = h.Track("b", 2.0, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"));
        Assert.Equal(0, h.Engine.CurrentIndex);

        var before = h.TrackChanges.Count;
        h.Engine.SeekTo(TimeSpan.FromSeconds(0.2));

        AssertPlaysOnFrom(h.PositionsAfterSeek(20), 0.2);
        Assert.Equal(0, h.Engine.CurrentIndex);

        // Granica B, otwartego przed przewinięciem, nie może mignąć w interfejsie.
        Thread.Sleep(50);
        Assert.All(h.TrackChanges.Skip(before), index => Assert.Equal(0, index));

        h.PumpUntil(() => h.Engine.CurrentIndex == 1);
        Assert.InRange(h.Engine.Position.TotalSeconds, 0, 0.3);
    }

    /// <summary>
    /// Dekoder, którego przewinięcie się nie udało, czyta dalej skądinąd; pozycja ma opisywać
    /// to, co gra, nie cel.
    /// </summary>
    [Fact]
    public void FailedDecoderSeekReportsWhereItReallyIs()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A, d => new SyntheticDecoder(d.TotalFrames, A, d.Channels, d.SampleRate) { IgnoreSeeks = true });

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(1));

        h.Engine.SeekTo(TimeSpan.FromSeconds(6));
        var positions = h.PositionsAfterSeek();

        Assert.True(positions[0] < 3, $"pozycja {positions[0]}");
    }

    // ---------- zmiany kolejki ----------

    /// <summary>
    /// Usunięcie wiersza przed grającym w ostatnim pół sekundy utworu, gdy następny jest już
    /// otwarty: numer wskazuje utwór słyszany, a po granicy następny, nie o jeden dalej.
    /// </summary>
    [Fact]
    public void RemovingAnEarlierRowNearTheBoundaryKeepsTheIndices()
    {
        using var h = new EngineHarness();
        var x = h.Track("x", 10, C);
        var a = h.Track("a", 1.0, A);
        var b = h.Track("b", 10, B);
        var c = h.Track("c", 10, 0.75f);

        h.Engine.SetQueue([x, a, b, c], startIndex: 1);
        h.PumpUntil(() => h.Opened.Contains("b.wav"));

        h.Engine.RemoveAt(0);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1) && h.Engine.CurrentIndex != 0);
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.Equal(B, h.Output.Samples[^1], 1e-5f);
    }

    /// <summary>
    /// Usunięcie następnego utworu, który dekoder już otworzył, nie zatrzymuje słyszanego;
    /// po nim gra kolejny w kolejce.
    /// </summary>
    [Fact]
    public void RemovingThePrefetchedTrackKeepsPlayingAndSkipsIt()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 1.0, A);
        var b = h.Track("b", 10, B);
        var c = h.Track("c", 10, C);

        h.Engine.SetQueue([a, b, c]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"));

        h.Engine.RemoveAt(1);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position > TimeSpan.FromSeconds(0.1));
        Assert.Equal(C, h.Output.Samples[^1], 1e-5f);
    }

    /// <summary>
    /// Po zatrzymaniu i po usunięciu grającego utworu wątek dekodujący niczego sam nie
    /// otwiera: plik nie zostaje zablokowany, a następny utwór nie jest wczytywany na zapas.
    /// </summary>
    [Fact]
    public void NothingIsOpenedWithoutARequest()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.2));

        h.Engine.RemoveAt(0);
        h.PumpUntil(() => !h.Engine.IsSeeking);
        Thread.Sleep(100);

        Assert.Equal(new[] { "a.wav" }, h.Opened.ToArray());
        Assert.Equal(PlaybackState.Stopped, h.Engine.State);
        Assert.Equal(-1, h.Engine.CurrentIndex);
    }

    /// <summary>Pozycje po przewinięciu zaczynają się przy celu i tylko rosną.</summary>
    private static void AssertPlaysOnFrom(double[] positions, double target)
    {
        Assert.InRange(positions[0], target - 0.01, target + 0.2);
        for (var i = 1; i < positions.Length; i++) Assert.True(positions[i] >= positions[i - 1], $"pozycja cofnęła się: {string.Join(" ", positions)}");
    }

    // ---------- wyciszenia ----------

    /// <summary>
    /// Wyciszenie przy przewinięciu przechodzi przez tor, więc obejmuje je głośność. Przy
    /// suwaku na zerze żadna próbka nie może dotrzeć do wyjścia.
    /// </summary>
    [Fact]
    public void SeekFadeRespectsVolume()
    {
        using var h = new EngineHarness(periodFrames: 256);
        var a = h.Track("a", 10, A);

        h.Engine.Graph.Volume.Target = 0;
        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.2));

        h.Engine.SeekTo(TimeSpan.FromSeconds(3));
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(3.1));

        Assert.All(h.Output.Samples, sample => Assert.Equal(0f, sample));
    }

    /// <summary>
    /// Przy okresie krótszym od wyciszenia rampa ciągnie się przez kilka okresów i schodzi do
    /// zera bez skoku; potem tor nie wypuszcza starego dźwięku.
    /// </summary>
    [Fact]
    public void SeekFadeIsContinuousAcrossShortPeriods()
    {
        using var h = new EngineHarness(periodFrames: 64);
        var a = h.Track("a", 10, A);

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.3));
        h.Output.ClearSamples();

        h.Engine.SeekTo(TimeSpan.FromSeconds(5));
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(5.1));

        var left = h.Output.Samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();
        var steps = left.Zip(left.Skip(1), (x, y) => Math.Abs(y - x)).ToArray();
        var at = Array.IndexOf(steps, steps.Max());

        // Rampa 6 ms przy 48 kHz to krok około A / 288.
        Assert.True(steps[at] < 0.01f, $"skok {steps[at]} w ramce {at} z {left.Length}: " +
            string.Join(" ", left.Skip(Math.Max(0, at - 3)).Take(7)));
    }

    /// <summary>Pauza wycisza wyjście przed zatrzymaniem, wznowienie zaczyna od ciszy.</summary>
    [Fact]
    public void PauseAndResumeFade()
    {
        using var h = new EngineHarness(periodFrames: 128);
        var a = h.Track("a", 10, A);

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.3));

        h.Output.StartAutoPump();
        h.Engine.Pause();
        h.Output.StopAutoPump();

        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        var paused = h.Output.Samples;
        Assert.Equal(0f, paused[^1]);
        Assert.Equal(0f, paused[^2]);

        var left = paused.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();
        var largestStep = left.Zip(left.Skip(1), (x, y) => Math.Abs(y - x)).Max();
        Assert.True(largestStep < 0.01f, $"skok przy pauzie {largestStep}");

        h.Output.ClearSamples();
        h.Engine.Play();
        h.PumpPeriods(20);

        var resumed = h.Output.Samples;
        Assert.True(Math.Abs(resumed[0]) < 0.01f);
        Assert.Equal(A, resumed[^1], 1e-5f);
    }

    /// <summary>
    /// Po wyciszeniu pauza oddaje jeszcze ciszę na długość bufora urządzenia. Zatrzymanie zaraz po
    /// wyrenderowaniu wyciszenia ucina próbki czekające w buforze sprzętowym i słychać trzask.
    /// </summary>
    [Fact]
    public void PauseDrainsTheDeviceBufferBeforeStopping()
    {
        // Okres jak w prawdziwym urządzeniu. Przy bardzo krótkim atrapa, pompowana w rytmie zegara
        // systemowego, nie zdążyłaby oddać ciszy przed limitem czasu pauzy.
        using var h = new EngineHarness(periodFrames: 512);
        var a = h.Track("a", 10, A);

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.3));

        h.Output.ClearSamples();
        h.Output.StartAutoPump();
        h.Engine.Pause();
        h.Output.StopAutoPump();

        var left = h.Output.Samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();
        var silence = Enumerable.Reverse(left).TakeWhile(s => s == 0f).Count();
        Assert.True(silence >= EngineHarness.Frames(0.04), $"cisza przed zatrzymaniem: {silence} ramek");
    }

    // ---------- błędy plików ----------

    /// <summary>
    /// Wyjątek dekodera w środku utworu kończy ten utwór i przechodzi do następnego; ponowne
    /// otwarcie grałoby go od początku w nieskończoność.
    /// </summary>
    [Fact]
    public void DecoderErrorMidTrackMovesOn()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 2.0, A, d => new SyntheticDecoder(d.TotalFrames, A, d.Channels, d.SampleRate)
        {
            ThrowAtFrame = EngineHarness.Frames(0.4),
        });
        var b = h.Track("b", 0.3, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished > 0);

        // Utwór A gra raz, do miejsca błędu, bez pierwszych ramek wyciszenia wejściowego.
        var played = EngineHarness.CountFrames(h.Output.Samples, A);
        Assert.InRange(played, EngineHarness.Frames(0.4) - 300, EngineHarness.Frames(0.4));
        Assert.Equal(EngineHarness.Frames(0.3), EngineHarness.CountFrames(h.Output.Samples, B));
        Assert.Single(h.Opened, name => name == "a.wav");

        var failure = Assert.Single(h.Failures);
        Assert.Equal(0, failure.Index);
        Assert.Equal(TrackFailureReason.PlaybackInterrupted, failure.Failure.Reason);
    }

    /// <summary>
    /// Kolejka samych plików nieobsługiwanych: każdy zgłoszony raz, koniec kolejki, potem
    /// spokój: wątek dekodujący nie ponawia otwierania sam z siebie.
    /// </summary>
    [Fact]
    public void QueueOfUnsupportedFilesDoesNotSpin()
    {
        using var h = new EngineHarness();
        var x = h.Unsupported("x");
        var y = h.Unsupported("y");

        h.Engine.SetQueue([x, y]);
        h.PumpUntil(() => h.Finished > 0);

        h.PumpPeriods(20);
        Thread.Sleep(300);

        Assert.Equal(2, h.Failures.Count);
        Assert.All(h.Failures, f => Assert.Equal(TrackFailureReason.UnsupportedFormat, f.Failure.Reason));
        Assert.Equal(1, h.Finished);
        Assert.Empty(h.Opened);
    }

    /// <summary>Nieobsługiwany ostatni wpis przy powtarzaniu kolejki zawija na początek.</summary>
    [Fact]
    public void RepeatQueueSkipsUnsupportedLastEntryToTheStart()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 0.3, A);
        var x = h.Unsupported("x");

        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([a, x]);

        h.PumpUntil(() => h.Opened.Count(name => name == "a.wav") >= 2);

        Assert.Equal(0, h.Finished);
        Assert.Contains(h.Failures, f => f.Index == 1);
    }

    /// <summary>
    /// Plik, którego chwilowo nie da się otworzyć, nie zostaje oznaczony na stałe: jawne
    /// polecenie odtworzenia próbuje go otworzyć ponownie.
    /// </summary>
    [Fact]
    public void FailedOpenIsRetriedOnRequest()
    {
        using var h = new EngineHarness();
        var available = false;
        var a = h.Track("a", 10, A, d => available ? d : throw new IOException("Dysk niedostępny."));

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Finished > 0);

        var failure = Assert.Single(h.Failures);
        Assert.Equal(TrackFailureReason.FileUnavailable, failure.Failure.Reason);
        Assert.False(h.Engine.Queue[0].IsUnsupported);

        available = true;
        h.Engine.PlayIndex(0);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));

        Assert.Null(h.Engine.Queue[0].OpenFailure);
    }

    // ---------- wyrównanie głośności ----------

    /// <summary>
    /// Wzmocnienie następnego utworu zaczyna działać dokładnie na granicy. Ustawione przy
    /// otwarciu pliku objęłoby ostatnie pół sekundy poprzedniego utworu.
    /// </summary>
    [Fact]
    public void NormalisationChangesAtTheTrackBoundary()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 0.8, A);
        var b = h.Track("b", 0.4, B);

        h.Engine.Graph.GainSource = new FixedGains(new() { ["a"] = -6.0206, ["b"] = 6.0206 });
        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished > 0);

        var samples = h.Output.Samples;
        var tail = 0;
        for (var i = 0; i < samples.Length; i += EngineHarness.Channels)
            if (samples[i] > 0 && samples[i] <= A * 0.5f + 1e-4f) tail++;

        // Wszystkie ramki utworu A, poza pierwszą (wyciszona do zera), grają z jego wzmocnieniem.
        Assert.InRange(tail, EngineHarness.Frames(0.8) - 1, EngineHarness.Frames(0.8));
    }

    /// <summary>Po przeskoku nowy utwór zaczyna od swojego wzmocnienia, bez dochodzenia do niego.</summary>
    [Fact]
    public void NormalisationSnapsAfterAJump()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);

        h.Engine.Graph.GainSource = new FixedGains(new() { ["a"] = -12, ["b"] = 6.0206 });
        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.3));

        h.Output.ClearSamples();
        h.Engine.PlayIndex(1);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position > TimeSpan.FromSeconds(0.2));

        // Pierwsza ramka utworu B to pierwsza powyżej poziomu A; po wyciszeniu wejściowym
        // (288 ramek) B ma już pełne wzmocnienie. Dochodzenie do niego trwałoby ponad 100 ms.
        var left = h.Output.Samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();
        var start = Array.FindLastIndex(left, s => s == 0f) + 1;
        var firstLoud = Array.FindIndex(left, start, s => s > 0.9f);

        Assert.True(firstLoud >= start, "utwór B nie osiągnął swojego poziomu");
        Assert.InRange(firstLoud - start, 0, 400);
    }

    // ---------- losowanie ----------

    /// <summary>„Poprzedni” w trybie losowym wraca do utworu granego wcześniej.</summary>
    [Fact]
    public void PreviousInShuffleReturnsToThePreviousTrack()
    {
        using var h = new EngineHarness();
        var paths = Enumerable.Range(0, 6).Select(i => h.Track("t" + i, 10, 0.1f * (i + 1))).ToArray();

        h.Engine.Shuffle = true;
        h.Engine.SetQueue(paths, startIndex: 2);
        h.PumpUntil(() => h.TrackChanges.Contains(2));

        h.Engine.Next();
        h.PumpUntil(() => h.Engine.CurrentIndex != 2 && h.TrackChanges.Count >= 2);
        var second = h.Engine.CurrentIndex;

        h.Engine.Next();
        h.PumpUntil(() => h.Engine.CurrentIndex != second && h.TrackChanges.Count >= 3);

        h.Engine.Previous();
        h.PumpUntil(() => h.TrackChanges.Count >= 4);
        Assert.Equal(second, h.Engine.CurrentIndex);

        h.Engine.Previous();
        h.PumpUntil(() => h.TrackChanges.Count >= 5);
        Assert.Equal(2, h.Engine.CurrentIndex);
    }

    /// <summary>Losowanie bez powtarzania gra każdy utwór dokładnie raz i kończy kolejkę.</summary>
    [Fact]
    public void ShufflePlaysEveryTrackOnceThenFinishes()
    {
        using var h = new EngineHarness();
        var paths = Enumerable.Range(0, 5).Select(i => h.Track("t" + i, 0.1, 0.1f * (i + 1))).ToArray();

        h.Engine.Shuffle = true;
        h.Engine.SetQueue(paths);
        h.PumpUntil(() => h.Finished > 0);

        Assert.Equal(5, h.Opened.Count);
        Assert.Equal(5, h.Opened.Distinct().Count());
    }

    // ---------- urządzenie ----------

    /// <summary>
    /// Utrata urządzenia: pauza, zdarzenie i zamknięcie wyjścia, żeby samoczynny restart
    /// sterownika nie grał przy stanie pauzy. Utwór i pozycja czekają na Play, które
    /// otwiera wyjście od nowa.
    /// </summary>
    [Fact]
    public void LostDeviceStopsAndPlayReopens()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B);

        h.Engine.SetQueue([a, b], startIndex: 1);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(1));
        var lostAt = h.Engine.Position.TotalSeconds;

        h.Output.Lose();
        EngineHarness.WaitUntil(() => h.Lost == 1);

        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.True(h.Outputs[0].Disposed);
        Assert.False(h.Engine.IsDeviceOpen);
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.InRange(h.Engine.Position.TotalSeconds, lostAt - 0.01, lostAt + 0.1);

        h.Engine.Play();
        Assert.Equal(2, h.Outputs.Count);

        AssertPlaysOnFrom(h.PositionsAfterSeek(), lostAt);
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>
    /// Sterownik potrafi sam uruchomić utracone urządzenie po ponownym podłączeniu sprzętu,
    /// zanim silnik zdąży je zamknąć. Jeśli w tej chwili Play otworzyło już nowe wyjście, stare
    /// nie może grać ani dotykać toru, który przygotowano dla nowego.
    /// </summary>
    [Fact]
    public async Task OldDeviceRestartedDuringLossHandlingDoesNotRender()
    {
        using var h = new EngineHarness();
        using var gate = new ManualResetEventSlim(true);
        using var waiting = new ManualResetEventSlim(false);
        SyntheticDecoder? stuck = null;

        // Zawiesza się tylko pierwszy dekoder, żeby zamykanie starej sesji czekało na jej wątek.
        var a = h.Track("a", 10, A, d => stuck is null
            ? stuck = new SyntheticDecoder(d.TotalFrames, A, d.Channels, d.SampleRate) { Gate = gate, Waiting = waiting }
            : d);

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.5));

        // Bufor starej sesji zostaje prawie pełny, a jej wątek stoi w odczycie.
        gate.Reset();
        h.PumpUntil(() => waiting.IsSet);

        var old = h.Outputs[0];
        var loss = Task.Run(old.Lose);
        EngineHarness.WaitUntil(() => !h.Engine.IsDeviceOpen);

        h.Engine.Play();
        Assert.Equal(2, h.Outputs.Count);

        old.ClearSamples();
        old.Start();
        old.Pump();
        Assert.All(old.Samples, sample => Assert.Equal(0f, sample));

        gate.Set();
        await loss;
        Assert.Equal(1, h.Lost);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    /// <summary>
    /// Zgłoszenie utraty od urządzenia, które zdążyło zostać zamknięte przy zmianie wyjścia,
    /// nie dotyczy nowego urządzenia.
    /// </summary>
    [Fact]
    public void LossReportedByAClosedDeviceIsIgnored()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);

        h.Engine.SetQueue([a]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.2));

        var pending = h.Output.LostHandlers;
        h.Engine.Reopen();
        pending?.Invoke();

        Assert.Equal(PlaybackState.Playing, h.Engine.State);
        Assert.False(h.Outputs[1].Disposed);
        Assert.Equal(0, h.Lost);
    }

    /// <summary>
    /// Zatrzymanie, zanim cokolwiek grało, nie zostawia zaległego przewinięcia: pierwsze Play
    /// po dodaniu plików zaczyna od pierwszego wpisu. Tak samo Play zaraz po Stop.
    /// </summary>
    [Fact]
    public void StopDoesNotLeaveAnEmptySeekForTheNextPlay()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 10, A);

        h.Engine.Stop();
        Assert.False(h.Engine.IsSeeking);

        h.Engine.Enqueue([a]);
        h.Engine.Play();
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Engine.Stop();
        h.Engine.Play();
        h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position > TimeSpan.FromSeconds(0.1));
        Assert.Equal(0, h.Engine.CurrentIndex);
    }

    /// <summary>
    /// Zmiana urządzenia po końcu kolejki zachowuje koniec: Play zaczyna od pierwszego wpisu,
    /// zamiast od razu skończyć na ostatnim.
    /// </summary>
    [Fact]
    public void ReopenAfterTheEndKeepsTheEnd()
    {
        using var h = new EngineHarness();
        var a = h.Track("a", 0.3, A);
        var b = h.Track("b", 0.3, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Finished == 1);

        h.Engine.Reopen();
        h.Engine.Play();
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.PumpUntil(() => h.Finished == 2);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, A) > EngineHarness.Frames(0.25));
    }

    /// <summary>
    /// Przewinięcie, gdy wybrany utwór jeszcze się otwiera (wolny dysk), dotyczy tego utworu,
    /// a numer bieżącego wpisu wskazuje go już w trakcie otwierania.
    /// </summary>
    [Fact]
    public void SeekWhileTheChosenTrackIsStillOpeningTargetsIt()
    {
        using var h = new EngineHarness();
        using var gate = new ManualResetEventSlim(true);
        var a = h.Track("a", 10, A);
        var b = h.Track("b", 10, B, d =>
        {
            gate.Wait();
            return d;
        });

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));

        gate.Reset();
        h.Engine.PlayIndex(1);
        h.PumpUntil(() => h.Opened.Contains("b.wav"));
        Assert.Equal(1, h.Engine.CurrentIndex);

        h.Engine.SeekTo(TimeSpan.FromSeconds(5));
        gate.Set();

        h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position > TimeSpan.FromSeconds(5.05));
        Assert.Equal(1, h.Engine.CurrentIndex);
        Assert.InRange(h.Engine.Position.TotalSeconds, 5.0, 5.5);
    }

    /// <summary>
    /// Wątek dekodujący, który utknął w otwieraniu pliku, po powrocie nie kończy przewinięcia
    /// wykonywanego właśnie przez nową sesję.
    /// </summary>
    [Fact]
    public void AStuckSessionDoesNotEndTheNewSessionsSeek()
    {
        using var h = new EngineHarness();
        using var openA = new ManualResetEventSlim(false);
        using var openB = new ManualResetEventSlim(true);
        SyntheticDecoder? stuck = null;

        var a = h.Track("a", 10, A, d =>
        {
            if (Interlocked.CompareExchange(ref stuck, d, null) is null) openA.Wait();
            return d;
        });
        var b = h.Track("b", 10, B, d =>
        {
            openB.Wait();
            return d;
        });

        h.Engine.SetQueue([a, b]);
        EngineHarness.WaitUntil(() => h.Opened.Contains("a.wav"));

        h.Engine.Reopen();
        openB.Reset();
        h.Engine.PlayIndex(1);
        h.PumpUntil(() => h.Opened.Contains("b.wav"));

        openA.Set();
        EngineHarness.WaitUntil(() => stuck!.DisposeCount == 1);
        Assert.True(h.Engine.IsSeeking);

        openB.Set();
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position > TimeSpan.FromSeconds(0.1));
    }

    /// <summary>
    /// Zamknięcie, gdy wątek dekodujący utknął w odczycie: dekoder zamyka dopiero ten wątek,
    /// po wyjściu z odczytu, a nowa sesja działa obok niezależnie.
    /// </summary>
    [Fact]
    public void ReopenWithAStuckDecoderLeavesItToItsThread()
    {
        using var h = new EngineHarness();
        using var gate = new ManualResetEventSlim(true);
        SyntheticDecoder? stuck = null;

        // Zawiesza się tylko pierwszy dekoder; nowa sesja może otworzyć ten sam plik od nowa.
        var a = h.Track("a", 10, A, d => stuck is null
            ? stuck = new SyntheticDecoder(d.TotalFrames, A, d.Channels, d.SampleRate) { Gate = gate }
            : d);
        var b = h.Track("b", 10, B);

        h.Engine.SetQueue([a, b]);
        h.PumpUntil(() => h.Engine.Position > TimeSpan.FromSeconds(0.1));

        gate.Reset();
        h.PumpPeriods(40);

        h.Engine.Reopen();
        h.Engine.PlayIndex(1);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position > TimeSpan.FromSeconds(0.1));

        Assert.Equal(0, stuck!.DisposeCount);

        gate.Set();
        EngineHarness.WaitUntil(() => stuck.DisposeCount == 1);
    }
}
