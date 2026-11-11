using System.Buffers;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming.Hls;
using Xunit;

namespace Cewka.Audio.Tests.Streaming.Hls;

public sealed class TsDemuxerTests
{
    private static readonly byte[] Audio = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();

    private static byte[] Demux(params byte[][] parts)
    {
        var demuxer = new TsDemuxer();
        var output = new ArrayBufferWriter<byte>();
        foreach (var part in parts) demuxer.Push(part, output);
        return output.WrittenSpan.ToArray();
    }

    [Fact]
    public void AudioTrackComesOutWhole()
    {
        Assert.Equal(Audio, Demux(TsTestFile.Mux(Audio)));
    }

    [Fact]
    public void VideoAndMetadataTracksAreLeftOut()
    {
        Assert.Equal(Audio, Demux(TsTestFile.Mux(Audio, withOtherTracks: true)));
    }

    [Fact]
    public void Mp3TrackIsAudioToo()
    {
        Assert.Equal(Audio, Demux(TsTestFile.Mux(Audio, TsTestFile.StreamTypeMp3)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(187)]
    [InlineData(189)]
    public void ChunkSizeDoesNotMatter(int size)
    {
        var file = TsTestFile.Mux(Audio, withOtherTracks: true);

        Assert.Equal(Audio, Demux(file.Chunk(size).ToArray()));
    }

    [Fact]
    public void BytesBeforeTheFirstPacketAreSkipped()
    {
        byte[] data = [1, 2, 3, .. TsTestFile.Mux(Audio)];

        Assert.Equal(Audio, Demux(data));
    }

    [Fact]
    public void NoAudioBeforeTheTablesArrive()
    {
        var file = TsTestFile.Mux(Audio);

        Assert.Empty(Demux(file.AsSpan(2 * 188).ToArray()));
    }

    [Fact]
    public void PesHeaderLongerThanThePacketContinuesInTheNext()
    {
        List<byte> file = [.. TsTestFile.Pat(), .. TsTestFile.Pmt()];
        foreach (var packet in TsTestFile.Pes(TsTestFile.AudioPid, Audio, headerExtra: 200)) file.AddRange(packet);

        Assert.Equal(Audio, Demux([.. file]));
    }

    [Fact]
    public void ScrambledPacketsAndTransportErrorsAreSkipped()
    {
        List<byte> file = [.. TsTestFile.Pat(), .. TsTestFile.Pmt()];
        file.AddRange(TsTestFile.Packet(TsTestFile.AudioPid, false, new byte[184], error: true));
        foreach (var packet in TsTestFile.Pes(TsTestFile.AudioPid, Audio, control: 2)) file.AddRange(packet);

        Assert.Empty(Demux([.. file]));
    }

    [Fact]
    public void DataBeforeTheFirstPesStartIsNotAudio()
    {
        List<byte> file = [.. TsTestFile.Pat(), .. TsTestFile.Pmt()];
        file.AddRange(TsTestFile.Packet(TsTestFile.AudioPid, false, new byte[100]));
        foreach (var packet in TsTestFile.Pes(TsTestFile.AudioPid, Audio)) file.AddRange(packet);

        Assert.Equal(Audio, Demux([.. file]));
    }

    [Fact]
    public void BrokenPacketAtTheEndOfASegmentDoesNotTouchTheNext()
    {
        var file = TsTestFile.Mux(Audio);
        var demuxer = new TsDemuxer();
        var output = new ArrayBufferWriter<byte>();

        demuxer.Push(file, output);
        demuxer.Push(TsTestFile.Packet(TsTestFile.AudioPid, false, new byte[184]).AsSpan(0, 100), output);
        demuxer.EndSegment();
        output.Clear();

        foreach (var packet in TsTestFile.Pes(TsTestFile.AudioPid, Audio)) demuxer.Push(packet, output);

        Assert.Equal(Audio, output.WrittenSpan.ToArray());
    }

    [Fact]
    public void FileFromARealMuxerKeepsItsFrames()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Dane");
        var original = File.ReadAllBytes(Path.Combine(directory, "ton-440-stereo-44100.aac"));
        var transport = File.ReadAllBytes(Path.Combine(directory, "ton-440-stereo-44100.ts"));

        var audio = Demux(transport);

        var expected = Frames(original);
        var actual = Frames(audio);

        Assert.InRange(actual.Count, expected.Count - 2, expected.Count);
        Assert.All(actual, header => Assert.True(header.SameStreamAs(expected[0])));
    }

    private static List<AdtsHeader> Frames(ReadOnlySpan<byte> data)
    {
        var frames = new List<AdtsHeader>();
        var at = 0;

        while (AdtsHeader.TryParse(data[at..], out var header))
        {
            frames.Add(header);
            at += header.FrameLength;
            if (at >= data.Length) break;
        }

        return frames;
    }

    [Fact]
    public void RandomDamageNeverThrows()
    {
        var random = new Random(20261105);
        var original = TsTestFile.Mux(Audio, withOtherTracks: true);

        for (var round = 0; round < 500; round++)
        {
            var data = original.ToArray();

            if (round % 4 == 0)
            {
                random.NextBytes(data);
            }
            else
            {
                for (var hit = random.Next(1, 60); hit > 0; hit--) data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            var demuxer = new TsDemuxer();
            var output = new ArrayBufferWriter<byte>();

            foreach (var chunk in data.Chunk(random.Next(1, 400))) demuxer.Push(chunk, output);
        }
    }
}
