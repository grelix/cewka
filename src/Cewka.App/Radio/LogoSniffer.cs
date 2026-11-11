using System.Buffers.Binary;

namespace Cewka.App.Radio;

public enum LogoFormat { Unknown, Png, Jpeg, Gif, WebP, Ico, Svg }

/// <summary>
/// Rozpoznanie formatu po sygnaturze i odczyt wymiarów z nagłówka, przed jakimkolwiek dekodowaniem: serwery
/// podają zły typ (JPEG jako PNG, WebP jako .png), a dekoder pełnowymiarowego obrazu zużywa pamięć
/// proporcjonalną do wymiarów, nie do liczby bajtów.
/// </summary>
internal static class LogoSniffer
{
    public static LogoFormat Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return LogoFormat.Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return LogoFormat.Jpeg;
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return LogoFormat.Gif;
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
            && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return LogoFormat.WebP;
        if (d.Length >= 6 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0 && d[4] > 0) return LogoFormat.Ico;

        // SVG: tekst XML; pierwsze bajty po BOM i białych znakach.
        var head = d[..Math.Min(d.Length, 512)];
        var text = System.Text.Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("<?xml", StringComparison.Ordinal) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return LogoFormat.Svg;

        return LogoFormat.Unknown;
    }

    /// <summary>Wymiary z nagłówka; null, gdy nagłówek jest urwany albo nie da się ich odczytać.</summary>
    public static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> d, LogoFormat format)
    {
        switch (format)
        {
            case LogoFormat.Png:
                return d.Length >= 24
                    ? (BinaryPrimitives.ReadInt32BigEndian(d[16..]), BinaryPrimitives.ReadInt32BigEndian(d[20..]))
                    : null;

            case LogoFormat.Gif:
                return d.Length >= 10
                    ? (BinaryPrimitives.ReadUInt16LittleEndian(d[6..]), BinaryPrimitives.ReadUInt16LittleEndian(d[8..]))
                    : null;

            case LogoFormat.Jpeg:
                return JpegSize(d);

            case LogoFormat.WebP:
                return WebPSize(d);

            default:
                return null;
        }
    }

    private static (int, int)? JpegSize(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }

            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }

            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]);
                return (width, height);
            }

            if (length < 2) return null;
            i += 2 + length;
        }

        return null;
    }

    private static (int, int)? WebPSize(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return null;

        var kind = System.Text.Encoding.ASCII.GetString(d.Slice(12, 4));
        switch (kind)
        {
            case "VP8 ":
                return ((d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF);
            case "VP8L":
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            case "VP8X":
                return ((d[24] | (d[25] << 8) | (d[26] << 16)) + 1, (d[27] | (d[28] << 8) | (d[29] << 16)) + 1);
            default:
                return null;
        }
    }

    /// <summary>
    /// Z pliku ICO wybiera największy wpis. Zwraca go, gdy jest to PNG (tak zapisuje większość nowych
    /// plików); wpis w postaci bitmapy BMP nie jest obsługiwany i daje null.
    /// </summary>
    public static byte[]? IcoLargestPng(ReadOnlySpan<byte> d)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
        var bestSide = -1;
        byte[]? best = null;

        for (var n = 0; n < count; n++)
        {
            var entry = 6 + (n * 16);
            if (entry + 16 > d.Length) break;

            var width = d[entry] == 0 ? 256 : d[entry];
            var height = d[entry + 1] == 0 ? 256 : d[entry + 1];
            var size = BinaryPrimitives.ReadInt32LittleEndian(d[(entry + 8)..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(d[(entry + 12)..]);
            if (size <= 8 || offset < 0 || (long)offset + size > d.Length) continue;

            var image = d.Slice(offset, size);
            if (Detect(image) != LogoFormat.Png) continue;

            var side = Math.Max(width, height);
            if (side > bestSide)
            {
                bestSide = side;
                best = image.ToArray();
            }
        }

        return best;
    }
}
