using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Błąd otwarcia niesie kod przyczyny, a w komunikacie nie ma tekstów wyjątków bibliotek.
/// </summary>
public class DecoderFailureTests
{
    private static readonly byte[] Garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray();

    [Theory]
    [InlineData(AudioFileFormat.OggVorbis)]
    [InlineData(AudioFileFormat.Opus)]
    public void DamagedFileReportsReasonWithoutLibraryText(AudioFileFormat format)
    {
        var ex = Assert.Throws<AudioException>(
            () => DecoderFactory.Open(new MemoryStream(Garbage), format, ownsStream: true, 48000, 2));

        Assert.Equal(DecoderFailure.DamagedFile, ex.Reason);
        Assert.Equal(DecoderFailures.Describe(DecoderFailure.DamagedFile), ex.Message);
    }

    [Theory]
    [InlineData(AudioFileFormat.Unknown, DecoderFailure.UnrecognisedFormat)]
    [InlineData(AudioFileFormat.Wma, DecoderFailure.OutOfScopeFormat)]
    public void UnsupportedFormatReportsReason(AudioFileFormat format, DecoderFailure expected)
    {
        Assert.Equal(expected, DecoderFactory.ClassifyUnsupported(format));

        var ex = Assert.Throws<AudioException>(
            () => DecoderFactory.Open(new MemoryStream(Garbage), format, ownsStream: true, 48000, 2));
        Assert.Equal(expected, ex.Reason);
    }

    [Fact]
    public void UnrecognisedFileOnDiskReportsReason()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cewka-test-{Guid.NewGuid():N}.xyz");
        File.WriteAllBytes(path, Garbage);
        try
        {
            var ex = Assert.Throws<AudioException>(() => DecoderFactory.Open(path, 48000, 2));
            Assert.Equal(DecoderFailure.UnrecognisedFormat, ex.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExceptionsOutsideDecodingCarryNoReason()
    {
        Assert.Equal(DecoderFailure.Unspecified, new AudioException("x").Reason);
    }
}
