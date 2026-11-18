using Cewka.Audio.Decoding;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Przenikanie utworów: kształt krzywych, granica i pozycja następnego utworu, skracanie przy krótkich
/// utworach, przypadki bez przenikania, wzmocnienia wyrównania obu utworów oraz polecenia i zmiany
/// kolejki w trakcie nakładki. Utwory mają stałe wartości próbek, więc nakładkę widać jako wartości
/// pośrednie; wyciszenia 6 ms dają ich tylko kilkaset.
/// </summary>
public sealed class CrossfadeTests
{
    private const float A = 0.5f;
    private const float B = 0.25f;
    private const float C = 0.125f;
    private const int Period = 1024;
    private const int Timeout = 30000;

    /// <summary>Wartości pośrednie z wyciszeń przy przewinięciu, pauzie i początku: kilkaset ramek, a nie sekunda.</summary>
    private const int FadeFrames = 1000;

    private static readonly int R = EngineHarness.Rate;

    private static EngineHarness Rig(double crossfade, int seed = 1234)
    {
        var h = new EngineHarness(Period, seed);
        h.Engine.Crossfade = TimeSpan.FromSeconds(crossfade);
        return h;
    }

    private static float[] Left(float[] samples) => samples.Where((_, i) => i % EngineHarness.Channels == 0).ToArray();

    private static float[] Left(EngineHarness h) => Left(h.Output.Samples);

    private static int Frames(EngineHarness h) => h.Output.Samples.Length / EngineHarness.Channels;

    /// <summary>Indeks pierwszej ramki pierwszego utworu: ta ramka jest wyciszona do zera, dźwięk zaczyna się od następnej.</summary>
    private static int SoundStart(float[] left) => Array.FindIndex(left, v => v > 0) - 1;

    /// <summary>
    /// Ramki, które nie są ani ciszą, ani żadną z dwóch stałych wartości: suma obu utworów w nakładce
    /// albo rampa wyciszenia. Suma o stałej mocy przekracza też większą z wartości.
    /// </summary>
    private static int Mixed(IEnumerable<float> left, float low, float high) =>
        left.Count(v => Math.Abs(v) > 0.01f && Math.Abs(v - low) > 0.01f && Math.Abs(v - high) > 0.01f);

    /// <summary>Ramki lewego kanału równe wartości; <see cref="EngineHarness.CountFrames"/> liczy na próbkach przeplecionych.</summary>
    private static int Steady(float[] left, float value) => left.Count(v => Math.Abs(v - value) <= 1e-5f);

    private static float LargestStep(float[] left, int from, int to)
    {
        var largest = 0f;
        for (var i = Math.Max(1, from); i < Math.Min(left.Length, to); i++) largest = Math.Max(largest, Math.Abs(left[i] - left[i - 1]));
        return largest;
    }

    private static float[] PlayAll(EngineHarness h, params string[] paths)
    {
        h.Engine.SetQueue(paths);
        h.PumpUntil(() => h.Finished > 0, Timeout);
        return Left(h);
    }

    private static string Shaped(EngineHarness h, string name, double seconds, Func<long, float> value, double declared) =>
        h.Track(name, () => new ShapedDecoder(EngineHarness.Frames(seconds), value, EngineHarness.Frames(declared)));

    private static void Tag(string path, int? track)
    {
        var tags = new ATL.Track(path) { Album = "Album", Title = Path.GetFileNameWithoutExtension(path) };
        if (track is { } number) tags.TrackNumber = number;
        Assert.True(tags.Save(), "nie zapisano znaczników");
    }

    // ---------- kształt i granica ----------

    /// <summary>
    /// Wyciszanie po cosinusie, narastanie po sinusie, suma mocy stała. Pierwszy przebieg (B milczy) daje
    /// krzywą wyciszania, drugi (oba równe) sumę obu krzywych.
    /// </summary>
    [Fact]
    public void KrzyweMajaStalaMocWyciszanieCosinusNarastanieSinus()
    {
        float[] silent, equal;
        int s1, s2;

        using (var h = Rig(1))
        {
            silent = PlayAll(h, h.Track("a", 3, A), h.Track("b", 3, 0));
            s1 = SoundStart(silent);
        }

        using (var h = Rig(1))
        {
            equal = PlayAll(h, h.Track("a", 3, A), h.Track("b", 3, A));
            s2 = SoundStart(equal);
        }

        Assert.Equal(A, silent[s1 + 2 * R - 1], 4);
        for (var k = 0; k < R; k += 7)
        {
            var angle = k * Math.PI / 2 / R;
            var fadeOut = silent[s1 + 2 * R + k] / A;
            var fadeIn = (equal[s2 + 2 * R + k] - silent[s1 + 2 * R + k]) / A;

            Assert.True(Math.Abs(fadeOut - Math.Cos(angle)) < 1e-4, $"wyciszanie w {k}: {fadeOut}");
            Assert.True(Math.Abs(fadeIn - Math.Sin(angle)) < 1e-4, $"narastanie w {k}: {fadeIn}");
            Assert.True(Math.Abs(fadeOut * fadeOut + fadeIn * fadeIn - 1) < 1e-3, $"suma mocy w {k}");
        }

        Assert.Equal(0, silent[s1 + 3 * R + 1], 4);
        Assert.Equal(A, equal[s2 + 3 * R + 1], 4);
    }

    /// <summary>Następny utwór staje się bieżący na początku nakładki, z pozycją od zera i własną długością.</summary>
    [Fact]
    public void GranicaNaPoczatkuNakladkiAPozycjaNastepnegoOdZera()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 4, B)]);

        h.PumpUntil(() => h.Engine.CurrentIndex == 1, Timeout);

        var left = Left(h);
        var s = SoundStart(left);
        Assert.InRange(left.Length - s, 2 * R, 2 * R + 4 * Period);
        Assert.InRange(h.Engine.Position.TotalSeconds, 0, 4.0 * Period / R);
        Assert.Equal(4.0, h.Engine.Duration.TotalSeconds, 3);
        Assert.Equal(A, left[s + 2 * R - 1], 4);
        EngineHarness.WaitUntil(() => h.TrackChanges.Contains(1));

        h.PumpUntil(() => h.Engine.Position.TotalSeconds >= 0.5, Timeout);
        var heard = Frames(h) - s - 2 * R;
        Assert.InRange(h.Engine.Position.TotalSeconds * R - heard, -2 * Period, 2 * Period);
        Assert.Equal(1, h.Engine.CurrentIndex);
    }

    /// <summary>Czas skraca się do połowy krótszego z utworów; inaczej obowiązuje wybrany.</summary>
    [Theory]
    [InlineData(1.0, 3.0, 5.0, 0.5)]
    [InlineData(3.0, 1.0, 5.0, 0.5)]
    [InlineData(4.0, 4.0, 1.0, 1.0)]
    public void CzasSkracaSieDoPolowyKrotszegoUtworu(double first, double second, double crossfade, double expected)
    {
        using var h = Rig(crossfade);
        var left = PlayAll(h, h.Track("a", first, A), h.Track("b", second, 0));
        var s = SoundStart(left);
        var start = s + (int)((first - expected) * R);
        var length = (int)(expected * R);

        Assert.Equal(A, left[start - 10], 4);
        Assert.Equal(A * Math.Cos(Math.PI / 4), left[start + length / 2], 3);
        Assert.Equal(0, left[start + length + 10], 4);
    }

    // ---------- bez przenikania ----------

    [Fact]
    public void OstatniUtworKolejkiGraDoKonca()
    {
        using var h = Rig(1);
        var left = PlayAll(h, h.Track("a", 3, A));

        Assert.True(Mixed(left, 0, A) < FadeFrames, $"pośrednich: {Mixed(left, 0, A)}");
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, A) >= 3 * R - FadeFrames);
    }

    [Fact]
    public void UtworONieznanejDlugosciPrzechodziBezPrzenikania()
    {
        using var h = Rig(1);
        var left = PlayAll(h, Shaped(h, "a", 3, _ => A, declared: 0), h.Track("b", 3, B));

        Assert.True(Mixed(left, B, A) < FadeFrames, $"pośrednich: {Mixed(left, B, A)}");
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 3 * R - FadeFrames);
    }

    [Fact]
    public void RecznaZmianaUtworuNiePrzenika()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 6, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => !h.Engine.IsSeeking && h.Engine.Position.TotalSeconds > 1, Timeout);

        h.Engine.Next();
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        Assert.True(Mixed(left, B, A) < FadeFrames, $"pośrednich: {Mixed(left, B, A)}");
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 3 * R - FadeFrames);
    }

    /// <summary>Następca na albumie przechodzi bez przerwy, chyba że wyłączono „Albumy bez przenikania”.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NastepcaNaAlbumieBezPrzenikaniaZaleznieOdUstawienia(bool albumsWithout)
    {
        using var h = Rig(1);
        h.Engine.AlbumsWithoutCrossfade = albumsWithout;
        var a = h.Track("a", 3, A);
        var b = h.Track("b", 3, B);
        Tag(a, 1);
        Tag(b, 2);

        var mixed = Mixed(PlayAll(h, a, b), B, A);

        if (albumsWithout) Assert.True(mixed < FadeFrames, $"pośrednich: {mixed}");
        else Assert.True(mixed > R / 2, $"pośrednich: {mixed}");
    }

    /// <summary>Kolejne ścieżki arkusza grają dalej z jednego dekodera; bez ustawienia przenikają z dwóch.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KolejneSciezkiArkuszaZaleznieOdUstawienia(bool albumsWithout)
    {
        using var h = Rig(1);
        h.Engine.AlbumsWithoutCrossfade = albumsWithout;
        var path = Shaped(h, "koncert", 4, frame => frame < 2 * R ? A : B, declared: 4);

        h.Engine.SetQueue(new[]
        {
            new QueueItem(path, new SheetTrack { SheetPath = "koncert.cue", Number = 1, Start = 0, End = 150, Album = "Koncert" }),
            new QueueItem(path, new SheetTrack { SheetPath = "koncert.cue", Number = 2, Start = 150, End = null, Album = "Koncert" }),
        });
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var mixed = Mixed(Left(h), B, A);
        if (albumsWithout)
        {
            Assert.True(mixed < FadeFrames, $"pośrednich: {mixed}");
            Assert.Equal(1, h.Opened.Count(name => name == "koncert.wav"));
        }
        else
        {
            Assert.True(mixed > R / 2, $"pośrednich: {mixed}");
            Assert.Equal(2, h.Opened.Count(name => name == "koncert.wav"));
        }
    }

    /// <summary>Włączenie stacji w czasie nakładki: plik milknie po wyciszeniu, nic się z nim nie miesza.</summary>
    [Fact]
    public void DoStacjiBezPrzenikania()
    {
        using var rig = new LiveRig(Period);
        var h = rig.Harness;
        h.Engine.Crossfade = TimeSpan.FromSeconds(1);
        rig.NextDecoder = () => new ScriptedLiveDecoder(C).Feed(long.MaxValue / 2);

        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);

        h.Engine.PlayLive(LiveRig.Station());
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        h.PumpUntil(() => !h.Engine.IsSeeking, Timeout);
        h.PumpPeriods(100);

        var tail = Left(h)[(mark + FadeFrames)..];
        Assert.DoesNotContain(tail, v => v > C + 1e-3f);
        Assert.True(Steady(Left(h)[mark..], C) > 50 * Period);
    }

    /// <summary>Koniec stacji nie przechodzi do kolejki plików.</summary>
    [Fact]
    public void ZeStacjiBezPrzenikania()
    {
        using var rig = new LiveRig(Period);
        var h = rig.Harness;
        h.Engine.Crossfade = TimeSpan.FromSeconds(1);
        rig.NextDecoder = () =>
        {
            var decoder = new ScriptedLiveDecoder(C).Feed(R);
            decoder.End();
            return decoder;
        };

        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.Position.TotalSeconds > 0.3, Timeout);

        h.Engine.PlayLive(LiveRig.Station());
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        h.PumpUntil(() => h.Engine.State == PlaybackState.Paused, Timeout);
        Thread.Sleep(100);

        Assert.DoesNotContain("b.wav", h.Opened);
        Assert.DoesNotContain(Left(h)[(mark + FadeFrames)..], v => v > C + 1e-3f);
    }

    // ---------- wzmocnienia ----------

    /// <summary>W nakładce każdy utwór gra ze swoim wzmocnieniem, choć tor ma już wzmocnienie następnego.</summary>
    [Fact]
    public void ObaUtworyWNakladceGrajaZeSwoimWzmocnieniem()
    {
        using var h = Rig(1);
        h.Engine.Graph.GainSource = new FixedGains(new Dictionary<string, double> { ["a"] = -6, ["b"] = 6 });
        var left = PlayAll(h, h.Track("a", 3, A), h.Track("b", 3, B));
        var s = SoundStart(left);
        var gainA = Math.Pow(10, -6.0 / 20);
        var gainB = Math.Pow(10, 6.0 / 20);

        Assert.Equal(A * gainA, left[s + 2 * R - 1000], 3);
        for (var k = 0; k < R; k += 97)
        {
            var angle = k * Math.PI / 2 / R;
            var expected = A * gainA * Math.Cos(angle) + B * gainB * Math.Sin(angle);
            Assert.True(Math.Abs(left[s + 2 * R + k] - expected) < 2e-3, $"w {k}: {left[s + 2 * R + k]} zamiast {expected}");
        }

        Assert.Equal(B * gainB, left[s + 3 * R + 1000], 3);
    }

    /// <summary>
    /// Zmiana wyrównania w trakcie trzysekundowej nakładki, gdy następny jest już słyszany, a bufor pełny.
    /// Zwraca lewy kanał, początek nakładki w wyjściu i miejsce zmiany.
    /// </summary>
    private static (float[] Left, int Boundary, int Mark) ChangeDuringOverlap(
        bool normalisation, Dictionary<string, double> gains, Action<EngineHarness> change)
    {
        using var h = Rig(3);
        h.Engine.Graph.NormalisationEnabled = normalisation;
        h.Engine.Graph.GainSource = new FixedGains(gains);
        h.Engine.SetQueue([h.Track("a", 6, A), h.Track("b", 6, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.5, Timeout);
        EngineHarness.WaitUntil(() => h.Engine.BufferedFrames >= EngineHarness.Frames(0.45));

        var mark = Frames(h);
        change(h);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        return (left, SoundStart(left) + 3 * R, mark);
    }

    /// <summary>
    /// Po wybrzmieniu tego, co w chwili zmiany leżało w buforze (pół sekundy), nakładka gra oba utwory
    /// z nowymi wzmocnieniami, a przejście do nich nie ma skoku próbki.
    /// </summary>
    private static void AssertLevelsAfterChange(float[] left, int boundary, int mark, double gainA, double gainB)
    {
        var end = boundary + 3 * R - 100;
        for (var at = mark + (int)(0.6 * R); at < end; at += 97)
        {
            var angle = (at - boundary) * Math.PI / 2 / (3 * R);
            var expected = A * gainA * Math.Cos(angle) + B * gainB * Math.Sin(angle);
            Assert.True(Math.Abs(left[at] - expected) < 3e-3, $"w {at - boundary}: {left[at]} zamiast {expected}");
        }

        var step = LargestStep(left, mark, end);
        Assert.True(step < 0.005f, $"skok {step}");
    }

    private static readonly double Minus12 = Math.Pow(10, -12.0 / 20);

    [Fact]
    public void WylaczenieWyrownaniaWTrakcieNakladkiZdejmujeStosunekWzmocnien()
    {
        var (left, boundary, mark) = ChangeDuringOverlap(true, new() { ["a"] = 0, ["b"] = -12 }, h =>
        {
            h.Engine.Graph.NormalisationEnabled = false;
            h.Engine.RefreshTrackGain();
        });

        AssertLevelsAfterChange(left, boundary, mark, 1, 1);
    }

    [Fact]
    public void WlaczenieWyrownaniaWTrakcieNakladkiDajeStosunekWzmocnien()
    {
        var (left, boundary, mark) = ChangeDuringOverlap(false, new() { ["a"] = 0, ["b"] = -12 }, h =>
        {
            h.Engine.Graph.NormalisationEnabled = true;
            h.Engine.RefreshTrackGain();
        });

        AssertLevelsAfterChange(left, boundary, mark, 1, Minus12);
    }

    /// <summary>Nowe wartości źródła po odświeżeniu: oba utwory grają z nowymi, odczytanymi przy tych samych ustawieniach.</summary>
    [Fact]
    public void OdswiezenieWzmocnieniaWTrakcieNakladkiZmieniaObaUtwory()
    {
        var gains = new Dictionary<string, double> { ["a"] = -6, ["b"] = 0 };
        var (left, boundary, mark) = ChangeDuringOverlap(true, gains, h =>
        {
            gains["a"] = 0;
            gains["b"] = -12;
            h.Engine.RefreshTrackGain();
        });

        AssertLevelsAfterChange(left, boundary, mark, 1, Minus12);
    }

    /// <summary>Bez zmiany ustawień nakładka nie ma skoku także przy dużej różnicy wzmocnień.</summary>
    [Fact]
    public void NakladkaODuzejRoznicyWzmocnienBezSkoku()
    {
        var (left, boundary, _) = ChangeDuringOverlap(true, new() { ["a"] = 0, ["b"] = -12 }, _ => { });

        AssertLevelsAfterChange(left, boundary, boundary + 100, 1, Minus12);
    }

    // ---------- polecenia w trakcie nakładki ----------

    /// <summary>Przewinięcie w następnym utworze w trakcie nakładki: poprzedni już nie wraca, bez trzasku.</summary>
    [Fact]
    public void PrzewiniecieWNastepnymPorzucaPoprzedni()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.2, Timeout);

        h.Engine.SeekTo(TimeSpan.FromSeconds(1.5));
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        var positions = h.PositionsAfterSeek(5);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.InRange(positions[0], 1.5, 1.6);
        var left = Left(h);
        Assert.DoesNotContain(left[(mark + FadeFrames)..], v => v > B + 1e-3f);
        Assert.True(Steady(left[mark..], B) >= 1.4 * R);
        Assert.True(LargestStep(left, mark - 4 * Period, mark + R) < 0.01f, $"skok {LargestStep(left, mark - 4 * Period, mark + R)}");
    }

    /// <summary>Przewinięcie w utworze jeszcze słyszanym, gdy nakładka czeka w buforze: utwór gra od celu i przenika od nowa.</summary>
    [Fact]
    public void PrzewiniecieWPoprzednimPrzedGranicaPrzenikaOdNowa()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Engine.SeekTo(TimeSpan.FromSeconds(0.5));
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        var positions = h.PositionsAfterSeek(3);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.InRange(positions[0], 0.5, 0.6);
        Assert.Equal(2, h.Opened.Count(name => name == "b.wav"));
        var tail = Left(h)[mark..];
        Assert.InRange(Steady(tail, A), 1.4 * R, 1.55 * R);
        Assert.True(Mixed(tail, B, A) > 0.8 * R, $"pośrednich: {Mixed(tail, B, A)}");
    }

    /// <summary>Pauza i miękka pauza w trakcie nakładki: po wznowieniu nakładka kończy się raz, bez trzasku.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauzaWTrakcieNakladki(bool soft)
    {
        using var h = Rig(1);
        if (soft) h.Engine.SoftPause = TimeSpan.FromSeconds(0.05);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.3, Timeout);

        if (soft)
        {
            // Rampę miękkiej pauzy pompuje test sam; wątek naśladujący urządzenie zużyłby w czasie
            // zegarowym wyciszenia sekundy dźwięku.
            h.Engine.Pause();
            h.PumpPeriods(4);
            EngineHarness.WaitUntil(() => !h.Engine.IsSoftPausing);
        }
        else
        {
            h.Output.StartAutoPump(independentOfSystemClock: true);
            h.Engine.Pause();
            h.Output.StopAutoPump();
        }

        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.False(h.Output.IsRunning);

        h.Engine.Play();
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        var s = SoundStart(left);
        var lastLoud = Array.FindLastIndex(left, v => v > B - 0.01f);
        Assert.True(LargestStep(left, s + 400, lastLoud) < 0.01f, $"skok {LargestStep(left, s + 400, lastLoud)}");
        Assert.True(left[(s + 2 * R)..].Count(v => v > B + 0.01f) < R + FadeFrames, "nakładka dłuższa niż jej czas");
        Assert.True(Steady(left, B) >= 1.9 * R);
    }

    [Fact]
    public void NastepnyPoGranicyGraTrzeciBezDwochPierwszych()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B), h.Track("c", 2, C)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.2, Timeout);

        h.Engine.Next();
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var tail = Left(h)[mark..];
        Assert.DoesNotContain(tail[FadeFrames..], v => v > C + 1e-3f);
        Assert.True(Steady(tail, C) >= 1.9 * R);
    }

    /// <summary>„Następny”, gdy nakładka czeka w buforze, a słychać jeszcze poprzedni: następny gra od początku, sam.</summary>
    [Fact]
    public void NastepnyPrzedGranicaGraNastepnyOdPoczatkuSam()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Engine.Next();
        h.WaitForSeekToBeTaken();
        var mark = Frames(h);
        var positions = h.PositionsAfterSeek(3);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.True(positions[0] < 0.1, $"pozycja {positions[0]}");
        var tail = Left(h)[mark..];
        Assert.DoesNotContain(tail[FadeFrames..], v => v > B + 1e-3f);
        Assert.True(Steady(tail, B) >= 3 * R - FadeFrames);
    }

    [Fact]
    public void PoprzedniWTrakcieNakladkiWracaDoPoczatkuPoprzedniego()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.2, Timeout);

        h.Engine.Previous();
        var positions = h.PositionsAfterSeek(3);

        Assert.Equal(0, h.Engine.CurrentIndex);
        Assert.True(positions[0] < 0.1, $"pozycja {positions[0]}");

        h.PumpUntil(() => h.Finished > 0, Timeout);
        Assert.Equal(2, h.Opened.Count(name => name == "b.wav"));
    }

    /// <summary>Usunięcie następnego, gdy nakładka czeka w buforze: poprzedni gra dalej i przenika w kolejny.</summary>
    [Fact]
    public void UsuniecieNastepnegoPrzedGranicaPrzenikaWKolejny()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B), h.Track("c", 3, C)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Engine.RemoveAt(1);
        var mark = Frames(h);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var tail = Left(h)[mark..];
        Assert.Equal(1, h.Opened.Count(name => name == "b.wav"));
        Assert.Contains("c.wav", h.Opened);
        Assert.True(Steady(tail, B) < FadeFrames, $"B: {Steady(tail, B)}");
        Assert.True(Steady(tail, C) >= 1.9 * R);
        Assert.True(Mixed(tail, C, A) > 0.8 * R, $"pośrednich: {Mixed(tail, C, A)}");
    }

    [Fact]
    public void UsuniecieSlyszanegoWTrakcieNakladkiZatrzymuje()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.2, Timeout);

        h.Engine.RemoveAt(1);

        Assert.Equal(PlaybackState.Stopped, h.Engine.State);
        Assert.Equal(-1, h.Engine.CurrentIndex);
        Assert.False(h.Output.IsRunning);

        // Zatrzymane wyjście nie gra już niczego, także końcówki wyciszanego utworu.
        var frames = Frames(h);
        h.PumpPeriods(20);
        Assert.Equal(frames, Frames(h));
    }

    /// <summary>
    /// Następny usunięty z kolejki w chwili, gdy staje się słyszany: gra z bufora, a potem odtwarzanie
    /// przechodzi na wpis po poprzednim, bez kończenia kolejki przed czasem.
    /// </summary>
    [Fact]
    public void NastepnyUsunietyGdyStajeSieSlyszanyOddajeMiejsceKolejnemu()
    {
        using var h = Rig(0.3);
        var b = h.Track("b", 1, B);
        h.Engine.SetQueue([h.Track("a", 1, A), b, h.Track("c", 1, C)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);

        h.Engine.AfterRemovalChecked = () => h.PumpUntil(() => h.Engine.SignalPath.Entry?.Path == b);
        h.Engine.RemoveAt(1);
        h.Engine.AfterRemovalChecked = null;

        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) < R / 2, $"B: {EngineHarness.CountFrames(h.Output.Samples, B)}");
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, C) >= 0.9 * R);
    }

    [Fact]
    public void PrzestawienieKolejkiPrzedGranicaPrzenikaWNowyNastepny()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B), h.Track("c", 3, C)]);
        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);
        Assert.Equal(0, h.Engine.CurrentIndex);

        h.Engine.Move(2, 1);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var opened = h.Opened.ToList();
        Assert.True(opened.LastIndexOf("c.wav") < opened.LastIndexOf("b.wav"), string.Join(", ", opened));
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, C) >= 0.9 * R);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 1.9 * R);
    }

    /// <summary>Zmiana urządzenia w trakcie nakładki: nowe wyjście gra tylko następny utwór, od jego miejsca.</summary>
    [Fact]
    public void ZmianaUrzadzeniaWTrakcieNakladki()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.3, Timeout);
        var first = h.Output;

        h.Engine.SwitchDevice("Druga");
        Assert.NotSame(first, h.Output);
        Assert.Equal(1, h.Engine.CurrentIndex);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        Assert.DoesNotContain(left, v => v > B + 1e-3f);
        Assert.InRange(EngineHarness.CountFrames(h.Output.Samples, B), 2.4 * R, 2.75 * R);
    }

    [Fact]
    public void UtrataUrzadzeniaWTrakcieNakladki()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1 && h.Engine.Position.TotalSeconds > 0.3, Timeout);

        h.Output.Lose();
        EngineHarness.WaitUntil(() => h.Lost == 1);
        Assert.Equal(PlaybackState.Paused, h.Engine.State);

        h.Engine.Play();
        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.Equal(2, h.Outputs.Count);
        Assert.DoesNotContain(Left(h), v => v > B + 1e-3f);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 2.4 * R);
    }

    /// <summary>Zmiana czasu w trakcie nakładki nie zmienia trwającej; działa od następnego przejścia.</summary>
    [Fact]
    public void ZmianaCzasuDzialaOdNastepnegoPrzejscia()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 6, A), h.Track("b", 6, B), h.Track("c", 6, 0)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1, Timeout);

        h.Engine.Crossfade = TimeSpan.FromSeconds(2);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        var s = SoundStart(left);
        Assert.Equal(B, left[s + 6 * R + 200], 4);
        Assert.Equal(B, left[s + 9 * R - 200], 4);
        Assert.Equal(B * Math.Cos(Math.PI / 4), left[s + 10 * R], 3);
        Assert.Equal(0, left[s + 11 * R + 200], 4);
    }

    [Fact]
    public void WylaczeniePrzenikaniaWTrakcieNakladkiKonczyJaINastepnePrzejscieJestBezPrzerwy()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A), h.Track("b", 3, B), h.Track("c", 3, 0)]);
        h.PumpUntil(() => h.Engine.CurrentIndex == 1, Timeout);

        h.Engine.Crossfade = TimeSpan.Zero;
        h.PumpUntil(() => h.Finished > 0, Timeout);

        var left = Left(h);
        var s = SoundStart(left);
        Assert.True(Mixed(left[(s + 2 * R)..(s + 3 * R)], B, A) > 0.8 * R);
        Assert.Equal(B, left[s + 3 * R + 200], 4);
        Assert.True(Mixed(left[(s + 3 * R + 200)..], 0, B) < FadeFrames, $"pośrednich: {Mixed(left[(s + 3 * R + 200)..], 0, B)}");
    }

    // ---------- długość inna niż podana ----------

    /// <summary>Poprzedni kończy się w nakładce wcześniej, niż podawał: następny narasta dalej sam, bez dziury.</summary>
    [Fact]
    public void PoprzedniKrotszyNizPodawalNastepnyGraDalejSam()
    {
        using var h = Rig(2);
        var left = PlayAll(h, Shaped(h, "a", 3, _ => A, declared: 4), h.Track("b", 4, B));
        var s = SoundStart(left);
        var start = s + 2 * R;

        var angle = Math.PI / 8;
        Assert.Equal(A * Math.Cos(angle) + B * Math.Sin(angle), left[start + R / 2], 3);
        Assert.Equal(B * Math.Sin(0.75 * Math.PI / 2), left[start + 3 * R / 2], 3);
        Assert.DoesNotContain(left[(s + 400)..(start + 3 * R)], v => v < 0.05f);
        Assert.Equal(B, left[start + 2 * R + 200], 4);
    }

    /// <summary>Poprzedni dłuższy, niż podawał: jego reszta po nakładce przepada.</summary>
    [Fact]
    public void PoprzedniDluzszyNizPodawalPrzepadaPoNakladce()
    {
        using var h = Rig(1);
        var left = PlayAll(h, Shaped(h, "a", 4, _ => A, declared: 3), h.Track("b", 3, B));
        var s = SoundStart(left);

        Assert.True(Mixed(left[(s + 2 * R)..(s + 3 * R)], B, A) > 0.8 * R);
        Assert.DoesNotContain(left[(s + 3 * R + 10)..], v => v > B + 1e-3f);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 1.9 * R);
    }

    /// <summary>Poprzedni kończy się przed początkiem nakładki skróconej przez krótki następny: przejście bez przerwy.</summary>
    [Fact]
    public void PoprzedniKonczySiePrzedNakladkaNastepnyZaczynaBezPrzerwy()
    {
        using var h = Rig(2);
        var left = PlayAll(h, Shaped(h, "a", 4.5, _ => A, declared: 6), h.Track("b", 1, B));
        var s = SoundStart(left);

        Assert.True(Mixed(left, B, A) < FadeFrames, $"pośrednich: {Mixed(left, B, A)}");
        Assert.Equal(A, left[s + (int)(4.5 * R) - 1], 4);
        Assert.Equal(B, left[s + (int)(4.5 * R) + 10], 4);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= R - FadeFrames);
    }

    // ---------- powtarzanie, losowanie, pliki bez dźwięku ----------

    /// <summary>
    /// Powtarzanie utworu przenika utwór sam w siebie: koniec (B) nakłada się na początek (A). Utwór ma
    /// znacznik albumu bez numerów, więc sam dla siebie nie może uchodzić za następcę na albumie.
    /// </summary>
    [Fact]
    public void PowtarzanieUtworuPrzenikaUtworSamWSiebie()
    {
        using var h = Rig(0.5);
        h.Engine.Repeat = RepeatMode.Track;
        var a = Shaped(h, "a", 2, frame => frame < R ? A : B, declared: 2);
        Tag(a, null);
        h.Engine.SetQueue([a]);

        h.PumpUntil(() => h.Opened.Count(name => name == "a.wav") >= 3, Timeout);
        EngineHarness.WaitUntil(() => h.TrackChanges.Count(index => index == 0) >= 2);

        Assert.Equal(0, h.Finished);
        Assert.Equal(0, h.Engine.CurrentIndex);
        Assert.True(Mixed(Left(h), B, A) > R / 4, $"pośrednich: {Mixed(Left(h), B, A)}");
    }

    [Fact]
    public void PowtarzanieKolejkiPrzenikaOstatniWPierwszy()
    {
        using var h = Rig(0.3);
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("a", 1, A), h.Track("b", 1, B)]);

        h.PumpUntil(() => h.Opened.Count(name => name == "a.wav") >= 3, Timeout);

        Assert.Equal(0, h.Finished);
        Assert.True(Mixed(Left(h), B, A) > R / 2, $"pośrednich: {Mixed(Left(h), B, A)}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void LosowanieGraKazdyUtworRazZPrzenikaniem(int seed)
    {
        using var h = Rig(0.3, seed);
        h.Engine.Shuffle = true;
        float[] values = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f];
        var left = PlayAll(h, values.Select((value, i) => h.Track("t" + i, 1, value)).ToArray());

        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(1, h.Opened.Count(name => name == $"t{i}.wav"));
            Assert.True(EngineHarness.CountFrames(h.Output.Samples, values[i]) >= 0.35 * R, $"t{i}");
        }

        Assert.True(Mixed(left, 0.1f, 0.5f) > R, "bez nakładek");
    }

    /// <summary>Plik, który podaje długość, ale nie daje dźwięku: nakładka nie liczy mu dźwięku poprzedniego.</summary>
    [Fact]
    public void PustyPlikZDlugosciaMiedzyGrajacymiNieZatrzymujePowtarzania()
    {
        using var h = Rig(0.2);
        h.Engine.Repeat = RepeatMode.Queue;
        h.Engine.SetQueue([h.Track("gra", 0.6, A), Shaped(h, "pusty", 0, _ => B, declared: 2)]);

        h.PumpUntil(() => h.Opened.Count(name => name == "gra.wav") >= 4, Timeout);

        Assert.Equal(0, h.Finished);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    [Theory]
    [InlineData(RepeatMode.Queue)]
    [InlineData(RepeatMode.Track)]
    public void KoloPustychPlikowZDlugosciaKonczyKolejke(RepeatMode repeat)
    {
        using var h = Rig(0.5);
        h.Engine.Repeat = repeat;
        h.Engine.SetQueue([Shaped(h, "pusty1", 0, _ => A, declared: 2), Shaped(h, "pusty2", 0, _ => B, declared: 2)]);

        h.PumpUntil(() => h.Finished > 0, Timeout);
        Thread.Sleep(200);

        Assert.True(h.Opened.Count <= 4, $"pliki otwarto {h.Opened.Count} razy");
    }

    // ---------- następny nie do otwarcia, zmiana kolejki po odmowie ----------

    /// <summary>Żadnego następnego nie da się otworzyć: utwór gra do końca, a błąd jest zgłoszony raz.</summary>
    [Fact]
    public void NastepnyNieDoOtwarciaZgloszonyRazAUtworGraDoKonca()
    {
        using var h = Rig(0.5);
        var missing = Path.Combine(Path.GetTempPath(), "cewka-brak-" + Guid.NewGuid().ToString("N") + ".wav");
        PlayAll(h, h.Track("a", 2, A), missing);
        Thread.Sleep(100);

        Assert.Single(h.Failures);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, A) >= 2 * R - FadeFrames);
    }

    /// <summary>Dopisanie utworu, gdy ostatni jest już w miejscu przenikania: przenika w dopisany.</summary>
    [Fact]
    public void DopisanieDoKolejkiPoOdmowiePrzenikaWDopisany()
    {
        using var h = Rig(1);
        h.Engine.SetQueue([h.Track("a", 3, A)]);
        h.PumpUntil(() => h.Engine.Position.TotalSeconds >= 1.6, Timeout);

        // Dekoder jest wtedy pół sekundy dalej, za miejscem przenikania, i już odmówił: kolejka się kończyła.
        EngineHarness.WaitUntil(() => h.Engine.BufferedFrames >= EngineHarness.Frames(0.45));

        h.Engine.Enqueue([h.Track("b", 3, B)]);
        h.PumpUntil(() => h.Finished > 0, Timeout);

        Assert.True(Mixed(Left(h), B, A) > R / 2, $"pośrednich: {Mixed(Left(h), B, A)}");
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, B) >= 1.9 * R);
    }

    /// <summary>Źródło wzmocnienia, które rzuca wyjątek dla wskazanego pliku.</summary>
    private sealed class ThrowingGains(string name) : Cewka.Audio.Dsp.ITrackGainSource
    {
        public Cewka.Audio.Dsp.TrackGain? GetTrackGain(QueueEntry entry) =>
            Path.GetFileNameWithoutExtension(entry.Path) == name
                ? throw new InvalidOperationException("Błąd źródła wzmocnienia.")
                : null;
    }

    /// <summary>
    /// Wyjątek przy rozpoczęciu nakładki (źródło wzmocnienia następnego utworu): oba dekodery mają
    /// właściciela i zostają zamknięte zwykłą drogą błędu, a odtwarzanie idzie dalej.
    /// </summary>
    [Fact]
    public void WyjatekPrzyRozpoczeciuNakladkiZamykaObaDekodery()
    {
        using var h = Rig(1);
        h.Engine.Graph.GainSource = new ThrowingGains("b");
        var decoders = new System.Collections.Concurrent.ConcurrentQueue<SyntheticDecoder>();
        string Tracked(string name, float value) => h.Track(name, () =>
        {
            var decoder = new SyntheticDecoder(EngineHarness.Frames(3), value, EngineHarness.Channels, EngineHarness.Rate);
            decoders.Enqueue(decoder);
            return decoder;
        });

        h.Engine.SetQueue([Tracked("a", A), Tracked("b", B), Tracked("c", C)]);
        h.PumpUntil(() => h.Finished > 0, Timeout);
        Thread.Sleep(100);

        Assert.Contains("b.wav", h.Opened);
        Assert.All(decoders, decoder => Assert.Equal(1, decoder.DisposeCount));
        Assert.Contains(h.Failures, failure => failure.Index == 1);
        Assert.True(EngineHarness.CountFrames(h.Output.Samples, C) >= 1.9 * R);
    }

    // ---------- tor sygnału ----------

    [Fact]
    public void MigawkaToruWskazujeUtworBiezacyWTrakcieNakladki()
    {
        using var h = Rig(1);
        var a = h.Track("a", 3, 0.1f, d => new SyntheticDecoder(d.TotalFrames, 0.1f, d.Channels, d.SampleRate) { SourceSampleRate = 44100 });
        var b = h.Track("b", 3, 0.2f, d => new SyntheticDecoder(d.TotalFrames, 0.2f, d.Channels, d.SampleRate) { SourceSampleRate = 96000 });
        h.Engine.SetQueue([a, b]);

        h.PumpUntil(() => h.Opened.Contains("b.wav"), Timeout);
        Assert.Equal(a, h.Engine.SignalPath.Entry?.Path);
        Assert.Equal(44100, h.Engine.SignalPath.SourceSampleRate);

        h.PumpUntil(() => h.Engine.CurrentIndex == 1, Timeout);
        Assert.Equal(b, h.Engine.SignalPath.Entry?.Path);
        Assert.Equal(96000, h.Engine.SignalPath.SourceSampleRate);
    }
}

/// <summary>Dekoder o wartości zależnej od ramki i długości podanej inaczej niż faktyczna.</summary>
internal sealed class ShapedDecoder(long frames, Func<long, float> value, long declared) : IAudioDecoder
{
    private long _position;

    public int SampleRate => EngineHarness.Rate;
    public int Channels => EngineHarness.Channels;
    public long TotalFrames => declared;
    public long Position => _position;
    public bool CanSeek => true;
    public int SourceSampleRate => SampleRate;
    public int SourceChannels => Channels;
    public int InstantaneousBitrate => 0;

    public int Read(Span<float> destination)
    {
        var count = (int)Math.Clamp(frames - _position, 0, destination.Length / Channels);
        for (var i = 0; i < count; i++) destination.Slice(i * Channels, Channels).Fill(value(_position + i));
        _position += count;
        return count;
    }

    public void Seek(long frame) => _position = Math.Clamp(frame, 0, frames);

    public void Dispose()
    {
    }
}
