using System.Buffers.Binary;

namespace Cewka.RadioProbe;

internal enum ImageHeaderStatus { NeedMore, Found, NotAnImage }

internal readonly record struct ImageInfo(string Format, int Width, int Height);

/// <summary>Format i wymiary z nagłówka PNG, JPEG, WebP i GIF, bez dekodowania obrazu.</summary>
internal static class ImageHeader
{
    public static ImageHeaderStatus TryRead(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8];
        if (data.StartsWith(png))
            return ReadPng(data, out info);
        if (data.StartsWith(jpeg))
            return ReadJpeg(data, out info);
        if (data.StartsWith("GIF8"u8))
            return ReadGif(data, out info);
        if (data.StartsWith("RIFF"u8))
            return ReadRiff(data, out info);

        // Początek zgodny z którąś sygnaturą: rozstrzygną kolejne bajty.
        return png.StartsWith(data) || jpeg.StartsWith(data) || "GIF8"u8.StartsWith(data) || "RIFF"u8.StartsWith(data)
            ? ImageHeaderStatus.NeedMore
            : ImageHeaderStatus.NotAnImage;
    }

    private static ImageHeaderStatus ReadPng(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (data.Length < 24)
            return ImageHeaderStatus.NeedMore;
        if (!data[12..16].SequenceEqual("IHDR"u8))
            return ImageHeaderStatus.NotAnImage;

        return Found("png", BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]), out info);
    }

    private static ImageHeaderStatus ReadGif(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (data.Length < 10)
            return ImageHeaderStatus.NeedMore;

        return Found("gif", BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), out info);
    }

    private static ImageHeaderStatus ReadJpeg(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        var at = 2;
        while (true)
        {
            // Bajty dopełnienia 0xFF przed znacznikiem są dozwolone.
            while (at < data.Length && data[at] == 0xFF && at + 1 < data.Length && data[at + 1] == 0xFF)
                at++;

            if (at + 4 > data.Length)
                return ImageHeaderStatus.NeedMore;
            if (data[at] != 0xFF)
                return ImageHeaderStatus.NotAnImage;

            var marker = data[at + 1];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (at + 9 > data.Length)
                    return ImageHeaderStatus.NeedMore;

                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]);
                return Found("jpeg", width, height, out info);
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (length < 2)
                return ImageHeaderStatus.NotAnImage;
            at += 2 + length;
        }
    }

    private static ImageHeaderStatus ReadRiff(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (data.Length < 12)
            return ImageHeaderStatus.NeedMore;
        if (!data[8..12].SequenceEqual("WEBP"u8))
            return ImageHeaderStatus.NotAnImage;
        if (data.Length < 16)
            return ImageHeaderStatus.NeedMore;

        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (data.Length < 30)
                return ImageHeaderStatus.NeedMore;
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
                return ImageHeaderStatus.NotAnImage;

            return Found("webp", BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF, out info);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (data.Length < 25)
                return ImageHeaderStatus.NeedMore;
            if (data[20] != 0x2F)
                return ImageHeaderStatus.NotAnImage;

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return Found("webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, out info);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            if (data.Length < 30)
                return ImageHeaderStatus.NeedMore;

            return Found("webp", Read24(data[24..]) + 1, Read24(data[27..]) + 1, out info);
        }

        return ImageHeaderStatus.NotAnImage;
    }

    private static int Read24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static ImageHeaderStatus Found(string format, int width, int height, out ImageInfo info)
    {
        info = default;
        if (width <= 0 || height <= 0)
            return ImageHeaderStatus.NotAnImage;

        info = new ImageInfo(format, width, height);
        return ImageHeaderStatus.Found;
    }
}
