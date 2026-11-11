using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

public class CompressedBufferTests
{
    /// <summary>128 kb/s to 16 000 B/s; progi domyślne dają 24 000 B na start i 48 000 B po przerwie.</summary>
    private const int Rate = 16000;

    private static CompressedBuffer Buffer(LiveOptions? options = null) => new(options ?? new LiveOptions());

    /// <summary>Nagłówek w bitach albo liczba spoza zakresu nie może przestawić progów ani ich przepełnić.</summary>
    [Theory]
    [InlineData(128000)]
    [InlineData(4294968)]
    [InlineData(2147483647)]
    [InlineData(536870912)]
    [InlineData(1)]
    public void ImplausibleBitrateCountsAsUnknown(int kilobits)
    {
        foreach (var report in new Action<CompressedBuffer>[] { b => b.SetBitrate(kilobits), b => b.ReportBitrate(kilobits) })
        {
            var buffer = Buffer();
            buffer.BeginStream(LiveCodec.Mp3, false);
            report(buffer);

            buffer.Write(new byte[24000 - 1]);
            Assert.False(buffer.IsGateOpen);
            buffer.Write(new byte[1]);
            Assert.True(buffer.IsGateOpen);

            var (bytes, _, _) = Drain(buffer);
            Assert.Equal(24000, bytes.Count);
        }
    }

    /// <summary>Śmieci z połączenia, które już się skończyło, nie obciążają bieżącego adresu.</summary>
    [Fact]
    public void UndecodableOlderConnectionSkipsToTheNextOneWithoutReporting()
    {
        var buffer = Buffer();
        var reported = 0;
        buffer.Undecodable += _ => reported++;
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[30000]);
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(Sequence(100));
        var scratch = new byte[1000];
        buffer.Read(scratch, out _, out _);
        buffer.Read(scratch, out _, out _);

        Assert.True(buffer.ReportUndecodable(new LiveFailure(LiveFailureReason.UndecodableData)));

        Assert.Equal(0, reported);
        var (bytes, markers, _) = Drain(buffer);
        Assert.Equal(Sequence(100), bytes);
        Assert.IsType<StreamBegin>(Assert.Single(markers).Marker);
    }

    /// <summary>Śmieci z bieżącego połączenia: reszta przepada, a kolejne bajty czekają na nowe połączenie.</summary>
    [Fact]
    public void UndecodableCurrentConnectionIsReportedAndItsBytesAreDropped()
    {
        var buffer = Buffer();
        LiveFailure? reported = null;
        var failure = new LiveFailure(LiveFailureReason.UndecodableData);
        buffer.Undecodable += f => reported = f;
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[30000]);
        buffer.Read(new byte[1000], out _, out _);

        Assert.True(buffer.ReportUndecodable(failure));
        buffer.Write(new byte[30000]);
        buffer.AddTitle("Stary");

        Assert.Equal(failure, reported);
        Assert.Empty(Drain(buffer).Bytes);

        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(Sequence(50000));
        var (bytes, markers, _) = Drain(buffer);
        Assert.Equal(50000, bytes.Count);
        Assert.IsType<StreamBegin>(Assert.Single(markers).Marker);
    }

    [Fact]
    public void UndecodableAfterTheEndHasNoFallback()
    {
        var buffer = Buffer();
        buffer.Undecodable += _ => { };
        buffer.Complete(null);

        Assert.False(buffer.ReportUndecodable(new LiveFailure(LiveFailureReason.UndecodableData)));
    }

    private static byte[] Sequence(int count, int start = 0)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++)
            data[i] = (byte)(start + i);
        return data;
    }

    /// <summary>Wszystko, co da się przeczytać: bajty i znaczniki po kolei.</summary>
    private static (List<byte> Bytes, List<(int Position, StreamMarker Marker)> Markers, LiveReadStatus Last) Drain(
        CompressedBuffer buffer, int chunk = 1000)
    {
        var bytes = new List<byte>();
        var markers = new List<(int, StreamMarker)>();
        var scratch = new byte[chunk];
        while (true)
        {
            var status = buffer.Read(scratch, out var count, out var marker);
            if (status != LiveReadStatus.Data)
                return (bytes, markers, status);
            if (marker is not null)
                markers.Add((bytes.Count, marker));
            bytes.AddRange(scratch.AsSpan(0, count).ToArray());
        }
    }

    [Fact]
    public void BytesAndMarkersKeepOrderAndPlace()
    {
        var buffer = Buffer();
        var audio = Sequence(40000);
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(audio.AsSpan(0, 10000));
        buffer.AddTitle("A");
        buffer.Write(audio.AsSpan(10000, 15000));
        buffer.AddTitle(null);
        buffer.BeginStream(LiveCodec.Aac, false);
        buffer.Write(audio.AsSpan(25000));

        var (bytes, markers, last) = Drain(buffer);

        Assert.Equal(audio, bytes);
        Assert.Equal(
            new (int, StreamMarker)[]
            {
                (0, new StreamBegin(LiveCodec.Mp3)),
                (10000, new StreamTitle("A")),
                (25000, new StreamTitle(null)),
                (25000, new StreamBegin(LiveCodec.Aac)),
            },
            markers);
        Assert.Equal(LiveReadStatus.Waiting, last);
    }

    [Fact]
    public void MarkerIsReturnedAloneWithZeroBytes()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[Rate * 2]);
        var scratch = new byte[100];

        Assert.Equal(LiveReadStatus.Data, buffer.Read(scratch, out var count, out var marker));

        Assert.Equal(0, count);
        Assert.IsType<StreamBegin>(marker);
    }

    [Fact]
    public void ReadNeverCrossesTitlePosition()
    {
        var buffer = Buffer();
        buffer.Write(new byte[100]);
        buffer.AddTitle("A");
        buffer.Write(new byte[Rate * 2]);
        var scratch = new byte[1000];

        buffer.Read(scratch, out var count, out _);

        Assert.Equal(100, count);
    }

    [Fact]
    public void GateOpensAtStartThresholdWithBitrateFromHeader()
    {
        var buffer = Buffer();
        var events = new List<bool>();
        buffer.GateChanged += events.Add;
        buffer.SetBitrate(320);
        var scratch = new byte[100];

        // 1,5 s przy 320 kb/s to 60 000 B.
        buffer.Write(new byte[59999]);
        Assert.Equal(LiveReadStatus.Waiting, buffer.Read(scratch, out _, out _));
        Assert.Empty(events);

        buffer.Write(new byte[1]);
        Assert.Equal(new[] { true }, events);
        Assert.Equal(LiveReadStatus.Data, buffer.Read(scratch, out _, out _));
    }

    [Fact]
    public void MinimumBytesApplyToLowBitrate()
    {
        var buffer = Buffer();
        buffer.SetBitrate(8);

        // 1,5 s przy 8 kb/s to 1500 B, ale obowiązuje 16 KB.
        buffer.Write(new byte[16383]);
        Assert.False(buffer.IsGateOpen);
        buffer.Write(new byte[1]);
        Assert.True(buffer.IsGateOpen);
    }

    [Fact]
    public void MeasuredBitrateWinsOverHeader()
    {
        var buffer = Buffer();
        buffer.SetBitrate(320);
        buffer.ReportBitrate(96);

        // 1,5 s przy 96 kb/s to 18 000 B.
        buffer.Write(new byte[18000]);

        Assert.True(buffer.IsGateOpen);
    }

    /// <summary>Adres zapasowy o innej przepływności: próg liczony z poprzedniego wydłużałby ciszę.</summary>
    [Fact]
    public void NewConnectionForgetsTheBitrateOfThePreviousOne()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(320);
        Drain(buffer);
        buffer.ReportBitrate(320);

        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(64);

        Assert.Equal(8000, buffer.AudioBytesPerSecond);
    }

    [Fact]
    public void NewConnectionWithoutHeaderUsesTheDefaultBitrate()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(320);
        Drain(buffer);
        buffer.ReportBitrate(320);

        buffer.BeginStream(LiveCodec.Mp3, false);

        Assert.Equal(Rate, buffer.AudioBytesPerSecond);
    }

    /// <summary>Dekoder dogrywa jeszcze stare połączenie: jego pomiar nie dotyczy nowego.</summary>
    [Fact]
    public void ReportFromTheOldConnectionDoesNotMoveTheThresholdsOfTheNewOne()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[30000]);
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(64);

        buffer.ReportBitrate(320);

        Assert.Equal(8000, buffer.AudioBytesPerSecond);
    }

    [Fact]
    public void ReportAfterTheNewConnectionStartedCounts()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[30000]);
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.SetBitrate(64);
        Drain(buffer);

        buffer.ReportBitrate(96);

        Assert.Equal(12000, buffer.AudioBytesPerSecond);
    }

    [Fact]
    public void EmptyBufferClosesGateAndReopeningNeedsMore()
    {
        var buffer = Buffer();
        var events = new List<bool>();
        buffer.GateChanged += events.Add;
        buffer.Write(new byte[24000]);
        Drain(buffer);
        Assert.Equal(new[] { true, false }, events);

        // Histereza: po przerwie 3 s zamiast 1,5 s.
        buffer.Write(new byte[47999]);
        Assert.False(buffer.IsGateOpen);
        Assert.Equal(new[] { true, false }, events);

        buffer.Write(new byte[1]);
        Assert.Equal(new[] { true, false, true }, events);
    }

    [Fact]
    public void OverflowJumpsToTargetWithBeginAndLastSkippedTitle()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.AddTitle("Stary");
        buffer.Write(new byte[Rate]);
        buffer.AddTitle("Pominiety");
        buffer.Write(Sequence(Rate * 29));
        Assert.Equal(0, buffer.DroppedBytes);

        buffer.Write(Sequence(1000));

        Assert.Equal(Rate * 20 + 1000, buffer.DroppedBytes);
        Assert.Equal(10, buffer.BufferedSeconds, 3);

        var (bytes, markers, _) = Drain(buffer);
        Assert.Equal(
            new (int, StreamMarker)[] { (0, new StreamBegin(LiveCodec.Mp3)), (0, new StreamTitle("Pominiety")) },
            markers);
        Assert.Equal(Rate * 10, bytes.Count);
    }

    [Fact]
    public void OverflowKeepsLaterTitleInPlaceAndUsesSkippedCodec()
    {
        var buffer = Buffer();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(new byte[Rate * 10]);
        buffer.BeginStream(LiveCodec.Aac, false);
        buffer.Write(new byte[Rate * 15]);
        buffer.AddTitle("Dalej");
        buffer.Write(new byte[Rate * 5]);
        buffer.Write(new byte[1]);

        var (_, markers, _) = Drain(buffer);

        Assert.Equal(new StreamBegin(LiveCodec.Aac), markers[0].Marker);
        Assert.Equal(new StreamTitle("Dalej"), markers[1].Marker);
        Assert.Equal(Rate * 5 - 1, markers[1].Position);
    }

    [Fact]
    public void HardLimitWinsOverSeconds()
    {
        var buffer = Buffer(new LiveOptions { HardLimitBytes = 64 * 1024 });
        buffer.SetBitrate(320);

        for (var i = 0; i < 20; i++)
            buffer.Write(new byte[8192]);

        Assert.True(buffer.DroppedBytes > 0);
        Assert.True(buffer.BufferedSeconds * 40000 <= 64 * 1024);
        Assert.True(buffer.IsGateOpen);
    }

    [Fact]
    public void WriteLargerThanRingKeepsTail()
    {
        var buffer = Buffer(new LiveOptions { HardLimitBytes = 32 * 1024, MinStartBytes = 1024 });
        var audio = Sequence(100 * 1024);

        buffer.Write(audio);

        var (bytes, _, _) = Drain(buffer);
        Assert.Equal(audio[^bytes.Count..], bytes);
    }

    [Fact]
    public void CompleteOpensGateGivesRestThenEnded()
    {
        var buffer = Buffer();
        var events = new List<bool>();
        buffer.GateChanged += events.Add;
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.Write(Sequence(500));
        buffer.AddTitle("Koniec");

        var failure = new LiveFailure(LiveFailureReason.StreamEnded);
        buffer.Complete(failure);

        Assert.Equal(new[] { true }, events);
        var (bytes, markers, last) = Drain(buffer);
        Assert.Equal(500, bytes.Count);
        Assert.Equal(2, markers.Count);
        Assert.Equal(LiveReadStatus.Ended, last);
        Assert.Equal(failure, buffer.Failure);
    }

    [Fact]
    public void CompleteWithoutFailureMeansUserStopped()
    {
        var buffer = Buffer();

        buffer.Complete(null);
        buffer.Complete(new LiveFailure(LiveFailureReason.Stalled));

        Assert.Equal(LiveReadStatus.Ended, buffer.Read(new byte[1], out _, out _));
        Assert.Null(buffer.Failure);
    }

    [Fact]
    public void WritesAfterCompleteAreIgnored()
    {
        var buffer = Buffer();
        buffer.Complete(null);

        buffer.Write(new byte[100]);
        buffer.AddTitle("x");
        buffer.BeginStream(LiveCodec.Mp3, false);

        Assert.Equal(LiveReadStatus.Ended, buffer.Read(new byte[10], out var count, out var marker));
        Assert.Equal(0, count);
        Assert.Null(marker);
    }

    [Fact]
    public void DiscardPendingDropsOldBytesAndMarkers()
    {
        var buffer = Buffer();
        var events = new List<bool>();
        buffer.BeginStream(LiveCodec.Mp3, false);
        buffer.AddTitle("Sprzed uspienia");
        buffer.Write(new byte[30000]);
        buffer.GateChanged += events.Add;

        buffer.BeginStream(LiveCodec.Mp3, discardPending: true);

        Assert.Equal(30000, buffer.DroppedBytes);
        Assert.Equal(new[] { false }, events);
        Assert.False(buffer.IsGateOpen);

        buffer.Write(Sequence(48000));
        var (bytes, markers, _) = Drain(buffer);
        Assert.Equal(new (int, StreamMarker)[] { (0, new StreamBegin(LiveCodec.Mp3)) }, markers);
        Assert.Equal(48000, bytes.Count);
    }

    [Fact]
    public void LastWriteComesFromOptionsClock()
    {
        var clock = new ManualClock();
        var buffer = Buffer(new LiveOptions { Time = clock });
        Assert.Null(buffer.LastWrite);

        buffer.Write(new byte[1]);

        Assert.Equal(clock.GetUtcNow(), buffer.LastWrite);
    }

    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    public async Task TwoThreadsKeepOrder(int seed)
    {
        var options = new LiveOptions { HardLimitBytes = 64 * 1024, MinStartBytes = 1024, MinRebufferBytes = 2048 };
        var buffer = new CompressedBuffer(options);
        const int total = 2_000_000;

        var writer = Task.Run(() =>
        {
            var random = new Random(seed);
            var written = 0;
            buffer.BeginStream(LiveCodec.Mp3, false);
            while (written < total)
            {
                var size = Math.Min(random.Next(1, 4000), total - written);
                buffer.Write(Sequence(size, written));
                written += size;
                if (random.Next(20) == 0)
                    buffer.AddTitle(written.ToString());
                if (random.Next(10) == 0)
                    Thread.Yield();
            }

            buffer.Complete(new LiveFailure(LiveFailureReason.StreamEnded));
        });

        // Odczyt pilnuje ciągłości tylko między nieciągłościami: przepełnienie traci bajty.
        var reader = Task.Run(() =>
        {
            var random = new Random(seed + 1);
            var scratch = new byte[8192];
            byte? expected = null;
            long read = 0;
            var lastTitle = -1;
            while (true)
            {
                var status = buffer.Read(scratch.AsSpan(0, random.Next(1, scratch.Length)), out var count, out var marker);
                if (status == LiveReadStatus.Ended)
                    return (read, lastTitle);
                if (status == LiveReadStatus.Waiting)
                {
                    Thread.Yield();
                    continue;
                }

                if (marker is StreamBegin)
                    expected = null;
                if (marker is StreamTitle { Title: { } title })
                {
                    var position = int.Parse(title);
                    Assert.True(position > lastTitle, "tytuły poza kolejnością");
                    lastTitle = position;
                }

                for (var i = 0; i < count; i++)
                {
                    if (expected is { } value)
                        Assert.Equal(value, scratch[i]);
                    expected = (byte)(scratch[i] + 1);
                }

                read += count;
            }
        });

        await writer.WaitAsync(TimeSpan.FromSeconds(30));
        var (readTotal, _) = await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(total, readTotal + buffer.DroppedBytes);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
