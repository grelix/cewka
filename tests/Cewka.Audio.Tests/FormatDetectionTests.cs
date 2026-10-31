using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Rozpoznawanie formatu po pierwszych bajtach pliku.</summary>
public class FormatDetectionTests
{
    private static AudioFileFormat Detect(byte[] bytes, string? path = null) =>
        AudioFileFormatDetector.Detect(new MemoryStream(bytes), path);

    [Theory]
    [InlineData((byte)0xF1)] // MPEG-4, bez sumy kontrolnej
    [InlineData((byte)0xF9)] // MPEG-2, bez sumy kontrolnej
    [InlineData((byte)0xF0)] // MPEG-4, z sumą kontrolną
    public void AdtsIsRoutedToSystemCodecs(byte secondByte)
    {
        Assert.Equal(AudioFileFormat.Mp4, Detect(TestAudioFiles.AdtsFrames(secondByte), "utwor.aac"));
        Assert.Equal(AudioFileFormat.Mp4, Detect(TestAudioFiles.AdtsFrames(secondByte), "bez-rozszerzenia"));
    }

    [Fact]
    public void MpegFrameIsMp3()
    {
        Assert.Equal(AudioFileFormat.Mp3, Detect(TestAudioFiles.Mp3Frames(), "utwor.mp3"));
        Assert.Equal(AudioFileFormat.Mp3, Detect(TestAudioFiles.Mp3Frames(), "zle-nazwany.aac"));
    }

    [Fact]
    public void AdtsBehindId3TagIsRoutedToSystemCodecs()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(300), TestAudioFiles.AdtsFrames());
        Assert.Equal(AudioFileFormat.Mp4, Detect(bytes, "podcast.aac"));
        Assert.Equal(AudioFileFormat.Mp4, Detect(bytes, "podcast"));
    }

    [Fact]
    public void Id3FooterIsSkippedToo()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(200, footer: true), TestAudioFiles.AdtsFrames());
        Assert.Equal(AudioFileFormat.Mp4, Detect(bytes, "podcast"));
    }

    [Fact]
    public void Mp3BehindId3TagStaysMp3()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(5000), TestAudioFiles.Mp3Frames());
        Assert.Equal(AudioFileFormat.Mp3, Detect(bytes, "utwor.mp3"));
        Assert.Equal(AudioFileFormat.Mp3, Detect(bytes, "utwor.aac"));
    }

    [Fact]
    public void FlacBehindId3TagIsFlac()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(64), "fLaC"u8.ToArray(), new byte[64]);
        Assert.Equal(AudioFileFormat.Flac, Detect(bytes, "utwor.mp3"));
    }

    [Fact]
    public void UnreadableDataBehindId3FallsBackToExtension()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(64), new byte[64]);
        Assert.Equal(AudioFileFormat.Mp4, Detect(bytes, "podcast.aac"));
        Assert.Equal(AudioFileFormat.Mp3, Detect(bytes, "utwor.mp3"));
        Assert.Equal(AudioFileFormat.Mp3, Detect(bytes, "bez-rozszerzenia"));
    }

    [Fact]
    public void Id3OnStreamWithoutSeekingTrustsExtension()
    {
        var bytes = TestAudioFiles.Concat(TestAudioFiles.Id3Tag(64), TestAudioFiles.AdtsFrames());
        Assert.Equal(AudioFileFormat.Mp4, AudioFileFormatDetector.Detect(new ForwardOnlyStream(bytes), "podcast.aac"));
        Assert.Equal(AudioFileFormat.Mp3, AudioFileFormatDetector.Detect(new ForwardOnlyStream(bytes), "utwor.mp3"));
    }

    [Fact]
    public void DetectionLeavesStreamWhereItWas()
    {
        var bytes = TestAudioFiles.Concat(new byte[7], TestAudioFiles.Id3Tag(300), TestAudioFiles.AdtsFrames());
        var stream = new MemoryStream(bytes) { Position = 7 };

        Assert.Equal(AudioFileFormat.Mp4, AudioFileFormatDetector.Detect(stream, "x.aac"));
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void FlacInsideOggIsFlac()
    {
        var packet = TestAudioFiles.Concat([0x7F], "FLAC"u8.ToArray(), [1, 0, 0, 1], "fLaC"u8.ToArray(), new byte[38]);
        var bytes = TestAudioFiles.OggFirstPacket(packet);

        Assert.Equal(AudioFileFormat.Flac, Detect(bytes, "album.oga"));
        Assert.Equal(AudioFileFormat.Flac, Detect(bytes, "album.ogg"));
    }

    [Fact]
    public void VorbisAndOpusInsideOggAreRecognised()
    {
        var vorbis = TestAudioFiles.Concat([1], "vorbis"u8.ToArray(), new byte[23]);
        var opus = TestAudioFiles.Concat("OpusHead"u8.ToArray(), new byte[11]);

        Assert.Equal(AudioFileFormat.OggVorbis, Detect(TestAudioFiles.OggFirstPacket(vorbis), "a.opus"));
        Assert.Equal(AudioFileFormat.Opus, Detect(TestAudioFiles.OggFirstPacket(opus), "a.ogg"));
    }

    [Fact]
    public void UnknownCodecInsideOggIsNotCalledVorbis()
    {
        var speex = TestAudioFiles.Concat("Speex   "u8.ToArray(), new byte[72]);
        Assert.Equal(AudioFileFormat.Unknown, Detect(TestAudioFiles.OggFirstPacket(speex), "mowa.ogg"));
    }

    private static readonly byte[] Skeleton = TestAudioFiles.Concat("fishead\0"u8.ToArray(), new byte[56]);
    private static readonly byte[] Theora = TestAudioFiles.Concat([0x80], "theora"u8.ToArray(), new byte[35]);

    [Fact]
    public void VorbisBehindSkeletonAndTheoraIsVorbis()
    {
        var vorbis = TestAudioFiles.Concat([1], "vorbis"u8.ToArray(), new byte[23]);
        var bytes = TestAudioFiles.OggBeginningPages(Skeleton, Theora, vorbis);

        Assert.Equal(AudioFileFormat.OggVorbis, Detect(bytes, "film.ogg"));
        Assert.Equal(AudioFileFormat.OggVorbis, Detect(bytes, "bez-rozszerzenia"));
    }

    [Fact]
    public void OggWithoutAnyAudioStreamIsUnknown()
    {
        var bytes = TestAudioFiles.OggBeginningPages(Skeleton, Theora);
        Assert.Equal(AudioFileFormat.Unknown, Detect(bytes, "film.ogg"));
    }

    [Fact]
    public void SpeexBehindSkeletonIsUnknown()
    {
        var speex = TestAudioFiles.Concat("Speex   "u8.ToArray(), new byte[72]);
        Assert.Equal(AudioFileFormat.Unknown, Detect(TestAudioFiles.OggBeginningPages(Skeleton, speex), "mowa.ogg"));
    }

    [Fact]
    public void TruncatedOggHeaderFallsBackToExtension()
    {
        // Sama strona Skeleton: plik urywa się, zanim wiadomo, czy dalej jest dźwięk.
        var bytes = TestAudioFiles.OggBeginningPages(Skeleton);
        var cut = bytes[..(27 + 1 + Skeleton.Length)];

        Assert.Equal(AudioFileFormat.OggVorbis, Detect(cut, "utwor.ogg"));
    }

    /// <summary>Strumień bez możliwości przewijania, jak potok albo gniazdo.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
