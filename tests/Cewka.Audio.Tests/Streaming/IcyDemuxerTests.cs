using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.Audio.Tests.Streaming;

/// <summary>Rozdzielanie dźwięku i metadanych przy dowolnym podziale na porcje.</summary>
public class IcyDemuxerTests
{
    private sealed class Collector : IIcySink
    {
        public readonly List<byte> Audio = [];
        public readonly List<byte[]> Blocks = [];
        public void OnAudio(ReadOnlySpan<byte> audio) => Audio.AddRange(audio.ToArray());
        public void OnMetadata(ReadOnlySpan<byte> block) => Blocks.Add(block.ToArray());
    }

    private static byte[] Audio(int count)
    {
        var audio = new byte[count];
        new Random(7).NextBytes(audio);
        return audio;
    }

    /// <summary>Metadane na granicy porcji: każde cięcie daje ten sam dźwięk i te same bloki.</summary>
    [Fact]
    public void EveryCutPositionGivesSameResult()
    {
        const int metaInt = 32;
        var audio = Audio(metaInt * 3);
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => FakeRadio.Title($"Tytul {i}"));

        for (var cut = 0; cut <= stream.Length; cut++)
        {
            var sink = new Collector();
            var demuxer = new IcyDemuxer(metaInt, sink);
            demuxer.Push(stream.AsSpan(0, cut));
            demuxer.Push(stream.AsSpan(cut));

            Assert.Equal(audio, sink.Audio);
            Assert.Equal(3, sink.Blocks.Count);
            Assert.StartsWith("StreamTitle='Tytul 1';", FakeRadio.Latin1String(sink.Blocks[1]));
        }
    }

    [Fact]
    public void ZeroLengthByteMeansNoBlock()
    {
        var sink = new Collector();
        var demuxer = new IcyDemuxer(4, sink);

        demuxer.Push([1, 2, 3, 4, 0, 5, 6, 7, 8, 0, 9]);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, sink.Audio);
        Assert.Empty(sink.Blocks);
    }

    [Fact]
    public void LargestBlockFits()
    {
        var content = new byte[255 * 16];
        Array.Fill(content, (byte)'x');
        var stream = new byte[] { 1, 2, 255 }.Concat(content).Concat(new byte[] { 3 }).ToArray();
        var sink = new Collector();
        var demuxer = new IcyDemuxer(2, sink);

        foreach (var b in stream)
            demuxer.Push([b]);

        Assert.Equal(new byte[] { 1, 2, 3 }, sink.Audio);
        Assert.Equal(content, Assert.Single(sink.Blocks));
    }

    [Fact]
    public void BlockCutByEndOfConnectionIsLost()
    {
        var sink = new Collector();
        var demuxer = new IcyDemuxer(2, sink);

        demuxer.Push([1, 2, 1, (byte)'S', (byte)'t']);

        Assert.Equal(new byte[] { 1, 2 }, sink.Audio);
        Assert.Empty(sink.Blocks);
    }

    [Fact]
    public void SinkCanChangeBetweenChunks()
    {
        var first = new Collector();
        var second = new Collector();
        var demuxer = new IcyDemuxer(2, first);

        demuxer.Push([1]);
        demuxer.Sink = second;
        demuxer.Push([2, 0, 3]);

        Assert.Equal(new byte[] { 1 }, first.Audio);
        Assert.Equal(new byte[] { 2, 3 }, second.Audio);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomChunksWithSeed(int seed)
    {
        var random = new Random(seed);
        const int metaInt = 256;
        var audio = Audio(metaInt * 40);
        var stream = FakeRadio.WithMetadata(audio, metaInt, i => i % 3 == 0 ? [] : FakeRadio.Title(new string('t', i * 5)));
        var sink = new Collector();
        var demuxer = new IcyDemuxer(metaInt, sink);

        for (var at = 0; at < stream.Length;)
        {
            var size = Math.Min(random.Next(1, 700), stream.Length - at);
            demuxer.Push(stream.AsSpan(at, size));
            at += size;
        }

        Assert.Equal(audio, sink.Audio);
        Assert.Equal(40 - 14, sink.Blocks.Count);
    }
}
