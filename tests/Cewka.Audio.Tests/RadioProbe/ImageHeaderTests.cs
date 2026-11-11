using Cewka.RadioProbe;
using Xunit;

namespace Cewka.Audio.Tests.RadioProbe;

public class ImageHeaderTests
{
    internal static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        data[11] = 13;
        "IHDR"u8.CopyTo(data.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        return data;
    }

    internal static byte[] Jpeg(int width, int height, int exifBytes = 20)
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        output.Write([0xFF, 0xE1, (byte)((exifBytes + 2) >> 8), (byte)(exifBytes + 2)]);
        output.Write(new byte[exifBytes]);
        output.Write([0xFF, 0xC2, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3]);
        output.Write(new byte[9]);
        return output.ToArray();
    }

    private static byte[] Gif(int width, int height) =>
        [.. "GIF89a"u8, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0];

    private static byte[] WebP(string chunk, byte[] payload)
    {
        var data = new byte[20 + payload.Length];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(data, 12);
        payload.CopyTo(data, 20);
        return data;
    }

    private static void AssertImage(byte[] data, string format, int width, int height)
    {
        Assert.Equal(ImageHeaderStatus.Found, ImageHeader.TryRead(data, out var found));
        Assert.Equal(new ImageInfo(format, width, height), found);
    }

    [Fact]
    public void Png_dimensions_are_read() => AssertImage(Png(500, 300), "png", 500, 300);

    [Fact]
    public void Gif_dimensions_are_read() => AssertImage(Gif(320, 200), "gif", 320, 200);

    [Fact]
    public void Jpeg_dimensions_are_read_past_other_segments() => AssertImage(Jpeg(1024, 768, 5000), "jpeg", 1024, 768);

    [Fact]
    public void Webp_lossy_dimensions_are_read()
    {
        byte[] payload = [0, 0, 0, 0x9D, 0x01, 0x2A, 0x90, 0x01, 0x2C, 0x01];
        AssertImage(WebP("VP8 ", payload), "webp", 400, 300);
    }

    [Fact]
    public void Webp_lossless_dimensions_are_read()
    {
        // 14 bitów szerokości minus 1 i 14 bitów wysokości minus 1, od najmłodszego.
        var bits = (400u - 1) | ((300u - 1) << 14);
        byte[] payload = [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)];
        AssertImage(WebP("VP8L", payload), "webp", 400, 300);
    }

    [Fact]
    public void Webp_extended_dimensions_are_read()
    {
        byte[] payload = [0, 0, 0, 0, 0x8F, 0x01, 0, 0x2B, 0x01, 0];
        AssertImage(WebP("VP8X", payload), "webp", 400, 300);
    }

    [Fact]
    public void Too_short_data_asks_for_more()
    {
        var full = Png(1, 1);
        for (var length = 0; length < 24; length++)
            Assert.Equal(ImageHeaderStatus.NeedMore, ImageHeader.TryRead(full.AsSpan(0, length), out _));

        var jpeg = Jpeg(10, 10, 5000);
        Assert.Equal(ImageHeaderStatus.NeedMore, ImageHeader.TryRead(jpeg.AsSpan(0, 3000), out _));
    }

    [Theory]
    [InlineData("<!doctype html>")]
    [InlineData("zwykly tekst, bez naglowka obrazu")]
    public void Other_content_is_not_an_image(string text)
    {
        Assert.Equal(ImageHeaderStatus.NotAnImage, ImageHeader.TryRead(System.Text.Encoding.ASCII.GetBytes(text), out _));
    }

    [Fact]
    public void Random_and_mutated_headers_never_throw()
    {
        var random = new Random(4242);
        byte[][] samples = [Png(5, 5), Jpeg(5, 5), Gif(5, 5), WebP("VP8X", new byte[10]), WebP("VP8 ", new byte[10]), WebP("VP8L", new byte[5])];
        for (var round = 0; round < 3000; round++)
        {
            var data = (byte[])samples[random.Next(samples.Length)].Clone();
            for (var i = random.Next(5); i >= 0; i--)
                data[random.Next(data.Length)] = (byte)random.Next(256);

            ImageHeader.TryRead(data.AsSpan(0, random.Next(data.Length + 1)), out _);
        }
    }
}
